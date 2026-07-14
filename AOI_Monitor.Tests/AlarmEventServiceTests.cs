using System.Text.Json;
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
    public void AcknowledgeAllActiveAcknowledgesEveryUnacknowledgedAlarm()
    {
        AlarmEventService.Raise(AlarmSeverity.Alarm, "Camera", "Camera link lost.", "Check the camera cable.");
        AlarmEventService.Raise(AlarmSeverity.Warning, "MES", "MES queue has pending records.", "Retry the queue.");
        AlarmEventService.Raise(AlarmSeverity.Critical, "Robot", "Emergency stop input is active.", "Clear the cell.");

        var acknowledged = AlarmEventService.AcknowledgeAllActive("AlarmAdmin [Admin]");

        Assert.Equal(3, acknowledged);
        Assert.All(
            AlarmEventService.GetEvents(new AlarmEventQuery { ActiveOnly = true, OperatorVisibleOnly = false }),
            item => Assert.Equal(AlarmAcknowledgementState.Acknowledged, item.AcknowledgementState));
        Assert.Equal(0, AlarmEventService.AcknowledgeAllActive("AlarmAdmin [Admin]"));
    }

    [Fact]
    public void ExpireStaleActiveAlarmsResolvesOldNonCriticalOnly()
    {
        WriteAlarmSnapshot(
            _root,
            MakeAlarm("OLD-WARN", AlarmSeverity.Warning, ageDays: 30),
            MakeAlarm("OLD-ALARM", AlarmSeverity.Alarm, ageDays: 20),
            MakeAlarm("OLD-CRIT", AlarmSeverity.Critical, ageDays: 30),
            MakeAlarm("FRESH-ALARM", AlarmSeverity.Alarm, ageDays: 1));
        AlarmEventService.ReloadFromDiskForTests();

        var expired = AlarmEventService.ExpireStaleActiveAlarms(TimeSpan.FromDays(14));

        Assert.Equal(2, expired);
        var active = AlarmEventService.GetEvents(new AlarmEventQuery { ActiveOnly = true, OperatorVisibleOnly = false });
        Assert.Contains(active, item => item.AlarmId == "OLD-CRIT");
        Assert.Contains(active, item => item.AlarmId == "FRESH-ALARM");
        Assert.DoesNotContain(active, item => item.AlarmId == "OLD-WARN");
        Assert.DoesNotContain(active, item => item.AlarmId == "OLD-ALARM");

        var oldWarn = AlarmEventService.GetEvents(new AlarmEventQuery { ActiveOnly = false, OperatorVisibleOnly = false })
            .Single(item => item.AlarmId == "OLD-WARN");
        Assert.Equal(AlarmAcknowledgementState.Resolved, oldWarn.AcknowledgementState);
        Assert.Contains("auto-expiry", oldWarn.ResolvedBy, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SnapshotLoadDropsResolvedAlarmsOlderThanRetentionButKeepsActiveOnes()
    {
        WriteAlarmSnapshot(
            _root,
            MakeAlarm("OLD-RESOLVED", AlarmSeverity.Alarm, ageDays: 120, resolved: true),
            MakeAlarm("OLD-ACTIVE", AlarmSeverity.Alarm, ageDays: 120),
            MakeAlarm("FRESH-RESOLVED", AlarmSeverity.Alarm, ageDays: 5, resolved: true));
        AlarmEventService.ReloadFromDiskForTests();

        var all = AlarmEventService.GetEvents(new AlarmEventQuery { ActiveOnly = false, OperatorVisibleOnly = false });

        Assert.DoesNotContain(all, item => item.AlarmId == "OLD-RESOLVED");
        Assert.Contains(all, item => item.AlarmId == "OLD-ACTIVE");
        Assert.Contains(all, item => item.AlarmId == "FRESH-RESOLVED");
    }

    private static AlarmEvent MakeAlarm(string id, AlarmSeverity severity, int ageDays, bool resolved = false) => new()
    {
        AlarmId = id,
        TimestampUtc = DateTime.UtcNow.AddDays(-ageDays),
        Severity = severity,
        Source = "Test",
        Message = $"Test alarm {id}.",
        RecommendedAction = "Review the test alarm.",
        AcknowledgementState = resolved ? AlarmAcknowledgementState.Resolved : AlarmAcknowledgementState.Unacknowledged,
        ResolvedAtUtc = resolved ? DateTime.UtcNow.AddDays(-ageDays) : null,
        ResolvedBy = resolved ? "Test" : string.Empty,
    };

    // Mirrors AlarmEventService's snapshot format (camelCase, enums as numbers) so the service
    // loads these fixtures exactly like a real persisted state file.
    private static void WriteAlarmSnapshot(string root, params AlarmEvent[] alarms)
    {
        var path = Path.Combine(root, "exports", "alarm_events", "alarm_events_state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(
            alarms.ToList(),
            new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
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

    [Fact]
    public void RecurringKeyedAlarmReentersUnacknowledgedSetAfterAcknowledgement()
    {
        const string key = "MACHINE_INTERFACE_EXPORT_FAILED";
        var first = AlarmEventService.Raise(
            AlarmSeverity.Alarm,
            "MachineInterface",
            "Machine-interface export failed for board A.",
            "Check the export target and retry.",
            idempotencyKey: key);
        Assert.True(AlarmEventService.Acknowledge(first.AlarmId, "AlarmAdmin [Admin]"));

        // The same fault recurs with a materially different message: it must re-surface as
        // unacknowledged rather than stay silently acknowledged.
        var second = AlarmEventService.Raise(
            AlarmSeverity.Alarm,
            "MachineInterface",
            "Machine-interface export failed for board B.",
            "Check the export target and retry.",
            idempotencyKey: key);

        Assert.Equal(first.AlarmId, second.AlarmId);
        Assert.Equal(AlarmAcknowledgementState.Unacknowledged, second.AcknowledgementState);
        Assert.Null(second.AcknowledgedAtUtc);
        Assert.Contains(
            AlarmEventService.GetUnacknowledgedAlarmLevelEvents(),
            item => item.AlarmId == first.AlarmId);
    }

    [Fact]
    public void IdenticalKeyedReraiseDoesNotResurrectAcknowledgement()
    {
        const string key = "SIMULATION_BOUNDARY_ACTIVE";
        var first = AlarmEventService.Raise(
            AlarmSeverity.Warning,
            "SimulationBoundary",
            "Simulated source active.",
            "Keep evidence labeled simulated.",
            idempotencyKey: key);
        AlarmEventService.Acknowledge(first.AlarmId, "AlarmAdmin [Admin]");

        // A byte-identical re-raise is not a new occurrence; it must not nag the operator again.
        var second = AlarmEventService.Raise(
            AlarmSeverity.Warning,
            "SimulationBoundary",
            "Simulated source active.",
            "Keep evidence labeled simulated.",
            idempotencyKey: key);

        Assert.Equal(AlarmAcknowledgementState.Acknowledged, second.AcknowledgementState);
    }

    [Fact]
    public void CorruptSnapshotIsQuarantinedAndRaisesCriticalSelfAlarm()
    {
        var snapshotPath = Path.Combine(_root, "exports", "alarm_events", "alarm_events_state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
        File.WriteAllText(snapshotPath, "{ this is not valid alarm json ]]");

        AlarmEventService.ReloadFromDiskForTests();
        var active = AlarmEventService.GetEvents(new AlarmEventQuery { ActiveOnly = true, OperatorVisibleOnly = false });

        // A corrupt snapshot must not silently vanish: a Critical self-alarm surfaces the loss so
        // readiness gates block instead of reporting Go.
        Assert.Contains(active, item => item.Severity == AlarmSeverity.Critical && item.Source.Contains("AlarmPersistence", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(AlarmEventService.GetActiveCriticalAlarms(), item => item.Source.Contains("AlarmPersistence", StringComparison.OrdinalIgnoreCase));

        // The unreadable file is moved aside (not left to be overwritten by the next write).
        var quarantined = Directory.GetFiles(Path.GetDirectoryName(snapshotPath)!, "*.corrupt-*");
        Assert.NotEmpty(quarantined);
    }

    [Fact]
    public void PersistRewritesSnapshotAtomicallyWithoutLeavingTempFiles()
    {
        AlarmEventService.Raise(AlarmSeverity.Alarm, "Camera", "Camera link lost.", "Check the cable.");
        var dir = Path.Combine(_root, "exports", "alarm_events");

        Assert.True(File.Exists(Path.Combine(dir, "alarm_events_state.json")));
        // The atomic write swaps a temp file into place and must not leave temp residue behind.
        Assert.Empty(Directory.GetFiles(dir, "*.tmp-*"));
    }
}
