using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using AOI_Monitor.Data;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public static class FactoryReadinessService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static FactoryReadinessReport Evaluate(FactoryReadinessCriteria? criteria = null)
    {
        criteria ??= CriteriaForProfile(DeploymentProfileSettingsService.Load());
        AoiDatabase.Initialize();

        var report = new FactoryReadinessReport
        {
            GeneratedAtUtc = DateTime.UtcNow,
            DeploymentProfile = criteria.DeploymentProfile.ToString(),
            Scope = ScopeFor(criteria.DeploymentProfile),
            KnownLimitations = CustomerValidationReportContext.DefaultPrototypeLimitations.ToList(),
        };

        var diagnostics = SystemDiagnosticService.RunDiagnostics();
        var latestPackage = AoiDatabase.GetValidationPackages(1).FirstOrDefault();
        var latestRun = AoiDatabase.GetLatestBatchTestRun();
        var latestFalseCall = AoiDatabase.GetLatestFalseCallReductionRun();
        var latestVerifications = AoiDatabase.GetExportVerifications(100);
        var camera = AoiDatabase.GetLatestCameraAcceptanceRun(realHardwareOnly: false);
        var lighting = AoiDatabase.GetLatestLightingAcceptanceRun();
        var robot = AoiDatabase.GetLatestRobotAcceptanceRun();
        var mes = MesSpoolService.EvaluateReadiness(new MesReadinessCriteria
        {
            FailOnPendingQueue = criteria.RequireNoPendingMesQueueForProductionMode && !criteria.Stage1Only,
            FailOnFailedQueue = true,
        });

        AddBuildTest(report);
        AddDiagnosticCategory(report, "Database health", diagnostics, "Database");
        AddImageVault(report, diagnostics);
        AddModelReadiness(report, criteria);
        AddValidationPackage(report, criteria, latestPackage, latestRun);
        AddFalseCall(report, criteria, latestFalseCall);
        AddDatasetQuality(report, criteria, latestPackage);
        AddExportVerification(report, criteria, latestVerifications);
        AddSoakTest(report, criteria);
        AddCamera(report, criteria, camera);
        AddLighting(report, criteria, lighting);
        AddRobot(report, criteria, robot);
        AddMes(report, criteria, mes);
        AddSecurityAudit(report);
        AddKnownLimitations(report, criteria);

        report.BlockingIssues.AddRange(report.Categories
            .Where(category => category.Status == "No-Go")
            .Select(category => $"{category.Name}: {category.Evidence}"));
        report.Warnings.AddRange(report.Categories
            .Where(category => category.Status == "Conditional")
            .Select(category => $"{category.Name}: {category.Evidence}"));
        report.UnmetCriteria.AddRange(report.BlockingIssues.Concat(report.Warnings));
        report.RecommendedNextActions.AddRange(report.Categories
            .Where(category => category.Status != "Go" && !string.IsNullOrWhiteSpace(category.NextAction))
            .Select(category => $"{category.Name}: {category.NextAction}")
            .Distinct(StringComparer.OrdinalIgnoreCase));

        report.OverallStatus = report.BlockingIssues.Count > 0
            ? FactoryReadinessOverallStatus.NoGo.ToString()
            : report.Warnings.Count > 0
                ? FactoryReadinessOverallStatus.Conditional.ToString()
                : FactoryReadinessOverallStatus.Go.ToString();
        return report;
    }

    public static FactoryReadinessCriteria CriteriaForProfile(DeploymentProfile profile)
        => profile switch
        {
            DeploymentProfile.Stage1ImageValidation => new FactoryReadinessCriteria
            {
                DeploymentProfile = profile,
                Stage1Only = true,
                RequireSuccessfulLatestValidationPackage = true,
                RequireProductionModel = false,
                RequireFalseCallEvidence = false,
                RequireNoExportVerificationErrors = true,
            },
            DeploymentProfile.Stage2CameraPilot => new FactoryReadinessCriteria
            {
                DeploymentProfile = profile,
                Stage1Only = false,
                RequireSuccessfulLatestValidationPackage = true,
                RequireProductionModel = false,
                RequireCameraAcceptance = true,
                RequireLightingAcceptance = true,
                RequireNoExportVerificationErrors = true,
            },
            DeploymentProfile.Stage3RobotPilot => new FactoryReadinessCriteria
            {
                DeploymentProfile = profile,
                Stage1Only = false,
                RequireSuccessfulLatestValidationPackage = true,
                RequireProductionModel = false,
                RequireCameraAcceptance = true,
                RequireLightingAcceptance = true,
                RequireRobotAcceptance = true,
                RequireNoExportVerificationErrors = true,
            },
            DeploymentProfile.Stage4MesPilot => new FactoryReadinessCriteria
            {
                DeploymentProfile = profile,
                Stage1Only = false,
                RequireSuccessfulLatestValidationPackage = true,
                RequireProductionModel = false,
                RequireCameraAcceptance = true,
                RequireLightingAcceptance = true,
                RequireRobotAcceptance = true,
                RequireNoPendingMesQueueForProductionMode = true,
                RequireNoExportVerificationErrors = true,
            },
            DeploymentProfile.FullFactoryAutomation => new FactoryReadinessCriteria
            {
                DeploymentProfile = profile,
                Stage1Only = false,
                RequireSuccessfulLatestValidationPackage = true,
                RequireProductionModel = true,
                RequireFalseCallEvidence = true,
                RequireCameraAcceptance = true,
                RequireLightingAcceptance = true,
                RequireRobotAcceptance = true,
                RequireRealHardwareAcceptance = true,
                RequireNoPendingMesQueueForProductionMode = true,
                RequireSoakTestEvidenceForFactoryPilot = true,
                RequireNoExportVerificationErrors = true,
            },
            _ => new FactoryReadinessCriteria { DeploymentProfile = DeploymentProfile.Stage1ImageValidation },
        };

    public static string DisplayName(DeploymentProfile profile)
        => profile switch
        {
            DeploymentProfile.Stage1ImageValidation => "Stage 1 Customer Data Validation",
            DeploymentProfile.Stage2CameraPilot => "Stage 2 Camera Pilot",
            DeploymentProfile.Stage3RobotPilot => "Stage 3 Robot Cell Pilot",
            DeploymentProfile.Stage4MesPilot => "Stage 4 MES Traceability Pilot",
            DeploymentProfile.FullFactoryAutomation => "Full Factory Automation",
            _ => profile.ToString(),
        };

    public static FactoryReadinessPackageResult ExportGoNoGoPackage(
        FactoryReadinessCriteria? criteria = null,
        string? outputRoot = null)
    {
        var report = Evaluate(criteria);
        var root = string.IsNullOrWhiteSpace(outputRoot)
            ? Path.Combine(AoiDatabase.StorageRoot, "exports", "factory_readiness")
            : outputRoot.Trim();
        var packageFolder = EnsureUniqueFolder(Path.Combine(root, $"factory_readiness_{DateTime.UtcNow:yyyyMMdd_HHmmss}"));
        Directory.CreateDirectory(packageFolder);

        var jsonPath = Path.Combine(packageFolder, "factory_readiness_summary.json");
        var htmlPath = Path.Combine(packageFolder, "factory_readiness_summary.html");
        var readmePath = Path.Combine(packageFolder, "README.txt");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, JsonOptions), Encoding.UTF8);
        File.WriteAllText(htmlPath, BuildHtml(report), Encoding.UTF8);
        File.WriteAllText(readmePath, BuildReadme(report), Encoding.UTF8);

        CopyLatestValidationManifest(packageFolder);
        WriteLatestExportVerification(packageFolder);
        WriteLatestAcceptanceReports(packageFolder);
        WritePackageManifest(packageFolder, report);

        ExportVerificationService.RecordVerifiedExport("FactoryReadinessPackage", packageFolder, report.OverallStatus == "NoGo" ? "WARN" : "OK");
        AoiDatabase.RecordAuditEvent("FACTORY_READINESS_EXPORT", $"Factory readiness Go/No-Go package exported: {Path.GetFileName(packageFolder)}; status={report.OverallStatus}.", relatedPath: packageFolder);
        return new FactoryReadinessPackageResult(packageFolder, htmlPath, jsonPath, readmePath);
    }

    public static string BuildHtml(FactoryReadinessReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html><html><head><meta charset=\"utf-8\"><title>Factory Readiness Go/No-Go</title>");
        sb.AppendLine("<style>body{font-family:Segoe UI,Arial,sans-serif;margin:24px;color:#17212b;background:#f7f9fb}.hero{background:#fff;border:1px solid #d7e0e8;padding:18px;margin-bottom:16px}.Go{color:#176b3a}.Conditional{color:#8a5a00}.No-Go{color:#a12626}table{border-collapse:collapse;width:100%;background:#fff}td,th{border:1px solid #d7e0e8;padding:8px;text-align:left;vertical-align:top}th{background:#eef3f7}li{margin:4px 0}</style></head><body>");
        sb.AppendLine("<div class=\"hero\">");
        sb.AppendLine($"<h1>Factory Readiness Go/No-Go Summary</h1><p><strong class=\"{Css(report.OverallStatus)}\">{Html(report.OverallStatus)}</strong> for {Html(report.Scope)}<br>Deployment profile: {Html(report.DeploymentProfile)}<br>Generated UTC: {report.GeneratedAtUtc:O}</p>");
        if (report.DeploymentProfile == DeploymentProfile.Stage1ImageValidation.ToString() && report.OverallStatus != FactoryReadinessOverallStatus.NoGo.ToString())
            sb.AppendLine("<p><strong>Only Stage 1 is ready.</strong> Production camera, robot, lighting, MES, and full automation readiness are not implied by this profile.</p>");
        sb.AppendLine("<p>This package separates Stage 1 validation evidence from full factory readiness. Simulated camera, lighting, robot, or MES evidence is not real production equipment validation.</p>");
        sb.AppendLine("</div>");
        AppendList(sb, "Blocking Issues", report.BlockingIssues);
        AppendList(sb, "Warnings", report.Warnings);
        AppendList(sb, "Unmet Criteria", report.UnmetCriteria);
        AppendList(sb, "Recommended Next Actions", report.RecommendedNextActions);
        sb.AppendLine("<h2>Readiness Categories</h2><table><tr><th>Category</th><th>Status</th><th>Evidence</th><th>Next Action</th></tr>");
        foreach (var category in report.Categories)
            sb.AppendLine($"<tr><td>{Html(category.Name)}</td><td class=\"{Css(category.Status)}\">{Html(category.Status)}</td><td>{Html(category.Evidence)}</td><td>{Html(category.NextAction)}</td></tr>");
        sb.AppendLine("</table>");
        AppendList(sb, "Known Limitations", report.KnownLimitations);
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    private static void AddBuildTest(FactoryReadinessReport report)
        => Add(report, "Build/Test status", "Conditional", "No in-app build/test artifact is recorded. The dashboard cannot independently prove the latest repository build from runtime data.", "Attach CI output or run dotnet test before management/customer review.");

    private static void AddDiagnosticCategory(FactoryReadinessReport report, string name, SystemDiagnosticReport diagnostics, string diagnosticCategory)
    {
        var checks = diagnostics.Checks.Where(check => check.Category.Equals(diagnosticCategory, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (checks.Length == 0)
        {
            Add(report, name, "Conditional", $"No {diagnosticCategory} diagnostic check was available.", "Run system diagnostics.");
            return;
        }

        var status = checks.Any(check => check.Status == DiagnosticStatus.Error)
            ? "No-Go"
            : checks.Any(check => check.Status == DiagnosticStatus.Warn)
                ? "Conditional"
                : "Go";
        Add(report, name, status, string.Join(" ", checks.Select(check => $"{check.Name}: {check.Message}")), checks.FirstOrDefault(check => check.Status != DiagnosticStatus.OK)?.Remediation ?? "No action required.");
    }

    private static void AddImageVault(FactoryReadinessReport report, SystemDiagnosticReport diagnostics)
    {
        var rows = AoiDatabase.GetDatabaseHealthRows();
        var imageCount = rows.FirstOrDefault(row => row.Table == "Images")?.Count ?? "0";
        var checks = diagnostics.Checks.Where(check => check.Category == "Image Vault").ToArray();
        var status = checks.Any(check => check.Status == DiagnosticStatus.Error)
            ? "No-Go"
            : imageCount == "0"
                ? "Conditional"
                : "Go";
        Add(report, "Image vault health", status, $"Image records: {imageCount}. {string.Join(" ", checks.Select(check => check.Message))}", imageCount == "0" ? "Import or index validation images before factory/customer evidence review." : "No action required.");
    }

    private static void AddModelReadiness(FactoryReadinessReport report, FactoryReadinessCriteria criteria)
    {
        var configuration = InspectionModelConfigurationService.Load();
        var status = InspectionModelConfigurationService.GetStatus(configuration);
        var text = InspectionModelConfigurationService.GetStatusText(status);
        if (!criteria.RequireProductionModel && status == InspectionEngineStatus.PrototypeEngine)
        {
            Add(report, "Active model readiness", "Conditional", "Pixel Difference prototype engine is active. Acceptable for Stage 1 evidence only.", "Use a validated production model before claiming full production accuracy.");
            return;
        }

        if (status == InspectionEngineStatus.MlModelReady)
            Add(report, "Active model readiness", "Go", $"Active model is ready. ModelId={configuration.ActiveModelId}; version={configuration.ModelVersion}.", "No action required.");
        else
            Add(report, "Active model readiness", criteria.RequireProductionModel ? "No-Go" : "Conditional", $"Active model is not production-ready: {text}.", "Register and validate the production ONNX model or keep the review scoped to Stage 1 prototype evidence.");
    }

    private static void AddValidationPackage(FactoryReadinessReport report, FactoryReadinessCriteria criteria, ValidationPackageRecord? package, BatchTestRunRecord? run)
    {
        if (package is null)
        {
            Add(report, "Stage 1 validation package status", criteria.RequireSuccessfulLatestValidationPackage ? "No-Go" : "Conditional", "No Stage 1 validation package has been recorded.", "Create a Stage 1 customer validation package.");
            return;
        }

        var packageOk = package.AcceptanceStatus.Equals("PASS", StringComparison.OrdinalIgnoreCase) ||
            package.AcceptanceStatus.Equals("CONDITIONAL", StringComparison.OrdinalIgnoreCase) && criteria.Stage1Only;
        var status = packageOk ? (package.AcceptanceStatus.Equals("PASS", StringComparison.OrdinalIgnoreCase) ? "Go" : "Conditional") : "No-Go";
        Add(report, "Stage 1 validation package status", status, $"Latest package {package.PackageId}: acceptance={package.AcceptanceStatus}; run={package.RunId?.ToString(CultureInfo.InvariantCulture) ?? "none"}; latest batch images={run?.TotalImages ?? 0}; false-call-rate={run?.FalseCallRate.ToString("P1", CultureInfo.InvariantCulture) ?? "n/a"}.", status == "Go" ? "No action required." : "Review validation package failures and regenerate after fixes.");
    }

    private static void AddFalseCall(FactoryReadinessReport report, FactoryReadinessCriteria criteria, FalseCallReductionRun? run)
    {
        if (run?.Recommendation?.Point is not { } point)
        {
            Add(report, "False-call reduction status", criteria.RequireFalseCallEvidence ? "No-Go" : "Conditional", "No false-call reduction recommendation is available.", "Run False Call Reduction Workbench on customer-labeled Stage 1 data.");
            return;
        }

        var status = run.Recommendation.Status == "VALID" && point.FalseCallRate <= criteria.MaximumFalseCallRate
            ? "Go"
            : run.Recommendation.Status == "INVALID"
                ? "No-Go"
                : "Conditional";
        Add(report, "False-call reduction status", status, $"Recommendation={run.Recommendation.Status}; threshold={point.DifferenceThreshold:F2}; false-call-rate={point.FalseCallRate:P1}; possible-escape-rate={point.PossibleEscapeRate:P1}; review-rate={point.ReviewRate:P1}.", status == "Go" ? "No action required." : "Tune thresholds with sufficient labeled OK/NG data and review possible escapes.");
    }

    private static void AddDatasetQuality(FactoryReadinessReport report, FactoryReadinessCriteria criteria, ValidationPackageRecord? package)
    {
        var manifest = TryReadManifest(package?.ManifestPath);
        if (manifest is null)
        {
            Add(report, "Dataset quality status", criteria.RequireDatasetQualityEvidence ? "No-Go" : "Conditional", "No validation manifest with dataset-quality summary is available.", "Generate a validation package from a labeled dataset.");
            return;
        }

        var status = manifest.DatasetQualitySummary.Status.Equals("PASS", StringComparison.OrdinalIgnoreCase)
            ? "Go"
            : manifest.DatasetQualitySummary.Status.Equals("FAIL", StringComparison.OrdinalIgnoreCase)
                ? "No-Go"
                : "Conditional";
        Add(report, "Dataset quality status", status, $"Dataset quality={manifest.DatasetQualitySummary.Status}; total={manifest.DatasetQualitySummary.TotalImages}; known GT={manifest.DatasetQualitySummary.KnownGroundTruthImages}; OK={manifest.DatasetQualitySummary.OkImages}; NG={manifest.DatasetQualitySummary.NgImages}.", status == "Go" ? "No action required." : "Balance dataset labels, reduce UNKNOWN labels, and include required golden images.");
    }

    private static void AddExportVerification(FactoryReadinessReport report, FactoryReadinessCriteria criteria, IReadOnlyList<ExportVerificationRecord> verifications)
    {
        if (verifications.Count == 0)
        {
            Add(report, "Export verification status", criteria.RequireNoExportVerificationErrors ? "No-Go" : "Conditional", "No export verification records are available.", "Export and verify customer/factory evidence artifacts.");
            return;
        }

        var errors = verifications.Count(item => item.Status.Equals("ERROR", StringComparison.OrdinalIgnoreCase));
        var warns = verifications.Count(item => item.Status.Equals("WARN", StringComparison.OrdinalIgnoreCase));
        var status = errors > 0 && criteria.RequireNoExportVerificationErrors ? "No-Go" : warns > 0 || errors > 0 ? "Conditional" : "Go";
        Add(report, "Export verification status", status, $"Latest {verifications.Count} verification record(s): errors={errors}, warnings={warns}.", status == "Go" ? "No action required." : "Re-export failed artifacts and resolve checksum/manifest/header errors.");
    }

    private static void AddSoakTest(FactoryReadinessReport report, FactoryReadinessCriteria criteria)
    {
        var latest = AoiDatabase.GetLatestSoakTestRun();
        if (latest is null)
        {
            Add(report, "Soak test status", criteria.RequireSoakTestEvidenceForFactoryPilot ? "No-Go" : "Conditional", "No soak-test report export was found.", "Run a controlled soak test before factory pilot.");
            return;
        }

        var factoryAccepted = latest.IsCompletedFactoryEvidence;
        var status = factoryAccepted
            ? "Go"
            : criteria.RequireSoakTestEvidenceForFactoryPilot
                ? "No-Go"
                : latest.WasCanceled || latest.FailedCycles > 0 ? "Conditional" : "Go";
        Add(
            report,
            "Soak test status",
            status,
            $"Latest soak run {latest.RunId}: profile={latest.ProfileName}; source={latest.SourceKind}; duration={latest.ActualDuration.TotalHours:F2} h / requested={latest.RequestedDuration.TotalHours:F2} h; iterations={latest.TotalCycles}; failures={latest.FailedCycles}; canceled={latest.WasCanceled}; avg={latest.AverageInspectionMilliseconds:F0} ms; p95={latest.P95InspectionMilliseconds:F0} ms; max={latest.MaxInspectionMilliseconds:F0} ms; over1s={latest.CountOverOneSecond}; factoryEvidenceAccepted={factoryAccepted}.",
            status == "Go" ? "No action required." : "Run the Factory PoC 8-hour profile to completion with no critical errors.");
    }

    private static void AddCamera(FactoryReadinessReport report, FactoryReadinessCriteria criteria, CameraAcceptanceRun? run)
    {
        var summary = CameraAcceptanceTestService.ToSummary(run);
        var required = criteria.RequireCameraAcceptance;
        var realRequired = criteria.RequireRealHardwareAcceptance && required;
        var ok = summary.AcceptanceStatus is "PASS" or "WARN" && (!realRequired || summary.IsRealHardware);
        var status = ok ? "Go" : required ? "No-Go" : "Conditional";
        Add(report, "Camera acceptance status", status, $"Status={summary.Status}; acceptance={summary.AcceptanceStatus}; realHardware={summary.IsRealHardware}; adapter={summary.AdapterName}; received={summary.TotalReceivedFrames}/{summary.TotalRequestedFrames}. {string.Join(" ", summary.Messages)}", ok ? "No action required." : "Run camera acceptance with the required production camera profile.");
    }

    private static void AddLighting(FactoryReadinessReport report, FactoryReadinessCriteria criteria, LightingAcceptanceRun? run)
    {
        if (run is null)
        {
            Add(report, "Lighting sync status", criteria.RequireLightingAcceptance ? "No-Go" : "Conditional", "No lighting synchronization acceptance run has been recorded.", "Run lighting sync acceptance when lighting is part of deployment.");
            return;
        }

        var realOk = !criteria.RequireRealHardwareAcceptance || !run.IsSimulated;
        var ok = run.Status is "PASS" or "WARN" && realOk;
        Add(report, "Lighting sync status", ok ? "Go" : criteria.RequireLightingAcceptance ? "No-Go" : "Conditional", $"Status={run.Status}; mode={run.Mode}; simulated={run.IsSimulated}; steps={run.PassedStepCount}/{run.StepCount}. {string.Join(" ", run.Warnings.Concat(run.Failures))}", ok ? "No action required." : "Run lighting sync with the production lighting controller or scope review to simulation only.");
    }

    private static void AddRobot(FactoryReadinessReport report, FactoryReadinessCriteria criteria, RobotAcceptanceRun? run)
    {
        var summary = RobotAcceptanceTestService.ToSummary(run);
        var realOk = !criteria.RequireRealHardwareAcceptance || summary.SourceKind == "Real";
        var eStopOk = !criteria.RequireRobotAcceptance || summary.EmergencyStopBlocked;
        var ok = summary.Status == "PASS" && realOk && eStopOk;
        Add(report, "Robot acceptance status", ok ? "Go" : criteria.RequireRobotAcceptance ? "No-Go" : "Conditional", $"Status={summary.Status}; source={summary.SourceKind}; controller={summary.ControllerName}; fullCycleMs={summary.FullCycleMs:F1}; emergencyStopBlocked={summary.EmergencyStopBlocked}. {string.Join(" ", summary.Messages)}", ok ? "No action required." : "Run robot acceptance on the required controller, including emergency-stop blocking evidence, without equating simulation to real machine validation.");
    }

    private static void AddMes(FactoryReadinessReport report, FactoryReadinessCriteria criteria, MesReadinessSummary mes)
    {
        var productionMes = !criteria.Stage1Only && criteria.RequireNoPendingMesQueueForProductionMode;
        var status = mes.Status switch
        {
            "MES REST Ready" => "Go",
            "MES Queue Pending" or "MES Queue Pending FAIL" => productionMes ? "No-Go" : "Conditional",
            "MES REST Error" => "No-Go",
            "MES Mock Only" or "MES Not Configured" => productionMes ? "No-Go" : "Conditional",
            _ => "Conditional",
        };
        Add(report, "MES/spool status", status, $"{mes.Status}; mode={mes.Mode}; pending={mes.PendingCount}; failed={mes.FailedCount}; sent={mes.SentCount}; abandoned={mes.AbandonedCount}. {string.Join(" ", mes.Messages)}", status == "Go" ? "No action required." : "Resolve pending/failed MES queue items and configure production REST only when explicitly required.");
    }

    private static void AddSecurityAudit(FactoryReadinessReport report)
    {
        var audits = AoiDatabase.GetAuditEvents(new LogFilter()).Take(50).ToArray();
        var denied = audits.Count(item => item.ActionCategory.Equals("ACCESS_DENIED", StringComparison.OrdinalIgnoreCase));
        Add(report, "Security/role audit status", audits.Length == 0 ? "Conditional" : "Go", $"Audit rows available={audits.Length}; recent access-denied events={denied}. Admin-only actions remain role-gated in the local audit trail.", audits.Length == 0 ? "Exercise role-gated workflows and export audit evidence." : "No action required.");
    }

    private static void AddKnownLimitations(FactoryReadinessReport report, FactoryReadinessCriteria criteria)
    {
        var scope = criteria.Stage1Only
            ? $"Selected profile: {DisplayName(criteria.DeploymentProfile)}. Stage 1 package does not validate production camera, lighting, robot, MES writeback, or production model accuracy."
            : $"Selected profile: {DisplayName(criteria.DeploymentProfile)}. Full factory readiness requires real hardware and production MES evidence; simulated evidence is listed separately.";
        Add(report, "Known limitations", "Conditional", scope, "Keep customer/management wording limited to the evidence actually collected.");
    }

    private static string ScopeFor(DeploymentProfile profile)
        => DisplayName(profile);

    private static void Add(FactoryReadinessReport report, string name, string status, string evidence, string nextAction)
        => report.Categories.Add(new FactoryReadinessCategory
        {
            Name = name,
            Status = status,
            Evidence = evidence,
            NextAction = nextAction,
        });

    private static ValidationPackageManifest? TryReadManifest(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        try
        {
            return JsonSerializer.Deserialize<ValidationPackageManifest>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void CopyLatestValidationManifest(string packageFolder)
    {
        var latest = AoiDatabase.GetValidationPackages(1).FirstOrDefault();
        if (latest is null || !File.Exists(latest.ManifestPath))
            return;

        File.Copy(latest.ManifestPath, Path.Combine(packageFolder, "latest_validation_manifest.json"), overwrite: true);
    }

    private static void WriteLatestExportVerification(string packageFolder)
    {
        var records = AoiDatabase.GetExportVerifications(25);
        if (records.Count == 0)
            return;

        var path = Path.Combine(packageFolder, "latest_export_verification_report.json");
        File.WriteAllText(path, JsonSerializer.Serialize(records, JsonOptions), Encoding.UTF8);
    }

    private static void WriteLatestAcceptanceReports(string packageFolder)
    {
        var evidence = Path.Combine(packageFolder, "latest_acceptance_reports");
        Directory.CreateDirectory(evidence);

        if (AoiDatabase.GetLatestCameraAcceptanceRun(realHardwareOnly: false) is { } camera)
            File.WriteAllText(Path.Combine(evidence, "latest_camera_acceptance.json"), JsonSerializer.Serialize(camera, JsonOptions), Encoding.UTF8);
        if (AoiDatabase.GetLatestLightingAcceptanceRun() is { } lighting)
            File.WriteAllText(Path.Combine(evidence, "latest_lighting_acceptance.json"), JsonSerializer.Serialize(lighting, JsonOptions), Encoding.UTF8);
        if (AoiDatabase.GetLatestRobotAcceptanceRun() is { } robot)
            File.WriteAllText(Path.Combine(evidence, "latest_robot_acceptance.json"), JsonSerializer.Serialize(robot, JsonOptions), Encoding.UTF8);
        if (AoiDatabase.GetLatestSoakTestRun() is { } soak)
            File.WriteAllText(Path.Combine(evidence, "latest_soak_test.json"), JsonSerializer.Serialize(soak, JsonOptions), Encoding.UTF8);

        var mesReport = MesSpoolService.EvaluateReadiness();
        File.WriteAllText(Path.Combine(evidence, "latest_mes_readiness.json"), JsonSerializer.Serialize(mesReport, JsonOptions), Encoding.UTF8);
    }

    private static void WritePackageManifest(string packageFolder, FactoryReadinessReport report)
    {
        var manifestPath = Path.Combine(packageFolder, "package_manifest.json");
        var files = Directory.EnumerateFiles(packageFolder, "*", SearchOption.AllDirectories)
            .Where(path => !path.Equals(manifestPath, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => Path.GetRelativePath(packageFolder, path), StringComparer.OrdinalIgnoreCase)
            .Select(path => new ValidationIncludedFile
            {
                RelativePath = Path.GetRelativePath(packageFolder, path).Replace('\\', '/'),
                FileType = Classify(path),
                Bytes = new FileInfo(path).Length,
            })
            .ToList();

        var manifest = new
        {
            schemaVersion = "factory-readiness-package/v1",
            packageId = $"FACTORY-{report.GeneratedAtUtc:yyyyMMddHHmmss}",
            generatedAtUtc = report.GeneratedAtUtc,
            overallStatus = report.OverallStatus,
            deploymentProfile = report.DeploymentProfile,
            scope = report.Scope,
            unmetCriteria = report.UnmetCriteria,
            includedFiles = files,
        };
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions), Encoding.UTF8);
    }

    private static string Classify(string path)
    {
        var fileName = Path.GetFileName(path);
        if (fileName.Contains("factory_readiness_summary", StringComparison.OrdinalIgnoreCase))
            return "Factory readiness summary";
        if (fileName.Contains("validation_manifest", StringComparison.OrdinalIgnoreCase))
            return "Latest validation manifest";
        if (fileName.Contains("export_verification", StringComparison.OrdinalIgnoreCase))
            return "Latest export verification";
        if (fileName.Contains("acceptance", StringComparison.OrdinalIgnoreCase) || fileName.Contains("mes_readiness", StringComparison.OrdinalIgnoreCase))
            return "Latest acceptance/readiness evidence";
        if (fileName.Equals("README.txt", StringComparison.OrdinalIgnoreCase))
            return "README";
        return "Factory readiness evidence";
    }

    private static string BuildReadme(FactoryReadinessReport report)
        => $"""
        AOI Monitor Factory Readiness Go/No-Go Package

        Overall status: {report.OverallStatus}
        Deployment profile: {report.DeploymentProfile}
        Scope: {report.Scope}
        Generated UTC: {report.GeneratedAtUtc:O}

        Contents:
        - factory_readiness_summary.html: management-readable Go/No-Go summary.
        - factory_readiness_summary.json: machine-readable category evidence.
        - latest_validation_manifest.json: copied when a Stage 1 validation package exists.
        - latest_export_verification_report.json: copied when export verification records exist.
        - latest_acceptance_reports/: latest camera, lighting, robot, and MES readiness summaries when available.

        Evidence boundary:
        This package distinguishes Stage 1 customer/demo readiness from full factory readiness. Simulated camera, lighting, robot, or MES evidence must not be described as production equipment validation.

        Blocking issues:
        {FormatLines(report.BlockingIssues)}

        Warnings:
        {FormatLines(report.Warnings)}

        Unmet criteria:
        {FormatLines(report.UnmetCriteria)}
        """;

    private static void AppendList(StringBuilder sb, string title, IReadOnlyCollection<string> items)
    {
        if (items.Count == 0)
            return;
        sb.AppendLine($"<h2>{Html(title)}</h2><ul>");
        foreach (var item in items)
            sb.AppendLine($"<li>{Html(item)}</li>");
        sb.AppendLine("</ul>");
    }

    private static string FormatLines(IReadOnlyCollection<string> lines)
        => lines.Count == 0 ? "- None recorded." : string.Join(Environment.NewLine, lines.Select(line => $"- {line}"));

    private static string Css(string status)
        => status.Replace(" ", "-", StringComparison.Ordinal);

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
