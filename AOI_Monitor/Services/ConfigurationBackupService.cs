using System.Reflection;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AOI_Monitor.Data;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public sealed class ConfigurationBackupPackage
{
    public string SchemaVersion { get; set; } = ConfigurationBackupService.CurrentSchemaVersion;
    public int DatabaseSchemaVersion { get; set; } = AoiDatabase.LatestSchemaVersion;
    public string AppVersion { get; set; } = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string SourceStorageRoot { get; set; } = string.Empty;
    public Dictionary<string, JsonElement> Settings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<ModelRegistryRecord> ModelRegistry { get; set; } = new();
    public List<ThresholdProfile> ThresholdProfiles { get; set; } = new();
    public List<RecipeRevisionRecord> RecipeRevisions { get; set; } = new();
    public List<string> ExcludedData { get; set; } = new();
}

public sealed class ConfigurationRestorePreview
{
    public bool IsCompatible { get; set; }
    public string SchemaVersion { get; set; } = string.Empty;
    public int DatabaseSchemaVersion { get; set; }
    public int ModelRegistryCount { get; set; }
    public int ThresholdProfileCount { get; set; }
    public int RecipeRevisionCount { get; set; }
    public List<string> SettingsKeys { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public List<string> BlockingIssues { get; set; } = new();
    public string Summary => IsCompatible
        ? $"Compatible backup: models={ModelRegistryCount}, thresholdProfiles={ThresholdProfileCount}, recipes={RecipeRevisionCount}."
        : $"Backup cannot be restored: {string.Join(" ", BlockingIssues)}";
}

public sealed record ConfigurationBackupResult(string BackupPath, ConfigurationBackupPackage Package);

public static class ConfigurationBackupService
{
    public const string CurrentSchemaVersion = "configuration-backup/v1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static ConfigurationBackupResult Export(string outputFolder, string operatorId = "UNKNOWN")
    {
        if (string.IsNullOrWhiteSpace(outputFolder))
            throw new ArgumentException("Output folder is required.", nameof(outputFolder));

        AoiDatabase.Initialize();
        Directory.CreateDirectory(outputFolder);
        var package = BuildPackage();
        var path = Path.Combine(outputFolder, $"aoi_configuration_backup_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(package, JsonOptions));
        AoiDatabase.RecordAuditEvent("CONFIG_BACKUP", $"Configuration backup exported: {Path.GetFileName(path)}.", operatorWithRole: operatorId, relatedEntityType: "ConfigurationBackup", relatedPath: path);
        return new ConfigurationBackupResult(path, package);
    }

    public static ConfigurationRestorePreview Preview(string backupPath)
    {
        var preview = new ConfigurationRestorePreview();
        try
        {
            var package = ReadPackage(backupPath);
            preview.SchemaVersion = package.SchemaVersion;
            preview.DatabaseSchemaVersion = package.DatabaseSchemaVersion;
            preview.ModelRegistryCount = package.ModelRegistry.Count;
            preview.ThresholdProfileCount = package.ThresholdProfiles.Count;
            preview.RecipeRevisionCount = package.RecipeRevisions.Count;
            preview.SettingsKeys = package.Settings.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToList();

            if (!string.Equals(package.SchemaVersion, CurrentSchemaVersion, StringComparison.OrdinalIgnoreCase))
                preview.BlockingIssues.Add($"Unsupported backup schema {package.SchemaVersion}; expected {CurrentSchemaVersion}.");
            if (package.DatabaseSchemaVersion > AoiDatabase.LatestSchemaVersion)
                preview.BlockingIssues.Add($"Backup database schema {package.DatabaseSchemaVersion} is newer than this app supports ({AoiDatabase.LatestSchemaVersion}).");
            if (package.ExcludedData.Count == 0)
                preview.Warnings.Add("Backup does not list excluded runtime/customer image data.");
            preview.IsCompatible = preview.BlockingIssues.Count == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            preview.BlockingIssues.Add(ex.Message);
            preview.IsCompatible = false;
        }

        return preview;
    }

    public static ConfigurationRestorePreview Import(string backupPath, string operatorId = "UNKNOWN")
    {
        var preview = Preview(backupPath);
        if (!preview.IsCompatible)
            return preview;

        var package = ReadPackage(backupPath);
        AoiDatabase.Initialize();
        ApplySettings(package.Settings);
        foreach (var record in package.ModelRegistry)
            AoiDatabase.UpsertModelRegistryRecord(record);
        foreach (var profile in package.ThresholdProfiles)
        {
            AoiDatabase.SaveThresholdProfile(profile);
            if (string.Equals(profile.Status, "Deployed", StringComparison.OrdinalIgnoreCase))
                AoiDatabase.DeployThresholdProfile(profile, operatorId);
        }
        foreach (var recipe in package.RecipeRevisions)
            AoiDatabase.SaveRecipeRevision(recipe.RecipeName, recipe.BoardProgram, recipe.OperatorId, recipe.DetectionPriority, recipe.BackgroundImagePath, recipe.RecipeJson);

        AoiDatabase.RecordAuditEvent("CONFIG_RESTORE", $"Configuration backup restored: {Path.GetFileName(backupPath)}.", operatorWithRole: operatorId, relatedEntityType: "ConfigurationBackup", relatedPath: backupPath);
        InspectionModelConfigurationService.NotifyExternalConfigurationChanged();
        return preview;
    }

    private static ConfigurationBackupPackage BuildPackage()
    {
        return new ConfigurationBackupPackage
        {
            DatabaseSchemaVersion = AoiDatabase.LatestSchemaVersion,
            SourceStorageRoot = AoiDatabase.StorageRoot,
            Settings = ReadSettings(),
            ModelRegistry = AoiDatabase.GetModelRegistryRecords().ToList(),
            ThresholdProfiles = AoiDatabase.GetThresholdProfiles().ToList(),
            RecipeRevisions = AoiDatabase.GetRecipeRevisions().ToList(),
            ExcludedData =
            {
                "image_vault/",
                "training/",
                "exports/",
                "customer images and datasets",
                "raw production images",
                "SQLite database runtime files",
            },
        };
    }

    private static Dictionary<string, JsonElement> ReadSettings()
    {
        var settings = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, path) in SettingsFiles())
        {
            if (File.Exists(path))
                settings[key] = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(path));
        }

