using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public enum IntegrationConnectionStatus
{
    NotConnected,
    Simulated,
    Error,
    Ready,
}

public sealed record IntegrationCommandResult(
    bool Accepted,
    IntegrationConnectionStatus Status,
    string Message)
{
    public static IntegrationCommandResult NotConnected(string message)
        => new(false, IntegrationConnectionStatus.NotConnected, message);
}

public sealed record LoadCommand(
    string BoardId,
    string BoardModel,
    string LotId,
    string StationId);

public sealed record InspectCommand(
    string BoardId,
    string BoardModel,
    string LotId,
    string StationId,
    string ViewType);

public sealed record UnloadCommand(
    string BoardId,
    string BoardModel,
    string LotId,
    string StationId,
    string Destination);

public sealed record UploadResultCommand(
    string InspectionId,
    string BoardId,
    string BoardModel,
    string LotId,
    string Result,
    string OperatorId,
    DateTime TimestampUtc);

public sealed record UploadImageCommand(
    string InspectionId,
    string BoardId,
    string ImagePath,
    string ImageType,
    DateTime TimestampUtc);

public interface IIntegrationEndpoint
{
    string Name { get; }
    IntegrationConnectionStatus Status { get; }
    string StatusMessage { get; }
}

public interface ILightingController : IIntegrationEndpoint
{
    Task<IntegrationCommandResult> SetProgramAsync(
        string viewType,
        string programName,
        CancellationToken cancellationToken = default);
}

public interface IRobotController : IIntegrationEndpoint
{
    Task<IntegrationCommandResult> LoadAsync(
        LoadCommand command,
        CancellationToken cancellationToken = default);

    Task<IntegrationCommandResult> InspectAsync(
        InspectCommand command,
        CancellationToken cancellationToken = default);

    Task<IntegrationCommandResult> UnloadAsync(
        UnloadCommand command,
        CancellationToken cancellationToken = default);

    Task<IntegrationCommandResult> ResetAsync(
        CancellationToken cancellationToken = default);
}

public interface IMesClient : IIntegrationEndpoint
{
    Task<IntegrationCommandResult> UploadTraceabilityAsync(
        TraceabilityPayload payload,
        CancellationToken cancellationToken = default);

    Task<IntegrationCommandResult> UploadResultAsync(
        UploadResultCommand command,
        CancellationToken cancellationToken = default);

    Task<IntegrationCommandResult> UploadImageAsync(
        UploadImageCommand command,
        CancellationToken cancellationToken = default);
}

public interface ITraceabilityUploader : IIntegrationEndpoint
{
    Task<IntegrationCommandResult> UploadTraceabilityAsync(
        TraceabilityPayload payload,
        CancellationToken cancellationToken = default);

    Task<IntegrationCommandResult> UploadResultAsync(
        UploadResultCommand command,
        CancellationToken cancellationToken = default);

    Task<IntegrationCommandResult> UploadImageAsync(
        UploadImageCommand command,
        CancellationToken cancellationToken = default);
}

public interface IOpcUaMesClient : IIntegrationEndpoint
{
    Task<IntegrationCommandResult> WriteTraceabilityNodeAsync(
        TraceabilityPayload payload,
        CancellationToken cancellationToken = default);
}

public interface ICentralProductionDatabaseClient : IIntegrationEndpoint
{
    Task<IntegrationCommandResult> UploadCentralSyncPayloadAsync(
        CentralSyncPayload payload,
        CancellationToken cancellationToken = default);
}

public interface IEmergencyStopMonitor : IIntegrationEndpoint
{
    bool IsEmergencyStopActive { get; }
}

public sealed class SafetyStatus
{
    public bool IsGuardDoorClosed { get; set; } = true;
    public bool IsEmergencyStopActive { get; set; }
    public bool IsAirPressureOk { get; set; } = true;
    public bool IsRobotServoReady { get; set; } = true;
    public bool IsBoardClampReady { get; set; } = true;
    public bool IsLightCurtainClear { get; set; } = true;
    public List<string> ActiveFaults { get; set; } = new();
    public string Message { get; set; } = "Safety interlocks OK.";

