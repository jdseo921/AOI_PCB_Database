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
            OnnxEngineKey => new OnnxInspectionEngine(configuration),
            _ => new PixelDifferenceInspectionEngine(),
        };
    }

    public static string NormalizeEngineKey(string? engineKey)
        => string.IsNullOrWhiteSpace(engineKey)
            ? DefaultEngineKey
            : engineKey.Trim().ToLowerInvariant();
}
