namespace AOI_Monitor.Services;

public enum CameraViewType
{
    Top,
    Side,
    Bottom,
}

public enum CameraSourceStatus
{
    NotConnected,
    Simulated,
    Error,
}

public sealed record CameraFrame(
    string FrameId,
    string SourcePath,
    CameraViewType ViewType,
    DateTime CapturedAt,
    string SourceName,
    string BoardModel,
    string LotId)
{
    public string ImagePath => SourcePath;
}
