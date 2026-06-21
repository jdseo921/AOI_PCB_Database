using System.Globalization;
using AOI_Monitor.Data;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public sealed class CompletionAssessmentReport
{
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
    public List<CompletionAssessmentCategory> Categories { get; set; } = new();
    public double OverallPercent => Categories.Count == 0 ? 0 : Categories.Average(category => category.PercentComplete);
}

public sealed class CompletionAssessmentCategory
{
    public string Stage { get; set; } = string.Empty;
    public double PercentComplete { get; set; }
    public List<CompletionAssessmentCriterion> Criteria { get; set; } = new();
    public List<string> MissingEvidence { get; set; } = new();
    public List<string> NextActions { get; set; } = new();
    public string EvidenceSummary { get; set; } = string.Empty;
}

public sealed class CompletionAssessmentCriterion
{
    public string Name { get; set; } = string.Empty;
    public double Weight { get; set; }
    public double Earned { get; set; }
    public string Evidence { get; set; } = string.Empty;
    public bool IsSatisfied => Earned >= Weight;
}

public static class CompletionAssessmentService
{
    public static CompletionAssessmentReport Assess()
    {
        AoiDatabase.Initialize();
        var report = new CompletionAssessmentReport();
        report.Categories.Add(AssessStage1());
        report.Categories.Add(AssessModelReadiness());
        report.Categories.Add(AssessFalsePositiveReductionReadiness());
        report.Categories.Add(AssessStage2());
        report.Categories.Add(AssessStage3());
        report.Categories.Add(AssessStage4());
        report.Categories.Add(AssessCentralSyncManagement());
        report.Categories.Add(AssessReliability());
        report.Categories.Add(AssessDeploymentSupportability());
        report.Categories.Add(AssessCommercialReadiness());
        return report;
    }

    private static CompletionAssessmentCategory AssessStage1()
    {
        var latestPackage = AoiDatabase.GetValidationPackages(1).FirstOrDefault();
        var latestRun = AoiDatabase.GetLatestBatchTestRun();
        var latestFalseCall = AoiDatabase.GetLatestFalseCallReductionRun();
        var latestVerification = AoiDatabase.GetExportVerifications(25).FirstOrDefault();
        return Category("Stage 1 image validation",
            Criterion("Stage 1 customer validation package recorded", 40, latestPackage is not null, latestPackage is null ? "No validation package record." : $"package={latestPackage.PackageId}; status={latestPackage.AcceptanceStatus}."),
            Criterion("Validation batch run with persisted image results", 25, latestRun is { TotalImages: > 0 }, latestRun is null ? "No batch run." : $"run={latestRun.Id}; images={latestRun.TotalImages}; falseCall={latestRun.FalseCallRate:P1}."),
            Criterion("False-call reduction or operating-point evidence", 20, latestFalseCall?.Recommendation.Point is not null, latestFalseCall is null ? "No false-call reduction run." : $"run={latestFalseCall.Id}; status={latestFalseCall.Recommendation.Status}."),
            Criterion("Export verification evidence for customer package", 15, latestVerification is not null && !latestVerification.Status.Contains("ERROR", StringComparison.OrdinalIgnoreCase), latestVerification is null ? "No export verification record." : $"latest export verification={latestVerification.Status}."));
    }

