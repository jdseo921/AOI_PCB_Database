using System.IO;
using System.Windows;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace AOI_Monitor.Services;

public sealed record ModelDetection(
    int ClassId,
    string Label,
    double Confidence,
    Rect BoundingBox);

public interface IModelOutputParser
{
    string Name { get; }

    IReadOnlyList<ModelDetection> Parse(
        Tensor<float> output,
        IReadOnlyDictionary<int, string> labels,
        double confidenceThreshold,
        int inputWidth,
        int inputHeight);
}

public sealed class GenericDetectionOutputParser : IModelOutputParser
{
    public string Name => "Generic Detection [class,confidence,x,y,width,height]";

    public IReadOnlyList<ModelDetection> Parse(
        Tensor<float> output,
        IReadOnlyDictionary<int, string> labels,
        double confidenceThreshold,
        int inputWidth,
        int inputHeight)
    {
        var dimensions = output.Dimensions.ToArray();
        if (dimensions.Length == 0)
            return Array.Empty<ModelDetection>();

        var values = output.ToArray();
        var columns = dimensions[^1] >= 6 ? dimensions[^1] : 6;
        if (values.Length < 6 || values.Length % columns != 0)
            throw new InvalidDataException($"Unsupported detection output shape: [{string.Join(",", dimensions)}]. Expected rows of 6 values.");

        var detections = new List<ModelDetection>();
        var rows = values.Length / columns;
        for (var row = 0; row < rows; row++)
        {
            var offset = row * columns;
            var classId = (int)Math.Round(values[offset]);
            var confidence = values[offset + 1];
            if (confidence < confidenceThreshold)
                continue;

            var box = NormalizeBox(
                values[offset + 2],
                values[offset + 3],
                values[offset + 4],
                values[offset + 5],
                inputWidth,
                inputHeight);

            if (box.Width <= 0 || box.Height <= 0)
                continue;

            detections.Add(new ModelDetection(
                classId,
                labels.TryGetValue(classId, out var label) ? label : $"Class {classId}",
                Math.Clamp(confidence, 0, 1),
                box));
        }

        return detections
            .OrderByDescending(detection => detection.Confidence)
            .ToArray();
    }

    private static Rect NormalizeBox(double x, double y, double width, double height, int inputWidth, int inputHeight)
    {
        if (Math.Max(Math.Max(Math.Abs(x), Math.Abs(y)), Math.Max(Math.Abs(width), Math.Abs(height))) > 1.5)
        {
            x /= Math.Max(1, inputWidth);
            width /= Math.Max(1, inputWidth);
            y /= Math.Max(1, inputHeight);
            height /= Math.Max(1, inputHeight);
        }

        var left = Clamp01(x);
        var top = Clamp01(y);
        var right = Clamp01(x + width);
        var bottom = Clamp01(y + height);
        return new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    private static double Clamp01(double value) => Math.Clamp(value, 0, 1);
}

/// <summary>
/// Parses per-pixel anomaly heat maps as produced by anomaly-detection exports such as
/// anomalib PatchCore/PaDiM/FastFlow ONNX models: output shaped [1,1,H,W], [1,H,W] or [H,W]
/// where each value is an anomaly score. Pixels at or above the confidence threshold are
/// grouped into 4-connected regions; each region becomes one detection whose confidence is
/// the region's peak value and whose bounding box is the region extent. Maps whose values
/// exceed [0,1] (models exported without embedded normalization) are min-max normalized
/// first so the threshold keeps its meaning.
/// </summary>
public sealed class AnomalyHeatmapOutputParser : IModelOutputParser
{
    public string Name => "Anomaly Heatmap [1,1,H,W]";

    /// <summary>Regions smaller than this many pixels are treated as noise.</summary>
    public int MinimumRegionPixels { get; init; } = 9;

    public static bool LooksLikeHeatmap(ReadOnlySpan<int> dimensions)
    {
        // Accept [H,W], [1,H,W], [1,1,H,W] with a plausibly image-sized map. Detection outputs
        // have a small trailing dimension (rows of 6+ values), heat maps have two large ones.
        var trimmed = new List<int>();
        foreach (var dimension in dimensions)
        {
            if (dimension != 1)
                trimmed.Add(dimension);
        }

        return trimmed.Count == 2 && trimmed[0] >= 16 && trimmed[1] >= 16;
    }

