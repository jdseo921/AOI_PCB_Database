using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public sealed class CameraAdapterPackageValidationRequest
{
    public string AdapterFolder { get; set; } = string.Empty;
    public string SettingsJson { get; set; } = string.Empty;
    public string OutputFolder { get; set; } = string.Empty;
}

public sealed class CameraAdapterPackageValidationResult
{
    public string SchemaVersion { get; set; } = "camera-adapter-package-validation/v1";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string Status { get; set; } = "FAIL";
    public string AdapterFolder { get; set; } = string.Empty;
    public string SettingsJsonPath { get; set; } = string.Empty;
    public string OutputFolder { get; set; } = string.Empty;
    public string ManifestPath { get; set; } = string.Empty;
    public string AdapterId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string AssemblyFile { get; set; } = string.Empty;
    public string FactoryTypeName { get; set; } = string.Empty;
    public List<string> SupportedInterfaces { get; set; } = new();
    public List<string> SupportedViews { get; set; } = new();
    public List<string> SupportedPixelFormats { get; set; } = new();
    public string ManifestStatus { get; set; } = "NOT RUN";
    public string LoadStatus { get; set; } = "NOT RUN";
    public string LoadMessage { get; set; } = string.Empty;
    public bool AdapterLoaded { get; set; }
    public string DiscoveryStatus { get; set; } = "NOT RUN";
    public int DiscoveredDeviceCount { get; set; }
    public List<VisionDeviceInfo> DiscoveredDevices { get; set; } = new();
    public string AcceptanceStatus { get; set; } = "NOT RUN";
    public string FactoryReadinessStatus { get; set; } = "NOT VALIDATED";
    public bool IsRealHardwareReady { get; set; }
    public bool IsSimulatedEvidence { get; set; } = true;
    public CameraAcceptanceRun? AcceptanceRun { get; set; }
    public string AcceptanceJsonPath { get; set; } = string.Empty;
    public string AcceptanceHtmlPath { get; set; } = string.Empty;
    public string SummaryJsonPath { get; set; } = string.Empty;
    public string SummaryHtmlPath { get; set; } = string.Empty;
    public List<string> Messages { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public List<string> Failures { get; set; } = new();
}

public sealed class CameraAdapterPackageValidationException : Exception
{
    public CameraAdapterPackageValidationException(string message)
        : base(message)
    {
    }
}

public static class CameraAdapterPackageValidationService
{
    private static readonly JsonSerializerOptions ReadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions WriteJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static CameraAdapterPackageValidationResult Run(CameraAdapterPackageValidationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.OutputFolder))
            throw new CameraAdapterPackageValidationException("OutputFolder is required.");

        var result = new CameraAdapterPackageValidationResult
        {
            AdapterFolder = FullPathOrEmpty(request.AdapterFolder),
            SettingsJsonPath = FullPathOrEmpty(request.SettingsJson),
            OutputFolder = Path.GetFullPath(request.OutputFolder),
        };

        Directory.CreateDirectory(result.OutputFolder);

        try
        {
            ValidatePackage(request, result);
        }
        finally
        {
            FinalizeAndExport(result);
        }

