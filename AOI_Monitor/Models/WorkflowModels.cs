using System.Windows;

using System.Security.Cryptography;

namespace AOI_Monitor.Models;

public enum DetectionPriority
{
    MinimizeFalsePositives,
    Balanced,
    MaximizeDefectRecall,
}

public class AnalysisResult
{
    public const string CurrentSchemaVersion = "inspection-result/v1";

    public string SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string InspectionId { get; set; } = InspectionContractIds.NewInspectionId();
    public string SamplePath { get; set; } = "";
    public string? GoldenPath { get; set; }
    public string BoardProgram { get; set; } = "UNKNOWN";
    public string BoardId { get; set; } = "UNKNOWN";
    public string LotId { get; set; } = "UNKNOWN";
    public string StationId { get; set; } = "AOI-LIB-01";
    public string ViewType { get; set; } = "Top";
    public string SourceFrameId { get; set; } = string.Empty;
    public string SourceKind { get; set; } = "File";
    public bool IsSimulatedSource { get; set; }
    public string RecipeName { get; set; } = string.Empty;
    public string RecipeRevision { get; set; } = string.Empty;
    public string ErrorCode { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public string OperatorId { get; set; } = "UNKNOWN";
    public string InspectionEngine { get; set; } = "Pixel Difference Prototype Engine";
    public double DifferenceScore { get; set; }
    public double MeanBrightness { get; set; }
    public double ReviewThreshold { get; set; }
    public double NgThreshold { get; set; }
    public double Confidence { get; set; }
    public double DecisionMargin { get; set; }
    public string DecisionReason { get; set; } = "Not enough data.";
    public string ModelVersion { get; set; } = "PIXEL_DIFF_0.1";
    public string ModelFilePath { get; set; } = string.Empty;
    public double ConfidenceThreshold { get; set; }
    public string PolicyName { get; set; } = "Minimize False Positives";
    public List<string> Evidence { get; set; } = new();
    public List<DefectResult> Defects { get; set; } = new();
    public InspectionTiming Timing { get; set; } = new();
    public string Verdict { get; set; } = "REVIEW";
    public string SuggestedDefect { get; set; } = "Unknown";
    public Rect Hotspot { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.Now;
}

public class InspectionTiming
{
    public double ImageLoadMilliseconds { get; set; }
    public double PreprocessingMilliseconds { get; set; }
    public double InferenceMilliseconds { get; set; }
    public double OverlayRenderingMilliseconds { get; set; }
    public double TotalInspectionMilliseconds { get; set; }

    public bool IsOverOneSecond => TotalInspectionMilliseconds > 1000.0;

    public void RecalculateTotal()
    {
        TotalInspectionMilliseconds =
            ImageLoadMilliseconds +
            PreprocessingMilliseconds +
            InferenceMilliseconds +
            OverlayRenderingMilliseconds;
    }
}

public class DefectResult
{
    public string DefectId { get; set; } = InspectionContractIds.NewDefectId();
    public int? ClassId { get; set; }
    public string RefDes { get; set; } = string.Empty;
    public string Severity { get; set; } = "Review";
    public string DefectType { get; set; } = "Unknown";
    public double Confidence { get; set; }
    public Rect BoundingBox { get; set; }
    public double XPosition { get; set; }
    public double YPosition { get; set; }
    public double WidthPixels { get; set; }
    public double HeightPixels { get; set; }
    public double AreaPixels { get; set; }
    public double? BoardXMillimeters { get; set; }
    public double? BoardYMillimeters { get; set; }
    public double? HeightMicrons { get; set; }
    public double? VolumeEstimate { get; set; }
    public string SourceRoiId { get; set; } = string.Empty;
    public string RoiName { get; set; } = string.Empty;
    public string RoiType { get; set; } = string.Empty;
    public string SideOrViewType { get; set; } = "sample";
    public string RoiId { get; set; } = "ROI-UNASSIGNED";
    public string JudgmentStatus { get; set; } = "REVIEW";
}

public static class InspectionContractIds
{
    public static string NewInspectionId(DateTime? timestamp = null)
        => $"AOI-{(timestamp ?? DateTime.UtcNow):yyyyMMddHHmmssfff}-{RandomSuffix(4)}";

    public static string NewDefectId(string? inspectionId = null, int sequence = 1)
    {
        var prefix = string.IsNullOrWhiteSpace(inspectionId) ? "DEF" : inspectionId.Trim();
        return $"{prefix}-D{sequence:000}";
    }

    private static string RandomSuffix(int bytes)
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();
}

public class WorkflowEvent
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string Category { get; set; } = "INFO";
    public string Message { get; set; } = "";
}

public class TrainingSessionState
{
    public bool IsRunning { get; set; }
    public int QueuedSamples { get; set; }
    public int EpochsCompleted { get; set; }
    public DateTime? LastStartedAt { get; set; }
    public DateTime? LastCompletedAt { get; set; }
    public double LastValidationScore { get; set; }
}
