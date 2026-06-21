using System.Text.Json;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Xunit;

namespace AOI_Monitor.Tests;

public sealed class OperatingModeServiceTests : IDisposable
{
    private readonly string _root;

    public OperatingModeServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AOI_Monitor_OperatingMode_Tests", Guid.NewGuid().ToString("N"));
        AoiDatabase.ConfigureStorageRoot(_root);
        AoiDatabase.Initialize();
        AuthenticationSettingsService.ResetForTests();
        OperatingModeSettingsService.ResetForTests();
        DeploymentProfileSettingsService.ResetForTests();
        MesIntegrationSettingsService.Save(new MesIntegrationSettings());
        CentralSyncSettingsService.ResetForTests();
        CentralSyncSettingsService.Save(new CentralSyncSettings());
    }

    public void Dispose()
    {
        AuthenticationSettingsService.ResetForTests();
        OperatingModeSettingsService.ResetForTests();
        DeploymentProfileSettingsService.ResetForTests();
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
    public void DemoRowsHiddenInPilotAndProduction()
    {
        Assert.True(OperatingModePolicyService.ShouldShowDemoRows(0, OperatingMode.Demo));
        Assert.False(OperatingModePolicyService.ShouldShowDemoRows(0, OperatingMode.Pilot));
        Assert.False(OperatingModePolicyService.ShouldShowDemoRows(0, OperatingMode.Production));
        Assert.False(OperatingModePolicyService.ShouldShowDemoRows(4, OperatingMode.Demo));
    }

    [Fact]
    public void DemoRoleSelectorDisabledInProduction()
    {
        Assert.True(OperatingModePolicyService.IsDemoRoleSelectorAllowed(OperatingMode.Demo));
        Assert.False(OperatingModePolicyService.IsDemoRoleSelectorAllowed(OperatingMode.Pilot));
        Assert.False(OperatingModePolicyService.IsDemoRoleSelectorAllowed(OperatingMode.Production));
    }

    [Fact]
    public void FactoryReadinessFailsProductionModeWithoutAuthModelHardwareAndMes()
    {
        OperatingModeSettingsService.Save(OperatingMode.Production, "Admin01 [Admin]");
        AuthenticationSettingsService.Save(new AuthenticationSettings { Mode = AuthenticationMode.DemoLocalRoleSelector }, "Admin01 [Admin]");
        RecordStage1Package("PASS");
        RecordOkExportVerification();

        var report = FactoryReadinessService.Evaluate(new FactoryReadinessCriteria
        {
            DeploymentProfile = DeploymentProfile.Stage1ImageValidation,
            Stage1Only = true,
            RequireSuccessfulLatestValidationPackage = false,
            RequireDatasetQualityEvidence = false,
            RequireNoExportVerificationErrors = false,
        });

        Assert.Equal("NoGo", report.OverallStatus);
        Assert.Contains(report.Categories, category => category.Name == "Operating mode" && category.Status == "Go");
        Assert.Contains(report.Categories, category => category.Name == "Authentication mode" && category.Status == "No-Go");
        Assert.Contains(report.Categories, category => category.Name == "Active model readiness" && category.Status == "No-Go");
        Assert.Contains(report.Categories, category => category.Name == "Camera acceptance status" && category.Status == "No-Go");
        Assert.Contains(report.Categories, category => category.Name == "MES/spool status" && category.Status == "No-Go");
    }

    [Fact]
    public void SimulatedHardwareAllowedInDemoButNoGoInProduction()
    {
        RecordSimulatedHardwarePasses();

        OperatingModeSettingsService.Save(OperatingMode.Demo, "Admin01 [Admin]");
        var demo = FactoryReadinessService.Evaluate(new FactoryReadinessCriteria
        {
            DeploymentProfile = DeploymentProfile.Stage2CameraPilot,
            Stage1Only = false,
            RequireCameraAcceptance = true,
            RequireLightingAcceptance = true,
            RequireRobotAcceptance = true,
            RequireRealHardwareAcceptance = false,
            RequireSuccessfulLatestValidationPackage = false,
            RequireDatasetQualityEvidence = false,
            RequireNoExportVerificationErrors = false,
        });

        Assert.Contains(demo.Categories, category => category.Name == "Camera acceptance status" && category.Status == "Go");
        Assert.Contains(demo.Categories, category => category.Name == "Lighting sync status" && category.Status == "Go");
        Assert.Contains(demo.Categories, category => category.Name == "Robot acceptance status" && category.Status == "Go");

        OperatingModeSettingsService.Save(OperatingMode.Production, "Admin01 [Admin]");
        var production = FactoryReadinessService.Evaluate(new FactoryReadinessCriteria
        {
            DeploymentProfile = DeploymentProfile.Stage2CameraPilot,
            Stage1Only = false,
            RequireCameraAcceptance = true,
            RequireLightingAcceptance = true,
            RequireRobotAcceptance = true,
            RequireRealHardwareAcceptance = false,
            RequireSuccessfulLatestValidationPackage = false,
            RequireDatasetQualityEvidence = false,
            RequireNoExportVerificationErrors = false,
        });

        Assert.Equal("NoGo", production.OverallStatus);
        Assert.Contains(production.Categories, category => category.Name == "Camera acceptance status" && category.Status == "No-Go");
        Assert.Contains(production.Categories, category => category.Name == "Lighting sync status" && category.Status == "No-Go");
        Assert.Contains(production.Categories, category => category.Name == "Robot acceptance status" && category.Status == "No-Go");
    }

    private void RecordStage1Package(string status)
    {
        var packageFolder = Path.Combine(_root, "stage1_package");
        Directory.CreateDirectory(packageFolder);
        var manifestPath = Path.Combine(packageFolder, "validation_manifest.json");
        var manifest = new ValidationPackageManifest
        {
            PackageId = "PKG-OPERATING-MODE",
            GeneratedAtUtc = DateTime.UtcNow,
            AcceptanceStatus = status,
            DatasetQualitySummary = new DatasetQualitySummary
            {
                Status = "PASS",
                TotalImages = 40,
                KnownGroundTruthImages = 40,
                OkImages = 20,
                NgImages = 20,
                DefectClassCount = 2,
            },
        };
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest));
        AoiDatabase.RecordValidationPackage("PKG-OPERATING-MODE", packageFolder, manifestPath, status, "Synthetic Stage 1 package", operatorId: "Admin01 [Admin]");
    }

    private void RecordOkExportVerification()
    {
        var path = Path.Combine(_root, "verified.txt");
        File.WriteAllText(path, "verified");
        ExportVerificationService.RecordVerifiedExport("OperatingModeEvidence", path);
    }

    private static void RecordSimulatedHardwarePasses()
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
            SafetyControllerName = "Simulated PLC",
            SafetySourceKind = "Simulated",
            Status = "PASS",
            FullCycleMs = 100,
            ResetReturnedIdle = true,
            EmergencyStopBlocked = true,
            SafetyFaultBlocked = true,
            InvalidTransitionRejected = true,
        });
    }
}
