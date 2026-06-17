using System.IO;
using System.Text.Json;
using AOI_Monitor.Data;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public static class InspectionModelConfigurationService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static InspectionModelConfiguration? _cached;

    public static event Action? ConfigurationChanged;

    public static string ConfigurationPath => Path.Combine(AoiDatabase.StorageRoot, "inspection_model_config.json");

    public static InspectionModelConfiguration Load()
    {
        if (_cached is not null)
            return Clone(_cached);

        Directory.CreateDirectory(AoiDatabase.StorageRoot);

        if (!File.Exists(ConfigurationPath))
        {
            _cached = new InspectionModelConfiguration();
            Save(_cached, notify: false);
            return Clone(_cached);
        }

        try
        {
            var json = File.ReadAllText(ConfigurationPath);
            _cached = JsonSerializer.Deserialize<InspectionModelConfiguration>(json) ?? new InspectionModelConfiguration();
        }
        catch
        {
            _cached = new InspectionModelConfiguration();
        }

        Normalize(_cached);
        return Clone(_cached);
    }

    public static void Save(InspectionModelConfiguration configuration)
        => Save(configuration, notify: true);

    public static InspectionEngineStatus GetStatus()
        => GetStatus(Load());

    public static InspectionEngineStatus GetStatus(InspectionModelConfiguration configuration)
    {
        if (!configuration.IsOnnxSelected)
            return InspectionEngineStatus.PrototypeEngine;

        if (!configuration.HasModelFile)
            return InspectionEngineStatus.MlModelMissing;

        var currentHash = ModelConfigurationValidator.ComputeConfigurationHash(configuration);
        if (!string.Equals(configuration.LastModelCheckConfigurationHash, currentHash, StringComparison.OrdinalIgnoreCase))
            return InspectionEngineStatus.MlModelNotTested;

        return configuration.LastModelCheckResult switch
        {
            ModelConfigurationTestStatus.Ready => InspectionEngineStatus.MlModelReady,
            ModelConfigurationTestStatus.MissingModel => InspectionEngineStatus.MlModelMissing,
            ModelConfigurationTestStatus.InvalidLabelMap => InspectionEngineStatus.MlInvalidLabelMap,
            ModelConfigurationTestStatus.UnsupportedOutputFormat => InspectionEngineStatus.MlUnsupportedOutputFormat,
            ModelConfigurationTestStatus.RuntimeError => InspectionEngineStatus.MlRuntimeError,
            _ => InspectionEngineStatus.MlModelNotTested,
        };
    }

    public static string GetStatusText() => GetStatusText(GetStatus());

    public static string GetStatusText(InspectionEngineStatus status) => status switch
    {
        InspectionEngineStatus.MlModelReady => "Ready",
        InspectionEngineStatus.MlModelNotTested => "Model Not Tested",
        InspectionEngineStatus.MlModelMissing => "ML Model Missing",
        InspectionEngineStatus.MlInvalidLabelMap => "Invalid Label Map",
        InspectionEngineStatus.MlRuntimeError => "ML Runtime Error",
        InspectionEngineStatus.MlUnsupportedOutputFormat => "Unsupported Output Format",
        _ => "Prototype Engine",
    };

    public static ModelConfigurationTestResult TestAndSave(InspectionModelConfiguration configuration)
    {
        var result = ModelConfigurationValidator.Test(configuration);
        configuration.LastModelCheckTimestampUtc = result.TimestampUtc;
        configuration.LastModelCheckResult = result.Status;
        configuration.LastModelCheckMessage = result.Message;
        configuration.LastModelCheckConfigurationHash = result.ConfigurationHash;
        Save(configuration);
        return result;
    }

    private static void Save(InspectionModelConfiguration configuration, bool notify)
    {
        Normalize(configuration);
        Directory.CreateDirectory(AoiDatabase.StorageRoot);
        File.WriteAllText(ConfigurationPath, JsonSerializer.Serialize(configuration, JsonOptions));
        _cached = Clone(configuration);

        if (notify)
            ConfigurationChanged?.Invoke();
    }

    private static void Normalize(InspectionModelConfiguration configuration)
    {
        configuration.SelectedEngineKey = InspectionEngineFactory.NormalizeEngineKey(configuration.SelectedEngineKey);
        configuration.InputImageWidth = Math.Clamp(configuration.InputImageWidth, 32, 8192);
        configuration.InputImageHeight = Math.Clamp(configuration.InputImageHeight, 32, 8192);
        configuration.ConfidenceThreshold = Math.Clamp(configuration.ConfidenceThreshold, 0.0, 1.0);
        configuration.ModelFilePath = configuration.ModelFilePath?.Trim() ?? string.Empty;
        configuration.ModelVersion = string.IsNullOrWhiteSpace(configuration.ModelVersion)
            ? "UNCONFIGURED"
            : configuration.ModelVersion.Trim();
        configuration.LabelMapPath = configuration.LabelMapPath?.Trim() ?? string.Empty;
        configuration.InputTensorName = configuration.InputTensorName?.Trim() ?? string.Empty;
        configuration.OutputTensorName = configuration.OutputTensorName?.Trim() ?? string.Empty;
        configuration.LastModelCheckMessage = string.IsNullOrWhiteSpace(configuration.LastModelCheckMessage)
            ? "Not tested."
            : configuration.LastModelCheckMessage.Trim();
        configuration.LastModelCheckConfigurationHash = configuration.LastModelCheckConfigurationHash?.Trim() ?? string.Empty;
        if (configuration.BuiltInLabelMap is null ||
            configuration.BuiltInLabelMap.Count < 7 ||
            !configuration.BuiltInLabelMap.Values.Contains("Insufficient Solder", StringComparer.OrdinalIgnoreCase))
        {
            configuration.BuiltInLabelMap = new InspectionModelConfiguration().BuiltInLabelMap;
        }
    }

    private static InspectionModelConfiguration Clone(InspectionModelConfiguration source)
        => new()
        {
            SelectedEngineKey = source.SelectedEngineKey,
            ModelFilePath = source.ModelFilePath,
            ModelVersion = source.ModelVersion,
            InputImageWidth = source.InputImageWidth,
            InputImageHeight = source.InputImageHeight,
            InputTensorName = source.InputTensorName,
            OutputTensorName = source.OutputTensorName,
            ConfidenceThreshold = source.ConfidenceThreshold,
            LabelMapPath = source.LabelMapPath,
            LastModelCheckTimestampUtc = source.LastModelCheckTimestampUtc,
            LastModelCheckResult = source.LastModelCheckResult,
            LastModelCheckMessage = source.LastModelCheckMessage,
            LastModelCheckConfigurationHash = source.LastModelCheckConfigurationHash,
            BuiltInLabelMap = new Dictionary<int, string>(source.BuiltInLabelMap),
        };
}
