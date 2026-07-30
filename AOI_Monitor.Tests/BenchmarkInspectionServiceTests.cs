using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Xunit;

namespace AOI_Monitor.Tests;

public sealed class BenchmarkInspectionServiceTests : IDisposable
{
    private readonly string _root;

    public BenchmarkInspectionServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AOI_Monitor_Benchmark_Tests", Guid.NewGuid().ToString("N"));
        AoiDatabase.ConfigureStorageRoot(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void SyntheticImageBenchmarkRecordsTracesAndReports()
    {
        AoiDatabase.Initialize();
        var imageFolder = WriteImages(3);

        var result = BenchmarkInspectionService.Run(
            new BenchmarkInspectionOptions
            {
                SourceKind = BenchmarkInspectionSourceKind.ImageFolder,
                ImageFolder = imageFolder,
                RunCount = 3,
                OutputRoot = Path.Combine(_root, "benchmark-output"),
            },
            engineOverride: new FixedTimingEngine(10, 20, 30, 40));

        var traces = AoiDatabase.GetInspectionLatencyTraces(10);
        Assert.Equal(3, result.CompletedCount);
        Assert.Equal(3, traces.Count);
        Assert.True(File.Exists(result.JsonPath));
        Assert.True(File.Exists(result.HtmlPath));
        Assert.True(File.Exists(result.PdfPath));
        Assert.True(File.Exists(result.CsvPath));
        Assert.True(File.Exists(BenchmarkInspectionService.LatestSummaryPath));
    }

    [Fact]
    public void P95IsCalculatedCorrectly()
    {
        var p95 = BenchmarkInspectionService.Percentile(new[] { 100d, 200d, 300d, 400d, 500d }, 0.95);

        Assert.Equal(480, p95, precision: 3);
    }

    [Fact]
    public void OverOneSecondCountIsCorrect()
    {
        AoiDatabase.Initialize();
        var imageFolder = WriteImages(3);

        var result = BenchmarkInspectionService.Run(
            new BenchmarkInspectionOptions
            {
                SourceKind = BenchmarkInspectionSourceKind.ImageFolder,
                ImageFolder = imageFolder,
                RunCount = 3,
                OutputRoot = Path.Combine(_root, "benchmark-output"),
            },
            engineOverride: new SequenceTimingEngine(100, 1200, 900));

        Assert.Equal(1, result.OverOneSecondCount);
        Assert.Equal("FAIL", result.Status);
    }

    [Fact]
    public void RealCameraBenchmarkCannotBeSatisfiedByFolderSimulation()
    {
        AoiDatabase.Initialize();
        var imageFolder = WriteImages(2);
        var source = new TestCameraSource(imageFolder, simulated: true, sourceKind: "FolderCameraSimulation");

        var result = BenchmarkInspectionService.Run(
            new BenchmarkInspectionOptions
            {
                SourceKind = BenchmarkInspectionSourceKind.ActiveCameraSource,
                RunCount = 2,
                OutputRoot = Path.Combine(_root, "benchmark-output"),
            },
            engineOverride: new FixedTimingEngine(5, 5, 5, 5),
            cameraSourceOverride: source);

        Assert.False(result.IsRealCameraSource);
        Assert.Equal("SIMULATION_ONLY", result.Status);
        Assert.False(BenchmarkInspectionService.LatestSatisfiesRealCameraRequirement(1000, out var latest));
        Assert.NotNull(latest);
        Assert.Contains(result.Messages, message => message.Contains("simulated", StringComparison.OrdinalIgnoreCase));
    }

    private string WriteImages(int count)
    {
        var folder = Path.Combine(_root, "images");
        Directory.CreateDirectory(folder);
        for (var i = 0; i < count; i++)
            File.WriteAllBytes(Path.Combine(folder, $"image-{i:D2}.png"), TinyPngBytes());
        return folder;
    }

    private static byte[] TinyPngBytes()
        => Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII=");

    [Fact]
    public void WarmupSamplesAreExcludedFromStatsAndReportedAsColdStart()
    {
        AoiDatabase.Initialize();
        var imageFolder = WriteImages(3);

        var result = BenchmarkInspectionService.Run(
            new BenchmarkInspectionOptions
            {
                SourceKind = BenchmarkInspectionSourceKind.ImageFolder,
                ImageFolder = imageFolder,
                RunCount = 3,
                WarmupCount = 1,
                OutputRoot = Path.Combine(_root, "benchmark-warmup"),
            },
            engineOverride: new SequenceTimingEngine(1200, 100, 110, 120));

        Assert.Equal("PASS", result.Status);
        Assert.Equal(3, result.CompletedCount);
        Assert.Equal(1, result.ColdStartSampleCount);
        Assert.Equal(1, result.ColdStartOverThresholdCount);
        Assert.True(result.ColdStartMaxFrameToOverlayMs >= 1200);
        Assert.Equal(0, result.OverOneSecondCount);
        Assert.True(result.P95FrameToOverlayMs < 1000);
        Assert.True(result.Samples[0].IsWarmup);
        Assert.All(result.Samples.Skip(1), sample => Assert.False(sample.IsWarmup));
        Assert.Equal(result.Samples.Count, result.Samples.Select(sample => sample.Sequence).Distinct().Count());
        Assert.Contains(result.Messages, message => message.Contains("cold-start", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GoldenPathIsForwardedAndConfigurationIsRecorded()
    {
        AoiDatabase.Initialize();
        var imageFolder = WriteImages(2);
        var goldenPath = Path.Combine(imageFolder, "image-00.png");
        var capture = new GoldenCapturingEngine();

        var result = BenchmarkInspectionService.Run(
            new BenchmarkInspectionOptions
            {
                SourceKind = BenchmarkInspectionSourceKind.ImageFolder,
                ImageFolder = imageFolder,
                GoldenImagePath = goldenPath,
                RunCount = 2,
                OutputRoot = Path.Combine(_root, "benchmark-golden"),
            },
            engineOverride: capture);

        Assert.All(capture.GoldenPaths, path => Assert.Equal(goldenPath, path));
        Assert.Equal(goldenPath, result.GoldenImagePath);
        Assert.Contains("CPU", result.ExecutionProvider, StringComparison.Ordinal);
        Assert.Equal("Balanced", result.DetectionPriority);
        Assert.All(result.Samples, sample =>
        {
            Assert.True(sample.ImageWidth > 0);
            Assert.True(sample.ImageHeight > 0);
        });
        var csvHeader = File.ReadLines(result.CsvPath).First();
        Assert.Contains("IsWarmup", csvHeader, StringComparison.Ordinal);
        Assert.Contains("ImageWidth", csvHeader, StringComparison.Ordinal);
        var html = File.ReadAllText(result.HtmlPath);
        Assert.Contains("Execution provider", html, StringComparison.Ordinal);
        Assert.Contains("Cold-start samples", html, StringComparison.Ordinal);
    }

    private sealed class GoldenCapturingEngine : IInspectionEngine
    {
        public List<string?> GoldenPaths { get; } = new();

        public string Name => "Golden Capturing Engine";
        public string Version => "TEST-GOLD";

        public AnalysisResult Analyze(string samplePath, string? goldenPath, DetectionPriority priority)
        {
            GoldenPaths.Add(goldenPath);
            return Result(samplePath, 5, 5, 5, 5);
        }
    }

    private sealed class FixedTimingEngine : IInspectionEngine
    {
        private readonly double _load;
        private readonly double _preprocess;
        private readonly double _inference;
        private readonly double _overlay;

        public FixedTimingEngine(double load, double preprocess, double inference, double overlay)
        {
            _load = load;
            _preprocess = preprocess;
            _inference = inference;
            _overlay = overlay;
        }

        public string Name => "Fixed Timing Engine";
        public string Version => "TEST-1";

        public AnalysisResult Analyze(string samplePath, string? goldenPath, DetectionPriority priority)
            => Result(samplePath, _load, _preprocess, _inference, _overlay);
    }

    private sealed class SequenceTimingEngine : IInspectionEngine
    {
        private readonly Queue<double> _totals;

        public SequenceTimingEngine(params double[] totals)
        {
            _totals = new Queue<double>(totals);
        }

        public string Name => "Sequence Timing Engine";
        public string Version => "TEST-SEQ";

        public AnalysisResult Analyze(string samplePath, string? goldenPath, DetectionPriority priority)
        {
            var total = _totals.Count == 0 ? 100 : _totals.Dequeue();
            return Result(samplePath, 0, 0, total, 0);
        }
    }

    private sealed class TestCameraSource : ICameraSource
    {
        private readonly string[] _images;
        private readonly bool _simulated;
        private readonly string _sourceKind;
        private int _index = -1;

        public TestCameraSource(string imageFolder, bool simulated, string sourceKind)
        {
            _images = Directory.EnumerateFiles(imageFolder, "*.png").OrderBy(path => path).ToArray();
            _simulated = simulated;
            _sourceKind = sourceKind;
        }

        public string Name => "Test Camera";
        public CameraViewType SelectedView { get; set; } = CameraViewType.Top;
        public CameraSourceStatus ConnectionStatus => CameraSourceStatus.Ready;
        public string StatusMessage => "Ready";
        public bool IsAcquiring { get; private set; }

        public void StartAcquisition() => IsAcquiring = true;
        public void StopAcquisition() => IsAcquiring = false;

        public CameraFrame? GetNextFrame()
        {
            if (!IsAcquiring || _images.Length == 0)
                return null;

            _index = (_index + 1) % _images.Length;
            var capturedAtUtc = DateTime.UtcNow;
            return new CameraFrame(
                $"FRAME-{_index:D3}",
                _images[_index],
                SelectedView,
                capturedAtUtc.ToLocalTime(),
                Name,
                "BOARD",
                "LOT",
                CameraId: "CAM-TEST",
                CapturedAtUtc: capturedAtUtc,
                Width: 640,
                Height: 480,
                PixelFormat: "Mono8",
                SourceKind: _sourceKind,
                IsSimulated: _simulated);
        }
    }

    private static AnalysisResult Result(string samplePath, double load, double preprocess, double inference, double overlay)
    {
        var timing = new InspectionTiming
        {
            ImageLoadMilliseconds = load,
            PreprocessingMilliseconds = preprocess,
            InferenceMilliseconds = inference,
            OverlayRenderingMilliseconds = overlay,
        };
        timing.RecalculateTotal();
        return new AnalysisResult
        {
            SamplePath = samplePath,
            Verdict = "OK",
            InspectionEngine = "Benchmark Test Engine",
            ModelVersion = "TEST",
            Timing = timing,
        };
    }
}
