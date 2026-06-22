using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Xunit;

namespace AOI_Monitor.Tests;

public sealed class IndustrialQualityGateServiceTests : IDisposable
{
    private readonly string _root;

    public IndustrialQualityGateServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AOI_Monitor_IndustrialGate_Tests", Guid.NewGuid().ToString("N"));
        StorageRootSettingsService.ConfigureSettingsDirectoryForTests(_root);
        AoiDatabase.ConfigureStorageRoot(_root);
        AoiDatabase.Initialize();
        DeploymentProfileSettingsService.ResetForTests();
        CrashReportService.ClearForTests();
    }

    public void Dispose()
    {
        CrashReportService.ClearForTests();
        DeploymentProfileSettingsService.ResetForTests();
        StorageRootSettingsService.ConfigureSettingsDirectoryForTests(null);
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"Test cleanup failed for {nameof(IndustrialQualityGateServiceTests)}: {ex.Message}");
        }
    }

    [Fact]
    public void GateConfigParses()
    {
        var config = IndustrialQualityGateService.LoadConfig();

        Assert.Equal("industrial-quality-gates/v1", config.SchemaVersion);
        Assert.Contains(config.Gates, gate => gate.Id == "HMI-001");
        Assert.Contains(config.Gates, gate => gate.Id == "PKG-002");
        Assert.Contains("not formal", config.CertificationNotice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingRequiredEvidenceProducesFail()
    {
        var report = IndustrialQualityGateService.Evaluate(
            DeploymentProfile.FullFactoryAutomation,
            new IndustrialQualityEvidence());

        Assert.Equal(IndustrialGateStatus.Fail, report.OverallStatus);
        Assert.Contains(report.Checks, check => check.Id == "HMI-001" && check.Status == IndustrialGateStatus.Fail);
        Assert.Contains(report.Checks, check => check.Id == "EXPORT-001" && check.Status == IndustrialGateStatus.Fail);
    }

    [Fact]
    public void SimulationEvidenceCannotSatisfyRealHardwareGate()
    {
        var evidence = PassingEvidence();
        evidence.RealHardwareEvidence = false;
        evidence.SimulationHardwareEvidence = true;
        evidence.Details["realHardwareEvidence"] = "Simulated camera, lighting, 3D, and robot evidence only.";

        var report = IndustrialQualityGateService.Evaluate(DeploymentProfile.FullFactoryAutomation, evidence);

        var hardware = Assert.Single(report.Checks, check => check.Id == "HW-001");
        Assert.Equal(IndustrialGateStatus.Fail, hardware.Status);
        Assert.Contains("Simulation evidence", hardware.Evidence);
    }

    [Fact]
    public void MissingBuildTestEvidenceWarnsForStage1AndFailsForFactoryProfile()
    {
        var evidence = PassingEvidence();
        evidence.BuildTestEvidence = false;
        evidence.Details["buildTestEvidence"] = "No build/test evidence is recorded.";

        var stage1 = IndustrialQualityGateService.Evaluate(DeploymentProfile.Stage1ImageValidation, evidence);
        var factory = IndustrialQualityGateService.Evaluate(DeploymentProfile.FullFactoryAutomation, evidence);

        Assert.Contains(stage1.Checks, check => check.Id == "PKG-002" && check.Status == IndustrialGateStatus.Warn);
        Assert.Contains(factory.Checks, check => check.Id == "PKG-002" && check.Status == IndustrialGateStatus.Fail);
    }

    private static IndustrialQualityEvidence PassingEvidence()
        => new()
        {
            HmiLayoutEvidence = true,
            AlarmEventEvidence = true,
            PerformanceEvidence = true,
            ReliabilityEvidence = true,
            SecurityAccountabilityEvidence = true,
            ExportReportEvidence = true,
            RealHardwareEvidence = true,
            MesCentralSyncEvidence = true,
            ReleasePackagingEvidence = true,
            BuildTestEvidence = true,
        };
}
