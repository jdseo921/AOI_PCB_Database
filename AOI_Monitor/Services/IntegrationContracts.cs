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

public interface IEmergencyStopMonitor : IIntegrationEndpoint
{
    bool IsEmergencyStopActive { get; }
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
    public static IMesClient MesClient { get; set; } = new NullMesClient();
    public static ITraceabilityUploader TraceabilityUploader { get; set; } = new NullTraceabilityUploader();
    public static IEmergencyStopMonitor EmergencyStopMonitor { get; set; } = new NullEmergencyStopMonitor();
}
