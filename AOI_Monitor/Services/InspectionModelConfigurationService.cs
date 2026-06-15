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
    {
        var configuration = Load();
        if (!configuration.IsOnnxSelected)
            return InspectionEngineStatus.PrototypeEngine;

        if (!configuration.HasModelFile)
            return InspectionEngineStatus.MlModelMissing;

        return OnnxInspectionEngine.RuntimeAvailable
            ? InspectionEngineStatus.MlModelConfigured
            : InspectionEngineStatus.MlRuntimeError;
    }

    public static string GetStatusText() => GetStatus() switch
    {
        InspectionEngineStatus.MlModelConfigured => "ML Model Configured",
        InspectionEngineStatus.MlModelMissing => "ML Model Missing",
        InspectionEngineStatus.MlRuntimeError => "ML Runtime Error",
        _ => "Prototype Engine",
    };

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
    }

    private static InspectionModelConfiguration Clone(InspectionModelConfiguration source)
        => new()
        {
            SelectedEngineKey = source.SelectedEngineKey,
            ModelFilePath = source.ModelFilePath,
            ModelVersion = source.ModelVersion,
            InputImageWidth = source.InputImageWidth,
            InputImageHeight = source.InputImageHeight,
            ConfidenceThreshold = source.ConfidenceThreshold,
            LabelMapPath = source.LabelMapPath,
            BuiltInLabelMap = new Dictionary<int, string>(source.BuiltInLabelMap),
        };
}
