using System.Text.Json;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Xunit;

namespace AOI_Monitor.Tests;

public sealed class ConfigurationBackupServiceTests : IDisposable
{
    private readonly string _root;

    public ConfigurationBackupServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AOI_Monitor_ConfigBackup_Tests", Guid.NewGuid().ToString("N"));
        AoiDatabase.ConfigureStorageRoot(_root);
        RecipeService.Invalidate();
        FirstRunSettingsService.ResetForTests();
    }

    public void Dispose()
    {
        RecipeService.Invalidate();
        FirstRunSettingsService.ResetForTests();
        StorageRootSettingsService.ConfigureSettingsDirectoryForTests(null);
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void BackupExcludesRawCustomerImages()
    {
        AoiDatabase.Initialize();
        Directory.CreateDirectory(AoiDatabase.ImageVaultPath);
        File.WriteAllText(Path.Combine(AoiDatabase.ImageVaultPath, "customer_board_001.png"), "raw image bytes");
        var output = Path.Combine(_root, "backup");

        var result = ConfigurationBackupService.Export(output, "Admin01 [Admin]");
        var json = File.ReadAllText(result.BackupPath);

        Assert.DoesNotContain("customer_board_001.png", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(AoiDatabase.ImageVaultPath, json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.Package.ExcludedData, item => item.Contains("image_vault", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Package.ExcludedData, item => item.Contains("customer images", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RestorePreviewDetectsIncompatibleSchemaVersion()
    {
        var backupPath = Path.Combine(_root, "future_backup.json");
        Directory.CreateDirectory(_root);
        var package = new ConfigurationBackupPackage
        {
            SchemaVersion = "configuration-backup/v999",
            DatabaseSchemaVersion = AoiDatabase.LatestSchemaVersion + 1,
        };
        File.WriteAllText(backupPath, JsonSerializer.Serialize(package));

        var preview = ConfigurationBackupService.Preview(backupPath);

        Assert.False(preview.IsCompatible);
        Assert.Contains(preview.BlockingIssues, issue => issue.Contains("Unsupported backup schema", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(preview.BlockingIssues, issue => issue.Contains("newer than this app supports", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BackupRestoreRoundTripPreservesActiveDeploymentProfileModelThresholdCameraAndMesSettings()
    {
        AoiDatabase.Initialize();
        var modelPath = Path.Combine(_root, "candidate.onnx");
        File.WriteAllBytes(modelPath, [1, 2, 3, 4]);
        var model = ModelRegistryService.Register(new ModelRegistrationRequest
        {
            ModelFilePath = modelPath,
            DisplayName = "Customer Candidate",
            Version = "1.0.0",
            InputTensorName = "input",
            OutputTensorName = "output",
            InputWidth = 640,
            InputHeight = 640,
            ConfidenceThreshold = 0.72,
        });
        Assert.True(ModelRegistryService.SetActiveModel(model.ModelId));

        var profile = new ThresholdProfile
        {
            ProfileId = "TP-BACKUP-ROUNDTRIP",
            Revision = "R0001",
            BoardModel = "CUSTOMER-A",
            BoardProgram = "PROGRAM-A",
            RecipeName = "RECIPE-A",
            RecipeRevision = "R2",
            Status = "Approved",
            CreatedBy = "Engineer01 [Engineer]",
            CreatedAtUtc = DateTime.UtcNow,
            Rules =
            [
                new ThresholdProfileRule
                {
                    ViewType = "Top",
                    RoiType = "Solder",
                    DefectClass = "BRIDGE",
                    ReviewThreshold = 7,
                    NgThreshold = 13,
                    ConfidenceThreshold = 0.74,
                    MinimumAreaPixels = 5,
                    MaxAllowedFalseCallRate = 0.02,
                },
            ],
        };
        AoiDatabase.SaveThresholdProfile(profile);
        ThresholdProfileService.DeployProfile(profile.ProfileId, profile.Revision, UserRole.Engineer, "Engineer01 [Engineer]");
        var pluginFolder = Path.Combine(_root, "plugins", "camera");
        Directory.CreateDirectory(pluginFolder);
        CameraSourceSettingsService.Save(new CameraSourceSettings
        {
            SourceKey = CameraSourceFactory.GenericVisionAdapterSourceKey,
            AdapterFolder = pluginFolder,
            TopDeviceId = "CAM-TOP-001",
            BoardModel = "CUSTOMER-A",
            LotId = "LOT-RESTORE",
        });
        const string apiKey = "restore-api-key-secret";
        MesIntegrationSettingsService.Save(new MesIntegrationSettings
        {
            Mode = MesIntegrationMode.Rest,
            BaseUrl = "https://mes.restore.test",
            UploadResultPath = "/api/results",
            UploadImagePath = "/api/images",
            AuthMode = MesRestAuthMode.ApiKey,
            ApiKeyHeaderName = "X-Restore-Key",
            ApiKey = apiKey,
            TimeoutSeconds = 12,
            MaxRetryCount = 4,
        });
        DeploymentProfileSettingsService.Save(DeploymentProfile.Stage4MesPilot);
        var backupPath = ConfigurationBackupService.Export(Path.Combine(_root, "backup"), "Admin01 [Admin]").BackupPath;
        var backupJson = File.ReadAllText(backupPath);

        Assert.DoesNotContain(apiKey, backupJson, StringComparison.Ordinal);
        Assert.Contains(SecretProtectionService.ProtectedPrefix, backupJson, StringComparison.Ordinal);

        var restoreRoot = Path.Combine(_root, "restore-target");
        AoiDatabase.ConfigureStorageRoot(restoreRoot);
        AoiDatabase.Initialize();

        var preview = ConfigurationBackupService.Import(backupPath, "Admin01 [Admin]");
        var restoredModel = ModelRegistryService.GetActiveModel();
        var restoredProfile = AoiDatabase.GetActiveThresholdProfile("CUSTOMER-A", "PROGRAM-A", "RECIPE-A");
        var restoredCamera = CameraSourceSettingsService.Load();
        var restoredMes = MesIntegrationSettingsService.Load();

        Assert.True(preview.IsCompatible);
        Assert.Equal(DeploymentProfile.Stage4MesPilot, DeploymentProfileSettingsService.Load());
        Assert.NotNull(restoredModel);
        Assert.Equal(model.ModelId, restoredModel.ModelId);
        Assert.NotNull(restoredProfile);
        Assert.Equal(profile.ProfileId, restoredProfile.ProfileId);
        Assert.Equal(0.74, restoredProfile.Rules.Single().ConfidenceThreshold, precision: 3);
        Assert.Equal(13, restoredProfile.Rules.Single().NgThreshold, precision: 3);
        Assert.Equal(CameraSourceFactory.GenericVisionAdapterSourceKey, restoredCamera.SourceKey);
        Assert.Equal(pluginFolder, restoredCamera.AdapterFolder);
        Assert.Equal("CAM-TOP-001", restoredCamera.TopDeviceId);
        Assert.Equal(MesIntegrationMode.Rest, restoredMes.Mode);
        Assert.Equal("https://mes.restore.test", restoredMes.BaseUrl);
        Assert.Equal("X-Restore-Key", restoredMes.ApiKeyHeaderName);
        Assert.Equal(apiKey, restoredMes.ApiKey);
        Assert.Equal(4, restoredMes.MaxRetryCount);
    }

    [Fact]
    public void RestorePreviewReportsTargetPathMissingFilesPluginFoldersAndSettingsChanges()
    {
        AoiDatabase.Initialize();
        var missingModel = Path.Combine(_root, "missing.onnx");
        var missingPlugin = Path.Combine(_root, "missing-plugin");
        InspectionModelConfigurationService.Save(new InspectionModelConfiguration { ModelFilePath = missingModel });
        CameraSourceSettingsService.Save(new CameraSourceSettings
        {
            SourceKey = CameraSourceFactory.GenericVisionAdapterSourceKey,
            AdapterFolder = missingPlugin,
        });
        DeploymentProfileSettingsService.Save(DeploymentProfile.FullFactoryAutomation);
        var backupPath = ConfigurationBackupService.Export(Path.Combine(_root, "preview-backup"), "Admin01 [Admin]").BackupPath;

        AoiDatabase.ConfigureStorageRoot(Path.Combine(_root, "preview-target"));
        AoiDatabase.Initialize();
        DeploymentProfileSettingsService.Save(DeploymentProfile.Stage1ImageValidation);

        var preview = ConfigurationBackupService.Preview(backupPath);

        Assert.True(preview.IsCompatible);
        Assert.Equal(AoiDatabase.StorageRoot, preview.TargetStoragePath);
        Assert.Contains(preview.MissingModelFiles, path => path == missingModel);
        Assert.Contains(preview.MissingPluginFolders, path => path == missingPlugin);
        Assert.Contains(preview.SettingsChanges, change => change.Contains("deploymentProfile", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(preview.Warnings, warning => warning.Contains("model file", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(preview.Warnings, warning => warning.Contains("plugin folder", StringComparison.OrdinalIgnoreCase));
    }
}
