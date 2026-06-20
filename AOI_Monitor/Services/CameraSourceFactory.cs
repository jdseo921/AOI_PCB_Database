using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public static class CameraSourceFactory
{
    public const string NullSourceKey = "none";
    public const string FolderSimulationSourceKey = "folder-simulation";
    public const string GenericVisionAdapterSourceKey = "generic-vision-adapter";

    private static ICameraSource _activeSource = new NullCameraSource();

    public static event Action? ActiveSourceChanged;

    public static ICameraSource ActiveSource => _activeSource;

    public static ICameraSource CreateNull() => new NullCameraSource();

    public static FolderCameraSource CreateFolder(IReadOnlyDictionary<CameraViewType, string> folders)
        => new(folders);

    public static GenericVisionCameraSource CreateGeneric(CameraSourceSettings settings, IVisionCameraAdapter? adapter = null)
        => new(settings, adapter ?? new NullVisionCameraAdapter());

    public static ICameraSource Create(CameraSourceSettings settings)
        => Create(settings, null);

    public static ICameraSource Create(CameraSourceSettings settings, IVisionCameraAdapter? adapter)
    {
        var sourceKey = NormalizeSourceKey(settings.SourceKey);
        if (sourceKey == GenericVisionAdapterSourceKey)
            return CreateGeneric(settings, adapter);

        if (sourceKey != FolderSimulationSourceKey)
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
                GenericVisionAdapterSourceKey or "generic" or "generic-vision" or "vision" => GenericVisionAdapterSourceKey,
                _ => NullSourceKey,
            };

    public static void SetActiveSource(ICameraSource source)
    {
        _activeSource = source;
        ActiveSourceChanged?.Invoke();
    }
}
