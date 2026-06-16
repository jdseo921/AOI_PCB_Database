namespace AOI_Monitor.Services;

public sealed class NullCameraSource : ICameraSource
{
    public string Name => "No Camera Connected";
    public CameraViewType SelectedView { get; set; } = CameraViewType.Top;
    public CameraSourceStatus ConnectionStatus => CameraSourceStatus.NotConnected;
    public string StatusMessage => "No camera source is configured.";
    public bool IsAcquiring { get; private set; }

    public void StartAcquisition()
    {
        IsAcquiring = false;
    }

    public void StopAcquisition()
    {
        IsAcquiring = false;
    }

    public CameraFrame? GetNextFrame() => null;
}
