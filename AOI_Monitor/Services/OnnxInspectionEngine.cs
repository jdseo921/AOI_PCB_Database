using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AOI_Monitor.Models;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace AOI_Monitor.Services;

public sealed class OnnxInspectionEngine : IInspectionEngine
{
    private readonly InspectionModelConfiguration _configuration;
    private readonly IModelOutputParser _outputParser;

    public OnnxInspectionEngine(InspectionModelConfiguration configuration)
        : this(configuration, new GenericDetectionOutputParser())
    {
    }

    public OnnxInspectionEngine(InspectionModelConfiguration configuration, IModelOutputParser outputParser)
    {
        _configuration = configuration;
        _outputParser = outputParser;
    }

    public static bool RuntimeAvailable => true;

    public string Name => "ONNX ML Model";
    public string Version => _configuration.EffectiveModelVersion;

    public AnalysisResult Analyze(string samplePath, string? goldenPath, DetectionPriority priority)
    {
        var timing = new InspectionTiming();
        var threshold = Math.Clamp(_configuration.ConfidenceThreshold, 0.0, 1.0);
        if (!_configuration.HasModelFile)
            return BuildUnavailableResult(samplePath, goldenPath, "ML_MODEL_MISSING", "ML Model Missing", "Configured ONNX model file is missing. Pixel-difference remains the safe fallback.", timing: timing);

        if (!string.IsNullOrWhiteSpace(_configuration.ActiveModelId) &&
            _configuration.LastModelCheckResult != ModelConfigurationTestStatus.Ready)
        {
            return BuildUnavailableResult(
                samplePath,
                goldenPath,
                "MODEL_REGISTRY_NOT_READY",
                "ML Model Not Ready",
                $"Active registry model '{_configuration.ActiveModelId}' is not validated as Ready. Current status: {_configuration.LastModelCheckResult}.",
                timing: timing);
        }

        if (!File.Exists(samplePath))
            return BuildUnavailableResult(samplePath, goldenPath, "SAMPLE_IMAGE_MISSING", "ML Runtime Error", $"Sample image file is missing: {samplePath}", timing: timing);

        var inferenceWatch = new Stopwatch();
        try
        {
            inferenceWatch.Start();
            using var session = new InferenceSession(_configuration.ModelFilePath);
            var inputName = ResolveTensorName(_configuration.InputTensorName, session.InputMetadata.Keys, "input");
            var outputName = ResolveTensorName(_configuration.OutputTensorName, session.OutputMetadata.Keys, "output");
            inferenceWatch.Stop();
            timing.InferenceMilliseconds += inferenceWatch.Elapsed.TotalMilliseconds;

            var inputTensor = PreprocessImage(samplePath, _configuration.InputImageWidth, _configuration.InputImageHeight, timing, out var brightness);
            var labels = LoadLabelMap(out var labelMapEvidence);

            inferenceWatch.Restart();
            using var results = session.Run(new[]
            {
                NamedOnnxValue.CreateFromTensor(inputName, inputTensor),
            });

            var output = results.FirstOrDefault(value => string.Equals(value.Name, outputName, StringComparison.Ordinal))
                ?? throw new InvalidOperationException($"ONNX output tensor '{outputName}' was not returned by the model.");

            var outputTensor = output.AsTensor<float>();
            var detections = _outputParser.Parse(
                outputTensor,
                labels,
                threshold,
                _configuration.InputImageWidth,
                _configuration.InputImageHeight);
            inferenceWatch.Stop();
            timing.InferenceMilliseconds += inferenceWatch.Elapsed.TotalMilliseconds;
            timing.RecalculateTotal();

            return BuildInferenceResult(
                samplePath,
                goldenPath,
                brightness,
                threshold,
                inputName,
                outputName,
                labelMapEvidence,
                detections,
                timing);
        }
        catch (Exception ex)
        {
            if (inferenceWatch.IsRunning)
            {
                inferenceWatch.Stop();
                timing.InferenceMilliseconds += inferenceWatch.Elapsed.TotalMilliseconds;
            }

            timing.RecalculateTotal();
            return BuildUnavailableResult(
                samplePath,
                goldenPath,
                "ML_RUNTIME_ERROR",
                "ML Runtime Error",
                $"ONNX model loading or inference failed: {ex.Message}",
                ex.GetType().Name,
                timing);
        }
    }

