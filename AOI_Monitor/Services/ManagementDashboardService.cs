using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using AOI_Monitor.Data;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public sealed class ManagementDashboardFilter
{
    public DateTime? FromDate { get; set; }
    public DateTime? ToDate { get; set; }
    public string BoardModel { get; set; } = string.Empty;
    public string LotId { get; set; } = string.Empty;
    public string OperatorId { get; set; } = string.Empty;
    public string ModelVersion { get; set; } = string.Empty;
    public DeploymentProfile? DeploymentProfile { get; set; }
}

public sealed class ManagementDashboardReport
{
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
    public ManagementDashboardFilter Filter { get; set; } = new();
    public int TotalBoardsInspected { get; set; }
    public int OkCount { get; set; }
    public int NgCount { get; set; }
    public int ReviewCount { get; set; }
    public double FalseCallRate { get; set; }
    public int FalseCallCount { get; set; }
    public int PossibleEscapeCount { get; set; }
    public double ManualReviewBurdenMinutes { get; set; }
    public double AverageInspectionTimeMs { get; set; }
    public double P95InspectionTimeMs { get; set; }
    public string MesSyncStatus { get; set; } = string.Empty;
    public string CentralSyncStatus { get; set; } = string.Empty;
    public string AcceptanceReadinessStatus { get; set; } = string.Empty;
    public int OpenPilotIssueCount { get; set; }
    public int CriticalOpenPilotIssueCount { get; set; }
    public Dictionary<string, int> PilotIssuesByCategory { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> PilotIssuesByStatus { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<ManagementDashboardContributor> TopDefectClasses { get; set; } = new();
    public List<ManagementDashboardContributor> TopRoiRefdesContributors { get; set; } = new();
    public List<ManagementDashboardTrendPoint> ModelVersionTrend { get; set; } = new();
    public List<ManagementDashboardBreakdown> LotModelBreakdown { get; set; } = new();
    public List<string> Notes { get; set; } = new();
}

public sealed class ManagementDashboardContributor
{
    public string Name { get; set; } = string.Empty;
    public int Count { get; set; }
    public int FalseCalls { get; set; }
    public int PossibleEscapes { get; set; }
    public int Reviews { get; set; }
}

public sealed class ManagementDashboardTrendPoint
{
    public string ModelVersion { get; set; } = string.Empty;
    public int Total { get; set; }
    public int Ok { get; set; }
    public int Ng { get; set; }
    public int Review { get; set; }
    public double AverageInspectionTimeMs { get; set; }
}

public sealed class ManagementDashboardBreakdown
{
    public string LotId { get; set; } = string.Empty;
    public string BoardModel { get; set; } = string.Empty;
    public string ModelVersion { get; set; } = string.Empty;
    public int Total { get; set; }
    public int FalseCalls { get; set; }
    public int PossibleEscapes { get; set; }
    public double FalseCallRate { get; set; }
}

public sealed record ManagementDashboardExportResult(string Folder, string HtmlPath, string CsvPath, string PdfPath, string JsonPath);

public static class ManagementDashboardService
{
    private const double ReviewMinutesPerBoard = 0.75;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static async Task<ManagementDashboardReport> BuildAsync(
        ManagementDashboardFilter? filter = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var report = Build(filter);
            cancellationToken.ThrowIfCancellationRequested();
            return report;
        }, cancellationToken).ConfigureAwait(false);
    }

