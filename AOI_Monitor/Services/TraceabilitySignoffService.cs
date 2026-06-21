using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using AOI_Monitor.Data;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public static class TraceabilitySignoffService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static async Task<TraceabilityTestReport> RunAsync(
        string? testImagePath = null,
        bool productionModeConfirmed = false,
        string? operatorId = null,
        CancellationToken cancellationToken = default)
    {
        var settings = MesIntegrationSettingsService.Load();
        MesIntegrationSettingsService.ApplyIntegrationBoundary();
        var report = new TraceabilityTestReport
        {
            CreatedAtUtc = DateTime.UtcNow,
            Mode = TraceabilityUploadService.ToDisplay(settings.Mode),
            EndpointUrl = EndpointFor(settings),
            ProductionModeConfirmed = productionModeConfirmed,
            OperatorId = string.IsNullOrWhiteSpace(operatorId) ? "UNKNOWN" : operatorId,
        };

        var payload = new TraceabilityPayload
        {
            IntegrationMode = report.Mode,
            LotId = "TRACEABILITY-SIGNOFF",
            BoardModel = "SIGNOFF-MODEL",
            SerialNumber = $"SIGNOFF-{DateTime.UtcNow:yyyyMMddHHmmss}",
            StationId = "AOI-SIGNOFF",
            OperatorId = report.OperatorId,
            Result = "OK",
            TimestampUtc = DateTime.UtcNow,
            DefectSummary = "Synthetic MES traceability signoff payload. Not a production board result.",
            InspectionEngine = "TraceabilitySignoffService",
            ModelVersion = "N/A",
            SourceNotice = "Synthetic traceability signoff evidence. Scope is MES connectivity and spool behavior, not product inspection accuracy.",
        };

        var resultOutcome = await TraceabilityUploadService.UploadAsync(payload, cancellationToken).ConfigureAwait(false);
        report.PayloadPath = resultOutcome.PayloadPath;
        report.ResultStatus = resultOutcome.Result.Accepted ? "PASS" : "FAIL";
        report.Message = resultOutcome.Result.Message;

        report.ImageStatus = await TrySendImageAsync(settings, payload, testImagePath, productionModeConfirmed, cancellationToken).ConfigureAwait(false);
        report.Status = report.ResultStatus == "PASS" && report.ImageStatus is "PASS" or "NOT SENT" ? "PASS" : "FAIL";

        WriteReports(report);
        AoiDatabase.RecordTraceabilityTestReport(report);
        return report;
    }

    private static async Task<string> TrySendImageAsync(
        MesIntegrationSettings settings,
        TraceabilityPayload payload,
        string? imagePath,
        bool productionModeConfirmed,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
            return "NOT SENT";
        if (!File.Exists(imagePath))
            return "FAIL: image file not found";
        if (settings.Mode == MesIntegrationMode.Rest && !productionModeConfirmed)
            return "NOT SENT: production REST image upload requires explicit confirmation";

        var command = new UploadImageCommand(
            $"TRACE-{payload.SerialNumber}",
            payload.SerialNumber ?? "SIGNOFF",
            imagePath,
            "traceability-signoff",
            DateTime.UtcNow);
        var result = await IntegrationBoundaryRegistry.MesClient.UploadImageAsync(command, cancellationToken).ConfigureAwait(false);
        return result.Accepted ? "PASS" : $"FAIL: {MesIntegrationSettingsService.RedactSecrets(result.Message, settings)}";
    }

    private static void WriteReports(TraceabilityTestReport report)
    {
        var root = Path.Combine(AoiDatabase.StorageRoot, "exports", "traceability_signoff");
        Directory.CreateDirectory(root);
        var stamp = report.CreatedAtUtc.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        report.ReportJsonPath = Path.Combine(root, $"traceability_signoff_{stamp}.json");
        report.ReportHtmlPath = Path.Combine(root, $"traceability_signoff_{stamp}.html");
        var safe = RedactedCopy(report);
        File.WriteAllText(report.ReportJsonPath, JsonSerializer.Serialize(safe, JsonOptions), Encoding.UTF8);
        File.WriteAllText(report.ReportHtmlPath, BuildHtml(safe), Encoding.UTF8);
        ExportVerificationService.RecordVerifiedExport("TraceabilitySignoffJson", report.ReportJsonPath, report.Status == "PASS" ? "OK" : "ERROR", report.OperatorId);
        ExportVerificationService.RecordVerifiedExport("TraceabilitySignoffHtml", report.ReportHtmlPath, report.Status == "PASS" ? "OK" : "ERROR", report.OperatorId);
    }

    private static TraceabilityTestReport RedactedCopy(TraceabilityTestReport report)
        => new()
        {
            Id = report.Id,
            CreatedAtUtc = report.CreatedAtUtc,
            Status = report.Status,
            Mode = report.Mode,
            EndpointUrl = MesIntegrationSettingsService.RedactSecrets(report.EndpointUrl),
            ResultStatus = report.ResultStatus,
            ImageStatus = MesIntegrationSettingsService.RedactSecrets(report.ImageStatus),
            PayloadPath = report.PayloadPath,
            ReportJsonPath = report.ReportJsonPath,
            ReportHtmlPath = report.ReportHtmlPath,
            Message = MesIntegrationSettingsService.RedactSecrets(report.Message),
            ProductionModeConfirmed = report.ProductionModeConfirmed,
            OperatorId = report.OperatorId,
        };

    private static string BuildHtml(TraceabilityTestReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html><html><head><meta charset=\"utf-8\"><title>Traceability Signoff</title>");
        sb.AppendLine("<style>body{font-family:Segoe UI,Arial,sans-serif;margin:24px;color:#17212b}table{border-collapse:collapse}td,th{border:1px solid #ccd5dd;padding:6px 8px}.notice{background:#fff8e8;border-left:5px solid #d9951b;padding:12px}</style></head><body>");
        sb.AppendLine("<h1>MES Traceability Signoff Report</h1>");
        sb.AppendLine("<p class=\"notice\">This is connectivity and queue evidence only. It does not certify production MES, OPC UA, user authentication, or inspection accuracy.</p>");
        sb.AppendLine("<table>");
        AppendRow(sb, "Status", report.Status);
        AppendRow(sb, "Mode", report.Mode);
        AppendRow(sb, "Endpoint", report.EndpointUrl);
        AppendRow(sb, "Result payload", report.ResultStatus);
        AppendRow(sb, "Image payload", report.ImageStatus);
        AppendRow(sb, "Production image confirmation", report.ProductionModeConfirmed ? "Yes" : "No");
        AppendRow(sb, "Message", report.Message);
        sb.AppendLine("</table></body></html>");
        return sb.ToString();
    }

    private static void AppendRow(StringBuilder sb, string key, string value)
        => sb.AppendLine($"<tr><th>{Escape(key)}</th><td>{Escape(value)}</td></tr>");

    private static string EndpointFor(MesIntegrationSettings settings)
    {
        if (settings.Mode == MesIntegrationMode.MockRest)
            return settings.MockEndpointUrl;
        if (settings.Mode != MesIntegrationMode.Rest)
            return string.Empty;
        return $"{settings.BaseUrl.TrimEnd('/')}/{settings.UploadResultPath.TrimStart('/')}";
    }

    private static string Escape(string value) => System.Net.WebUtility.HtmlEncode(value ?? string.Empty);
}
