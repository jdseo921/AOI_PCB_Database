using System.IO;
using System.Reflection;
using System.Text.Json;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public static class LightingControllerFactory
{
    public static ILightingController Create(LightingSettings settings)
        => LightingSettingsService.NormalizeMode(settings.Mode) switch
        {
            LightingModes.Simulated => new SimulatedLightingController(settings),
            LightingModes.TcpText => new TcpTextLightingController(settings),
            LightingModes.SerialText => new SerialTextLightingController(settings),
            _ => new NullLightingController(),
        };

    public static void ApplyActiveController()
        => IntegrationBoundaryRegistry.LightingController = Create(LightingSettingsService.Load());
}

public interface ILightingControllerFactory
{
    string DriverId { get; }
    string DisplayName { get; }
    string Version { get; }
    IReadOnlyList<string> SupportedModes { get; }
    ILightingController Create(LightingSettings settings);
}

public sealed class LightingAdapterManifest
{
    public string DriverId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string AssemblyFile { get; set; } = string.Empty;
    public string AssemblyPath { get; set; } = string.Empty;
    public string FactoryTypeName { get; set; } = string.Empty;
    public string FactoryType { get; set; } = string.Empty;
    public List<string> SupportedModes { get; set; } = new();

    public string EffectiveAssemblyFile => string.IsNullOrWhiteSpace(AssemblyFile) ? AssemblyPath : AssemblyFile;
    public string EffectiveFactoryTypeName => string.IsNullOrWhiteSpace(FactoryTypeName) ? FactoryType : FactoryTypeName;
}

public sealed record LightingPluginLoadResult(
    bool Success,
    LightingAdapterManifest? Manifest,
    ILightingControllerFactory? Factory,
    string Message)
{
    public static LightingPluginLoadResult Fail(string message)
        => new(false, null, null, message);
}

public static class LightingAdapterPluginService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static LightingPluginLoadResult LoadFactory(string adapterFolder)
    {
        if (string.IsNullOrWhiteSpace(adapterFolder))
            return LightingPluginLoadResult.Fail("No external lighting adapter folder is configured.");
        if (!Directory.Exists(adapterFolder))
            return LightingPluginLoadResult.Fail($"Lighting adapter folder does not exist: {adapterFolder}");

        var manifestPath = Directory.EnumerateFiles(adapterFolder, "*.lighting-adapter.json", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(adapterFolder, "lighting_adapter_manifest.json", SearchOption.TopDirectoryOnly))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        return string.IsNullOrWhiteSpace(manifestPath)
            ? LightingPluginLoadResult.Fail("No lighting adapter manifest was found. Expected *.lighting-adapter.json or lighting_adapter_manifest.json.")
            : LoadFactoryFromManifest(manifestPath);
    }

    public static LightingPluginLoadResult LoadFactoryFromManifest(string manifestPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
                return LightingPluginLoadResult.Fail("Lighting adapter manifest file was not found.");

            var manifestFolder = Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? Directory.GetCurrentDirectory();
            var manifest = JsonSerializer.Deserialize<LightingAdapterManifest>(File.ReadAllText(manifestPath), JsonOptions)
                ?? new LightingAdapterManifest();
            var validation = ValidateManifest(manifest);
            if (validation.Count > 0)
                return LightingPluginLoadResult.Fail(string.Join(" ", validation));

            var assemblyFile = manifest.EffectiveAssemblyFile;
            var assemblyPath = Path.IsPathRooted(assemblyFile) ? assemblyFile : Path.Combine(manifestFolder, assemblyFile);
            if (!File.Exists(assemblyPath))
                return LightingPluginLoadResult.Fail($"Lighting adapter assembly was not found: {assemblyPath}");

            var assembly = Assembly.LoadFrom(assemblyPath);
            var factoryTypeName = manifest.EffectiveFactoryTypeName;
            var factoryType = assembly.GetType(factoryTypeName, throwOnError: false);
            if (factoryType is null)
                return LightingPluginLoadResult.Fail($"Factory type '{factoryTypeName}' was not found in lighting adapter assembly.");
            if (!typeof(ILightingControllerFactory).IsAssignableFrom(factoryType))
                return LightingPluginLoadResult.Fail($"Factory type '{factoryTypeName}' does not implement ILightingControllerFactory.");
            if (Activator.CreateInstance(factoryType) is not ILightingControllerFactory factory)
                return LightingPluginLoadResult.Fail($"Factory type '{factoryTypeName}' could not be created.");

            var identityErrors = ValidateFactoryIdentity(manifest, factory);
            if (identityErrors.Count > 0)
                return LightingPluginLoadResult.Fail(string.Join(" ", identityErrors));

            return new LightingPluginLoadResult(true, manifest, factory, $"Loaded lighting adapter {factory.DisplayName} {factory.Version}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or ReflectionTypeLoadException or TargetInvocationException or TypeLoadException or JsonException)
        {
            return LightingPluginLoadResult.Fail($"Lighting adapter plugin failed safely: {ex.Message}");
        }
    }

    private static List<string> ValidateManifest(LightingAdapterManifest manifest)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(manifest.DriverId))
            errors.Add("DriverId is required.");
        if (string.IsNullOrWhiteSpace(manifest.DisplayName))
            errors.Add("DisplayName is required.");
        if (string.IsNullOrWhiteSpace(manifest.Version))
            errors.Add("Version is required.");
        if (string.IsNullOrWhiteSpace(manifest.EffectiveAssemblyFile))
            errors.Add("AssemblyFile is required.");
        if (string.IsNullOrWhiteSpace(manifest.EffectiveFactoryTypeName))
            errors.Add("FactoryTypeName is required.");
        if (manifest.SupportedModes.Count == 0)
            errors.Add("SupportedModes must list at least one mode.");
        return errors;
    }

    private static List<string> ValidateFactoryIdentity(LightingAdapterManifest manifest, ILightingControllerFactory factory)
    {
        var errors = new List<string>();
        if (!string.Equals(manifest.DriverId, factory.DriverId, StringComparison.OrdinalIgnoreCase))
            errors.Add("Lighting factory identity does not match manifest DriverId.");
        if (!string.Equals(manifest.Version, factory.Version, StringComparison.OrdinalIgnoreCase))
            errors.Add("Lighting factory version does not match manifest Version.");
        if (!manifest.SupportedModes.All(mode => factory.SupportedModes.Contains(mode, StringComparer.OrdinalIgnoreCase)))
            errors.Add("Lighting factory supported modes do not satisfy manifest SupportedModes.");
        return errors;
    }
}

public static class LightingSynchronizationService
{
    public static async Task<IntegrationCommandResult> SynchronizeAsync(
        ILightingController controller,
        LightingSettings settings,
        string viewType,
        CancellationToken cancellationToken = default)
    {
        var program = LightingCommandFormatter.ProgramForView(settings, viewType);
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(settings.ResponseTimeoutMs));
            return await controller.SetProgramAsync(viewType, program, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new IntegrationCommandResult(false, IntegrationConnectionStatus.Error, $"Lighting sync timed out after {settings.ResponseTimeoutMs} ms.");
        }
        catch (Exception ex)
        {
            return new IntegrationCommandResult(false, IntegrationConnectionStatus.Error, $"Lighting sync failed safely: {ex.Message}");
        }
    }
}