    public bool IsSafeToMove =>
        IsGuardDoorClosed &&
        !IsEmergencyStopActive &&
        IsAirPressureOk &&
        IsRobotServoReady &&
        IsBoardClampReady &&
        IsLightCurtainClear &&
        ActiveFaults.Count == 0;

    public bool IsOk => IsSafeToMove;

    public string BlockingReason()
    {
        var reasons = new List<string>();
        if (!IsGuardDoorClosed)
            reasons.Add("guard door open");
        if (IsEmergencyStopActive)
            reasons.Add("emergency stop active");
        if (!IsAirPressureOk)
            reasons.Add("air pressure not OK");
        if (!IsRobotServoReady)
            reasons.Add("robot servo not ready");
        if (!IsBoardClampReady)
            reasons.Add("board clamp not ready");
        if (!IsLightCurtainClear)
            reasons.Add("light curtain not clear");
        reasons.AddRange(ActiveFaults.Where(fault => !string.IsNullOrWhiteSpace(fault)));
        return reasons.Count == 0 ? Message : string.Join(", ", reasons);
    }
}

public interface IPlcSafetyController : IIntegrationEndpoint
{
    bool IsGuardDoorClosed { get; }
    bool IsEmergencyStopActive { get; }
    bool IsAirPressureOk { get; }
    bool IsRobotServoReady { get; }
    bool IsBoardClampReady { get; }
    bool IsLightCurtainClear { get; }
    Task<IntegrationCommandResult> ResetSafetyFaultAsync(CancellationToken cancellationToken = default);
    SafetyStatus GetSafetyStatus();
    SafetyStatus GetDiagnostics();
}

public sealed class NullLightingController : ILightingController
{
    public string Name => "Null Lighting Controller";
    public IntegrationConnectionStatus Status => IntegrationConnectionStatus.NotConnected;
    public string StatusMessage => "Stage 2 lighting integration boundary only. No real lighting hardware is connected.";

    public Task<IntegrationCommandResult> SetProgramAsync(
        string viewType,
        string programName,
        CancellationToken cancellationToken = default)
        => Task.FromResult(IntegrationCommandResult.NotConnected(StatusMessage));
}

public sealed class NullRobotController : IRobotController
{
    public string Name => "Null Robot Controller";
    public IntegrationConnectionStatus Status => IntegrationConnectionStatus.NotConnected;
    public string StatusMessage => "Stage 3 Planned Robot Integration boundary only. No robot commands are sent.";

    public Task<IntegrationCommandResult> LoadAsync(
        LoadCommand command,
        CancellationToken cancellationToken = default)
        => Task.FromResult(IntegrationCommandResult.NotConnected(StatusMessage));

    public Task<IntegrationCommandResult> InspectAsync(
        InspectCommand command,
        CancellationToken cancellationToken = default)
        => Task.FromResult(IntegrationCommandResult.NotConnected(StatusMessage));

    public Task<IntegrationCommandResult> UnloadAsync(
        UnloadCommand command,
        CancellationToken cancellationToken = default)
        => Task.FromResult(IntegrationCommandResult.NotConnected(StatusMessage));

    public Task<IntegrationCommandResult> ResetAsync(
        CancellationToken cancellationToken = default)
        => Task.FromResult(IntegrationCommandResult.NotConnected(StatusMessage));
}

public sealed class NullPlcSafetyController : IPlcSafetyController
{
    public string Name => "Null PLC Safety Controller";
    public IntegrationConnectionStatus Status => IntegrationConnectionStatus.NotConnected;
    public string StatusMessage => "Stage 3 PLC/safety interlock boundary only. No real guard door, clamp, air, servo, or e-stop circuit is monitored.";
    public bool IsGuardDoorClosed => false;
    public bool IsEmergencyStopActive => false;
    public bool IsAirPressureOk => false;
    public bool IsRobotServoReady => false;
    public bool IsBoardClampReady => false;
    public bool IsLightCurtainClear => false;

