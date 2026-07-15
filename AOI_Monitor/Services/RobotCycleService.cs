using System.Diagnostics;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public enum RobotCycleState
{
    Idle,
    Loading,
    Loaded,
    MovingToInspect,
    ReadyToInspect,
    Inspecting,
    Unloading,
    Completed,
    Faulted,
    EmergencyStopped,
    Canceled,
}

public sealed record RobotCycleTransition(
    RobotCycleState From,
    RobotCycleState To,
    DateTime TimestampUtc,
    string Message);

public sealed record RobotCycleRunResult(
    bool Accepted,
    RobotCycleState State,
    AnalysisResult? Analysis,
    IntegrationCommandResult? LastCommandResult,
    string Message,
    TimeSpan Elapsed);

public sealed class RobotCycleOptions
{
    /// <summary>
    /// When <c>true</c>, a robot motion command is permitted even though the PLC safety status is
    /// not OK, but only in the fully-simulated case (no PLC configured and no ready robot). This is
    /// an opt-in demonstration aid and defaults to <c>false</c> (fail-safe) per the AOI Software
    /// Architecture, Secure Development, and Change-Control Standard (Docs/standard, §34 robot and
    /// safety boundary: safety bypass is never the default, so an adapter that mis-reports its
    /// connection state cannot silently obtain motion). Simulation/acceptance flows that intend to
    /// exercise the cycle without hardware must set this explicitly.
    /// </summary>
    public bool PermitSafetyBypassForSimulation { get; set; }
}

public sealed class RobotCycleService
{
    private readonly Action<string, string> _auditSink;
    private readonly RobotCycleOptions _options;
    private readonly Stopwatch _elapsed = new();
    private readonly List<RobotCycleTransition> _transitions = new();

    public RobotCycleService(Action<string, string>? auditSink = null, RobotCycleOptions? options = null)
    {
        _auditSink = auditSink ?? ((category, message) => WorkflowState.Instance.AddEvent(category, message));
        _options = options ?? new RobotCycleOptions();
    }

    public RobotCycleState CurrentState { get; private set; } = RobotCycleState.Idle;
    public IntegrationCommandResult? LastResult { get; private set; }
    public AnalysisResult? LastAnalysis { get; private set; }
    public string LastError { get; private set; } = string.Empty;
    public string CycleId { get; private set; } = string.Empty;
    public string BoardId { get; private set; } = string.Empty;
    public DateTime? StartedAtUtc { get; private set; }
    public DateTime? CompletedAtUtc { get; private set; }
    public DateTime? LastTransitionAtUtc { get; private set; }
    public TimeSpan Elapsed => _elapsed.Elapsed;
    public IReadOnlyList<RobotCycleTransition> Transitions => _transitions;

    public event Action<RobotCycleState>? StateChanged;

    public async Task<IntegrationCommandResult> LoadAsync(
        LoadCommand command,
        CancellationToken cancellationToken = default)
    {
        if (CurrentState is not (RobotCycleState.Idle or RobotCycleState.Completed or RobotCycleState.Canceled))
            return InvalidTransition("load", RobotCycleState.Idle, RobotCycleState.Completed, RobotCycleState.Canceled);

        StartNewCycle(command.BoardId);
        TransitionTo(RobotCycleState.Loading, $"Loading board {BoardId}.");

        var result = await ExecuteRobotCommandAsync(
            "Load",
            token => IntegrationBoundaryRegistry.RobotController.LoadAsync(command, token),
            cancellationToken).ConfigureAwait(false);

        if (!result.Accepted)
            return CurrentState is RobotCycleState.EmergencyStopped or RobotCycleState.Canceled ? result : FaultFromResult(result);

        TransitionTo(RobotCycleState.Loaded, result.Message);
        return result;
    }

    public async Task<IntegrationCommandResult> MoveToInspectAsync(
        InspectCommand command,
        CancellationToken cancellationToken = default)
    {
        if (CurrentState is not RobotCycleState.Loaded)
            return InvalidTransition("move to inspect", RobotCycleState.Loaded);

        TransitionTo(RobotCycleState.MovingToInspect, $"Moving board {BoardId} to inspect position for {command.ViewType}.");

        var result = await ExecuteRobotCommandAsync(
            "MoveToInspect",
            token => IntegrationBoundaryRegistry.RobotController.InspectAsync(command, token),
            cancellationToken).ConfigureAwait(false);

        if (!result.Accepted)
            return CurrentState is RobotCycleState.EmergencyStopped or RobotCycleState.Canceled ? result : FaultFromResult(result);

        TransitionTo(RobotCycleState.ReadyToInspect, result.Message);
        return result;
    }

