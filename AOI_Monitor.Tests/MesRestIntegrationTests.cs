using System.Net;
using System.Text;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Xunit;

namespace AOI_Monitor.Tests;

public sealed class MesRestIntegrationTests : IDisposable
{
    private readonly string _root;
    private readonly IMesClient _previousMesClient;
    private readonly ITraceabilityUploader _previousTraceabilityUploader;

    public MesRestIntegrationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AOI_Monitor_MesRest_Tests", Guid.NewGuid().ToString("N"));
        AoiDatabase.ConfigureStorageRoot(_root);
        AoiDatabase.Initialize();
        _previousMesClient = IntegrationBoundaryRegistry.MesClient;
        _previousTraceabilityUploader = IntegrationBoundaryRegistry.TraceabilityUploader;
        MesIntegrationSettingsService.RestClientFactory = null;
    }

    public void Dispose()
    {
        MesIntegrationSettingsService.RestClientFactory = null;
        IntegrationBoundaryRegistry.MesClient = _previousMesClient;
        IntegrationBoundaryRegistry.TraceabilityUploader = _previousTraceabilityUploader;
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task MesRestClientSendsExpectedJsonAndAuthenticationHeader()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { ReasonPhrase = "OK" });
        var settings = RestSettings();
        settings.AuthMode = MesRestAuthMode.ApiKey;
        settings.ApiKeyHeaderName = "X-Test-Key";
        settings.ApiKey = "secret-api-key";
        settings.MaxRetryCount = 0;
        var client = new MesRestClient(settings, handler);

        var result = await client.UploadTraceabilityAsync(new TraceabilityPayload
        {
            LotId = "LOT-1",
            BoardModel = "TBOX",
            Result = "NG",
        });

        Assert.True(result.Accepted);
        Assert.Equal(IntegrationConnectionStatus.Ready, result.Status);
        Assert.Equal(HttpMethod.Post, handler.LastRequest?.Method);
        Assert.Equal("http://mes.test/api/aoi/results", handler.LastRequest?.RequestUri?.ToString());
        Assert.NotNull(handler.LastRequest);
        Assert.True(handler.LastRequest.Headers.TryGetValues("X-Test-Key", out var values));
        Assert.Equal("secret-api-key", Assert.Single(values));
        Assert.Contains("\"lotId\":\"LOT-1\"", handler.LastContent);
        Assert.Contains("\"result\":\"NG\"", handler.LastContent);
    }

    [Fact]
    public async Task Http500RecordsFailedAttemptAndCreatesSpoolItem()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError) { ReasonPhrase = "Server Error" });
        MesIntegrationSettingsService.RestClientFactory = settings => new MesRestClient(settings, handler);
        MesIntegrationSettingsService.Save(RestSettings(maxRetryCount: 0));

        var outcome = await TraceabilityUploadService.UploadAsync(new TraceabilityPayload
        {
            LotId = "LOT-500",
            BoardModel = "TBOX",
            StationId = "AOI-LIB-01",
            OperatorId = "Engineer01 [Engineer]",
            Result = "NG",
            DefectSummary = "Synthetic failure",
        });

        var attempts = AoiDatabase.GetMesUploadAttempts();
        var spool = AoiDatabase.GetMesSpoolQueue();

        Assert.False(outcome.Result.Accepted);
        Assert.Equal(IntegrationConnectionStatus.Error, outcome.Result.Status);
        Assert.Single(attempts);
        Assert.Equal("REST", attempts[0].Mode);
        Assert.Equal("ERROR", attempts[0].Status);
        Assert.Single(spool);
        Assert.Equal("Pending", spool[0].Status);
        Assert.Equal("LOT-500", spool[0].LotId);
        Assert.Contains("HTTP 500", spool[0].LastError);
    }

    [Fact]
    public void MesSettingsSummaryRedactsSecrets()
    {
        var settings = RestSettings();
        settings.AuthMode = MesRestAuthMode.Basic;
        settings.Username = "mes-user";
        settings.Password = "super-secret-password";
        settings.ApiKey = "api-secret";
        settings.BearerToken = "bearer-secret";

        var summary = MesIntegrationSettingsService.RedactedSummary(settings);

        Assert.Contains("auth=Basic", summary);
        Assert.Contains("***", summary);
        Assert.DoesNotContain("super-secret-password", summary);
        Assert.DoesNotContain("api-secret", summary);
        Assert.DoesNotContain("bearer-secret", summary);
    }

    [Fact]
    public async Task SpoolRetryMarksSuccessfulItemSent()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { ReasonPhrase = "OK" });
        MesIntegrationSettingsService.RestClientFactory = settings => new MesRestClient(settings, handler);
        MesIntegrationSettingsService.Save(RestSettings(maxRetryCount: 0));
        EnqueueTraceabilitySpool("LOT-RETRY", maxRetryCount: 3);

        var summary = await TraceabilityUploadService.RetryPendingMesUploadsAsync();

        Assert.Equal(1, summary.Attempted);
        Assert.Equal(1, summary.Succeeded);
        var spool = AoiDatabase.GetMesSpoolQueue();
        var item = Assert.Single(spool);
        Assert.Equal("Sent", item.Status);
        Assert.Contains("200 OK", item.LastError);
    }

    [Fact]
    public async Task SpoolRetryIncrementsRetryCountOnFailure()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { ReasonPhrase = "Unavailable" });
        MesIntegrationSettingsService.RestClientFactory = settings => new MesRestClient(settings, handler);
        MesIntegrationSettingsService.Save(RestSettings(maxRetryCount: 0, retryBackoffMs: 0));
        EnqueueTraceabilitySpool("LOT-FAIL", maxRetryCount: 3);

        var summary = await TraceabilityUploadService.RetryPendingMesUploadsAsync();
        var spool = AoiDatabase.GetMesSpoolQueue();

        Assert.Equal(1, summary.Attempted);
        Assert.Equal(1, summary.Failed);
        Assert.Single(spool);
        Assert.Equal(1, spool[0].RetryCount);
        Assert.Equal("Pending", spool[0].Status);
        Assert.Contains("HTTP 503", spool[0].LastError);
    }

    [Fact]
    public void AbandonRequiresAdminAndMarksQueueItem()
    {
        EnqueueTraceabilitySpool("LOT-ABANDON", maxRetryCount: 3);
        var item = Assert.Single(AoiDatabase.GetMesSpoolQueue());

        Assert.Throws<UnauthorizedAccessException>(() =>
            MesSpoolService.MarkAbandoned(item.Id, UserRole.Engineer, "Engineer01 [Engineer]", "Test abandon"));

        MesSpoolService.MarkAbandoned(item.Id, UserRole.Admin, "Admin01 [Admin]", "Test abandon");

        var spool = AoiDatabase.GetMesSpoolQueue();
        var abandoned = Assert.Single(spool);
        Assert.Equal("Abandoned", abandoned.Status);
        Assert.Equal("Test abandon", abandoned.LastError);
    }

    [Fact]
    public void MesReadinessReportsPendingAndMockStates()
    {
        MesIntegrationSettingsService.Save(RestSettings(maxRetryCount: 0));
        EnqueueTraceabilitySpool("LOT-PENDING", maxRetryCount: 3);

        var pending = MesSpoolService.EvaluateReadiness();

        Assert.Equal("MES Queue Pending", pending.Status);
        Assert.Equal(1, pending.PendingCount);

        AoiDatabase.MarkMesSpoolItemAbandoned(AoiDatabase.GetMesSpoolQueue()[0].Id, "Clear pending test item", "Admin01 [Admin]");
        var settings = RestSettings();
        settings.Mode = MesIntegrationMode.MockRest;
        MesIntegrationSettingsService.Save(settings);

        var mock = MesSpoolService.EvaluateReadiness();

        Assert.Equal("MES Mock Only", mock.Status);
        Assert.Contains(mock.Messages, message => message.Contains("Mock MES", StringComparison.OrdinalIgnoreCase));
    }

    private static MesIntegrationSettings RestSettings(int maxRetryCount = 0, int retryBackoffMs = 0)
        => new()
        {
            Mode = MesIntegrationMode.Rest,
            BaseUrl = "http://mes.test",
            UploadResultPath = "/api/aoi/results",
            UploadImagePath = "/api/aoi/images",
            AuthMode = MesRestAuthMode.None,
            TimeoutSeconds = 5,
            UploadTimeoutSeconds = 5,
            MaxRetryCount = maxRetryCount,
            RetryBackoffMs = retryBackoffMs,
        };

    private static void EnqueueTraceabilitySpool(string lotId, int maxRetryCount)
    {
        var payload = new TraceabilityPayload
        {
            LotId = lotId,
            BoardModel = "TBOX",
            StationId = "AOI-LIB-01",
            OperatorId = "Engineer01 [Engineer]",
            Result = "REVIEW",
            DefectSummary = "Retry test",
        };
        var json = System.Text.Json.JsonSerializer.Serialize(payload, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
        AoiDatabase.EnqueueMesSpoolItem(
            nameof(TraceabilityPayload),
            json,
            @"C:\payloads\traceability.json",
            "http://mes.test/api/aoi/results",
            maxRetryCount,
            "Initial failure",
            payload.OperatorId,
            payload.LotId,
            payload.BoardModel,
            payload.Result);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

        public CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        public HttpRequestMessage? LastRequest { get; private set; }
        public string LastContent { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastContent = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            LastRequest = CloneRequestMetadata(request);
            return _responseFactory(request);
        }

        private static HttpRequestMessage CloneRequestMetadata(HttpRequestMessage request)
        {
            var clone = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var header in request.Headers)
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            return clone;
        }
    }
}
