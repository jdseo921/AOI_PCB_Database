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

public interface ITriggerSynchronizer
{
    bool IsArmed { get; }
    void Arm();
    void Disarm();
}

public interface ILightingController
{
    bool IsAvailable { get; }
    void SetProgram(string viewType, string programName);
}

public interface ICameraStatusDiagnostics
{
    CameraSourceStatus GetStatus();
    IReadOnlyList<string> GetMessages();
}
