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
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"Camera source settings load fallback used: {ex.Message}");
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
        settings.TopDeviceId = settings.TopDeviceId?.Trim() ?? string.Empty;
        settings.SideDeviceId = settings.SideDeviceId?.Trim() ?? string.Empty;
        settings.BottomDeviceId = settings.BottomDeviceId?.Trim() ?? string.Empty;
        settings.AdapterFolder = settings.AdapterFolder?.Trim() ?? string.Empty;
        settings.ExposureMs = Math.Clamp(settings.ExposureMs <= 0 ? 5.0 : settings.ExposureMs, 0.001, 10000.0);
        settings.Gain = Math.Clamp(settings.Gain < 0 ? 1.0 : settings.Gain, 0.0, 1000.0);
        settings.TriggerTimeoutMs = Math.Clamp(settings.TriggerTimeoutMs <= 0 ? 250 : settings.TriggerTimeoutMs, 1, 60000);
        settings.FrameTimeoutMs = Math.Clamp(settings.FrameTimeoutMs <= 0 ? 1000 : settings.FrameTimeoutMs, 1, 60000);
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
            TopDeviceId = source.TopDeviceId,
            SideDeviceId = source.SideDeviceId,
            BottomDeviceId = source.BottomDeviceId,
            AdapterFolder = source.AdapterFolder,
            AcquisitionMode = source.AcquisitionMode,
            ExposureMs = source.ExposureMs,
            Gain = source.Gain,
            TriggerTimeoutMs = source.TriggerTimeoutMs,
            FrameTimeoutMs = source.FrameTimeoutMs,
            BoardModel = source.BoardModel,
            LotId = source.LotId,
        };
}
