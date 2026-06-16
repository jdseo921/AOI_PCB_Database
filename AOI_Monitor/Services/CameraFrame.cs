namespace AOI_Monitor.Services;

public enum CameraViewType
{
    Top,
    Side,
    Bottom,
}

public enum CameraConnectionStatus
{
    NotConnected,
    Simulated,
    Error,
}

public sealed record CameraFrame(
    string ImagePath,
    CameraViewType ViewType,
    DateTime CapturedAt,
    string SourceName);
