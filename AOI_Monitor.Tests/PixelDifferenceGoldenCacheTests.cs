using System.Windows.Media;
using System.Windows.Media.Imaging;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Xunit;

namespace AOI_Monitor.Tests;

public sealed class PixelDifferenceGoldenCacheTests : IDisposable
{
    private readonly string _root;

    public PixelDifferenceGoldenCacheTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AOI_Monitor_GoldenCache_Tests", Guid.NewGuid().ToString("N"));
        AoiDatabase.ConfigureStorageRoot(_root);
        PixelDifferenceGoldenCache.ClearForTests();
    }

    public void Dispose()
    {
        PixelDifferenceGoldenCache.ClearForTests();
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.WriteLine($"Test cleanup failed for {nameof(PixelDifferenceGoldenCacheTests)}: {ex.Message}");
        }
    }

    [Fact]
    public void CachedGoldenProducesBitIdenticalScoresVerdictsAndHotspots()
    {
        AoiDatabase.Initialize();
        var goldenPath = WritePng("golden.png", 32, 32, baseValue: 100);
        var samplePath = WritePng("sample.png", 32, 32, baseValue: 100, blobValue: 240);

        var first = new PixelDifferenceInspectionEngine().Analyze(samplePath, goldenPath, DetectionPriority.Balanced);
        Assert.Equal(1, PixelDifferenceGoldenCache.Misses);
        var second = new PixelDifferenceInspectionEngine().Analyze(samplePath, goldenPath, DetectionPriority.Balanced);

        Assert.True(PixelDifferenceGoldenCache.Hits >= 1);
        Assert.Equal(first.DifferenceScore, second.DifferenceScore);
        Assert.Equal(first.Verdict, second.Verdict);
        Assert.Equal(first.Hotspot, second.Hotspot);
        Assert.Equal(first.MeanBrightness, second.MeanBrightness);
    }

    [Fact]
    public void OverwritingTheGoldenFileInvalidatesTheCacheEntry()
    {
        AoiDatabase.Initialize();
        var goldenPath = WritePng("golden.png", 32, 32, baseValue: 100);
        var samplePath = WritePng("sample.png", 32, 32, baseValue: 100);

        var beforeOverwrite = new PixelDifferenceInspectionEngine().Analyze(samplePath, goldenPath, DetectionPriority.Balanced);

        WritePng("golden.png", 32, 32, baseValue: 230);
        File.SetLastWriteTimeUtc(goldenPath, DateTime.UtcNow.AddMinutes(5));
        var afterOverwrite = new PixelDifferenceInspectionEngine().Analyze(samplePath, goldenPath, DetectionPriority.Balanced);

        Assert.Equal(2, PixelDifferenceGoldenCache.Misses);
        Assert.True(afterOverwrite.DifferenceScore > beforeOverwrite.DifferenceScore);
    }

    [Fact]
    public void MismatchedDimensionsUseTheCroppedGoldenConsistently()
    {
        AoiDatabase.Initialize();
        var goldenPath = WritePng("golden-large.png", 48, 40, baseValue: 90);
        var samplePath = WritePng("sample-small.png", 32, 32, baseValue: 90, blobValue: 220);

        var first = new PixelDifferenceInspectionEngine().Analyze(samplePath, goldenPath, DetectionPriority.Balanced);
        var second = new PixelDifferenceInspectionEngine().Analyze(samplePath, goldenPath, DetectionPriority.Balanced);

        Assert.True(PixelDifferenceGoldenCache.Hits >= 1);
        Assert.Equal(first.DifferenceScore, second.DifferenceScore);
        Assert.Equal(first.Hotspot, second.Hotspot);
    }

    [Fact]
    public void MissingGoldenStillFailsGracefullyWithoutCaching()
    {
        AoiDatabase.Initialize();
        var samplePath = WritePng("sample.png", 16, 16, baseValue: 90);

        var result = new PixelDifferenceInspectionEngine().Analyze(
            samplePath,
            Path.Combine(_root, "missing-golden.png"),
            DetectionPriority.Balanced);

        Assert.Equal("REVIEW", result.Verdict);
        Assert.Equal("GOLDEN_IMAGE_LOAD_FAILED", result.ErrorCode);
    }

    [Fact]
    public void ScaledGoldenWithRealCropProducesIdenticalResultsOnHitAndMiss()
    {
        AoiDatabase.Initialize();
        // Larger than the 384 px normalization bound so the WPF/WIC Fant scaler really
        // runs, with different aspect ratios so the min-dims crop path really executes.
        var goldenPath = WritePng("golden-scaled.png", 500, 400, baseValue: 90);
        var samplePath = WritePng("sample-scaled.png", 450, 450, baseValue: 90, blobValue: 220);

        var first = new PixelDifferenceInspectionEngine().Analyze(samplePath, goldenPath, DetectionPriority.Balanced);
        var second = new PixelDifferenceInspectionEngine().Analyze(samplePath, goldenPath, DetectionPriority.Balanced);

        Assert.True(PixelDifferenceGoldenCache.Hits >= 1);
        Assert.Equal(first.DifferenceScore, second.DifferenceScore);
        Assert.Equal(first.Verdict, second.Verdict);
        Assert.Equal(first.Hotspot, second.Hotspot);
    }

    [Fact]
    public void CachedFullCopyPlusManualCropMatchesLegacyCroppedBitmapExtraction()
    {
        // Pins the load-bearing WIC assumption behind CropGolden: a full CopyPixels of
        // the Fant-scaled golden followed by a top-left memory crop must be byte-identical
        // to the legacy CroppedBitmap(0,0,w,h) + CopyPixels extraction the engine used
        // before the cache. Uses a >384 px source so real resampling occurs.
        var goldenPath = WritePng("golden-wic.png", 500, 400, baseValue: 77, blobValue: 180);
        var bmp = new System.Windows.Media.Imaging.BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        bmp.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreImageCache;
        bmp.UriSource = new Uri(goldenPath, UriKind.Absolute);
        bmp.EndInit();
        bmp.Freeze();
        var scale = Math.Min(384.0 / bmp.PixelWidth, 384.0 / bmp.PixelHeight);
        var scaled = new System.Windows.Media.Imaging.TransformedBitmap(bmp, new ScaleTransform(scale, scale));
        scaled.Freeze();
        var normalized = scaled.Format == PixelFormats.Bgra32
            ? (System.Windows.Media.Imaging.BitmapSource)scaled
            : new System.Windows.Media.Imaging.FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
        var fullWidth = normalized.PixelWidth;
        var fullHeight = normalized.PixelHeight;
        var cropWidth = fullWidth - 10;
        var cropHeight = fullHeight - 7;

        var fullPixels = new byte[fullWidth * 4 * fullHeight];
        normalized.CopyPixels(fullPixels, fullWidth * 4, 0);
        var manualCrop = new byte[cropWidth * 4 * cropHeight];
        for (var y = 0; y < cropHeight; y++)
            Buffer.BlockCopy(fullPixels, y * fullWidth * 4, manualCrop, y * cropWidth * 4, cropWidth * 4);

        var legacyCrop = new byte[cropWidth * 4 * cropHeight];
        var cropped = new System.Windows.Media.Imaging.CroppedBitmap(normalized, new System.Windows.Int32Rect(0, 0, cropWidth, cropHeight));
        cropped.CopyPixels(legacyCrop, cropWidth * 4, 0);

        Assert.True(fullWidth < 500 && fullHeight < 400);
        Assert.Equal(legacyCrop, manualCrop);
    }

    [Fact]
    public void OverlayDataPreparationTimeIsRecorded()
    {
        AoiDatabase.Initialize();
        var goldenPath = WritePng("golden.png", 32, 32, baseValue: 100);
        var samplePath = WritePng("sample.png", 32, 32, baseValue: 100, blobValue: 240);

        var result = new PixelDifferenceInspectionEngine().Analyze(samplePath, goldenPath, DetectionPriority.Balanced);

        Assert.True(result.Timing.OverlayRenderingMilliseconds > 0);
        Assert.True(result.Timing.TotalInspectionMilliseconds >= result.Timing.OverlayRenderingMilliseconds);
    }

    private string WritePng(string fileName, int width, int height, int baseValue, int? blobValue = null)
    {
        var path = Path.Combine(_root, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = ((y * width) + x) * 4;
                var inBlob = blobValue is not null && x >= width / 2 && y >= height / 2;
                var value = (byte)Math.Clamp(inBlob ? blobValue!.Value : baseValue + ((x + y) % 7), 0, 255);
                pixels[offset] = value;
                pixels[offset + 1] = value;
                pixels[offset + 2] = value;
                pixels[offset + 3] = 255;
            }
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }
}