    private static CompletionAssessmentCategory AssessStage2()
    {
        var camera = AoiDatabase.GetLatestCameraAcceptanceRun(realHardwareOnly: false);
        var realCamera = AoiDatabase.GetLatestCameraAcceptanceRun(realHardwareOnly: true);
        var lighting = AoiDatabase.GetLatestLightingAcceptanceRun();
        var profile3D = AoiDatabase.GetLatestProfile3DAcceptanceRun();
        var lightingReal = lighting is not null && !lighting.IsSimulated && IsPass(lighting.Status);
        var profileReal = profile3D is not null && !profile3D.IsSimulated && IsPass(profile3D.Status);
        var simulatedBoundary = (camera is not null && !camera.IsRealHardware && IsPass(camera.Status)) ||
                                (lighting is not null && lighting.IsSimulated && IsPass(lighting.Status)) ||
                                (profile3D is not null && profile3D.IsSimulated && IsPass(profile3D.Status));

        return Category("Stage 2 camera/lighting/3D",
            Criterion("Real camera acceptance PASS", 35, realCamera is not null && IsPass(realCamera.Status), realCamera is null ? CameraEvidence(camera) : CameraEvidence(realCamera)),
            Criterion("Real lighting sync acceptance PASS", 25, lightingReal, lighting is null ? "No lighting acceptance run." : $"status={lighting.Status}; simulated={lighting.IsSimulated}."),
            Criterion("Real 3D profile acceptance PASS", 25, profileReal, profile3D is null ? "No 3D profile acceptance run." : $"status={profile3D.Status}; simulated={profile3D.IsSimulated}; source={profile3D.SourceKind}."),
            Criterion("Simulation boundary exercised but not real hardware", 15, simulatedBoundary, simulatedBoundary ? "Simulation acceptance exists; this does not satisfy real hardware criteria." : "No simulated boundary exercise recorded."));
    }

    private static CompletionAssessmentCategory AssessStage3()
    {
        var robot = AoiDatabase.GetLatestRobotAcceptanceRun();
        var realRobot = robot is not null &&
                        IsPass(robot.Status) &&
                        !robot.SourceKind.Contains("Simulated", StringComparison.OrdinalIgnoreCase);
        var realSafety = robot is not null &&
                         !robot.SafetySourceKind.Contains("Simulated", StringComparison.OrdinalIgnoreCase) &&
                         !robot.SafetySourceKind.Contains("NotConnected", StringComparison.OrdinalIgnoreCase);

        return Category("Stage 3 robot/safety",
            Criterion("Real robot cell acceptance PASS", 35, realRobot, robot is null ? "No robot acceptance run." : $"status={robot.Status}; source={robot.SourceKind}."),
            Criterion("Safety interlock evidence from real safety/PLC boundary", 30, realSafety && robot!.EmergencyStopBlocked && robot.SafetyFaultBlocked, robot is null ? "No safety evidence." : $"safety={robot.SafetySourceKind}; eStop={robot.EmergencyStopBlocked}; safetyFault={robot.SafetyFaultBlocked}."),
            Criterion("Invalid transition and reset checks pass", 20, robot is not null && robot.InvalidTransitionRejected && robot.ResetReturnedIdle, robot is null ? "No robot transition/reset evidence." : $"invalidRejected={robot.InvalidTransitionRejected}; resetIdle={robot.ResetReturnedIdle}."),
            Criterion("Robot audit events recorded", 15, robot is not null && robot.AuditEventCount > 0, robot is null ? "No robot audit evidence." : $"auditEvents={robot.AuditEventCount}."));
    }

    private static CompletionAssessmentCategory AssessStage4()
    {
        var mes = MesSpoolService.EvaluateReadiness(new MesReadinessCriteria { RequirePassingTraceabilityTest = true });
        var traceability = AoiDatabase.GetLatestTraceabilityTestReport();
        return Category("Stage 4 MES/ERP",
            Criterion("Passing MES traceability acceptance", 35, traceability is not null && IsPass(traceability.Status), traceability is null ? "No traceability test report." : $"status={traceability.Status}; mode={traceability.Mode}."),
            Criterion("MES REST configured and ready", 25, mes.Status == "MES REST Ready", $"MES readiness={mes.Status}; mode={mes.Mode}."),
            Criterion("MES queue has no pending or failed production items", 20, mes.PendingCount == 0 && mes.FailedCount == 0, $"pending={mes.PendingCount}; failed={mes.FailedCount}; sent={mes.SentCount}."),
            Criterion("MES abandoned-item disposition visible", 20, mes.AbandonedCount == 0, $"abandoned={mes.AbandonedCount}; latestTraceability={mes.LatestTraceabilityTestStatus}."));
    }

