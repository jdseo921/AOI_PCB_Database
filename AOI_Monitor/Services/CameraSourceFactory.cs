namespace AOI_Monitor.Services;

public static class CameraSourceFactory
{
    private static ICameraSource _activeSource = new NullCameraSource();

    public static event Action? ActiveSourceChanged;

    public static ICameraSource ActiveSource => _activeSource;

    public static ICameraSource CreateNull() => new NullCameraSource();

    public static FolderCameraSource CreateFolder(IReadOnlyDictionary<CameraViewType, string> folders)
        => new(folders);

    public static void SetActiveSource(ICameraSource source)
    {
        _activeSource = source;
        ActiveSourceChanged?.Invoke();
    }
}
