using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Xunit;

namespace AOI_Monitor.Tests;

/// <summary>
/// Regression tests for localized defect detection in the Pixel Difference Prototype Engine's
/// whole-board compare path.
///
/// Background: the engine originally judged on the whole-frame mean absolute difference. A real
/// PCB defect — a solder bridge, a missing 0402, a shifted connector — covers a small fraction of
/// the frame, so that mean was diluted by roughly the frame-area / defect-area ratio and could
/// never reach the NG threshold. Measured on the shipped Stage 1 demo dataset the engine returned
/// OK for all 40 known-defect boards (recall 0 %) even though the underlying signal separated
/// known-good from known-defect boards by more than an order of magnitude.
///
/// The decision statistic is now the worst-region mean difference, which keeps the existing
/// 0-100 threshold units meaningful while making localized defects detectable. These tests pin
/// that behaviour so it cannot silently regress.
/// </summary>
public sealed class PixelDifferenceLocalizedDefectTests : IDisposable
{
    private const int Size = 256;

    private readonly string _root;

    public PixelDifferenceLocalizedDefectTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AOI_Monitor_PixelDiffLocalized_Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        // Same isolation rationale as PixelDifferenceAlignmentTests: a recipe with enabled ROIs
        // left behind by another test would divert the engine to the ROI path.
        AoiDatabase.ConfigureStorageRoot(_root);
        RecipeService.Invalidate();
        RecipeService.ClearPreviewOverride();
    }

    public void Dispose()
    {
        RecipeService.Invalidate();
        RecipeService.ClearPreviewOverride();
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException ex)
        {
            System.Diagnostics.Trace.WriteLine($"Localized defect test cleanup skipped: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            System.Diagnostics.Trace.WriteLine($"Localized defect test cleanup skipped: {ex.Message}");
        }
    }

    [Fact]
    public void LocalizedDefectCoveringUnderTwoPercentOfTheFrameIsNotVerdictedOk()
    {
        // 32x32 blob on a 256x256 board = 1.56 % of the frame. This is the size regime a real
        // PCB defect lives in and the exact case the frame-mean statistic could not see.
        var golden = WriteBoardPng("golden.png");
        var defective = WriteBoardPng("defect.png", defect: new Int32Rect(96, 96, 32, 32));
        var engine = new PixelDifferenceInspectionEngine();

        var result = engine.Analyze(defective, golden, DetectionPriority.MaximizeDefectRecall);

        Assert.NotEqual("OK", result.Verdict);
    }

    [Fact]
    public void WholeFrameMeanAloneWouldHaveMissedTheSameDefect()
    {
        // Demonstrates *why* the decision statistic changed: the frame mean for this defect sits
        // far below even the most sensitive review threshold, while the worst-region statistic
        // clears it. Both numbers are reported so the evidence stays auditable.
        var golden = WriteBoardPng("golden-context.png");
        var defective = WriteBoardPng("defect-context.png", defect: new Int32Rect(96, 96, 32, 32));
        var engine = new PixelDifferenceInspectionEngine();

        var result = engine.Analyze(defective, golden, DetectionPriority.MaximizeDefectRecall);

        // MaximizeDefectRecall review threshold is 5 %.
        Assert.True(
            result.FrameMeanDifferenceScore < 5.0,
            $"Frame mean {result.FrameMeanDifferenceScore:F2}% was expected to be below the most sensitive review threshold.");
        Assert.True(
            result.DifferenceScore > result.FrameMeanDifferenceScore * 4,
            $"Worst-region score {result.DifferenceScore:F2}% should concentrate the localized defect well above the frame mean {result.FrameMeanDifferenceScore:F2}%.");
    }

    [Fact]
    public void IdenticalBoardStaysOkAndScoresEssentiallyZero()
    {
        var golden = WriteBoardPng("golden-clean.png");
        var sample = WriteBoardPng("sample-clean.png");
        var engine = new PixelDifferenceInspectionEngine();

        var result = engine.Analyze(sample, golden, DetectionPriority.MaximizeDefectRecall);

        Assert.Equal("OK", result.Verdict);
        Assert.True(result.DifferenceScore < 1.0, $"Identical board scored {result.DifferenceScore:F3}%.");
    }

    [Fact]
    public void SensorNoiseOnAGoodBoardDoesNotBecomeAFalseCall()
    {
        // Low-amplitude noise spread over the whole board must stay well clear of the review
        // band; concentrating on the worst region must not turn ordinary noise into a defect.
        var golden = WriteBoardPng("golden-noise.png");
        var noisy = WriteBoardPng("sample-noise.png", noiseAmplitude: 6);
        var engine = new PixelDifferenceInspectionEngine();

        var result = engine.Analyze(noisy, golden, DetectionPriority.MaximizeDefectRecall);

        Assert.Equal("OK", result.Verdict);
    }

    [Fact]
    public void HotspotPointsAtTheDefectRegion()
    {
        // The reported hotspot must be the region that drove the verdict, otherwise the operator
        // overlay sends the reviewer to the wrong part of the board.
        var golden = WriteBoardPng("golden-hotspot.png");
        var defective = WriteBoardPng("defect-hotspot.png", defect: new Int32Rect(192, 32, 32, 32));
        var engine = new PixelDifferenceInspectionEngine();

        var result = engine.Analyze(defective, golden, DetectionPriority.MaximizeDefectRecall);

        // Defect centre is at (208, 48) of 256 => 0.8125, 0.1875 normalized.
        Assert.InRange(result.Hotspot.X + (result.Hotspot.Width / 2), 0.70, 0.95);
        Assert.InRange(result.Hotspot.Y + (result.Hotspot.Height / 2), 0.05, 0.30);
    }

    [Theory]
    // Detection priority is the shipped mechanism for trading recall against false calls. A
    // clearly defective board must never be verdicted OK under any policy.
    [InlineData(DetectionPriority.MinimizeFalsePositives)]
    [InlineData(DetectionPriority.Balanced)]
    [InlineData(DetectionPriority.MaximizeDefectRecall)]
    public void GrossLocalizedDefectIsNeverVerdictedOkUnderAnyPolicy(DetectionPriority priority)
    {
        var golden = WriteBoardPng($"golden-{priority}.png");
        var defective = WriteBoardPng($"defect-{priority}.png", defect: new Int32Rect(64, 64, 64, 64));
        var engine = new PixelDifferenceInspectionEngine();

        var result = engine.Analyze(defective, golden, priority);

        Assert.NotEqual("OK", result.Verdict);
    }

    /// <summary>
    /// A 256x256 synthetic board: dark green substrate, a regular pad grid, and optionally a
    /// bright localized defect blob and/or low-amplitude deterministic noise.
    /// </summary>
    private string WriteBoardPng(string fileName, Int32Rect? defect = null, int noiseAmplitude = 0)
    {
        var pixels = new byte[Size * Size * 4];

        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                // Substrate plus pad grid, so the board has real structure to align against.
                var onPad = x % 32 is >= 8 and <= 23 && y % 32 is >= 8 and <= 23;
                byte b = onPad ? (byte)150 : (byte)24;
                byte g = onPad ? (byte)160 : (byte)90;
                byte r = onPad ? (byte)170 : (byte)20;

                if (noiseAmplitude > 0)
                {
                    // Deterministic, zero-mean-ish ripple; no RNG so runs are reproducible.
                    var ripple = ((x * 7 + y * 13) % (noiseAmplitude * 2)) - noiseAmplitude;
                    b = Clamp(b + ripple);
                    g = Clamp(g + ripple);
                    r = Clamp(r + ripple);
                }

                if (defect is { } area &&
                    x >= area.X && x < area.X + area.Width &&
                    y >= area.Y && y < area.Y + area.Height)
                {
                    // Bright solder-bridge-like blob against the dark substrate.
                    b = 60;
                    g = 240;
                    r = 250;
                }

                var index = ((y * Size) + x) * 4;
                pixels[index] = b;
                pixels[index + 1] = g;
                pixels[index + 2] = r;
                pixels[index + 3] = 255;
            }
        }

        var bitmap = BitmapSource.Create(Size, Size, 96, 96, PixelFormats.Bgra32, null, pixels, Size * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var path = Path.Combine(_root, fileName);
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    private static byte Clamp(int value)
        => (byte)Math.Clamp(value, 0, 255);
}
