using AOI_Monitor.Services;

namespace AOI_Monitor.Models;

public sealed class CameraSourceSettings
{
    public string SourceKey { get; set; } = CameraSourceFactory.NullSourceKey;
    public string TopFolder { get; set; } = string.Empty;
    public string SideFolder { get; set; } = string.Empty;
    public string BottomFolder { get; set; } = string.Empty;
    public string BoardModel { get; set; } = "TBOX-MAIN";
    public string LotId { get; set; } = "POC-LOT";

    public bool IsFolderSimulation =>
        string.Equals(SourceKey, CameraSourceFactory.FolderSimulationSourceKey, StringComparison.OrdinalIgnoreCase);
}
