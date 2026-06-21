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
    public DefectTaxonomySnapshot? ActiveTaxonomy { get; set; }
    public List<string> PluginFolderReferences { get; set; } = new();
    public List<string> ExcludedData { get; set; } = new();
}

public sealed class ConfigurationRestorePreview
{
    public bool IsCompatible { get; set; }
    public string SchemaVersion { get; set; } = string.Empty;
    public int DatabaseSchemaVersion { get; set; }
    public string TargetStoragePath { get; set; } = string.Empty;
    public int ModelRegistryCount { get; set; }
    public int ThresholdProfileCount { get; set; }
    public int RecipeRevisionCount { get; set; }
    public List<string> SettingsKeys { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public List<string> BlockingIssues { get; set; } = new();
    public List<string> Conflicts { get; set; } = new();
    public List<string> MissingModelFiles { get; set; } = new();
    public List<string> MissingPluginFolders { get; set; } = new();
    public List<string> SettingsChanges { get; set; } = new();
    public List<string> SecretProtectionStatus { get; set; } = new();
    public string Summary => IsCompatible
        ? $"Compatible backup: models={ModelRegistryCount}, thresholdProfiles={ThresholdProfileCount}, recipes={RecipeRevisionCount}."
        : $"Backup cannot be restored: {string.Join(" ", BlockingIssues)}";
}

public sealed record ConfigurationBackupResult(string BackupPath, ConfigurationBackupPackage Package);
public sealed record ConfigurationRollbackInfo(string RollbackBackupPath, string RestoredBackupPath, DateTime CreatedAtUtc);

public static class ConfigurationBackupService
{
    public const string CurrentSchemaVersion = "configuration-backup/v1";
    private const string RollbackMarkerFileName = "last_restore_rollback.json";

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
            preview.TargetStoragePath = AoiDatabase.StorageRoot;
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
            if (package.ActiveTaxonomy is null)
                preview.Warnings.Add("Backup does not include an active defect taxonomy; current taxonomy will remain unless restore data is available.");
            AddRestorePreviewDetails(package, preview);
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
        => Import(backupPath, operatorId, createRollback: true);

    public static ConfigurationRestorePreview RollbackLastRestore(string operatorId = "UNKNOWN")
    {
        var info = GetLastRollbackInfo()
            ?? throw new InvalidOperationException("No restore rollback package is available.");
        if (string.IsNullOrWhiteSpace(info.RollbackBackupPath) || !File.Exists(info.RollbackBackupPath))
            throw new FileNotFoundException("The last restore rollback package was not found.", info.RollbackBackupPath);

        var preview = Import(info.RollbackBackupPath, operatorId, createRollback: false);
        if (preview.IsCompatible)
        {
            AoiDatabase.RecordAuditEvent(
                "CONFIG_RESTORE_ROLLBACK",
                $"Configuration restore rolled back using {Path.GetFileName(info.RollbackBackupPath)}.",
                operatorWithRole: operatorId,
                relatedEntityType: "ConfigurationBackup",
                relatedPath: info.RollbackBackupPath);
        }

        return preview;
    }

