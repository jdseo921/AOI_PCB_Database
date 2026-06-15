namespace AOI_Monitor.Services;

public static class InspectionEngineFactory
{
    public const string DefaultEngineKey = "pixel-difference";

    public static IInspectionEngine Create(string? engineKey = null)
    {
        return Normalize(engineKey) switch
        {
            DefaultEngineKey => new PixelDifferenceInspectionEngine(),
            _ => new PixelDifferenceInspectionEngine(),
        };
    }

    private static string Normalize(string? engineKey)
        => string.IsNullOrWhiteSpace(engineKey)
            ? DefaultEngineKey
            : engineKey.Trim().ToLowerInvariant();
}
