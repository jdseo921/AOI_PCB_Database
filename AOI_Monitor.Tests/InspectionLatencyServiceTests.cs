using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Xunit;

namespace AOI_Monitor.Tests;

public sealed class InspectionLatencyServiceTests : IDisposable
{
    private readonly string _root;

    public InspectionLatencyServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AOI_Monitor_Latency_Tests", Guid.NewGuid().ToString("N"));
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
            // Best-effort cleanup for temp test folders.
        }
    }

    [Fact]
    public void TraceCalculatesFrameToOverlayAndSavedResultTotals()
    {
        AoiDatabase.Initialize();
        var start = DateTime.UtcNow;
        var trace = new InspectionLatencyTrace
        {
            TraceId = Guid.NewGuid().ToString("N"),
            CreatedAtUtc = start,
            FrameCapturedAtUtc = start,
            FrameReceivedAtUtc = start.AddMilliseconds(20),
            PreprocessingStartUtc = start.AddMilliseconds(100),
            PreprocessingEndUtc = start.AddMilliseconds(200),
            InferenceStartUtc = start.AddMilliseconds(200),
            InferenceEndUtc = start.AddMilliseconds(450),
            PostprocessStartUtc = start.AddMilliseconds(450),
            PostprocessEndUtc = start.AddMilliseconds(500),
            OverlayRenderStartUtc = start.AddMilliseconds(500),
            OverlayRenderEndUtc = start.AddMilliseconds(700),
            ResultPersistStartUtc = start.AddMilliseconds(720),
            ResultPersistEndUtc = start.AddMilliseconds(820),
            SourceKind = "UnitTest",
            Engine = "Unit Test Engine",
            ModelId = "MODEL",
            ImageWidth = 1280,
            ImageHeight = 720,
        };

        InspectionLatencyService.Persist(trace);
        var saved = AoiDatabase.GetInspectionLatencyTraces(1).Single();

        Assert.Equal(700, saved.TotalFrameToOverlayMs, precision: 3);
        Assert.Equal(820, saved.TotalFrameToSavedResultMs, precision: 3);
        Assert.Empty(saved.Warnings);
    }

    [Fact]
    public void OverOneSecondImageIsCounted()
    {
        var start = DateTime.UtcNow;
        var summary = InspectionLatencyService.Summarize(new[]
        {
            new InspectionLatencyTrace
            {
                FrameCapturedAtUtc = start,
                OverlayRenderEndUtc = start.AddMilliseconds(1200),
                TotalFrameToOverlayMs = 1200,
            },
            new InspectionLatencyTrace
            {
                FrameCapturedAtUtc = start,
                OverlayRenderEndUtc = start.AddMilliseconds(400),
                TotalFrameToOverlayMs = 400,
            },
        });

        Assert.Equal(2, summary.TraceCount);
        Assert.Equal(1, summary.OverOneSecondCount);
        Assert.Equal("WARN", summary.Status);
    }

    [Fact]
    public void MissingSpansDoNotCrashAndEmitWarnings()
    {
        AoiDatabase.Initialize();
        var trace = new InspectionLatencyTrace
        {
            TraceId = Guid.NewGuid().ToString("N"),
            CreatedAtUtc = DateTime.UtcNow,
            SourceKind = "UnitTest",
            Engine = "Unit Test Engine",
        };

        InspectionLatencyService.Persist(trace);
        var saved = AoiDatabase.GetInspectionLatencyTraces(1).Single();

        Assert.Contains(saved.Warnings, warning => warning.Contains("overlay", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(saved.Warnings, warning => warning.Contains("Frame captured", StringComparison.OrdinalIgnoreCase));
    }
}
