using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AOI_Monitor.Data;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public sealed class PilotIssueExportOptions
{
    public string OutputRoot { get; set; } = string.Empty;
    public bool RedactImagePaths { get; set; } = true;
    public PilotIssueFilter? Filter { get; set; }
}

public sealed record PilotIssueExportResult(string Folder, string JsonPath, string HtmlPath, string CsvPath);

public static class PilotIssueService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static PilotIssue Create(PilotIssue issue, string operatorId = "UNKNOWN")
    {
        Normalize(issue);
        issue.Status = PilotIssueStatus.Open;
        issue.ClosedAtUtc = null;
        AoiDatabase.SavePilotIssue(issue, "CREATE", "Pilot issue created.", operatorId);
        AoiDatabase.RecordAuditEvent(
            "PILOT_ISSUE_CREATE",
            $"Pilot issue created: {issue.IssueId}; category={issue.Category}; severity={issue.Severity}; status={issue.Status}.",
            operatorWithRole: operatorId,
            relatedEntityType: "PilotIssue",
            relatedEntityId: issue.IssueId,
            relatedPath: issue.ImagePath);
        return AoiDatabase.GetPilotIssue(issue.IssueId) ?? issue;
    }

    public static PilotIssue Update(PilotIssue issue, string message = "Pilot issue updated.", string operatorId = "UNKNOWN")
    {
        var existing = AoiDatabase.GetPilotIssue(issue.IssueId) ?? throw new InvalidOperationException("Pilot issue does not exist.");
        Normalize(issue);
        if (issue.Status is PilotIssueStatus.Closed or PilotIssueStatus.Waived or PilotIssueStatus.Fixed)
            issue.ClosedAtUtc ??= DateTime.UtcNow;
        else
            issue.ClosedAtUtc = null;
        AoiDatabase.SavePilotIssue(issue, "UPDATE", message, operatorId, existing.Status.ToString());
        AoiDatabase.RecordAuditEvent(
            "PILOT_ISSUE_UPDATE",
            $"Pilot issue updated: {issue.IssueId}; {existing.Status}->{issue.Status}; category={issue.Category}; severity={issue.Severity}.",
            operatorWithRole: operatorId,
            relatedEntityType: "PilotIssue",
            relatedEntityId: issue.IssueId,
            relatedPath: issue.ImagePath);
        return AoiDatabase.GetPilotIssue(issue.IssueId) ?? issue;
    }

    public static PilotIssue Close(string issueId, string resolution, string operatorId = "UNKNOWN")
    {
        if (string.IsNullOrWhiteSpace(resolution))
            throw new ArgumentException("Resolution is required when closing a pilot issue.", nameof(resolution));

        var issue = AoiDatabase.GetPilotIssue(issueId) ?? throw new InvalidOperationException("Pilot issue does not exist.");
        issue.Status = PilotIssueStatus.Closed;
        issue.Resolution = SecretProtectionService.RedactKnownSecrets(resolution.Trim());
        issue.ClosedAtUtc = DateTime.UtcNow;
        return Update(issue, "Pilot issue closed.", operatorId);
    }

    public static IReadOnlyList<PilotIssue> Search(PilotIssueFilter? filter = null)
        => AoiDatabase.GetPilotIssues(filter);

    public static PilotIssueSummary Summarize(PilotIssueFilter? filter = null)
    {
        var issues = AoiDatabase.GetPilotIssues(filter, limit: 5000);
        return new PilotIssueSummary
        {
            Total = issues.Count,
            Open = issues.Count(IsOpen),
            CriticalOpen = issues.Count(issue => IsOpen(issue) && IsCritical(issue.Severity)),
            ByCategory = issues.GroupBy(issue => issue.Category.ToString()).ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase),
            ByStatus = issues.GroupBy(issue => issue.Status.ToString()).ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase),
        };
    }

    public static PilotIssueExportResult Export(PilotIssueExportOptions? options = null, string operatorId = "UNKNOWN")
    {
        options ??= new PilotIssueExportOptions();
        AoiDatabase.Initialize();
        var root = string.IsNullOrWhiteSpace(options.OutputRoot)
            ? Path.Combine(AoiDatabase.StorageRoot, "exports", "pilot_issues")
            : options.OutputRoot.Trim();
        var folder = EnsureUniqueFolder(Path.Combine(root, $"pilot_issues_{DateTime.UtcNow:yyyyMMdd_HHmmss}"));
        Directory.CreateDirectory(folder);

        var issues = AoiDatabase.GetPilotIssues(options.Filter, limit: 5000);
        var summary = Summarize(options.Filter);
        var export = new
        {
            schemaVersion = "pilot-issues/v1",
            generatedAtUtc = DateTime.UtcNow,
            redactedImagePaths = options.RedactImagePaths,
            summary,
            issues = issues.Select(issue => ExportIssue(issue, options.RedactImagePaths)).ToArray(),
        };

        var jsonPath = Path.Combine(folder, "pilot_issue_report.json");
        var htmlPath = Path.Combine(folder, "pilot_issue_report.html");
        var csvPath = Path.Combine(folder, "pilot_issue_report.csv");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(export, JsonOptions), Encoding.UTF8);
        File.WriteAllText(htmlPath, BuildHtml(issues, summary, options.RedactImagePaths), Encoding.UTF8);
        File.WriteAllText(csvPath, BuildCsv(issues, options.RedactImagePaths), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        ExportVerificationService.RecordVerifiedExport("PilotIssueReport", folder, "OK", operatorId);
        AoiDatabase.RecordAuditEvent(
            "PILOT_ISSUE_EXPORT",
            $"Pilot issue report exported: {Path.GetFileName(folder)}; count={issues.Count}; redactedImagePaths={options.RedactImagePaths}.",
            operatorWithRole: operatorId,
            relatedEntityType: "PilotIssueReport",
            relatedPath: folder);
        return new PilotIssueExportResult(folder, jsonPath, htmlPath, csvPath);
    }

    public static PilotIssue CreateFromInspection(InspectionHistoryRecord inspection, PilotIssueCategory category, string severity, string notes, string operatorId)
        => Create(new PilotIssue
        {
            Category = category,
            Severity = severity,
            BoardModel = inspection.BoardProgram,
            ImagePath = inspection.SampleImagePath,
            RelatedInspectionId = inspection.Id.ToString(CultureInfo.InvariantCulture),
            Notes = notes,
            Owner = operatorId,
        }, operatorId);

    public static bool IsCritical(string severity)
        => string.Equals(severity?.Trim(), "Critical", StringComparison.OrdinalIgnoreCase);

    private static bool IsOpen(PilotIssue issue)
        => issue.Status is PilotIssueStatus.Open or PilotIssueStatus.Investigating;

    private static void Normalize(PilotIssue issue)
    {
        issue.Severity = string.IsNullOrWhiteSpace(issue.Severity) ? "Medium" : issue.Severity.Trim();
        issue.BoardModel = issue.BoardModel?.Trim() ?? string.Empty;
        issue.LotId = issue.LotId?.Trim() ?? string.Empty;
        issue.ImagePath = issue.ImagePath?.Trim() ?? string.Empty;
        issue.RelatedInspectionId = issue.RelatedInspectionId?.Trim() ?? string.Empty;
        issue.RelatedAcceptanceRunId = issue.RelatedAcceptanceRunId?.Trim() ?? string.Empty;
        issue.Owner = issue.Owner?.Trim() ?? string.Empty;
        issue.Notes = SecretProtectionService.RedactKnownSecrets(issue.Notes?.Trim() ?? string.Empty);
        issue.Resolution = SecretProtectionService.RedactKnownSecrets(issue.Resolution?.Trim() ?? string.Empty);
    }

    private static object ExportIssue(PilotIssue issue, bool redactImagePaths)
        => new
        {
            issue.IssueId,
            issue.CreatedAtUtc,
            category = issue.Category.ToString(),
            issue.Severity,
            issue.BoardModel,
            issue.LotId,
            imagePath = RedactPath(issue.ImagePath, redactImagePaths),
            issue.RelatedInspectionId,
            issue.RelatedAcceptanceRunId,
            status = issue.Status.ToString(),
            issue.Owner,
            issue.Notes,
            issue.Resolution,
            issue.ClosedAtUtc,
        };

    private static string BuildHtml(IReadOnlyList<PilotIssue> issues, PilotIssueSummary summary, bool redactImagePaths)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html><html><head><meta charset=\"utf-8\"><title>Pilot Issue Report</title>");
        sb.AppendLine("<style>body{font-family:Segoe UI,Arial,sans-serif;margin:24px;color:#17212b;background:#f7f9fb}.hero{background:#fff;border:1px solid #d7e0e8;padding:18px;margin-bottom:16px}.Critical{color:#a12626;font-weight:700}table{border-collapse:collapse;width:100%;background:#fff}td,th{border:1px solid #d7e0e8;padding:7px;text-align:left;vertical-align:top}th{background:#eef3f7}</style></head><body>");
        sb.AppendLine($"<div class=\"hero\"><h1>Pilot Issue Report</h1><p>Total={summary.Total}; open={summary.Open}; critical open={summary.CriticalOpen}; generated UTC={DateTime.UtcNow:O}</p></div>");
        sb.AppendLine("<table><tr><th>ID</th><th>Created</th><th>Category</th><th>Severity</th><th>Status</th><th>Board</th><th>Lot</th><th>Image</th><th>Owner</th><th>Notes</th><th>Resolution</th></tr>");
        foreach (var issue in issues)
            sb.AppendLine($"<tr><td>{Html(issue.IssueId)}</td><td>{issue.CreatedAtUtc:O}</td><td>{issue.Category}</td><td class=\"{Html(issue.Severity)}\">{Html(issue.Severity)}</td><td>{issue.Status}</td><td>{Html(issue.BoardModel)}</td><td>{Html(issue.LotId)}</td><td>{Html(RedactPath(issue.ImagePath, redactImagePaths))}</td><td>{Html(issue.Owner)}</td><td>{Html(issue.Notes)}</td><td>{Html(issue.Resolution)}</td></tr>");
        sb.AppendLine("</table></body></html>");
        return sb.ToString();
    }

    private static string BuildCsv(IReadOnlyList<PilotIssue> issues, bool redactImagePaths)
    {
        var sb = new StringBuilder();
        sb.AppendLine("IssueId,CreatedAtUtc,Category,Severity,Status,BoardModel,LotId,ImagePath,RelatedInspectionId,RelatedAcceptanceRunId,Owner,Notes,Resolution,ClosedAtUtc");
        foreach (var issue in issues)
        {
            sb.AppendLine(string.Join(",",
                Csv(issue.IssueId),
                Csv(issue.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture)),
                Csv(issue.Category.ToString()),
                Csv(issue.Severity),
                Csv(issue.Status.ToString()),
                Csv(issue.BoardModel),
                Csv(issue.LotId),
                Csv(RedactPath(issue.ImagePath, redactImagePaths)),
                Csv(issue.RelatedInspectionId),
                Csv(issue.RelatedAcceptanceRunId),
                Csv(issue.Owner),
                Csv(issue.Notes),
                Csv(issue.Resolution),
                Csv(issue.ClosedAtUtc?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty)));
        }

        return sb.ToString();
    }

    private static string RedactPath(string path, bool redact)
    {
        if (!redact || string.IsNullOrWhiteSpace(path))
            return path ?? string.Empty;
        var fileName = Path.GetFileName(path);
        return string.IsNullOrWhiteSpace(fileName) ? "[REDACTED_IMAGE_PATH]" : $"[REDACTED_IMAGE_PATH]/{fileName}";
    }

    private static string Csv(string value)
        => $"\"{(value ?? string.Empty).Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static string Html(string value)
        => System.Net.WebUtility.HtmlEncode(value ?? string.Empty);

    private static string EnsureUniqueFolder(string folder)
    {
        if (!Directory.Exists(folder))
            return folder;
        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{folder}_{i:D2}";
            if (!Directory.Exists(candidate))
                return candidate;
        }

        return $"{folder}_{Guid.NewGuid():N}";
    }
}
