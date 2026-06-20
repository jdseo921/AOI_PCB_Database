using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public static class InspectionEngineFactory
{
    public const string DefaultEngineKey = "pixel-difference";
    public const string OnnxEngineKey = "onnx";

    public static IInspectionEngine Create(string? engineKey = null)
    {
        var configuration = InspectionModelConfigurationService.Load();
        var selectedKey = string.IsNullOrWhiteSpace(engineKey)
            ? configuration.SelectedEngineKey
            : engineKey;

        return NormalizeEngineKey(selectedKey) switch
        {
            DefaultEngineKey => new PixelDifferenceInspectionEngine(),
            OnnxEngineKey => new OnnxInspectionEngine(ResolveRegistryOnnxConfiguration(configuration)),
            _ => new PixelDifferenceInspectionEngine(),
        };
    }

    public static string NormalizeEngineKey(string? engineKey)
        => string.IsNullOrWhiteSpace(engineKey)
            ? DefaultEngineKey
            : engineKey.Trim().ToLowerInvariant();

    private static InspectionModelConfiguration ResolveRegistryOnnxConfiguration(InspectionModelConfiguration fallback)
    {
        try
        {
            if (ModelRegistryService.GetActiveModel() is { } activeModel)
                return ModelRegistryService.ToInspectionConfiguration(activeModel);
        }
        catch
        {
        }

        return new InspectionModelConfiguration
        {
            SelectedEngineKey = OnnxEngineKey,
            ActiveModelId = fallback.ActiveModelId,
            ActiveModelSha256 = fallback.ActiveModelSha256,
            ActiveModelValidationStatus = fallback.ActiveModelValidationStatus,
            ModelVersion = string.IsNullOrWhiteSpace(fallback.ModelVersion) ? "UNCONFIGURED" : fallback.ModelVersion,
            ConfidenceThreshold = fallback.ConfidenceThreshold,
            InputImageWidth = fallback.InputImageWidth,
            InputImageHeight = fallback.InputImageHeight,
            InputTensorName = fallback.InputTensorName,
            OutputTensorName = fallback.OutputTensorName,
            LastModelCheckResult = ModelConfigurationTestStatus.MissingModel,
            LastModelCheckMessage = "No active model registry deployment is selected.",
        };
    }
}
