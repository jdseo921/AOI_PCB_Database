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
    public AuthenticationMode AuthenticationMode => CurrentUser.AuthenticationMode;
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
        AoiDatabase.AuditUserIdProvider = () => OperatorId;
        AoiDatabase.AuditUserRoleProvider = () => CurrentRole.ToString();
        AoiDatabase.AuditStationProvider = () => StationId;
    }

    public void SetCurrentUser(string userId, UserRole role)
        => SetCurrentUser(userId, role, CurrentUser.AuthenticationMode);

    public void SetCurrentUser(string userId, UserRole role, AuthenticationMode authenticationMode)
    {
        var previousUser = OperatorWithRole;
        CurrentUser.UserId = NormalizeUserId(userId);
        CurrentUser.Role = role;
        CurrentUser.AuthenticationMode = authenticationMode;
        AddEvent("LOGIN", $"User set to {OperatorWithRole}; authMode={authenticationMode}. Previous user: {previousUser}.");
        Notify();
    }

    public void SetAuthenticationMode(AuthenticationMode mode)
    {
        CurrentUser.AuthenticationMode = mode;
        AddEvent("AUTHENTICATION_MODE", $"Authentication mode active: {mode}.");
        Notify();
    }

    public void SetRole(UserRole role)
    {
        var previousRole = CurrentUser.Role;
        CurrentUser.Role = role;
        AddEvent("ROLE", $"Local role changed from {previousRole} to {role}.");
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
        if (string.IsNullOrWhiteSpace(result.BoardProgram) || result.BoardProgram == "UNKNOWN")
            result.BoardProgram = BoardProgram;
        if (string.IsNullOrWhiteSpace(result.BoardId) || result.BoardId == "UNKNOWN")
            result.BoardId = result.BoardProgram;
        if (string.IsNullOrWhiteSpace(result.LotId) || result.LotId == "UNKNOWN")
            result.LotId = "POC-LOT";
        if (string.IsNullOrWhiteSpace(result.StationId))
            result.StationId = StationId;
        if (string.IsNullOrWhiteSpace(result.OperatorId) || result.OperatorId == "UNKNOWN")
            result.OperatorId = OperatorWithRole;
        LastAnalysis = result;
        long? inspectionResultId = null;
        if (persist)
        {
            inspectionResultId = AoiDatabase.RecordInspectionResult(result);

            try
            {
                var path = MachineInterfaceExportService.ExportInspectionDecision(result);
                AddEvent(
                    "INTEGRATION",
                    $"Machine interface JSON exported: {Path.GetFileName(path)}",
                    relatedEntityType: "InspectionResult",
                    relatedEntityId: inspectionResultId?.ToString() ?? string.Empty,
                    relatedPath: path);
            }
            catch (Exception ex)
            {
                // latest_decision.json still holds the PREVIOUS board's verdict; anything
                // polling the machine-interface folder would act on stale data. Surface it
                // as an alarm so the operator sees it on the Monitor page, not only in the
                // audit log.
                AddEvent("INTEGRATION", $"Contract export failed: {ex.Message}");
                AlarmEventService.Raise(
                    AlarmSeverity.Alarm,
                    "Machine Interface Export",
                    "Machine-interface decision export failed; downstream consumers may still see the previous board's verdict.",
                    "Check the exports/machine_interface folder is writable, then re-run or re-save the inspection.",
                    engineerDetails: ex.Message,
                    idempotencyKey: "MACHINE_INTERFACE_EXPORT_FAILED");
            }

            QueueAutomaticTraceabilityUpload(result);
        }

        AddEvent(
            "ANALYSIS",
            persist
                ? $"Inspection saved -> score {result.DifferenceScore:F1}% ({result.Verdict})"
                : $"Inspection complete -> score {result.DifferenceScore:F1}% ({result.Verdict})",
            relatedEntityType: persist ? "InspectionResult" : "AnalysisResult",
            relatedEntityId: inspectionResultId?.ToString() ?? string.Empty,
            relatedPath: result.SamplePath);

        Notify();
    }

    private void QueueAutomaticTraceabilityUpload(AnalysisResult result)
    {
        var settings = MesIntegrationSettingsService.Load();
        if (!settings.AutoUploadEnabled || !settings.IsUploadCapable)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                var outcome = await TraceabilityUploadService.UploadInspectionResultAsync(result).ConfigureAwait(false);
                AddEvent(
                    outcome.Result.Accepted ? "MES_UPLOAD" : "MES_UPLOAD_ERROR",
                    $"Automatic MES traceability upload {outcome.Result.Status}: {outcome.Result.Message}",
                    relatedEntityType: "AnalysisResult",
                    relatedEntityId: result.InspectionId,
                    relatedPath: outcome.PayloadPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Net.Http.HttpRequestException)
            {
                AddEvent("MES_UPLOAD_ERROR", $"Automatic MES traceability upload failed safely: {ex.Message}");
            }
            catch (Exception ex)
            {
                // Last-resort catch: this task is fire-and-forget, so an escaped exception
                // (e.g. SqliteException on a locked DB) would otherwise vanish and auto-upload
                // would look healthy while silently dead.
                AddEvent("MES_UPLOAD_ERROR", $"Automatic MES traceability upload failed unexpectedly: {ex.Message}");
            }
        });
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

    public void AddEvent(
        string category,
        string message,
        string relatedEntityType = "",
        string relatedEntityId = "",
        string relatedPath = "")
    {
        var entry = new WorkflowEvent
        {
            Category = category,
            Message = message,
            Timestamp = DateTime.Now,
        };

        History.Add(entry);
        AoiDatabase.RecordWorkflowEvent(category, message, entry.Timestamp, OperatorWithRole);
        AoiDatabase.RecordAuditEvent(
            category,
            message,
            entry.Timestamp,
            operatorWithRole: OperatorWithRole,
            userId: OperatorId,
            userRole: CurrentRole.ToString(),
            stationId: StationId,
            relatedEntityType: relatedEntityType,
            relatedEntityId: relatedEntityId,
            relatedPath: relatedPath);

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
