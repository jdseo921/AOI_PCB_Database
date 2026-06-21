using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using AOI_Monitor.Data;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public sealed class FactoryAcceptanceChecklist
{
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
    public DeploymentProfile DeploymentProfile { get; set; } = DeploymentProfile.Stage1ImageValidation;
    public string ProfileDisplayName { get; set; } = string.Empty;
    public List<FactoryAcceptanceChecklistItem> Items { get; set; } = new();
    public List<string> Limitations { get; set; } = new();
}

public sealed class FactoryAcceptanceChecklistItem
{
    public string RequirementId { get; set; } = string.Empty;
    public string RequirementText { get; set; } = string.Empty;
    public string RequiredEvidence { get; set; } = string.Empty;
    public string CurrentEvidencePath { get; set; } = string.Empty;
    public string Status { get; set; } = "Open";
    public string SignoffRole { get; set; } = string.Empty;
    public string SignedBy { get; set; } = string.Empty;
    public DateTime? SignedAtUtc { get; set; }
    public string OperatorSignoff { get; set; } = string.Empty;
    public string EngineerSignoff { get; set; } = string.Empty;
    public string AdminSignoff { get; set; } = string.Empty;
    public DateTime? TimestampUtc { get; set; }
    public string Notes { get; set; } = string.Empty;

    public string Requirement
    {
        get => RequirementText;
        set => RequirementText = value;
    }

    public string ExpectedEvidence
    {
        get => RequiredEvidence;
        set => RequiredEvidence = value;
    }
}

public sealed record FactoryAcceptanceChecklistExport(
    string Folder,
    string HtmlPath,
    string JsonPath,
    string CsvPath,
    string PdfPath);