    private static CompletionAssessmentCategory AssessModelReadiness()
    {
        var active = ModelRegistryService.GetActiveModel();
        var latestAcceptance = AoiDatabase.GetLatestModelAcceptanceRun(active?.ModelId);
        var passing = AoiDatabase.GetLatestPassingProductionModelAcceptance(active?.ModelId);
        return Category("Production model readiness",
            Criterion("Active model registered", 15, active is not null, active is null ? "No active model registry record." : $"model={active.ModelId}; state={active.LifecycleState}."),
            Criterion("Runtime validation completed", 15, active is not null && active.ValidationStatus != ModelConfigurationTestStatus.NotTested, active is null ? "No runtime validation." : $"runtime={active.ValidationStatus}."),
            Criterion("PASS model acceptance run", 35, passing is not null, latestAcceptance is null ? "No model acceptance run." : $"latest={latestAcceptance.Status}; model={latestAcceptance.ModelId}."),
            Criterion("Production candidate or deployed lifecycle state", 20, active is not null && (active.LifecycleState == ModelLifecycleState.ProductionCandidate || active.LifecycleState == ModelLifecycleState.Deployed), active is null ? "No lifecycle state." : $"state={active.LifecycleState}."),
            Criterion("Release package path recorded", 15, active is not null && !string.IsNullOrWhiteSpace(active.LatestReleasePackagePath), active is null ? "No release package evidence." : $"release={active.LatestReleasePackagePath}."));
    }

    private static CompletionAssessmentCategory AssessFalsePositiveReductionReadiness()
    {
        var latestBatch = AoiDatabase.GetLatestBatchTestRun();
        var falseCall = AoiDatabase.GetLatestFalseCallReductionRun(latestBatch?.Id);
        var anyFalseCall = falseCall ?? AoiDatabase.GetLatestFalseCallReductionRun();
        var deployedProfile = ThresholdProfileService.GetActiveEvidenceSummary("ANY", "ANY", "ANY");
        var hasRecommendation = anyFalseCall?.Recommendation.Point is not null;
        return Category("False-positive reduction readiness",
            Criterion("Validation run has measurable false-call rate", 20, latestBatch is not null, latestBatch is null ? "No validation batch run." : $"run={latestBatch.Id}; falseCall={latestBatch.FalseCallRate:P1}."),
            Criterion("False-call sweep completed", 30, anyFalseCall is not null, anyFalseCall is null ? "No false-call reduction run." : $"run={anyFalseCall.Id}; status={anyFalseCall.Recommendation.Status}."),
            Criterion("Recommended operating point exists", 25, hasRecommendation, hasRecommendation ? "Recommendation point persisted." : "No recommended threshold/operating point."),
            Criterion("Approved/deployed threshold profile linked to false-call evidence", 25, deployedProfile.IsDeployed && deployedProfile.SourceFalseCallReductionRunId is not null, $"profile={deployedProfile.ProfileId}; deployed={deployedProfile.IsDeployed}; sourceFalseCallRun={deployedProfile.SourceFalseCallReductionRunId}."));
    }

    private static CompletionAssessmentCategory AssessCentralSyncManagement()
    {
        var central = CentralSyncService.EvaluateReadiness();
        var exports = AoiDatabase.GetExportHistory(500);
        return Category("Central sync/management",
            Criterion("Central sync configured", 20, central.Mode != "Disabled", $"centralSync={central.Status}; mode={central.Mode}."),
            Criterion("Central sync queue has no failed items", 20, central.FailedCount == 0, $"pending={central.PendingCount}; failed={central.FailedCount}; sent={central.SentCount}; skipped={central.SkippedCount}."),
            Criterion("Management dashboard exported", 30, exports.Any(row => row.ExportType.Contains("ManagementDashboard", StringComparison.OrdinalIgnoreCase)), "Requires management dashboard export."),
            Criterion("Central sync or management report exported", 30, exports.Any(row => row.ExportType.Contains("CentralSync", StringComparison.OrdinalIgnoreCase) || row.ExportType.Contains("ManagementDashboard", StringComparison.OrdinalIgnoreCase)), "Requires central sync or management dashboard export evidence."));
    }

