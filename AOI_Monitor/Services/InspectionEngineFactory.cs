using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public static class InspectionEngineFactory
{
    public const string DefaultEngineKey = "pixel-difference";
    public const string OnnxEngineKey = "onnx";
    public const string LearnedVisualEngineKey = "learned-pcb-visual";

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
            LearnedVisualEngineKey => new LearnedPcbVisualInspectionEngine(configuration.ActiveModelId),
            _ => new PixelDifferenceInspectionEngine(),
        };
    }

    public static string NormalizeEngineKey(string? engineKey)
    {
        if (string.IsNullOrWhiteSpace(engineKey))
            return DefaultEngineKey;

        return engineKey.Trim().ToLowerInvariant() switch
        {
            "pixel" or "pixel-diff" or "pixel-difference-prototype" => DefaultEngineKey,
            "onnx-ml" or "onnx-model" or "onnx-ml-model" => OnnxEngineKey,
            "learned" or "learned-visual" or "learned-pcb-visual-model" or "image-learning" => LearnedVisualEngineKey,
            var normalized => normalized,
        };
    }

    private static InspectionModelConfiguration ResolveRegistryOnnxConfiguration(InspectionModelConfiguration fallback)
    {
        try
        {
            if (ModelRegistryService.GetActiveModel() is { } activeModel)
                return ModelRegistryService.ToInspectionConfiguration(activeModel);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"Active model registry configuration fallback used: {ex.Message}");
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