    private AnalysisResult BuildInferenceResult(
        string samplePath,
        string? goldenPath,
        double brightness,
        double threshold,
        string inputName,
        string outputName,
        string labelMapEvidence,
        IReadOnlyList<ModelDetection> detections,
        InspectionTiming timing)
    {
        var nonOkDetections = detections
            .Where(detection => !IsOkLabel(detection.Label))
            .ToArray();
        var topDetection = nonOkDetections.FirstOrDefault() ?? detections.FirstOrDefault();
        var verdict = nonOkDetections.Length > 0 ? "NG" : "OK";
        var confidence = topDetection?.Confidence ?? threshold;
        var hotspot = topDetection?.BoundingBox ?? new Rect(0.45, 0.4, 0.14, 0.12);
        var suggestedDefect = nonOkDetections.Length > 0
            ? topDetection?.Label ?? "Anomaly"
            : "OK";
        var timestamp = DateTime.Now;

        var result = new AnalysisResult
        {
            SchemaVersion = AnalysisResult.CurrentSchemaVersion,
            InspectionId = InspectionContractIds.NewInspectionId(timestamp.ToUniversalTime()),
            SamplePath = samplePath,
            GoldenPath = goldenPath,
            InspectionEngine = Name,
            ModelVersion = Version,
            ModelFilePath = _configuration.ModelFilePath,
            ConfidenceThreshold = threshold,
            MeanBrightness = brightness,
            Timestamp = timestamp,
            SuggestedDefect = suggestedDefect,
            Verdict = verdict,
            DifferenceScore = confidence * 100.0,
            ReviewThreshold = threshold * 100.0,
            NgThreshold = threshold * 100.0,
            Confidence = confidence,
            DecisionMargin = (confidence - threshold) * 100.0,
            DecisionReason = nonOkDetections.Length > 0
                ? "ONNX model emitted one or more non-OK detections above the configured confidence threshold."
                : "ONNX model completed inference with no non-OK detections above the configured confidence threshold.",
            PolicyName = $"ML confidence >= {threshold:P0}",
            Hotspot = hotspot,
            SourceKind = "File",
            SourceFrameId = Path.GetFileName(samplePath),
            IsSimulatedSource = false,
            Evidence = new List<string>
            {
                "ONNX Runtime inference executed successfully.",
                $"Registry model ID: {RegistryValue(_configuration.ActiveModelId)}.",
                $"Registry validation status: {RegistryValue(_configuration.ActiveModelValidationStatus)}.",
                $"Model SHA-256: {RegistryValue(_configuration.ActiveModelSha256)}.",
                $"Model: {_configuration.ModelFilePath}.",
                $"Model version: {Version}.",
                $"Input tensor: {inputName}, size {_configuration.InputImageWidth}x{_configuration.InputImageHeight}, layout NCHW RGB normalized 0..1.",
                $"Output tensor: {outputName}.",
                $"Output parser: {_outputParser.Name}.",
                $"Confidence threshold: {threshold:P0}.",
                $"Detections above threshold: {detections.Count}.",
                labelMapEvidence,
            },
            Timing = timing,
        };
        ApplyWorkflowContext(result);
        var recipeLoad = RecipeService.LoadLatestRecipe(result.BoardProgram);
        if (recipeLoad.Recipe is { } recipe)
        {
            result.RecipeName = recipe.RecipeName;
            result.RecipeRevision = recipe.Revision;
            foreach (var warning in recipeLoad.Warnings)
                result.Evidence.Add($"Recipe warning: {warning}");
        }
        else
        {
            foreach (var warning in recipeLoad.Warnings)
                result.Evidence.Add($"Recipe warning: {warning}");
        }

        var sequence = 1;
        foreach (var detection in detections)
        {
            var judgment = IsOkLabel(detection.Label) ? "OK" : "NG";
            var widthPixels = detection.BoundingBox.Width * _configuration.InputImageWidth;
            var heightPixels = detection.BoundingBox.Height * _configuration.InputImageHeight;
            var matchingRoi = FindIntersectingRoi(recipeLoad.Recipe, detection.BoundingBox);
            var roiId = matchingRoi?.RoiId ?? "UNASSIGNED";
            result.Defects.Add(new DefectResult
            {
                DefectId = InspectionContractIds.NewDefectId(result.InspectionId, sequence++),
                ClassId = detection.ClassId,
                DefectType = detection.Label,
                Confidence = detection.Confidence,
                BoundingBox = detection.BoundingBox,
                XPosition = detection.BoundingBox.X + detection.BoundingBox.Width / 2.0,
                YPosition = detection.BoundingBox.Y + detection.BoundingBox.Height / 2.0,
                WidthPixels = widthPixels,
                HeightPixels = heightPixels,
                AreaPixels = widthPixels * heightPixels,
                Severity = ToDefectSeverity(judgment),
                SourceRoiId = roiId,
                RoiName = matchingRoi?.DisplayName ?? string.Empty,
                RoiType = matchingRoi?.RoiType ?? string.Empty,
                SideOrViewType = result.ViewType,
                RoiId = roiId,
                JudgmentStatus = judgment,
            });
        }

        return result;
    }