    private static CompletionAssessmentCategory AssessReliability()
    {
        var soak = AoiDatabase.GetLatestSoakTestRun();
        var latency = InspectionLatencyService.GetRecentSummary();
        return Category("Reliability/soak",
            Criterion("Soak test run recorded", 25, soak is not null, soak is null ? "No soak test run." : $"run={soak.RunId}; cycles={soak.TotalCycles}; canceled={soak.WasCanceled}."),
            Criterion("30-minute or longer stability evidence", 20, soak is not null && soak.ActualDuration >= TimeSpan.FromMinutes(30), soak is null ? "No duration evidence." : $"duration={soak.ActualDuration.TotalMinutes:F1} minutes."),
            Criterion("8-hour factory PoC evidence complete", 30, soak?.IsCompletedFactoryEvidence == true, soak is null ? "No factory soak evidence." : $"factoryEvidence={soak.IsCompletedFactoryEvidence}."),
            Criterion("No failed cycles or critical errors", 15, soak is not null && soak.FailedCycles == 0 && string.IsNullOrWhiteSpace(soak.FirstCriticalError), soak is null ? "No failure evidence." : $"failed={soak.FailedCycles}; critical={soak.FirstCriticalError}."),
            Criterion("Inspection latency traces available", 10, latency.TraceCount > 0, $"latencyTraces={latency.TraceCount}; p95Overlay={latency.P95FrameToOverlayMs:F0} ms."));
    }

    private static CompletionAssessmentCategory AssessDeploymentSupportability()
    {
        var exports = AoiDatabase.GetExportHistory(500);
        var build = AoiDatabase.GetLatestBuildTestEvidence();
        var backupExported = exports.Any(row => row.ExportType.Contains("ConfigurationBackup", StringComparison.OrdinalIgnoreCase)) ||
                             AoiDatabase.GetAuditEvents(new LogFilter { ActionCategory = "CONFIG_BACKUP" }, 25).Count > 0;
        return Category("Deployment/supportability",
            Criterion("Passing build/test/publish evidence imported", 30, build is not null && build.BuildStatus == "PASS" && build.TestStatus == "PASS" && build.PublishValidationStatus == "PASS", build is null ? "No build/test evidence." : $"build={build.BuildStatus}; test={build.TestStatus}; publish={build.PublishValidationStatus}."),
            Criterion("Configuration backup exported", 25, backupExported, "Requires configuration backup audit/export evidence."),
            Criterion("Factory readiness package exported", 25, exports.Any(row => row.ExportType.Contains("FactoryReadiness", StringComparison.OrdinalIgnoreCase)), "Requires exported Go/No-Go package."),
            Criterion("Factory acceptance checklist/package exported", 20, exports.Any(row => row.ExportType.Contains("FactoryAcceptance", StringComparison.OrdinalIgnoreCase)), "Requires FAT checklist/package export."));
    }

    private static CompletionAssessmentCategory AssessCommercialReadiness()
    {
        var exports = AoiDatabase.GetExportHistory(500);
        var build = AoiDatabase.GetLatestBuildTestEvidence();
        var auth = AuthenticationSettingsService.CurrentMode;
        var readiness = FactoryReadinessService.Evaluate(FactoryReadinessService.CriteriaForProfile(DeploymentProfile.FullFactoryAutomation));
        return Category("Commercial readiness",
            Criterion("Local accountability mode enabled", 25, auth == AuthenticationMode.LocalUsers, $"authMode={auth}; demo mode is not production accountability."),
            Criterion("Management dashboard evidence exported", 20, exports.Any(row => row.ExportType.Contains("ManagementDashboard", StringComparison.OrdinalIgnoreCase)), "Requires management dashboard export."),
            Criterion("Full-factory readiness has no blocking issues", 25, readiness.BlockingIssues.Count == 0 && readiness.OverallStatus != FactoryReadinessOverallStatus.NoGo.ToString(), $"fullFactory={readiness.OverallStatus}; blocking={readiness.BlockingIssues.Count}."),
            Criterion("Release/support build evidence present", 15, build is not null && build.BuildStatus == "PASS" && build.TestStatus == "PASS", build is null ? "No release/support build evidence." : $"build={build.BuildStatus}; test={build.TestStatus}."),
            Criterion("Customer/commercial evidence package exported", 15, exports.Any(row => row.ExportType.Contains("Customer", StringComparison.OrdinalIgnoreCase) || row.ExportType.Contains("ValidationPackage", StringComparison.OrdinalIgnoreCase)), "Requires customer or validation package export evidence."));
    }