    public async Task<AnalysisResult?> RunInspectionStepAsync(
        Func<CancellationToken, Task<AnalysisResult?>> inspectionAction,
        CancellationToken cancellationToken = default)
    {
        if (CurrentState is not RobotCycleState.ReadyToInspect)
        {
            InvalidTransition("run inspection", RobotCycleState.ReadyToInspect);
            return null;
        }

        if (BlockIfEmergencyStopped("Run inspection") is not null)
            return null;

        TransitionTo(RobotCycleState.Inspecting, $"Running inspection for board {BoardId}.");

        try
        {
            var analysis = await inspectionAction(cancellationToken).ConfigureAwait(false);
            if (BlockIfEmergencyStopped("Run inspection") is not null)
                return null;

            if (analysis is null)
            {
                Fault("Inspection action did not return an analysis result.");
                return null;
            }

            LastAnalysis = analysis;
            LastResult = new IntegrationCommandResult(
                true,
                IntegrationBoundaryRegistry.RobotController.Status,
                $"Inspection action completed for board {BoardId}.");
            return analysis;
        }
        catch (OperationCanceledException)
        {
            Cancel("Inspection action canceled.");
            return null;
        }
        catch (Exception ex)
        {
            Fault($"Inspection action failed: {ex.Message}");
            return null;
        }
    }

    public async Task<IntegrationCommandResult> UnloadAsync(
        UnloadCommand command,
        CancellationToken cancellationToken = default)
    {
        if (CurrentState is not (RobotCycleState.Loaded or RobotCycleState.ReadyToInspect or RobotCycleState.Inspecting))
            return InvalidTransition("unload", RobotCycleState.Loaded, RobotCycleState.ReadyToInspect, RobotCycleState.Inspecting);

        TransitionTo(RobotCycleState.Unloading, $"Unloading board {BoardId}.");

        var result = await ExecuteRobotCommandAsync(
            "Unload",
            token => IntegrationBoundaryRegistry.RobotController.UnloadAsync(command, token),
            cancellationToken).ConfigureAwait(false);

        if (!result.Accepted)
            return CurrentState is RobotCycleState.EmergencyStopped or RobotCycleState.Canceled ? result : FaultFromResult(result);

        TransitionTo(RobotCycleState.Completed, result.Message);
        return result;
    }

    public async Task<IntegrationCommandResult> ResetAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await IntegrationBoundaryRegistry.RobotController.ResetAsync(cancellationToken).ConfigureAwait(false);
            LastResult = result;

            if (!result.Accepted)
                return FaultFromResult(result);

            if (IntegrationBoundaryRegistry.PlcSafetyController.Status != IntegrationConnectionStatus.NotConnected)
                await IntegrationBoundaryRegistry.PlcSafetyController.ResetSafetyFaultAsync(cancellationToken).ConfigureAwait(false);

            if (IntegrationBoundaryRegistry.EmergencyStopMonitor.IsEmergencyStopActive ||
                IntegrationBoundaryRegistry.PlcSafetyController.IsEmergencyStopActive)
                return MarkEmergencyStopped("Reset completed, but emergency stop remains active.");