        return result;
    }

    private static void ValidatePackage(CameraAdapterPackageValidationRequest request, CameraAdapterPackageValidationResult result)
    {
        if (string.IsNullOrWhiteSpace(request.AdapterFolder) || !Directory.Exists(request.AdapterFolder))
        {
            result.ManifestStatus = "FAIL";
            result.Failures.Add($"AdapterFolder does not exist: {request.AdapterFolder}");
            return;
        }

        var manifestPath = FindManifest(request.AdapterFolder, result);
        if (string.IsNullOrWhiteSpace(manifestPath))
        {
            result.ManifestStatus = "FAIL";
            return;
        }

        result.ManifestPath = manifestPath;
        var manifest = LoadManifest(manifestPath, result);
        if (manifest is null)
        {
            result.ManifestStatus = "FAIL";
            return;
        }

        CopyManifestFields(result, manifest);
        ValidateRequiredManifestFields(manifest, result);
        result.ManifestStatus = result.Failures.Count > 0 ? "FAIL" : "PASS";
        if (!string.Equals(result.ManifestStatus, "PASS", StringComparison.OrdinalIgnoreCase))
            return;

        var load = VisionCameraPluginLoader.LoadFactoryFromManifest(manifestPath);
        result.LoadMessage = load.Message;
        result.AdapterLoaded = load.Success && load.Factory is not null;
        result.LoadStatus = result.AdapterLoaded ? "PASS" : "FAIL";
        if (!result.AdapterLoaded || load.Factory is null)
        {
            result.Failures.Add(load.Message);
            return;
        }

        result.Messages.Add(load.Message);

        var settingsLoad = LoadSettings(request.SettingsJson, request.AdapterFolder, manifest);
        if (!settingsLoad.Success)
        {
            result.Failures.Add(settingsLoad.Message);
            return;
        }

        var settings = settingsLoad.Settings;
        var criteria = settingsLoad.Criteria;

        RunDiscovery(load.Factory, settings, criteria, result);
        RunAcceptance(load.Factory, settings, criteria, manifest, result);
    }

    private static string FindManifest(string adapterFolder, CameraAdapterPackageValidationResult result)
    {
        var primary = Path.Combine(adapterFolder, "camera_adapter_manifest.json");
        if (File.Exists(primary))
            return Path.GetFullPath(primary);

        var candidates = Directory.EnumerateFiles(adapterFolder, "*.camera-adapter.json", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (candidates.Length == 0)
        {
            result.Failures.Add("No camera adapter manifest was found. Expected camera_adapter_manifest.json or *.camera-adapter.json.");
            return string.Empty;
        }

        if (candidates.Length > 1)
            result.Warnings.Add($"Multiple *.camera-adapter.json manifests were found; using {Path.GetFileName(candidates[0])}.");
        return Path.GetFullPath(candidates[0]);
    }

    private static VisionCameraAdapterManifest? LoadManifest(string manifestPath, CameraAdapterPackageValidationResult result)
    {
        try
        {
            return JsonSerializer.Deserialize<VisionCameraAdapterManifest>(File.ReadAllText(manifestPath), ReadJsonOptions)
                ?? new VisionCameraAdapterManifest();
        }
        catch (JsonException ex)
        {
            result.Failures.Add($"Manifest JSON is invalid: {ex.Message}");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result.Failures.Add($"Manifest could not be read: {ex.Message}");
            return null;
        }
    }

    private static void CopyManifestFields(CameraAdapterPackageValidationResult result, VisionCameraAdapterManifest manifest)
    {
        result.AdapterId = manifest.AdapterId;
        result.DisplayName = manifest.DisplayName;
        result.Version = manifest.Version;
        result.AssemblyFile = manifest.EffectiveAssemblyFile;
        result.FactoryTypeName = manifest.EffectiveFactoryTypeName;
        result.SupportedInterfaces = manifest.SupportedInterfaces.ToList();
        result.SupportedViews = manifest.SupportedViews.ToList();
        result.SupportedPixelFormats = manifest.SupportedPixelFormats.ToList();
    }

    private static void ValidateRequiredManifestFields(VisionCameraAdapterManifest manifest, CameraAdapterPackageValidationResult result)
    {
        if (string.IsNullOrWhiteSpace(manifest.AdapterId))
            result.Failures.Add("adapterId is required.");
        if (string.IsNullOrWhiteSpace(manifest.DisplayName))
            result.Failures.Add("displayName is required.");
        if (string.IsNullOrWhiteSpace(manifest.Version))
            result.Failures.Add("version is required.");
        if (string.IsNullOrWhiteSpace(manifest.EffectiveAssemblyFile))
            result.Failures.Add("assemblyFile is required.");
        if (string.IsNullOrWhiteSpace(manifest.EffectiveFactoryTypeName))
            result.Failures.Add("factoryTypeName is required.");
        if (manifest.SupportedInterfaces.Count == 0)
            result.Failures.Add("supportedInterfaces must list at least one interface.");
        if (manifest.SupportedViews.Count == 0)
            result.Failures.Add("supportedViews must list at least one view.");
        if (manifest.SupportedPixelFormats.Count == 0)
            result.Failures.Add("supportedPixelFormats must list at least one pixel format.");
    }

    private static SettingsLoadResult LoadSettings(string settingsJson, string adapterFolder, VisionCameraAdapterManifest manifest)
    {
        var settings = new CameraSourceSettings
        {
            SourceKey = CameraSourceFactory.GenericVisionAdapterSourceKey,
            AdapterFolder = Path.GetFullPath(adapterFolder),
        };
        var criteria = BuildDefaultCriteria(manifest);

        if (!string.IsNullOrWhiteSpace(settingsJson))
        {
            if (!File.Exists(settingsJson))
                return SettingsLoadResult.Fail($"SettingsJson was provided but does not exist: {settingsJson}");

            try
            {
                var text = File.ReadAllText(settingsJson);
                using var document = JsonDocument.Parse(text);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object &&
                    (TryGetProperty(root, "cameraSourceSettings", out _) ||
                     TryGetProperty(root, "settings", out _) ||
                     TryGetProperty(root, "acceptanceCriteria", out _) ||
                     TryGetProperty(root, "criteria", out _)))
                {
                    if (TryGetProperty(root, "cameraSourceSettings", out var settingsElement) ||
                        TryGetProperty(root, "settings", out settingsElement))
                    {
                        settings = settingsElement.Deserialize<CameraSourceSettings>(ReadJsonOptions) ?? settings;
                    }

                    if (TryGetProperty(root, "acceptanceCriteria", out var criteriaElement) ||
                        TryGetProperty(root, "criteria", out criteriaElement))
                    {
                        criteria = criteriaElement.Deserialize<CameraAcceptanceCriteria>(ReadJsonOptions) ?? criteria;
                    }
                }
                else
                {
                    settings = JsonSerializer.Deserialize<CameraSourceSettings>(text, ReadJsonOptions) ?? settings;
                }
            }
            catch (JsonException ex)
            {
                return SettingsLoadResult.Fail($"SettingsJson is not valid JSON: {ex.Message}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return SettingsLoadResult.Fail($"SettingsJson could not be read: {ex.Message}");
            }
        }

        settings.SourceKey = CameraSourceFactory.GenericVisionAdapterSourceKey;
        settings.AdapterFolder = Path.GetFullPath(adapterFolder);
        if (criteria.RequiredPixelFormats.Count == 0)
            criteria.RequiredPixelFormats = manifest.SupportedPixelFormats.ToList();
        if (criteria.RequiredViews.Count == 0)
            criteria.RequiredViews = new List<string> { FirstSupportedViewOrTop(manifest) };
        return SettingsLoadResult.Ok(settings, criteria);
    }

    private static CameraAcceptanceCriteria BuildDefaultCriteria(VisionCameraAdapterManifest manifest)
        => new()
        {
            FramesPerView = 1,
            RequiredViews = new List<string> { FirstSupportedViewOrTop(manifest) },
            MaxConnectMs = 5000,
            MaxFirstFrameMs = 5000,
            MaxAverageFrameIntervalMs = 5000,
            MaxDroppedFrameRate = 0,
            MaxTriggerFailureRate = 0,
            MinimumWidth = 1,
            MinimumHeight = 1,
            RequiredPixelFormats = manifest.SupportedPixelFormats.ToList(),
        };

    private static string FirstSupportedViewOrTop(VisionCameraAdapterManifest manifest)
        => manifest.SupportedViews.FirstOrDefault(IsKnownView) ?? CameraViewType.Top.ToString();

    private static bool IsKnownView(string view)
        => Enum.TryParse<CameraViewType>(view, ignoreCase: true, out _);

    private static void RunDiscovery(
        IVisionCameraAdapterFactory factory,
        CameraSourceSettings settings,
        CameraAcceptanceCriteria criteria,
        CameraAdapterPackageValidationResult result)
    {
        IVisionDeviceDiscovery? discovery;
        try
        {
            discovery = factory.CreateDeviceDiscovery(settings);
        }
        catch (Exception ex)
        {
            result.DiscoveryStatus = "FAIL";
            result.Failures.Add($"Device discovery creation failed safely: {ex.Message}");
            return;
        }

        if (discovery is null)
        {
            result.DiscoveryStatus = "NOT SUPPORTED";
            result.Warnings.Add("Loaded adapter does not provide device discovery.");
            return;
        }

        try
        {
            var devices = discovery.DiscoverDevices() ?? Array.Empty<VisionDeviceInfo>();
            result.DiscoveredDevices = devices.ToList();
            result.DiscoveredDeviceCount = result.DiscoveredDevices.Count;
            result.DiscoveryStatus = result.DiscoveredDeviceCount > 0 ? "PASS" : "WARN";
            if (result.DiscoveredDeviceCount == 0)
                result.Warnings.Add("Device discovery completed but no devices were returned.");
            ApplyDiscoveryDefaults(settings, criteria, result.DiscoveredDevices);
        }
        catch (Exception ex)
        {
            result.DiscoveryStatus = "FAIL";
            result.Failures.Add($"Device discovery failed safely: {ex.Message}");
        }
    }

    private static void ApplyDiscoveryDefaults(
        CameraSourceSettings settings,
        CameraAcceptanceCriteria criteria,
        IReadOnlyList<VisionDeviceInfo> devices)
    {
        foreach (var viewName in criteria.RequiredViews)
        {
            if (!Enum.TryParse<CameraViewType>(viewName, ignoreCase: true, out var view))
                continue;
            if (!string.IsNullOrWhiteSpace(DeviceIdForView(settings, view)))
                continue;

            var match = devices.FirstOrDefault(device =>
                string.Equals(device.SuggestedView, view.ToString(), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(device.ViewAssignment, view.ToString(), StringComparison.OrdinalIgnoreCase));
            if (match is null && view == CameraViewType.Top)
                match = devices.Count > 0 ? devices[0] : null;
            if (match is not null && !string.IsNullOrWhiteSpace(match.DeviceId))
                SetDeviceIdForView(settings, view, match.DeviceId);
        }
    }

    private static void RunAcceptance(
        IVisionCameraAdapterFactory factory,
        CameraSourceSettings settings,
        CameraAcceptanceCriteria criteria,
        VisionCameraAdapterManifest manifest,
        CameraAdapterPackageValidationResult result)
    {
        IVisionCameraAdapter adapter;
        try
        {
            adapter = factory.CreateAdapter(settings);
            if (adapter is null)
            {
                result.Failures.Add("Loaded adapter factory returned no camera adapter.");
                return;
            }
        }
        catch (Exception ex)
        {
            result.Failures.Add($"Loaded adapter factory failed safely: {ex.Message}");
            return;
        }

        CameraAcceptanceRun run;
        try
        {
            run = CameraAcceptanceTestService.Run(settings, criteria, adapter);
        }
        catch (Exception ex)
        {
            result.Failures.Add($"Camera acceptance failed safely: {ex.Message}");
            return;
        }

        if (IsTemplateOrFakeAdapter(manifest, factory) && run.IsRealHardware)
        {
            run.IsRealHardware = false;
            run.FactoryReadinessStatus = "NOT VALIDATED";
            run.Warnings.Add("Template/fake camera adapters must mark frames IsSimulated=true and cannot produce real hardware readiness.");
            result.Failures.Add("Template/fake camera adapter attempted to produce real hardware readiness.");
        }

        result.AcceptanceRun = run;
        result.AcceptanceStatus = run.Status;
        result.FactoryReadinessStatus = run.FactoryReadinessStatus;
        result.IsRealHardwareReady = run.IsRealHardware && run.FactoryReadinessStatus is "PASS" or "WARN";
        result.IsSimulatedEvidence = !run.IsRealHardware || run.Frames.Any(frame => frame.IsSimulated);
        if (result.IsSimulatedEvidence)
            result.Warnings.Add("Camera acceptance evidence is simulation-only or not real hardware; Stage 2 real hardware readiness remains NOT VALIDATED.");
        if (string.Equals(run.Status, "FAIL", StringComparison.OrdinalIgnoreCase))
            result.Failures.Add("Camera acceptance test failed. Review the exported JSON/HTML report for frame, metadata, and timing failures.");
        else if (string.Equals(run.Status, "WARN", StringComparison.OrdinalIgnoreCase))
            result.Warnings.Add("Camera acceptance test completed with warnings.");

        var acceptanceFolder = Path.Combine(result.OutputFolder, "camera_acceptance");
        var export = CameraAcceptanceTestService.ExportReport(run, acceptanceFolder);
        result.AcceptanceJsonPath = export.JsonPath;
        result.AcceptanceHtmlPath = export.HtmlPath;
    }

    private static void FinalizeAndExport(CameraAdapterPackageValidationResult result)
    {
        result.Status = result.Failures.Count > 0
            ? "FAIL"
            : result.Warnings.Count > 0
                ? "WARN"
                : "PASS";
        result.SummaryJsonPath = Path.Combine(result.OutputFolder, "camera_adapter_validation_summary.json");
        result.SummaryHtmlPath = Path.Combine(result.OutputFolder, "camera_adapter_validation_summary.html");

        File.WriteAllText(result.SummaryJsonPath, JsonSerializer.Serialize(result, WriteJsonOptions), Encoding.UTF8);
        File.WriteAllText(result.SummaryHtmlPath, BuildHtml(result), Encoding.UTF8);
    }

    private static string BuildHtml(CameraAdapterPackageValidationResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html><html><head><meta charset=\"utf-8\"><title>Camera Adapter Package Validation</title>");
        sb.AppendLine("<style>body{font-family:Segoe UI,Arial,sans-serif;background:#f7f9fb;color:#17212b;margin:24px}.card{background:white;border:1px solid #d8e0e7;padding:16px;margin:0 0 16px}table{border-collapse:collapse;width:100%}td,th{border:1px solid #d8e0e7;padding:6px;text-align:left}.fail{color:#a12626}.warn{color:#8a5a00}.ok{color:#176b3a}</style></head><body>");
        sb.AppendLine("<h1>Stage 2 Camera Adapter Package Validation</h1>");
        sb.AppendLine(CultureInfo.InvariantCulture, $"<div class=\"card\"><strong>Status:</strong> {Escape(result.Status)}<br><strong>Manifest:</strong> {Escape(result.ManifestStatus)}<br><strong>Load:</strong> {Escape(result.LoadStatus)}<br><strong>Discovery:</strong> {Escape(result.DiscoveryStatus)}<br><strong>Acceptance:</strong> {Escape(result.AcceptanceStatus)}<br><strong>Factory readiness:</strong> {Escape(result.FactoryReadinessStatus)}<br><strong>Accepted real hardware evidence:</strong> {result.IsRealHardwareReady}<br><strong>Simulated evidence:</strong> {result.IsSimulatedEvidence}</div>");
        sb.AppendLine("<div class=\"card\"><h2>Adapter</h2><table>");
        AppendRow(sb, "Adapter ID", result.AdapterId);
        AppendRow(sb, "Display name", result.DisplayName);
        AppendRow(sb, "Version", result.Version);
        AppendRow(sb, "Assembly", result.AssemblyFile);
        AppendRow(sb, "Factory", result.FactoryTypeName);
        AppendRow(sb, "Supported interfaces", string.Join(", ", result.SupportedInterfaces));
        AppendRow(sb, "Supported views", string.Join(", ", result.SupportedViews));
        AppendRow(sb, "Supported pixel formats", string.Join(", ", result.SupportedPixelFormats));
        AppendRow(sb, "Load message", result.LoadMessage);
        sb.AppendLine("</table></div>");
        AppendMessages(sb, "Failures", result.Failures, "fail");
        AppendMessages(sb, "Warnings / Limitations", result.Warnings, "warn");
        AppendMessages(sb, "Messages", result.Messages, string.Empty);
        sb.AppendLine("<div class=\"card\"><p>Fake, template, folder, replay, null, or metadata-only evidence is not real camera hardware readiness. Real Stage 2 readiness requires live hardware frames with IsSimulated=false and complete device metadata.</p></div>");
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    private static void AppendRow(StringBuilder sb, string label, string value)
        => sb.AppendLine($"<tr><th>{Escape(label)}</th><td>{Escape(value)}</td></tr>");

    private static void AppendMessages(StringBuilder sb, string title, IReadOnlyList<string> messages, string cssClass)
    {
        if (messages.Count == 0)
            return;

        var classAttribute = string.IsNullOrWhiteSpace(cssClass) ? "card" : $"card {cssClass}";
        sb.AppendLine($"<div class=\"{classAttribute}\"><h2>{Escape(title)}</h2><ul>");
        foreach (var message in messages)
            sb.AppendLine($"<li>{Escape(message)}</li>");
        sb.AppendLine("</ul></div>");
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool IsTemplateOrFakeAdapter(VisionCameraAdapterManifest manifest, IVisionCameraAdapterFactory factory)
        => ContainsBoundaryMarker(manifest.AdapterId) ||
           ContainsBoundaryMarker(manifest.DisplayName) ||
           ContainsBoundaryMarker(manifest.EffectiveFactoryTypeName) ||
           ContainsBoundaryMarker(factory.AdapterId) ||
           ContainsBoundaryMarker(factory.DisplayName);

    private static bool ContainsBoundaryMarker(string value)
        => !string.IsNullOrWhiteSpace(value) &&
           (value.Contains("template", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("fake", StringComparison.OrdinalIgnoreCase));

    private static string DeviceIdForView(CameraSourceSettings settings, CameraViewType view)
        => view switch
        {
            CameraViewType.Side => settings.SideDeviceId,
            CameraViewType.Bottom => settings.BottomDeviceId,
            _ => settings.TopDeviceId,
        };

    private static void SetDeviceIdForView(CameraSourceSettings settings, CameraViewType view, string deviceId)
    {
        switch (view)
        {
            case CameraViewType.Side:
                settings.SideDeviceId = deviceId;
                break;
            case CameraViewType.Bottom:
                settings.BottomDeviceId = deviceId;
                break;
            default:
                settings.TopDeviceId = deviceId;
                break;
        }
    }

    private static string FullPathOrEmpty(string path)
        => string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFullPath(path);

    private static string Escape(string value)
        => WebUtility.HtmlEncode(value ?? string.Empty);

    private sealed record SettingsLoadResult(
        bool Success,
        string Message,
        CameraSourceSettings Settings,
        CameraAcceptanceCriteria Criteria)
    {
        public static SettingsLoadResult Ok(CameraSourceSettings settings, CameraAcceptanceCriteria criteria)
            => new(true, string.Empty, settings, criteria);

        public static SettingsLoadResult Fail(string message)
            => new(false, message, new CameraSourceSettings(), new CameraAcceptanceCriteria());
    }
}
