namespace AOI_Monitor.Services;

public interface ICameraSource
{
    string Name { get; }
    CameraViewType SelectedView { get; set; }
    CameraSourceStatus ConnectionStatus { get; }
    string StatusMessage { get; }
    bool IsAcquiring { get; }

    void StartAcquisition();
    void StopAcquisition();
    CameraFrame? GetNextFrame();
}

public interface ICameraStatusDiagnostics
{
    CameraSourceStatus GetStatus();
    IReadOnlyList<string> GetMessages();
}