    public Task<IntegrationCommandResult> ResetSafetyFaultAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(IntegrationCommandResult.NotConnected(StatusMessage));

    public SafetyStatus GetSafetyStatus()
        => new()
        {
            IsGuardDoorClosed = false,
            IsEmergencyStopActive = false,
            IsAirPressureOk = false,
            IsRobotServoReady = false,
            IsBoardClampReady = false,
            IsLightCurtainClear = false,
            ActiveFaults = { "PLC/safety controller not connected" },
            Message = StatusMessage,
        };

    public SafetyStatus GetDiagnostics() => GetSafetyStatus();
}

public sealed class NullCentralProductionDatabaseClient : ICentralProductionDatabaseClient
{
    public string Name => "Null Central Production Database Client";
    public IntegrationConnectionStatus Status => IntegrationConnectionStatus.NotConnected;
    public string StatusMessage => "Central PostgreSQL integration boundary only. Local SQLite remains the offline source of truth and no Npgsql client is bundled.";

    public Task<IntegrationCommandResult> UploadCentralSyncPayloadAsync(
        CentralSyncPayload payload,
        CancellationToken cancellationToken = default)
        => Task.FromResult(IntegrationCommandResult.NotConnected(
            "Central production database upload is not configured. Expected future mapping: local InspectionResults, Defects, ReviewEvents, ValidationPackages, acceptance reports, and ExportVerification records to central reporting tables."));
}

public sealed class SimulatedPlcSafetyController : IPlcSafetyController
{
    public string Name => "Simulated PLC Safety Controller";
    public IntegrationConnectionStatus Status => IsEmergencyStopActive ? IntegrationConnectionStatus.Error : IntegrationConnectionStatus.Simulated;
    public string StatusMessage => IsEmergencyStopActive
        ? "Simulated PLC safety fault active. No safety-certified hardware is monitored."
        : "PLC/safety simulation only. No real interlock circuit is monitored.";
    public bool IsGuardDoorClosed { get; private set; } = true;
    public bool IsEmergencyStopActive { get; private set; }
    public bool IsAirPressureOk { get; private set; } = true;
    public bool IsRobotServoReady { get; private set; } = true;
    public bool IsBoardClampReady { get; private set; } = true;
    public bool IsLightCurtainClear { get; private set; } = true;

    public void SetGuardDoorClosed(bool value) => IsGuardDoorClosed = value;
    public void SetEmergencyStopActive(bool value) => IsEmergencyStopActive = value;
    public void SetAirPressureOk(bool value) => IsAirPressureOk = value;
    public void SetRobotServoReady(bool value) => IsRobotServoReady = value;
    public void SetBoardClampReady(bool value) => IsBoardClampReady = value;
    public void SetLightCurtainClear(bool value) => IsLightCurtainClear = value;

    public Task<IntegrationCommandResult> ResetSafetyFaultAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IsGuardDoorClosed = true;
        IsEmergencyStopActive = false;
        IsAirPressureOk = true;
        IsRobotServoReady = true;
        IsBoardClampReady = true;
        IsLightCurtainClear = true;
        return Task.FromResult(new IntegrationCommandResult(true, IntegrationConnectionStatus.Simulated, "Simulated PLC safety fault reset. No real safety circuit was reset."));
    }

    public SafetyStatus GetSafetyStatus()
    {
        var status = new SafetyStatus
        {
            IsGuardDoorClosed = IsGuardDoorClosed,
            IsEmergencyStopActive = IsEmergencyStopActive,
            IsAirPressureOk = IsAirPressureOk,
            IsRobotServoReady = IsRobotServoReady,
            IsBoardClampReady = IsBoardClampReady,
            IsLightCurtainClear = IsLightCurtainClear,
            Message = StatusMessage,
        };
        if (!IsGuardDoorClosed)
            status.ActiveFaults.Add("guard door open");
        if (IsEmergencyStopActive)
            status.ActiveFaults.Add("emergency stop active");
        if (!IsAirPressureOk)
            status.ActiveFaults.Add("air pressure not OK");
        if (!IsRobotServoReady)
            status.ActiveFaults.Add("robot servo not ready");
        if (!IsBoardClampReady)
            status.ActiveFaults.Add("board clamp not ready");
        if (!IsLightCurtainClear)
            status.ActiveFaults.Add("light curtain not clear");
        return status;
    }

    public SafetyStatus GetDiagnostics() => GetSafetyStatus();
}

