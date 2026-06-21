using System.IO;
using System.Text.Json;
using AOI_Monitor.Data;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public static class DeploymentProfileSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static DeploymentProfile? _cached;

    public static event Action? SettingsChanged;

    public static string SettingsPath => Path.Combine(AoiDatabase.StorageRoot, "deployment_profile_settings.json");

    public static DeploymentProfile Load()
    {
        if (_cached is { } cached)
            return cached;

        if (!File.Exists(SettingsPath))
        {
            _cached = DeploymentProfile.Stage1ImageValidation;
            Save(_cached.Value, notify: false);
            return _cached.Value;
        }

        try
        {
            var settings = JsonSerializer.Deserialize<DeploymentProfileSettings>(File.ReadAllText(SettingsPath));
            _cached = settings?.Profile ?? DeploymentProfile.Stage1ImageValidation;
        }
        catch
        {
            _cached = DeploymentProfile.Stage1ImageValidation;
        }

        return _cached.Value;
    }

    public static void Save(DeploymentProfile profile)
        => Save(profile, notify: true);

    public static void ResetForTests()
    {
        _cached = null;
        SettingsChanged = null;
    }

    private static void Save(DeploymentProfile profile, bool notify)
    {
        Directory.CreateDirectory(AoiDatabase.StorageRoot);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new DeploymentProfileSettings { Profile = profile }, JsonOptions));
        _cached = profile;
        if (notify)
            SettingsChanged?.Invoke();
    }

    private sealed class DeploymentProfileSettings
    {
        public DeploymentProfile Profile { get; set; } = DeploymentProfile.Stage1ImageValidation;
    }
}
