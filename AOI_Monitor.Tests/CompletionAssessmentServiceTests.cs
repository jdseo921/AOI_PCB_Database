using System.Text.Json;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Xunit;

namespace AOI_Monitor.Tests;

public sealed class CompletionAssessmentServiceTests : IDisposable
{
    private readonly string _root;

    public CompletionAssessmentServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AOI_Monitor_CompletionAssessment_Tests", Guid.NewGuid().ToString("N"));
        AoiDatabase.ConfigureStorageRoot(_root);
        AoiDatabase.Initialize();
        AuthenticationSettingsService.ResetForTests();
        DeploymentProfileSettingsService.ResetForTests();
        FirstRunSettingsService.ResetForTests();
        MesIntegrationSettingsService.Save(new MesIntegrationSettings());
        InspectionModelConfigurationService.Save(new InspectionModelConfiguration());
    }

    public void Dispose()
    {
        AuthenticationSettingsService.ResetForTests();
        DeploymentProfileSettingsService.ResetForTests();
        FirstRunSettingsService.ResetForTests();
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
    public void EmptyLocalEvidenceGivesLowScore()
    {
        var report = CompletionAssessmentService.Assess();

        Assert.Equal(7, report.Categories.Count);
        Assert.True(report.OverallPercent < 30, $"Expected low empty evidence score, got {report.OverallPercent:F1}%.");
        Assert.All(report.Categories, category => Assert.True(category.PercentComplete < 50, $"{category.Stage} was unexpectedly high at {category.PercentComplete:F1}%."));
    }

    [Fact]
    public void Stage1ValidationPackageIncreasesOnlyStage1Score()
    {
        var before = CompletionAssessmentService.Assess().Categories.ToDictionary(category => category.Stage, category => category.PercentComplete);

        RecordStage1Package("PASS");

        var after = CompletionAssessmentService.Assess().Categories.ToDictionary(category => category.Stage, category => category.PercentComplete);

        Assert.True(after["Stage 1 image validation"] > before["Stage 1 image validation"]);
        foreach (var stage in before.Keys.Where(stage => stage != "Stage 1 image validation"))
            Assert.Equal(before[stage], after[stage]);
    }

    [Fact]
    public void SimulationHardwareDoesNotSatisfyRealHardwareScore()
    {
        RecordSimulatedHardwareAcceptance();

        var report = CompletionAssessmentService.Assess();
        var stage2 = Find(report, "Stage 2 camera/lighting/3D");
        var stage3 = Find(report, "Stage 3 robot/safety");

        Assert.True(stage2.PercentComplete < 50, $"Simulation should not satisfy Stage 2 real hardware score, got {stage2.PercentComplete:F1}%.");
        Assert.Contains(stage2.MissingEvidence, item => item.Contains("Real camera acceptance PASS", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(stage2.MissingEvidence, item => item.Contains("Real lighting sync acceptance PASS", StringComparison.OrdinalIgnoreCase));
        Assert.True(stage3.PercentComplete < 50, $"Simulation should not satisfy Stage 3 real hardware score, got {stage3.PercentComplete:F1}%.");
        Assert.Contains(stage3.MissingEvidence, item => item.Contains("Real robot cell acceptance PASS", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RealHardwareAcceptanceRecordsIncreaseHardwareScore()
    {
        RecordSimulatedHardwareAcceptance();
        var simulated = CompletionAssessmentService.Assess();
        var simulatedStage2 = Find(simulated, "Stage 2 camera/lighting/3D").PercentComplete;
        var simulatedStage3 = Find(simulated, "Stage 3 robot/safety").PercentComplete;

        RecordRealHardwareAcceptance();

        var real = CompletionAssessmentService.Assess();
        var realStage2 = Find(real, "Stage 2 camera/lighting/3D");
        var realStage3 = Find(real, "Stage 3 robot/safety");

        Assert.True(realStage2.PercentComplete > simulatedStage2);
        Assert.True(realStage2.PercentComplete >= 85);
        Assert.True(realStage3.PercentComplete > simulatedStage3);
        Assert.Equal(100, realStage3.PercentComplete);
        Assert.DoesNotContain(realStage2.MissingEvidence, item => item.Contains("Real camera acceptance PASS", StringComparison.OrdinalIgnoreCase));
    }

    private void RecordStage1Package(string status)
    {
        var packageFolder = Path.Combine(_root, "stage1_package");
        Directory.CreateDirectory(packageFolder);
        var manifestPath = Path.Combine(packageFolder, "validation_manifest.json");
        var manifest = new ValidationPackageManifest
        {
            PackageId = "PKG-COMPLETE-STAGE1",
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
        AoiDatabase.RecordValidationPackage("PKG-COMPLETE-STAGE1", packageFolder, manifestPath, status, "Completion matrix Stage 1 package", operatorId: "TestAdmin [Admin]");
    }

    private static CompletionAssessmentCategory Find(CompletionAssessmentReport report, string stage)
        => Assert.Single(report.Categories, category => category.Stage == stage);

    private static void RecordSimulatedHardwareAcceptance()
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
        AoiDatabase.RecordProfile3DAcceptanceRun(new Profile3DAcceptanceRun
        {
            CreatedAtUtc = DateTime.UtcNow,
            SourceName = "Simulated 3D",
            SourceKind = "CSV Sample",
            IsSimulated = true,
            Status = "PASS",
            FactoryReadinessStatus = "NOT VALIDATED",
            FrameId = "SIM-3D",
            Width = 4,
            Height = 4,
            Unit = "microns",
            XPitchMicrons = 1,
            YPitchMicrons = 1,
        });
        AoiDatabase.RecordRobotAcceptanceRun(new RobotAcceptanceRun
        {
            CreatedAtUtc = DateTime.UtcNow,
            ControllerName = "Simulated Robot",
            SafetyControllerName = "Simulated Safety",
            SourceKind = "Simulated",
            SafetySourceKind = "Simulated",
            Status = "PASS",
            FullCycleMs = 100,
            InvalidTransitionRejected = true,
            EmergencyStopBlocked = true,
            SafetyFaultBlocked = true,
            ResetReturnedIdle = true,
            AuditEventCount = 2,
        });
    }

    private static void RecordRealHardwareAcceptance()
    {
        AoiDatabase.RecordCameraAcceptanceRun(new CameraAcceptanceRun
        {
            CreatedAtUtc = DateTime.UtcNow.AddSeconds(1),
            AdapterName = "Vendor GigE Camera",
            SourceKey = "vendor-gige",
            Status = "PASS",
            FactoryReadinessStatus = "PASS",
            IsRealHardware = true,
            TotalRequestedFrames = 10,
            TotalReceivedFrames = 10,
        });
        AoiDatabase.RecordLightingAcceptanceRun(new LightingAcceptanceRun
        {
            CreatedAtUtc = DateTime.UtcNow.AddSeconds(1),
            ControllerName = "Vendor Lighting Controller",
            Mode = LightingModes.TcpText,
            Status = "PASS",
            IsSimulated = false,
            StepCount = 2,
            PassedStepCount = 2,
        });
        AoiDatabase.RecordProfile3DAcceptanceRun(new Profile3DAcceptanceRun
        {
            CreatedAtUtc = DateTime.UtcNow.AddSeconds(1),
            SourceName = "Vendor 3D Profiler",
            SourceKind = "Vendor3D",
            IsSimulated = false,
            Status = "PASS",
            FactoryReadinessStatus = "PASS",
            FrameId = "REAL-3D",
            Width = 8,
            Height = 8,
            Unit = "microns",
            XPitchMicrons = 5,
            YPitchMicrons = 5,
        });
        AoiDatabase.RecordRobotAcceptanceRun(new RobotAcceptanceRun
        {
            CreatedAtUtc = DateTime.UtcNow.AddSeconds(1),
            ControllerName = "Vendor Robot Cell",
            SafetyControllerName = "PLC Safety",
            SourceKind = "VendorRobot",
            SafetySourceKind = "PLC",
            Status = "PASS",
            FullCycleMs = 250,
            InvalidTransitionRejected = true,
            EmergencyStopBlocked = true,
            SafetyFaultBlocked = true,
            ResetReturnedIdle = true,
            AuditEventCount = 4,
        });
    }
}