public sealed class TcpTextPlcSafetyController : IPlcSafetyController
{
    private readonly string _endpointSummary;

    public TcpTextPlcSafetyController(string endpointSummary = "")
    {
        _endpointSummary = endpointSummary;
    }

    public string Name => "TCP Text PLC Safety Boundary";
    public IntegrationConnectionStatus Status => string.IsNullOrWhiteSpace(_endpointSummary) ? IntegrationConnectionStatus.NotConnected : IntegrationConnectionStatus.Ready;
    public string StatusMessage => Status == IntegrationConnectionStatus.Ready
        ? $"TCP text PLC boundary configured for {_endpointSummary}. Commands are sent only by explicit PLC integration code."
        : "TCP text PLC boundary is not configured; no network safety command will be sent.";
    public bool IsGuardDoorClosed => false;
    public bool IsEmergencyStopActive => false;
    public bool IsAirPressureOk => false;
    public bool IsRobotServoReady => false;
    public bool IsBoardClampReady => false;
    public bool IsLightCurtainClear => false;

    public Task<IntegrationCommandResult> ResetSafetyFaultAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(IntegrationCommandResult.NotConnected("TCP text PLC reset is a boundary only in this build. No vendor PLC or safety-certified reset command was sent."));

    public SafetyStatus GetSafetyStatus()
        => new()
        {
            IsGuardDoorClosed = false,
            IsEmergencyStopActive = false,
            IsAirPressureOk = false,
            IsRobotServoReady = false,
            IsBoardClampReady = false,
            IsLightCurtainClear = false,
            ActiveFaults = { "TCP text PLC boundary does not read safety state in this build" },
            Message = StatusMessage,
        };

    public SafetyStatus GetDiagnostics() => GetSafetyStatus();
}

public sealed class SimulatedRobotController : IRobotController
{
    private const int SimulatedStepDelayMilliseconds = 150;
    private bool _isBoardLoaded;
    private bool _isEmergencyStopActive;
    private string _loadedBoardId = string.Empty;

    public string Name => "Simulated Robot / Handler";
    public IntegrationConnectionStatus Status => _isEmergencyStopActive
        ? IntegrationConnectionStatus.Error
        : IntegrationConnectionStatus.Simulated;

    public string StatusMessage => _isEmergencyStopActive
        ? "Simulated emergency stop is active. No real robot hardware is connected."
        : "Robot/handler simulation only. No real robot hardware is connected or controlled.";

    public bool IsBoardLoaded => _isBoardLoaded;
    public bool IsEmergencyStopActive => _isEmergencyStopActive;
    public string LoadedBoardId => _loadedBoardId;

    public async Task<IntegrationCommandResult> LoadAsync(
        LoadCommand command,
        CancellationToken cancellationToken = default)
    {
        var blocked = BlockIfUnavailable("Load");
        if (blocked is not null)
            return blocked;

        if (_isBoardLoaded)
            return new IntegrationCommandResult(false, IntegrationConnectionStatus.Error, $"Simulated load rejected. Board {_loadedBoardId} is already loaded.");

        await SimulateStepDelayAsync(cancellationToken);

        blocked = BlockIfUnavailable("Load");
        if (blocked is not null)
            return blocked;

        _isBoardLoaded = true;
        _loadedBoardId = string.IsNullOrWhiteSpace(command.BoardId) ? "SIM-BOARD" : command.BoardId;

        return new IntegrationCommandResult(
            true,
            IntegrationConnectionStatus.Simulated,
            $"Simulated load complete for board {_loadedBoardId}. No real robot command was sent.");
    }

