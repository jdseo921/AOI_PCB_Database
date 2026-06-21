using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public sealed class GenericVisionCameraSource : ICameraSource, ICameraStatusDiagnostics
{
    private readonly CameraSourceSettings _settings;
    private readonly IVisionCameraAdapter _adapter;

    public GenericVisionCameraSource(CameraSourceSettings settings)
        : this(settings, new NullVisionCameraAdapter())
    {
    }

    public GenericVisionCameraSource(CameraSourceSettings settings, IVisionCameraAdapter adapter)
    {
        _settings = settings;
        _adapter = adapter;
    }

    public string Name => "Generic Vision Adapter";
    public CameraViewType SelectedView { get; set; } = CameraViewType.Top;
    public CameraSourceStatus ConnectionStatus => _adapter.GetDiagnostics().Status;
    public string StatusMessage => _adapter.GetDiagnostics().Message;
    public bool IsAcquiring { get; private set; }

    public void StartAcquisition()
    {
        var deviceId = DeviceIdForView(SelectedView);
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            IsAcquiring = false;
            return;
        }

        var connected = _adapter.Connect(
            SelectedView,
            deviceId,
            _settings.AcquisitionMode,
            _settings.ExposureMs,
            _settings.Gain,
            _settings.FrameTimeoutMs);
        IsAcquiring = connected && _adapter.Start();
    }

    public void StopAcquisition()
    {
        try
        {
            _adapter.Stop();
            _adapter.Disconnect();
        }
        finally
        {
            IsAcquiring = false;
        }
    }

    public CameraFrame? GetNextFrame()
    {
        if (!IsAcquiring)
            return null;

        if (_settings.AcquisitionMode == CameraAcquisitionMode.SoftwareTrigger &&
            !_adapter.Trigger(_settings.TriggerTimeoutMs))
        {
            return null;
        }

        return _adapter.TryGetFrame(SelectedView, _settings.FrameTimeoutMs, out var frame) && frame is not null
            ? NormalizeFrame(frame)
            : null;
    }

    public CameraSourceStatus GetStatus() => ConnectionStatus;

    public IReadOnlyList<string> GetMessages()
    {
        var diagnostics = _adapter.GetDiagnostics();
        return diagnostics.Details.Count == 0
            ? new[] { diagnostics.Message }
            : diagnostics.Details;
    }

    private CameraFrame NormalizeFrame(CameraFrame frame)
    {
        var capturedAtUtc = frame.CapturedAtUtc ?? frame.CapturedAt.ToUniversalTime();
        var cameraId = string.IsNullOrWhiteSpace(frame.CameraId)
            ? DeviceIdForView(SelectedView)
            : frame.CameraId;

        return frame with
        {
            ViewType = SelectedView,
            SourceName = Name,
            BoardModel = string.IsNullOrWhiteSpace(frame.BoardModel) ? _settings.BoardModel : frame.BoardModel,
            LotId = string.IsNullOrWhiteSpace(frame.LotId) ? _settings.LotId : frame.LotId,
            CameraId = cameraId,
            CapturedAtUtc = capturedAtUtc,
            SourceKind = string.IsNullOrWhiteSpace(frame.SourceKind) ? "GenericVisionAdapter" : frame.SourceKind,
            IsSimulated = frame.IsSimulated,
        };
    }

    private string DeviceIdForView(CameraViewType viewType)
        => (viewType switch
        {
            CameraViewType.Side => _settings.SideDeviceId,
            CameraViewType.Bottom => _settings.BottomDeviceId,
            _ => _settings.TopDeviceId,
        })?.Trim() ?? string.Empty;
}