    public static ManagementDashboardReport Build(ManagementDashboardFilter? filter = null)
    {
        filter ??= new ManagementDashboardFilter();
        AoiDatabase.Initialize();

        var inspections = AoiDatabase.GetInspectionHistory(new LogFilter
        {
            FromDate = filter.FromDate,
            ToDate = filter.ToDate,
            BoardProgram = NullIfBlank(filter.BoardModel),
            OperatorId = NullIfBlank(filter.OperatorId),
        })
        .Where(row => Matches(row.ModelVersion, filter.ModelVersion))
        .ToArray();

        var latestRun = AoiDatabase.GetLatestBatchTestRun();
        var validationRows = latestRun is null
            ? Array.Empty<BatchTestResultRecord>()
            : AoiDatabase.GetBatchTestResults(latestRun.Id)
                .Where(row => InDateRange(row.CreatedAtUtc, filter))
                .Where(row => Matches(row.BoardModel, filter.BoardModel))
                .Where(row => Matches(row.LotId, filter.LotId))
                .Where(row => Matches(row.ModelVersion, filter.ModelVersion))
                .ToArray();

        var report = new ManagementDashboardReport
        {
            Filter = filter,
            TotalBoardsInspected = inspections.Length,
            OkCount = inspections.Count(row => IsVerdict(row.Verdict, "OK")),
            NgCount = inspections.Count(row => IsVerdict(row.Verdict, "NG")),
            ReviewCount = inspections.Count(row => IsVerdict(row.Verdict, "REVIEW")),
            AverageInspectionTimeMs = inspections.Length == 0 ? 0 : inspections.Average(row => row.TotalInspectionMilliseconds),
            P95InspectionTimeMs = Percentile(inspections.Select(row => row.TotalInspectionMilliseconds), 0.95),
        };

        ApplyLatencySummary(report, filter);

        var falseCalls = validationRows.Where(IsFalseCall).ToArray();
        var possibleEscapes = validationRows.Where(IsPossibleEscape).ToArray();
        var reviewRows = validationRows.Where(row => IsVerdict(row.EngineResult, "REVIEW")).ToArray();
        report.FalseCallCount = falseCalls.Length;
        report.PossibleEscapeCount = possibleEscapes.Length;
        report.FalseCallRate = validationRows.Length == 0 ? 0 : falseCalls.Length / (double)validationRows.Length;
        report.ManualReviewBurdenMinutes = (report.ReviewCount + reviewRows.Length + falseCalls.Length) * ReviewMinutesPerBoard;
        report.TopDefectClasses = BuildDefectContributors(inspections, validationRows);
        report.TopRoiRefdesContributors = BuildRoiRefdesContributors(validationRows);
        report.ModelVersionTrend = BuildModelTrend(inspections);
        report.LotModelBreakdown = BuildLotModelBreakdown(validationRows);

        var mes = MesSpoolService.EvaluateReadiness();
        var central = CentralSyncService.EvaluateReadiness();
        var issueSummary = PilotIssueService.Summarize(new PilotIssueFilter
        {
            BoardModel = filter.BoardModel,
            LotId = filter.LotId,
        });
        var readiness = filter.DeploymentProfile is { } profile
            ? FactoryReadinessService.Evaluate(FactoryReadinessService.CriteriaForProfile(profile))
            : FactoryReadinessService.Evaluate();
        report.MesSyncStatus = $"{mes.Status}; mode={mes.Mode}; pending={mes.PendingCount}; failed={mes.FailedCount}; sent={mes.SentCount}; abandoned={mes.AbandonedCount}.";
        report.CentralSyncStatus = $"{central.Status}; mode={central.Mode}; pending={central.PendingCount}; failed={central.FailedCount}; sent={central.SentCount}; skipped={central.SkippedCount}.";
        report.AcceptanceReadinessStatus = $"{readiness.OverallStatus}; profile={readiness.DeploymentProfile}; warnings={readiness.Warnings.Count}; blocking={readiness.BlockingIssues.Count}.";
        report.OpenPilotIssueCount = issueSummary.Open;
        report.CriticalOpenPilotIssueCount = issueSummary.CriticalOpen;
        report.PilotIssuesByCategory = issueSummary.ByCategory;
        report.PilotIssuesByStatus = issueSummary.ByStatus;

        if (latestRun is null)
            report.Notes.Add("False-call, possible-escape, lot, ROI, and refdes analytics require a validation batch run with ground truth.");
        if (central.Mode is "Disabled" or "Not Connected")
            report.Notes.Add("Central sync is not required for this dashboard; local SQLite is the source of truth.");
        report.Notes.Add("Dashboard exports intentionally omit raw customer image paths and image bytes.");
        return report;
    }

