using AOI_Monitor.Services;

namespace AOI_Monitor.Models;

public sealed class CameraSourceSettings
{
    public string SourceKey { get; set; } = CameraSourceFactory.NullSourceKey;
    public string TopFolder { get; set; } = string.Empty;
    public string SideFolder { get; set; } = string.Empty;
    public string BottomFolder { get; set; } = string.Empty;
    public string TopDeviceId { get; set; } = string.Empty;
    public string SideDeviceId { get; set; } = string.Empty;
    public string BottomDeviceId { get; set; } = string.Empty;
    public string AdapterFolder { get; set; } = string.Empty;
    public CameraAcquisitionMode AcquisitionMode { get; set; } = CameraAcquisitionMode.Continuous;
    public double ExposureMs { get; set; } = 5.0;
    public double Gain { get; set; } = 1.0;
    public int TriggerTimeoutMs { get; set; } = 250;
    public int FrameTimeoutMs { get; set; } = 1000;
    public string BoardModel { get; set; } = "TBOX-MAIN";
    public string LotId { get; set; } = "POC-LOT";

    public bool IsFolderSimulation =>
        string.Equals(SourceKey, CameraSourceFactory.FolderSimulationSourceKey, StringComparison.OrdinalIgnoreCase);

    public bool IsGenericVisionAdapter =>
        string.Equals(SourceKey, CameraSourceFactory.GenericVisionAdapterSourceKey, StringComparison.OrdinalIgnoreCase);
}

public enum CameraAcquisitionMode
{
    Continuous,
    SoftwareTrigger,
    HardwareTrigger,
}
