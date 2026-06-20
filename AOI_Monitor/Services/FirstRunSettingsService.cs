using System.IO;
using System.Text.Json;
using AOI_Monitor.Data;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public static class FirstRunSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static FirstRunSettings? _cached;

    public static string SettingsPath => Path.Combine(AoiDatabase.StorageRoot, "first_run_settings.json");

    public static FirstRunSettings Load()
    {
        if (_cached is not null)
            return Clone(_cached);

        Directory.CreateDirectory(AoiDatabase.StorageRoot);
        if (!File.Exists(SettingsPath))
        {
            _cached = new FirstRunSettings();
            Save(_cached);
            return Clone(_cached);
        }

        try
        {
            var json = File.ReadAllText(SettingsPath);
            _cached = JsonSerializer.Deserialize<FirstRunSettings>(json) ?? new FirstRunSettings();
        }
        catch
        {
            _cached = new FirstRunSettings();
        }

        return Clone(_cached);
    }

    public static void Save(FirstRunSettings settings)
    {
        Directory.CreateDirectory(AoiDatabase.StorageRoot);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
        _cached = Clone(settings);
    }

    public static bool IsCompleted()
    {
        var settings = Load();
        return settings.Completed &&
            string.Equals(settings.VersionCompleted, FirstRunSettings.CurrentWizardVersion, StringComparison.OrdinalIgnoreCase);
    }

    public static void MarkCompleted()
    {
        Save(new FirstRunSettings
        {
            Completed = true,
            VersionCompleted = FirstRunSettings.CurrentWizardVersion,
            CompletedAtUtc = DateTime.UtcNow,
        });
    }

    public static void ResetForTests()
        => _cached = null;

    private static FirstRunSettings Clone(FirstRunSettings source)
        => new()
        {
            Completed = source.Completed,
            VersionCompleted = source.VersionCompleted,
            CompletedAtUtc = source.CompletedAtUtc,
        };
}
