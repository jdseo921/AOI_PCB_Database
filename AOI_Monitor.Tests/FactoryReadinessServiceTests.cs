using System.Text.Json;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Xunit;

namespace AOI_Monitor.Tests;

public sealed class FactoryReadinessServiceTests : IDisposable
{
    private readonly string _root;

    public FactoryReadinessServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AOI_Monitor_FactoryReadiness_Tests", Guid.NewGuid().ToString("N"));
        AoiDatabase.ConfigureStorageRoot(_root);
        AoiDatabase.Initialize();
        InspectionModelConfigurationService.Save(new InspectionModelConfiguration());
        MesIntegrationSettingsService.Save(new MesIntegrationSettings());
        CentralSyncSettingsService.ResetForTests();
        CentralSyncSettingsService.Save(new CentralSyncSettings());
        DeploymentProfileSettingsService.ResetForTests();
        OperatingModeSettingsService.ResetForTests();
        OperatingModeSettingsService.Save(OperatingMode.Demo, "TestAdmin [Admin]");
    }

    public void Dispose()
    {
        DeploymentProfileSettingsService.ResetForTests();
        OperatingModeSettingsService.ResetForTests();
        CentralSyncSettingsService.ResetForTests();
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void NoBuildTestEvidenceLeavesBuildCategoryConditional()
    {
        var report = FactoryReadinessService.Evaluate(new FactoryReadinessCriteria
        {
            DeploymentProfile = DeploymentProfile.Stage1ImageValidation,
            Stage1Only = true,
            RequireSuccessfulLatestValidationPackage = false,
            RequireDatasetQualityEvidence = false,
            RequireNoExportVerificationErrors = false,
        });

        Assert.Contains(report.Categories, category =>
            category.Name == "Build/Test status" &&
            category.Status == "Conditional" &&
            category.Evidence.Contains("No local build/test evidence", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PassingBuildTestEvidenceMakesBuildCategoryGo()
    {
        BuildTestEvidenceService.CreateLocalEvidence(operatorId: "TestAdmin [Admin]");

        var report = FactoryReadinessService.Evaluate(new FactoryReadinessCriteria
        {
            DeploymentProfile = DeploymentProfile.Stage1ImageValidation,
            Stage1Only = true,
            RequireSuccessfulLatestValidationPackage = false,
            RequireDatasetQualityEvidence = false,
            RequireNoExportVerificationErrors = false,
        });

        Assert.Contains(report.Categories, category =>
            category.Name == "Build/Test status" &&
            category.Status == "Go" &&
            category.Evidence.Contains("hygiene=PASS", StringComparison.OrdinalIgnoreCase) &&
            category.Evidence.Contains("publishValidation=PASS", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FailedTestEvidenceMakesBuildCategoryNoGo()
    {
        BuildTestEvidenceService.CreateLocalEvidence(testStatus: "FAIL", operatorId: "TestAdmin [Admin]");

        var report = FactoryReadinessService.Evaluate(new FactoryReadinessCriteria
        {
            DeploymentProfile = DeploymentProfile.Stage1ImageValidation,
            Stage1Only = true,
            RequireSuccessfulLatestValidationPackage = false,
            RequireDatasetQualityEvidence = false,
            RequireNoExportVerificationErrors = false,
        });

        Assert.Equal("NoGo", report.OverallStatus);
        Assert.Contains(report.Categories, category =>
            category.Name == "Build/Test status" &&
            category.Status == "No-Go" &&
            category.Evidence.Contains("test=FAIL", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExportedFactoryReadinessPackageIncludesBuildEvidenceJson()
    {
        BuildTestEvidenceService.CreateLocalEvidence(testResultPath: Path.Combine(_root, "TestResults", "test-results.trx"), operatorId: "TestAdmin [Admin]");

        var result = FactoryReadinessService.ExportGoNoGoPackage(new FactoryReadinessCriteria
        {
            DeploymentProfile = DeploymentProfile.Stage1ImageValidation,
            Stage1Only = true,
            RequireSuccessfulLatestValidationPackage = false,
            RequireDatasetQualityEvidence = false,
            RequireNoExportVerificationErrors = false,
        }, _root);

        Assert.True(File.Exists(Path.Combine(result.PackageFolder, "build_test_evidence", "latest_build_test_evidence.json")));
        Assert.True(File.Exists(Path.Combine(result.PackageFolder, "build_test_evidence", "latest_build_test_evidence_summary.json")));
    }

    [Fact]
    public void ExportedFactoryReadinessPackagesContainExpectedEvidenceFilesAndChecksums()
    {
        BuildTestEvidenceService.CreateLocalEvidence(testResultPath: Path.Combine(_root, "TestResults", "test-results.trx"), operatorId: "TestAdmin [Admin]");
        RecordStage1Package("PASS");
        RecordOkExportVerification();

        foreach (var profile in new[] { DeploymentProfile.Stage1ImageValidation, DeploymentProfile.Stage2CameraPilot, DeploymentProfile.FullFactoryAutomation })
        {
            var result = FactoryReadinessService.ExportGoNoGoPackage(
                FactoryReadinessService.CriteriaForProfile(profile),
                Path.Combine(_root, "readiness_packages", profile.ToString()));

            AssertReadinessPackageFiles(result.PackageFolder);
        }
    }

    [Fact]
    public void MissingProductionModelCausesNoGoWhenRequired()
    {
        var report = FactoryReadinessService.Evaluate(new FactoryReadinessCriteria
        {
            DeploymentProfile = DeploymentProfile.FullFactoryAutomation,
            Stage1Only = false,
            RequireProductionModel = true,
            RequireSuccessfulLatestValidationPackage = false,
            RequireDatasetQualityEvidence = false,
        });

        Assert.Equal("NoGo", report.OverallStatus);
        Assert.Contains(report.Categories, category =>
            category.Name == "Active model readiness" &&
            category.Status == "No-Go");
    }

    [Fact]
    public void SimulatedOnlyHardwareCannotSatisfyProductionHardwareCriteria()
    {
        AoiDatabase.RecordCameraAcceptanceRun(new CameraAcceptanceRun
        {
            CreatedAtUtc = DateTime.UtcNow,
            AdapterName = "Folder Simulation",
            SourceKey = "folder",
            Status = "PASS",
            FactoryReadinessStatus = "NOT VALIDATED",
            IsRealHardware = false,
            TotalRequestedFrames = 5,
            TotalReceivedFrames = 5,
        });
        AoiDatabase.RecordLightingAcceptanceRun(new LightingAcceptanceRun
        {
            CreatedAtUtc = DateTime.UtcNow,
            ControllerName = "Simulated Lighting",
            Mode = LightingModes.Simulated,
            Status = "PASS",
            IsSimulated = true,
            StepCount = 1,
            PassedStepCount = 1,
        });
        AoiDatabase.RecordRobotAcceptanceRun(new RobotAcceptanceRun
        {
            CreatedAtUtc = DateTime.UtcNow,
            ControllerName = "Simulated Robot",
            SourceKind = "Simulated",
            Status = "PASS",
            FullCycleMs = 100,
            ResetReturnedIdle = true,
            EmergencyStopBlocked = true,
            InvalidTransitionRejected = true,
        });

        var report = FactoryReadinessService.Evaluate(new FactoryReadinessCriteria
        {
            DeploymentProfile = DeploymentProfile.FullFactoryAutomation,
            Stage1Only = false,
            RequireSuccessfulLatestValidationPackage = false,
            RequireDatasetQualityEvidence = false,
            RequireCameraAcceptance = true,
            RequireLightingAcceptance = true,
            RequireRobotAcceptance = true,
            RequireRealHardwareAcceptance = true,
        });

        Assert.Equal("NoGo", report.OverallStatus);
        Assert.Contains(report.Categories, category => category.Name == "Camera acceptance status" && category.Status == "No-Go");
        Assert.Contains(report.Categories, category => category.Name == "Lighting sync status" && category.Status == "No-Go");
        Assert.Contains(report.Categories, category => category.Name == "Robot acceptance status" && category.Status == "No-Go");
    }

    [Fact]
    public void SimulationOnlyProfile3DEvidenceCannotSatisfyRealHardwareReadiness()
    {
        AoiDatabase.RecordProfile3DAcceptanceRun(new Profile3DAcceptanceRun
        {
            CreatedAtUtc = DateTime.UtcNow,
            SourceName = "Sample CSV 3D Profile Source",
            SourceKind = "CSV Sample",
            IsSimulated = true,
            Status = "WARN",
            FactoryReadinessStatus = "NOT VALIDATED",
            FrameId = "CSV-SAMPLE",
            Width = 4,
            Height = 4,
            Unit = "microns",
            XPitchMicrons = 1,
            YPitchMicrons = 1,
            Warnings = new() { "Simulation/sample CSV evidence only; no real 3D camera hardware was validated." },
        });

        var report = FactoryReadinessService.Evaluate(new FactoryReadinessCriteria
        {
            DeploymentProfile = DeploymentProfile.FullFactoryAutomation,
            Stage1Only = false,
            RequireSuccessfulLatestValidationPackage = false,
            RequireDatasetQualityEvidence = false,
            RequireProfile3DAcceptance = true,
            RequireRealHardwareAcceptance = true,
        });

        Assert.Equal("NoGo", report.OverallStatus);
        Assert.Contains(report.Categories, category =>
            category.Name == "3D profile acceptance status" &&
            category.Status == "No-Go" &&
            category.Evidence.Contains("simulated=True", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void VerifiedStage1PackageWithoutHardwareRequirementIsNotNoGo()
    {
        RecordStage1Package("PASS");
        RecordOkExportVerification();

        var report = FactoryReadinessService.Evaluate(new FactoryReadinessCriteria
        {
            DeploymentProfile = DeploymentProfile.Stage1ImageValidation,
            Stage1Only = true,
            RequireSuccessfulLatestValidationPackage = true,
            RequireNoExportVerificationErrors = true,
        });

        Assert.NotEqual("NoGo", report.OverallStatus);
        Assert.Contains(report.Categories, category =>
            category.Name == "Stage 1 validation package status" &&
            category.Status == "Go");
        Assert.Contains(report.Categories, category =>
            category.Name == "Camera acceptance status" &&
            category.Status == "Conditional");
    }

    [Fact]
    public void PendingMesQueueDegradesProductionReadiness()
    {
        EnqueueMesItem();

        var report = FactoryReadinessService.Evaluate(new FactoryReadinessCriteria
        {
            DeploymentProfile = DeploymentProfile.Stage4MesPilot,
            Stage1Only = false,
            RequireSuccessfulLatestValidationPackage = false,
            RequireDatasetQualityEvidence = false,
            RequireNoPendingMesQueueForProductionMode = true,
        });

        Assert.Equal("NoGo", report.OverallStatus);
        Assert.Contains(report.Categories, category =>
            category.Name == "MES/spool status" &&
            category.Status == "No-Go");
    }

    [Fact]
    public void FailedMesQueueMakesStage4ReadinessNoGo()
    {
        var id = EnqueueMesItem(maxRetryCount: 1);
        AoiDatabase.RecordMesSpoolRetryFailure(id, "MES REST upload failed after retry.", retryBackoffMs: 0);

        var report = FactoryReadinessService.Evaluate(new FactoryReadinessCriteria
        {
            DeploymentProfile = DeploymentProfile.Stage4MesPilot,
            Stage1Only = false,
            RequireSuccessfulLatestValidationPackage = false,
            RequireDatasetQualityEvidence = false,
            RequireNoPendingMesQueueForProductionMode = true,
        });

        Assert.Equal("NoGo", report.OverallStatus);
        Assert.Contains(report.Categories, category =>
            category.Name == "MES/spool status" &&
            category.Status == "No-Go" &&
            category.Evidence.Contains("failed=1", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Stage4ReadinessIsNoGoWhenMesTraceabilityTestFails()
    {
        AoiDatabase.RecordTraceabilityTestReport(new TraceabilityTestReport
        {
            CreatedAtUtc = DateTime.UtcNow,
            Status = "FAIL",
            Mode = "REST",
            EndpointUrl = "http://mes.test/api/aoi/results",
            ResultStatus = "FAIL",
            ImageStatus = "NOT SENT",
            Message = "Response schema validation failed.",
            OperatorId = "Engineer01 [Engineer]",
        });

        var report = FactoryReadinessService.Evaluate(FactoryReadinessService.CriteriaForProfile(DeploymentProfile.Stage4MesPilot));

        Assert.Equal("NoGo", report.OverallStatus);
        Assert.Contains(report.Categories, category =>
            category.Name == "MES/spool status" &&
            category.Status == "No-Go" &&
            category.Evidence.Contains("traceability=FAIL", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void JsonReportContainsAllReadinessCategories()
    {
        RecordStage1Package("PASS");
        RecordOkExportVerification();

        var result = FactoryReadinessService.ExportGoNoGoPackage(new FactoryReadinessCriteria
        {
            DeploymentProfile = DeploymentProfile.Stage1ImageValidation,
            Stage1Only = true,
            RequireSuccessfulLatestValidationPackage = true,
        }, _root);

        using var document = JsonDocument.Parse(File.ReadAllText(result.SummaryJsonPath));
        var categories = document.RootElement.GetProperty("categories").EnumerateArray().ToArray();
        var names = categories
            .Select(item => item.GetProperty("name").GetString())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.True(File.Exists(Path.Combine(result.PackageFolder, "package_manifest.json")));
        Assert.Equal(22, categories.Length);
        Assert.Contains("Operating mode", names);
        Assert.Contains("Pilot issue status", names);
        Assert.Contains("Build/Test status", names);
        Assert.Contains("Inspection latency trace", names);
        Assert.Contains("3D profile acceptance status", names);
        Assert.Contains("MES/spool status", names);
        Assert.Contains("Central sync status", names);
        Assert.Contains("Alarm/event status", names);
        Assert.Contains("Authentication mode", names);
        Assert.Contains("Known limitations", names);
        Assert.Equal("Stage1ImageValidation", document.RootElement.GetProperty("deploymentProfile").GetString());
        Assert.True(document.RootElement.TryGetProperty("unmetCriteria", out _));
    }

    [Fact]
    public void FullFactoryAutomationWithoutEightHourSoakRunHasNoGoSoakCategory()
    {
        var report = FactoryReadinessService.Evaluate(FactoryReadinessService.CriteriaForProfile(DeploymentProfile.FullFactoryAutomation));

        Assert.Contains(report.Categories, category =>
            category.Name == "Soak test status" &&
            category.Status == "No-Go" &&
            category.Evidence.Contains("No soak-test", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Stage1FactoryAcceptanceChecklistDoesNotRequireRobotOrMes()
    {
        var checklist = FactoryAcceptanceChecklistService.Generate(DeploymentProfile.Stage1ImageValidation);

        Assert.Contains(checklist.Items, item => item.RequirementId == "S3-ROBOT-001" && item.Status == "Waived");
        Assert.Contains(checklist.Items, item => item.RequirementId == "S4-MES-001" && item.Status == "Waived");
        Assert.DoesNotContain(checklist.Items, item =>
            (item.RequirementId == "S3-ROBOT-001" || item.RequirementId == "S4-MES-001") &&
            item.Status is "Open" or "Failed");
    }

    [Fact]
    public void FullFactoryAutomationChecklistRequiresAllStages()
    {
        var checklist = FactoryAcceptanceChecklistService.Generate(DeploymentProfile.FullFactoryAutomation);
        var requiredIds = new[] { "S1-001", "S2-CAM-001", "S2-LIGHT-001", "S3-ROBOT-001", "S4-MES-001", "TR-009" };

        foreach (var id in requiredIds)
            Assert.Contains(checklist.Items, item => item.RequirementId == id && item.Status != "Waived");
    }

    [Fact]
    public void MissingFactoryAcceptanceEvidenceItemsBecomeOpenNoGo()
    {
        var checklist = FactoryAcceptanceChecklistService.Generate(DeploymentProfile.FullFactoryAutomation);

        Assert.Contains(checklist.Items, item => item.RequirementId == "S2-CAM-001" && item.Status == "Open");
        Assert.Contains(checklist.Items, item => item.RequirementId == "TR-009" && item.Status == "Open");
    }

    [Fact]
    public void ExportedFactoryAcceptanceChecklistContainsRequirementIds()
    {
        var export = FactoryAcceptanceChecklistService.Export(DeploymentProfile.FullFactoryAutomation, _root);
        var json = File.ReadAllText(export.JsonPath);
        var csv = File.ReadAllText(export.CsvPath);
        var html = File.ReadAllText(export.HtmlPath);

        foreach (var id in new[] { "GUI-001", "TR-010", "BT-001", "S1-DATA-001", "S1-MODEL-001", "S1-001", "S2-CAM-001", "S2-007", "S3-ROBOT-001", "S4-MES-001", "TR-009", "CENTRAL-SYNC-001", "FA-001" })
        {
            Assert.Contains(id, json);
            Assert.Contains(id, csv);
            Assert.Contains(id, html);
        }
    }

    [Fact]
    public void SameEvidenceProducesDifferentReadinessByDeploymentProfile()
    {
        RecordStage1Package("PASS");
        RecordOkExportVerification();

        var stage1 = FactoryReadinessService.Evaluate(FactoryReadinessService.CriteriaForProfile(DeploymentProfile.Stage1ImageValidation));
        var stage2 = FactoryReadinessService.Evaluate(FactoryReadinessService.CriteriaForProfile(DeploymentProfile.Stage2CameraPilot));

        Assert.NotEqual("NoGo", stage1.OverallStatus);
        Assert.Equal("NoGo", stage2.OverallStatus);
        Assert.Equal("Stage1ImageValidation", stage1.DeploymentProfile);
        Assert.Equal("Stage2CameraPilot", stage2.DeploymentProfile);
        Assert.Contains(stage2.Categories, category => category.Name == "Camera acceptance status" && category.Status == "No-Go");
    }

    [Fact]
    public void Stage2CameraPilotIsNoGoWithoutCameraLightingAnd3DEvidence()
    {
        RecordStage1Package("PASS");
        RecordOkExportVerification();

        var report = FactoryReadinessService.Evaluate(FactoryReadinessService.CriteriaForProfile(DeploymentProfile.Stage2CameraPilot));

        Assert.Equal("NoGo", report.OverallStatus);
        Assert.Contains(report.Categories, category => category.Name == "Camera acceptance status" && category.Status == "No-Go");
        Assert.Contains(report.Categories, category => category.Name == "Lighting sync status" && category.Status == "No-Go");
        Assert.Contains(report.Categories, category => category.Name == "3D profile acceptance status" && category.Status == "No-Go");
    }

    [Fact]
    public void Stage2CameraPilotEvidencePackageRejectsSimulatedCameraForRealHardwareReadiness()
    {
        RecordStage1Package("PASS");
        RecordOkExportVerification();
        RecordSimulatedCameraAcceptance();
        RecordRealLightingAcceptance();
        RecordRealProfile3DAcceptance();

        var result = Stage2CameraPilotEvidencePackageService.CreatePackage(new Stage2CameraPilotEvidenceRequest
        {
            OutputFolder = Path.Combine(_root, "stage2_packages"),
            OperatorId = "TestAdmin [Admin]",
        });

        Assert.Equal("NO-GO", result.Status);
        Assert.Equal("NOT VALIDATED", result.CameraEvidenceStatus);
        Assert.True(result.SimulationEvidencePresent);
        Assert.False(result.RealHardwareValidated);
        Assert.Contains(result.BlockingIssues, issue => issue.Contains("real vendor camera acceptance", StringComparison.OrdinalIgnoreCase));

        using var document = JsonDocument.Parse(File.ReadAllText(result.ManifestPath));
        Assert.Equal("Stage2CameraPilot", document.RootElement.GetProperty("deploymentProfile").GetString());
        Assert.Equal("NOT VALIDATED", document.RootElement.GetProperty("cameraEvidenceStatus").GetString());
        Assert.True(document.RootElement.TryGetProperty("profile3dEvidenceStatus", out _));
        Assert.True(document.RootElement.GetProperty("simulationEvidencePresent").GetBoolean());
        Assert.False(document.RootElement.GetProperty("realHardwareValidated").GetBoolean());
        Assert.NotEmpty(document.RootElement.GetProperty("includedFiles").EnumerateArray());
    }

    [Fact]
    public void Stage2CameraPilotEvidencePackageWithSimulationFlagIsDryRunOnly()
    {
        RecordStage1Package("PASS");
        RecordOkExportVerification();
        RecordSimulatedCameraAcceptance();
        AoiDatabase.RecordLightingAcceptanceRun(new LightingAcceptanceRun
        {
            CreatedAtUtc = DateTime.UtcNow,
            ControllerName = "Simulated Lighting",
            Mode = LightingModes.Simulated,
            Status = "PASS",
            IsSimulated = true,
            StepCount = 1,
            PassedStepCount = 1,
            Warnings = new() { "Lighting acceptance was run in simulated mode." },
        });
        AoiDatabase.RecordProfile3DAcceptanceRun(new Profile3DAcceptanceRun
        {
            CreatedAtUtc = DateTime.UtcNow,
            SourceName = "CSV Sample 3D Source",
            SourceKind = "CSV Sample",
            IsSimulated = true,
            Status = "WARN",
            FactoryReadinessStatus = "NOT VALIDATED",
            FrameId = "SIM-3D-001",
            Width = 4,
            Height = 4,
            Unit = "microns",
            XPitchMicrons = 1,
            YPitchMicrons = 1,
            Warnings = new() { "Simulation/sample CSV evidence only." },
        });

        var result = Stage2CameraPilotEvidencePackageService.CreatePackage(new Stage2CameraPilotEvidenceRequest
        {
            OutputFolder = Path.Combine(_root, "stage2_packages"),
            OperatorId = "TestAdmin [Admin]",
            AllowSimulation = true,
        });

        Assert.Equal("DRY RUN", result.Status);
        Assert.True(result.DryRun);
        Assert.False(result.RealHardwareValidated);
        Assert.Contains(Stage2CameraPilotEvidencePackageService.SimulationNotice, File.ReadAllText(result.ReadmePath));
        Assert.Contains(Stage2CameraPilotEvidencePackageService.SimulationNotice, File.ReadAllText(result.ManifestPath));
    }

    [Fact]
    public void Stage2CameraPilotSimulationFlagCreatesDryRunEvidenceOnCleanStorage()
    {
        RecordStage1Package("PASS");
        RecordOkExportVerification();

        var result = Stage2CameraPilotEvidencePackageService.CreatePackage(new Stage2CameraPilotEvidenceRequest
        {
            OutputFolder = Path.Combine(_root, "stage2_clean_simulation_packages"),
            OperatorId = "TestAdmin [Admin]",
            AllowSimulation = true,
        });

        Assert.Equal("DRY RUN", result.Status);
        Assert.True(result.DryRun);
        Assert.Equal("NOT VALIDATED", result.CameraEvidenceStatus);
        Assert.Equal("NOT VALIDATED", result.LightingEvidenceStatus);
        Assert.Equal("NOT VALIDATED", result.Profile3DEvidenceStatus);
        Assert.True(result.SimulationEvidencePresent);
        Assert.False(result.RealHardwareValidated);
        Assert.Contains(Stage2CameraPilotEvidencePackageService.SimulationNotice, File.ReadAllText(result.ReadmePath));
        Assert.Contains(Stage2CameraPilotEvidencePackageService.SimulationNotice, File.ReadAllText(result.ManifestPath));
        Assert.False(AoiDatabase.GetLatestCameraAcceptanceRun(realHardwareOnly: false)?.IsRealHardware ?? true);
        Assert.True(AoiDatabase.GetLatestLightingAcceptanceRun()?.IsSimulated ?? false);
        Assert.True(AoiDatabase.GetLatestProfile3DAcceptanceRun()?.IsSimulated ?? false);
    }

    [Fact]
    public void Stage1CanPassWithoutRealRobotOrMes()
    {
        RecordStage1Package("PASS");
        RecordOkExportVerification();
        MesIntegrationSettingsService.Save(new MesIntegrationSettings { Mode = MesIntegrationMode.NotConnected });

        var report = FactoryReadinessService.Evaluate(FactoryReadinessService.CriteriaForProfile(DeploymentProfile.Stage1ImageValidation));

        Assert.NotEqual("NoGo", report.OverallStatus);
        Assert.Contains(report.Categories, category => category.Name == "Robot acceptance status" && category.Status == "Conditional");
        Assert.Contains(report.Categories, category => category.Name == "MES/spool status" && category.Status == "Conditional");
    }

    [Fact]
    public void Stage1PackageCanBeConditionalOrGoWithoutRealHardware()
    {
        RecordStage1Package("PASS");
        RecordOkExportVerification();

        var report = FactoryReadinessService.Evaluate(FactoryReadinessService.CriteriaForProfile(DeploymentProfile.Stage1ImageValidation));

        Assert.Contains(report.OverallStatus, new[] { "Conditional", "Go" });
        Assert.DoesNotContain(report.BlockingIssues, issue => issue.Contains("Camera acceptance", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(report.BlockingIssues, issue => issue.Contains("Lighting sync", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(report.BlockingIssues, issue => issue.Contains("3D profile", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(report.Categories, category => category.Name == "Known limitations" && category.Evidence.Contains("Stage 1 package does not validate production camera", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FullFactoryAutomationCannotPassWithSimulatedHardwareOnly()
    {
        RecordStage1Package("PASS");
        RecordOkExportVerification();
        AoiDatabase.RecordCameraAcceptanceRun(new CameraAcceptanceRun
        {
            CreatedAtUtc = DateTime.UtcNow,
            AdapterName = "Folder Simulation",
            SourceKey = "folder",
            Status = "PASS",
            FactoryReadinessStatus = "NOT VALIDATED",
            IsRealHardware = false,
            TotalRequestedFrames = 5,
            TotalReceivedFrames = 5,
        });
        AoiDatabase.RecordLightingAcceptanceRun(new LightingAcceptanceRun
        {
            CreatedAtUtc = DateTime.UtcNow,
            ControllerName = "Simulated Lighting",
            Mode = LightingModes.Simulated,
            Status = "PASS",
            IsSimulated = true,
            StepCount = 1,
            PassedStepCount = 1,
        });
        AoiDatabase.RecordRobotAcceptanceRun(new RobotAcceptanceRun
        {
            CreatedAtUtc = DateTime.UtcNow,
            ControllerName = "Simulated Robot",
            SourceKind = "Simulated",
            Status = "PASS",
            FullCycleMs = 100,
            ResetReturnedIdle = true,
            EmergencyStopBlocked = true,
            InvalidTransitionRejected = true,
        });

        var report = FactoryReadinessService.Evaluate(FactoryReadinessService.CriteriaForProfile(DeploymentProfile.FullFactoryAutomation));

        Assert.Equal("NoGo", report.OverallStatus);
        Assert.Contains(report.Categories, category => category.Name == "Camera acceptance status" && category.Status == "No-Go");
        Assert.Contains(report.Categories, category => category.Name == "Lighting sync status" && category.Status == "No-Go");
        Assert.Contains(report.Categories, category => category.Name == "Robot acceptance status" && category.Status == "No-Go");
    }

    [Fact]
    public void FullFactoryAutomationIsNoGoWithoutProductionModelHardwareMesCentralSyncAndEightHourSoak()
    {
        RecordStage1Package("PASS");
        RecordOkExportVerification();

        var report = FactoryReadinessService.Evaluate(FactoryReadinessService.CriteriaForProfile(DeploymentProfile.FullFactoryAutomation));

        Assert.Equal("NoGo", report.OverallStatus);
        Assert.Contains(report.Categories, category => category.Name == "Active model readiness" && category.Status == "No-Go");
        Assert.Contains(report.Categories, category => category.Name == "Camera acceptance status" && category.Status == "No-Go");
        Assert.Contains(report.Categories, category => category.Name == "Lighting sync status" && category.Status == "No-Go");
        Assert.Contains(report.Categories, category => category.Name == "3D profile acceptance status" && category.Status == "No-Go");
        Assert.Contains(report.Categories, category => category.Name == "MES/spool status" && category.Status == "No-Go");
        var centralSync = Assert.Single(report.Categories, category => category.Name == "Central sync status");
        Assert.Equal("No-Go", centralSync.Status);
        Assert.Contains(report.Categories, category => category.Name == "Soak test status" && category.Status == "No-Go");
    }

    private static void AssertReadinessPackageFiles(string packageFolder)
    {
        foreach (var fileName in new[]
        {
            "factory_readiness_summary.json",
            "factory_readiness_summary.html",
            "factory_readiness_summary.pdf",
            "standards_traceability_matrix.json",
            "standards_traceability_matrix.html",
            "standards_traceability_matrix.pdf",
            "factory_acceptance_checklist.json",
            "factory_acceptance_checklist.html",
            "factory_acceptance_checklist.pdf",
            "latest_validation_manifest.json",
            "latest_export_verification_report.json",
            "package_manifest.json",
        })
        {
            Assert.True(File.Exists(Path.Combine(packageFolder, fileName)), $"{fileName} missing from {packageFolder}.");
        }

        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(packageFolder, "package_manifest.json")));
        var includedFiles = document.RootElement.GetProperty("includedFiles").EnumerateArray().ToArray();
        Assert.NotEmpty(includedFiles);
        Assert.All(includedFiles, file =>
        {
            Assert.True(file.GetProperty("bytes").GetInt64() > 0);
            Assert.Matches("^[0-9a-f]{64}$", file.GetProperty("sha256").GetString() ?? string.Empty);
        });
    }

    private void RecordStage1Package(string status)
    {
        var packageFolder = Path.Combine(_root, "stage1_package");
        Directory.CreateDirectory(packageFolder);
        var manifestPath = Path.Combine(packageFolder, "validation_manifest.json");
        var manifest = new ValidationPackageManifest
        {
            PackageId = "PKG-STAGE1",
            GeneratedAtUtc = DateTime.UtcNow,
            AcceptanceStatus = status,
            DatasetQualitySummary = new DatasetQualitySummary
            {
                Status = "PASS",
                TotalImages = 60,
                KnownGroundTruthImages = 60,
                OkImages = 30,
                NgImages = 30,
                DefectClassCount = 2,
            },
        };
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        AoiDatabase.RecordValidationPackage("PKG-STAGE1", packageFolder, manifestPath, status, "Synthetic Stage 1 package", operatorId: "TestAdmin [Admin]");
    }

    private void RecordOkExportVerification()
    {
        var path = Path.Combine(_root, "verified.txt");
        File.WriteAllText(path, "verified");
        ExportVerificationService.RecordVerifiedExport("FactoryReadinessEvidenceText", path);
    }

    private static void RecordSimulatedCameraAcceptance()
    {
        AoiDatabase.RecordCameraAcceptanceRun(new CameraAcceptanceRun
        {
            CreatedAtUtc = DateTime.UtcNow,
            AdapterName = "Folder Simulation",
            SourceKey = "folder",
            Status = "PASS",
            FactoryReadinessStatus = "NOT VALIDATED",
            IsRealHardware = false,
            TotalRequestedFrames = 5,
            TotalReceivedFrames = 5,
            Frames =
            {
                new CameraAcceptanceFrameRecord
                {
                    ViewType = "Top",
                    Sequence = 1,
                    FrameId = "SIM-CAM-001",
                    CameraId = "SIM",
                    Width = 32,
                    Height = 32,
                    PixelFormat = "Mono8",
                    SourceKind = "FolderSimulation",
                    IsSimulated = true,
                    MetadataValid = true,
                },
            },
            Warnings = new() { "Folder simulation evidence only." },
        });
    }

    private static void RecordRealLightingAcceptance()
    {
        AoiDatabase.RecordLightingAcceptanceRun(new LightingAcceptanceRun
        {
            CreatedAtUtc = DateTime.UtcNow,
            ControllerName = "Vendor Lighting Controller",
            Mode = LightingModes.TcpText,
            Status = "PASS",
            IsSimulated = false,
            StepCount = 1,
            PassedStepCount = 1,
            Steps =
            {
                new LightingAcceptanceStep
                {
                    ViewType = "Top",
                    ProgramName = "TOP",
                    CommandAccepted = true,
                    FrameReceived = true,
                    FrameId = "REAL-CAM-001",
                    CameraId = "CAM-001",
                    Status = "PASS",
                    Message = "Real lighting command and camera frame evidence recorded.",
                },
            },
        });
    }

    private static void RecordRealProfile3DAcceptance()
    {
        AoiDatabase.RecordProfile3DAcceptanceRun(new Profile3DAcceptanceRun
        {
            CreatedAtUtc = DateTime.UtcNow,
            SourceName = "Vendor 3D Profile Camera",
            SourceKind = "Real Hardware",
            IsSimulated = false,
            Status = "PASS",
            FactoryReadinessStatus = "PASS",
            FrameId = "REAL-3D-001",
            Width = 4,
            Height = 4,
            Unit = "microns",
            XPitchMicrons = 1,
            YPitchMicrons = 1,
        });
    }

    private static long EnqueueMesItem(int maxRetryCount = 3)
    {
        var payload = new TraceabilityPayload
        {
            LotId = "LOT-PENDING",
            BoardModel = "TBOX",
            Result = "NG",
        };
        return AoiDatabase.EnqueueMesSpoolItem(
            nameof(TraceabilityPayload),
            JsonSerializer.Serialize(payload),
            @"C:\payloads\traceability.json",
            "http://mes.test/api/aoi/results",
            maxRetryCount,
            "Synthetic pending upload",
            "TestAdmin [Admin]",
            payload.LotId,
            payload.BoardModel,
            payload.Result);
    }
}
