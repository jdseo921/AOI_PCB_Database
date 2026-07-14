using System.Diagnostics;
using System.IO;
using System.Windows;
using AOI_Monitor.Data;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public sealed class LearnedPcbVisualInspectionEngine : IInspectionEngine
{
    private readonly string _modelId;
    private readonly ImageOnlyPcbLearningOptions? _options;

    public LearnedPcbVisualInspectionEngine()
        : this(string.Empty)
    {
    }

    public LearnedPcbVisualInspectionEngine(string modelId, ImageOnlyPcbLearningOptions? options = null)
    {
        _modelId = modelId?.Trim() ?? string.Empty;
        _options = options;
    }

    public string Name => ImageOnlyPcbLearningService.EngineName;
    public string Version => ImageOnlyPcbLearningService.EngineVersion;

    public AnalysisResult Analyze(string samplePath, string? goldenPath, DetectionPriority priority)
    {
        var timing = new InspectionTiming();
        var timestamp = DateTime.Now;
        var result = CreateBaseResult(samplePath, goldenPath, priority, timing, timestamp);

        if (string.IsNullOrWhiteSpace(_modelId))
        {
            result.ErrorCode = "IMAGE_LEARNING_MODEL_NOT_SELECTED";
            result.ErrorMessage = "No learned PCB visual model is selected.";
            result.DecisionReason = result.ErrorMessage;
            result.Timing.RecalculateTotal();
            return result;
        }

        try
        {
            var watch = Stopwatch.StartNew();
            var inspection = ImageOnlyPcbLearningService.InspectImagePath(
                _modelId,
                samplePath,
                _options,
                result.OperatorId,
                persist: false);
            watch.Stop();

            var model = AoiDatabase.GetLearnedPcbVisualModel(_modelId);
            timing.InferenceMilliseconds = watch.Elapsed.TotalMilliseconds;
            result.ModelVersion = model?.ModelVersion ?? Version;
            result.ModelFilePath = model?.Artifacts.FirstOrDefault()?.ArtifactPath ?? string.Empty;
            result.Verdict = inspection.InspectionResult.Verdict;
            result.DifferenceScore = inspection.ComparisonResult.DifferenceScore;
            result.ReviewThreshold = model?.LearnedThreshold ?? 0;
            result.NgThreshold = (model?.LearnedThreshold ?? 0) * 1.25;
            result.Confidence = inspection.Regions.Count > 0
                ? inspection.Regions.Max(region => region.Confidence)
                : result.Verdict == "OK" ? 0.82 : 0.58;
            result.ConfidenceThreshold = result.ReviewThreshold;
            result.DecisionReason = inspection.InspectionResult.DecisionReason;
            // The calibrated verdict comes from the anomaly score (a connectivity-agnostic order
            // statistic), while regions require a connected blob of at least the localization size.
            // A non-OK board can therefore trip the score with anomalous area too scattered to form
            // a single region — label that honestly instead of "No anomaly", which would contradict
            // the NG/REVIEW verdict.
            result.SuggestedDefect = inspection.Regions.Count > 0
                ? "Image-only Visual Anomaly"
                : result.Verdict == "OK"
                    ? "No Image-only Visual Anomaly"
                    : "Diffuse Image-only Anomaly (below localization size)";
            result.Hotspot = inspection.Regions.Count == 0
                ? new Rect(0, 0, 0, 0)
                : new Rect(
                    inspection.Regions[0].X,
                    inspection.Regions[0].Y,
                    inspection.Regions[0].Width,
                    inspection.Regions[0].Height);
            var evidence = new List<string>
            {
                $"Model: {_modelId}.",
            };
            if (model is not null)
                evidence.AddRange(LearnedVisualModelRegistryService.BuildEvidenceLines(model));
            evidence.Add($"Anomaly score: {inspection.InspectionResult.AnomalyScore:F2}; learned threshold: {result.ReviewThreshold:F2}.");
            evidence.Add($"Alignment offset: x={inspection.AlignmentOffsetX}, y={inspection.AlignmentOffsetY}, rotation={inspection.AlignmentRotationDegrees:F2} deg.");
            evidence.Add("Image-only inspection does not require manual defect labels or training bounding boxes.");
            result.Evidence = evidence;
            result.Defects = inspection.Regions
                .Select((region, index) => ToDefectResult(result, region, index + 1, model?.InputWidth ?? 0, model?.InputHeight ?? 0))
                .ToList();
            result.Timing.RecalculateTotal();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or NotSupportedException or System.IO.FileFormatException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            result.ErrorCode = "IMAGE_LEARNING_INSPECTION_FAILED";
            result.ErrorMessage = ex.Message;
            result.DecisionReason = "Image-only learned model inspection failed; review is required.";
            result.Evidence =
            [
                result.DecisionReason,
                ex.Message,
            ];
            result.Timing.RecalculateTotal();
        }

        return result;
    }

    private AnalysisResult CreateBaseResult(
        string samplePath,
        string? goldenPath,
        DetectionPriority priority,
        InspectionTiming timing,
        DateTime timestamp)
    {
        var result = new AnalysisResult
        {
            SchemaVersion = AnalysisResult.CurrentSchemaVersion,
            InspectionId = InspectionContractIds.NewInspectionId(timestamp.ToUniversalTime()),
            SamplePath = samplePath,
            GoldenPath = goldenPath,
            InspectionEngine = Name,
            ModelVersion = Version,
            Timestamp = timestamp,
            SuggestedDefect = "Image-only Visual Anomaly",
            Verdict = "REVIEW",
            DifferenceScore = 0,
            ReviewThreshold = 0,
            NgThreshold = 0,
            Confidence = 0.55,
            DecisionMargin = 0,
            DecisionReason = "Learned PCB visual model inspection has not completed.",
            PolicyName = ToPolicyDisplay(priority),
            SourceKind = "File",
            SourceFrameId = Path.GetFileName(samplePath),
            IsSimulatedSource = false,
            Evidence =
            [
                "Learned PCB visual model inspection is image-only and uses learned normal variation.",
            ],
            Timing = timing,
        };

        ApplyWorkflowContext(result);
        return result;
    }

    private static DefectResult ToDefectResult(
        AnalysisResult result,
        ImageLearningAnomalyRegion region,
        int sequence,
        int imageWidth,
        int imageHeight)
    {
        var boundingBox = new Rect(region.X, region.Y, region.Width, region.Height);
        var widthPixels = imageWidth > 0 ? region.Width * imageWidth : 0;
        var heightPixels = imageHeight > 0 ? region.Height * imageHeight : 0;
        return new DefectResult
        {
            DefectId = InspectionContractIds.NewDefectId(result.InspectionId, sequence),
            DefectType = "Image-only Visual Anomaly",
            Confidence = region.Confidence,
            BoundingBox = boundingBox,
            XPosition = boundingBox.X + boundingBox.Width / 2.0,
            YPosition = boundingBox.Y + boundingBox.Height / 2.0,
            WidthPixels = widthPixels,
            HeightPixels = heightPixels,
            AreaPixels = region.AreaPixels,
            Severity = region.Severity == "NG" ? "Major" : "Review",
            SourceRoiId = region.RegionId,
            SideOrViewType = result.ViewType,
            RoiId = region.RegionId,
            RoiType = region.RegionType,
            JudgmentStatus = region.Severity,
        };
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
            Trace.WriteLine($"Learned PCB visual inspection workflow context could not be applied: {ex.Message}");
        }
    }

    private static string ToPolicyDisplay(DetectionPriority priority) => priority switch
    {
        DetectionPriority.MinimizeFalsePositives => "Minimize False Positives",
        DetectionPriority.Balanced => "Balanced",
        DetectionPriority.MaximizeDefectRecall => "Maximize Defect Recall",
        _ => "Balanced",
    };
}
