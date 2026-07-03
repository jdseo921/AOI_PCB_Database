using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Xunit;

namespace AOI_Monitor.Tests;

/// <summary>
/// Locks the Stage 2 camera drop-in seam: any future vendor SDK adapter that implements
/// <see cref="IVisionCameraAdapter"/> flows through <see cref="GenericVisionCameraSource"/> and the
/// shared <see cref="ICameraSource"/> contract that <c>MonitorView</c> consumes at runtime
/// (StartAcquisition -> GetNextFrame -> StopAcquisition), without any UI or inspection-workflow change.
/// </summary>
public sealed class CameraSourceContractTests
{
    // Minimal in-memory stand-in for a real vendor adapter (Basler/Hikrobot/etc.). Produces a
    // simulated frame while started, and nothing when stopped.
    private sealed class StubVisionCameraAdapter : IVisionCameraAdapter
    {
        private bool _started;

        public bool Connect(CameraViewType viewType, string deviceId, CameraAcquisitionMode acquisitionMode, double exposureMs, double gain, int timeoutMs) => true;
        public void Disconnect() => _started = false;
        public bool Start()
        {
            _started = true;
            return true;
        }

        public void Stop() => _started = false;
        public bool Trigger(int timeoutMs) => _started;

        public bool TryGetFrame(CameraViewType viewType, int timeoutMs, out CameraFrame? frame)
        {
            frame = _started
                ? new CameraFrame("F1", "/sim/frame.png", viewType, DateTime.UtcNow, "stub", "BOARD-X", "LOT-Y", Width: 640, Height: 480, PixelFormat: "Mono8", IsSimulated: true)
                : null;
            return frame is not null;
        }

        public VisionCameraDiagnostics GetDiagnostics()
            => new(CameraSourceStatus.Simulated, "stub adapter ready", new[] { "simulation" });
    }

    [Fact]
    public void GenericVisionCameraSourceDeliversAdapterFramesThroughICameraSource()
    {
        var settings = new CameraSourceSettings
        {
            SourceKey = CameraSourceFactory.GenericVisionAdapterSourceKey,
            TopDeviceId = "CAM-TOP",
            AcquisitionMode = CameraAcquisitionMode.SoftwareTrigger,
        };

        ICameraSource source = new GenericVisionCameraSource(settings, new StubVisionCameraAdapter())
        {
            SelectedView = CameraViewType.Top,
        };

        // No frames until acquisition starts.
        Assert.Null(source.GetNextFrame());

        source.StartAcquisition();
        Assert.True(source.IsAcquiring);

        var frame = source.GetNextFrame();

        Assert.NotNull(frame);
        Assert.Equal(CameraViewType.Top, frame!.ViewType);
        Assert.Equal("Generic Vision Adapter", frame.SourceName); // normalized by the source
        Assert.Equal("CAM-TOP", frame.CameraId);                  // filled from the configured device id
        Assert.Equal(640, frame.Width);
        Assert.True(frame.IsSimulated);                           // simulation evidence is preserved, never claimed real

        source.StopAcquisition();
        Assert.False(source.IsAcquiring);
        Assert.Null(source.GetNextFrame());
    }

    [Fact]
    public void GenericVisionCameraSourceDefaultsToSafeNotConnectedAdapter()
    {
        // With no injected vendor adapter, the source falls back to the diagnostic Null adapter and
        // never fabricates frames or claims a real camera.
        var source = new GenericVisionCameraSource(new CameraSourceSettings { TopDeviceId = "CAM-TOP" });

        Assert.Equal(CameraSourceStatus.NotConnected, source.ConnectionStatus);
        source.StartAcquisition();
        Assert.False(source.IsAcquiring);
        Assert.Null(source.GetNextFrame());
    }
}
