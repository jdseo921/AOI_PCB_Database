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
