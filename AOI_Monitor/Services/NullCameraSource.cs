namespace AOI_Monitor.Services;

public sealed class NullCameraSource : ICameraSource
{
    public string Name => "No Camera Connected";
    public CameraViewType SelectedView { get; set; } = CameraViewType.Top;
    public CameraConnectionStatus ConnectionStatus => CameraConnectionStatus.NotConnected;
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
