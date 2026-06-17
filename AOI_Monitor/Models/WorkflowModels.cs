using System.Windows;

namespace AOI_Monitor.Models;

public enum DetectionPriority
{
    MinimizeFalsePositives,
    Balanced,
    MaximizeDefectRecall,
}

public class AnalysisResult
{
    public string SamplePath { get; set; } = "";
    public string? GoldenPath { get; set; }
    public string BoardProgram { get; set; } = "UNKNOWN";
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
    public string DefectType { get; set; } = "Unknown";
    public double Confidence { get; set; }
    public Rect BoundingBox { get; set; }
    public double XPosition { get; set; }
    public double YPosition { get; set; }
    public string SideOrViewType { get; set; } = "sample";
    public string RoiId { get; set; } = "ROI-UNASSIGNED";
    public string JudgmentStatus { get; set; } = "REVIEW";
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