    public static ConfigurationRollbackInfo? GetLastRollbackInfo()
    {
        var path = RollbackMarkerPath();
        if (!File.Exists(path))
            return null;
        try
        {
            return JsonSerializer.Deserialize<ConfigurationRollbackInfo>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ConfigurationRestorePreview Import(string backupPath, string operatorId, bool createRollback)
    {
        var preview = Preview(backupPath);
        if (!preview.IsCompatible)
            return preview;

        var package = ReadPackage(backupPath);
        AoiDatabase.Initialize();
        if (createRollback)
            CreateRollbackPackage(backupPath, operatorId);

        ApplySettings(package.Settings);
        if (package.ActiveTaxonomy is not null)
            AoiDatabase.SaveDefectTaxonomySnapshot(package.ActiveTaxonomy, operatorId);
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
            ActiveTaxonomy = DefectTaxonomyService.GetActiveTaxonomy(),
            PluginFolderReferences = GetPluginFolderReferences().Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            ExcludedData =
            {
                "image_vault/",
                "training/",
                "exports/ (generated exports are excluded unless explicitly selected by a future backup option)",
                "customer images and datasets",
                "raw production images",
                "SQLite WAL/SHM runtime files",
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

        settings["inspectionModel"] = JsonSerializer.SerializeToElement(InspectionModelConfigurationService.Load(), JsonOptions);
        settings["cameraSource"] = JsonSerializer.SerializeToElement(CameraSourceSettingsService.Load(), JsonOptions);
        settings["lighting"] = JsonSerializer.SerializeToElement(LightingSettingsService.Load(), JsonOptions);
        settings["mesIntegration"] = ProtectedMesSettingsElement(MesIntegrationSettingsService.Load());
        settings["centralSync"] = ProtectedCentralSyncSettingsElement(CentralSyncSettingsService.Load());
        settings["deploymentProfile"] = JsonSerializer.SerializeToElement(DeploymentProfileSettingsService.Load(), JsonOptions);
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
            MesIntegrationSettingsService.Save(UnprotectMesSettingsForRestore(mes.Deserialize<MesIntegrationSettings>(JsonOptions) ?? new MesIntegrationSettings()));
        if (settings.TryGetValue("centralSync", out var central))
            CentralSyncSettingsService.Save(UnprotectCentralSyncSettingsForRestore(central.Deserialize<CentralSyncSettings>(JsonOptions) ?? new CentralSyncSettings()));
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

    private static void AddRestorePreviewDetails(ConfigurationBackupPackage package, ConfigurationRestorePreview preview)
    {
        if (!string.IsNullOrWhiteSpace(package.SourceStorageRoot) &&
            !string.Equals(Path.GetFullPath(package.SourceStorageRoot), Path.GetFullPath(AoiDatabase.StorageRoot), StringComparison.OrdinalIgnoreCase))
        {
            preview.Warnings.Add($"Backup source storage root was {package.SourceStorageRoot}; target storage root is {AoiDatabase.StorageRoot}.");
        }

        var currentModels = AoiDatabase.GetModelRegistryRecords().Select(model => model.ModelId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var model in package.ModelRegistry)
        {
            if (currentModels.Contains(model.ModelId))
                preview.Conflicts.Add($"Model registry entry already exists and will be updated: {model.ModelId}.");
            if (!string.IsNullOrWhiteSpace(model.StoredModelPath) && !File.Exists(model.StoredModelPath))
                preview.MissingModelFiles.Add(model.StoredModelPath);
        }

        var currentProfiles = AoiDatabase.GetThresholdProfiles()
            .Select(profile => $"{profile.ProfileId}/{profile.Revision}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in package.ThresholdProfiles)
        {
            var key = $"{profile.ProfileId}/{profile.Revision}";
            if (currentProfiles.Contains(key))
                preview.Conflicts.Add($"Threshold profile already exists and will be updated: {key}.");
        }

        if (TryDeserializeSetting<InspectionModelConfiguration>(package.Settings, "inspectionModel", out var inspection) &&
            !string.IsNullOrWhiteSpace(inspection.ModelFilePath) &&
            !File.Exists(inspection.ModelFilePath))
        {
            preview.MissingModelFiles.Add(inspection.ModelFilePath);
        }

        if (TryDeserializeSetting<CameraSourceSettings>(package.Settings, "cameraSource", out var camera) &&
            !string.IsNullOrWhiteSpace(camera.AdapterFolder) &&
            !Directory.Exists(camera.AdapterFolder))
        {
            preview.MissingPluginFolders.Add(camera.AdapterFolder);
        }

        foreach (var pluginFolder in package.PluginFolderReferences.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            if (!Directory.Exists(pluginFolder))
                preview.MissingPluginFolders.Add(pluginFolder);
        }

        AddSettingChange(preview, "inspectionModel", package.Settings, InspectionModelConfigurationService.Load());
        AddSettingChange(preview, "cameraSource", package.Settings, CameraSourceSettingsService.Load());
        AddSettingChange(preview, "lighting", package.Settings, LightingSettingsService.Load());
        AddSettingChange(preview, "mesIntegration", package.Settings, ProtectedComparableMesSettings(MesIntegrationSettingsService.Load()));
        AddSettingChange(preview, "centralSync", package.Settings, ProtectedComparableCentralSyncSettings(CentralSyncSettingsService.Load()));
        AddSettingChange(preview, "deploymentProfile", package.Settings, DeploymentProfileSettingsService.Load());
        AddSettingChange(preview, "activeTaxonomy", new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
        {
            ["activeTaxonomy"] = JsonSerializer.SerializeToElement(package.ActiveTaxonomy, JsonOptions),
        }, DefectTaxonomyService.GetActiveTaxonomy());
        AddSecretProtectionStatus(package, preview);

        if (preview.MissingModelFiles.Count > 0)
            preview.Warnings.Add($"{preview.MissingModelFiles.Count} model file path(s) in the backup do not exist on this PC.");
        if (preview.MissingPluginFolders.Count > 0)
            preview.Warnings.Add($"{preview.MissingPluginFolders.Count} plugin folder path(s) in the backup do not exist on this PC.");
        preview.MissingModelFiles = preview.MissingModelFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        preview.MissingPluginFolders = preview.MissingPluginFolders.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void AddSettingChange<T>(ConfigurationRestorePreview preview, string key, Dictionary<string, JsonElement> settings, T current)
    {
        if (!settings.TryGetValue(key, out var backup))
            return;

        var currentJson = JsonSerializer.Serialize(current, JsonOptions);
        if (!JsonEquivalent(backup.GetRawText(), currentJson))
            preview.SettingsChanges.Add($"{key} will change.");
    }

    private static bool JsonEquivalent(string left, string right)
    {
        using var leftDocument = JsonDocument.Parse(left);
        using var rightDocument = JsonDocument.Parse(right);
        return JsonSerializer.Serialize(leftDocument.RootElement, JsonOptions) ==
               JsonSerializer.Serialize(rightDocument.RootElement, JsonOptions);
    }

    private static bool TryDeserializeSetting<T>(Dictionary<string, JsonElement> settings, string key, out T value)
    {
        value = default!;
        if (!settings.TryGetValue(key, out var element))
            return false;
        try
        {
            value = element.Deserialize<T>(JsonOptions)!;
            return value is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static JsonElement ProtectedMesSettingsElement(MesIntegrationSettings settings)
        => JsonSerializer.SerializeToElement(ProtectedComparableMesSettings(settings), JsonOptions);

    private static MesIntegrationSettings ProtectedComparableMesSettings(MesIntegrationSettings settings)
        => new()
        {
            Mode = settings.Mode,
            MockEndpointUrl = settings.MockEndpointUrl,
            UploadTimeoutSeconds = settings.UploadTimeoutSeconds,
            AutoUploadEnabled = settings.AutoUploadEnabled,
            BaseUrl = settings.BaseUrl,
            UploadResultPath = settings.UploadResultPath,
            UploadImagePath = settings.UploadImagePath,
            AuthMode = settings.AuthMode,
            ApiKeyHeaderName = settings.ApiKeyHeaderName,
            ApiKey = string.IsNullOrWhiteSpace(settings.ApiKey) ? string.Empty : SecretProtectionService.Protect(settings.ApiKey),
            BearerToken = string.IsNullOrWhiteSpace(settings.BearerToken) ? string.Empty : SecretProtectionService.Protect(settings.BearerToken),
            Username = settings.Username,
            Password = string.IsNullOrWhiteSpace(settings.Password) ? string.Empty : SecretProtectionService.Protect(settings.Password),
            TimeoutSeconds = settings.TimeoutSeconds,
            MaxRetryCount = settings.MaxRetryCount,
            RetryBackoffMs = settings.RetryBackoffMs,
        };

    private static JsonElement ProtectedCentralSyncSettingsElement(CentralSyncSettings settings)
        => JsonSerializer.SerializeToElement(ProtectedComparableCentralSyncSettings(settings), JsonOptions);

    private static CentralSyncSettings ProtectedComparableCentralSyncSettings(CentralSyncSettings settings)
        => new()
        {
            Mode = settings.Mode,
            EndpointOrFolder = settings.EndpointOrFolder,
            EndpointUrl = settings.EndpointUrl,
            FileDropFolder = settings.FileDropFolder,
            StationId = settings.StationId,
            SyncIntervalSeconds = settings.SyncIntervalSeconds,
            IncludeImages = settings.IncludeImages,
            RedactOperatorId = settings.RedactOperatorId,
            RedactImagePaths = settings.RedactImagePaths,
            RedactEndpointInExports = settings.RedactEndpointInExports,
            MaxRetryCount = settings.MaxRetryCount,
            RetryBackoffMs = settings.RetryBackoffMs,
            SharedSecret = string.IsNullOrWhiteSpace(settings.SharedSecret) ? string.Empty : SecretProtectionService.Protect(settings.SharedSecret),
        };

    private static MesIntegrationSettings UnprotectMesSettingsForRestore(MesIntegrationSettings settings)
    {
        settings.ApiKey = TryUnprotectForRestore(settings.ApiKey);
        settings.BearerToken = TryUnprotectForRestore(settings.BearerToken);
        settings.Password = TryUnprotectForRestore(settings.Password);
        return settings;
    }

    private static CentralSyncSettings UnprotectCentralSyncSettingsForRestore(CentralSyncSettings settings)
    {
        settings.SharedSecret = TryUnprotectForRestore(settings.SharedSecret);
        return settings;
    }

    private static string TryUnprotectForRestore(string value)
    {
        if (!SecretProtectionService.IsProtected(value))
            return value;
        try
        {
            return SecretProtectionService.Unprotect(value);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void CreateRollbackPackage(string restoreBackupPath, string operatorId)
    {
        var folder = Path.Combine(AoiDatabase.StorageRoot, "configuration_rollback");
        Directory.CreateDirectory(folder);
        var rollback = Export(folder, operatorId);
        var info = new ConfigurationRollbackInfo(rollback.BackupPath, restoreBackupPath, DateTime.UtcNow);
        File.WriteAllText(RollbackMarkerPath(), JsonSerializer.Serialize(info, JsonOptions));
        AoiDatabase.RecordAuditEvent(
            "CONFIG_RESTORE_ROLLBACK_PACKAGE",
            $"Rollback package created before restore: {Path.GetFileName(rollback.BackupPath)}.",
            operatorWithRole: operatorId,
            relatedEntityType: "ConfigurationBackup",
            relatedPath: rollback.BackupPath);
    }

    private static string RollbackMarkerPath()
        => Path.Combine(AoiDatabase.StorageRoot, RollbackMarkerFileName);

    private static IEnumerable<string> GetPluginFolderReferences()
    {
        var camera = CameraSourceSettingsService.Load();
        if (!string.IsNullOrWhiteSpace(camera.AdapterFolder))
            yield return camera.AdapterFolder;
    }

    private static void AddSecretProtectionStatus(ConfigurationBackupPackage package, ConfigurationRestorePreview preview)
    {
        AddSecretProtectionStatus(package.Settings, preview, "mesIntegration", nameof(MesIntegrationSettings.ApiKey));
        AddSecretProtectionStatus(package.Settings, preview, "mesIntegration", nameof(MesIntegrationSettings.BearerToken));
        AddSecretProtectionStatus(package.Settings, preview, "mesIntegration", nameof(MesIntegrationSettings.Password));
        AddSecretProtectionStatus(package.Settings, preview, "centralSync", nameof(CentralSyncSettings.SharedSecret));
    }

    private static void AddSecretProtectionStatus(Dictionary<string, JsonElement> settings, ConfigurationRestorePreview preview, string key, string propertyName)
    {
        if (!settings.TryGetValue(key, out var element) ||
            (!element.TryGetProperty(propertyName, out var valueElement) &&
             !element.TryGetProperty(JsonName(propertyName), out valueElement)))
        {
            return;
        }

        var value = valueElement.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            preview.SecretProtectionStatus.Add($"{key}.{propertyName}: empty/not configured.");
        }
        else if (SecretProtectionService.IsProtected(value))
        {
            preview.SecretProtectionStatus.Add($"{key}.{propertyName}: protected.");
        }
        else
        {
            preview.Warnings.Add($"{key}.{propertyName} is not marked protected in the backup; it will be treated as unsafe legacy data.");
            preview.SecretProtectionStatus.Add($"{key}.{propertyName}: NOT PROTECTED.");
        }
    }

    private static string JsonName(string propertyName)
        => string.IsNullOrWhiteSpace(propertyName)
            ? propertyName
            : char.ToLowerInvariant(propertyName[0]) + propertyName[1..];

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
