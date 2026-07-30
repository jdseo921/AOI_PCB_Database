using AOI_Monitor.Models;
using AOI_Monitor.Services;

namespace CameraAdapterTemplate;

public sealed class FakeVisionCameraAdapterFactory : IVisionCameraAdapterFactory
{
    public string AdapterId => "template.fake-camera";
    public string DisplayName => "Template Fake Vision Camera Adapter";
    public string Version => "0.1.0";
    public IReadOnlyList<string> SupportedInterfaces => new[] { "Template", "GigE Vision", "USB3 Vision" };
    public IReadOnlyList<string> SupportedViews => new[] { "Top", "Side", "Bottom" };
    public IReadOnlyList<string> SupportedPixelFormats => new[] { "Mono8", "BayerRG8", "RGB24" };

    public IVisionCameraAdapter CreateAdapter(CameraSourceSettings settings)
        => new FakeVisionCameraAdapter(settings);

    public IVisionDeviceDiscovery? CreateDeviceDiscovery(CameraSourceSettings settings)
        => new FakeVisionDeviceDiscovery();
}

public sealed class FakeVisionDeviceDiscovery : IVisionDeviceDiscovery
{
    public IReadOnlyList<VisionDeviceInfo> DiscoverDevices()
    {
        // Replace this with SDK enumeration, for example:
        // Basler: TlFactory.EnumerateDevices()
        // Hikrobot: MV_CC_EnumDevices_NET()
        // Cognex/Keyence: vendor discovery/session APIs.
        return new[]
        {
            new VisionDeviceInfo(
                "FAKE-CAM-TOP",
                "Template",
                "NoOpCamera",
                "SIM-0001",
                "Template",
                "",
                "Top",
                "Simulated",
                new[] { "software-trigger", "metadata-only", "simulation" }),
        };
    }
}

public sealed class FakeVisionCameraAdapter : IVisionCameraAdapter
{
    private readonly CameraSourceSettings _settings;
    private CameraViewType _viewType = CameraViewType.Top;
    private string _deviceId = "FAKE-CAM";
    private bool _connected;
    private bool _started;
    private int _sequence;

    public FakeVisionCameraAdapter(CameraSourceSettings settings)
    {
        _settings = settings;
    }

    public bool Connect(CameraViewType viewType, string deviceId, CameraAcquisitionMode acquisitionMode, double exposureMs, double gain, int timeoutMs)
    {
        _viewType = viewType;
        _deviceId = string.IsNullOrWhiteSpace(deviceId) ? $"FAKE-CAM-{viewType}" : deviceId.Trim();

        // Vendor SDK connection belongs here:
        // open device by serial/IP, configure exposure/gain, set trigger mode,
        // register frame callback or allocate stream buffers.
        _connected = true;
        return true;
    }

    public void Disconnect()
    {
        // Vendor SDK cleanup belongs here: stop stream, unregister callbacks,
        // release buffers, close handle/session.
        _started = false;
        _connected = false;
    }

    public bool Start()
    {
        _started = _connected;
        return _started;
    }

    public void Stop()
    {
        _started = false;
    }

    public bool Trigger(int timeoutMs)
    {
        // Vendor SDK software trigger belongs here.
        return _connected && _started;
    }

    // The inspection pipeline consumes frames as image files on disk: MonitorView, the
    // benchmark, and every engine reject a frame whose SourcePath is not a readable
    // file. A REAL vendor adapter must therefore persist each captured frame (or a
    // rolling buffer) and set SourcePath to it, reporting the persisted frame's true
    // dimensions. The template demonstrates the obligation with one tiny placeholder
    // PNG written to the temp folder (declared Width/Height stay illustrative values).
    private const string PlaceholderPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

    private string _frameFilePath = "";

    private string EnsureFrameFileOnDisk()
    {
        if (!string.IsNullOrWhiteSpace(_frameFilePath) && File.Exists(_frameFilePath))
            return _frameFilePath;

        try
        {
            var path = Path.Combine(Path.GetTempPath(), "aoi_camera_template_frame.png");
            File.WriteAllBytes(path, Convert.FromBase64String(PlaceholderPngBase64));
            _frameFilePath = path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Frame persistence failed; the frame will carry no SourcePath and the
            // acceptance run will surface the missing-file warning.
            _frameFilePath = "";
        }

        return _frameFilePath;
    }

    public bool TryGetFrame(CameraViewType viewType, int timeoutMs, out CameraFrame? frame)
    {
        if (!_connected || !_started)
        {
            frame = null;
            return false;
        }

        var now = DateTime.UtcNow;
        _sequence++;
        frame = new CameraFrame(
            FrameId: $"FAKE-{viewType}-{_sequence:D6}",
            SourcePath: EnsureFrameFileOnDisk(),
            ViewType: viewType,
            CapturedAt: now,
            SourceName: "Template Fake Vision Camera Adapter",
            BoardModel: _settings.BoardModel,
            LotId: _settings.LotId,
            CameraId: _deviceId,
            CapturedAtUtc: now,
            Width: 1280,
            Height: 960,
            PixelFormat: "Mono8",
            SourceKind: "TemplateFakeCamera",
            IsSimulated: true);
        return true;
    }

    public VisionCameraDiagnostics GetDiagnostics()
        => _connected
            ? new VisionCameraDiagnostics(CameraSourceStatus.Simulated, "Template fake camera is connected. No real hardware is used.", new[] { "Simulation-only adapter.", $"Device={_deviceId}", $"View={_viewType}" })
            : new VisionCameraDiagnostics(CameraSourceStatus.NotConnected, "Template fake camera is not connected.", new[] { "No vendor SDK call has been made." });
}
