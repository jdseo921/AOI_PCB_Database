using System.IO;
using System.Text.Json;
using AOI_Monitor.Data;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public static class CameraSourceSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static CameraSourceSettings? _cached;

    public static event Action? SettingsChanged;

    public static string SettingsPath => Path.Combine(AoiDatabase.StorageRoot, "camera_source_settings.json");

    public static CameraSourceSettings Load()
    {
        if (_cached is not null)
            return Clone(_cached);

        Directory.CreateDirectory(AoiDatabase.StorageRoot);
        if (!File.Exists(SettingsPath))
        {
            _cached = new CameraSourceSettings();
            Save(_cached, notify: false);
            return Clone(_cached);
        }

        try
        {
            var json = File.ReadAllText(SettingsPath);
            _cached = JsonSerializer.Deserialize<CameraSourceSettings>(json) ?? new CameraSourceSettings();
        }
        catch
        {
            _cached = new CameraSourceSettings();
        }

        Normalize(_cached);
        return Clone(_cached);
    }

    public static void Save(CameraSourceSettings settings)
        => Save(settings, notify: true);

    public static void ApplyActiveSource()
        => CameraSourceFactory.SetActiveSource(CameraSourceFactory.Create(Load()));

    private static void Save(CameraSourceSettings settings, bool notify)
    {
        Normalize(settings);
        Directory.CreateDirectory(AoiDatabase.StorageRoot);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
        _cached = Clone(settings);

        if (notify)
            SettingsChanged?.Invoke();
    }

    private static void Normalize(CameraSourceSettings settings)
    {
        settings.SourceKey = CameraSourceFactory.NormalizeSourceKey(settings.SourceKey);
        settings.TopFolder = settings.TopFolder?.Trim() ?? string.Empty;
        settings.SideFolder = settings.SideFolder?.Trim() ?? string.Empty;
        settings.BottomFolder = settings.BottomFolder?.Trim() ?? string.Empty;
        settings.BoardModel = string.IsNullOrWhiteSpace(settings.BoardModel) ? "TBOX-MAIN" : settings.BoardModel.Trim();
        settings.LotId = string.IsNullOrWhiteSpace(settings.LotId) ? "POC-LOT" : settings.LotId.Trim();
    }

    private static CameraSourceSettings Clone(CameraSourceSettings source)
        => new()
        {
            SourceKey = source.SourceKey,
            TopFolder = source.TopFolder,
            SideFolder = source.SideFolder,
            BottomFolder = source.BottomFolder,
            BoardModel = source.BoardModel,
            LotId = source.LotId,
        };
}
