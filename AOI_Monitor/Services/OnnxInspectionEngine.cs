using System.Buffers;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public sealed class OnnxInspectionEngine : IInspectionEngine
{
    private readonly InspectionModelConfiguration _configuration;

    public OnnxInspectionEngine(InspectionModelConfiguration configuration)
    {
        _configuration = configuration;
    }

    public static bool RuntimeAvailable => false;

    public string Name => "ONNX ML Model";
    public string Version => _configuration.EffectiveModelVersion;

    public AnalysisResult Analyze(string samplePath, string? goldenPath, DetectionPriority priority)
    {
        if (!_configuration.HasModelFile)
            return BuildUnavailableResult(samplePath, goldenPath, "ML Model Missing", "Configured ONNX model file is missing. Pixel-difference remains the safe production fallback.");

        if (!RuntimeAvailable)
            return BuildUnavailableResult(samplePath, goldenPath, "ML Runtime Error", "ONNX model file is configured, but ONNX Runtime is not installed in this build.");

        return BuildUnavailableResult(samplePath, goldenPath, "ML Runtime Error", "ONNX Runtime adapter is present, but inference implementation has not been connected yet.");
    }

    private AnalysisResult BuildUnavailableResult(string samplePath, string? goldenPath, string defectType, string reason)
    {
        var hotspot = new Rect(0.45, 0.4, 0.14, 0.12);
        var confidence = Math.Clamp(_configuration.ConfidenceThreshold, 0.0, 1.0);

        var result = new AnalysisResult
        {
            SamplePath = samplePath,
            GoldenPath = goldenPath,
            InspectionEngine = Name,
            ModelVersion = Version,
            MeanBrightness = CalculateBrightness(samplePath),
            Timestamp = DateTime.Now,
            SuggestedDefect = defectType,
            Verdict = "REVIEW",
            DifferenceScore = 0,
            ReviewThreshold = confidence * 100.0,
            NgThreshold = confidence * 100.0,
            Confidence = 0,
            DecisionMargin = 0,
            DecisionReason = reason,
            PolicyName = $"ML confidence >= {confidence:P0}",
            Hotspot = hotspot,
            Evidence = new List<string>
            {
                $"Selected engine: {Name}.",
                $"Configured model: {(_configuration.HasModelFile ? _configuration.ModelFilePath : "missing")}.",
                $"Input size: {_configuration.InputImageWidth}x{_configuration.InputImageHeight}.",
                "No ML inference was executed by this prototype build.",
            },
        };

        result.Defects.Add(new DefectResult
        {
            DefectType = defectType,
            Confidence = result.Confidence,
            BoundingBox = hotspot,
            XPosition = hotspot.X + hotspot.Width / 2.0,
            YPosition = hotspot.Y + hotspot.Height / 2.0,
            SideOrViewType = "sample",
            RoiId = "ROI-ML-ADAPTER",
            JudgmentStatus = result.Verdict,
        });

        return result;
    }

    private static double CalculateBrightness(string path)
    {
        if (!File.Exists(path))
            return 0;

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();

            BitmapSource source = bmp.Format == PixelFormats.Bgra32
                ? bmp
                : new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);

            var stride = source.PixelWidth * 4;
            var count = stride * source.PixelHeight;
            var pool = ArrayPool<byte>.Shared;
            var pixels = pool.Rent(count);

            try
            {
                source.CopyPixels(pixels, stride, 0);
                double sum = 0;
                for (var i = 0; i < count; i += 4)
                    sum += 0.114 * pixels[i] + 0.587 * pixels[i + 1] + 0.299 * pixels[i + 2];

                return count == 0 ? 0 : sum / (count / 4.0);
            }
            finally
            {
                pool.Return(pixels);
            }
        }
        catch
        {
            return 0;
        }
    }
}