    private static CompletionAssessmentCategory Category(string stage, params CompletionAssessmentCriterion[] criteria)
    {
        var category = new CompletionAssessmentCategory
        {
            Stage = stage,
            Criteria = criteria.ToList(),
        };
        var totalWeight = category.Criteria.Sum(item => item.Weight);
        category.PercentComplete = totalWeight <= 0 ? 0 : Math.Round(category.Criteria.Sum(item => item.Earned) / totalWeight * 100.0, 1);
        category.MissingEvidence = category.Criteria
            .Where(item => !item.IsSatisfied)
            .Select(item => $"{item.Name}: {item.Evidence}")
            .ToList();
        category.NextActions = category.Criteria
            .Where(item => !item.IsSatisfied)
            .Select(item => NextActionFor(item.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        category.EvidenceSummary = string.Join(" ", category.Criteria.Select(item => $"{item.Name}={item.Earned.ToString("0.#", CultureInfo.InvariantCulture)}/{item.Weight.ToString("0.#", CultureInfo.InvariantCulture)}"));
        return category;
    }

    private static CompletionAssessmentCriterion Criterion(string name, double weight, bool satisfied, string evidence)
        => new()
        {
            Name = name,
            Weight = weight,
            Earned = satisfied ? weight : 0,
            Evidence = evidence,
        };

    private static bool IsPass(string? status)
        => string.Equals(status, "PASS", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(status, "Go", StringComparison.OrdinalIgnoreCase);

    private static string CameraEvidence(CameraAcceptanceRun? run)
        => run is null
            ? "No camera acceptance run."
            : $"status={run.Status}; realHardware={run.IsRealHardware}; adapter={run.AdapterName}.";

    private static string NextActionFor(string criterionName)
    {
        var name = criterionName.ToLowerInvariant();
        if (name.Contains("model", StringComparison.Ordinal) || name.Contains("release", StringComparison.Ordinal))
            return "Run runtime validation, model acceptance, lifecycle promotion, and release packaging.";
        if (name.Contains("false-call", StringComparison.Ordinal) || name.Contains("threshold", StringComparison.Ordinal) || name.Contains("operating point", StringComparison.Ordinal))
            return "Run false-call reduction, approve the threshold profile, and deploy it with audit evidence.";
        if (name.Contains("camera", StringComparison.Ordinal) || name.Contains("lighting", StringComparison.Ordinal) || name.Contains("3d", StringComparison.Ordinal))
            return "Run real hardware camera, lighting, and 3D acceptance tests.";
        if (name.Contains("robot", StringComparison.Ordinal) || name.Contains("safety", StringComparison.Ordinal) || name.Contains("interlock", StringComparison.Ordinal))
            return "Run real robot cell and PLC/safety acceptance with audit events.";
        if (name.Contains("mes", StringComparison.Ordinal) || name.Contains("traceability", StringComparison.Ordinal))
            return "Configure MES REST, pass traceability acceptance, and clear the MES queue.";
        if (name.Contains("central", StringComparison.Ordinal) || name.Contains("management", StringComparison.Ordinal))
            return "Configure central sync and export the management dashboard/report package.";
        if (name.Contains("soak", StringComparison.Ordinal) || name.Contains("latency", StringComparison.Ordinal) || name.Contains("stability", StringComparison.Ordinal))
            return "Run soak and latency evidence, including the full factory-duration profile when required.";
        if (name.Contains("backup", StringComparison.Ordinal) || name.Contains("build", StringComparison.Ordinal) || name.Contains("publish", StringComparison.Ordinal))
            return "Import passing build/test evidence and export a configuration backup.";
        if (name.Contains("accountability", StringComparison.Ordinal) || name.Contains("customer", StringComparison.Ordinal) || name.Contains("commercial", StringComparison.Ordinal))
            return "Enable LocalUsers accountability and export customer/commercial review packages.";
        return "Record the required evidence through the matching acceptance, export, or readiness workflow.";
    }
}
