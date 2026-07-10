using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AOI_Monitor.Data;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public enum ClientDemoGateStatus
{
    Pass,
    Warning,
    Blocked,
}

public sealed class ClientDemoGateCheck
{
    public string Name { get; init; } = string.Empty;
    public ClientDemoGateStatus Status { get; init; }
    public string Evidence { get; init; } = string.Empty;
    public string NextAction { get; init; } = string.Empty;
}

public sealed class ClientDemoReadinessGateReport
{
    public string SchemaVersion { get; init; } = "client-demo-readiness-gate/v1";
    public DateTime GeneratedAtUtc { get; init; } = DateTime.UtcNow;
    public string AppVersion { get; init; } = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
    public DeploymentProfile DeploymentProfile { get; init; }
    public ClientDemoGateStatus OverallStatus { get; init; }
    public string StandardsTraceabilitySummary { get; init; } = string.Empty;
    public List<ClientDemoGateCheck> Checks { get; init; } = new();
    public List<string> BlockingIssues { get; init; } = new();
    public List<string> Warnings { get; init; } = new();
    public bool CanCreateClientPackage => OverallStatus != ClientDemoGateStatus.Blocked;
}

public sealed record ClientDemoReadinessGateExport(string Folder, string JsonPath, string HtmlPath, string TextPath);

