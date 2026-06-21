using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Xunit;

namespace AOI_Monitor.Tests;

public sealed class CustomerPilotWizardServiceTests : IDisposable
{
    private readonly string _root;

    public CustomerPilotWizardServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AOI_Monitor_CustomerPilot_Tests", Guid.NewGuid().ToString("N"));
        AoiDatabase.ConfigureStorageRoot(_root);
        AoiDatabase.Initialize();
        InspectionModelConfigurationService.Save(new InspectionModelConfiguration());
        MesIntegrationSettingsService.Save(new MesIntegrationSettings());
        CentralSyncSettingsService.ResetForTests();
        CentralSyncSettingsService.Save(new CentralSyncSettings());
    }

    public void Dispose()
    {
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
    public void Stage1PilotDoesNotRequireHardwareSteps()
    {
        var snapshot = CustomerPilotWizardService.StartNew(DeploymentProfile.Stage1ImageValidation, "Engineer01 [Engineer]");

        var stage2 = Assert.Single(snapshot.Steps, step => step.StepKey == CustomerPilotStepKind.RunStage2Acceptance);
        Assert.Equal(CustomerPilotStepStatus.Skipped, stage2.Status);

        snapshot = CustomerPilotWizardService.EvaluateStage2Acceptance(snapshot.Session.Id);

        stage2 = Assert.Single(snapshot.Steps, step => step.StepKey == CustomerPilotStepKind.RunStage2Acceptance);
        Assert.Equal(CustomerPilotStepStatus.Skipped, stage2.Status);
        Assert.Contains(stage2.Messages, message => message.Contains("not required", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Stage2PilotRequiresCameraLightingAnd3DAcceptance()
    {
        var snapshot = CustomerPilotWizardService.StartNew(DeploymentProfile.Stage2CameraPilot, "Engineer01 [Engineer]");

        snapshot = CustomerPilotWizardService.EvaluateStage2Acceptance(snapshot.Session.Id);

        var stage2 = Assert.Single(snapshot.Steps, step => step.StepKey == CustomerPilotStepKind.RunStage2Acceptance);
        Assert.Equal(CustomerPilotStepStatus.Failed, stage2.Status);
        Assert.Contains(stage2.Messages, message => message.Contains("camera acceptance", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(stage2.Messages, message => message.Contains("lighting acceptance", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(stage2.Messages, message => message.Contains("3D profile acceptance", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FailedDatasetPreflightBlocksBatchValidationUnlessEngineerOrAdminWaives()
    {
        var snapshot = CustomerPilotWizardService.StartNew(DeploymentProfile.Stage1ImageValidation, "Engineer01 [Engineer]");
        CustomerPilotWizardService.RecordDatasetSelection(snapshot.Session.Id, Path.Combine(_root, "missing_dataset"), Path.Combine(_root, "missing_manifest.csv"));
        snapshot = CustomerPilotWizardService.RunDatasetPreflight(snapshot.Session.Id);
        var preflight = Assert.Single(snapshot.Steps, step => step.StepKey == CustomerPilotStepKind.RunDatasetPreflight);
        Assert.Equal(CustomerPilotStepStatus.Failed, preflight.Status);

        Assert.Throws<InvalidOperationException>(() =>
            CustomerPilotWizardService.RecordBatchValidationEvidence(snapshot.Session.Id, "SQLite:BatchTestRuns/1", CustomerPilotStepStatus.Passed, "Should be blocked."));

        Assert.Throws<UnauthorizedAccessException>(() =>
            CustomerPilotWizardService.RecordWaiver(snapshot.Session.Id, CustomerPilotStepKind.RunDatasetPreflight, "Operator cannot waive.", UserRole.Operator, "Operator01 [Operator]"));

        snapshot = CustomerPilotWizardService.RecordWaiver(snapshot.Session.Id, CustomerPilotStepKind.RunDatasetPreflight, "Engineer accepted customer-provided manifest exception for pilot only.", UserRole.Engineer, "Engineer01 [Engineer]");
        Assert.True(CustomerPilotWizardService.CanRunBatchValidation(snapshot));

        snapshot = CustomerPilotWizardService.RecordBatchValidationEvidence(snapshot.Session.Id, "SQLite:BatchTestRuns/1", CustomerPilotStepStatus.Passed, "Batch validation evidence recorded after waiver.");
        var batch = Assert.Single(snapshot.Steps, step => step.StepKey == CustomerPilotStepKind.RunBatchValidation);
        Assert.Equal(CustomerPilotStepStatus.Passed, batch.Status);
        Assert.Contains(AoiDatabase.GetAuditEvents(new LogFilter { ActionCategory = "CUSTOMER_PILOT_WAIVER" }), audit => audit.UserRole == "Engineer");
    }

    [Fact]
    public void ResumeLatestPilotRestoresStepStatus()
    {
        var started = CustomerPilotWizardService.StartNew(DeploymentProfile.Stage1ImageValidation, "Engineer01 [Engineer]");
        CustomerPilotWizardService.ConfirmDeploymentProfile(started.Session.Id, DeploymentProfile.Stage1ImageValidation, "Engineer01 [Engineer]");

        var resumed = CustomerPilotWizardService.StartOrResume(DeploymentProfile.Stage2CameraPilot, "Admin01 [Admin]");

        Assert.Equal(started.Session.Id, resumed.Session.Id);
        Assert.Equal(DeploymentProfile.Stage1ImageValidation, resumed.Session.DeploymentProfile);
        Assert.Contains(resumed.Steps, step =>
            step.StepKey == CustomerPilotStepKind.ConfirmDeploymentProfile &&
            step.Status == CustomerPilotStepStatus.Passed);
    }
}
