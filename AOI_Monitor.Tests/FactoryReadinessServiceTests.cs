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
        DeploymentProfileSettingsService.ResetForTests();
    }

    public void Dispose()
    {
        DeploymentProfileSettingsService.ResetForTests();
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
        Assert.Equal(17, categories.Length);
        Assert.Contains("Build/Test status", names);
        Assert.Contains("3D profile acceptance status", names);
        Assert.Contains("MES/spool status", names);
        Assert.Contains("Central sync status", names);
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

        Assert.Contains(checklist.Items, item => item.RequirementId == "S3-ROBOT-001" && item.Status == "Not Required");
        Assert.Contains(checklist.Items, item => item.RequirementId == "S4-MES-001" && item.Status == "Not Required");
        Assert.DoesNotContain(checklist.Items, item =>
            (item.RequirementId == "S3-ROBOT-001" || item.RequirementId == "S4-MES-001") &&
            item.Status.Contains("No-Go", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FullFactoryAutomationChecklistRequiresAllStages()
    {
        var checklist = FactoryAcceptanceChecklistService.Generate(DeploymentProfile.FullFactoryAutomation);
        var requiredIds = new[] { "S1-001", "S2-CAM-001", "S2-LIGHT-001", "S3-ROBOT-001", "S4-MES-001", "TR-009" };

        foreach (var id in requiredIds)
            Assert.Contains(checklist.Items, item => item.RequirementId == id && item.Status != "Not Required");
    }

    [Fact]
    public void MissingFactoryAcceptanceEvidenceItemsBecomeOpenNoGo()
    {
        var checklist = FactoryAcceptanceChecklistService.Generate(DeploymentProfile.FullFactoryAutomation);

        Assert.Contains(checklist.Items, item => item.RequirementId == "S2-CAM-001" && item.Status == "Open/No-Go");
        Assert.Contains(checklist.Items, item => item.RequirementId == "TR-009" && item.Status == "No-Go");
    }

    [Fact]
    public void ExportedFactoryAcceptanceChecklistContainsRequirementIds()
    {
        var export = FactoryAcceptanceChecklistService.Export(DeploymentProfile.FullFactoryAutomation, _root);
        var json = File.ReadAllText(export.JsonPath);
        var csv = File.ReadAllText(export.CsvPath);
        var html = File.ReadAllText(export.HtmlPath);

        foreach (var id in new[] { "GUI-001", "PERF-001", "BT-001", "S1-001", "S2-CAM-001", "S3-ROBOT-001", "S4-MES-001", "TR-009", "FA-001" })
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

    private static void EnqueueMesItem()
    {
        var payload = new TraceabilityPayload
        {
            LotId = "LOT-PENDING",
            BoardModel = "TBOX",
            Result = "NG",
        };
        AoiDatabase.EnqueueMesSpoolItem(
            nameof(TraceabilityPayload),
            JsonSerializer.Serialize(payload),
            @"C:\payloads\traceability.json",
            "http://mes.test/api/aoi/results",
            3,
            "Synthetic pending upload",
            "TestAdmin [Admin]",
            payload.LotId,
            payload.BoardModel,
            payload.Result);
    }
}
