using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using AOI_Monitor.Tools;
using Xunit;

namespace AOI_Monitor.Tests;

public sealed class BatchSoakTestServiceTests : IDisposable
{
    private readonly string _root;

    public BatchSoakTestServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AOI_Monitor_BatchSoak_Tests", Guid.NewGuid().ToString("N"));
        AoiDatabase.ConfigureStorageRoot(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.WriteLine($"Test cleanup failed for {nameof(BatchSoakTestServiceTests)}: {ex.Message}");
        }
    }

    [Fact]
    public async Task SmokeRunPassesCapturesPerPassMetricsAndWritesVerifiedReports()
    {
        AoiDatabase.Initialize();
        var imageFolder = WriteImages(3);
        var outputFolder = Path.Combine(_root, "soak-output");

        var result = await BatchSoakTestService.RunAsync(
            new BatchSoakOptions
            {
                ImageFolder = imageFolder,
                OutputFolder = outputFolder,
                OperatorId = "test-soak",
                Duration = TimeSpan.FromMinutes(5),
                DelayBetweenPasses = TimeSpan.Zero,
                MaxPasses = 2,
            },
            progress: null,
            CancellationToken.None);
        var reports = BatchSoakReportService.WriteReports(result, outputFolder);

        Assert.Equal("PASS", result.Status);
        Assert.Empty(result.FailReasons);
        Assert.Equal(2, result.TotalPasses);
        Assert.Equal(6, result.TotalImagesProcessed);
        Assert.Equal(0, result.TotalErrorCount);
        Assert.Equal(3, result.DatasetImageCountAtStart);
        Assert.False(string.IsNullOrWhiteSpace(result.DatasetFingerprintSha256));
        Assert.Equal("Balanced", result.EngineConfig.DetectionPriority);
        Assert.All(result.Passes, pass =>
        {
            Assert.True(pass.ManagedMemoryMegabytes > 0);
            Assert.True(pass.WorkingSetMegabytes > 0);
            Assert.True(pass.HandleCount > 0);
            Assert.True(pass.DatabaseSizeMegabytes > 0);
            Assert.NotNull(pass.BatchRunId);
        });

        using var json = JsonDocument.Parse(File.ReadAllText(reports.JsonPath));
        Assert.Equal(result.RunId, json.RootElement.GetProperty("runId").GetString());
        Assert.Equal("PASS", json.RootElement.GetProperty("status").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("softwareVersion").GetString()));
        Assert.Contains("not live", json.RootElement.GetProperty("scopeStatement").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.False(json.RootElement.GetProperty("isEightHourUploadedImagePoCEvidence").GetBoolean());

        var csvLines = File.ReadAllLines(reports.PassesCsvPath);
        Assert.StartsWith("# Stage 1 uploaded-image", csvLines[0], StringComparison.Ordinal);
        Assert.Contains(result.RunId, csvLines[1], StringComparison.Ordinal);
        Assert.StartsWith("run_id,", csvLines[2], StringComparison.Ordinal);
        Assert.Equal(5, csvLines.Length);

        var verification = AoiDatabase.GetExportVerifications(10)
            .FirstOrDefault(record => record.ExportType == "Stage1BatchSoak");
        Assert.NotNull(verification);
        Assert.Contains("OK", verification!.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void MemoryTrendEvaluationFlagsSustainedGrowthAndAcceptsFlatProfile()
    {
        var growing = Enumerable.Range(0, 16)
            .Select(index => new BatchSoakMemoryTrendSample(index * 0.5, 200 + (index * 40)))
            .ToArray();
        var flat = Enumerable.Range(0, 16)
            .Select(index => new BatchSoakMemoryTrendSample(index * 0.5, 200 + ((index % 2) * 3)))
            .ToArray();

        var growingTrend = BatchSoakTestService.EvaluateMemoryTrend(growing, slopeFailMegabytesPerHour: 64, growthFloorMegabytes: 256);
        var flatTrend = BatchSoakTestService.EvaluateMemoryTrend(flat, slopeFailMegabytesPerHour: 64, growthFloorMegabytes: 256);
        var tooFew = BatchSoakTestService.EvaluateMemoryTrend(growing.Take(4).ToArray(), 64, 256);

        Assert.True(growingTrend.Exceeded);
        Assert.True(growingTrend.SlopeMegabytesPerHour > 64);
        Assert.False(flatTrend.Exceeded);
        Assert.False(tooFew.Exceeded);
        Assert.Contains("Insufficient samples", tooFew.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MemoryTrendFailureFiresThroughRealRunWiring()
    {
        AoiDatabase.Initialize();
        var imageFolder = WriteImages(1);

        var result = await BatchSoakTestService.RunAsync(
            new BatchSoakOptions
            {
                ImageFolder = imageFolder,
                OutputFolder = Path.Combine(_root, "trend-output"),
                OperatorId = "test-soak",
                Duration = TimeSpan.FromMinutes(5),
                DelayBetweenPasses = TimeSpan.Zero,
                MaxPasses = 12,
                MemorySlopeFailMegabytesPerHour = -1000,
                MemoryGrowthFailFloorMegabytes = -1000,
                MemoryTrendWarmup = TimeSpan.Zero,
            },
            progress: null,
            CancellationToken.None);

        Assert.Equal("FAIL", result.Status);
        Assert.Contains(BatchSoakTestService.FailReasonMemoryGrowthTrend, result.FailReasons);
        Assert.True(result.MemoryTrend.Exceeded);
        Assert.Equal(8, result.TotalPasses);
        Assert.Contains(result.Errors, message => message.Contains("managed-memory growth trend", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ShortRunTrendStaysInformationalWhenWarmupGateNotReached()
    {
        AoiDatabase.Initialize();
        var imageFolder = WriteImages(1);

        var result = await BatchSoakTestService.RunAsync(
            new BatchSoakOptions
            {
                ImageFolder = imageFolder,
                OutputFolder = Path.Combine(_root, "trend-warmup-output"),
                OperatorId = "test-soak",
                Duration = TimeSpan.FromHours(8),
                DelayBetweenPasses = TimeSpan.Zero,
                MaxPasses = 12,
                MemorySlopeFailMegabytesPerHour = -1000,
                MemoryGrowthFailFloorMegabytes = -1000,
            },
            progress: null,
            CancellationToken.None);

        Assert.Equal("PASS", result.Status);
        Assert.DoesNotContain(BatchSoakTestService.FailReasonMemoryGrowthTrend, result.FailReasons);
        Assert.False(result.MemoryTrend.Exceeded);
        Assert.Contains("Informational only", result.MemoryTrend.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StuckInspectionFailsRunWithStuckIterationReason()
    {
        AoiDatabase.Initialize();
        var imageFolder = WriteImages(1);
        using var gate = new ManualResetEventSlim(initialState: false);
        using var released = new ManualResetEventSlim(initialState: false);
        var engine = new BlockingEngine(gate, entered: null, released);

        try
        {
            var result = await BatchSoakTestService.RunAsync(
                new BatchSoakOptions
                {
                    ImageFolder = imageFolder,
                    OutputFolder = Path.Combine(_root, "stuck-output"),
                    OperatorId = "test-soak",
                    Duration = TimeSpan.FromMinutes(5),
                    DelayBetweenPasses = TimeSpan.Zero,
                    MaxPasses = 1,
                    StuckImageTimeout = TimeSpan.FromMilliseconds(200),
                },
                progress: null,
                CancellationToken.None,
                engineOverride: engine);

            Assert.Equal("FAIL", result.Status);
            Assert.Contains(BatchSoakTestService.FailReasonStuckIteration, result.FailReasons);
            Assert.Contains(result.Errors, message => message.Contains("stuck-iteration timeout", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            gate.Set();
            released.Wait(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task MidInspectionCancellationIsCanceledNotStuck()
    {
        AoiDatabase.Initialize();
        var imageFolder = WriteImages(1);
        using var gate = new ManualResetEventSlim(initialState: false);
        using var entered = new ManualResetEventSlim(initialState: false);
        using var released = new ManualResetEventSlim(initialState: false);
        using var cancellation = new CancellationTokenSource();
        var engine = new BlockingEngine(gate, entered, released);

        try
        {
            var runTask = BatchSoakTestService.RunAsync(
                new BatchSoakOptions
                {
                    ImageFolder = imageFolder,
                    OutputFolder = Path.Combine(_root, "cancel-output"),
                    OperatorId = "test-soak",
                    Duration = TimeSpan.FromMinutes(5),
                    DelayBetweenPasses = TimeSpan.Zero,
                    StuckImageTimeout = TimeSpan.FromSeconds(30),
                },
                progress: null,
                cancellation.Token,
                engineOverride: engine);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            cancellation.Cancel();
            var result = await runTask;

            Assert.Equal("CANCELED", result.Status);
            Assert.True(result.WasCanceled);
            Assert.Empty(result.FailReasons);
            Assert.DoesNotContain(BatchSoakTestService.FailReasonStuckIteration, result.FailReasons);
            Assert.Contains("abandoned", result.CancellationReason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            gate.Set();
            released.Wait(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task UnhandledEngineExceptionFailsRunWithOperatorSafeMessageAndDebugFile()
    {
        AoiDatabase.Initialize();
        var imageFolder = WriteImages(2);
        var outputFolder = Path.Combine(_root, "unhandled-output");

        var result = await BatchSoakTestService.RunAsync(
            new BatchSoakOptions
            {
                ImageFolder = imageFolder,
                OutputFolder = outputFolder,
                OperatorId = "test-soak",
                Duration = TimeSpan.FromMinutes(5),
                DelayBetweenPasses = TimeSpan.Zero,
                MaxPasses = 1,
            },
            progress: null,
            CancellationToken.None,
            engineOverride: new ThrowingEngine());
        var reports = BatchSoakReportService.WriteReports(result, outputFolder);

        Assert.Equal("FAIL", result.Status);
        Assert.Contains(BatchSoakTestService.FailReasonUnhandledException, result.FailReasons);
        var unhandledMessage = Assert.Single(result.Errors, message => message.Contains("InvalidCastException", StringComparison.Ordinal));
        Assert.DoesNotContain("   at ", unhandledMessage, StringComparison.Ordinal);
        Assert.Contains(BatchSoakTestService.EngineerDebugFileName, unhandledMessage, StringComparison.Ordinal);
        var debugPath = Path.Combine(outputFolder, BatchSoakTestService.EngineerDebugFileName);
        Assert.True(File.Exists(debugPath));
        Assert.Contains("Synthetic unhandled failure", File.ReadAllText(debugPath), StringComparison.Ordinal);
        Assert.True(File.Exists(reports.HtmlPath));
        Assert.True(File.Exists(reports.JsonPath));
        var exportHistory = AoiDatabase.GetExportHistory(10)
            .FirstOrDefault(record => record.ExportType == "Stage1BatchSoak");
        Assert.NotNull(exportHistory);
        Assert.Contains("WARN", exportHistory!.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryImageFailingWithHandledExceptionsFailsRunWithoutUnhandledReason()
    {
        AoiDatabase.Initialize();
        var imageFolder = WriteImages(2);

        var result = await BatchSoakTestService.RunAsync(
            new BatchSoakOptions
            {
                ImageFolder = imageFolder,
                OutputFolder = Path.Combine(_root, "everyfail-output"),
                OperatorId = "test-soak",
                Duration = TimeSpan.FromMinutes(5),
                DelayBetweenPasses = TimeSpan.Zero,
                MaxPasses = 3,
            },
            progress: null,
            CancellationToken.None,
            engineOverride: new FailingEngine());

        Assert.Equal("FAIL", result.Status);
        Assert.Contains(BatchSoakTestService.FailReasonEveryImageFailed, result.FailReasons);
        Assert.DoesNotContain(BatchSoakTestService.FailReasonUnhandledException, result.FailReasons);
        Assert.Equal(1, result.TotalPasses);
        Assert.Equal(result.TotalImagesProcessed, result.TotalErrorCount);
    }

    [Fact]
    public async Task PreCanceledRunReportsCanceledWithoutFailureReasons()
    {
        AoiDatabase.Initialize();
        var imageFolder = WriteImages(1);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        var result = await BatchSoakTestService.RunAsync(
            new BatchSoakOptions
            {
                ImageFolder = imageFolder,
                OutputFolder = Path.Combine(_root, "canceled-output"),
                OperatorId = "test-soak",
                Duration = TimeSpan.FromMinutes(5),
                DelayBetweenPasses = TimeSpan.Zero,
            },
            progress: null,
            canceled.Token);

        Assert.Equal("CANCELED", result.Status);
        Assert.True(result.WasCanceled);
        Assert.Empty(result.FailReasons);
        Assert.False(result.IsEightHourUploadedImagePoCEvidence);
    }

    [Fact]
    public async Task EightHourEvidenceRequiresActualDurationNotJustRequested()
    {
        AoiDatabase.Initialize();
        var imageFolder = WriteImages(1);

        var result = await BatchSoakTestService.RunAsync(
            new BatchSoakOptions
            {
                ImageFolder = imageFolder,
                OutputFolder = Path.Combine(_root, "eighthour-output"),
                OperatorId = "test-soak",
                Duration = TimeSpan.FromHours(8),
                DelayBetweenPasses = TimeSpan.Zero,
                MaxPasses = 1,
            },
            progress: null,
            CancellationToken.None);

        Assert.Equal("PASS", result.Status);
        Assert.True(result.RequestedDuration >= TimeSpan.FromHours(8));
        Assert.True(result.ActualDuration < TimeSpan.FromHours(8));
        Assert.False(result.IsEightHourUploadedImagePoCEvidence);
    }

    [Fact]
    public async Task DisablingPersistenceLeavesNoBatchRunIds()
    {
        AoiDatabase.Initialize();
        var imageFolder = WriteImages(2);

        var result = await BatchSoakTestService.RunAsync(
            new BatchSoakOptions
            {
                ImageFolder = imageFolder,
                OutputFolder = Path.Combine(_root, "nopersist-output"),
                OperatorId = "test-soak",
                Duration = TimeSpan.FromMinutes(5),
                DelayBetweenPasses = TimeSpan.Zero,
                MaxPasses = 2,
                PersistBatchRuns = false,
            },
            progress: null,
            CancellationToken.None);

        Assert.Equal("PASS", result.Status);
        Assert.False(result.BatchRunsPersisted);
        Assert.All(result.Passes, pass => Assert.Null(pass.BatchRunId));
    }

    [Fact]
    public async Task ManifestPathIsValidatedAndRecorded()
    {
        AoiDatabase.Initialize();
        var imageFolder = WriteImages(2);

        await Assert.ThrowsAsync<ArgumentException>(() => BatchSoakTestService.RunAsync(
            new BatchSoakOptions
            {
                ImageFolder = imageFolder,
                OutputFolder = Path.Combine(_root, "manifest-output"),
                OperatorId = "test-soak",
                ManifestPath = Path.Combine(_root, "missing_manifest.csv"),
            },
            progress: null,
            CancellationToken.None));

        var manifestPath = Path.Combine(_root, "manifest.csv");
        File.WriteAllLines(manifestPath, new[]
        {
            "image,label",
            "board_000.png,OK",
            "board_001.png,NG",
        });
        var result = await BatchSoakTestService.RunAsync(
            new BatchSoakOptions
            {
                ImageFolder = imageFolder,
                OutputFolder = Path.Combine(_root, "manifest-output"),
                OperatorId = "test-soak",
                ManifestPath = manifestPath,
                Duration = TimeSpan.FromMinutes(5),
                DelayBetweenPasses = TimeSpan.Zero,
                MaxPasses = 1,
            },
            progress: null,
            CancellationToken.None);

        Assert.Equal("PASS", result.Status);
        Assert.Equal(Path.GetFullPath(manifestPath), result.ManifestPath);
    }

    [Fact]
    public async Task ReportsCarryTruthfulUploadedImageScopeAndNoProductionReadyClaim()
    {
        AoiDatabase.Initialize();
        var imageFolder = WriteImages(1);

        var result = await BatchSoakTestService.RunAsync(
            new BatchSoakOptions
            {
                ImageFolder = imageFolder,
                OutputFolder = Path.Combine(_root, "scope-output"),
                OperatorId = "test-soak",
                Duration = TimeSpan.FromMinutes(5),
                DelayBetweenPasses = TimeSpan.Zero,
                MaxPasses = 1,
            },
            progress: null,
            CancellationToken.None);
        var html = BatchSoakReportService.BuildHtmlReport(result);
        var csv = BatchSoakReportService.BuildPassesCsv(result);

        Assert.Contains("uploaded-image batch-inspection soak evidence", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not live camera acquisition", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not satisfy Stage 2-4 hardware readiness gates", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("production ready", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("production-ready", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("uploaded-image batch-inspection soak evidence", csv, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.IsEightHourUploadedImagePoCEvidence);
    }

    [Fact]
    public void CrashMarkerWriteCreatesRecoverableEvidence()
    {
        var runFolder = Path.Combine(_root, "crash-output");

        var path = BatchSoakTestService.WriteCrashMarker(runFolder, "Synthetic crash detail for coverage.");

        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        var content = File.ReadAllText(path!);
        Assert.Contains("Batch soak run crashed at", content, StringComparison.Ordinal);
        Assert.Contains("Synthetic crash detail for coverage.", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CommandRejectsBadArgumentsWithUsageExitCode()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var imageFolder = WriteImages(1);

        Assert.Equal(2, await BatchSoakCommand.ExecuteAsync(
            new[] { "batch-soak", "--images", imageFolder },
            output, error));
        Assert.Contains("--output", error.ToString(), StringComparison.Ordinal);

        Assert.Equal(2, await BatchSoakCommand.ExecuteAsync(
            new[] { "batch-soak", "--images", imageFolder, "--output", Path.Combine(_root, "o"), "--operator", "x", "--bogus-option", "1" },
            output, error));
        Assert.Contains("Unknown option: --bogus-option", error.ToString(), StringComparison.Ordinal);

        Assert.Equal(2, await BatchSoakCommand.ExecuteAsync(
            new[] { "batch-soak", "--images", imageFolder, "--output", Path.Combine(_root, "o"), "--operator", "x", "--engine", "typo" },
            output, error));
        Assert.Contains("Unknown --engine value", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CommandReturnsOneForFailingRunAndZeroForPassingRun()
    {
        AoiDatabase.Initialize();
        using var output = new StringWriter();
        using var error = new StringWriter();

        var emptyFolder = Path.Combine(_root, "empty-images");
        Directory.CreateDirectory(emptyFolder);
        var failExit = await BatchSoakCommand.ExecuteAsync(
            new[]
            {
                "batch-soak",
                "--images", emptyFolder,
                "--output", Path.Combine(_root, "cli-fail-output"),
                "--operator", "cli-soak",
                "--max-passes", "1",
                "--delay-seconds", "0",
            },
            output, error);
        Assert.Equal(1, failExit);
        Assert.Contains("FAIL Batch soak run", output.ToString(), StringComparison.Ordinal);
        Assert.Contains(BatchSoakTestService.FailReasonNoImagesFound, output.ToString(), StringComparison.Ordinal);

        var imageFolder = WriteImages(2);
        var passOutputFolder = Path.Combine(_root, "cli-pass-output");
        var passExit = await BatchSoakCommand.ExecuteAsync(
            new[]
            {
                "batch-soak",
                "--images", imageFolder,
                "--output", passOutputFolder,
                "--operator", "cli-soak",
                "--max-passes", "1",
                "--delay-seconds", "0",
                "--no-persist-batch-runs",
            },
            output, error);

        Assert.Equal(0, passExit);
        var text = output.ToString();
        Assert.Contains("PASS Batch soak run", text, StringComparison.Ordinal);
        Assert.Contains("uploaded-image batch-inspection soak evidence", text, StringComparison.OrdinalIgnoreCase);
        var runFolder = Directory.GetDirectories(passOutputFolder).Single();
        Assert.Single(Directory.GetFiles(runFolder, "batch_soak_report_*.html"));
        var jsonPath = Directory.GetFiles(runFolder, "batch_soak_report_*.json").Single();
        Assert.Single(Directory.GetFiles(runFolder, "batch_soak_passes_*.csv"));
        using var json = JsonDocument.Parse(File.ReadAllText(jsonPath));
        Assert.False(json.RootElement.GetProperty("batchRunsPersisted").GetBoolean());
    }

    private string WriteImages(int count)
    {
        var folder = Path.Combine(_root, "images", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        for (var i = 0; i < count; i++)
            WritePng(Path.Combine(folder, $"board_{i:D3}.png"), 60 + (i * 10));
        return folder;
    }

    private static void WritePng(string path, int value)
    {
        const int width = 8;
        const int height = 8;
        var pixels = new byte[width * height * 4];
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = (byte)Math.Clamp(value, 0, 255);
            pixels[offset + 1] = (byte)Math.Clamp(value + 8, 0, 255);
            pixels[offset + 2] = (byte)Math.Clamp(value + 16, 0, 255);
            pixels[offset + 3] = 255;
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private sealed class BlockingEngine : IInspectionEngine
    {
        private readonly ManualResetEventSlim _gate;
        private readonly ManualResetEventSlim? _entered;
        private readonly ManualResetEventSlim _released;

        public BlockingEngine(ManualResetEventSlim gate, ManualResetEventSlim? entered, ManualResetEventSlim released)
        {
            _gate = gate;
            _entered = entered;
            _released = released;
        }

        public string Name => "Blocking Test Engine";
        public string Version => "TEST";

        public AnalysisResult Analyze(string samplePath, string? goldenPath, DetectionPriority priority)
        {
            _entered?.Set();
            _gate.Wait();
            _released.Set();
            return new AnalysisResult { SamplePath = samplePath, Verdict = "OK" };
        }
    }

    private sealed class ThrowingEngine : IInspectionEngine
    {
        public string Name => "Throwing Test Engine";
        public string Version => "TEST";

        public AnalysisResult Analyze(string samplePath, string? goldenPath, DetectionPriority priority)
            => throw new InvalidCastException("Synthetic unhandled failure for soak coverage.");
    }

    private sealed class FailingEngine : IInspectionEngine
    {
        public string Name => "Failing Test Engine";
        public string Version => "TEST";

        public AnalysisResult Analyze(string samplePath, string? goldenPath, DetectionPriority priority)
            => throw new InvalidOperationException("Synthetic handled failure for soak coverage.");
    }
}
