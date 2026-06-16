using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public static class CameraSourceFactory
{
    public const string NullSourceKey = "none";
    public const string FolderSimulationSourceKey = "folder-simulation";

    private static ICameraSource _activeSource = new NullCameraSource();

    public static event Action? ActiveSourceChanged;

    public static ICameraSource ActiveSource => _activeSource;

    public static ICameraSource CreateNull() => new NullCameraSource();

    public static FolderCameraSource CreateFolder(IReadOnlyDictionary<CameraViewType, string> folders)
        => new(folders);

    public static ICameraSource Create(CameraSourceSettings settings)
    {
        if (NormalizeSourceKey(settings.SourceKey) != FolderSimulationSourceKey)
            return CreateNull();

        var folders = new Dictionary<CameraViewType, string>();
        if (!string.IsNullOrWhiteSpace(settings.TopFolder))
            folders[CameraViewType.Top] = settings.TopFolder;
        if (!string.IsNullOrWhiteSpace(settings.SideFolder))
            folders[CameraViewType.Side] = settings.SideFolder;
        if (!string.IsNullOrWhiteSpace(settings.BottomFolder))
            folders[CameraViewType.Bottom] = settings.BottomFolder;

        return new FolderCameraSource(folders, settings.BoardModel, settings.LotId);
    }

    public static string NormalizeSourceKey(string? sourceKey)
        => string.IsNullOrWhiteSpace(sourceKey)
            ? NullSourceKey
            : sourceKey.Trim().ToLowerInvariant() switch
            {
                FolderSimulationSourceKey or "folder" or "simulation" => FolderSimulationSourceKey,
                _ => NullSourceKey,
            };

    public static void SetActiveSource(ICameraSource source)
    {
        _activeSource = source;
        ActiveSourceChanged?.Invoke();
    }
}
