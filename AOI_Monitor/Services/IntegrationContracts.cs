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
}

public interface IMesClient : IIntegrationEndpoint
{
    Task<IntegrationCommandResult> UploadResultAsync(
        UploadResultCommand command,
        CancellationToken cancellationToken = default);

    Task<IntegrationCommandResult> UploadImageAsync(
        UploadImageCommand command,
        CancellationToken cancellationToken = default);
}

public interface ITraceabilityUploader : IIntegrationEndpoint
{
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
    public string StatusMessage => "Stage 3 robot/handler integration boundary only. No robot commands are sent.";

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
}

public sealed class NullMesClient : IMesClient
{
    public string Name => "Null MES Client";
    public IntegrationConnectionStatus Status => IntegrationConnectionStatus.NotConnected;
    public string StatusMessage => "Stage 4 MES/ERP integration boundary only. No production MES writeback is performed.";

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
