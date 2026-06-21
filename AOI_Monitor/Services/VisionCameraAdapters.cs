using AOI_Monitor.Models;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace AOI_Monitor.Services;

public sealed record VisionCameraDiagnostics(
    CameraSourceStatus Status,
    string Message,
    IReadOnlyList<string> Details);

/// <summary>
/// Stage 2 vendor SDK boundary for GigE/USB3 Vision cameras.
/// Vendor-specific adapters can implement this interface later without changing
/// the WPF UI, inspection workflow, or saved camera configuration model.
/// </summary>
public interface IVisionCameraAdapter
{
    bool Connect(CameraViewType viewType, string deviceId, CameraAcquisitionMode acquisitionMode, double exposureMs, double gain, int timeoutMs);
    void Disconnect();
    bool Start();
    void Stop();
    bool Trigger(int timeoutMs);
    bool TryGetFrame(CameraViewType viewType, int timeoutMs, out CameraFrame? frame);
    VisionCameraDiagnostics GetDiagnostics();
}

public interface IVisionCameraAdapterFactory
{
    string AdapterId { get; }
    string DisplayName { get; }
    string Version { get; }
    IReadOnlyList<string> SupportedInterfaces { get; }
    IReadOnlyList<string> SupportedViews { get; }
    IReadOnlyList<string> SupportedPixelFormats { get; }
    IVisionCameraAdapter CreateAdapter(CameraSourceSettings settings);
    IVisionDeviceDiscovery? CreateDeviceDiscovery(CameraSourceSettings settings);
}

public interface IVisionDeviceDiscovery
{
    IReadOnlyList<VisionDeviceInfo> DiscoverDevices();
}

public sealed record VisionDeviceInfo(
    string DeviceId,
    string Vendor,
    string Model,
    string Serial,
    string InterfaceType,
    string IpAddress,
    string SuggestedView,
    string Status,
    IReadOnlyList<string>? Capabilities = null)
{
    public string ViewAssignment => SuggestedView;
}

public sealed class VisionCameraAdapterManifest
{
    public string AdapterId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string AssemblyFile { get; set; } = string.Empty;
    public string AssemblyPath { get; set; } = string.Empty;
    public string FactoryTypeName { get; set; } = string.Empty;
    public string FactoryType { get; set; } = string.Empty;
    public List<string> SupportedInterfaces { get; set; } = new();
    public List<string> SupportedViews { get; set; } = new();
    public List<string> SupportedPixelFormats { get; set; } = new();

    public string EffectiveAssemblyFile => string.IsNullOrWhiteSpace(AssemblyFile) ? AssemblyPath : AssemblyFile;
    public string EffectiveFactoryTypeName => string.IsNullOrWhiteSpace(FactoryTypeName) ? FactoryType : FactoryTypeName;
    public IReadOnlyList<string> EffectiveSupportedInterfaces => SupportedInterfaces;
}

public sealed record VisionCameraPluginLoadResult(
    bool Success,
    VisionCameraAdapterManifest? Manifest,
    IVisionCameraAdapterFactory? Factory,
    string Message)
{
    public static VisionCameraPluginLoadResult Fail(string message)
        => new(false, null, null, message);
}

