using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using AOI_Monitor.Data;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public sealed record StandardsTraceabilityExport(string Folder, string JsonPath, string HtmlPath, string PdfPath);

public static class StandardsTraceabilityService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string DefaultOutputRoot => Path.Combine(AoiDatabase.StorageRoot, "exports", "standards_traceability");

    public static StandardsTraceabilityReport Evaluate(DeploymentProfile? profile = null, string? artifactRoot = null)
    {
        AoiDatabase.Initialize();
        var deploymentProfile = profile ?? DeploymentProfileSettingsService.Load();
        var rows = new List<StandardsTraceabilityMatrix>();
        var layout = ReadHmiLayoutAudit(artifactRoot);
        var navigation = ReadNavigationPerformance(artifactRoot);
        var codeQuality = ReadSimpleStatusReport("code_quality_report.json", artifactRoot);
        var industrialGate = ReadSimpleStatusReport("industrial_quality_gate_report.json", artifactRoot);
        var buildSummary = BuildTestEvidenceService.GetSummary();
        var exportVerifications = AoiDatabase.GetExportVerifications(100);
        var latency = InspectionLatencyService.GetRecentSummary();
        var uiSoak = UiNavigationSoakTestService.GetLatestReport();
        var factorySoak = AoiDatabase.GetLatestSoakTestRun();
        var validationPackages = AoiDatabase.GetValidationPackages(1);
        var validationPackage = validationPackages.Count > 0 ? validationPackages[0] : null;
        var modelAcceptance = AoiDatabase.GetLatestModelAcceptanceRun();
        var camera = AoiDatabase.GetLatestCameraAcceptanceRun(realHardwareOnly: false);
        var realCamera = AoiDatabase.GetLatestCameraAcceptanceRun(realHardwareOnly: true);
        var lighting = AoiDatabase.GetLatestLightingAcceptanceRun();
        var profile3D = AoiDatabase.GetLatestProfile3DAcceptanceRun();
        var robot = AoiDatabase.GetLatestRobotAcceptanceRun();
        var mes = MesSpoolService.EvaluateReadiness();
        var centralSync = CentralSyncService.EvaluateReadiness();
        var alarmEvents = AlarmEventService.GetEvents(new AlarmEventQuery { OperatorVisibleOnly = false });
        var activeCriticalAlarms = AlarmEventService.GetActiveCriticalAlarms();
        var unacknowledgedAlarms = AlarmEventService.GetUnacknowledgedAlarmLevelEvents();
        var pilotIssues = PilotIssueService.Summarize();
        var recentAudits = AoiDatabase.GetAuditEvents(new LogFilter(), 25);
        var repoRoot = FindRepositoryRoot();

        AddProjectSpecRows(rows, deploymentProfile, layout, navigation, codeQuality, industrialGate, buildSummary, exportVerifications, latency, uiSoak, factorySoak, validationPackage, realCamera, camera, lighting, profile3D, robot, mes, centralSync, repoRoot);
        AddIso9241Rows(rows, layout, navigation, uiSoak, alarmEvents, activeCriticalAlarms, validationPackage, repoRoot);
        AddIso25010Rows(rows, layout, navigation, codeQuality, industrialGate, buildSummary, exportVerifications, latency, uiSoak, factorySoak, validationPackage, modelAcceptance, realCamera, camera, lighting, profile3D, robot, mes, centralSync, recentAudits, pilotIssues, repoRoot);
        AddIecAlarmRows(rows, alarmEvents, activeCriticalAlarms, unacknowledgedAlarms, repoRoot);

        var report = new StandardsTraceabilityReport
        {
            GeneratedAtUtc = DateTime.UtcNow,
            DeploymentProfile = deploymentProfile,
            Items = rows,
            SatisfiedCount = rows.Count(IsSatisfied),
            PartialCount = rows.Count(IsPartial),
            MissingCount = rows.Count(IsMissing),
            NotApplicableCount = rows.Count(IsNotApplicable),
            ReleaseBlockerMissingCount = rows.Count(IsMissingReleaseBlocker),
        };
        report.OverallStatus = report.ReleaseBlockerMissingCount > 0
            ? StandardsTraceabilityStatus.Missing
            : report.MissingCount > 0 || report.PartialCount > 0
                ? StandardsTraceabilityStatus.Partial
                : StandardsTraceabilityStatus.Satisfied;
        return report;
    }

    public static StandardsTraceabilityExport ExportReport(
        string? outputRoot = null,
        DeploymentProfile? profile = null,
        string? artifactRoot = null)
    {
        var root = string.IsNullOrWhiteSpace(outputRoot) ? DefaultOutputRoot : outputRoot.Trim();
        var folder = EnsureUniqueFolder(Path.Combine(root, $"standards_traceability_{DateTime.UtcNow:yyyyMMdd_HHmmss}"));
        return ExportToFolder(folder, profile, artifactRoot, recordExport: true);
    }

    public static StandardsTraceabilityExport ExportToFolder(
        string folder,
        DeploymentProfile? profile = null,
        string? artifactRoot = null,
        bool recordExport = true)
    {
        Directory.CreateDirectory(folder);
        var report = Evaluate(profile, artifactRoot);
        var jsonPath = Path.Combine(folder, "standards_traceability_matrix.json");
        var htmlPath = Path.Combine(folder, "standards_traceability_matrix.html");
        var pdfPath = Path.Combine(folder, "standards_traceability_matrix.pdf");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, JsonOptions), Encoding.UTF8);
        File.WriteAllText(htmlPath, BuildHtml(report), Encoding.UTF8);
        PdfExportService.ExportHtmlFileToPdf(htmlPath, pdfPath, "Standards Traceability Matrix");

        if (recordExport)
        {
            ExportVerificationService.RecordVerifiedExport("StandardsTraceabilityReport", folder, report.OverallStatus == StandardsTraceabilityStatus.Missing ? "WARN" : "OK");
            AoiDatabase.RecordAuditEvent(
                "STANDARDS_TRACEABILITY_EXPORT",
                $"Standards traceability matrix exported: status={report.OverallStatus}; missing={report.MissingCount}; releaseBlockerMissing={report.ReleaseBlockerMissingCount}.",
                relatedEntityType: "StandardsTraceabilityReport",
                relatedPath: folder);
        }

        return new StandardsTraceabilityExport(folder, jsonPath, htmlPath, pdfPath);
    }

    public static string BuildHtml(StandardsTraceabilityReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html><html><head><meta charset=\"utf-8\"><title>Standards Traceability Matrix</title>");
        sb.AppendLine("<style>body{font-family:Segoe UI,Arial,sans-serif;margin:24px;color:#17212b;background:#f7f9fb}.hero{background:#fff;border:1px solid #d7e0e8;padding:16px;margin-bottom:16px}.Satisfied{color:#176b3a}.Partial{color:#8a5a00}.Missing{color:#a12626}.NotApplicable{color:#596773}table{border-collapse:collapse;width:100%;background:#fff}td,th{border:1px solid #d7e0e8;padding:7px;text-align:left;vertical-align:top}th{background:#eef3f7}.blocker{font-weight:700;color:#a12626}</style></head><body>");
        sb.AppendLine("<div class=\"hero\">");
        sb.AppendLine($"<h1>Standards Traceability Matrix</h1><p><strong class=\"{Css(report.OverallStatus)}\">{Html(report.OverallStatus)}</strong><br>Deployment profile: {Html(report.DeploymentProfile.ToString())}<br>Generated UTC: {report.GeneratedAtUtc:O}</p>");
        sb.AppendLine($"<p><strong>Certification boundary:</strong> {Html(report.CertificationStatement)}</p>");
        sb.AppendLine($"<p>Satisfied={report.SatisfiedCount}; Partial={report.PartialCount}; Missing={report.MissingCount}; Not applicable={report.NotApplicableCount}; Missing release blockers={report.ReleaseBlockerMissingCount}.</p>");
        sb.AppendLine("</div>");
        sb.AppendLine("<table><tr><th>Source</th><th>Requirement / Principle</th><th>ID</th><th>Evidence Type</th><th>Evidence Path</th><th>Status</th><th>Blocking</th><th>Notes</th></tr>");
        foreach (var item in report.Items)
        {
            var blockingClass = item.BlockingLevel.Equals(StandardsTraceabilityBlockingLevel.ReleaseBlocker, StringComparison.OrdinalIgnoreCase)
                ? " class=\"blocker\""
                : string.Empty;
            sb.AppendLine($"<tr><td>{Html(item.StandardOrSource)}</td><td>{Html(item.PrincipleOrRequirement)}</td><td>{Html(item.ProjectRequirementId)}</td><td>{Html(item.EvidenceType)}</td><td>{Html(item.EvidencePath)}</td><td class=\"{Css(item.Status)}\">{Html(item.Status)}</td><td{blockingClass}>{Html(item.BlockingLevel)}</td><td>{Html(item.Notes)}</td></tr>");
        }

        sb.AppendLine("</table></body></html>");
        return sb.ToString();
    }

    private static void AddProjectSpecRows(
        ICollection<StandardsTraceabilityMatrix> rows,
        DeploymentProfile profile,
        HmiLayoutEvidence layout,
        NavigationPerformanceEvidence navigation,
        SimpleStatusEvidence codeQuality,
        SimpleStatusEvidence industrialGate,
        BuildTestEvidenceSummary buildSummary,
        IReadOnlyList<ExportVerificationRecord> exportVerifications,
        InspectionLatencySummary latency,
        UiNavigationSoakResult? uiSoak,
        SoakTestResult? factorySoak,
        ValidationPackageRecord? validationPackage,
        CameraAcceptanceRun? realCamera,
        CameraAcceptanceRun? anyCamera,
        LightingAcceptanceRun? lighting,
        Profile3DAcceptanceRun? profile3D,
        RobotAcceptanceRun? robot,
        MesReadinessSummary mes,
        CentralSyncReadinessSummary centralSync,
        string repoRoot)
    {
        Add(rows, "Project Spec", "1920x1080 minimum window and 100/125/150% DPI layout audit", "HMI-001", "HMI layout audit", layout.Path, LayoutStatus(layout), StandardsTraceabilityBlockingLevel.ReleaseBlocker, LayoutNotes(layout));
        Add(rows, "Project Spec", "Operator-facing text is at least 14 pt and primary buttons are at least 120x40 px", "HMI-002", "HMI layout audit", layout.Path, LayoutStatus(layout), StandardsTraceabilityBlockingLevel.ReleaseBlocker, LayoutNotes(layout));
        Add(rows, "Project Spec", "Dense pages use ScrollViewer/adaptive layout and critical controls remain reachable", "HMI-003", "HMI layout audit", layout.Path, LayoutStatus(layout), StandardsTraceabilityBlockingLevel.ReleaseBlocker, LayoutNotes(layout));
        Add(rows, "Project Spec", "High contrast plus green/red/yellow status semantics", "HMI-004", "Style guide plus HMI audit", CombineEvidence(DocPath(repoRoot, "HMI_Style_Guide.md"), layout.Path), File.Exists(DocPath(repoRoot, "HMI_Style_Guide.md")) ? (layout.Exists ? LayoutStatus(layout) : StandardsTraceabilityStatus.Partial) : StandardsTraceabilityStatus.Missing, StandardsTraceabilityBlockingLevel.Warning, "Color/status requirements are standards-aligned design rules, not a certification claim.");
        Add(rows, "Project Spec", "Navigation click visual response and cached page switching remain responsive", "PERF-001", "UI navigation performance report", navigation.Path, NavigationStatus(navigation), StandardsTraceabilityBlockingLevel.ReleaseBlocker, NavigationNotes(navigation));
        Add(rows, "Project Spec", "Inspection visualization targets one-second frame-to-overlay/result feedback", "PERF-002", "Latency trace or benchmark evidence", LatencyEvidencePath(latency, codeQuality, industrialGate), LatencyStatus(latency), StandardsTraceabilityBlockingLevel.ReleaseBlocker, latency.TraceCount == 0 ? "No recent latency traces are recorded." : $"Traces={latency.TraceCount}; p95 frame-to-overlay={latency.P95FrameToOverlayMs:F0} ms; over1s={latency.OverOneSecondCount}.");
        Add(rows, "Project Spec", "8-hour stable factory PoC is required separately for Full Factory Automation", "REL-001", "System soak test report", factorySoak is null ? string.Empty : $"SQLite:SoakTestRuns/{factorySoak.Id}", FactorySoakStatus(profile, factorySoak), profile == DeploymentProfile.FullFactoryAutomation ? StandardsTraceabilityBlockingLevel.ReleaseBlocker : StandardsTraceabilityBlockingLevel.Info, FactorySoakNotes(profile, factorySoak));
        Add(rows, "Project Spec", "Verified CSV, PNG, and PDF exports are available for client evidence", "EXPORT-001", "Export verification records", ExportEvidencePath(exportVerifications), ExportEvidenceStatus(exportVerifications), StandardsTraceabilityBlockingLevel.ReleaseBlocker, ExportEvidenceNotes(exportVerifications));
        Add(rows, "Project Spec", "Stage 1 validation package evidence exists when Stage 1 profile is used", "EXPORT-002", "Validation package record", validationPackage?.ManifestPath ?? string.Empty, validationPackage is null ? (profile == DeploymentProfile.Stage1ImageValidation ? StandardsTraceabilityStatus.Missing : StandardsTraceabilityStatus.Partial) : StandardsTraceabilityStatus.Satisfied, profile == DeploymentProfile.Stage1ImageValidation ? StandardsTraceabilityBlockingLevel.ReleaseBlocker : StandardsTraceabilityBlockingLevel.Warning, validationPackage is null ? "No Stage 1 validation package is recorded." : $"Package={validationPackage.PackageId}; status={validationPackage.AcceptanceStatus}.");
        Add(rows, "Project Spec", "Camera, lighting, 3D profile, and robot integration evidence is staged and simulation is not treated as real hardware", "HW-001", "Acceptance run records", HardwareEvidencePath(realCamera, anyCamera, lighting, profile3D, robot), HardwareStatus(profile, realCamera, anyCamera, lighting, profile3D, robot), profile == DeploymentProfile.FullFactoryAutomation ? StandardsTraceabilityBlockingLevel.ReleaseBlocker : StandardsTraceabilityBlockingLevel.Warning, HardwareNotes(profile, realCamera, anyCamera, lighting, profile3D, robot));
        Add(rows, "Project Spec", "MES and central sync evidence is staged and pending/failed queues are visible", "MES-001", "MES queue and central sync readiness", $"MES={mes.Status}; CentralSync={centralSync.Status}", MesCentralStatus(profile, mes, centralSync), profile is DeploymentProfile.Stage4MesPilot or DeploymentProfile.FullFactoryAutomation ? StandardsTraceabilityBlockingLevel.ReleaseBlocker : StandardsTraceabilityBlockingLevel.Warning, $"MES pending={mes.PendingCount}; failed={mes.FailedCount}; traceability={mes.LatestTraceabilityTestStatus}; central pending={centralSync.PendingCount}; failed={centralSync.FailedCount}.");
        Add(rows, "Project Spec", "Build, tests, code quality, and industrial gates are recorded before client packaging", "CI-001", "Build/test/code-quality evidence", CombineEvidence(BuildEvidencePath(buildSummary), codeQuality.Path, industrialGate.Path), BuildAndCodeQualityStatus(buildSummary, codeQuality, industrialGate), StandardsTraceabilityBlockingLevel.ReleaseBlocker, BuildAndCodeQualityNotes(buildSummary, codeQuality, industrialGate));
    }

    private static void AddIso9241Rows(
        ICollection<StandardsTraceabilityMatrix> rows,
        HmiLayoutEvidence layout,
        NavigationPerformanceEvidence navigation,
        UiNavigationSoakResult? uiSoak,
        IReadOnlyList<AlarmEvent> alarmEvents,
        IReadOnlyList<AlarmEvent> activeCriticalAlarms,
        ValidationPackageRecord? validationPackage,
        string repoRoot)
    {
        Add(rows, "ISO 9241-style HMI", "Task suitability: screens support inspection, review, export, readiness, and integration work without hiding critical actions", "HMI-TASK", "Layout audit and validation package", CombineEvidence(layout.Path, validationPackage?.ManifestPath ?? string.Empty), layout.Passed && validationPackage is not null ? StandardsTraceabilityStatus.Satisfied : layout.Exists ? StandardsTraceabilityStatus.Partial : StandardsTraceabilityStatus.Missing, StandardsTraceabilityBlockingLevel.Warning, "Evidence focuses on task fit and operator workflow, not formal ISO certification.");
        Add(rows, "ISO 9241-style HMI", "Self-descriptiveness: operator messages are plain language and simulated/not-validated states are labeled", "HMI-DESC", "Alarm tests, glossary, and HMI docs", CombineEvidence(TestPath(repoRoot, "PlainLanguageGlossaryTests.cs"), TestPath(repoRoot, "AlarmEventServiceTests.cs"), DocPath(repoRoot, "HMI_Style_Guide.md")), TestsExist(repoRoot, "PlainLanguageGlossaryTests.cs", "AlarmEventServiceTests.cs") ? StandardsTraceabilityStatus.Satisfied : StandardsTraceabilityStatus.Partial, StandardsTraceabilityBlockingLevel.Warning, "Engineer/Admin detail may include technical data; operator text must remain plain language.");
        Add(rows, "ISO 9241-style HMI", "Controllability: long operations expose progress/cancel behavior and navigation failures are recoverable", "HMI-CTRL", "Navigation soak and error-boundary tests", CombineEvidence(SoakEvidencePath(uiSoak), TestPath(repoRoot, "CrashSafetyTests.cs")), UiNavigationSoakTestService.IsThirtyMinuteOrBetterPass(uiSoak) ? StandardsTraceabilityStatus.Satisfied : TestsExist(repoRoot, "CrashSafetyTests.cs") ? StandardsTraceabilityStatus.Partial : StandardsTraceabilityStatus.Missing, StandardsTraceabilityBlockingLevel.Warning, "Accepted shorter smoke evidence is still only partial for client-demo readiness.");
        Add(rows, "ISO 9241-style HMI", "Expectation conformity: status colors, alarm severities, and readiness outcomes follow consistent meanings", "HMI-EXPECT", "Style guide and alarm model tests", CombineEvidence(DocPath(repoRoot, "HMI_Style_Guide.md"), TestPath(repoRoot, "AlarmEventServiceTests.cs")), File.Exists(DocPath(repoRoot, "HMI_Style_Guide.md")) && TestsExist(repoRoot, "AlarmEventServiceTests.cs") ? StandardsTraceabilityStatus.Satisfied : StandardsTraceabilityStatus.Partial, StandardsTraceabilityBlockingLevel.Info, "Green/amber/red outcomes are project conventions aligned to industrial HMI practice.");
        Add(rows, "ISO 9241-style HMI", "Learnability: user, deployment, and contributor checklists are maintained", "HMI-LEARN", "Documentation", CombineEvidence(DocPath(repoRoot, "User_Manual.md"), DocPath(repoRoot, "Contributor_Quality_Checklist.md")), File.Exists(DocPath(repoRoot, "User_Manual.md")) && File.Exists(DocPath(repoRoot, "Contributor_Quality_Checklist.md")) ? StandardsTraceabilityStatus.Satisfied : StandardsTraceabilityStatus.Partial, StandardsTraceabilityBlockingLevel.Info, "Documentation supports onboarding; it is not certification evidence by itself.");
        Add(rows, "ISO 9241-style HMI", "User-error robustness: alarms, recoverable errors, and active Critical alarms are visible", "HMI-ERROR", "Alarm state and recoverability tests", CombineEvidence(TestPath(repoRoot, "CrashSafetyTests.cs"), TestPath(repoRoot, "AlarmEventServiceTests.cs")), activeCriticalAlarms.Count == 0 && TestsExist(repoRoot, "CrashSafetyTests.cs", "AlarmEventServiceTests.cs") ? StandardsTraceabilityStatus.Satisfied : StandardsTraceabilityStatus.Partial, StandardsTraceabilityBlockingLevel.ReleaseBlocker, activeCriticalAlarms.Count == 0 ? $"Alarm events recorded={alarmEvents.Count}; no active Critical alarms." : $"{activeCriticalAlarms.Count} active Critical alarm(s) must remain visible and be resolved.");
        _ = navigation;
    }

    private static void AddIso25010Rows(
        ICollection<StandardsTraceabilityMatrix> rows,
        HmiLayoutEvidence layout,
        NavigationPerformanceEvidence navigation,
        SimpleStatusEvidence codeQuality,
        SimpleStatusEvidence industrialGate,
        BuildTestEvidenceSummary buildSummary,
        IReadOnlyList<ExportVerificationRecord> exportVerifications,
        InspectionLatencySummary latency,
        UiNavigationSoakResult? uiSoak,
        SoakTestResult? factorySoak,
        ValidationPackageRecord? validationPackage,
        ModelAcceptanceRun? modelAcceptance,
        CameraAcceptanceRun? realCamera,
        CameraAcceptanceRun? anyCamera,
        LightingAcceptanceRun? lighting,
        Profile3DAcceptanceRun? profile3D,
        RobotAcceptanceRun? robot,
        MesReadinessSummary mes,
        CentralSyncReadinessSummary centralSync,
        IReadOnlyList<AuditEventRecord> recentAudits,
        PilotIssueSummary pilotIssues,
        string repoRoot)
    {
        Add(rows, "ISO/IEC 25010-style Quality", "Functional suitability: inspection, model validation, exports, and readiness workflows have evidence", "QUAL-FUNC", "Validation/model/export records", CombineEvidence(validationPackage?.ManifestPath ?? string.Empty, modelAcceptance is null ? string.Empty : $"SQLite:ModelAcceptanceRuns/{modelAcceptance.Id}", ExportEvidencePath(exportVerifications)), validationPackage is not null && ExportEvidenceStatus(exportVerifications) == StandardsTraceabilityStatus.Satisfied ? StandardsTraceabilityStatus.Satisfied : StandardsTraceabilityStatus.Partial, StandardsTraceabilityBlockingLevel.Warning, modelAcceptance is null ? "Model acceptance evidence is not recorded yet." : $"Latest model acceptance={modelAcceptance.Status}.");
        Add(rows, "ISO/IEC 25010-style Quality", "Performance efficiency: navigation, latency, and soak metrics are measured", "QUAL-PERF", "Performance reports", CombineEvidence(navigation.Path, LatencyEvidencePath(latency, codeQuality, industrialGate), SoakEvidencePath(uiSoak)), NavigationStatus(navigation) == StandardsTraceabilityStatus.Satisfied && LatencyStatus(latency) != StandardsTraceabilityStatus.Missing ? StandardsTraceabilityStatus.Satisfied : StandardsTraceabilityStatus.Partial, StandardsTraceabilityBlockingLevel.ReleaseBlocker, $"Navigation slow events={navigation.SlowEventCount}; latency traces={latency.TraceCount}; UI soak={uiSoak?.Status ?? "none"}.");
        Add(rows, "ISO/IEC 25010-style Quality", "Compatibility: staged camera, lighting, robot, MES, and central sync integration evidence is explicit", "QUAL-COMPAT", "Integration acceptance and readiness records", HardwareEvidencePath(realCamera, anyCamera, lighting, profile3D, robot) + "; " + $"MES={mes.Status}; CentralSync={centralSync.Status}", CompatibilityStatus(realCamera, anyCamera, lighting, profile3D, robot, mes, centralSync), StandardsTraceabilityBlockingLevel.Warning, "Simulation, mock, or not-validated integration evidence must remain labeled.");
        Add(rows, "ISO/IEC 25010-style Quality", "Usability: layout, typography, scroll, alarms, and operator text are audited", "QUAL-USABLE", "HMI audit and alarm tests", CombineEvidence(layout.Path, TestPath(repoRoot, "AlarmEventServiceTests.cs")), layout.Passed && TestsExist(repoRoot, "AlarmEventServiceTests.cs") ? StandardsTraceabilityStatus.Satisfied : StandardsTraceabilityStatus.Partial, StandardsTraceabilityBlockingLevel.ReleaseBlocker, LayoutNotes(layout));
        Add(rows, "ISO/IEC 25010-style Quality", "Reliability: crash reports, recoverable errors, image loading, and soak evidence are monitored", "QUAL-REL", "Crash/reliability tests and soak reports", CombineEvidence(TestPath(repoRoot, "CrashSafetyTests.cs"), SoakEvidencePath(uiSoak), factorySoak is null ? string.Empty : $"SQLite:SoakTestRuns/{factorySoak.Id}"), ReliabilityStatus(uiSoak, factorySoak, repoRoot), StandardsTraceabilityBlockingLevel.ReleaseBlocker, $"UI soak={uiSoak?.Status ?? "none"}; factory soak accepted={factorySoak?.IsCompletedFactoryEvidence.ToString(CultureInfo.InvariantCulture) ?? "false"}.");
        Add(rows, "ISO/IEC 25010-style Quality", "Security/accountability: secrets are redacted, roles are audited, and code-quality scans block risky patterns", "QUAL-SEC", "Audit log and code quality report", CombineEvidence(codeQuality.Path, industrialGate.Path), SecurityStatus(codeQuality, industrialGate, recentAudits), StandardsTraceabilityBlockingLevel.ReleaseBlocker, $"Recent audit rows={recentAudits.Count}; code-quality={codeQuality.Status}; industrialGate={industrialGate.Status}.");
        Add(rows, "ISO/IEC 25010-style Quality", "Maintainability: analyzers, PR gates, contributor checklist, and tests are enforced", "QUAL-MAINT", "Code-quality and PR evidence", CombineEvidence(codeQuality.Path, DocPath(repoRoot, "Contributor_Quality_Checklist.md"), Path.Combine(repoRoot, ".github", "pull_request_template.md")), codeQuality.IsPass && File.Exists(Path.Combine(repoRoot, ".github", "pull_request_template.md")) ? StandardsTraceabilityStatus.Satisfied : StandardsTraceabilityStatus.Partial, StandardsTraceabilityBlockingLevel.Warning, "Release builds use selected warnings-as-errors and script-based pattern checks.");
        Add(rows, "ISO/IEC 25010-style Quality", "Portability: release packaging scripts and deployment docs exist for Windows desktop delivery", "QUAL-PORT", "Publish script and deployment docs", CombineEvidence(Path.Combine(repoRoot, "Scripts", "publish.ps1"), DocPath(repoRoot, "Deployment_Package_Guide.md")), File.Exists(Path.Combine(repoRoot, "Scripts", "publish.ps1")) && File.Exists(DocPath(repoRoot, "Deployment_Package_Guide.md")) ? StandardsTraceabilityStatus.Satisfied : StandardsTraceabilityStatus.Partial, StandardsTraceabilityBlockingLevel.Info, "Portability is scoped to the supported Windows desktop package.");
        Add(rows, "ISO/IEC 25010-style Quality", "Open Critical/High pilot issues are visible and block client readiness where required", "QUAL-ISSUE", "Pilot issue register", "SQLite:PilotIssues", pilotIssues.CriticalOpen == 0 ? StandardsTraceabilityStatus.Satisfied : StandardsTraceabilityStatus.Partial, StandardsTraceabilityBlockingLevel.ReleaseBlocker, $"Open issues={pilotIssues.Open}; criticalOpen={pilotIssues.CriticalOpen}.");
    }

    private static void AddIecAlarmRows(
        ICollection<StandardsTraceabilityMatrix> rows,
        IReadOnlyList<AlarmEvent> alarmEvents,
        IReadOnlyList<AlarmEvent> activeCriticalAlarms,
        IReadOnlyList<AlarmEvent> unacknowledgedAlarms,
        string repoRoot)
    {
        var alarmTests = TestPath(repoRoot, "AlarmEventServiceTests.cs");
        Add(rows, "IEC 62682 / ISA-18.2-style Alarm Discipline", "Every alarm/warning has timestamp, severity, source, message, recommended action, and acknowledgement state", "ALARM-001", "Alarm model tests and active alarm store", CombineEvidence(alarmTests, "SQLite/JSON:AlarmEvents"), TestsExist(repoRoot, "AlarmEventServiceTests.cs") ? StandardsTraceabilityStatus.Satisfied : StandardsTraceabilityStatus.Partial, StandardsTraceabilityBlockingLevel.ReleaseBlocker, $"Alarm events recorded={alarmEvents.Count}.");
        Add(rows, "IEC 62682 / ISA-18.2-style Alarm Discipline", "Severity levels are Info, Warning, Alarm, Critical and are prioritized for operators", "ALARM-002", "Alarm model and panel tests", alarmTests, TestsExist(repoRoot, "AlarmEventServiceTests.cs") ? StandardsTraceabilityStatus.Satisfied : StandardsTraceabilityStatus.Missing, StandardsTraceabilityBlockingLevel.Warning, "Severity naming is project-defined and standards-aligned.");
        Add(rows, "IEC 62682 / ISA-18.2-style Alarm Discipline", "Operator-visible messages do not show raw exception stack traces", "ALARM-003", "Alarm transformation tests", alarmTests, TestsExist(repoRoot, "AlarmEventServiceTests.cs") ? StandardsTraceabilityStatus.Satisfied : StandardsTraceabilityStatus.Missing, StandardsTraceabilityBlockingLevel.ReleaseBlocker, "Engineer/Admin details may include redacted technical detail.");
        Add(rows, "IEC 62682 / ISA-18.2-style Alarm Discipline", "Critical alarms remain visible until acknowledged or resolved", "ALARM-004", "Active alarm state", "SQLite/JSON:AlarmEvents", activeCriticalAlarms.Count == 0 ? StandardsTraceabilityStatus.Satisfied : StandardsTraceabilityStatus.Partial, StandardsTraceabilityBlockingLevel.ReleaseBlocker, activeCriticalAlarms.Count == 0 ? "No active Critical alarms." : $"{activeCriticalAlarms.Count} active Critical alarm(s) require resolution.");
        Add(rows, "IEC 62682 / ISA-18.2-style Alarm Discipline", "Alarm log is exportable with timestamp, severity, source, message, and recommended action", "ALARM-005", "Alarm export service/tests", alarmTests, TestsExist(repoRoot, "AlarmEventServiceTests.cs") ? StandardsTraceabilityStatus.Satisfied : StandardsTraceabilityStatus.Partial, StandardsTraceabilityBlockingLevel.Warning, "Export evidence is created when the alarm log is exported.");
        Add(rows, "IEC 62682 / ISA-18.2-style Alarm Discipline", "Unacknowledged Alarm-level events are visible before release or client demo", "ALARM-006", "Client demo readiness gate", "SQLite/JSON:AlarmEvents", unacknowledgedAlarms.Count == 0 ? StandardsTraceabilityStatus.Satisfied : StandardsTraceabilityStatus.Partial, StandardsTraceabilityBlockingLevel.Warning, $"{unacknowledgedAlarms.Count} unacknowledged Alarm-level event(s).");
    }

    private static void Add(
        ICollection<StandardsTraceabilityMatrix> rows,
        string standardOrSource,
        string principleOrRequirement,
        string projectRequirementId,
        string evidenceType,
        string evidencePath,
        string status,
        string blockingLevel,
        string notes)
    {
        rows.Add(new StandardsTraceabilityMatrix
        {
            StandardOrSource = standardOrSource,
            PrincipleOrRequirement = principleOrRequirement,
            ProjectRequirementId = projectRequirementId,
            EvidenceType = evidenceType,
            EvidencePath = string.IsNullOrWhiteSpace(evidencePath) ? "No evidence path recorded." : evidencePath,
            Status = status,
            BlockingLevel = blockingLevel,
            Notes = notes,
        });
    }

    private static HmiLayoutEvidence ReadHmiLayoutAudit(string? artifactRoot)
    {
        var path = FindLatestArtifact("hmi_layout_audit.json", artifactRoot);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return new HmiLayoutEvidence(string.Empty, false, false, 0, 0, "No hmi_layout_audit.json report found.");

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var passed = document.RootElement.TryGetProperty("passed", out var passedElement) && passedElement.GetBoolean();
            var failCount = 0;
            var issueCount = 0;
            if (document.RootElement.TryGetProperty("issues", out var issuesElement) && issuesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var issue in issuesElement.EnumerateArray())
                {
                    issueCount++;
                    var isFail = issue.TryGetProperty("severity", out var severity) &&
                        severity.GetString()?.Equals("Fail", StringComparison.OrdinalIgnoreCase) == true;
                    var approved = issue.TryGetProperty("approved", out var approvedElement) && approvedElement.GetBoolean();
                    if (isFail && !approved)
                        failCount++;
                }
            }

            return new HmiLayoutEvidence(path, true, passed && failCount == 0, failCount, issueCount, $"issues={issueCount}; unapprovedFails={failCount}.");
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
            return new HmiLayoutEvidence(path, true, false, 1, 1, $"Layout audit could not be parsed: {ex.Message}");
        }
    }

    private static NavigationPerformanceEvidence ReadNavigationPerformance(string? artifactRoot)
    {
        var path = FindLatestArtifact("ui_navigation_performance.json", artifactRoot);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return new NavigationPerformanceEvidence(string.Empty, false, false, 0, "No ui_navigation_performance.json report found.");

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var slowEvents = 0;
            var eventCount = 0;
            if (document.RootElement.TryGetProperty("events", out var eventsElement) && eventsElement.ValueKind == JsonValueKind.Array)
            {
                var visualThreshold = document.RootElement.TryGetProperty("visualResponseThresholdMilliseconds", out var visual)
                    ? visual.GetInt64()
                    : UiPerformanceMonitorService.MenuResponseWarningMilliseconds;
                var cachedThreshold = document.RootElement.TryGetProperty("cachedSwitchThresholdMilliseconds", out var cached)
                    ? cached.GetInt64()
                    : UiPerformanceMonitorService.CachedPageSwitchWarningMilliseconds;
                foreach (var item in eventsElement.EnumerateArray())
                {
                    eventCount++;
                    var category = item.TryGetProperty("category", out var cat) ? cat.GetString() ?? string.Empty : string.Empty;
                    var duration = item.TryGetProperty("durationMilliseconds", out var dur) ? dur.GetInt64() : 0;
                    if (category.Equals("NAVIGATION_VISUAL_RESPONSE", StringComparison.OrdinalIgnoreCase) && duration > visualThreshold)
                        slowEvents++;
                    if (category.Equals("CACHED_PAGE_SWITCH", StringComparison.OrdinalIgnoreCase) && duration > cachedThreshold)
                        slowEvents++;
                }
            }

            return new NavigationPerformanceEvidence(path, true, slowEvents == 0, slowEvents, $"events={eventCount}; slow={slowEvents}.");
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
            return new NavigationPerformanceEvidence(path, true, false, 1, $"Navigation report could not be parsed: {ex.Message}");
        }
    }

    private static SimpleStatusEvidence ReadSimpleStatusReport(string fileName, string? artifactRoot)
    {
        var path = FindLatestArtifact(fileName, artifactRoot);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return new SimpleStatusEvidence(string.Empty, string.Empty, false);

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var status = document.RootElement.TryGetProperty("status", out var statusElement)
                ? statusElement.GetString() ?? string.Empty
                : document.RootElement.TryGetProperty("overallStatus", out var overallElement)
                    ? overallElement.GetString() ?? string.Empty
                    : string.Empty;
            return new SimpleStatusEvidence(path, status, status.Equals("PASS", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
            return new SimpleStatusEvidence(path, $"Unreadable: {ex.Message}", false);
        }
    }

    private static string LayoutStatus(HmiLayoutEvidence layout)
        => !layout.Exists ? StandardsTraceabilityStatus.Missing : layout.Passed ? StandardsTraceabilityStatus.Satisfied : StandardsTraceabilityStatus.Partial;

    private static string LayoutNotes(HmiLayoutEvidence layout)
        => layout.Exists ? layout.Notes : "Run the WPF HMI layout audit on Windows and attach JSON/HTML evidence.";

    private static string NavigationStatus(NavigationPerformanceEvidence navigation)
        => !navigation.Exists ? StandardsTraceabilityStatus.Missing : navigation.Passed ? StandardsTraceabilityStatus.Satisfied : StandardsTraceabilityStatus.Partial;

    private static string NavigationNotes(NavigationPerformanceEvidence navigation)
        => navigation.Exists ? navigation.Notes : "Run the UI navigation performance smoke test.";

    private static string LatencyStatus(InspectionLatencySummary latency)
        => latency.TraceCount == 0
            ? StandardsTraceabilityStatus.Missing
            : latency.OverOneSecondCount == 0 ? StandardsTraceabilityStatus.Satisfied : StandardsTraceabilityStatus.Partial;

    private static string LatencyEvidencePath(InspectionLatencySummary latency, SimpleStatusEvidence codeQuality, SimpleStatusEvidence industrialGate)
        => latency.TraceCount > 0
            ? "SQLite:InspectionLatencyTraces"
            : CombineEvidence(codeQuality.Path, industrialGate.Path);

    private static string FactorySoakStatus(DeploymentProfile profile, SoakTestResult? soak)
    {
        if (profile != DeploymentProfile.FullFactoryAutomation)
            return StandardsTraceabilityStatus.NotApplicable;
        if (soak?.IsCompletedFactoryEvidence == true)
            return StandardsTraceabilityStatus.Satisfied;
        return soak is null ? StandardsTraceabilityStatus.Missing : StandardsTraceabilityStatus.Partial;
    }

    private static string FactorySoakNotes(DeploymentProfile profile, SoakTestResult? soak)
    {
        if (profile != DeploymentProfile.FullFactoryAutomation)
            return "8-hour factory PoC evidence is required separately only for the Full Factory Automation profile.";
        if (soak is null)
            return "No factory soak-test run is recorded.";
        return $"profile={soak.ProfileName}; realCamera={soak.IsRealCameraSource}; duration={soak.ActualDuration.TotalHours:F2} h; factoryAccepted={soak.IsCompletedFactoryEvidence}.";
    }

    private static string ExportEvidenceStatus(IReadOnlyList<ExportVerificationRecord> verifications)
    {
        if (verifications.Count == 0)
            return StandardsTraceabilityStatus.Missing;
        if (verifications.Any(item => item.Status.Equals("ERROR", StringComparison.OrdinalIgnoreCase)))
            return StandardsTraceabilityStatus.Partial;

        var types = ExportExtensions(verifications);
        return types.Contains(".csv") && types.Contains(".png") && types.Contains(".pdf")
            ? StandardsTraceabilityStatus.Satisfied
            : StandardsTraceabilityStatus.Partial;
    }

    private static string ExportEvidencePath(IReadOnlyList<ExportVerificationRecord> verifications)
        => verifications.Count == 0 ? string.Empty : $"SQLite:ExportVerification recent={verifications.Count}";

    private static string ExportEvidenceNotes(IReadOnlyList<ExportVerificationRecord> verifications)
    {
        if (verifications.Count == 0)
            return "No export verification records found.";
        var errors = verifications.Count(item => item.Status.Equals("ERROR", StringComparison.OrdinalIgnoreCase));
        var extensions = string.Join(", ", ExportExtensions(verifications).OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
        return $"records={verifications.Count}; errors={errors}; extensions={extensions}. CSV, PNG, and PDF evidence are expected for client packages when applicable.";
    }

    private static HashSet<string> ExportExtensions(IReadOnlyList<ExportVerificationRecord> verifications)
    {
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in verifications)
        {
            if (!string.IsNullOrWhiteSpace(Path.GetExtension(record.ExportPath)))
                extensions.Add(Path.GetExtension(record.ExportPath));
            try
            {
                var checksums = JsonSerializer.Deserialize<Dictionary<string, string>>(record.ArtifactChecksumsJson) ?? new();
                foreach (var key in checksums.Keys)
                {
                    var extension = Path.GetExtension(key);
                    if (!string.IsNullOrWhiteSpace(extension))
                        extensions.Add(extension);
                }
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                System.Diagnostics.Trace.WriteLine($"Standards traceability export checksum parse skipped: {ex.Message}");
            }
        }

        return extensions;
    }

    private static string HardwareStatus(DeploymentProfile profile, CameraAcceptanceRun? realCamera, CameraAcceptanceRun? anyCamera, LightingAcceptanceRun? lighting, Profile3DAcceptanceRun? profile3D, RobotAcceptanceRun? robot)
    {
        var fullRequired = profile == DeploymentProfile.FullFactoryAutomation;
        var stage2Plus = profile is DeploymentProfile.Stage2CameraPilot or DeploymentProfile.Stage3RobotPilot or DeploymentProfile.Stage4MesPilot or DeploymentProfile.FullFactoryAutomation;
        var stage3Plus = profile is DeploymentProfile.Stage3RobotPilot or DeploymentProfile.Stage4MesPilot or DeploymentProfile.FullFactoryAutomation;
        var realCameraOk = realCamera?.FactoryReadinessStatus is "PASS" or "WARN";
        var lightingOk = lighting?.Status is "PASS" or "WARN";
        var profile3DOk = profile3D?.Status is "PASS" or "WARN";
        var robotOk = robot?.Status == "PASS";
        if (fullRequired)
            return realCameraOk && lighting?.IsSimulated == false && profile3D?.IsSimulated == false && robot?.SourceKind == "Real"
                ? StandardsTraceabilityStatus.Satisfied
                : StandardsTraceabilityStatus.Missing;
        if (stage3Plus)
            return realCameraOk && lightingOk && profile3DOk && robotOk ? StandardsTraceabilityStatus.Satisfied : StandardsTraceabilityStatus.Partial;
        if (stage2Plus)
            return (realCameraOk || anyCamera is not null) && lightingOk && profile3DOk ? StandardsTraceabilityStatus.Satisfied : StandardsTraceabilityStatus.Partial;
        return anyCamera is null && lighting is null && profile3D is null && robot is null
            ? StandardsTraceabilityStatus.NotApplicable
            : StandardsTraceabilityStatus.Partial;
    }

    private static string HardwareEvidencePath(CameraAcceptanceRun? realCamera, CameraAcceptanceRun? anyCamera, LightingAcceptanceRun? lighting, Profile3DAcceptanceRun? profile3D, RobotAcceptanceRun? robot)
        => CombineEvidence(
            realCamera is null ? (anyCamera is null ? string.Empty : $"SQLite:CameraAcceptanceRuns/{anyCamera.Id}") : $"SQLite:CameraAcceptanceRuns/{realCamera.Id}",
            lighting is null ? string.Empty : $"SQLite:LightingAcceptanceRuns/{lighting.Id}",
            profile3D is null ? string.Empty : $"SQLite:Profile3DAcceptanceRuns/{profile3D.Id}",
            robot is null ? string.Empty : $"SQLite:RobotAcceptanceRuns/{robot.Id}");

    private static string HardwareNotes(DeploymentProfile profile, CameraAcceptanceRun? realCamera, CameraAcceptanceRun? anyCamera, LightingAcceptanceRun? lighting, Profile3DAcceptanceRun? profile3D, RobotAcceptanceRun? robot)
        => $"profile={profile}; realCamera={realCamera is not null}; anyCamera={anyCamera is not null}; lightingSimulated={lighting?.IsSimulated.ToString(CultureInfo.InvariantCulture) ?? "none"}; profile3DSimulated={profile3D?.IsSimulated.ToString(CultureInfo.InvariantCulture) ?? "none"}; robotSource={robot?.SourceKind ?? "none"}.";

    private static string MesCentralStatus(DeploymentProfile profile, MesReadinessSummary mes, CentralSyncReadinessSummary centralSync)
    {
        var required = profile is DeploymentProfile.Stage4MesPilot or DeploymentProfile.FullFactoryAutomation;
        var mesOk = mes.Status.Equals("MES REST Ready", StringComparison.OrdinalIgnoreCase);
        var centralOk = centralSync.Status.Equals("Central Sync Ready", StringComparison.OrdinalIgnoreCase) ||
            (!required && centralSync.Status.Equals("Central Sync Disabled", StringComparison.OrdinalIgnoreCase));
        if (mesOk && centralOk)
            return StandardsTraceabilityStatus.Satisfied;
        return required ? StandardsTraceabilityStatus.Missing : StandardsTraceabilityStatus.Partial;
    }

    private static string BuildAndCodeQualityStatus(BuildTestEvidenceSummary buildSummary, SimpleStatusEvidence codeQuality, SimpleStatusEvidence industrialGate)
    {
        if (buildSummary.IsPassing && (codeQuality.IsPass || industrialGate.IsPass))
            return StandardsTraceabilityStatus.Satisfied;
        if (codeQuality.IsPass || industrialGate.IsPass || buildSummary.Latest is not null)
            return StandardsTraceabilityStatus.Partial;
        return StandardsTraceabilityStatus.Missing;
    }

    private static string BuildEvidencePath(BuildTestEvidenceSummary buildSummary)
        => buildSummary.Latest?.EvidencePath ?? string.Empty;

    private static string BuildAndCodeQualityNotes(BuildTestEvidenceSummary buildSummary, SimpleStatusEvidence codeQuality, SimpleStatusEvidence industrialGate)
        => $"buildEvidence={buildSummary.Status}; codeQuality={codeQuality.Status}; industrialGate={industrialGate.Status}.";

    private static string CompatibilityStatus(CameraAcceptanceRun? realCamera, CameraAcceptanceRun? anyCamera, LightingAcceptanceRun? lighting, Profile3DAcceptanceRun? profile3D, RobotAcceptanceRun? robot, MesReadinessSummary mes, CentralSyncReadinessSummary centralSync)
        => realCamera is not null && lighting is not null && profile3D is not null && robot is not null && mes.Status.Contains("Ready", StringComparison.OrdinalIgnoreCase) && centralSync.Status.Contains("Ready", StringComparison.OrdinalIgnoreCase)
            ? StandardsTraceabilityStatus.Satisfied
            : anyCamera is not null || lighting is not null || profile3D is not null || robot is not null || !mes.Status.Contains("Not Configured", StringComparison.OrdinalIgnoreCase)
                ? StandardsTraceabilityStatus.Partial
                : StandardsTraceabilityStatus.Missing;

    private static string ReliabilityStatus(UiNavigationSoakResult? uiSoak, SoakTestResult? factorySoak, string repoRoot)
        => UiNavigationSoakTestService.IsThirtyMinuteOrBetterPass(uiSoak) || factorySoak?.IsCompletedFactoryEvidence == true
            ? StandardsTraceabilityStatus.Satisfied
            : TestsExist(repoRoot, "CrashSafetyTests.cs", "ImageMemoryDiagnosticsTests.cs", "UiNavigationSoakTestServiceTests.cs")
                ? StandardsTraceabilityStatus.Partial
                : StandardsTraceabilityStatus.Missing;

    private static string SecurityStatus(SimpleStatusEvidence codeQuality, SimpleStatusEvidence industrialGate, IReadOnlyList<AuditEventRecord> recentAudits)
        => (codeQuality.IsPass || industrialGate.IsPass) && recentAudits.Count > 0
            ? StandardsTraceabilityStatus.Satisfied
            : codeQuality.IsPass || industrialGate.IsPass || recentAudits.Count > 0
                ? StandardsTraceabilityStatus.Partial
                : StandardsTraceabilityStatus.Missing;

    private static string SoakEvidencePath(UiNavigationSoakResult? uiSoak)
        => string.IsNullOrWhiteSpace(uiSoak?.JsonPath) ? string.Empty : uiSoak.JsonPath;

    private static bool TestsExist(string repoRoot, params string[] testFileNames)
        => testFileNames.All(name => File.Exists(TestPath(repoRoot, name)));

    private static string TestPath(string repoRoot, string fileName)
        => Path.Combine(repoRoot, "AOI_Monitor.Tests", fileName);

    private static string DocPath(string repoRoot, string fileName)
        => Path.Combine(repoRoot, "Docs", fileName);

    private static string CombineEvidence(params string[] values)
    {
        var parts = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return parts.Length == 0 ? string.Empty : string.Join(" | ", parts);
    }

    private static bool IsSatisfied(StandardsTraceabilityMatrix item)
        => item.Status.Equals(StandardsTraceabilityStatus.Satisfied, StringComparison.OrdinalIgnoreCase);

    private static bool IsPartial(StandardsTraceabilityMatrix item)
        => item.Status.Equals(StandardsTraceabilityStatus.Partial, StringComparison.OrdinalIgnoreCase);

    private static bool IsMissing(StandardsTraceabilityMatrix item)
        => item.Status.Equals(StandardsTraceabilityStatus.Missing, StringComparison.OrdinalIgnoreCase);

    private static bool IsNotApplicable(StandardsTraceabilityMatrix item)
        => item.Status.Equals(StandardsTraceabilityStatus.NotApplicable, StringComparison.OrdinalIgnoreCase);

    private static bool IsMissingReleaseBlocker(StandardsTraceabilityMatrix item)
        => IsMissing(item) && item.BlockingLevel.Equals(StandardsTraceabilityBlockingLevel.ReleaseBlocker, StringComparison.OrdinalIgnoreCase);

    private static string? FindLatestArtifact(string fileName, string? artifactRoot)
    {
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(artifactRoot))
        {
            roots.Add(artifactRoot.Trim());
        }
        else
        {
            var repoRoot = FindRepositoryRoot();
            roots.Add(Path.Combine(repoRoot, "TestResults"));
            roots.Add(Path.Combine(AppContext.BaseDirectory, "TestResults"));
            roots.Add(Path.Combine(AoiDatabase.StorageRoot, "exports"));
        }

        return roots
            .Where(root => !string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
            .SelectMany(root => SafeEnumerate(root, fileName))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static IEnumerable<string> SafeEnumerate(string root, string fileName)
    {
        try
        {
            return Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.WriteLine($"Standards traceability artifact scan skipped for {root}: {ex.Message}");
            return Array.Empty<string>();
        }
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

        current = new DirectoryInfo(Environment.CurrentDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AOI_PCB_Database.slnx")))
                return current.FullName;
            current = current.Parent;
        }

        return AppContext.BaseDirectory;
    }

    private static string EnsureUniqueFolder(string path)
    {
        if (!Directory.Exists(path))
            return path;
        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{path}_{i:D2}";
            if (!Directory.Exists(candidate))
                return candidate;
        }

        return $"{path}_{Guid.NewGuid():N}";
    }

    private static string Html(string value)
        => System.Net.WebUtility.HtmlEncode(value ?? string.Empty);

    private static string Css(string status)
        => status.Replace(" ", string.Empty, StringComparison.Ordinal);

    private sealed record HmiLayoutEvidence(string Path, bool Exists, bool Passed, int UnapprovedFailCount, int IssueCount, string Notes);
    private sealed record NavigationPerformanceEvidence(string Path, bool Exists, bool Passed, int SlowEventCount, string Notes);
    private sealed record SimpleStatusEvidence(string Path, string Status, bool IsPass);
}