    private AnalysisResult BuildUnavailableResult(
        string samplePath,
        string? goldenPath,
        string errorCode,
        string defectType,
        string reason,
        string? errorType = null,
        InspectionTiming? timing = null)
    {
        var hotspot = new Rect(0.45, 0.4, 0.14, 0.12);
        var confidence = Math.Clamp(_configuration.ConfidenceThreshold, 0.0, 1.0);
        timing ??= new InspectionTiming();
        timing.RecalculateTotal();
        var timestamp = DateTime.Now;

        var result = new AnalysisResult
        {
            SchemaVersion = AnalysisResult.CurrentSchemaVersion,
            InspectionId = InspectionContractIds.NewInspectionId(timestamp.ToUniversalTime()),
            SamplePath = samplePath,
            GoldenPath = goldenPath,
            InspectionEngine = Name,
            ModelVersion = Version,
            ModelFilePath = _configuration.ModelFilePath,
            ConfidenceThreshold = confidence,
            MeanBrightness = CalculateBrightness(samplePath),
            Timestamp = timestamp,
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
            SourceKind = "File",
            SourceFrameId = Path.GetFileName(samplePath),
            IsSimulatedSource = false,
            ErrorCode = errorCode,
            ErrorMessage = reason,
            Evidence = new List<string>
            {
                $"Selected engine: {Name}.",
                $"Registry model ID: {RegistryValue(_configuration.ActiveModelId)}.",
                $"Registry validation status: {RegistryValue(_configuration.ActiveModelValidationStatus)}.",
                $"Model SHA-256: {RegistryValue(_configuration.ActiveModelSha256)}.",
                $"Configured model: {(_configuration.HasModelFile ? _configuration.ModelFilePath : "missing")}.",
                $"Model version: {Version}.",
                $"Confidence threshold: {confidence:P0}.",
                $"Input tensor: {TensorDisplay(_configuration.InputTensorName)}.",
                $"Output tensor: {TensorDisplay(_configuration.OutputTensorName)}.",
                $"Label map: {LabelMapDisplay()}.",
                $"Input size: {_configuration.InputImageWidth}x{_configuration.InputImageHeight}.",
                errorType is null ? "No ML inference was executed." : $"No ML inference result was accepted. Error type: {errorType}.",
            },
            Timing = timing,
        };
        ApplyWorkflowContext(result);

        var widthPixels = hotspot.Width * Math.Max(1, _configuration.InputImageWidth);
        var heightPixels = hotspot.Height * Math.Max(1, _configuration.InputImageHeight);
        result.Defects.Add(new DefectResult
        {
            DefectId = InspectionContractIds.NewDefectId(result.InspectionId, 1),
            DefectType = defectType,
            Confidence = result.Confidence,
            BoundingBox = hotspot,
            XPosition = hotspot.X + hotspot.Width / 2.0,
            YPosition = hotspot.Y + hotspot.Height / 2.0,
            WidthPixels = widthPixels,
            HeightPixels = heightPixels,
            AreaPixels = widthPixels * heightPixels,
            Severity = ToDefectSeverity(result.Verdict),
            SourceRoiId = "ROI-ML-ERROR",
            SideOrViewType = result.ViewType,
            RoiId = "ROI-ML-ERROR",
            JudgmentStatus = result.Verdict,
        });

        return result;
    }

