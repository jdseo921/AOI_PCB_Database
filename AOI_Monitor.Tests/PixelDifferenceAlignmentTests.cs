using System.Windows.Media;
using System.Windows.Media.Imaging;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Xunit;

namespace AOI_Monitor.Tests;

/// <summary>
/// Regression tests for the translation alignment added to the Pixel Difference Prototype
/// Engine's whole-board compare: a small camera shift on a good board must not raise the
/// difference score, while a genuine gross defect must never be "aligned away".
/// </summary>
public sealed class PixelDifferenceAlignmentTests : IDisposable
{
    private readonly string _root;

    public PixelDifferenceAlignmentTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AOI_Monitor_PixelDiffAlignment_Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            System.Diagnostics.Trace.WriteLine("Pixel-diff alignment test cleanup skipped; temp folder still in use.");
        }
        catch (UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.WriteLine("Pixel-diff alignment test cleanup skipped; temp folder not accessible.");
        }
    }

    [Fact]
    public void ShiftedGoodBoardScoresLowAfterAlignmentRecovery()
    {
        var golden = WritePatternPng("golden.png", offsetX: 0, offsetY: 0);
        var shifted = WritePatternPng("sample-shifted.png", offsetX: 2, offsetY: 1);
        var engine = new PixelDifferenceInspectionEngine();

        var result = engine.Analyze(shifted, golden, DetectionPriority.Balanced);

        // Without alignment the 2px shift flips roughly half the checkerboard cells
        // (score far above the 8% Balanced review threshold); with recovery the overlap
        // region is identical.
        Assert.Equal("OK", result.Verdict);
    }

    [Fact]
    public void GrossDefectIsNotAlignedAway()
    {
        var golden = WritePatternPng("golden-defect.png", offsetX: 0, offsetY: 0);
        var defective = WritePatternPng("sample-defect.png", offsetX: 0, offsetY: 0, blackTopHalf: true);
        var engine = new PixelDifferenceInspectionEngine();

        var result = engine.Analyze(defective, golden, DetectionPriority.Balanced);

        Assert.NotEqual("OK", result.Verdict);
    }

    [Fact]
    public void ShiftedComparisonIsDeterministic()
    {
        var golden = WritePatternPng("golden-det.png", offsetX: 0, offsetY: 0);
        var shifted = WritePatternPng("sample-det.png", offsetX: -2, offsetY: 2);
        var engine = new PixelDifferenceInspectionEngine();

        var first = engine.Analyze(shifted, golden, DetectionPriority.Balanced);
        var second = engine.Analyze(shifted, golden, DetectionPriority.Balanced);

        Assert.Equal(first.Confidence, second.Confidence);
        Assert.Equal(first.Verdict, second.Verdict);
    }

    /// <summary>
    /// 64x64 checkerboard (4px cells) plus three asymmetric solid blocks that break the
    /// checkerboard's 8px periodicity so the translation search has a unique optimum.
    /// Shifting moves the pattern content; vacated border pixels take the base value and
    /// fall outside the aligned overlap, so they are never compared.
    /// </summary>
    private string WritePatternPng(string fileName, int offsetX, int offsetY, bool blackTopHalf = false)
    {
        const int size = 64;
        var pixels = new byte[size * size * 4];

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var px = x - offsetX;
                var py = y - offsetY;
                byte value;
                if (px < 0 || py < 0 || px >= size || py >= size)
                {
                    value = 40;
                }
                else
                {
                    value = (byte)((px / 4 + py / 4) % 2 == 0 ? 40 : 180);
                    if (px is >= 6 and <= 13 && py is >= 40 and <= 47) value = 230;
                    if (px is >= 44 and <= 55 && py is >= 8 and <= 13) value = 90;
                    if (px is >= 30 and <= 35 && py is >= 30 and <= 39) value = 140;
                }

                if (blackTopHalf && y < size / 2)
                    value = 0;

                var index = (y * size + x) * 4;
                pixels[index] = value;
                pixels[index + 1] = value;
                pixels[index + 2] = value;
                pixels[index + 3] = 255;
            }
        }

        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var path = Path.Combine(_root, fileName);
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }
}