            LastError = string.Empty;
            LastAnalysis = null;
            BoardId = string.Empty;
            CycleId = string.Empty;
            StartedAtUtc = null;
            CompletedAtUtc = DateTime.UtcNow;
            _elapsed.Reset();
            TransitionTo(RobotCycleState.Idle, result.Message);
            return result;
        }
        catch (OperationCanceledException)
        {
            return Cancel("Robot reset canceled.");
        }
        catch (Exception ex)
        {
            return Fault($"Robot reset failed: {ex.Message}");
        }
    }

    public async Task<RobotCycleRunResult> RunFullCycleAsync(
        LoadCommand loadCommand,
        InspectCommand inspectCommand,
        Func<CancellationToken, Task<AnalysisResult?>> inspectionAction,
        UnloadCommand unloadCommand,
        CancellationToken cancellationToken = default)
    {
        var load = await LoadAsync(loadCommand, cancellationToken).ConfigureAwait(false);
        if (!load.Accepted)
            return CreateRunResult(false, load.Message);

        var move = await MoveToInspectAsync(inspectCommand, cancellationToken).ConfigureAwait(false);
        if (!move.Accepted)
            return CreateRunResult(false, move.Message);

        var analysis = await RunInspectionStepAsync(inspectionAction, cancellationToken).ConfigureAwait(false);
        if (analysis is null)
            return CreateRunResult(false, LastError);

        var unload = await UnloadAsync(unloadCommand, cancellationToken).ConfigureAwait(false);
        if (!unload.Accepted)
            return CreateRunResult(false, unload.Message);

        return CreateRunResult(true, $"Robot cycle {CycleId} completed for board {loadCommand.BoardId}.");
    }

    public bool RefreshEmergencyStopStatus(string message = "Emergency stop is active.")
    {
        if (!IntegrationBoundaryRegistry.EmergencyStopMonitor.IsEmergencyStopActive)
            return false;

        MarkEmergencyStopped(message);
        return true;
    }

    private async Task<IntegrationCommandResult> ExecuteRobotCommandAsync(
        string action,
        Func<CancellationToken, Task<IntegrationCommandResult>> command,
        CancellationToken cancellationToken)
    {
        var safety = BlockIfSafetyNotOk(action);
        if (safety is not null)
            return safety;

        var emergency = BlockIfEmergencyStopped(action);
        if (emergency is not null)
            return emergency;

        try
        {
            var result = await command(cancellationToken).ConfigureAwait(false);
            LastResult = result;

            emergency = BlockIfEmergencyStopped(action);
            return emergency ?? result;
        }
        catch (OperationCanceledException)
        {
            return Cancel($"{action} canceled.");
        }
        catch (Exception ex)
        {
            return Fault($"{action} command failed: {ex.Message}");
        }
    }

    private IntegrationCommandResult? BlockIfEmergencyStopped(string action)
    {
        if (!IntegrationBoundaryRegistry.EmergencyStopMonitor.IsEmergencyStopActive &&
            !IntegrationBoundaryRegistry.PlcSafetyController.IsEmergencyStopActive)
            return null;

        return MarkEmergencyStopped($"{action} blocked because emergency stop is active.");
    }

    private IntegrationCommandResult? BlockIfSafetyNotOk(string action)
    {
        var plc = IntegrationBoundaryRegistry.PlcSafetyController;
        var status = plc.GetSafetyStatus();
        if (status.IsOk)
            return null;

        var simulationBypass =
            _options.PermitSafetyBypassForSimulation &&
            plc.Status == IntegrationConnectionStatus.NotConnected &&
            IntegrationBoundaryRegistry.RobotController.Status != IntegrationConnectionStatus.Ready;
        if (simulationBypass)
        {
            _auditSink("ROBOT_SAFETY_BYPASS", $"{action} permitted because robot simulation is active and no PLC safety controller is configured. No production movement is validated.");
            return null;
        }

        if (status.IsEmergencyStopActive)
            return MarkEmergencyStopped($"{action} blocked by PLC safety interlock: {status.BlockingReason()}.");

        return Fault($"{action} blocked by PLC safety interlock: {status.BlockingReason()}.", IntegrationConnectionStatus.Error);
    }

    private IntegrationCommandResult MarkEmergencyStopped(string message)
    {
        LastError = message;
        LastResult = new IntegrationCommandResult(false, IntegrationConnectionStatus.Error, message);
        TransitionTo(RobotCycleState.EmergencyStopped, message);
        return LastResult;
    }

    private IntegrationCommandResult FaultFromResult(IntegrationCommandResult result)
        => Fault(result.Message, result.Status);

    private IntegrationCommandResult Fault(string message, IntegrationConnectionStatus status = IntegrationConnectionStatus.Error)
    {
        LastError = message;
        LastResult = new IntegrationCommandResult(false, status, message);
        TransitionTo(RobotCycleState.Faulted, message);
        return LastResult;
    }

    private IntegrationCommandResult Cancel(string message)
    {
        LastError = message;
        LastResult = new IntegrationCommandResult(false, IntegrationConnectionStatus.Error, message);
        TransitionTo(RobotCycleState.Canceled, message);
        return LastResult;
    }

    private IntegrationCommandResult InvalidTransition(string action, params RobotCycleState[] allowedStates)
    {
        var allowed = string.Join(", ", allowedStates);
        var message = $"Cannot {action} while robot cycle state is {CurrentState}. Required state: {allowed}.";
        LastError = message;
        LastResult = new IntegrationCommandResult(false, IntegrationConnectionStatus.Error, message);
        _auditSink("ROBOT_CYCLE", message);
        return LastResult;
    }

    private void StartNewCycle(string boardId)
    {
        var timestamp = DateTime.UtcNow;
        CycleId = $"RC-{timestamp:yyyyMMddHHmmssfff}-{Guid.NewGuid().ToString("N")[..6]}";
        BoardId = string.IsNullOrWhiteSpace(boardId) ? "SIM-BOARD" : boardId.Trim();
        StartedAtUtc = timestamp;
        CompletedAtUtc = null;
        LastTransitionAtUtc = timestamp;
        LastError = string.Empty;
        LastResult = null;
        LastAnalysis = null;
        _transitions.Clear();
        _elapsed.Restart();
    }

    private void TransitionTo(RobotCycleState nextState, string message)
    {
        var previous = CurrentState;
        CurrentState = nextState;
        LastTransitionAtUtc = DateTime.UtcNow;
        _transitions.Add(new RobotCycleTransition(previous, nextState, LastTransitionAtUtc.Value, message));

        if (nextState is RobotCycleState.Completed or RobotCycleState.Faulted or RobotCycleState.EmergencyStopped or RobotCycleState.Canceled)
        {
            CompletedAtUtc = LastTransitionAtUtc;
            if (_elapsed.IsRunning)
                _elapsed.Stop();
        }

        _auditSink("ROBOT_CYCLE", $"{CycleIdDisplay()} {previous} -> {nextState}. {message}");
        StateChanged?.Invoke(nextState);
    }

    private RobotCycleRunResult CreateRunResult(bool accepted, string message)
        => new(accepted, CurrentState, LastAnalysis, LastResult, message, Elapsed);

    private string CycleIdDisplay()
        => string.IsNullOrWhiteSpace(CycleId) ? "Robot cycle" : $"Robot cycle {CycleId}";
}