    private static void ApplyWorkflowContext(AnalysisResult result)
    {
        try
        {
            var state = WorkflowState.Instance;
            result.StationId = state.StationId;
            result.BoardProgram = state.BoardProgram;
            result.BoardId = state.BoardProgram;
            result.OperatorId = state.OperatorWithRole;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"ONNX analysis workflow context could not be applied: {ex.Message}");
        }
    }

    private static string ToDefectSeverity(string judgmentStatus)
        => judgmentStatus.ToUpperInvariant() switch
        {
            "NG" => "Major",
            "OK" => "Info",
            _ => "Review",
        };

    private static RecipeRoi? FindIntersectingRoi(RecipeDefinition? recipe, Rect detectionBox)
    {
        if (recipe is null)
            return null;

        return recipe.Rois
            .Where(roi => roi.Enabled)
            .Select(roi => new
            {
                Roi = roi,
                Area = IntersectionArea(new Rect(roi.X, roi.Y, roi.Width, roi.Height), detectionBox),
            })
            .Where(candidate => candidate.Area > 0)
            .OrderByDescending(candidate => candidate.Area)
            .Select(candidate => candidate.Roi)
            .FirstOrDefault();
    }

    private static double IntersectionArea(Rect first, Rect second)
    {
        var left = Math.Max(first.Left, second.Left);
        var top = Math.Max(first.Top, second.Top);
        var right = Math.Min(first.Right, second.Right);
        var bottom = Math.Min(first.Bottom, second.Bottom);
        return right <= left || bottom <= top ? 0 : (right - left) * (bottom - top);
    }

    private DenseTensor<float> PreprocessImage(string path, int width, int height, InspectionTiming timing, out double brightness)
    {
        var loadWatch = Stopwatch.StartNew();
        BitmapSource? source;
        try
        {
            source = LoadBgra32(path);
        }
        finally
        {
            loadWatch.Stop();
            timing.ImageLoadMilliseconds += loadWatch.Elapsed.TotalMilliseconds;
        }

        if (source is null)
            throw new InvalidOperationException("Unable to load sample image for ONNX preprocessing.");

        var preprocessingWatch = Stopwatch.StartNew();
        brightness = CalculateBrightness(source);
        var resized = ResizeExact(source, width, height);
        var stride = width * 4;
        var count = stride * height;
        var pool = ArrayPool<byte>.Shared;
        var pixels = pool.Rent(count);
        var tensor = new DenseTensor<float>(new[] { 1, 3, height, width });

        try
        {
            resized.CopyPixels(pixels, stride, 0);
            for (var y = 0; y < height; y++)
            {
                var row = y * stride;
                for (var x = 0; x < width; x++)
                {
                    var offset = row + x * 4;
                    tensor[0, 0, y, x] = pixels[offset + 2] / 255f;
                    tensor[0, 1, y, x] = pixels[offset + 1] / 255f;
                    tensor[0, 2, y, x] = pixels[offset] / 255f;
                }
            }
        }
        finally
        {
            pool.Return(pixels);
            preprocessingWatch.Stop();
            timing.PreprocessingMilliseconds += preprocessingWatch.Elapsed.TotalMilliseconds;
        }

        return tensor;
    }

    private IReadOnlyDictionary<int, string> LoadLabelMap(out string evidence)
    {
        var labels = new SortedDictionary<int, string>(_configuration.BuiltInLabelMap);
        if (string.IsNullOrWhiteSpace(_configuration.LabelMapPath))
        {
            evidence = "Label map: built-in AOI fallback labels.";
            return labels;
        }

        if (!File.Exists(_configuration.LabelMapPath))
        {
            evidence = $"Label map file is missing; built-in labels used: {_configuration.LabelMapPath}.";
            return labels;
        }

        try
        {
            var extension = Path.GetExtension(_configuration.LabelMapPath);
            if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
                LoadJsonLabelMap(_configuration.LabelMapPath, labels);
            else
                LoadTextLabelMap(_configuration.LabelMapPath, labels);

            evidence = $"Label map loaded: {_configuration.LabelMapPath}.";
            return labels;
        }
        catch (Exception ex)
        {
            evidence = $"Label map could not be parsed; built-in labels used. {ex.Message}";
            return new SortedDictionary<int, string>(_configuration.BuiltInLabelMap);
        }
    }

    private static void LoadJsonLabelMap(string path, IDictionary<int, string> labels)
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

            return;
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
    }

    private static void LoadTextLabelMap(string path, IDictionary<int, string> labels)
    {
        var nextIndex = 0;
        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#", StringComparison.Ordinal))
                continue;

            var parts = line.Split(',', 2);
            if (parts.Length == 2 && int.TryParse(parts[0].Trim(), out var classId))
            {
                labels[classId] = parts[1].Trim();
                nextIndex = Math.Max(nextIndex, classId + 1);
            }
            else
            {
                labels[nextIndex++] = line;
            }
        }
    }

    private static string ResolveTensorName(string configuredName, IEnumerable<string> availableNames, string tensorKind)
    {
        var names = availableNames.ToArray();
        if (names.Length == 0)
            throw new InvalidOperationException($"The ONNX model exposes no {tensorKind} tensors.");

        if (string.IsNullOrWhiteSpace(configuredName))
            return names[0];

        if (names.Contains(configuredName, StringComparer.Ordinal))
            return configuredName;

        throw new InvalidOperationException($"Configured ONNX {tensorKind} tensor '{configuredName}' was not found. Available: {string.Join(", ", names)}.");
    }

    private string LabelMapDisplay()
    {
        if (string.IsNullOrWhiteSpace(_configuration.LabelMapPath))
            return "built-in AOI fallback labels";

        return File.Exists(_configuration.LabelMapPath)
            ? _configuration.LabelMapPath
            : $"missing ({_configuration.LabelMapPath})";
    }

    private static string RegistryValue(string value)
        => string.IsNullOrWhiteSpace(value) ? "not selected" : value;

    private static string TensorDisplay(string configuredName)
        => string.IsNullOrWhiteSpace(configuredName) ? "auto-detect first tensor" : configuredName;

    private static bool IsOkLabel(string label)
        => string.Equals(label.Trim(), "OK", StringComparison.OrdinalIgnoreCase);

    private static BitmapSource? LoadBgra32(string path)
    {
        if (!File.Exists(path))
            return null;

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

    private static BitmapSource ResizeExact(BitmapSource source, int width, int height)
    {
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawImage(source, new Rect(0, 0, width, height));
        }

        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(drawing);
        target.Freeze();

        var converted = new FormatConvertedBitmap(target, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        return converted;
    }

    private static double CalculateBrightness(string path)
    {
        var source = LoadBgra32(path);
        return source is null ? 0 : CalculateBrightness(source);
    }

    private static double CalculateBrightness(BitmapSource src)
    {
        var stride = src.PixelWidth * 4;
        var count = stride * src.PixelHeight;
        if (count == 0)
            return 0;

        var pool = ArrayPool<byte>.Shared;
        var pixels = pool.Rent(count);

        try
        {
            src.CopyPixels(pixels, stride, 0);
            double sum = 0;
            for (var i = 0; i < count; i += 4)
                sum += 0.114 * pixels[i] + 0.587 * pixels[i + 1] + 0.299 * pixels[i + 2];

            return sum / (count / 4.0);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"ONNX brightness estimate fallback used: {ex.Message}");
            return 0;
        }
        finally
        {
            pool.Return(pixels);
        }
    }
}
