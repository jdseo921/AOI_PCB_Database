using System.IO;
using System.Text.Json;
using AOI_Monitor.Data;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public static class MesIntegrationSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static MesIntegrationSettings? _cached;

    public static event Action? SettingsChanged;
    public static Func<MesIntegrationSettings, MesRestClient>? RestClientFactory { get; set; }

    public static string SettingsPath => Path.Combine(AoiDatabase.StorageRoot, "mes_integration_settings.json");

    public static MesIntegrationSettings Load()
    {
        if (_cached is not null)
            return Clone(_cached);

        Directory.CreateDirectory(AoiDatabase.StorageRoot);
        if (!File.Exists(SettingsPath))
        {
            _cached = new MesIntegrationSettings();
            Save(_cached, notify: false);
            return Clone(_cached);
        }

        try
        {
            var json = File.ReadAllText(SettingsPath);
            _cached = JsonSerializer.Deserialize<MesIntegrationSettings>(json) ?? new MesIntegrationSettings();
            UnprotectSecrets(_cached);
        }
        catch
        {
            _cached = new MesIntegrationSettings();
        }

        Normalize(_cached);
        return Clone(_cached);
    }

    public static void Save(MesIntegrationSettings settings)
        => Save(settings, notify: true);

    public static void ApplyIntegrationBoundary()
    {
        var settings = Load();
        if (settings.Mode == MesIntegrationMode.MockRest)
        {
            var client = new MockMesClient(settings);
            IntegrationBoundaryRegistry.MesClient = client;
            IntegrationBoundaryRegistry.TraceabilityUploader = client;
            return;
        }

        if (settings.Mode == MesIntegrationMode.Rest)
        {
            var client = RestClientFactory?.Invoke(settings) ?? new MesRestClient(settings);
            IntegrationBoundaryRegistry.MesClient = client;
            IntegrationBoundaryRegistry.TraceabilityUploader = client;
            return;
        }

        IntegrationBoundaryRegistry.MesClient = new NullMesClient();
        IntegrationBoundaryRegistry.TraceabilityUploader = new NullTraceabilityUploader();
    }

    public static IReadOnlyList<string> Validate(MesIntegrationSettings settings)
    {
        Normalize(settings);
        var errors = new List<string>();

        if (settings.Mode == MesIntegrationMode.Rest)
        {
            if (!Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https"))
            {
                errors.Add("MES REST base URL must be an absolute http/https URL.");
            }

            if (string.IsNullOrWhiteSpace(settings.UploadResultPath))
                errors.Add("MES REST result path is required.");
            if (string.IsNullOrWhiteSpace(settings.UploadImagePath))
                errors.Add("MES REST image path is required.");

            if (settings.AuthMode == MesRestAuthMode.ApiKey &&
                (string.IsNullOrWhiteSpace(settings.ApiKeyHeaderName) || string.IsNullOrWhiteSpace(settings.ApiKey)))
            {
                errors.Add("MES REST API-key auth requires a header name and API key.");
            }

            if (settings.AuthMode == MesRestAuthMode.Bearer && string.IsNullOrWhiteSpace(settings.BearerToken))
                errors.Add("MES REST bearer auth requires a token.");

            if (settings.AuthMode == MesRestAuthMode.Basic &&
                (string.IsNullOrWhiteSpace(settings.Username) || string.IsNullOrWhiteSpace(settings.Password)))
            {
                errors.Add("MES REST basic auth requires username and password.");
            }
        }

        return errors;
    }

    public static string RedactedSummary(MesIntegrationSettings settings)
    {
        Normalize(settings);
        return settings.Mode switch
        {
            MesIntegrationMode.Rest =>
                $"REST endpoint={settings.BaseUrl}; resultPath={settings.UploadResultPath}; imagePath={settings.UploadImagePath}; auth={settings.AuthMode}; autoUpload={settings.AutoUploadEnabled}; retries={settings.MaxRetryCount}; secrets={RedactedSecretMarker(settings)}",
            MesIntegrationMode.MockRest => string.IsNullOrWhiteSpace(settings.MockEndpointUrl)
                ? $"Mock local JSON; autoUpload={settings.AutoUploadEnabled}"
                : $"Mock REST endpoint={settings.MockEndpointUrl}; autoUpload={settings.AutoUploadEnabled}",
            MesIntegrationMode.FutureProduction => "Future Production Planned; OPC UA adapter TODO for Stage 4.",
            _ => "Not Connected",
        };
    }

    public static string RedactSecrets(string text, MesIntegrationSettings? settings = null)
    {
        var redacted = text ?? string.Empty;
        var source = settings ?? _cached ?? new MesIntegrationSettings();
        redacted = SecretProtectionService.RedactKnownSecrets(redacted, source.ApiKey, source.BearerToken, source.Password);

        if (!string.IsNullOrWhiteSpace(source.Username) && source.AuthMode == MesRestAuthMode.Basic)
            redacted = redacted.Replace(source.Username, "***", StringComparison.Ordinal);
        return redacted;
    }

    private static void Save(MesIntegrationSettings settings, bool notify)
    {
        Normalize(settings);
        Directory.CreateDirectory(AoiDatabase.StorageRoot);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(ProtectedClone(settings), JsonOptions));
        _cached = Clone(settings);
        ApplyIntegrationBoundary();

        if (notify)
            SettingsChanged?.Invoke();
    }

    private static void Normalize(MesIntegrationSettings settings)
    {
        if (!Enum.IsDefined(settings.Mode))
            settings.Mode = MesIntegrationMode.NotConnected;
        if (!Enum.IsDefined(settings.AuthMode))
            settings.AuthMode = MesRestAuthMode.None;

        settings.MockEndpointUrl = settings.MockEndpointUrl?.Trim() ?? string.Empty;
        settings.UploadTimeoutSeconds = Math.Clamp(settings.UploadTimeoutSeconds, 1, 300);
        settings.BaseUrl = settings.BaseUrl?.Trim() ?? string.Empty;
        settings.UploadResultPath = NormalizePath(settings.UploadResultPath, "/api/aoi/results");
        settings.UploadImagePath = NormalizePath(settings.UploadImagePath, "/api/aoi/images");
        settings.ApiKeyHeaderName = string.IsNullOrWhiteSpace(settings.ApiKeyHeaderName) ? "X-API-Key" : settings.ApiKeyHeaderName.Trim();
        settings.ApiKey = settings.ApiKey ?? string.Empty;
        settings.BearerToken = settings.BearerToken ?? string.Empty;
        settings.Username = settings.Username?.Trim() ?? string.Empty;
        settings.Password = settings.Password ?? string.Empty;
        settings.TimeoutSeconds = Math.Clamp(settings.TimeoutSeconds <= 0 ? settings.UploadTimeoutSeconds : settings.TimeoutSeconds, 1, 300);
        settings.UploadTimeoutSeconds = settings.TimeoutSeconds;
        settings.MaxRetryCount = Math.Clamp(settings.MaxRetryCount, 0, 20);
        settings.RetryBackoffMs = Math.Clamp(settings.RetryBackoffMs <= 0 ? 500 : settings.RetryBackoffMs, 0, 600000);
    }

    private static MesIntegrationSettings Clone(MesIntegrationSettings source)
        => new()
        {
            Mode = source.Mode,
            MockEndpointUrl = source.MockEndpointUrl,
            UploadTimeoutSeconds = source.UploadTimeoutSeconds,
            AutoUploadEnabled = source.AutoUploadEnabled,
            BaseUrl = source.BaseUrl,
            UploadResultPath = source.UploadResultPath,
            UploadImagePath = source.UploadImagePath,
            AuthMode = source.AuthMode,
            ApiKeyHeaderName = source.ApiKeyHeaderName,
            ApiKey = source.ApiKey,
            BearerToken = source.BearerToken,
            Username = source.Username,
            Password = source.Password,
            TimeoutSeconds = source.TimeoutSeconds,
            MaxRetryCount = source.MaxRetryCount,
            RetryBackoffMs = source.RetryBackoffMs,
        };

    private static MesIntegrationSettings ProtectedClone(MesIntegrationSettings source)
    {
        var clone = Clone(source);
        clone.ApiKey = SecretProtectionService.Protect(clone.ApiKey);
        clone.BearerToken = SecretProtectionService.Protect(clone.BearerToken);
        clone.Password = SecretProtectionService.Protect(clone.Password);
        return clone;
    }

    private static void UnprotectSecrets(MesIntegrationSettings settings)
    {
        settings.ApiKey = SecretProtectionService.Unprotect(settings.ApiKey);
        settings.BearerToken = SecretProtectionService.Unprotect(settings.BearerToken);
        settings.Password = SecretProtectionService.Unprotect(settings.Password);
    }

    private static string NormalizePath(string? path, string fallback)
    {
        var value = string.IsNullOrWhiteSpace(path) ? fallback : path.Trim();
        return value.StartsWith("/", StringComparison.Ordinal) ? value : $"/{value}";
    }

    private static string RedactedSecretMarker(MesIntegrationSettings settings)
        => settings.AuthMode switch
        {
            MesRestAuthMode.ApiKey => string.IsNullOrWhiteSpace(settings.ApiKey) ? "not configured" : "***",
            MesRestAuthMode.Bearer => string.IsNullOrWhiteSpace(settings.BearerToken) ? "not configured" : "***",
            MesRestAuthMode.Basic => string.IsNullOrWhiteSpace(settings.Password) ? "not configured" : "***",
            _ => "none",
        };
}
