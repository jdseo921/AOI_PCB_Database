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
    Ready,
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
    string LotId,
    string CameraId = "",
    DateTime? CapturedAtUtc = null,
    int Width = 0,
    int Height = 0,
    string PixelFormat = "",
    string SourceKind = "File",
    bool IsSimulated = false)
{
    public string ImagePath => SourcePath;
    public DateTime EffectiveCapturedAtUtc => CapturedAtUtc ?? CapturedAt.ToUniversalTime();
}
