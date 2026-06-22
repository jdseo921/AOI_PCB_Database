using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Xunit;

namespace AOI_Monitor.Tests;

public sealed class AlarmEventServiceTests : IDisposable
{
    private readonly string _root;

    public AlarmEventServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AOI_Monitor_AlarmEvent_Tests", Guid.NewGuid().ToString("N"));
        AoiDatabase.ConfigureStorageRoot(_root);
        AoiDatabase.Initialize();
        WorkflowState.Instance.SetCurrentUser("AlarmAdmin", UserRole.Admin);
        AlarmEventService.ClearForTests();
        CrashReportService.ClearForTests();
    }

    public void Dispose()
    {
        AlarmEventService.ClearForTests();
        CrashReportService.ClearForTests();
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"Test cleanup failed for {nameof(AlarmEventServiceTests)}: {ex.Message}");
        }
    }

    [Fact]
    public void RawExceptionMessageIsTransformedForOperator()
    {
        var exception = new InvalidOperationException("System.InvalidOperationException: secret detail\r\n   at Vendor.Driver.Open()");

        var alarm = AlarmEventService.RaiseFromException(
            "Open camera",
            "Camera",
            exception,
            AlarmSeverity.Alarm);

        Assert.DoesNotContain("InvalidOperationException", alarm.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Vendor.Driver", alarm.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("   at ", alarm.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("failed safely", alarm.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("InvalidOperationException", alarm.EngineerDetails, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CriticalAlarmPersistsUntilAcknowledgedAndResolved()
    {
        var alarm = AlarmEventService.Raise(
            AlarmSeverity.Critical,
            "Robot",
            "Emergency stop input is active.",
            "Stop the cell and verify the safety circuit before resuming.");

        Assert.Contains(AlarmEventService.GetActiveCriticalAlarms(), item => item.AlarmId == alarm.AlarmId);

        Assert.True(AlarmEventService.Acknowledge(alarm.AlarmId, "AlarmAdmin [Admin]"));
        var acknowledged = AlarmEventService.GetEvents(new AlarmEventQuery { ActiveOnly = true, OperatorVisibleOnly = false })
            .Single(item => item.AlarmId == alarm.AlarmId);
        Assert.Equal(AlarmAcknowledgementState.Acknowledged, acknowledged.AcknowledgementState);
        Assert.Contains(AlarmEventService.GetActiveCriticalAlarms(), item => item.AlarmId == alarm.AlarmId);

        Assert.True(AlarmEventService.Resolve(alarm.AlarmId, "AlarmAdmin [Admin]"));
        Assert.DoesNotContain(AlarmEventService.GetActiveCriticalAlarms(), item => item.AlarmId == alarm.AlarmId);
    }

    [Fact]
    public void SimulatedHardwareWarningIsOperatorVisible()
    {
        AlarmEventService.SetSimulationBoundaryWarning(
            active: true,
            detail: "Camera source is simulated and MES is mock REST.",
            operatorId: "AlarmAdmin [Admin]");

        var warnings = AlarmEventService.GetEvents(new AlarmEventQuery
        {
            ActiveOnly = true,
            Severity = AlarmSeverity.Warning,
            SourceContains = "Simulation",
        });

        var warning = Assert.Single(warnings);
        Assert.True(warning.IsOperatorVisible);
        Assert.True(warning.IsSimulatedOrMock);
        Assert.Contains("SIMULATED", warning.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("real hardware", warning.RecommendedAction, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AlarmExportIncludesIndustrialRequiredFields()
    {
        AlarmEventService.Raise(
            AlarmSeverity.Warning,
            "MES",
            "MES upload queue has pending records.",
            "Retry the queue or keep the station in local-only mode.");

        var export = AlarmEventService.ExportLog(Path.Combine(_root, "alarm_export"));
        var csv = File.ReadAllText(export.CsvPath);

        Assert.Contains("TimestampUtc,Severity,Source,Message,RecommendedAction", csv);
        Assert.Contains("MES", csv);
        Assert.Contains("MES upload queue has pending records.", csv);
        Assert.Contains("Retry the queue", csv);
    }

    [Fact]
    public void ActiveCriticalAlarmBlocksFactoryAndClientReadiness()
    {
        AlarmEventService.Raise(
            AlarmSeverity.Critical,
            "Startup",
            "Database is unavailable.",
            "Restore database access before continuing.");

        var client = ClientDemoReadinessGateService.Evaluate(DeploymentProfile.Stage1ImageValidation);
        var factory = FactoryReadinessService.Evaluate(new FactoryReadinessCriteria
        {
            DeploymentProfile = DeploymentProfile.Stage1ImageValidation,
            Stage1Only = true,
            RequireSuccessfulLatestValidationPackage = false,
            RequireDatasetQualityEvidence = false,
            RequireNoExportVerificationErrors = false,
        });

        Assert.Equal(ClientDemoGateStatus.Blocked, client.OverallStatus);
        Assert.Contains(client.Checks, check => check.Name == "Active critical alarms" && check.Status == ClientDemoGateStatus.Blocked);
        Assert.Contains(factory.Categories, category => category.Name == "Alarm/event status" && category.Status == "No-Go");
    }

    [Fact]
    public void UnacknowledgedAlarmWarnsReadinessUntilAcknowledged()
    {
        var alarm = AlarmEventService.Raise(
            AlarmSeverity.Alarm,
            "Export",
            "Report export failed safely.",
            "Check the export folder and retry.");

        var client = ClientDemoReadinessGateService.Evaluate(DeploymentProfile.Stage1ImageValidation);
        Assert.Contains(client.Checks, check => check.Name == "Unacknowledged alarm-level events" && check.Status == ClientDemoGateStatus.Warning);

        AlarmEventService.Acknowledge(alarm.AlarmId, "AlarmAdmin [Admin]");
        var acknowledged = ClientDemoReadinessGateService.Evaluate(DeploymentProfile.Stage1ImageValidation);
        Assert.Contains(acknowledged.Checks, check => check.Name == "Unacknowledged alarm-level events" && check.Status == ClientDemoGateStatus.Pass);
    }
}