    public IReadOnlyList<ModelDetection> Parse(
        Tensor<float> output,
        IReadOnlyDictionary<int, string> labels,
        double confidenceThreshold,
        int inputWidth,
        int inputHeight)
    {
        var dimensions = output.Dimensions.ToArray();
        if (!LooksLikeHeatmap(dimensions))
            throw new InvalidDataException($"Unsupported anomaly heat-map shape: [{string.Join(",", dimensions)}]. Expected [1,1,H,W], [1,H,W] or [H,W].");

        var significant = dimensions.Where(dimension => dimension != 1).ToArray();
        var height = significant[0];
        var width = significant[1];
        var values = output.ToArray();

        // Models exported without embedded normalization emit raw scores; min-max normalize so
        // the configured threshold keeps a [0,1] meaning. Normalized exports pass through.
        var min = values.Min();
        var max = values.Max();
        if (min < 0f || max > 1f)
        {
            var range = Math.Max(1e-6f, max - min);
            for (var i = 0; i < values.Length; i++)
                values[i] = (values[i] - min) / range;
        }

        var threshold = (float)Math.Clamp(confidenceThreshold, 0.0, 1.0);
        var visited = new bool[values.Length];
        var detections = new List<ModelDetection>();
        var queue = new Queue<int>();
        var anomalyLabel = labels.TryGetValue(0, out var label) ? label : "Anomaly";

        for (var start = 0; start < values.Length; start++)
        {
            if (visited[start] || values[start] < threshold)
                continue;

            visited[start] = true;
            queue.Enqueue(start);
            var minX = int.MaxValue;
            var minY = int.MaxValue;
            var maxX = int.MinValue;
            var maxY = int.MinValue;
            var peak = 0f;
            var pixels = 0;

            while (queue.Count > 0)
            {
                var index = queue.Dequeue();
                var x = index % width;
                var y = index / width;
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
                peak = Math.Max(peak, values[index]);
                pixels++;

                Visit(x - 1, y);
                Visit(x + 1, y);
                Visit(x, y - 1);
                Visit(x, y + 1);
            }

            if (pixels < MinimumRegionPixels)
                continue;

            detections.Add(new ModelDetection(
                0,
                anomalyLabel,
                Math.Clamp(peak, 0, 1),
                new Rect(
                    minX / (double)width,
                    minY / (double)height,
                    Math.Max(1, maxX - minX + 1) / (double)width,
                    Math.Max(1, maxY - minY + 1) / (double)height)));

            void Visit(int nx, int ny)
            {
                if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                    return;

                var neighbor = (ny * width) + nx;
                if (visited[neighbor] || values[neighbor] < threshold)
                    return;

                visited[neighbor] = true;
                queue.Enqueue(neighbor);
            }
        }

        return detections
            .OrderByDescending(detection => detection.Confidence)
            .ToArray();
    }
}

/// <summary>
/// Dispatches to the heat-map parser when the output tensor is shaped like a per-pixel map
/// and to the generic detection parser otherwise, so both anomaly-detection exports (e.g.
/// anomalib) and detector exports work through the same configured ONNX slot.
/// </summary>
public sealed class AutoDetectOutputParser : IModelOutputParser
{
    private readonly GenericDetectionOutputParser _detection = new();
    private readonly AnomalyHeatmapOutputParser _heatmap = new();

    public string Name => "Auto (detection rows or anomaly heat map)";

    public IReadOnlyList<ModelDetection> Parse(
        Tensor<float> output,
        IReadOnlyDictionary<int, string> labels,
        double confidenceThreshold,
        int inputWidth,
        int inputHeight)
    {
        return AnomalyHeatmapOutputParser.LooksLikeHeatmap(output.Dimensions)
            ? _heatmap.Parse(output, labels, confidenceThreshold, inputWidth, inputHeight)
            : _detection.Parse(output, labels, confidenceThreshold, inputWidth, inputHeight);
    }
}