public static class ClientDemoReadinessGateService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static ClientDemoReadinessGateReport Evaluate(DeploymentProfile? profile = null)
    {
        AoiDatabase.Initialize();
        var deploymentProfile = profile ?? DeploymentProfileSettingsService.Load();
        var checks = new List<ClientDemoGateCheck>();

        AddHygieneEvidence(checks);
        AddBuildEvidence(checks);
        AddTestEvidence(checks);
        AddUiLayoutAudit(checks);
        AddNavigationPerformance(checks);
        AddUiStabilitySoak(checks);
        AddExportVerification(checks);
        AddAlarmEvents(checks);
        AddCrashReports(checks);
        AddCriticalPilotIssues(checks);
        AddStage1Package(checks, deploymentProfile);
        AddHardwareWarning(checks);
        var standardsReport = StandardsTraceabilityService.Evaluate(deploymentProfile);
        AddStandardsTraceabilitySummary(checks, standardsReport);

        var blocking = checks
            .Where(check => check.Status == ClientDemoGateStatus.Blocked)
            .Select(check => $"{check.Name}: {check.Evidence}")
            .ToList();
        var warnings = checks
            .Where(check => check.Status == ClientDemoGateStatus.Warning)
            .Select(check => $"{check.Name}: {check.Evidence}")
            .ToList();

        var overall = blocking.Count > 0
            ? ClientDemoGateStatus.Blocked
            : warnings.Count > 0
                ? ClientDemoGateStatus.Warning
                : ClientDemoGateStatus.Pass;

        return new ClientDemoReadinessGateReport
        {
            DeploymentProfile = deploymentProfile,
            OverallStatus = overall,
            StandardsTraceabilitySummary = StandardsTraceabilitySummaryFor(standardsReport),
            Checks = checks,
            BlockingIssues = blocking,
            Warnings = warnings,
        };
    }

    public static ClientDemoReadinessGateExport ExportReport(string? outputRoot = null, DeploymentProfile? profile = null)
    {
        var report = Evaluate(profile);
        var root = string.IsNullOrWhiteSpace(outputRoot)
            ? Path.Combine(AoiDatabase.StorageRoot, "exports", "client_demo_readiness")
            : outputRoot.Trim();
        var folder = EnsureUniqueFolder(Path.Combine(root, $"client_demo_readiness_{DateTime.UtcNow:yyyyMMdd_HHmmss}"));
        Directory.CreateDirectory(folder);

        var jsonPath = Path.Combine(folder, "client_demo_readiness_gate.json");
        var htmlPath = Path.Combine(folder, "client_demo_readiness_gate.html");
        var textPath = Path.Combine(folder, "client_demo_readiness_gate.txt");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, JsonOptions), Encoding.UTF8);
        File.WriteAllText(htmlPath, BuildHtml(report), Encoding.UTF8);
        File.WriteAllText(textPath, BuildText(report), Encoding.UTF8);

        ExportVerificationService.RecordVerifiedExport(
            "ClientDemoReadinessGate",
            folder,
            report.OverallStatus == ClientDemoGateStatus.Blocked ? "WARN" : "OK");
        AoiDatabase.RecordAuditEvent(
            "CLIENT_DEMO_GATE_EXPORT",
            $"Client demo readiness gate exported: {Path.GetFileName(folder)}; status={report.OverallStatus}.",
            relatedEntityType: "ClientDemoReadinessGate",
            relatedPath: folder);

        return new ClientDemoReadinessGateExport(folder, jsonPath, htmlPath, textPath);
    }

    public static string BuildHtml(ClientDemoReadinessGateReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html><html><head><meta charset=\"utf-8\"><title>Client Demo Readiness Gate</title>");
        sb.AppendLine("<style>body{font-family:Segoe UI,Arial,sans-serif;margin:24px;color:#17212b}.Pass{color:#176b3a}.Warning{color:#8a5a00}.Blocked{color:#a12626}table{border-collapse:collapse;width:100%}td,th{border:1px solid #cfd8df;padding:8px;text-align:left;vertical-align:top}th{background:#edf2f5}</style></head><body>");
        sb.AppendLine($"<h1>Client Demo Readiness Gate</h1><p><strong class=\"{report.OverallStatus}\">{report.OverallStatus}</strong><br>Profile: {Html(report.DeploymentProfile.ToString())}<br>Generated UTC: {report.GeneratedAtUtc:O}</p>");
        if (!string.IsNullOrWhiteSpace(report.StandardsTraceabilitySummary))
            sb.AppendLine($"<p>{Html(report.StandardsTraceabilitySummary)}</p>");
        sb.AppendLine("<table><tr><th>Gate</th><th>Status</th><th>Evidence</th><th>Next Action</th></tr>");
        foreach (var check in report.Checks)
            sb.AppendLine($"<tr><td>{Html(check.Name)}</td><td class=\"{check.Status}\">{check.Status}</td><td>{Html(check.Evidence)}</td><td>{Html(check.NextAction)}</td></tr>");
        sb.AppendLine("</table></body></html>");
        return sb.ToString();
    }

    public static string BuildText(ClientDemoReadinessGateReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Client Demo Readiness Gate: {report.OverallStatus}");
        sb.AppendLine($"Profile: {report.DeploymentProfile}");
        sb.AppendLine($"Generated UTC: {report.GeneratedAtUtc:O}");
        if (!string.IsNullOrWhiteSpace(report.StandardsTraceabilitySummary))
            sb.AppendLine(report.StandardsTraceabilitySummary);
        foreach (var check in report.Checks)
            sb.AppendLine($"- {check.Name}: {check.Status}. {check.Evidence} Next: {check.NextAction}");
        return sb.ToString();
    }

    private static void AddHygieneEvidence(ICollection<ClientDemoGateCheck> checks)
    {
        var summary = BuildTestEvidenceService.GetSummary();
        var latest = summary.Latest;
        if (latest is not null && BuildTestEvidenceService.IsPassingStatus(latest.HygieneStatus))
        {
            checks.Add(Pass("Repository hygiene", $"Hygiene {latest.HygieneStatus}; evidence generated {latest.GeneratedAtUtc:O}."));
            return;
        }

        checks.Add(Blocked(
            "Repository hygiene",
            latest is null ? "No build/test evidence is recorded." : $"Hygiene status is {latest.HygieneStatus}.",
            "Run Scripts/check-repo-hygiene.ps1 and record/import passing build evidence."));
    }

    private static void AddBuildEvidence(ICollection<ClientDemoGateCheck> checks)
    {
        var summary = BuildTestEvidenceService.GetSummary();
        var latest = summary.Latest;
        if (latest is not null && BuildTestEvidenceService.IsPassingStatus(latest.BuildStatus))
        {
            checks.Add(Pass("Release build", $"Build {latest.BuildStatus}; configuration={latest.Configuration}; evidence generated {latest.GeneratedAtUtc:O}."));
            return;
        }

        checks.Add(Blocked(
            "Release build",
            latest is null ? "No build/test evidence is recorded." : $"Build status is {latest.BuildStatus}.",
            "Run dotnet build AOI_PCB_Database.slnx --configuration Release and record/import passing build evidence."));
    }

    private static void AddTestEvidence(ICollection<ClientDemoGateCheck> checks)
    {
        var summary = BuildTestEvidenceService.GetSummary();
        var latest = summary.Latest;
        if (latest is not null && BuildTestEvidenceService.IsPassingStatus(latest.TestStatus))
        {
            checks.Add(Pass("Release tests", $"Tests {latest.TestStatus}; testResultPath={latest.TestResultPath}; evidence generated {latest.GeneratedAtUtc:O}."));
            return;
        }

        checks.Add(Blocked(
            "Release tests",
            latest is null ? "No build/test evidence is recorded." : $"Test status is {latest.TestStatus}.",
            "Run dotnet test AOI_PCB_Database.slnx --configuration Release and record/import passing test evidence."));
    }

    private static void AddUiLayoutAudit(ICollection<ClientDemoGateCheck> checks)
    {
        var reportPath = FindLatestArtifact("hmi_layout_audit.json");
        if (string.IsNullOrWhiteSpace(reportPath) || !File.Exists(reportPath))
        {
            checks.Add(Blocked(
                "UI layout audit",
                "No hmi_layout_audit.json report is available.",
                "Run the WPF HMI layout audit on Windows and attach TestResults/hmi_layout_audit.html/json."));
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(reportPath));
            var passed = document.RootElement.TryGetProperty("passed", out var passedElement) && passedElement.GetBoolean();
            var failCount = document.RootElement.TryGetProperty("issues", out var issuesElement)
                ? issuesElement.EnumerateArray().Count(issue =>
                    issue.TryGetProperty("severity", out var severity) &&
                    severity.GetString()?.Equals("Fail", StringComparison.OrdinalIgnoreCase) == true &&
                    (!issue.TryGetProperty("approved", out var approved) || !approved.GetBoolean()))
                : 0;

            checks.Add(passed && failCount == 0
                ? Pass("UI layout audit", $"PASS layout audit: {reportPath}.")
                : Blocked("UI layout audit", $"Layout audit has {failCount} unapproved failure(s): {reportPath}.", "Fix clipped/unreachable UI or approve a narrow exception and rerun the audit."));
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
            checks.Add(Blocked("UI layout audit", $"Layout audit report could not be parsed: {ex.Message}", "Regenerate TestResults/hmi_layout_audit.json."));
        }
    }

    private static void AddNavigationPerformance(ICollection<ClientDemoGateCheck> checks)
    {
        var reportPath = FindLatestArtifact("ui_navigation_performance.json");
        if (string.IsNullOrWhiteSpace(reportPath) || !File.Exists(reportPath))
        {
            checks.Add(Blocked(
                "Navigation performance",
                "No ui_navigation_performance.json report is available.",
                "Run the UI navigation performance smoke test on Windows."));
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(reportPath));
            var slowEvents = 0;
            if (document.RootElement.TryGetProperty("events", out var eventsElement))
            {
                var visualThreshold = document.RootElement.TryGetProperty("visualResponseThresholdMilliseconds", out var visual)
                    ? visual.GetInt64()
                    : UiPerformanceMonitorService.MenuResponseWarningMilliseconds;
                var cachedThreshold = document.RootElement.TryGetProperty("cachedSwitchThresholdMilliseconds", out var cached)
                    ? cached.GetInt64()
                    : UiPerformanceMonitorService.CachedPageSwitchWarningMilliseconds;
                foreach (var item in eventsElement.EnumerateArray())
                {
                    var category = item.TryGetProperty("category", out var cat) ? cat.GetString() ?? string.Empty : string.Empty;
                    var duration = item.TryGetProperty("durationMilliseconds", out var dur) ? dur.GetInt64() : 0;
                    if (category.Equals("NAVIGATION_VISUAL_RESPONSE", StringComparison.OrdinalIgnoreCase) && duration > visualThreshold)
                        slowEvents++;
                    if (category.Equals("CACHED_PAGE_SWITCH", StringComparison.OrdinalIgnoreCase) && duration > cachedThreshold)
                        slowEvents++;
                }
            }

            checks.Add(slowEvents == 0
                ? Pass("Navigation performance", $"PASS navigation performance smoke: {reportPath}.")
                : Warning("Navigation performance", $"{slowEvents} navigation performance warning(s) found in {reportPath}.", "Review slow page construction or cached page switches before demo; warning is allowed but must be visible."));
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
            checks.Add(Blocked("Navigation performance", $"Navigation performance report could not be parsed: {ex.Message}", "Regenerate TestResults/ui_navigation_performance.json."));
        }
    }

    private static void AddUiStabilitySoak(ICollection<ClientDemoGateCheck> checks)
    {
        var latest = UiNavigationSoakTestService.GetLatestReport();
        if (UiNavigationSoakTestService.IsThirtyMinuteOrBetterPass(latest))
        {
            checks.Add(Pass(
                "UI stability soak",
                $"PASS {latest!.Profile}; actual={latest.ActualDuration}; pages={latest.PageAttempts}; slow={latest.SlowNavigationCount}; crashes={latest.CrashCount}; memoryGrowthMB={latest.MemoryGrowthMegabytes:F1}; report={latest.JsonPath}."));
            return;
        }

        if (UiNavigationSoakTestService.IsAcceptedShorterSmokePass(latest))
        {
            checks.Add(Warning(
                "UI stability soak",
                $"Accepted shorter smoke evidence: {latest!.Profile}; actual={latest.ActualDuration}; pages={latest.PageAttempts}; slow={latest.SlowNavigationCount}; crashes={latest.CrashCount}; memoryGrowthMB={latest.MemoryGrowthMegabytes:F1}; report={latest.JsonPath}.",
                "Run the 30-minute local stability profile before a formal client demo; keep this warning visible when relying on shorter smoke evidence."));
            return;
        }

        var evidence = latest is null
            ? "No ui_stability_report.json report is available."
            : $"Latest UI stability report is not accepted: status={latest.Status}; profile={latest.Profile}; actual={latest.ActualDuration}; requested={latest.RequestedDuration}; canceled={latest.WasCanceled}; crashes={latest.CrashCount}.";
        checks.Add(Blocked(
            "UI stability soak",
            evidence,
            "Run a PASS 30-minute UI navigation stability profile, or explicitly accepted 5-minute smoke evidence for short demo packaging."));
    }

    private static void AddExportVerification(ICollection<ClientDemoGateCheck> checks)
    {
        var verifications = AoiDatabase.GetExportVerifications(100);
        if (verifications.Count == 0)
        {
            checks.Add(Blocked("Export verification", "No export verification records are available.", "Run export verification tests and generate at least one verified report/package artifact."));
            return;
        }

        var errors = verifications.Where(item => item.Status.Equals("ERROR", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (errors.Length > 0)
        {
            checks.Add(Blocked("Export verification", $"{errors.Length} export verification ERROR record(s) exist. Latest={errors[0].ExportType}: {errors[0].ExportPath}.", "Fix failing exports and rerun verification."));
            return;
        }

        var warnings = verifications.Count(item => item.Status.Equals("WARN", StringComparison.OrdinalIgnoreCase));
        checks.Add(warnings > 0
            ? Warning("Export verification", $"Export verification has {warnings} WARN record(s) and no ERROR records.", "Review warning records before client delivery.")
            : Pass("Export verification", $"PASS export verification evidence. Records checked={verifications.Count}."));
    }

    private static void AddCrashReports(ICollection<ClientDemoGateCheck> checks)
    {
        if (!string.IsNullOrWhiteSpace(CrashReportService.LastCrashReportPath) &&
            File.Exists(CrashReportService.LastCrashReportPath))
        {
            checks.Add(Blocked(
                "Crash reports in latest session",
                $"Crash report exists for this app session: {CrashReportService.LastCrashReportPath}",
                "Investigate the crash report, fix the issue, restart the app, and rerun the gate."));
            return;
        }

        checks.Add(Pass("Crash reports in latest session", "No crash report has been recorded in the current app session."));
    }

    private static void AddAlarmEvents(ICollection<ClientDemoGateCheck> checks)
    {
        var critical = AlarmEventService.GetActiveCriticalAlarms();
        if (critical.Count > 0)
        {
            checks.Add(Blocked(
                "Active critical alarms",
                $"{critical.Count} active Critical alarm(s) exist. Latest: {critical[0].Source} - {critical[0].Message}",
                "Resolve Critical alarms before client demo or release packaging."));
            return;
        }

        checks.Add(Pass("Active critical alarms", "No active Critical alarms are present."));

        var unacknowledgedAlarms = AlarmEventService.GetUnacknowledgedAlarmLevelEvents();
        checks.Add(unacknowledgedAlarms.Count > 0
            ? Warning(
                "Unacknowledged alarm-level events",
                $"{unacknowledgedAlarms.Count} unacknowledged Alarm-level event(s) require operator or engineering acknowledgement.",
                "Acknowledge or resolve alarm-level events before client-facing demonstration.")
            : Pass("Unacknowledged alarm-level events", "No unacknowledged Alarm-level events are present."));
    }

    private static void AddCriticalPilotIssues(ICollection<ClientDemoGateCheck> checks)
    {
        var openHighRisk = PilotIssueService.Search(new PilotIssueFilter { OpenOnly = true })
            .Where(issue => PilotIssueService.IsHighOrCritical(issue.Severity))
            .ToArray();
        if (openHighRisk.Length > 0)
        {
            checks.Add(Blocked(
                "High/Critical pilot issues",
                $"{openHighRisk.Length} unresolved High/Critical pilot issue(s) are open.",
                "Close, fix, verify, or explicitly waive High/Critical issues before client package generation."));
            return;
        }

        var summary = PilotIssueService.Summarize();
        checks.Add(Pass("High/Critical pilot issues", $"No unresolved High/Critical pilot issues. Open issues={summary.Open}."));
    }

    private static void AddStage1Package(ICollection<ClientDemoGateCheck> checks, DeploymentProfile profile)
    {
        if (profile != DeploymentProfile.Stage1ImageValidation)
            return;

        var packages = AoiDatabase.GetValidationPackages(1);
        var latest = packages.Count > 0 ? packages[0] : null;
        if (latest is null)
        {
            checks.Add(Blocked(
                "Stage 1 package",
                "Stage 1 deployment profile is active, but no Stage 1 validation package record exists.",
                "Generate the Stage 1 validation/customer package after build, UI, and crash gates pass."));
            return;
        }

        checks.Add(Pass("Stage 1 package", $"Latest Stage 1 package={latest.PackageId}; status={latest.AcceptanceStatus}; folder={latest.PackagePath}."));
    }

    private static void AddHardwareWarning(ICollection<ClientDemoGateCheck> checks)
    {
        var camera = AoiDatabase.GetLatestCameraAcceptanceRun(realHardwareOnly: true);
        var robot = AoiDatabase.GetLatestRobotAcceptanceRun();
        var lighting = AoiDatabase.GetLatestLightingAcceptanceRun();
        var profile3D = AoiDatabase.GetLatestProfile3DAcceptanceRun();
        var realHardwareValidated =
            camera?.FactoryReadinessStatus is "PASS" or "WARN" &&
            robot?.SourceKind is not ("Simulated" or "NotValidated") &&
            lighting?.IsSimulated == false &&
            profile3D?.IsSimulated == false;

        checks.Add(realHardwareValidated
            ? Pass("Real hardware validation", "Real hardware acceptance evidence is present.")
            : Warning("Real hardware validation", "Real camera/lighting/robot/3D hardware is not fully validated. Client demo evidence must be labeled as PoC/simulated where applicable.", "Run real hardware acceptance tests or keep the warning in client-facing materials."));
    }

    private static void AddStandardsTraceabilitySummary(ICollection<ClientDemoGateCheck> checks, StandardsTraceabilityReport report)
    {
        var summary = StandardsTraceabilitySummaryFor(report);
        if (report.MissingCount == 0 && report.PartialCount == 0 && report.ReleaseBlockerMissingCount == 0)
        {
            checks.Add(Pass("Standards traceability", summary));
            return;
        }

        checks.Add(Warning(
            "Standards traceability",
            summary,
            "Review the Standards & Quality Checklist and include standards_traceability_matrix.html/pdf/json in the client evidence package."));
    }

    private static string StandardsTraceabilitySummaryFor(StandardsTraceabilityReport report)
        => $"Standards traceability {report.OverallStatus}: satisfied={report.SatisfiedCount}; partial={report.PartialCount}; missing={report.MissingCount}; missing release blockers={report.ReleaseBlockerMissingCount}. Standards-aligned evidence only; not certification.";

    private static ClientDemoGateCheck Pass(string name, string evidence)
        => new() { Name = name, Status = ClientDemoGateStatus.Pass, Evidence = evidence, NextAction = "No action required." };

    private static ClientDemoGateCheck Warning(string name, string evidence, string nextAction)
        => new() { Name = name, Status = ClientDemoGateStatus.Warning, Evidence = evidence, NextAction = nextAction };

    private static ClientDemoGateCheck Blocked(string name, string evidence, string nextAction)
        => new() { Name = name, Status = ClientDemoGateStatus.Blocked, Evidence = evidence, NextAction = nextAction };

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

    private static string Html(string value)
        => System.Net.WebUtility.HtmlEncode(value ?? string.Empty);

    private static string? _artifactRootOverrideForTests;

    /// <summary>
    /// Tests point artifact discovery at an isolated folder. The default roots include the
    /// repository TestResults folder, which other test processes (e.g. the UI test project
    /// running concurrently in a full-solution run) write real reports into — reading it
    /// from tests makes gate outcomes depend on test execution order.
    /// </summary>
    public static void OverrideArtifactRootForTests(string? directory)
        => _artifactRootOverrideForTests = directory;

    private static string? FindLatestArtifact(string fileName)
    {
        var roots = _artifactRootOverrideForTests is not null
            ? new[] { _artifactRootOverrideForTests }
            : new[]
            {
                Path.Combine(FindRepositoryRoot(), "TestResults"),
                Path.Combine(AppContext.BaseDirectory, "TestResults"),
            };

        return roots
            .Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AOI_PCB_Database.slnx")))
                return current.FullName;
            current = current.Parent;
        }

        return AppContext.BaseDirectory;
    }
}
