using System.IO;
using System.Text;
using System.Text.Json;
using AOI_Monitor.Data;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public static class MesSpoolService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static IReadOnlyList<MesSpoolQueueRecord> List(int limit = 500)
        => AoiDatabase.GetMesSpoolQueue(limit);

    public static IReadOnlyList<MesSpoolQueueRecord> ListEligibleForRetry(int limit = 100)
        => AoiDatabase.GetPendingMesSpoolItems(limit);

    public static async Task<MesSpoolRetrySummary> RetryEligibleAsync(
        int limit = 100,
        CancellationToken cancellationToken = default)
        => await RetryItemsAsync(ListEligibleForRetry(limit).Select(item => item.Id), cancellationToken).ConfigureAwait(false);

    public static async Task<MesSpoolRetrySummary> RetryItemsAsync(
        IEnumerable<long> itemIds,
        CancellationToken cancellationToken = default)
    {
        var ids = itemIds.Distinct().ToHashSet();
        if (ids.Count == 0)
            return new MesSpoolRetrySummary(0, 0, 0);

        var settings = MesIntegrationSettingsService.Load();
        MesIntegrationSettingsService.ApplyIntegrationBoundary();
        var items = AoiDatabase.GetMesSpoolQueue(1000)
            .Where(item => ids.Contains(item.Id) && item.Status is "Pending" or "Failed")
            .OrderBy(item => item.CreatedAtUtc)
            .ToArray();

        var attempted = 0;
        var succeeded = 0;
        var failed = 0;

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempted++;
            var result = await RetryOneAsync(item, cancellationToken).ConfigureAwait(false);
            AoiDatabase.RecordMesUploadAttempt(
                TraceabilityUploadService.ToDisplay(settings.Mode),
                item.EndpointUrl,
                item.PayloadPath,
                result.Accepted ? "OK" : "ERROR",
                $"Retry spool item {item.Id}: {result.Message}",
                item.OperatorId,
                item.LotId,
                item.BoardModel,
                item.Result);

            if (result.Accepted)
            {
                succeeded++;
                AoiDatabase.MarkMesSpoolItemSent(item.Id, result.Message);
            }
            else
            {
                failed++;
                AoiDatabase.RecordMesSpoolRetryFailure(item.Id, result.Message, settings.RetryBackoffMs);
            }
        }

        return new MesSpoolRetrySummary(attempted, succeeded, failed);
    }

    public static void MarkAbandoned(long id, UserRole role, string operatorId, string reason)
    {
        if (!RoleAuthorization.CanUseMaintenanceActions(role))
            throw new UnauthorizedAccessException(RoleAuthorization.DeniedMessage(role, "Abandoning MES spool items"));

        var message = string.IsNullOrWhiteSpace(reason)
            ? "Abandoned by authorized administrator."
            : reason.Trim();
        AoiDatabase.MarkMesSpoolItemAbandoned(id, message, operatorId);
    }

    public static MesReadinessSummary EvaluateReadiness(MesReadinessCriteria? criteria = null)
    {
        criteria ??= new MesReadinessCriteria();
        var settings = MesIntegrationSettingsService.Load();
        MesIntegrationSettingsService.ApplyIntegrationBoundary();
        var queue = AoiDatabase.GetMesSpoolQueue(1000);
        var summary = new MesReadinessSummary
        {
            Mode = TraceabilityUploadService.ToDisplay(settings.Mode),
            PendingCount = queue.Count(item => item.Status == "Pending"),
            FailedCount = queue.Count(item => item.Status == "Failed"),
            SentCount = queue.Count(item => item.Status == "Sent"),
            AbandonedCount = queue.Count(item => item.Status == "Abandoned"),
        };

        var validation = MesIntegrationSettingsService.Validate(settings);
        if (summary.PendingCount > 0)
            summary.Messages.Add($"{summary.PendingCount} MES spool item(s) are pending retry.");
        if (summary.FailedCount > 0)
            summary.Messages.Add($"{summary.FailedCount} MES spool item(s) failed all configured retries.");
        if (summary.AbandonedCount > 0)
            summary.Messages.Add($"{summary.AbandonedCount} MES spool item(s) were abandoned by Admin action.");

        if (summary.PendingCount > 0)
        {
            summary.Status = criteria.FailOnPendingQueue ? "MES Queue Pending FAIL" : "MES Queue Pending";
            return summary;
        }

        if (summary.FailedCount > 0 && criteria.FailOnFailedQueue)
        {
            summary.Status = "MES REST Error";
            return summary;
        }

        summary.Status = settings.Mode switch
        {
            MesIntegrationMode.NotConnected => "MES Not Configured",
            MesIntegrationMode.MockRest => "MES Mock Only",
            MesIntegrationMode.Rest when validation.Count == 0 => "MES REST Ready",
            MesIntegrationMode.Rest => "MES REST Error",
            _ => "MES Not Configured",
        };

        if (settings.Mode == MesIntegrationMode.MockRest)
            summary.Messages.Add("Mock MES remains local/mock evidence only and is not production writeback.");
        if (validation.Count > 0)
            summary.Messages.AddRange(validation);
        if (summary.Messages.Count == 0)
            summary.Messages.Add(summary.Status);
        return summary;
    }

    public static MesQueueReportExport ExportQueueReport(string? outputRoot = null)
    {
        var root = string.IsNullOrWhiteSpace(outputRoot)
            ? Path.Combine(AoiDatabase.StorageRoot, "exports", "mes_queue")
            : outputRoot;
        Directory.CreateDirectory(root);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var rows = AoiDatabase.GetMesSpoolQueue(1000);
        var jsonPath = Path.Combine(root, $"mes_queue_{stamp}.json");
        var htmlPath = Path.Combine(root, $"mes_queue_{stamp}.html");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(rows.Select(ToReportRow), JsonOptions), Encoding.UTF8);
        File.WriteAllText(htmlPath, BuildHtml(rows, EvaluateReadiness()), Encoding.UTF8);
        ExportVerificationService.RecordVerifiedExport("MesQueueReportJson", jsonPath);
        ExportVerificationService.RecordVerifiedExport("MesQueueReportHtml", htmlPath);
        return new MesQueueReportExport(jsonPath, htmlPath);
    }

    private static async Task<IntegrationCommandResult> RetryOneAsync(
        MesSpoolQueueRecord item,
        CancellationToken cancellationToken)
    {
        try
        {
            return item.PayloadType switch
            {
                "UploadResultCommand" => await IntegrationBoundaryRegistry.MesClient.UploadResultAsync(
                    JsonSerializer.Deserialize<UploadResultCommand>(item.PayloadJson, JsonOptions)!,
                    cancellationToken).ConfigureAwait(false),
                "UploadImageCommand" => await IntegrationBoundaryRegistry.MesClient.UploadImageAsync(
                    JsonSerializer.Deserialize<UploadImageCommand>(item.PayloadJson, JsonOptions)!,
                    cancellationToken).ConfigureAwait(false),
                _ => await IntegrationBoundaryRegistry.MesClient.UploadTraceabilityAsync(
                    JsonSerializer.Deserialize<TraceabilityPayload>(item.PayloadJson, JsonOptions)!,
                    cancellationToken).ConfigureAwait(false),
            };
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return new IntegrationCommandResult(false, IntegrationConnectionStatus.Error, $"MES spool retry could not read payload: {ex.Message}");
        }
    }

    private static string BuildHtml(IReadOnlyList<MesSpoolQueueRecord> rows, MesReadinessSummary readiness)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html><html><head><meta charset=\"utf-8\"><title>MES Queue Report</title>");
        sb.AppendLine("<style>body{font-family:Segoe UI,Arial,sans-serif;margin:24px;color:#17212b}table{border-collapse:collapse;width:100%}td,th{border:1px solid #cfd8df;padding:6px;text-align:left}.notice{background:#fff8e8;border-left:5px solid #d9951b;padding:12px;margin:12px 0}</style></head><body>");
        sb.AppendLine("<h1>MES Queue Report</h1>");
        sb.AppendLine($"<div class=\"notice\"><strong>Readiness:</strong> {Escape(readiness.Status)}. Secrets are redacted and payload JSON is not shown in this report.</div>");
        sb.AppendLine("<table><tr><th>Created</th><th>Type</th><th>Lot</th><th>Board</th><th>Result</th><th>Retries</th><th>Status</th><th>Last Error</th></tr>");
        foreach (var row in rows)
        {
            sb.AppendLine($"<tr><td>{row.CreatedAtUtc:u}</td><td>{Escape(row.PayloadType)}</td><td>{Escape(row.LotId)}</td><td>{Escape(row.BoardModel)}</td><td>{Escape(row.Result)}</td><td>{row.RetryCount}/{row.MaxRetryCount}</td><td>{Escape(row.Status)}</td><td>{Escape(row.LastError)}</td></tr>");
        }

        sb.AppendLine("</table></body></html>");
        return sb.ToString();
    }

    private static string Escape(string value)
        => System.Net.WebUtility.HtmlEncode(value ?? string.Empty);

    private static MesQueueReportRow ToReportRow(MesSpoolQueueRecord row)
        => new(
            row.Id,
            row.CreatedAtUtc,
            row.LastAttemptAtUtc,
            row.NextAttemptAtUtc,
            row.PayloadType,
            row.EndpointUrl,
            row.RetryCount,
            row.MaxRetryCount,
            row.Status,
            row.LastError,
            row.LotId,
            row.BoardModel,
            row.Result);
}

public sealed record MesQueueReportExport(string JsonPath, string HtmlPath);

public sealed record MesQueueReportRow(
    long Id,
    DateTime CreatedAtUtc,
    DateTime? LastAttemptAtUtc,
    DateTime? NextAttemptAtUtc,
    string PayloadType,
    string EndpointUrl,
    int RetryCount,
    int MaxRetryCount,
    string Status,
    string LastError,
    string LotId,
    string BoardModel,
    string Result);
