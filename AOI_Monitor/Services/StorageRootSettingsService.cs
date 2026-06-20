using System.IO;
using System.Text.Json;
using AOI_Monitor.Data;

namespace AOI_Monitor.Services;

public static class StorageRootSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static string? _settingsDirectoryOverride;

    public static string SettingsDirectory => _settingsDirectoryOverride ?? AoiDatabase.DefaultStorageRoot;
    public static string SettingsPath => Path.Combine(SettingsDirectory, "storage_root_settings.json");

    public static void ConfigureSettingsDirectoryForTests(string? settingsDirectory)
        => _settingsDirectoryOverride = string.IsNullOrWhiteSpace(settingsDirectory)
            ? null
            : Path.GetFullPath(settingsDirectory);

    public static string LoadStorageRoot()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<StorageRootSettings>(json);
                if (!string.IsNullOrWhiteSpace(settings?.StorageRoot))
                    return Path.GetFullPath(settings.StorageRoot.Trim());
            }
        }
        catch
        {
        }

        return AoiDatabase.DefaultStorageRoot;
    }

    public static string ApplySavedStorageRoot()
    {
        var storageRoot = LoadStorageRoot();
        AoiDatabase.ConfigureStorageRoot(storageRoot);
        return storageRoot;
    }

    public static void SaveStorageRoot(string storageRoot)
    {
        if (string.IsNullOrWhiteSpace(storageRoot))
            throw new ArgumentException("Storage root is required.", nameof(storageRoot));

        var fullPath = Path.GetFullPath(storageRoot.Trim());
        Directory.CreateDirectory(SettingsDirectory);
        Directory.CreateDirectory(fullPath);
        File.WriteAllText(
            SettingsPath,
            JsonSerializer.Serialize(new StorageRootSettings { StorageRoot = fullPath }, JsonOptions));
    }

    public static void ResetStorageRoot()
    {
        if (File.Exists(SettingsPath))
            File.Delete(SettingsPath);
    }

    private sealed class StorageRootSettings
    {
        public string StorageRoot { get; set; } = string.Empty;
    }
}
