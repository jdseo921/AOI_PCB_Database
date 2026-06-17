using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AOI_Monitor.Models;
using Microsoft.ML.OnnxRuntime;

namespace AOI_Monitor.Services;

public sealed record ModelConfigurationTestResult(
    ModelConfigurationTestStatus Status,
    DateTime TimestampUtc,
    string Message,
    string ConfigurationHash)
{
    public string DisplayStatus => ModelConfigurationValidator.ToDisplay(Status);
}

public static class ModelConfigurationValidator
{
    public static ModelConfigurationTestResult Test(InspectionModelConfiguration configuration)
    {
        var timestamp = DateTime.UtcNow;
        var fingerprint = ComputeConfigurationHash(configuration);

        if (!configuration.IsOnnxSelected)
            return Result(ModelConfigurationTestStatus.Ready, "Pixel Difference Prototype Engine selected; no ONNX ML Model is required.", timestamp, fingerprint);

        if (string.IsNullOrWhiteSpace(configuration.ModelFilePath) || !File.Exists(configuration.ModelFilePath))
            return Result(ModelConfigurationTestStatus.MissingModel, $"Model file is missing: {NullIfEmpty(configuration.ModelFilePath)}", timestamp, fingerprint);

        if (configuration.InputImageWidth < 32 || configuration.InputImageWidth > 8192 ||
            configuration.InputImageHeight < 32 || configuration.InputImageHeight > 8192)
        {
            return Result(ModelConfigurationTestStatus.RuntimeError, "Input width and height must be between 32 and 8192.", timestamp, fingerprint);
        }

        if (configuration.ConfidenceThreshold is < 0 or > 1)
            return Result(ModelConfigurationTestStatus.RuntimeError, "Confidence threshold must be between 0 and 1.", timestamp, fingerprint);

        if (string.IsNullOrWhiteSpace(configuration.InputTensorName) || string.IsNullOrWhiteSpace(configuration.OutputTensorName))
            return Result(ModelConfigurationTestStatus.RuntimeError, "Input and output tensor names are required for a readiness check.", timestamp, fingerprint);

        var labelMap = ValidateLabelMap(configuration, timestamp, fingerprint);
        if (labelMap is not null)
            return labelMap;

        try
        {
            using var session = new InferenceSession(configuration.ModelFilePath);

            if (!session.InputMetadata.ContainsKey(configuration.InputTensorName))
            {
                return Result(
                    ModelConfigurationTestStatus.RuntimeError,
                    $"Input tensor '{configuration.InputTensorName}' was not found. Available inputs: {string.Join(", ", session.InputMetadata.Keys)}.",
                    timestamp,
                    fingerprint);
            }

            if (!session.OutputMetadata.TryGetValue(configuration.OutputTensorName, out var outputMetadata))
            {
                return Result(
                    ModelConfigurationTestStatus.RuntimeError,
                    $"Output tensor '{configuration.OutputTensorName}' was not found. Available outputs: {string.Join(", ", session.OutputMetadata.Keys)}.",
                    timestamp,
                    fingerprint);
            }

            if (outputMetadata.ElementType != typeof(float))
            {
                return Result(
                    ModelConfigurationTestStatus.UnsupportedOutputFormat,
                    $"Output tensor '{configuration.OutputTensorName}' must be float for the generic detection parser. Actual type: {outputMetadata.ElementType.Name}.",
                    timestamp,
                    fingerprint);
            }

            var dimensions = outputMetadata.Dimensions?.ToArray() ?? Array.Empty<int>();
            if (dimensions.Length == 0)
            {
                return Result(
                    ModelConfigurationTestStatus.UnsupportedOutputFormat,
                    $"Output tensor '{configuration.OutputTensorName}' has no shape metadata. Expected rows of class, confidence, x, y, width, height.",
                    timestamp,
                    fingerprint);
            }

            var lastDimension = dimensions[^1];
            if (lastDimension > 0 && lastDimension < 6)
            {
                return Result(
                    ModelConfigurationTestStatus.UnsupportedOutputFormat,
                    $"Output tensor '{configuration.OutputTensorName}' shape [{string.Join(",", dimensions)}] is not compatible with class/confidence/box rows.",
                    timestamp,
                    fingerprint);
            }

            return Result(
                ModelConfigurationTestStatus.Ready,
                $"Ready. ONNX Runtime opened the model, found input '{configuration.InputTensorName}', output '{configuration.OutputTensorName}', and output shape [{string.Join(",", dimensions)}].",
                timestamp,
                fingerprint);
        }
        catch (Exception ex)
        {
            return Result(ModelConfigurationTestStatus.RuntimeError, $"ONNX Runtime could not create an inference session: {ex.Message}", timestamp, fingerprint);
        }
    }

