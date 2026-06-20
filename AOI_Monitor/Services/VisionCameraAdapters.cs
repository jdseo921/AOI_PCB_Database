using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public sealed record VisionCameraDiagnostics(
    CameraSourceStatus Status,
    string Message,
    IReadOnlyList<string> Details);

/// <summary>
/// Stage 2 vendor SDK boundary for GigE/USB3 Vision cameras.
/// Vendor-specific adapters can implement this interface later without changing
/// the WPF UI, inspection workflow, or saved camera configuration model.
/// </summary>
public interface IVisionCameraAdapter
{
    bool Connect(CameraViewType viewType, string deviceId, CameraAcquisitionMode acquisitionMode, double exposureMs, double gain, int timeoutMs);
    void Disconnect();
    bool Start();
    void Stop();
    bool Trigger(int timeoutMs);
    bool TryGetFrame(CameraViewType viewType, int timeoutMs, out CameraFrame? frame);
    VisionCameraDiagnostics GetDiagnostics();
}

public sealed class NullVisionCameraAdapter : IVisionCameraAdapter
{
    public bool Connect(CameraViewType viewType, string deviceId, CameraAcquisitionMode acquisitionMode, double exposureMs, double gain, int timeoutMs) => false;
    public void Disconnect()
    {
    }

    public bool Start() => false;
    public void Stop()
    {
    }

    public bool Trigger(int timeoutMs) => false;

    public bool TryGetFrame(CameraViewType viewType, int timeoutMs, out CameraFrame? frame)
    {
        frame = null;
        return false;
    }

    public VisionCameraDiagnostics GetDiagnostics()
        => new(
            CameraSourceStatus.NotConnected,
            "Generic Vision Adapter is configured, but no vendor SDK adapter is installed.",
            new[]
            {
                "This is the safe default Stage 2 boundary.",
                "Install or inject a vendor-specific IVisionCameraAdapter to connect real GigE/USB3 Vision hardware.",
                "No real camera readiness is claimed by this adapter.",
            });
}