    public async Task<IntegrationCommandResult> InspectAsync(
        InspectCommand command,
        CancellationToken cancellationToken = default)
    {
        var blocked = BlockIfUnavailable("Inspect");
        if (blocked is not null)
            return blocked;

        if (!_isBoardLoaded)
            return new IntegrationCommandResult(false, IntegrationConnectionStatus.Error, "Simulated inspect rejected. No simulated board is loaded.");

        await SimulateStepDelayAsync(cancellationToken);

        blocked = BlockIfUnavailable("Inspect");
        if (blocked is not null)
            return blocked;

        return new IntegrationCommandResult(
            true,
            IntegrationConnectionStatus.Simulated,
            $"Simulated inspect position reached for board {_loadedBoardId} on {command.ViewType}. No real robot command was sent.");
    }

    public async Task<IntegrationCommandResult> UnloadAsync(
        UnloadCommand command,
        CancellationToken cancellationToken = default)
    {
        var blocked = BlockIfUnavailable("Unload");
        if (blocked is not null)
            return blocked;

        if (!_isBoardLoaded)
            return new IntegrationCommandResult(false, IntegrationConnectionStatus.Error, "Simulated unload rejected. No simulated board is loaded.");

        await SimulateStepDelayAsync(cancellationToken);

        blocked = BlockIfUnavailable("Unload");
        if (blocked is not null)
            return blocked;

        var boardId = _loadedBoardId;
        _isBoardLoaded = false;
        _loadedBoardId = string.Empty;

        return new IntegrationCommandResult(
            true,
            IntegrationConnectionStatus.Simulated,
            $"Simulated unload complete for board {boardId} to {command.Destination}. No real robot command was sent.");
    }

    public Task<IntegrationCommandResult> ResetAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _isBoardLoaded = false;
        _loadedBoardId = string.Empty;
        _isEmergencyStopActive = false;

        return Task.FromResult(new IntegrationCommandResult(
            true,
            IntegrationConnectionStatus.Simulated,
            "Simulated robot reset complete. Emergency stop simulation cleared. No real robot command was sent."));
    }

    public void TriggerEmergencyStop() => _isEmergencyStopActive = true;

    public void ClearEmergencyStop() => _isEmergencyStopActive = false;

    private IntegrationCommandResult? BlockIfUnavailable(string commandName)
    {
        if (!_isEmergencyStopActive)
            return null;

        return new IntegrationCommandResult(
            false,
            IntegrationConnectionStatus.Error,
            $"Simulated {commandName} interrupted by emergency stop simulation. No real robot command was sent.");
    }

    private static Task SimulateStepDelayAsync(CancellationToken cancellationToken)
        => Task.Delay(SimulatedStepDelayMilliseconds, cancellationToken);
}

public sealed class SimulatedEmergencyStopMonitor : IEmergencyStopMonitor
{
    private readonly SimulatedRobotController _robotController;

    public SimulatedEmergencyStopMonitor(SimulatedRobotController robotController)
    {
        _robotController = robotController;
    }

    public string Name => "Simulated Emergency Stop Monitor";
    public IntegrationConnectionStatus Status => _robotController.IsEmergencyStopActive
        ? IntegrationConnectionStatus.Error
        : IntegrationConnectionStatus.Simulated;

    public string StatusMessage => _robotController.IsEmergencyStopActive
        ? "Simulated emergency stop is active. No real safety circuit is monitored."
        : "Emergency-stop simulation only. No real safety circuit is monitored.";

    public bool IsEmergencyStopActive => _robotController.IsEmergencyStopActive;
}

public sealed class PlcEmergencyStopMonitor : IEmergencyStopMonitor
{
    private readonly IPlcSafetyController _plc;

    public PlcEmergencyStopMonitor(IPlcSafetyController plc)
    {
        _plc = plc;
    }

    public string Name => $"{_plc.Name} E-Stop";
    public IntegrationConnectionStatus Status => _plc.Status;
    public string StatusMessage => _plc.StatusMessage;
    public bool IsEmergencyStopActive => _plc.IsEmergencyStopActive;
}

