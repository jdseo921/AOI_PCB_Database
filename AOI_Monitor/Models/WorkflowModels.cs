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
