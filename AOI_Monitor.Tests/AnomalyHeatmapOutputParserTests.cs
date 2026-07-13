using System.Windows;
using AOI_Monitor.Services;
using Microsoft.ML.OnnxRuntime.Tensors;
using Xunit;

namespace AOI_Monitor.Tests;

/// <summary>
/// The anomaly heat-map parser makes the ONNX slot compatible with anomaly-detection exports
/// (anomalib PatchCore/PaDiM/FastFlow): per-pixel maps become region detections. Pure tensor
/// logic — no ONNX Runtime session required.
/// </summary>
public sealed class AnomalyHeatmapOutputParserTests
{
    private static readonly IReadOnlyDictionary<int, string> Labels = new Dictionary<int, string> { [0] = "Anomaly" };

    private static DenseTensor<float> Heatmap(int height, int width, float background, params (int X, int Y, int W, int H, float Value)[] blocks)
    {
        var tensor = new DenseTensor<float>(new[] { 1, 1, height, width });
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
                tensor[0, 0, y, x] = background;
        }

        foreach (var block in blocks)
        {
            for (var y = block.Y; y < block.Y + block.H; y++)
            {
                for (var x = block.X; x < block.X + block.W; x++)
                    tensor[0, 0, y, x] = block.Value;
            }
        }

        return tensor;
    }

    [Fact]
    public void SingleHotRegionBecomesOneDetectionWithNormalizedBox()
    {
        var map = Heatmap(32, 32, 0.1f, (10, 12, 5, 5, 0.9f));
        var parser = new AnomalyHeatmapOutputParser();

        var detections = parser.Parse(map, Labels, confidenceThreshold: 0.65, inputWidth: 32, inputHeight: 32);

        var detection = Assert.Single(detections);
        Assert.Equal("Anomaly", detection.Label);
        Assert.Equal(0.9, detection.Confidence, precision: 3);
        Assert.Equal(10 / 32.0, detection.BoundingBox.X, precision: 6);
        Assert.Equal(12 / 32.0, detection.BoundingBox.Y, precision: 6);
        Assert.Equal(5 / 32.0, detection.BoundingBox.Width, precision: 6);
        Assert.Equal(5 / 32.0, detection.BoundingBox.Height, precision: 6);
    }

    [Fact]
    public void TwoSeparateRegionsBecomeTwoDetectionsOrderedByConfidence()
    {
        var map = Heatmap(32, 32, 0.05f, (2, 2, 4, 4, 0.7f), (20, 20, 5, 5, 0.95f));
        var parser = new AnomalyHeatmapOutputParser();

        var detections = parser.Parse(map, Labels, confidenceThreshold: 0.5, inputWidth: 32, inputHeight: 32);

        Assert.Equal(2, detections.Count);
        Assert.Equal(0.95, detections[0].Confidence, precision: 3);
        Assert.Equal(0.7, detections[1].Confidence, precision: 3);
    }

    [Fact]
    public void TinyRegionsAreFilteredAsNoise()
    {
        // 2x2 = 4 pixels < default MinimumRegionPixels (9).
        var map = Heatmap(32, 32, 0.0f, (5, 5, 2, 2, 0.99f));
        var parser = new AnomalyHeatmapOutputParser();

        var detections = parser.Parse(map, Labels, confidenceThreshold: 0.5, inputWidth: 32, inputHeight: 32);

        Assert.Empty(detections);
    }

    [Fact]
    public void CleanMapYieldsNoDetections()
    {
        var map = Heatmap(32, 32, 0.2f);
        var parser = new AnomalyHeatmapOutputParser();

        Assert.Empty(parser.Parse(map, Labels, confidenceThreshold: 0.65, inputWidth: 32, inputHeight: 32));
    }

    [Fact]
    public void UnnormalizedMapIsMinMaxNormalizedBeforeThresholding()
    {
        // Raw scores far outside [0,1] (model exported without embedded normalization):
        // background 5, blob 50 -> after min-max the blob is 1.0 and clears the threshold.
        var map = Heatmap(32, 32, 5f, (8, 8, 5, 5, 50f));
        var parser = new AnomalyHeatmapOutputParser();

        var detections = parser.Parse(map, Labels, confidenceThreshold: 0.65, inputWidth: 32, inputHeight: 32);

        var detection = Assert.Single(detections);
        Assert.Equal(1.0, detection.Confidence, precision: 3);
    }

    [Theory]
    [InlineData(new[] { 1, 1, 64, 64 }, true)]
    [InlineData(new[] { 1, 64, 64 }, true)]
    [InlineData(new[] { 64, 64 }, true)]
    [InlineData(new[] { 1, 100, 6 }, false)]  // detection rows
    [InlineData(new[] { 1, 3, 224, 224 }, false)]  // RGB tensor, not a map
    [InlineData(new[] { 6 }, false)]
    public void LooksLikeHeatmapClassifiesShapes(int[] dimensions, bool expected)
    {
        Assert.Equal(expected, AnomalyHeatmapOutputParser.LooksLikeHeatmap(dimensions));
    }

    [Fact]
    public void AutoDetectParserDispatchesHeatmapsAndDetectionRows()
    {
        var auto = new AutoDetectOutputParser();

        // Heat map goes through the region parser.
        var map = Heatmap(32, 32, 0.1f, (10, 10, 5, 5, 0.9f));
        var fromMap = auto.Parse(map, Labels, 0.65, 32, 32);
        Assert.Single(fromMap);

        // Detection rows go through the generic parser: one row, class 0, confidence 0.8,
        // normalized box.
        var rows = new DenseTensor<float>(new[] { 1, 1, 6 });
        rows[0, 0, 0] = 0f;
        rows[0, 0, 1] = 0.8f;
        rows[0, 0, 2] = 0.25f;
        rows[0, 0, 3] = 0.25f;
        rows[0, 0, 4] = 0.5f;
        rows[0, 0, 5] = 0.5f;
        var fromRows = auto.Parse(rows, Labels, 0.5, 32, 32);
        var detection = Assert.Single(fromRows);
        Assert.Equal(0.8, detection.Confidence, precision: 3);
        Assert.Equal(new Rect(0.25, 0.25, 0.5, 0.5), detection.BoundingBox);
    }
}
