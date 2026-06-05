using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public static class ImageAnalysisService
{
    public static AnalysisResult Analyze(string samplePath, string? goldenPath, DetectionPriority priority)
    {
        var sample = LoadBgra32(samplePath);
        if (sample is null)
            throw new InvalidOperationException("Unable to load sample image.");

        var result = new AnalysisResult
        {
            SamplePath = samplePath,
            GoldenPath = goldenPath,
            MeanBrightness = CalculateBrightness(sample),
            Timestamp = DateTime.Now,
            SuggestedDefect = "Solder Bridge",
            Verdict = "REVIEW",
            DifferenceScore = 0,
            Hotspot = new Rect(0.45, 0.4, 0.14, 0.12),
        };

        if (string.IsNullOrWhiteSpace(goldenPath))
            return result;

        var golden = LoadBgra32(goldenPath);
        if (golden is null)
            throw new InvalidOperationException("Unable to load golden image.");

        // Normalize both to a compact size so compare runtime is stable.
        var sampleNorm = Resize(sample, 384, 384);
        var goldenNorm = Resize(golden, 384, 384);

        var diff = Compare(sampleNorm, goldenNorm, out var hotspot);
        result.DifferenceScore = diff;
        result.Hotspot = hotspot;

        var (ngThreshold, reviewThreshold) = GetThresholds(priority);

        if (diff >= ngThreshold)
        {
            result.Verdict = "NG";
            result.SuggestedDefect = "Possible Solder Bridge";
        }
        else if (diff >= reviewThreshold)
        {
            result.Verdict = "REVIEW";
            result.SuggestedDefect = "Alignment / Reflection Difference";
        }
        else
        {
            result.Verdict = "OK";
            result.SuggestedDefect = "No Significant Difference";
        }

        return result;
    }

    private static (double ngThreshold, double reviewThreshold) GetThresholds(DetectionPriority priority)
    {
        // Conservative mode raises thresholds to reduce false positives.
        return priority switch
        {
            DetectionPriority.MinimizeFalsePositives => (24, 12),
            DetectionPriority.Balanced => (18, 8),
            DetectionPriority.MaximizeDefectRecall => (14, 5),
            _ => (18, 8),
        };
    }

    private static BitmapSource? LoadBgra32(string path)
    {
        if (!File.Exists(path)) return null;

        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.UriSource = new Uri(path, UriKind.Absolute);
        bmp.EndInit();
        bmp.Freeze();

        if (bmp.Format == PixelFormats.Bgra32)
            return bmp;

        var converted = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        return converted;
    }

    private static BitmapSource Resize(BitmapSource source, int maxW, int maxH)
    {
        var scale = Math.Min(maxW / (double)source.PixelWidth, maxH / (double)source.PixelHeight);
        scale = Math.Min(1.0, scale);

        var transform = new ScaleTransform(scale, scale);
        var resized = new TransformedBitmap(source, transform);
        resized.Freeze();

        if (resized.Format == PixelFormats.Bgra32)
            return resized;

        var converted = new FormatConvertedBitmap(resized, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        return converted;
    }

    private static double CalculateBrightness(BitmapSource src)
    {
        var stride = src.PixelWidth * 4;
        var pixels = new byte[stride * src.PixelHeight];
        src.CopyPixels(pixels, stride, 0);

        double sum = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            // BGR to luma approximation.
            sum += 0.114 * pixels[i] + 0.587 * pixels[i + 1] + 0.299 * pixels[i + 2];
        }

        var n = pixels.Length / 4.0;
        return n == 0 ? 0 : sum / n;
    }

    private static double Compare(BitmapSource a, BitmapSource b, out Rect hotspot)
    {
        var w = Math.Min(a.PixelWidth, b.PixelWidth);
        var h = Math.Min(a.PixelHeight, b.PixelHeight);

        var stride = w * 4;
        var pa = new byte[stride * h];
        var pb = new byte[stride * h];

        var ra = new CroppedBitmap(a, new Int32Rect(0, 0, w, h));
        var rb = new CroppedBitmap(b, new Int32Rect(0, 0, w, h));
        ra.CopyPixels(pa, stride, 0);
        rb.CopyPixels(pb, stride, 0);

        double total = 0;
        const int gridX = 8;
        const int gridY = 8;
        var bins = new double[gridX * gridY];
        int cw = Math.Max(1, w / gridX);
        int ch = Math.Max(1, h / gridY);

        for (int y = 0; y < h; y++)
        {
            int gy = Math.Min(gridY - 1, y / ch);
            int row = y * stride;
            for (int x = 0; x < w; x++)
            {
                int gx = Math.Min(gridX - 1, x / cw);
                int i = row + x * 4;

                double dr = Math.Abs(pa[i + 2] - pb[i + 2]);
                double dg = Math.Abs(pa[i + 1] - pb[i + 1]);
                double db = Math.Abs(pa[i] - pb[i]);
                double d = (dr + dg + db) / 3.0;

                total += d;
                bins[gy * gridX + gx] += d;
            }
        }

        int idx = 0;
        double best = double.MinValue;
        for (int i = 0; i < bins.Length; i++)
        {
            if (bins[i] > best)
            {
                best = bins[i];
                idx = i;
            }
        }

        int bx = idx % gridX;
        int by = idx / gridX;
        hotspot = new Rect(
            bx / (double)gridX,
            by / (double)gridY,
            1.0 / gridX,
            1.0 / gridY);

        var mad = total / (w * h);
        return Math.Min(100.0, mad / 255.0 * 100.0);
    }
}
