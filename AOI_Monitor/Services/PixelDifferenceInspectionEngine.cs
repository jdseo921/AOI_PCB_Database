using System.Buffers;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public sealed class PixelDifferenceInspectionEngine : IInspectionEngine
{
    public string Name => "Pixel Difference";
    public string Version => "PIXEL_DIFF_0.1";

    public AnalysisResult Analyze(string samplePath, string? goldenPath, DetectionPriority priority)
    {
        var sample = LoadBgra32(samplePath);
        if (sample is null)
            throw new InvalidOperationException("Unable to load sample image.");

        var result = new AnalysisResult
        {
            SamplePath = samplePath,
            GoldenPath = goldenPath,
            InspectionEngine = Name,
            ModelVersion = Version,
            MeanBrightness = CalculateBrightness(sample),
            Timestamp = DateTime.Now,
            SuggestedDefect = "Solder Bridge",
            Verdict = "REVIEW",
            DifferenceScore = 0,
            ReviewThreshold = 0,
            NgThreshold = 0,
            Confidence = 0.55,
            DecisionMargin = 0,
            DecisionReason = "Golden reference is required for differential judgement.",
            PolicyName = ToPolicyDisplay(priority),
            Hotspot = new Rect(0.45, 0.4, 0.14, 0.12),
            Evidence = new List<string>
            {
                "No golden image was supplied; decision remains REVIEW by policy.",
                "Run comparison against a verified golden image for actionable classification.",
            },
        };

        if (string.IsNullOrWhiteSpace(goldenPath))
        {
            result.Defects.Add(CreateDefectResult(result, "Reference Missing", "ROI-REFERENCE"));
            return result;
        }

        var golden = LoadBgra32(goldenPath);
        if (golden is null)
            throw new InvalidOperationException("Unable to load golden image.");

        var sampleNorm = Resize(sample, 384, 384);
        var goldenNorm = Resize(golden, 384, 384);

        var diff = Compare(sampleNorm, goldenNorm, out var hotspot);
        result.DifferenceScore = diff;
        result.Hotspot = hotspot;

        var (ngThreshold, reviewThreshold) = GetThresholds(priority);
        result.NgThreshold = ngThreshold;
        result.ReviewThreshold = reviewThreshold;

        if (diff >= ngThreshold)
        {
            result.Verdict = "NG";
            result.SuggestedDefect = "Possible Solder Bridge";
            result.DecisionMargin = diff - ngThreshold;
            result.DecisionReason = "Difference score exceeds NG threshold under current policy.";
        }
        else if (diff >= reviewThreshold)
        {
            result.Verdict = "REVIEW";
            result.SuggestedDefect = "Alignment / Reflection Difference";
            result.DecisionMargin = Math.Min(diff - reviewThreshold, ngThreshold - diff);
            result.DecisionReason = "Difference score is in the review band; human confirmation required.";
        }
        else
        {
            result.Verdict = "OK";
            result.SuggestedDefect = "No Significant Difference";
            result.DecisionMargin = reviewThreshold - diff;
            result.DecisionReason = "Difference score is below review threshold for this policy.";
        }

        result.Confidence = ComputeConfidence(result.Verdict, diff, reviewThreshold, ngThreshold);
        result.Evidence = BuildEvidence(result, priority);
        result.Defects.Add(CreateDefectResult(result, result.SuggestedDefect, "ROI-HOTSPOT-001"));

        return result;
    }

    private static DefectResult CreateDefectResult(AnalysisResult result, string defectType, string roiId)
    {
        var box = result.Hotspot;
        return new DefectResult
        {
            DefectType = defectType,
            Confidence = result.Confidence,
            BoundingBox = box,
            XPosition = box.X + box.Width / 2.0,
            YPosition = box.Y + box.Height / 2.0,
            SideOrViewType = "sample",
            RoiId = roiId,
            JudgmentStatus = result.Verdict,
        };
    }

    private static string ToPolicyDisplay(DetectionPriority priority) => priority switch
    {
        DetectionPriority.MinimizeFalsePositives => "Minimize False Positives",
        DetectionPriority.Balanced => "Balanced",
        DetectionPriority.MaximizeDefectRecall => "Maximize Defect Recall",
        _ => "Balanced",
    };

    private static double ComputeConfidence(string verdict, double diff, double reviewThreshold, double ngThreshold)
    {
        if (verdict == "NG")
        {
            var normalized = Math.Clamp((diff - ngThreshold) / Math.Max(2.0, ngThreshold * 0.5), 0, 1);
            return 0.72 + normalized * 0.27;
        }

        if (verdict == "OK")
        {
            var normalized = Math.Clamp((reviewThreshold - diff) / Math.Max(2.0, reviewThreshold * 0.6), 0, 1);
            return 0.68 + normalized * 0.3;
        }

        var mid = (reviewThreshold + ngThreshold) / 2.0;
        var halfBand = Math.Max(1.0, (ngThreshold - reviewThreshold) / 2.0);
        var centered = 1.0 - Math.Clamp(Math.Abs(diff - mid) / halfBand, 0, 1);
        return 0.52 + centered * 0.22;
    }

    private static List<string> BuildEvidence(AnalysisResult result, DetectionPriority priority)
    {
        return new List<string>
        {
            $"Difference score: {result.DifferenceScore:F1}% (Review >= {result.ReviewThreshold:F1}%, NG >= {result.NgThreshold:F1}%).",
            $"Policy: {ToPolicyDisplay(priority)}.",
            $"Hotspot: x={result.Hotspot.X:P0}, y={result.Hotspot.Y:P0}, w={result.Hotspot.Width:P0}, h={result.Hotspot.Height:P0}.",
            $"Mean brightness (sample): {result.MeanBrightness:F1}.",
            $"Decision margin: {result.DecisionMargin:F2}.",
        };
    }

    private static (double ngThreshold, double reviewThreshold) GetThresholds(DetectionPriority priority)
    {
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
        var count = stride * src.PixelHeight;
        var pool = ArrayPool<byte>.Shared;
        var pixels = pool.Rent(count);

        try
        {
            src.CopyPixels(pixels, stride, 0);

            double sum = 0;
            for (int i = 0; i < count; i += 4)
            {
                sum += 0.114 * pixels[i] + 0.587 * pixels[i + 1] + 0.299 * pixels[i + 2];
            }

            var n = count / 4.0;
            return n == 0 ? 0 : sum / n;
        }
        finally
        {
            pool.Return(pixels);
        }
    }

    private static double Compare(BitmapSource a, BitmapSource b, out Rect hotspot)
    {
        var w = Math.Min(a.PixelWidth, b.PixelWidth);
        var h = Math.Min(a.PixelHeight, b.PixelHeight);

        var stride = w * 4;
        var count = stride * h;
        var pool = ArrayPool<byte>.Shared;
        var pa = pool.Rent(count);
        var pb = pool.Rent(count);

        try
        {
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
        finally
        {
            pool.Return(pa);
            pool.Return(pb);
        }
    }
}
