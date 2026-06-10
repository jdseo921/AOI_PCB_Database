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
    public double DifferenceScore { get; set; }
    public double MeanBrightness { get; set; }
    public double ReviewThreshold { get; set; }
    public double NgThreshold { get; set; }
    public double Confidence { get; set; }
    public double DecisionMargin { get; set; }
    public string DecisionReason { get; set; } = "Not enough data.";
    public string ModelVersion { get; set; } = "AOI_AI_0.8.1";
    public string PolicyName { get; set; } = "Minimize False Positives";
    public List<string> Evidence { get; set; } = new();
    public string Verdict { get; set; } = "REVIEW";
    public string SuggestedDefect { get; set; } = "Unknown";
    public Rect Hotspot { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.Now;
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
