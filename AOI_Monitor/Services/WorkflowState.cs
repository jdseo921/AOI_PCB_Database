using System.IO;
using AOI_Monitor.Data;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public sealed class WorkflowState
{
    private const int MaxHistoryEntries = 500;

    public static WorkflowState Instance { get; } = new();

    public string? SampleImagePath { get; private set; }
    public string? GoldenImagePath { get; private set; }
    public AnalysisResult? LastAnalysis { get; private set; }
    public string StationId { get; } = "AOI-LIB-01";
    public string OperatorId { get; set; } = "Engineer01";
    public string BoardProgram { get; } = "TBOX-MAIN";
    public string ModelVersion { get; } = "PIXEL_DIFF_0.1";
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
        result.BoardProgram = BoardProgram;
        result.OperatorId = OperatorId;
        LastAnalysis = result;
        AoiDatabase.RecordInspectionResult(result);
        AddEvent("ANALYSIS", $"Compared images -> score {result.DifferenceScore:F1}% ({result.Verdict})");

        try
        {
            var path = MachineInterfaceExportService.ExportInspectionDecision(result);
            AddEvent("INTEGRATION", $"Machine interface JSON exported: {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            AddEvent("INTEGRATION", $"Contract export failed: {ex.Message}");
        }

        Notify();
    }

    public void AddDisposition(string action)
    {
        AddEvent("DISPOSITION", action);
        Notify();
    }

    public bool TrySetDetectionPriority(DetectionPriority priority, out string message)
    {
        if (IsRecipeLocked)
        {
            message = "Recipe is locked. Unlock recipe before changing detection priority.";
            return false;
        }

        DetectionPriority = priority;
        message = $"Detection priority set to {ToDisplay(priority)}.";
        AddEvent("POLICY", message);
        Notify();
        return true;
    }

    public void SetDetectionPriority(DetectionPriority priority)
    {
        TrySetDetectionPriority(priority, out _);
    }

    public void QueueTrainingSample(string fileName)
    {
        Training.QueuedSamples++;
        AddEvent("TRAINING_SET_EXPORT", $"Queued sample for training set export: {fileName}");
        Notify();
    }

    public void StartTraining()
    {
        Training.IsRunning = true;
        Training.LastStartedAt = DateTime.Now;
        AddEvent("TRAINING_SET_EXPORT", "Training set export preparation started.");
        Notify();
    }

    public void StopTraining()
    {
        Training.IsRunning = false;
        AddEvent("TRAINING_SET_EXPORT", "Training set export preparation stopped.");
        Notify();
    }

    public void CompleteTrainingEpoch(double validationScore)
    {
        Training.EpochsCompleted++;
        Training.LastValidationScore = validationScore;
        Training.LastCompletedAt = DateTime.Now;

        if (Training.QueuedSamples > 0)
            Training.QueuedSamples--;

        AddEvent("TRAINING_SET_EXPORT", $"Training set list check completed. Quality score {validationScore:F1}%.");
        Notify();
    }

    public void AddEvent(string category, string message)
    {
        var entry = new WorkflowEvent
        {
            Category = category,
            Message = message,
            Timestamp = DateTime.Now,
        };

        History.Add(entry);
        AoiDatabase.RecordWorkflowEvent(category, message, entry.Timestamp, OperatorId);

        if (History.Count > MaxHistoryEntries)
            History.RemoveRange(0, History.Count - MaxHistoryEntries);
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