    public static string ComputeConfigurationHash(InspectionModelConfiguration configuration)
    {
        var text = string.Join("|",
            InspectionEngineFactory.NormalizeEngineKey(configuration.SelectedEngineKey),
            configuration.ModelFilePath?.Trim() ?? string.Empty,
            configuration.ModelVersion?.Trim() ?? string.Empty,
            configuration.LabelMapPath?.Trim() ?? string.Empty,
            configuration.InputImageWidth,
            configuration.InputImageHeight,
            configuration.InputTensorName?.Trim() ?? string.Empty,
            configuration.OutputTensorName?.Trim() ?? string.Empty,
            configuration.ConfidenceThreshold.ToString("F6", System.Globalization.CultureInfo.InvariantCulture));

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    public static string ToDisplay(ModelConfigurationTestStatus status) => status switch
    {
        ModelConfigurationTestStatus.Ready => "Ready",
        ModelConfigurationTestStatus.MissingModel => "Missing Model",
        ModelConfigurationTestStatus.InvalidLabelMap => "Invalid Label Map",
        ModelConfigurationTestStatus.RuntimeError => "Runtime Error",
        ModelConfigurationTestStatus.UnsupportedOutputFormat => "Unsupported Output Format",
        _ => "Not Tested",
    };

    private static ModelConfigurationTestResult? ValidateLabelMap(
        InspectionModelConfiguration configuration,
        DateTime timestamp,
        string fingerprint)
    {
        if (string.IsNullOrWhiteSpace(configuration.LabelMapPath))
        {
            return configuration.BuiltInLabelMap.Count == 0
                ? Result(ModelConfigurationTestStatus.InvalidLabelMap, "No label map path is configured and no built-in labels are available.", timestamp, fingerprint)
                : null;
        }

        if (!File.Exists(configuration.LabelMapPath))
            return Result(ModelConfigurationTestStatus.InvalidLabelMap, $"Label map file is missing: {configuration.LabelMapPath}", timestamp, fingerprint);

        try
        {
            var labels = LoadLabels(configuration.LabelMapPath);
            return labels.Count == 0
                ? Result(ModelConfigurationTestStatus.InvalidLabelMap, $"Label map file contains no labels: {configuration.LabelMapPath}", timestamp, fingerprint)
                : null;
        }
        catch (Exception ex)
        {
            return Result(ModelConfigurationTestStatus.InvalidLabelMap, $"Label map could not be parsed: {ex.Message}", timestamp, fingerprint);
        }
    }

    private static Dictionary<int, string> LoadLabels(string path)
    {
        var labels = new Dictionary<int, string>();
        if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var item in document.RootElement.EnumerateArray())
                {
                    var label = item.GetString();
                    if (!string.IsNullOrWhiteSpace(label))
                        labels[index] = label.Trim();
                    index++;
                }

                return labels;
            }

            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("JSON label map must be an array or object.");

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (int.TryParse(property.Name, out var classId))
                {
                    var label = property.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(label))
                        labels[classId] = label.Trim();
                }
            }

            return labels;
        }

        var nextIndex = 0;
        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#", StringComparison.Ordinal))
                continue;

            var parts = line.Split(',', 2);
            if (parts.Length == 2 && int.TryParse(parts[0].Trim(), out var classId))
                labels[classId] = parts[1].Trim();
            else
                labels[nextIndex++] = line;
        }

        return labels;
    }

    private static ModelConfigurationTestResult Result(
        ModelConfigurationTestStatus status,
        string message,
        DateTime timestamp,
        string fingerprint)
        => new(status, timestamp, message, fingerprint);

    private static string NullIfEmpty(string value)
        => string.IsNullOrWhiteSpace(value) ? "(not configured)" : value;
}
