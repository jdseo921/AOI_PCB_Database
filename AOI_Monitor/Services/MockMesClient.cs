using System.Net.Http;
using System.Text;
using System.Text.Json;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public sealed class MockMesClient : IMesClient, ITraceabilityUploader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly MesIntegrationSettings _settings;

    public MockMesClient(MesIntegrationSettings settings)
    {
        _settings = settings;
    }

    public string Name => "Mock MES REST Client";
    public IntegrationConnectionStatus Status => IntegrationConnectionStatus.Simulated;
    public string StatusMessage => string.IsNullOrWhiteSpace(_settings.MockEndpointUrl)
        ? "Mock MES mode is enabled. No endpoint is configured, so uploads are written to local JSON only."
        : $"Mock MES mode is enabled for endpoint {_settings.MockEndpointUrl}. This is not production MES.";

    public async Task<IntegrationCommandResult> UploadTraceabilityAsync(
        TraceabilityPayload payload,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.MockEndpointUrl))
        {
            return new IntegrationCommandResult(
                true,
                IntegrationConnectionStatus.Simulated,
                "No mock endpoint configured; payload retained as local JSON mock evidence.");
        }

        try
        {
            using var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(Math.Clamp(_settings.UploadTimeoutSeconds, 1, 300)),
            };
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(_settings.MockEndpointUrl, content, cancellationToken);
            var message = $"Mock MES endpoint returned {(int)response.StatusCode} {response.ReasonPhrase}.";
            return new IntegrationCommandResult(
                response.IsSuccessStatusCode,
                response.IsSuccessStatusCode ? IntegrationConnectionStatus.Simulated : IntegrationConnectionStatus.Error,
                message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException)
        {
            return new IntegrationCommandResult(
                false,
                IntegrationConnectionStatus.Error,
                $"Mock MES upload failed: {ex.Message}");
        }
    }

    public Task<IntegrationCommandResult> UploadResultAsync(
        UploadResultCommand command,
        CancellationToken cancellationToken = default)
    {
        var payload = new TraceabilityPayload
        {
            LotId = command.LotId,
            BoardModel = command.BoardModel,
            StationId = "AOI-LIB-01",
            OperatorId = command.OperatorId,
            Result = command.Result,
            TimestampUtc = command.TimestampUtc,
            DefectSummary = $"BoardId={command.BoardId}; InspectionId={command.InspectionId}",
        };
        return UploadTraceabilityAsync(payload, cancellationToken);
    }

    public Task<IntegrationCommandResult> UploadImageAsync(
        UploadImageCommand command,
        CancellationToken cancellationToken = default)
    {
        var payload = new TraceabilityPayload
        {
            StationId = "AOI-LIB-01",
            OperatorId = "UNKNOWN",
            Result = "IMAGE_UPLOAD",
            TimestampUtc = command.TimestampUtc,
            ImagePath = command.ImagePath,
            DefectSummary = $"ImageType={command.ImageType}; BoardId={command.BoardId}; InspectionId={command.InspectionId}",
        };
        return UploadTraceabilityAsync(payload, cancellationToken);
    }
}