public static class VisionCameraPluginLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static VisionCameraPluginLoadResult LoadFactory(string adapterFolder)
    {
        if (string.IsNullOrWhiteSpace(adapterFolder))
            return VisionCameraPluginLoadResult.Fail("No external camera adapter folder is configured.");
        if (!Directory.Exists(adapterFolder))
            return VisionCameraPluginLoadResult.Fail($"Camera adapter folder does not exist: {adapterFolder}");

        var manifestPath = Directory.EnumerateFiles(adapterFolder, "*.camera-adapter.json", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(adapterFolder, "camera_adapter_manifest.json", SearchOption.TopDirectoryOnly))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(manifestPath))
            return VisionCameraPluginLoadResult.Fail("No camera adapter manifest was found. Expected *.camera-adapter.json or camera_adapter_manifest.json.");

        return LoadFactoryFromManifest(manifestPath);
    }

    public static VisionCameraPluginLoadResult LoadFactoryFromManifest(string manifestPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
                return VisionCameraPluginLoadResult.Fail("Camera adapter manifest file was not found.");

            var manifestFolder = Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? Directory.GetCurrentDirectory();
            var manifest = JsonSerializer.Deserialize<VisionCameraAdapterManifest>(File.ReadAllText(manifestPath), JsonOptions)
                ?? new VisionCameraAdapterManifest();
            var validation = ValidateManifest(manifest);
            if (validation.Count > 0)
                return VisionCameraPluginLoadResult.Fail(string.Join(" ", validation));

            var assemblyFile = manifest.EffectiveAssemblyFile;
            var assemblyPath = Path.IsPathRooted(assemblyFile)
                ? assemblyFile
                : Path.Combine(manifestFolder, assemblyFile);
            if (!File.Exists(assemblyPath))
                return VisionCameraPluginLoadResult.Fail($"Camera adapter assembly was not found: {assemblyPath}");

            var assembly = Assembly.LoadFrom(assemblyPath);
            var factoryTypeName = manifest.EffectiveFactoryTypeName;
            var factoryType = assembly.GetType(factoryTypeName, throwOnError: false);
            if (factoryType is null)
                return VisionCameraPluginLoadResult.Fail($"Factory type '{factoryTypeName}' was not found in adapter assembly.");
            if (!typeof(IVisionCameraAdapterFactory).IsAssignableFrom(factoryType))
                return VisionCameraPluginLoadResult.Fail($"Factory type '{factoryTypeName}' does not implement IVisionCameraAdapterFactory.");
            if (Activator.CreateInstance(factoryType) is not IVisionCameraAdapterFactory factory)
                return VisionCameraPluginLoadResult.Fail($"Factory type '{factoryTypeName}' could not be created.");

            var identityErrors = ValidateFactoryIdentity(manifest, factory);
            if (identityErrors.Count > 0)
                return VisionCameraPluginLoadResult.Fail(string.Join(" ", identityErrors));

            return new VisionCameraPluginLoadResult(true, manifest, factory, $"Loaded camera adapter {factory.DisplayName} {factory.Version}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or ReflectionTypeLoadException or TargetInvocationException or TypeLoadException or JsonException)
        {
            return VisionCameraPluginLoadResult.Fail($"Camera adapter plugin failed safely: {ex.Message}");
        }
    }

    public static IVisionDeviceDiscovery CreateDiscovery(CameraSourceSettings settings, out string message)
    {
        var load = LoadFactory(settings.AdapterFolder);
        message = load.Message;
        if (!load.Success || load.Factory is null)
            return new NullVisionDeviceDiscovery(message);

        return load.Factory.CreateDeviceDiscovery(settings) ?? new NullVisionDeviceDiscovery("Loaded adapter does not provide device discovery.");
    }

    public static IVisionCameraAdapter CreateAdapterOrNull(CameraSourceSettings settings, out string message)
    {
        var load = LoadFactory(settings.AdapterFolder);
        message = load.Message;
        if (!load.Success || load.Factory is null)
            return new DiagnosticNullVisionCameraAdapter(message);

        try
        {
            return load.Factory.CreateAdapter(settings) ?? new DiagnosticNullVisionCameraAdapter("Loaded adapter factory returned no camera adapter.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            message = $"Loaded adapter factory failed safely: {ex.Message}";
            return new DiagnosticNullVisionCameraAdapter(message);
        }
    }

    private static List<string> ValidateManifest(VisionCameraAdapterManifest manifest)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(manifest.AdapterId))
            errors.Add("AdapterId is required.");
        if (string.IsNullOrWhiteSpace(manifest.DisplayName))
            errors.Add("DisplayName is required.");
        if (string.IsNullOrWhiteSpace(manifest.Version))
            errors.Add("Version is required.");
        if (string.IsNullOrWhiteSpace(manifest.EffectiveAssemblyFile))
            errors.Add("AssemblyFile is required.");
        if (string.IsNullOrWhiteSpace(manifest.EffectiveFactoryTypeName))
            errors.Add("FactoryTypeName is required.");
        if (manifest.SupportedInterfaces.Count == 0 && manifest.SupportedViews.Count == 0)
            errors.Add("SupportedInterfaces must list at least one interface, such as GigE Vision or USB3 Vision.");
        if (manifest.SupportedPixelFormats.Count == 0)
            errors.Add("SupportedPixelFormats must list at least one pixel format.");
        return errors;
    }

    private static List<string> ValidateFactoryIdentity(VisionCameraAdapterManifest manifest, IVisionCameraAdapterFactory factory)
    {
        var errors = new List<string>();
        if (!string.Equals(manifest.AdapterId, factory.AdapterId, StringComparison.OrdinalIgnoreCase))
            errors.Add("Adapter factory identity does not match manifest AdapterId.");
        if (!string.Equals(manifest.Version, factory.Version, StringComparison.OrdinalIgnoreCase))
            errors.Add("Adapter factory version does not match manifest Version.");
        if (manifest.SupportedInterfaces.Count > 0 && !ContainsAll(factory.SupportedInterfaces, manifest.SupportedInterfaces))
            errors.Add("Adapter factory supported interfaces do not satisfy manifest SupportedInterfaces.");
        if (manifest.SupportedViews.Count > 0 && !ContainsAll(factory.SupportedViews, manifest.SupportedViews))
            errors.Add("Adapter factory supported views do not satisfy manifest SupportedViews.");
        if (!ContainsAll(factory.SupportedPixelFormats, manifest.SupportedPixelFormats))
            errors.Add("Adapter factory supported pixel formats do not satisfy manifest SupportedPixelFormats.");
        return errors;
    }

    private static bool ContainsAll(IReadOnlyList<string> actual, IReadOnlyList<string> required)
        => required.All(item => actual.Contains(item, StringComparer.OrdinalIgnoreCase));
}

