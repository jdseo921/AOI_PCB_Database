using System.IO;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public sealed class WorkflowState
{
    public static WorkflowState Instance { get; } = new();

    public string? SampleImagePath { get; private set; }
    public string? GoldenImagePath { get; private set; }
    public AnalysisResult? LastAnalysis { get; private set; }
    public bool IsRecipeLocked { get; set; }
    public DetectionPriority DetectionPriority { get; private set; } = DetectionPriority.MinimizeFalsePositives;
    public TrainingSessionState Training { get; } = new();

    public List<WorkflowEvent> History { get; } = new();

    public event Action? StateChanged;

    private WorkflowState() { }

    public void SetSampleImage(string path)
    {
        SampleImagePath = path;
        AddEvent("INPUT", $"Sample image loaded: {Path.GetFileName(path)}");
        Notify();
    }

    public void SetGoldenImage(string path)
    {
        GoldenImagePath = path;
        AddEvent("INPUT", $"Golden image loaded: {Path.GetFileName(path)}");
        Notify();
    }

    public void SetAnalysis(AnalysisResult result)
    {
        LastAnalysis = result;
        AddEvent("ANALYSIS", $"Compared images -> score {result.DifferenceScore:F1}% ({result.Verdict})");
        Notify();
    }

    public void AddDisposition(string action)
    {
        AddEvent("DISPOSITION", action);
        Notify();
    }

    public void SetDetectionPriority(DetectionPriority priority)
    {
        DetectionPriority = priority;
        AddEvent("POLICY", $"Detection priority set to {ToDisplay(priority)}.");
        Notify();
    }

    public void QueueTrainingSample(string fileName)
    {
        Training.QueuedSamples++;
        AddEvent("TRAINING", $"Queued sample for training: {fileName}");
        Notify();
    }

    public void StartTraining()
    {
        Training.IsRunning = true;
        Training.LastStartedAt = DateTime.Now;
        AddEvent("TRAINING", "Training session started.");
        Notify();
    }

    public void StopTraining()
    {
        Training.IsRunning = false;
        AddEvent("TRAINING", "Training session stopped.");
        Notify();
    }

    public void CompleteTrainingEpoch(double validationScore)
    {
        Training.EpochsCompleted++;
        Training.LastValidationScore = validationScore;
        Training.LastCompletedAt = DateTime.Now;

        if (Training.QueuedSamples > 0)
            Training.QueuedSamples--;

        AddEvent("TRAINING", $"Epoch completed. Validation score {validationScore:F1}%.");
        Notify();
    }

    public void AddEvent(string category, string message)
    {
        History.Add(new WorkflowEvent
        {
            Category = category,
            Message = message,
            Timestamp = DateTime.Now,
        });
    }

    public static string ToDisplay(DetectionPriority priority) => priority switch
    {
        DetectionPriority.MinimizeFalsePositives => "Minimize False Positives",
        DetectionPriority.Balanced => "Balanced",
        DetectionPriority.MaximizeDefectRecall => "Maximize Defect Recall",
        _ => "Balanced",
    };

    private void Notify() => StateChanged?.Invoke();
}
