using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AOI_Monitor.Data;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public static class ModelRegistryService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static event Action? RegistryChanged;

    public static string RegistryRoot => Path.Combine(AoiDatabase.StorageRoot, "model_registry");
    public static string ModelsRoot => Path.Combine(RegistryRoot, "models");
    public static void NotifyRegistryChanged() => RegistryChanged?.Invoke();

    public static ModelRegistryEntry Register(ModelRegistrationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ModelFilePath))
            throw new ArgumentException("Model file path is required.", nameof(request));
        if (!File.Exists(request.ModelFilePath))
            throw new FileNotFoundException("ONNX model file was not found.", request.ModelFilePath);

        Directory.CreateDirectory(ModelsRoot);
        var sha256 = ComputeSha256(request.ModelFilePath);
        var registeredAt = DateTime.UtcNow;
        var displayName = string.IsNullOrWhiteSpace(request.DisplayName)
            ? Path.GetFileNameWithoutExtension(request.ModelFilePath)
            : request.DisplayName.Trim();
        var version = string.IsNullOrWhiteSpace(request.Version)
            ? Path.GetFileNameWithoutExtension(request.ModelFilePath)
            : request.Version.Trim();
        var modelId = BuildModelId(displayName, version, sha256, registeredAt);
        var modelFolder = Path.Combine(ModelsRoot, modelId);
        Directory.CreateDirectory(modelFolder);

        var storedModelPath = Path.Combine(modelFolder, Path.GetFileName(request.ModelFilePath));
        File.Copy(request.ModelFilePath, storedModelPath, overwrite: true);

        var storedLabelMapPath = string.Empty;
        var labels = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.LabelMapPath))
        {
            if (!File.Exists(request.LabelMapPath))
                throw new FileNotFoundException("Label map file was not found.", request.LabelMapPath);

            storedLabelMapPath = Path.Combine(modelFolder, Path.GetFileName(request.LabelMapPath));
            File.Copy(request.LabelMapPath, storedLabelMapPath, overwrite: true);
            labels = ModelConfigurationValidator.LoadLabels(storedLabelMapPath)
                .OrderBy(pair => pair.Key)
                .Select(pair => pair.Value)
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (labels.Count == 0)
            labels = new InspectionModelConfiguration().BuiltInLabelMap
                .OrderBy(pair => pair.Key)
                .Select(pair => pair.Value)
                .ToList();

        var metadataPath = Path.Combine(modelFolder, "metadata.json");
        var entry = new ModelRegistryEntry
        {
            ModelId = modelId,
            DisplayName = displayName,
            Version = version,
            CreatedAtUtc = request.CreatedAtUtc?.ToUniversalTime() ?? registeredAt,
            RegisteredAtUtc = registeredAt,
            SourceFileName = Path.GetFileName(request.ModelFilePath),
            StoredModelPath = storedModelPath,
            StoredLabelMapPath = storedLabelMapPath,
            MetadataPath = metadataPath,
            Sha256 = sha256,
            InputTensorName = request.InputTensorName.Trim(),
            OutputTensorName = request.OutputTensorName.Trim(),
            InputWidth = Math.Clamp(request.InputWidth, 32, 8192),
            InputHeight = Math.Clamp(request.InputHeight, 32, 8192),
            ConfidenceThreshold = Math.Clamp(request.ConfidenceThreshold, 0.0, 1.0),
            Labels = labels,
            ValidationStatus = ModelConfigurationTestStatus.NotTested,
            ValidationMessage = "Not validated.",
            Notes = request.Notes.Trim(),
            LifecycleState = ModelLifecycleState.Registered,
        };

        WriteMetadata(entry);
        AoiDatabase.UpsertModelRegistryRecord(ToRecord(entry));
        AoiDatabase.RecordAuditEvent(
            "MODEL_REGISTRY",
            $"Registered local ONNX model {entry.ModelId}; version={entry.Version}; sha256={entry.Sha256}.",
            relatedEntityType: "ModelRegistry",
            relatedEntityId: entry.ModelId,
            relatedPath: entry.MetadataPath);
        NotifyRegistryChanged();
        return entry;
    }

    public static IReadOnlyList<ModelRegistryEntry> GetModels()
        => AoiDatabase.GetModelRegistryRecords()
            .Select(FromRecord)
            .ToArray();

    public static ModelRegistryEntry? GetModel(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return null;

        return GetModels().FirstOrDefault(model => string.Equals(model.ModelId, modelId, StringComparison.OrdinalIgnoreCase));
    }

    public static ModelRegistryEntry? GetActiveModel()
        => AoiDatabase.GetActiveModelRegistryRecord() is { } record
            ? FromRecord(record)
            : null;

    public static bool SetActiveModel(string modelId)
    {
        var model = GetModel(modelId);
        if (model is null)
            return false;
        if (model.LifecycleState == ModelLifecycleState.Retired)
            throw new InvalidOperationException("Retired models cannot be set active.");
        if (model.LifecycleState == ModelLifecycleState.AcceptanceFailed)
            throw new InvalidOperationException("Models with failed acceptance cannot be set active. Re-run acceptance or register a corrected model.");

        AoiDatabase.SetActiveModelRegistryRecord(model.ModelId);
        model.IsActive = true;
        WriteMetadata(model);
        SaveConfigurationFromModel(model);
        AoiDatabase.RecordAuditEvent(
            "MODEL_DEPLOYMENT",
            $"Set active ONNX model to {model.ModelId}; version={model.Version}; validation={model.ValidationStatus}.",
            relatedEntityType: "ModelRegistry",
            relatedEntityId: model.ModelId,
            relatedPath: model.MetadataPath);
        NotifyRegistryChanged();
        InspectionModelConfigurationService.NotifyExternalConfigurationChanged();
        return true;
    }

    public static ModelConfigurationTestResult Validate(string modelId)
    {
        var model = GetModel(modelId) ?? throw new InvalidOperationException($"Model registry entry was not found: {modelId}");
        var configuration = ToInspectionConfiguration(model);
        var result = ModelConfigurationValidator.Test(configuration);
        model.ValidationStatus = result.Status;
        model.LastValidatedAtUtc = result.TimestampUtc;
        model.ValidationMessage = result.Message;
        WriteMetadata(model);
        AoiDatabase.UpdateModelRegistryValidation(model.ModelId, result.Status, result.TimestampUtc, result.Message);
        AoiDatabase.RecordAuditEvent(
            "MODEL_CHECK",
            $"Model registry validation for {model.ModelId}: {result.DisplayStatus}. {result.Message}",
            relatedEntityType: "ModelRegistry",
            relatedEntityId: model.ModelId,
            relatedPath: model.MetadataPath);

        if (model.IsActive)
            SaveConfigurationFromModel(model);

        NotifyRegistryChanged();
        InspectionModelConfigurationService.NotifyExternalConfigurationChanged();
        return result;
    }

    public static InspectionModelConfiguration ToInspectionConfiguration(ModelRegistryEntry model)
    {
        var builtIn = new InspectionModelConfiguration().BuiltInLabelMap;
        if (model.Labels.Count > 0)
        {
            builtIn = model.Labels
                .Select((label, index) => new KeyValuePair<int, string>(index, label))
                .ToDictionary(pair => pair.Key, pair => pair.Value);
        }

        return new InspectionModelConfiguration
        {
            SelectedEngineKey = InspectionEngineFactory.OnnxEngineKey,
            ActiveModelId = model.ModelId,
            ActiveModelSha256 = model.Sha256,
            ActiveModelValidationStatus = model.ValidationStatus.ToString(),
            ModelFilePath = model.StoredModelPath,
            ModelVersion = model.Version,
            InputImageWidth = model.InputWidth,
            InputImageHeight = model.InputHeight,
            InputTensorName = model.InputTensorName,
            OutputTensorName = model.OutputTensorName,
            ConfidenceThreshold = model.ConfidenceThreshold,
            LabelMapPath = model.StoredLabelMapPath,
            LastModelCheckTimestampUtc = model.LastValidatedAtUtc,
            LastModelCheckResult = model.ValidationStatus,
            LastModelCheckMessage = model.ValidationMessage,
            LastModelCheckConfigurationHash = ModelConfigurationValidator.ComputeConfigurationHash(new InspectionModelConfiguration
            {
                SelectedEngineKey = InspectionEngineFactory.OnnxEngineKey,
                ModelFilePath = model.StoredModelPath,
                ModelVersion = model.Version,
                InputImageWidth = model.InputWidth,
                InputImageHeight = model.InputHeight,
                InputTensorName = model.InputTensorName,
                OutputTensorName = model.OutputTensorName,
                ConfidenceThreshold = model.ConfidenceThreshold,
                LabelMapPath = model.StoredLabelMapPath,
            }),
            BuiltInLabelMap = builtIn,
        };
    }

    public static string ComputeSha256(string path) => HashUtil.ComputeSha256(path);

    internal static ModelRegistryRecord ToRecord(ModelRegistryEntry entry)
        => new(
            0,
            entry.ModelId,
            entry.DisplayName,
            entry.Version,
            entry.CreatedAtUtc,
            entry.RegisteredAtUtc,
            entry.SourceFileName,
            entry.StoredModelPath,
            entry.StoredLabelMapPath,
            entry.MetadataPath,
            entry.Sha256,
            entry.InputTensorName,
            entry.OutputTensorName,
            entry.InputWidth,
            entry.InputHeight,
            entry.ConfidenceThreshold,
            entry.Labels,
            entry.ValidationStatus,
            entry.LastValidatedAtUtc,
            entry.ValidationMessage,
            entry.Notes,
            entry.IsActive,
            null,
            entry.LifecycleState,
            entry.LatestAcceptanceStatus,
            entry.LatestAcceptanceRunId,
            entry.LatestReleasePackageId,
            entry.LatestReleasePackagePath,
            entry.DeploymentWaiverReason,
            entry.WaiverExpiresAtUtc,
            entry.DeploymentWaivedBy,
            entry.DeploymentWaivedAtUtc,
            entry.DeploymentWaiverRiskClassification,
            entry.DeployedAtUtc,
            entry.RetiredReason,
            entry.RetiredAtUtc);

    private static ModelRegistryEntry FromRecord(ModelRegistryRecord record)
        => new()
        {
            ModelId = record.ModelId,
            DisplayName = record.DisplayName,
            Version = record.Version,
            CreatedAtUtc = record.CreatedAtUtc,
            RegisteredAtUtc = record.RegisteredAtUtc,
            SourceFileName = record.SourceFileName,
            StoredModelPath = record.StoredModelPath,
            StoredLabelMapPath = record.StoredLabelMapPath,
            MetadataPath = record.MetadataPath,
            Sha256 = record.Sha256,
            InputTensorName = record.InputTensorName,
            OutputTensorName = record.OutputTensorName,
            InputWidth = record.InputWidth,
            InputHeight = record.InputHeight,
            ConfidenceThreshold = record.ConfidenceThreshold,
            Labels = record.Labels.ToList(),
            ValidationStatus = record.ValidationStatus,
            LastValidatedAtUtc = record.LastValidatedAtUtc,
            ValidationMessage = record.ValidationMessage,
            Notes = record.Notes,
            IsActive = record.IsActive,
            LifecycleState = record.LifecycleState,
            LatestAcceptanceStatus = record.LatestAcceptanceStatus,
            LatestAcceptanceRunId = record.LatestAcceptanceRunId,
            LatestReleasePackageId = record.LatestReleasePackageId,
            LatestReleasePackagePath = record.LatestReleasePackagePath,
            DeploymentWaiverReason = record.DeploymentWaiverReason,
            WaiverExpiresAtUtc = record.WaiverExpiresAtUtc,
            DeploymentWaivedBy = record.DeploymentWaivedBy,
            DeploymentWaivedAtUtc = record.DeploymentWaivedAtUtc,
            DeploymentWaiverRiskClassification = record.DeploymentWaiverRiskClassification,
            DeployedAtUtc = record.DeployedAtUtc,
            RetiredReason = record.RetiredReason,
            RetiredAtUtc = record.RetiredAtUtc,
        };

    private static void SaveConfigurationFromModel(ModelRegistryEntry model)
        => InspectionModelConfigurationService.Save(ToInspectionConfiguration(model));

    private static void WriteMetadata(ModelRegistryEntry entry)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(entry.MetadataPath) ?? ModelsRoot);
        File.WriteAllText(entry.MetadataPath, JsonSerializer.Serialize(entry, JsonOptions));
    }

    private static string BuildModelId(string displayName, string version, string sha256, DateTime registeredAtUtc)
    {
        var safeName = SanitizeIdentifier(string.IsNullOrWhiteSpace(displayName) ? "model" : displayName);
        var safeVersion = SanitizeIdentifier(string.IsNullOrWhiteSpace(version) ? "unconfigured" : version);
        return $"{safeName}-{safeVersion}-{registeredAtUtc:yyyyMMddHHmmss}-{sha256[..8]}".ToLowerInvariant();
    }

    private static string SanitizeIdentifier(string value)
    {
        var chars = value
            .Trim()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray();
        var collapsed = string.Join("-", new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(collapsed) ? "model" : collapsed;
    }
}
