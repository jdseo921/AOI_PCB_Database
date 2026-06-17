using System.IO;
using System.Text.Json;
using AOI_Monitor.Data;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public static class MesIntegrationSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static MesIntegrationSettings? _cached;

    public static event Action? SettingsChanged;

    public static string SettingsPath => Path.Combine(AoiDatabase.StorageRoot, "mes_integration_settings.json");

    public static MesIntegrationSettings Load()
    {
        if (_cached is not null)
            return Clone(_cached);

        Directory.CreateDirectory(AoiDatabase.StorageRoot);
        if (!File.Exists(SettingsPath))
        {
            _cached = new MesIntegrationSettings();
            Save(_cached, notify: false);
            return Clone(_cached);
        }

        try
        {
            var json = File.ReadAllText(SettingsPath);
            _cached = JsonSerializer.Deserialize<MesIntegrationSettings>(json) ?? new MesIntegrationSettings();
        }
        catch
        {
            _cached = new MesIntegrationSettings();
        }

        Normalize(_cached);
        return Clone(_cached);
    }

    public static void Save(MesIntegrationSettings settings)
        => Save(settings, notify: true);

    public static void ApplyIntegrationBoundary()
    {
        var settings = Load();
        if (settings.Mode == MesIntegrationMode.MockRest)
        {
            var client = new MockMesClient(settings);
            IntegrationBoundaryRegistry.MesClient = client;
            IntegrationBoundaryRegistry.TraceabilityUploader = client;
            return;
        }

        IntegrationBoundaryRegistry.MesClient = new NullMesClient();
        IntegrationBoundaryRegistry.TraceabilityUploader = new NullTraceabilityUploader();
    }

    private static void Save(MesIntegrationSettings settings, bool notify)
    {
        Normalize(settings);
        Directory.CreateDirectory(AoiDatabase.StorageRoot);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
        _cached = Clone(settings);
        ApplyIntegrationBoundary();

        if (notify)
            SettingsChanged?.Invoke();
    }

    private static void Normalize(MesIntegrationSettings settings)
    {
        if (!Enum.IsDefined(settings.Mode))
            settings.Mode = MesIntegrationMode.NotConnected;

        settings.MockEndpointUrl = settings.MockEndpointUrl?.Trim() ?? string.Empty;
        settings.UploadTimeoutSeconds = Math.Clamp(settings.UploadTimeoutSeconds, 1, 300);
    }

    private static MesIntegrationSettings Clone(MesIntegrationSettings source)
        => new()
        {
            Mode = source.Mode,
            MockEndpointUrl = source.MockEndpointUrl,
            UploadTimeoutSeconds = source.UploadTimeoutSeconds,
        };
}
