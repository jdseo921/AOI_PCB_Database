using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using AOI_Monitor.Views;
using Xunit;

namespace AOI_Monitor.Tests;

/// <summary>
/// Contract tests added by the Stage 2-4 de-risking review (Docs/ARCHITECTURE.md, seam inventory):
/// real-hardware camera classification, soak-source injection, TCP lighting transport,
/// integration-registry survival, MES contract drift lock, central-sync queue correctness,
/// 3D dropout tolerance, and the template frame-on-disk obligation.
/// </summary>
public sealed class Stage2DeriskingSeamTests : IDisposable
{
    private readonly string _root;

    public Stage2DeriskingSeamTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AOI_Monitor_Derisk_Tests", Guid.NewGuid().ToString("N"));
        AoiDatabase.ConfigureStorageRoot(_root);
        CentralSyncSettingsService.ResetForTests();
    }

    public void Dispose()
    {
        CentralSyncSettingsService.ResetForTests();
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.WriteLine($"Test cleanup failed for {nameof(Stage2DeriskingSeamTests)}: {ex.Message}");
        }
    }

    [Fact]
    public void CameraAcceptanceRealHardwareSourceProducesRealHardwarePass()
    {
        AoiDatabase.Initialize();
        var framePath = WriteTinyPng("real-frame.png");
        var source = new ReadyCameraSource(framePath);

        var run = CameraAcceptanceTestService.Run(
            new CameraSourceSettings { SourceKey = CameraSourceFactory.GenericVisionAdapterSourceKey },
            sourceOverride: source);

        Assert.True(run.IsRealHardware);
        Assert.Equal("PASS", run.Status);
        Assert.Equal("PASS", run.FactoryReadinessStatus);
        Assert.DoesNotContain(run.Warnings, warning => warning.Contains("simulation evidence only", StringComparison.OrdinalIgnoreCase));
        var summary = CameraAcceptanceTestService.ToSummary(run);
        Assert.Equal("PASS", summary.Status);

        var id = AoiDatabase.RecordCameraAcceptanceRun(run, "derisk-test");
        Assert.True(id > 0);
        var persisted = AoiDatabase.GetLatestCameraAcceptanceRun();
        Assert.NotNull(persisted);
        Assert.True(persisted!.IsRealHardware);
    }

    [Fact]
    public void CameraAcceptanceWarnsWhenFramesHaveNoOnDiskSourcePath()
    {
        AoiDatabase.Initialize();
        var source = new ReadyCameraSource(framePath: string.Empty);

        var run = CameraAcceptanceTestService.Run(
            new CameraSourceSettings { SourceKey = CameraSourceFactory.GenericVisionAdapterSourceKey },
            sourceOverride: source);

        Assert.Contains(run.Warnings, warning => warning.Contains("no readable on-disk SourcePath", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("WARN", run.Status);
    }

    [Fact]
    public void TemplateCameraAdapterPersistsFramesToDisk()
    {
        var adapter = new global::CameraAdapterTemplate.FakeVisionCameraAdapter(new CameraSourceSettings());
        Assert.True(adapter.Connect(CameraViewType.Top, "template-device", CameraAcquisitionMode.Continuous, exposureMs: 5, gain: 1, timeoutMs: 1000));
        adapter.Start();
        Assert.True(adapter.TryGetFrame(CameraViewType.Top, 1000, out var frame));
        Assert.NotNull(frame);
        Assert.False(string.IsNullOrWhiteSpace(frame!.SourcePath));
        Assert.True(File.Exists(frame.SourcePath));
        Assert.True(frame.IsSimulated);
    }

    [Fact]
    public async Task SoakTestAcceptsInjectedReadySourceAndRefusesNotConnectedSource()
    {
        AoiDatabase.Initialize();
        var framePath = WriteTinyPng("soak-frame.png");
        var readyOptions = SoakTestService.CreateProfileOptions(
            SoakTestProfile.Smoke, Path.GetDirectoryName(framePath)!, InspectionEngineFactory.DefaultEngineKey,
            Path.Combine(_root, "soak-out"), "derisk-test", "TBOX-MAIN", "LOT-1") with
        {
            MaxIterations = 2,
            DelayBetweenInspections = TimeSpan.Zero,
            CameraSourceOverride = new ReadyCameraSource(framePath),
        };

        var readyResult = await SoakTestService.RunAsync(readyOptions, progress: null, CancellationToken.None);
        Assert.Equal("RealCamera", readyResult.SourceKind);
        Assert.True(readyResult.IsRealCameraSource);
        Assert.Equal(2, readyResult.TotalCycles);
        Assert.Equal(0, readyResult.FailedCycles);

        var notConnectedResult = await SoakTestService.RunAsync(
            readyOptions with { CameraSourceOverride = new NullCameraSource() },
            progress: null,
            CancellationToken.None);
        Assert.Equal(0, notConnectedResult.TotalCycles);
        Assert.Contains(notConnectedResult.Errors, error => error.Contains("is not ready", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TcpLightingControllerSendsCommandBytesOnSuccess()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var receiveTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            var buffer = new byte[256];
            var read = await stream.ReadAsync(buffer);
            return Encoding.ASCII.GetString(buffer, 0, read);
        });

        var settings = new LightingSettings
        {
            Mode = LightingModes.TcpText,
            TcpHost = "127.0.0.1",
            TcpPort = port,
            ResponseTimeoutMs = 2000,
        };
        var controller = new TcpTextLightingController(settings);
        var result = await controller.SetProgramAsync("Top", "PROG1");

        Assert.True(result.Accepted);
        Assert.Contains("TCP lighting command sent", result.Message, StringComparison.Ordinal);
        var received = await receiveTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("Top", received, StringComparison.Ordinal);
        Assert.Contains("PROG1", received, StringComparison.Ordinal);
        listener.Stop();
    }

    [Fact]
    public async Task TcpLightingControllerReportsRefusedConnectionAsOperatorSafeFailure()
    {
        int closedPort;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            closedPort = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
        }

        var controller = new TcpTextLightingController(new LightingSettings
        {
            Mode = LightingModes.TcpText,
            TcpHost = "127.0.0.1",
            TcpPort = closedPort,
            ResponseTimeoutMs = 2000,
        });

        var result = await controller.SetProgramAsync("Top", "PROG1");

        Assert.False(result.Accepted);
        Assert.True(
            result.Message.Contains("TCP lighting command failed", StringComparison.Ordinal) ||
            result.Message.Contains("timed out", StringComparison.Ordinal));
        Assert.DoesNotContain("   at ", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TcpLightingControllerTimesOutAgainstUnreachableHostWithinBound()
    {
        var controller = new TcpTextLightingController(new LightingSettings
        {
            Mode = LightingModes.TcpText,
            TcpHost = "10.255.255.1",
            TcpPort = 9,
            ResponseTimeoutMs = 300,
        });

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = await controller.SetProgramAsync("Top", "PROG1");
        watch.Stop();

        Assert.False(result.Accepted);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void MonitorViewDoesNotClobberCommissionedRobotRegistrations()
    {
        AoiDatabase.Initialize();
        UiNavigationSmokeTests.RunOnStaForTests(() =>
        {
            EnsureApplicationResources();
            var marker = new MarkerRobotController();
            var markerPlc = new MarkerPlcSafetyController();
            try
            {
                IntegrationBoundaryRegistry.RobotController = marker;
                IntegrationBoundaryRegistry.PlcSafetyController = markerPlc;
                var view = new MonitorView();
                Assert.Same(marker, IntegrationBoundaryRegistry.RobotController);
                Assert.Same(markerPlc, IntegrationBoundaryRegistry.PlcSafetyController);
            }
            finally
            {
                IntegrationBoundaryRegistry.RobotController = new NullRobotController();
                IntegrationBoundaryRegistry.PlcSafetyController = new NullPlcSafetyController();
                IntegrationBoundaryRegistry.EmergencyStopMonitor = new NullEmergencyStopMonitor();
            }
        });
    }

    [Fact]
    public void MonitorViewInstallsSimulatorsOverNullDefaultsIncludingPlc()
    {
        AoiDatabase.Initialize();
        UiNavigationSmokeTests.RunOnStaForTests(() =>
        {
            EnsureApplicationResources();
            try
            {
                IntegrationBoundaryRegistry.RobotController = new NullRobotController();
                IntegrationBoundaryRegistry.PlcSafetyController = new NullPlcSafetyController();
                IntegrationBoundaryRegistry.EmergencyStopMonitor = new NullEmergencyStopMonitor();
                var view = new MonitorView();
                Assert.IsType<SimulatedRobotController>(IntegrationBoundaryRegistry.RobotController);
                Assert.IsType<SimulatedPlcSafetyController>(IntegrationBoundaryRegistry.PlcSafetyController);
                Assert.IsType<SimulatedEmergencyStopMonitor>(IntegrationBoundaryRegistry.EmergencyStopMonitor);
            }
            finally
            {
                IntegrationBoundaryRegistry.RobotController = new NullRobotController();
                IntegrationBoundaryRegistry.PlcSafetyController = new NullPlcSafetyController();
                IntegrationBoundaryRegistry.EmergencyStopMonitor = new NullEmergencyStopMonitor();
            }
        });
    }

    [Fact]
    public void MesPayloadContractExportMatchesTraceabilityPayloadExactly()
    {
        AoiDatabase.Initialize();
        var export = TraceabilityAcceptanceTestService.ExportEndpointContracts(Path.Combine(_root, "mes-contract"));

        using var contract = JsonDocument.Parse(File.ReadAllText(export.PayloadContractPath));
        var contractKeys = contract.RootElement.EnumerateObject().Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal).ToArray();
        var payloadKeys = typeof(TraceabilityPayload).GetProperties()
            .Select(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name))
            .OrderBy(name => name, StringComparer.Ordinal).ToArray();

        Assert.Equal(payloadKeys, contractKeys);
    }

    [Fact]
    public async Task CentralSyncRetryReachesItemsBeyondTheNewest1000()
    {
        AoiDatabase.Initialize();
        var dropFolder = Path.Combine(_root, "filedrop");
        Directory.CreateDirectory(dropFolder);
        CentralSyncSettingsService.Save(new CentralSyncSettings
        {
            Mode = CentralSyncMode.FileDrop,
            FileDropFolder = dropFolder,
        });

        for (var i = 0; i < 1050; i++)
        {
            AoiDatabase.EnqueueCentralSyncItem(
                "AuditEvent",
                $"derisk-{i}",
                "{\"schemaVersion\":\"central-sync/v1\",\"itemType\":\"AuditEvent\"}",
                payloadPath: string.Empty,
                endpointOrFolder: dropFolder,
                stationId: "TEST-STATION",
                maxRetryCount: 5);
        }

        var oldestPending = AoiDatabase.GetPendingCentralSyncItems(1);
        var summary = await CentralSyncService.RetryEligibleAsync(limit: 5);

        Assert.Equal(5, summary.Attempted);
        Assert.True(summary.Sent >= 1);
        var oldestNow = AoiDatabase.GetCentralSyncItemsByIds(new[] { oldestPending[0].Id }).Single();
        Assert.Equal("Sent", oldestNow.Status);
    }

    [Fact]
    public void CentralSyncAuditQueueingDoesNotFeedBackOnItself()
    {
        AoiDatabase.Initialize();
        CentralSyncSettingsService.Save(new CentralSyncSettings
        {
            Mode = CentralSyncMode.FileDrop,
            FileDropFolder = Path.Combine(_root, "filedrop2"),
        });
        AoiDatabase.RecordAuditEvent("DERISK_TEST", "Seed audit event for feedback-loop regression test.");

        CentralSyncService.QueueLocalChangesForSync();
        var afterFirst = CountQueuedAuditItems();
        CentralSyncService.QueueLocalChangesForSync();
        var afterSecond = CountQueuedAuditItems();

        Assert.Equal(afterFirst, afterSecond);
    }

    [Fact]
    public void Profile3DAcceptanceToleratesSmallDropoutAndFailsLargeDropout()
    {
        AoiDatabase.Initialize();
        var smallDropout = new StubProfile3DSource(width: 10, height: 10, nanCount: 3);
        var largeDropout = new StubProfile3DSource(width: 10, height: 10, nanCount: 30);

        var tolerated = Profile3DAcceptanceTestService.Run(smallDropout);
        var failed = Profile3DAcceptanceTestService.Run(largeDropout);

        Assert.DoesNotContain(tolerated.Failures, failure => failure.Contains("NaN", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(tolerated.Warnings, warning => warning.Contains("dropout tolerance", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(failed.Failures, failure => failure.Contains("above the", StringComparison.OrdinalIgnoreCase));
    }

    private static void EnsureApplicationResources()
    {
        if (System.Windows.Application.Current is not null)
            return;

        var app = new App();
        app.InitializeComponent();
    }

    private int CountQueuedAuditItems()
        => AoiDatabase.GetCentralSyncQueue(5000).Count(item => item.ItemType == "AuditEvent");

    private string WriteTinyPng(string fileName)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, fileName);
        File.WriteAllBytes(path, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg=="));
        return path;
    }

    private sealed class ReadyCameraSource : ICameraSource
    {
        private readonly string _framePath;
        private int _sequence;

        public ReadyCameraSource(string framePath)
            => _framePath = framePath;

        public string Name => "Derisk Ready Camera";
        public CameraViewType SelectedView { get; set; } = CameraViewType.Top;
        public CameraSourceStatus ConnectionStatus => CameraSourceStatus.Ready;
        public string StatusMessage => "Ready (test double emulating accepted real hardware).";
        public bool IsAcquiring { get; private set; }

        public void StartAcquisition() => IsAcquiring = true;
        public void StopAcquisition() => IsAcquiring = false;

        public CameraFrame? GetNextFrame()
        {
            if (!IsAcquiring)
                return null;

            _sequence++;
            var now = DateTime.UtcNow;
            return new CameraFrame(
                FrameId: $"REAL-{SelectedView}-{_sequence:D4}",
                SourcePath: _framePath,
                ViewType: SelectedView,
                CapturedAt: now.ToLocalTime(),
                SourceName: Name,
                BoardModel: "TBOX-MAIN",
                LotId: "LOT-1",
                CameraId: "CAM-REAL-01",
                CapturedAtUtc: now,
                Width: 1280,
                Height: 960,
                PixelFormat: "Mono8",
                SourceKind: "TestRealHardware",
                IsSimulated: false);
        }
    }

    private sealed class MarkerRobotController : IRobotController
    {
        public string Name => "Marker Robot Controller";
        public IntegrationConnectionStatus Status => IntegrationConnectionStatus.Ready;
        public string StatusMessage => "Commissioned marker controller for registry-survival test.";

        public Task<IntegrationCommandResult> LoadAsync(LoadCommand command, CancellationToken cancellationToken = default)
            => Task.FromResult(new IntegrationCommandResult(true, IntegrationConnectionStatus.Ready, "marker"));

        public Task<IntegrationCommandResult> InspectAsync(InspectCommand command, CancellationToken cancellationToken = default)
            => Task.FromResult(new IntegrationCommandResult(true, IntegrationConnectionStatus.Ready, "marker"));

        public Task<IntegrationCommandResult> UnloadAsync(UnloadCommand command, CancellationToken cancellationToken = default)
            => Task.FromResult(new IntegrationCommandResult(true, IntegrationConnectionStatus.Ready, "marker"));

        public Task<IntegrationCommandResult> ResetAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new IntegrationCommandResult(true, IntegrationConnectionStatus.Ready, "marker"));
    }

    private sealed class MarkerPlcSafetyController : IPlcSafetyController
    {
        public string Name => "Marker PLC Safety Controller";
        public IntegrationConnectionStatus Status => IntegrationConnectionStatus.Ready;
        public string StatusMessage => "Commissioned marker PLC for registry-survival test.";
        public bool IsGuardDoorClosed => true;
        public bool IsEmergencyStopActive => false;
        public bool IsAirPressureOk => true;
        public bool IsRobotServoReady => true;
        public bool IsBoardClampReady => true;
        public bool IsLightCurtainClear => true;

        public Task<IntegrationCommandResult> ResetSafetyFaultAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new IntegrationCommandResult(true, IntegrationConnectionStatus.Ready, "marker"));

        public SafetyStatus GetSafetyStatus() => new()
        {
            IsGuardDoorClosed = true,
            IsEmergencyStopActive = false,
            IsAirPressureOk = true,
            IsRobotServoReady = true,
            IsBoardClampReady = true,
            IsLightCurtainClear = true,
            Message = "Marker PLC status.",
        };

        public SafetyStatus GetDiagnostics() => GetSafetyStatus();
    }

    private sealed class StubProfile3DSource : IProfile3DSource
    {
        private readonly int _width;
        private readonly int _height;
        private readonly int _nanCount;

        public StubProfile3DSource(int width, int height, int nanCount)
        {
            _width = width;
            _height = height;
            _nanCount = nanCount;
        }

        public string Name => "Derisk Stub 3D Source";
        public string Status => "Simulated";
        public string StatusMessage => "Stub 3D source for dropout-tolerance tests.";
        public bool IsAcquiring { get; private set; }

        public void StartAcquisition() => IsAcquiring = true;

        public void StopAcquisition() => IsAcquiring = false;

        public IReadOnlyDictionary<string, string> GetDiagnostics()
            => new Dictionary<string, string> { ["sourceKind"] = "Simulation" };

        public Profile3DFrame? GetNextHeightMap(CancellationToken cancellationToken = default)
        {
            var values = new double[_width * _height];
            for (var i = 0; i < values.Length; i++)
                values[i] = i < _nanCount ? double.NaN : 10 + (i % 7);
            return new Profile3DFrame
            {
                FrameId = "STUB-3D-001",
                Width = _width,
                Height = _height,
                HeightValues = values,
                Unit = "microns",
                XPitchMicrons = 10,
                YPitchMicrons = 10,
                ViewType = "Top",
                IsSimulated = true,
            };
        }
    }
}
