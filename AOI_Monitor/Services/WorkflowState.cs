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
    public CurrentUser CurrentUser { get; } = new();
    public string OperatorId
    {
        get => CurrentUser.UserId;
        set => CurrentUser.UserId = NormalizeUserId(value);
    }
    public UserRole CurrentRole => CurrentUser.Role;
    public string BoardProgram { get; } = "TBOX-MAIN";
    public string ModelVersion { get; } = "PIXEL_DIFF_0.1";
    public bool IsRecipeLocked { get; set; }
    public DetectionPriority DetectionPriority { get; private set; } = DetectionPriority.MinimizeFalsePositives;
    public TrainingSessionState Training { get; } = new();

    public List<WorkflowEvent> History { get; } = new();

    public event Action? StateChanged;

    private WorkflowState()
    {
        AoiDatabase.AuditOperatorProvider = () => OperatorWithRole;
    }

    public void SetCurrentUser(string userId, UserRole role)
    {
        CurrentUser.UserId = NormalizeUserId(userId);
        CurrentUser.Role = role;
        AddEvent("LOGIN", $"Local user set to {OperatorWithRole}. MES authentication is Stage 4 planned.");
        Notify();
    }

    public void SetRole(UserRole role)
    {
        CurrentUser.Role = role;
        AddEvent("ROLE", $"Local role changed to {role}.");
        Notify();
    }

    public bool HasPermission(Func<UserRole, bool> permission) => permission(CurrentRole);

    public bool TryAuthorize(Func<UserRole, bool> permission, string action, out string message)
    {
        if (permission(CurrentRole))
        {
            message = string.Empty;
            return true;
        }

        message = RoleAuthorization.DeniedMessage(CurrentRole, action);
        AddEvent("ACCESS_DENIED", message);
        Notify();
        return false;
    }

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
        => SetAnalysis(result, persist: true);

    public void SetAnalysis(AnalysisResult result, bool persist)
    {
        result.BoardProgram = BoardProgram;
        result.OperatorId = OperatorWithRole;
        LastAnalysis = result;
        if (persist)
        {
            AoiDatabase.RecordInspectionResult(result);

            try
            {
                var path = MachineInterfaceExportService.ExportInspectionDecision(result);
                AddEvent("INTEGRATION", $"Machine interface JSON exported: {Path.GetFileName(path)}");
            }
            catch (Exception ex)
            {
                AddEvent("INTEGRATION", $"Contract export failed: {ex.Message}");
            }
        }

        AddEvent(
            "ANALYSIS",
            persist
                ? $"Inspection saved -> score {result.DifferenceScore:F1}% ({result.Verdict})"
                : $"Inspection complete -> score {result.DifferenceScore:F1}% ({result.Verdict})");

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
        AoiDatabase.RecordWorkflowEvent(category, message, entry.Timestamp, OperatorWithRole);

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

    public string OperatorWithRole => CurrentUser.AuditId;

    private static string NormalizeUserId(string? userId)
        => string.IsNullOrWhiteSpace(userId) ? "UNKNOWN" : userId.Trim();
}