        settings.TryAdd("inspectionModel", JsonSerializer.SerializeToElement(InspectionModelConfigurationService.Load(), JsonOptions));
        settings.TryAdd("cameraSource", JsonSerializer.SerializeToElement(CameraSourceSettingsService.Load(), JsonOptions));
        settings.TryAdd("lighting", JsonSerializer.SerializeToElement(LightingSettingsService.Load(), JsonOptions));
        settings.TryAdd("mesIntegration", JsonSerializer.SerializeToElement(MesIntegrationSettingsService.Load(), JsonOptions));
        settings.TryAdd("centralSync", JsonSerializer.SerializeToElement(CentralSyncSettingsService.Load(), JsonOptions));
        settings.TryAdd("deploymentProfile", JsonSerializer.SerializeToElement(DeploymentProfileSettingsService.Load(), JsonOptions));
        return settings;
    }

    private static void ApplySettings(Dictionary<string, JsonElement> settings)
    {
        Directory.CreateDirectory(AoiDatabase.StorageRoot);
        foreach (var (key, path) in SettingsFiles())
        {
            if (settings.TryGetValue(key, out var value))
                File.WriteAllText(path, value.GetRawText());
        }

        if (settings.TryGetValue("inspectionModel", out var inspection))
            InspectionModelConfigurationService.Save(inspection.Deserialize<InspectionModelConfiguration>(JsonOptions) ?? new InspectionModelConfiguration());
        if (settings.TryGetValue("cameraSource", out var camera))
            CameraSourceSettingsService.Save(camera.Deserialize<CameraSourceSettings>(JsonOptions) ?? new CameraSourceSettings());
        if (settings.TryGetValue("lighting", out var lighting))
            LightingSettingsService.Save(lighting.Deserialize<LightingSettings>(JsonOptions) ?? new LightingSettings());
        if (settings.TryGetValue("mesIntegration", out var mes))
            MesIntegrationSettingsService.Save(mes.Deserialize<MesIntegrationSettings>(JsonOptions) ?? new MesIntegrationSettings());
        if (settings.TryGetValue("centralSync", out var central))
            CentralSyncSettingsService.Save(central.Deserialize<CentralSyncSettings>(JsonOptions) ?? new CentralSyncSettings());
        if (settings.TryGetValue("deploymentProfile", out var profile) &&
            profile.Deserialize<DeploymentProfile>(JsonOptions) is { } deploymentProfile)
        {
            DeploymentProfileSettingsService.Save(deploymentProfile);
        }
        if (settings.TryGetValue("authentication", out var auth) &&
            auth.Deserialize<AuthenticationSettings>(JsonOptions) is { } authenticationSettings)
        {
            AuthenticationSettingsService.Save(authenticationSettings);
        }
    }

    private static ConfigurationBackupPackage ReadPackage(string backupPath)
    {
        if (string.IsNullOrWhiteSpace(backupPath) || !File.Exists(backupPath))
            throw new FileNotFoundException("Configuration backup file was not found.", backupPath);

        return JsonSerializer.Deserialize<ConfigurationBackupPackage>(File.ReadAllText(backupPath), JsonOptions)
            ?? throw new InvalidDataException("Configuration backup could not be read.");
    }

    private static IEnumerable<(string Key, string Path)> SettingsFiles()
    {
        yield return ("firstRun", FirstRunSettingsService.SettingsPath);
        yield return ("inspectionModel", InspectionModelConfigurationService.ConfigurationPath);
        yield return ("cameraSource", CameraSourceSettingsService.SettingsPath);
        yield return ("lighting", LightingSettingsService.SettingsPath);
        yield return ("mesIntegration", MesIntegrationSettingsService.SettingsPath);
        yield return ("centralSync", CentralSyncSettingsService.SettingsPath);
        yield return ("deploymentProfile", DeploymentProfileSettingsService.SettingsPath);
        yield return ("authentication", AuthenticationSettingsService.SettingsPath);
        yield return ("localUsers", AuthenticationSettingsService.LocalUsersPath);
    }
}