    public static ManagementDashboardExportResult Export(ManagementDashboardReport report, string outputRoot, string operatorId = "UNKNOWN")
    {
        var folder = Path.Combine(outputRoot, $"management_dashboard_{DateTime.UtcNow:yyyyMMdd_HHmmss}");
        Directory.CreateDirectory(folder);
        var htmlPath = Path.Combine(folder, "management_dashboard.html");
        var csvPath = Path.Combine(folder, "management_dashboard.csv");
        var pdfPath = Path.Combine(folder, "management_dashboard.pdf");
        var jsonPath = Path.Combine(folder, "management_dashboard.json");

        File.WriteAllText(htmlPath, BuildHtml(report), Encoding.UTF8);
        File.WriteAllText(csvPath, BuildCsv(report), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, JsonOptions), Encoding.UTF8);
        PdfExportService.ExportHtmlFileToPdf(htmlPath, pdfPath, "Management Dashboard");
        AoiDatabase.RecordAuditEvent(
            "MANAGEMENT_DASHBOARD_EXPORT",
            $"Management dashboard exported: {Path.GetFileName(folder)}.",
            operatorWithRole: operatorId,
            relatedEntityType: "ManagementDashboard",
            relatedPath: folder);
        ExportVerificationService.RecordVerifiedExport("ManagementDashboard", folder, "OK", operatorId);
        return new ManagementDashboardExportResult(folder, htmlPath, csvPath, pdfPath, jsonPath);
    }

    public static string BuildHtml(ManagementDashboardReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html><html><head><meta charset=\"utf-8\"><title>Management Dashboard</title>");
        sb.AppendLine("<style>body{font-family:Segoe UI,Arial,sans-serif;margin:24px;color:#17212b;background:#f7f9fb}.hero{background:#fff;border:1px solid #d7e0e8;padding:18px;margin-bottom:16px}.metric{display:inline-block;background:#fff;border:1px solid #d7e0e8;padding:10px;margin:0 8px 8px 0;min-width:150px}table{border-collapse:collapse;width:100%;background:#fff;margin-bottom:16px}td,th{border:1px solid #d7e0e8;padding:7px;text-align:left}th{background:#eef3f7}</style></head><body>");
        sb.AppendLine("<div class=\"hero\"><h1>Management Dashboard</h1>");
        sb.AppendLine($"<p>Generated UTC: {report.GeneratedAtUtc:O}<br>Acceptance readiness: {Html(report.AcceptanceReadinessStatus)}<br>MES: {Html(report.MesSyncStatus)}<br>Central sync: {Html(report.CentralSyncStatus)}</p></div>");
        AddMetric(sb, "Boards inspected", report.TotalBoardsInspected.ToString("N0", CultureInfo.InvariantCulture));
        AddMetric(sb, "OK / NG / REVIEW", $"{report.OkCount:N0} / {report.NgCount:N0} / {report.ReviewCount:N0}");
        AddMetric(sb, "False-call rate", report.FalseCallRate.ToString("P1", CultureInfo.InvariantCulture));
        AddMetric(sb, "Possible escapes", report.PossibleEscapeCount.ToString("N0", CultureInfo.InvariantCulture));
        AddMetric(sb, "Review burden", $"{report.ManualReviewBurdenMinutes:F1} min");
        AddMetric(sb, "Avg / p95 time", $"{report.AverageInspectionTimeMs:F0} / {report.P95InspectionTimeMs:F0} ms");
        AddMetric(sb, "Pilot issues", $"open {report.OpenPilotIssueCount:N0}; critical {report.CriticalOpenPilotIssueCount:N0}");
        AddContributors(sb, "Top Defect Classes", report.TopDefectClasses);
        AddContributors(sb, "Top ROI / RefDes Contributors", report.TopRoiRefdesContributors);
        sb.AppendLine("<h2>Model Version Trend</h2><table><tr><th>Model</th><th>Total</th><th>OK</th><th>NG</th><th>Review</th><th>Avg ms</th></tr>");
        foreach (var row in report.ModelVersionTrend)
            sb.AppendLine($"<tr><td>{Html(row.ModelVersion)}</td><td>{row.Total}</td><td>{row.Ok}</td><td>{row.Ng}</td><td>{row.Review}</td><td>{row.AverageInspectionTimeMs:F0}</td></tr>");
        sb.AppendLine("</table>");
        sb.AppendLine("<h2>Lot / Model Breakdown</h2><table><tr><th>Lot</th><th>Board</th><th>Model</th><th>Total</th><th>False Calls</th><th>Escapes</th><th>False-call Rate</th></tr>");
        foreach (var row in report.LotModelBreakdown)
            sb.AppendLine($"<tr><td>{Html(row.LotId)}</td><td>{Html(row.BoardModel)}</td><td>{Html(row.ModelVersion)}</td><td>{row.Total}</td><td>{row.FalseCalls}</td><td>{row.PossibleEscapes}</td><td>{row.FalseCallRate:P1}</td></tr>");
        sb.AppendLine("</table>");
        sb.AppendLine("<h2>Notes</h2><ul>");
        foreach (var note in report.Notes)
            sb.AppendLine($"<li>{Html(note)}</li>");
        sb.AppendLine("</ul></body></html>");
        return sb.ToString();
    }

    public static string BuildCsv(ManagementDashboardReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("section,key,value");
        Csv(sb, "summary", "total_boards_inspected", report.TotalBoardsInspected);
        Csv(sb, "summary", "ok_count", report.OkCount);
        Csv(sb, "summary", "ng_count", report.NgCount);
        Csv(sb, "summary", "review_count", report.ReviewCount);
        Csv(sb, "summary", "false_call_rate", report.FalseCallRate.ToString("P4", CultureInfo.InvariantCulture));
        Csv(sb, "summary", "possible_escape_count", report.PossibleEscapeCount);
        Csv(sb, "summary", "manual_review_burden_minutes", report.ManualReviewBurdenMinutes.ToString("F2", CultureInfo.InvariantCulture));
        Csv(sb, "summary", "average_inspection_time_ms", report.AverageInspectionTimeMs.ToString("F2", CultureInfo.InvariantCulture));
        Csv(sb, "summary", "p95_inspection_time_ms", report.P95InspectionTimeMs.ToString("F2", CultureInfo.InvariantCulture));
        Csv(sb, "summary", "mes_sync_status", report.MesSyncStatus);
        Csv(sb, "summary", "central_sync_status", report.CentralSyncStatus);
        Csv(sb, "summary", "acceptance_readiness_status", report.AcceptanceReadinessStatus);
        Csv(sb, "summary", "open_pilot_issue_count", report.OpenPilotIssueCount);
        Csv(sb, "summary", "critical_open_pilot_issue_count", report.CriticalOpenPilotIssueCount);
        foreach (var item in report.PilotIssuesByCategory)
            Csv(sb, "pilot_issues_by_category", item.Key, item.Value);
        foreach (var item in report.PilotIssuesByStatus)
            Csv(sb, "pilot_issues_by_status", item.Key, item.Value);
        foreach (var row in report.TopDefectClasses)
            Csv(sb, "top_defect_class", row.Name, $"count={row.Count}; falseCalls={row.FalseCalls}; escapes={row.PossibleEscapes}; reviews={row.Reviews}");
        foreach (var row in report.TopRoiRefdesContributors)
            Csv(sb, "top_roi_refdes", row.Name, $"count={row.Count}; falseCalls={row.FalseCalls}; escapes={row.PossibleEscapes}; reviews={row.Reviews}");
        foreach (var row in report.ModelVersionTrend)
            Csv(sb, "model_version_trend", row.ModelVersion, $"total={row.Total}; ok={row.Ok}; ng={row.Ng}; review={row.Review}; avgMs={row.AverageInspectionTimeMs:F2}");
        foreach (var row in report.LotModelBreakdown)
            Csv(sb, "lot_model_breakdown", $"{row.LotId}|{row.BoardModel}|{row.ModelVersion}", $"total={row.Total}; falseCalls={row.FalseCalls}; escapes={row.PossibleEscapes}; falseCallRate={row.FalseCallRate:P4}");
        return sb.ToString();
    }

    private static List<ManagementDashboardContributor> BuildDefectContributors(IReadOnlyList<InspectionHistoryRecord> inspections, IReadOnlyList<BatchTestResultRecord> validationRows)
    {
        var operational = inspections
            .Where(row => !string.IsNullOrWhiteSpace(row.SuggestedDefect))
            .GroupBy(row => row.SuggestedDefect.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => new ManagementDashboardContributor
            {
                Name = group.Key,
                Count = group.Count(),
                Reviews = group.Count(row => IsVerdict(row.Verdict, "REVIEW")),
            });
        var validation = validationRows
            .Where(row => !string.IsNullOrWhiteSpace(row.NormalizedDefectClass) || !string.IsNullOrWhiteSpace(row.DefectType))
            .GroupBy(row => string.IsNullOrWhiteSpace(row.NormalizedDefectClass) ? row.DefectType : row.NormalizedDefectClass, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ManagementDashboardContributor
            {
                Name = group.Key,
                Count = group.Count(),
                FalseCalls = group.Count(IsFalseCall),
                PossibleEscapes = group.Count(IsPossibleEscape),
                Reviews = group.Count(row => IsVerdict(row.EngineResult, "REVIEW")),
            });
        return operational.Concat(validation)
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ManagementDashboardContributor
            {
                Name = group.First().Name,
                Count = group.Sum(item => item.Count),
                FalseCalls = group.Sum(item => item.FalseCalls),
                PossibleEscapes = group.Sum(item => item.PossibleEscapes),
                Reviews = group.Sum(item => item.Reviews),
            })
            .OrderByDescending(item => item.Count + item.FalseCalls + item.PossibleEscapes + item.Reviews)
            .ThenBy(item => item.Name)
            .Take(10)
            .ToList();
    }

    private static void ApplyLatencySummary(ManagementDashboardReport report, ManagementDashboardFilter filter)
    {
        var fromUtc = filter.FromDate?.Date.ToUniversalTime() ?? DateTime.MinValue;
        var toUtc = filter.ToDate?.Date.AddDays(1).AddTicks(-1).ToUniversalTime() ?? DateTime.MaxValue;
        var traces = AoiDatabase.GetInspectionLatencyTraces(fromUtc, toUtc)
            .Where(trace => Matches(trace.ModelId, filter.ModelVersion))
            .ToArray();
        if (traces.Length == 0)
            return;

        var overlayTotals = traces
            .Select(trace => trace.TotalFrameToOverlayMs)
            .Where(value => value > 0)
            .OrderBy(value => value)
            .ToArray();
        if (overlayTotals.Length == 0)
            return;

        report.AverageInspectionTimeMs = overlayTotals.Average();
        report.P95InspectionTimeMs = Percentile(overlayTotals, 0.95);
        report.Notes.Add($"Latency metrics use {traces.Length:N0} persisted frame-to-overlay trace(s) for the selected date/model filter.");
    }

    private static List<ManagementDashboardContributor> BuildRoiRefdesContributors(IReadOnlyList<BatchTestResultRecord> rows)
        => rows
            .GroupBy(row => $"{Blank(row.RefDes, "NO_REFDES")} / {Blank(row.RoiId, "NO_ROI")}", StringComparer.OrdinalIgnoreCase)
            .Select(group => new ManagementDashboardContributor
            {
                Name = group.Key,
                Count = group.Count(),
                FalseCalls = group.Count(IsFalseCall),
                PossibleEscapes = group.Count(IsPossibleEscape),
                Reviews = group.Count(row => IsVerdict(row.EngineResult, "REVIEW")),
            })
            .OrderByDescending(item => item.FalseCalls + item.PossibleEscapes + item.Reviews)
            .ThenByDescending(item => item.Count)
            .ThenBy(item => item.Name)
            .Take(10)
            .ToList();

    private static List<ManagementDashboardTrendPoint> BuildModelTrend(IReadOnlyList<InspectionHistoryRecord> inspections)
        => inspections
            .GroupBy(row => Blank(row.ModelVersion, "UNKNOWN"), StringComparer.OrdinalIgnoreCase)
            .Select(group => new ManagementDashboardTrendPoint
            {
                ModelVersion = group.Key,
                Total = group.Count(),
                Ok = group.Count(row => IsVerdict(row.Verdict, "OK")),
                Ng = group.Count(row => IsVerdict(row.Verdict, "NG")),
                Review = group.Count(row => IsVerdict(row.Verdict, "REVIEW")),
                AverageInspectionTimeMs = group.Average(row => row.TotalInspectionMilliseconds),
            })
            .OrderByDescending(item => item.Total)
            .ThenBy(item => item.ModelVersion)
            .ToList();

    private static List<ManagementDashboardBreakdown> BuildLotModelBreakdown(IReadOnlyList<BatchTestResultRecord> rows)
        => rows
            .GroupBy(row => new { LotId = Blank(row.LotId, "UNKNOWN"), BoardModel = Blank(row.BoardModel, "UNKNOWN"), ModelVersion = Blank(row.ModelVersion, "UNKNOWN") })
            .Select(group =>
            {
                var total = group.Count();
                var falseCalls = group.Count(IsFalseCall);
                return new ManagementDashboardBreakdown
                {
                    LotId = group.Key.LotId,
                    BoardModel = group.Key.BoardModel,
                    ModelVersion = group.Key.ModelVersion,
                    Total = total,
                    FalseCalls = falseCalls,
                    PossibleEscapes = group.Count(IsPossibleEscape),
                    FalseCallRate = total == 0 ? 0 : falseCalls / (double)total,
                };
            })
            .OrderByDescending(item => item.Total)
            .ThenBy(item => item.LotId)
            .ToList();

    private static bool IsFalseCall(BatchTestResultRecord row)
        => IsGroundTruth(row.GroundTruth, "OK") &&
           (IsVerdict(row.EngineResult, "NG") || IsVerdict(row.EngineResult, "REVIEW") || row.FailureCategory.Contains("False", StringComparison.OrdinalIgnoreCase));

    private static bool IsPossibleEscape(BatchTestResultRecord row)
        => IsGroundTruth(row.GroundTruth, "NG") &&
           (IsVerdict(row.EngineResult, "OK") || row.FailureCategory.Contains("Escape", StringComparison.OrdinalIgnoreCase));

    private static bool InDateRange(DateTime timestampUtc, ManagementDashboardFilter filter)
    {
        if (filter.FromDate is { } from && timestampUtc.Date < from.Date)
            return false;
        if (filter.ToDate is { } to && timestampUtc.Date > to.Date)
            return false;
        return true;
    }

    private static bool Matches(string value, string filter)
        => string.IsNullOrWhiteSpace(filter) ||
           (value ?? string.Empty).Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool IsGroundTruth(string value, string expected)
        => string.Equals(value?.Trim(), expected, StringComparison.OrdinalIgnoreCase);

    private static bool IsVerdict(string value, string expected)
        => string.Equals(value?.Trim(), expected, StringComparison.OrdinalIgnoreCase);

    private static double Percentile(IEnumerable<double> values, double percentile)
    {
        var ordered = values.Where(value => value >= 0).OrderBy(value => value).ToArray();
        if (ordered.Length == 0)
            return 0;
        var index = Math.Clamp((int)Math.Ceiling(percentile * ordered.Length) - 1, 0, ordered.Length - 1);
        return ordered[index];
    }

    private static string? NullIfBlank(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Blank(string value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static void AddMetric(StringBuilder sb, string label, string value)
        => sb.AppendLine($"<div class=\"metric\"><strong>{Html(label)}</strong><br>{Html(value)}</div>");

    private static void AddContributors(StringBuilder sb, string title, IReadOnlyList<ManagementDashboardContributor> rows)
    {
        sb.AppendLine($"<h2>{Html(title)}</h2><table><tr><th>Name</th><th>Count</th><th>False Calls</th><th>Escapes</th><th>Reviews</th></tr>");
        foreach (var row in rows)
            sb.AppendLine($"<tr><td>{Html(row.Name)}</td><td>{row.Count}</td><td>{row.FalseCalls}</td><td>{row.PossibleEscapes}</td><td>{row.Reviews}</td></tr>");
        sb.AppendLine("</table>");
    }

    private static void Csv(StringBuilder sb, string section, string key, object? value)
        => sb.AppendLine($"{EscapeCsv(section)},{EscapeCsv(key)},{EscapeCsv(value?.ToString() ?? string.Empty)}");

    private static string EscapeCsv(string value)
        => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static string Html(string value)
        => System.Net.WebUtility.HtmlEncode(value ?? string.Empty);
}
