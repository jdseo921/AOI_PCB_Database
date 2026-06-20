using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public sealed class MesRestClient : IMesClient, ITraceabilityUploader, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly MesIntegrationSettings _settings;
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public MesRestClient(MesIntegrationSettings settings)
        : this(settings, new HttpClient(), ownsClient: true)
    {
    }

    public MesRestClient(MesIntegrationSettings settings, HttpMessageHandler handler)
        : this(settings, new HttpClient(handler), ownsClient: true)
    {
    }

    public MesRestClient(MesIntegrationSettings settings, HttpClient client, bool ownsClient = false)
    {
        _settings = settings;
        _client = client;
        _ownsClient = ownsClient;
        _client.Timeout = TimeSpan.FromSeconds(Math.Clamp(_settings.TimeoutSeconds, 1, 300));
    }

    public string Name => "MES REST Client";
    public IntegrationConnectionStatus Status => MesIntegrationSettingsService.Validate(_settings).Count == 0
        ? IntegrationConnectionStatus.Ready
        : IntegrationConnectionStatus.Error;

    public string StatusMessage => Status == IntegrationConnectionStatus.Ready
        ? $"MES REST configured. {MesIntegrationSettingsService.RedactedSummary(_settings)}"
        : string.Join(" ", MesIntegrationSettingsService.Validate(_settings));

    public Task<IntegrationCommandResult> UploadTraceabilityAsync(
        TraceabilityPayload payload,
        CancellationToken cancellationToken = default)
        => PostJsonAsync(_settings.UploadResultPath, payload, "traceability payload", cancellationToken);

    public Task<IntegrationCommandResult> UploadResultAsync(
        UploadResultCommand command,
        CancellationToken cancellationToken = default)
        => PostJsonAsync(_settings.UploadResultPath, command, "inspection result", cancellationToken);

    public async Task<IntegrationCommandResult> UploadImageAsync(
        UploadImageCommand command,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.ImagePath) || !File.Exists(command.ImagePath))
        {
            return new IntegrationCommandResult(
                false,
                IntegrationConnectionStatus.Error,
                $"MES REST image upload failed: image file does not exist: {command.ImagePath}");
        }

        var validation = MesIntegrationSettingsService.Validate(_settings);
        if (validation.Count > 0)
            return new IntegrationCommandResult(false, IntegrationConnectionStatus.Error, string.Join(" ", validation));

        try
        {
            return await SendWithRetryAsync(
                () =>
                {
                    var request = CreateRequest(HttpMethod.Post, _settings.UploadImagePath);
                    var content = new MultipartFormDataContent
                    {
                        { new StringContent(command.InspectionId), "inspectionId" },
                        { new StringContent(command.BoardId), "boardId" },
                        { new StringContent(command.ImageType), "imageType" },
                        { new StringContent(command.TimestampUtc.ToString("O")), "timestampUtc" },
                    };
                    var bytes = File.ReadAllBytes(command.ImagePath);
                    var fileContent = new ByteArrayContent(bytes);
                    fileContent.Headers.ContentType = new MediaTypeHeaderValue(ContentTypeFromPath(command.ImagePath));
                    content.Add(fileContent, "file", Path.GetFileName(command.ImagePath));
                    request.Content = content;
                    return request;
                },
                $"image {Path.GetFileName(command.ImagePath)}",
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new IntegrationCommandResult(false, IntegrationConnectionStatus.Error, $"MES REST image upload failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
            _client.Dispose();
    }

    private async Task<IntegrationCommandResult> PostJsonAsync<T>(
        string relativePath,
        T payload,
        string payloadName,
        CancellationToken cancellationToken)
    {
        var validation = MesIntegrationSettingsService.Validate(_settings);
        if (validation.Count > 0)
            return new IntegrationCommandResult(false, IntegrationConnectionStatus.Error, string.Join(" ", validation));

        try
        {
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            return await SendWithRetryAsync(
                () =>
                {
                    var request = CreateRequest(HttpMethod.Post, relativePath);
                    request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                    return request;
                },
                payloadName,
                cancellationToken).ConfigureAwait(false);
        }
        catch (NotSupportedException ex)
        {
            return new IntegrationCommandResult(false, IntegrationConnectionStatus.Error, $"MES REST serialization failed: {ex.Message}");
        }
        catch (JsonException ex)
        {
            return new IntegrationCommandResult(false, IntegrationConnectionStatus.Error, $"MES REST serialization failed: {ex.Message}");
        }
    }

    private async Task<IntegrationCommandResult> SendWithRetryAsync(
        Func<HttpRequestMessage> requestFactory,
        string payloadName,
        CancellationToken cancellationToken)
    {
        var attempts = Math.Max(1, _settings.MaxRetryCount + 1);
        var lastMessage = string.Empty;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                using var request = requestFactory();
                using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return new IntegrationCommandResult(
                        true,
                        IntegrationConnectionStatus.Ready,
                        $"MES REST uploaded {payloadName}: {(int)response.StatusCode} {response.ReasonPhrase}.");
                }

                lastMessage = $"MES REST upload failed for {payloadName}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}.";
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lastMessage = $"MES REST upload timed out for {payloadName} after {_settings.TimeoutSeconds} seconds.";
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or UriFormatException)
            {
                lastMessage = $"MES REST upload failed for {payloadName}: {ex.Message}";
            }

            if (attempt < attempts && _settings.RetryBackoffMs > 0)
                await Task.Delay(_settings.RetryBackoffMs, cancellationToken).ConfigureAwait(false);
        }

        return new IntegrationCommandResult(false, IntegrationConnectionStatus.Error, lastMessage);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string relativePath)
    {
        var request = new HttpRequestMessage(method, BuildUri(relativePath));
        ApplyAuthentication(request);
        return request;
    }

    private Uri BuildUri(string relativePath)
    {
        var baseUrl = _settings.BaseUrl.TrimEnd('/') + "/";
        var path = (string.IsNullOrWhiteSpace(relativePath) ? _settings.UploadResultPath : relativePath).TrimStart('/');
        return new Uri(new Uri(baseUrl), path);
    }

    private void ApplyAuthentication(HttpRequestMessage request)
    {
        switch (_settings.AuthMode)
        {
            case MesRestAuthMode.ApiKey:
                request.Headers.TryAddWithoutValidation(_settings.ApiKeyHeaderName, _settings.ApiKey);
                break;
            case MesRestAuthMode.Bearer:
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.BearerToken);
                break;
            case MesRestAuthMode.Basic:
                var raw = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_settings.Username}:{_settings.Password}"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", raw);
                break;
        }
    }

    private static string ContentTypeFromPath(string path)
        => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".bmp" => "image/bmp",
            ".tif" or ".tiff" => "image/tiff",
            _ => "image/png",
        };
}