public static class CameraAdapterPluginService
{
    public static VisionCameraPluginLoadResult LoadFactory(string adapterFolder)
        => VisionCameraPluginLoader.LoadFactory(adapterFolder);

    public static VisionCameraPluginLoadResult LoadFactoryFromManifest(string manifestPath)
        => VisionCameraPluginLoader.LoadFactoryFromManifest(manifestPath);

    public static IVisionDeviceDiscovery CreateDiscovery(CameraSourceSettings settings, out string message)
        => VisionCameraPluginLoader.CreateDiscovery(settings, out message);

    public static IVisionCameraAdapter CreateAdapterOrNull(CameraSourceSettings settings, out string message)
        => VisionCameraPluginLoader.CreateAdapterOrNull(settings, out message);
}

public sealed class NullVisionCameraAdapter : IVisionCameraAdapter
{
    public bool Connect(CameraViewType viewType, string deviceId, CameraAcquisitionMode acquisitionMode, double exposureMs, double gain, int timeoutMs) => false;
    public void Disconnect()
    {
    }

    public bool Start() => false;
    public void Stop()
    {
    }

    public bool Trigger(int timeoutMs) => false;

    public bool TryGetFrame(CameraViewType viewType, int timeoutMs, out CameraFrame? frame)
    {
        frame = null;
        return false;
    }

    public VisionCameraDiagnostics GetDiagnostics()
        => new(
            CameraSourceStatus.NotConnected,
            "Generic Vision Adapter is configured, but no vendor SDK adapter is installed.",
            new[]
            {
                "This is the safe default Stage 2 boundary.",
                "Install or inject a vendor-specific IVisionCameraAdapter to connect real GigE/USB3 Vision hardware.",
                "No real camera readiness is claimed by this adapter.",
            });
}

public sealed class DiagnosticNullVisionCameraAdapter : IVisionCameraAdapter
{
    private readonly string _message;

    public DiagnosticNullVisionCameraAdapter(string message)
    {
        _message = string.IsNullOrWhiteSpace(message) ? "Vision adapter is not connected." : message;
    }

    public bool Connect(CameraViewType viewType, string deviceId, CameraAcquisitionMode acquisitionMode, double exposureMs, double gain, int timeoutMs) => false;
    public void Disconnect()
    {
    }

    public bool Start() => false;
    public void Stop()
    {
    }

    public bool Trigger(int timeoutMs) => false;

    public bool TryGetFrame(CameraViewType viewType, int timeoutMs, out CameraFrame? frame)
    {
        frame = null;
        return false;
    }

    public VisionCameraDiagnostics GetDiagnostics()
        => new(CameraSourceStatus.NotConnected, _message, new[] { _message, "No vendor SDK command was sent." });
}

public sealed class NullVisionDeviceDiscovery : IVisionDeviceDiscovery
{
    private readonly string _message;

    public NullVisionDeviceDiscovery(string message)
    {
        _message = string.IsNullOrWhiteSpace(message) ? "No camera discovery adapter is available." : message;
    }

    public IReadOnlyList<VisionDeviceInfo> DiscoverDevices()
        => new[]
        {
            new VisionDeviceInfo(
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                "None",
                string.Empty,
                string.Empty,
                string.Create(CultureInfo.InvariantCulture, $"NotConnected: {_message}")),
        };
}