public static class FactoryAcceptanceChecklistService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static FactoryAcceptanceChecklist Generate(DeploymentProfile profile)
    {
        AoiDatabase.Initialize();
        var checklist = new FactoryAcceptanceChecklist
        {
            GeneratedAtUtc = DateTime.UtcNow,
            DeploymentProfile = profile,
            ProfileDisplayName = FactoryReadinessService.DisplayName(profile),
            Limitations =
            {
                "Manual signoff fields are intentionally blank until completed by authorized customer/factory reviewers.",
                "Simulation evidence supports workflow review only and must not be described as real production equipment validation.",
                "Local SQLite remains the offline source of truth unless a separately accepted central sync/MES integration is validated.",
            },
        };

        var readiness = FactoryReadinessService.Evaluate(FactoryReadinessService.CriteriaForProfile(profile));
        var latestValidation = AoiDatabase.GetValidationPackages(1).FirstOrDefault();
        var latestBuild = AoiDatabase.GetLatestBuildTestEvidence();
        var latestExportVerification = AoiDatabase.GetExportVerifications(1).FirstOrDefault();
        var latestCamera = AoiDatabase.GetLatestCameraAcceptanceRun(realHardwareOnly: false);
        var latestProfile3D = AoiDatabase.GetLatestProfile3DAcceptanceRun();
        var latestLighting = AoiDatabase.GetLatestLightingAcceptanceRun();
        var latestRobot = AoiDatabase.GetLatestRobotAcceptanceRun();
        var latestModelAcceptance = AoiDatabase.GetLatestModelAcceptanceRun();
        var latestTraceability = AoiDatabase.GetLatestTraceabilityTestReport();
        var latestSoak = AoiDatabase.GetLatestSoakTestRun();
        var mesReadiness = MesSpoolService.EvaluateReadiness();
        var centralSync = CentralSyncService.EvaluateReadiness();

        Add(checklist, "GUI-001", "Operator GUI inspection flow can be demonstrated end to end.", "Inspection/review/audit logs and readiness summary.", "FactoryReadiness", FindCategory(readiness, "Database health")?.Evidence ?? string.Empty, required: true, signoffRole: "Operator");
        Add(checklist, "TR-010", "Inspection visualization and processing evidence tracks the one-second target.", "Validation performance summary or soak-test timing report.", latestSoak is null ? Evidence(latestValidation?.ManifestPath) : $"SQLite:SoakTestRuns/{latestSoak.Id}", latestSoak is null ? latestValidation?.Summary ?? string.Empty : $"p95={latestSoak.P95InspectionMilliseconds:F0} ms; over1s={latestSoak.CountOverOneSecond}.", required: true, signoffRole: "Engineer");
        Add(checklist, "BT-001", "Exact local build/test validation evidence is available.", "Build/test evidence JSON imported into the app.", Evidence(latestBuild?.EvidencePath), latestBuild is null ? "No build/test evidence imported." : $"build={latestBuild.BuildStatus}; test={latestBuild.TestStatus}; publish={latestBuild.PublishValidationStatus}.", required: true, signoffRole: "Admin");
        Add(checklist, "S1-DATA-001", "Dataset quality is sufficient for the selected Stage 1 acceptance claim.", "Dataset quality summary with known labels, OK/NG balance, defect-class coverage, and limitations.", Evidence(latestValidation?.ManifestPath), latestValidation is null ? "No validation manifest with dataset quality evidence." : $"acceptance={latestValidation.AcceptanceStatus}; see manifest for dataset-quality details.", required: true, signoffRole: "Engineer");
        Add(checklist, "S1-MODEL-001", "Active model acceptance or prototype limitation is documented.", "Production model acceptance run, or explicit prototype-engine limitation for Stage 1 only.", latestModelAcceptance is null ? Evidence(latestValidation?.ManifestPath) : $"SQLite:ModelAcceptanceRuns/{latestModelAcceptance.Id}", latestModelAcceptance is null ? "No formal model acceptance run; Stage 1 may proceed only with an explicit prototype/customer-dataset limitation." : $"status={latestModelAcceptance.Status}; productionCandidate={latestModelAcceptance.IsProductionCandidate}; model={latestModelAcceptance.ModelId}.", required: true, signoffRole: "Engineer", evidenceAcceptable: latestModelAcceptance is null ? null : latestModelAcceptance.Status.Equals("PASS", StringComparison.OrdinalIgnoreCase));
        Add(checklist, "S1-001", "Stage 1 customer data validation package is available.", "validation_manifest.json, HTML report, CSV exports, dataset quality, limitations.", Evidence(latestValidation?.ManifestPath), latestValidation?.Summary ?? "No validation package recorded.", required: true, signoffRole: "Engineer");
        Add(checklist, "S1-002", "Report/export verification evidence is available.", "Export verification checksum/status records.", Evidence(latestExportVerification?.ExportPath), latestExportVerification is null ? "No export verification record." : $"status={latestExportVerification.Status}; sha256={latestExportVerification.Sha256}.", required: true, signoffRole: "Engineer");

        var stage2 = profile is DeploymentProfile.Stage2CameraPilot or DeploymentProfile.Stage3RobotPilot or DeploymentProfile.Stage4MesPilot or DeploymentProfile.FullFactoryAutomation;
        Add(checklist, "S2-003", "Stage 2 live inspection demo evidence is available for the selected camera source.", "Live/folder camera demo run with source kind clearly labeled.", latestCamera is null ? string.Empty : $"SQLite:CameraAcceptanceRuns/{latestCamera.Id}", latestCamera is null ? "No live camera or simulation acceptance evidence." : $"status={latestCamera.Status}; realHardware={latestCamera.IsRealHardware}; source={latestCamera.SourceKey}.", stage2, signoffRole: "Engineer");
        Add(checklist, "S2-CAM-001", "Stage 2 camera acceptance is complete for required views.", "Camera acceptance JSON/HTML or SQLite run record with source kind and frame metadata.", latestCamera is null ? string.Empty : $"SQLite:CameraAcceptanceRuns/{latestCamera.Id}", latestCamera is null ? "No camera acceptance run." : $"status={latestCamera.Status}; realHardware={latestCamera.IsRealHardware}; source={latestCamera.SourceKey}.", stage2, signoffRole: "Engineer", evidenceAcceptable: !stage2 ? null : latestCamera?.Status.Equals("PASS", StringComparison.OrdinalIgnoreCase) == true);
        Add(checklist, "S2-LIGHT-001", "Stage 2 lighting/trigger synchronization acceptance is complete.", "Lighting sync acceptance report with per-view timing.", latestLighting is null ? string.Empty : $"SQLite:LightingAcceptanceRuns/{latestLighting.Id}", latestLighting is null ? "No lighting acceptance run." : $"status={latestLighting.Status}; simulated={latestLighting.IsSimulated}.", stage2, signoffRole: "Engineer", evidenceAcceptable: !stage2 ? null : latestLighting?.Status.Equals("PASS", StringComparison.OrdinalIgnoreCase) == true);
        Add(checklist, "S2-007", "3D profile acceptance is complete when 3D inspection is enabled.", "3D profile acceptance JSON/HTML with source kind, dimensions, unit, pitch, and invalid-height checks.", latestProfile3D is null ? string.Empty : $"SQLite:Profile3DAcceptanceRuns/{latestProfile3D.Id}", latestProfile3D is null ? "No 3D profile acceptance run. Waive only when 3D inspection is out of scope." : $"status={latestProfile3D.Status}; source={latestProfile3D.SourceKind}; simulated={latestProfile3D.IsSimulated}; readiness={latestProfile3D.FactoryReadinessStatus}.", stage2, signoffRole: "Engineer");

        var stage3 = profile is DeploymentProfile.Stage3RobotPilot or DeploymentProfile.Stage4MesPilot or DeploymentProfile.FullFactoryAutomation;
        Add(checklist, "S3-001", "Stage 3 robot cycle acceptance is complete.", "Robot load, inspect, unload, invalid-transition, reset, and timing evidence.", latestRobot is null ? string.Empty : $"SQLite:RobotAcceptanceRuns/{latestRobot.Id}", latestRobot is null ? "No robot acceptance run." : $"status={latestRobot.Status}; source={latestRobot.SourceKind}; fullCycleMs={latestRobot.FullCycleMs:F0}.", stage3, signoffRole: "Engineer");
        Add(checklist, "S3-005", "PLC/safety interlock evidence is recorded separately from robot movement commands.", "Safety controller source, guard/clamp/e-stop blocking evidence, and reset result.", latestRobot is null ? string.Empty : $"SQLite:RobotAcceptanceRuns/{latestRobot.Id}", latestRobot is null ? "No PLC/safety acceptance evidence." : $"safetySource={latestRobot.SafetySourceKind}; eStopBlocked={latestRobot.EmergencyStopBlocked}; safetyFaultBlocked={latestRobot.SafetyFaultBlocked}; resetIdle={latestRobot.ResetReturnedIdle}.", stage3, signoffRole: "Engineer", evidenceAcceptable: !stage3 ? null : latestRobot?.EmergencyStopBlocked == true && latestRobot.SafetyFaultBlocked);
        Add(checklist, "S3-ROBOT-001", "Stage 3 robot cell cycle and interlock acceptance is complete.", "Robot acceptance report with invalid transition, e-stop, reset, and safety-interlock evidence.", latestRobot is null ? string.Empty : $"SQLite:RobotAcceptanceRuns/{latestRobot.Id}", latestRobot is null ? "No robot acceptance run." : $"status={latestRobot.Status}; source={latestRobot.SourceKind}; safety={latestRobot.SafetySourceKind}.", stage3, signoffRole: "Engineer", evidenceAcceptable: !stage3 ? null : latestRobot?.Status.Equals("PASS", StringComparison.OrdinalIgnoreCase) == true);

        var stage4 = profile is DeploymentProfile.Stage4MesPilot or DeploymentProfile.FullFactoryAutomation;
        Add(checklist, "S4-001", "Stage 4 traceability acceptance test is complete.", "Traceability acceptance JSON/HTML with result payload status and optional confirmed image payload status.", Evidence(latestTraceability?.ReportJsonPath), latestTraceability is null ? "No MES traceability signoff report." : $"status={latestTraceability.Status}; mode={latestTraceability.Mode}.", stage4, signoffRole: "Engineer", evidenceAcceptable: !stage4 ? null : latestTraceability?.Status.Equals("PASS", StringComparison.OrdinalIgnoreCase) == true);
        Add(checklist, "S4-004", "MES spool queue has no failed production-review items.", "MES queue report showing pending/failed/sent/abandoned counts.", "SQLite:MesSpoolQueue", $"mode={mesReadiness.Mode}; pending={mesReadiness.PendingCount}; oldPending={mesReadiness.OldPendingCount}; failed={mesReadiness.FailedCount}; sent={mesReadiness.SentCount}; status={mesReadiness.Status}.", stage4, signoffRole: "Admin", evidenceAcceptable: !stage4 ? null : mesReadiness.FailedCount == 0);
        Add(checklist, "S4-005", "MES authentication or station identity check is documented where available.", "MES settings/readiness evidence with mock vs REST mode clearly separated and secrets redacted.", "SQLite:MesIntegrationSettings", $"mode={mesReadiness.Mode}; status={mesReadiness.Status}; mock MES is not production identity validation.", stage4, signoffRole: "Admin");
        Add(checklist, "S4-MES-001", "Stage 4 MES traceability and spool visibility acceptance is complete.", "MES traceability test report and queue/readiness report.", Evidence(latestTraceability?.ReportJsonPath), latestTraceability is null ? "No MES traceability signoff report." : $"status={latestTraceability.Status}; mode={latestTraceability.Mode}.", stage4, signoffRole: "Admin", evidenceAcceptable: !stage4 ? null : latestTraceability?.Status.Equals("PASS", StringComparison.OrdinalIgnoreCase) == true && mesReadiness.FailedCount == 0);

        var full = profile == DeploymentProfile.FullFactoryAutomation;
        var soakNotes = latestSoak is null
            ? "No soak-test run."
            : $"profile={latestSoak.ProfileName}; duration={latestSoak.ActualDuration.TotalHours:F2}h; canceled={latestSoak.WasCanceled}; failures={latestSoak.FailedCycles}; realCamera={latestSoak.IsRealCameraSource}; factoryAccepted={latestSoak.IsCompletedFactoryEvidence}.";
        Add(checklist, "TR-009", "Full factory automation has completed 8-hour stability evidence.", "Completed non-canceled 8-hour Factory PoC soak report with real camera source and no critical errors.", latestSoak is null ? string.Empty : $"SQLite:SoakTestRuns/{latestSoak.Id}", soakNotes, full, signoffRole: "Engineer", evidenceAcceptable: !full ? null : latestSoak?.IsCompletedFactoryEvidence == true);
        Add(checklist, "CENTRAL-SYNC-001", "Central synchronization is ready for multi-station management review.", "Central sync readiness report or explicit waiver when profile does not require management aggregation.", "SQLite:CentralSyncQueue", $"status={centralSync.Status}; mode={centralSync.Mode}; pending={centralSync.PendingCount}; failed={centralSync.FailedCount}; sent={centralSync.SentCount}.", full, signoffRole: "Admin", evidenceAcceptable: !full ? null : centralSync.Status != "Central Sync Error");
        Add(checklist, "FA-001", "Final Go/No-Go readiness package is available for the selected deployment profile.", "Factory readiness summary HTML/JSON and package manifest.", "Generated by this export package.", $"Readiness status={readiness.OverallStatus}; profile={profile}.", required: true, signoffRole: "Admin", evidenceAcceptable: readiness.OverallStatus != FactoryReadinessOverallStatus.NoGo.ToString());

        return checklist;
    }

    public static FactoryAcceptanceChecklistExport Export(DeploymentProfile profile, string? outputRoot = null)
    {
        var checklist = Generate(profile);
        var root = string.IsNullOrWhiteSpace(outputRoot)
            ? Path.Combine(AoiDatabase.StorageRoot, "exports", "factory_acceptance")
            : outputRoot;
        var folder = Path.Combine(root, $"factory_acceptance_{profile}_{DateTime.UtcNow:yyyyMMdd_HHmmss}");
        Directory.CreateDirectory(folder);

        var jsonPath = Path.Combine(folder, "factory_acceptance_checklist.json");
        var htmlPath = Path.Combine(folder, "factory_acceptance_checklist.html");
        var csvPath = Path.Combine(folder, "factory_acceptance_checklist.csv");
        var pdfPath = Path.Combine(folder, "factory_acceptance_checklist.pdf");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(checklist, JsonOptions), Encoding.UTF8);
        File.WriteAllText(htmlPath, BuildHtml(checklist), Encoding.UTF8);
        File.WriteAllText(csvPath, BuildCsv(checklist), Encoding.UTF8);
        PdfExportService.ExportHtmlFileToPdf(htmlPath, pdfPath, "Factory Acceptance Checklist");

        ExportVerificationService.RecordVerifiedExport("FactoryAcceptanceChecklistJson", jsonPath);
        ExportVerificationService.RecordVerifiedExport("FactoryAcceptanceChecklistHtml", htmlPath);
        ExportVerificationService.RecordVerifiedExport("FactoryAcceptanceChecklistCsv", csvPath);
        ExportVerificationService.RecordVerifiedExport("FactoryAcceptanceChecklistPdf", pdfPath);
        AoiDatabase.RecordAuditEvent("FACTORY_ACCEPTANCE_EXPORT", $"Factory acceptance checklist exported for {profile}: {Path.GetFileName(folder)}.", relatedPath: folder);
        return new FactoryAcceptanceChecklistExport(folder, htmlPath, jsonPath, csvPath, pdfPath);
    }

    public static FactoryAcceptanceChecklistExport ExportToFolder(DeploymentProfile profile, string folder)
    {
        Directory.CreateDirectory(folder);
        var checklist = Generate(profile);
        var jsonPath = Path.Combine(folder, "factory_acceptance_checklist.json");
        var htmlPath = Path.Combine(folder, "factory_acceptance_checklist.html");
        var csvPath = Path.Combine(folder, "factory_acceptance_checklist.csv");
        var pdfPath = Path.Combine(folder, "factory_acceptance_checklist.pdf");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(checklist, JsonOptions), Encoding.UTF8);
        File.WriteAllText(htmlPath, BuildHtml(checklist), Encoding.UTF8);
        File.WriteAllText(csvPath, BuildCsv(checklist), Encoding.UTF8);
        PdfExportService.ExportHtmlFileToPdf(htmlPath, pdfPath, "Factory Acceptance Checklist");
        return new FactoryAcceptanceChecklistExport(folder, htmlPath, jsonPath, csvPath, pdfPath);
    }

    private static void Add(
        FactoryAcceptanceChecklist checklist,
        string id,
        string requirement,
        string expected,
        string evidencePath,
        string notes,
        bool required,
        string signoffRole = "Engineer",
        bool? evidenceAcceptable = null)
    {
        var hasEvidence = !string.IsNullOrWhiteSpace(evidencePath);
        var status = !required
            ? "Waived"
            : !hasEvidence
                ? "Open"
            : evidenceAcceptable == false
                ? "Failed"
                : evidenceAcceptable == true ? "Passed" : "EvidencePresent";
        checklist.Items.Add(new FactoryAcceptanceChecklistItem
        {
            RequirementId = id,
            RequirementText = requirement,
            RequiredEvidence = expected,
            CurrentEvidencePath = evidencePath,
            Status = status,
            SignoffRole = signoffRole,
            TimestampUtc = hasEvidence ? checklist.GeneratedAtUtc : null,
            Notes = notes,
        });
    }

    private static FactoryReadinessCategory? FindCategory(FactoryReadinessReport report, string name)
        => report.Categories.FirstOrDefault(category => category.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string Evidence(string? path)
        => string.IsNullOrWhiteSpace(path) ? string.Empty : path;

    private static string BuildHtml(FactoryAcceptanceChecklist checklist)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html><html><head><meta charset=\"utf-8\"><title>Factory Acceptance Checklist</title>");
        sb.AppendLine("<style>body{font-family:Segoe UI,Arial,sans-serif;margin:24px;color:#17212b}table{border-collapse:collapse;width:100%}td,th{border:1px solid #cfd8df;padding:7px;vertical-align:top;text-align:left}th{background:#eef3f7}.notice{background:#fff8e8;border-left:5px solid #d9951b;padding:12px;margin:12px 0}</style></head><body>");
        sb.AppendLine($"<h1>Factory Acceptance Checklist</h1><p><strong>Profile:</strong> {Html(checklist.ProfileDisplayName)}<br><strong>Generated UTC:</strong> {checklist.GeneratedAtUtc:O}</p>");
        sb.AppendLine("<div class=\"notice\">Manual acceptance fields are blank until signed by authorized reviewers. Simulated evidence is not production hardware validation.</div>");
        sb.AppendLine("<table><tr><th>ID</th><th>Requirement</th><th>Required Evidence</th><th>Current Evidence</th><th>Status</th><th>Signoff Role</th><th>Signed By</th><th>Signed UTC</th><th>Timestamp</th><th>Notes</th></tr>");
        foreach (var item in checklist.Items)
            sb.AppendLine($"<tr><td>{Html(item.RequirementId)}</td><td>{Html(item.RequirementText)}</td><td>{Html(item.RequiredEvidence)}</td><td>{Html(item.CurrentEvidencePath)}</td><td>{Html(item.Status)}</td><td>{Html(item.SignoffRole)}</td><td>{Html(item.SignedBy)}</td><td>{item.SignedAtUtc:O}</td><td>{item.TimestampUtc:O}</td><td>{Html(item.Notes)}</td></tr>");
        sb.AppendLine("</table><h2>Limitations</h2><ul>");
        foreach (var limitation in checklist.Limitations)
            sb.AppendLine($"<li>{Html(limitation)}</li>");
        sb.AppendLine("</ul></body></html>");
        return sb.ToString();
    }

    private static string BuildCsv(FactoryAcceptanceChecklist checklist)
    {
        var sb = new StringBuilder();
        sb.AppendLine("requirement_id,requirement_text,required_evidence,current_evidence_path,status,signoff_role,signed_by,signed_at_utc,timestamp_utc,notes");
        foreach (var item in checklist.Items)
        {
            sb.AppendLine(string.Join(",", new[]
            {
                Csv(item.RequirementId),
                Csv(item.RequirementText),
                Csv(item.RequiredEvidence),
                Csv(item.CurrentEvidencePath),
                Csv(item.Status),
                Csv(item.SignoffRole),
                Csv(item.SignedBy),
                Csv(item.SignedAtUtc?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty),
                Csv(item.TimestampUtc?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty),
                Csv(item.Notes),
            }));
        }

        return sb.ToString();
    }

    private static string Csv(string value)
        => $"\"{(value ?? string.Empty).Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static string Html(string? value)
        => System.Net.WebUtility.HtmlEncode(value ?? string.Empty);
}