public sealed class NullMesClient : IMesClient
{
    public string Name => "Null MES Client";
    public IntegrationConnectionStatus Status => IntegrationConnectionStatus.NotConnected;
    public string StatusMessage => "Stage 4 Planned MES/ERP Integration boundary only. No production MES writeback is performed.";

    public Task<IntegrationCommandResult> UploadTraceabilityAsync(
        TraceabilityPayload payload,
        CancellationToken cancellationToken = default)
        => Task.FromResult(IntegrationCommandResult.NotConnected(StatusMessage));

    public Task<IntegrationCommandResult> UploadResultAsync(
        UploadResultCommand command,
        CancellationToken cancellationToken = default)
        => Task.FromResult(IntegrationCommandResult.NotConnected(StatusMessage));

    public Task<IntegrationCommandResult> UploadImageAsync(
        UploadImageCommand command,
        CancellationToken cancellationToken = default)
        => Task.FromResult(IntegrationCommandResult.NotConnected(StatusMessage));
}

public sealed class NullTraceabilityUploader : ITraceabilityUploader
{
    public string Name => "Null Traceability Uploader";
    public IntegrationConnectionStatus Status => IntegrationConnectionStatus.NotConnected;
    public string StatusMessage => "Stage 4 traceability upload boundary only. Results and images remain local.";

    public Task<IntegrationCommandResult> UploadTraceabilityAsync(
        TraceabilityPayload payload,
        CancellationToken cancellationToken = default)
        => Task.FromResult(IntegrationCommandResult.NotConnected(StatusMessage));

    public Task<IntegrationCommandResult> UploadResultAsync(
        UploadResultCommand command,
        CancellationToken cancellationToken = default)
        => Task.FromResult(IntegrationCommandResult.NotConnected(StatusMessage));

    public Task<IntegrationCommandResult> UploadImageAsync(
        UploadImageCommand command,
        CancellationToken cancellationToken = default)
        => Task.FromResult(IntegrationCommandResult.NotConnected(StatusMessage));
}

public sealed class NullOpcUaMesClient : IOpcUaMesClient
{
    public string Name => "Null OPC UA MES Client";
    public IntegrationConnectionStatus Status => IntegrationConnectionStatus.NotConnected;
    public string StatusMessage => "OPC UA MES adapter boundary only. No OPC UA package or production implementation is bundled.";

    public Task<IntegrationCommandResult> WriteTraceabilityNodeAsync(
        TraceabilityPayload payload,
        CancellationToken cancellationToken = default)
        => Task.FromResult(IntegrationCommandResult.NotConnected("OPC UA MES traceability requires a reviewed adapter with endpoint, namespace, node mapping, certificate, and security-mode configuration. No production OPC UA write was attempted."));
}

public sealed class NullEmergencyStopMonitor : IEmergencyStopMonitor
{
    public string Name => "Null Emergency Stop Monitor";
    public IntegrationConnectionStatus Status => IntegrationConnectionStatus.NotConnected;
    public string StatusMessage => "Stage 3 safety/PLC integration boundary only. No emergency-stop hardware is monitored.";
    public bool IsEmergencyStopActive => false;
}

public static class IntegrationBoundaryRegistry
{
    public static ILightingController LightingController { get; set; } = new NullLightingController();
    public static IRobotController RobotController { get; set; } = new NullRobotController();
    public static IPlcSafetyController PlcSafetyController { get; set; } = new NullPlcSafetyController();
    public static IMesClient MesClient { get; set; } = new NullMesClient();
    public static ITraceabilityUploader TraceabilityUploader { get; set; } = new NullTraceabilityUploader();
    public static IOpcUaMesClient OpcUaMesClient { get; set; } = new NullOpcUaMesClient();
    public static ICentralProductionDatabaseClient CentralProductionDatabaseClient { get; set; } = new NullCentralProductionDatabaseClient();
    public static IEmergencyStopMonitor EmergencyStopMonitor { get; set; } = new NullEmergencyStopMonitor();
}
