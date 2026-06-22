using System.IO;
using System.Text;
using System.Text.Json;
using AOI_Monitor.Data;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public static class AlarmEventService
{
    private const string SimulationBoundaryKey = "SIMULATION_BOUNDARY_ACTIVE";
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly List<AlarmEvent> Events = new();
    private static string _loadedStorageRoot = string.Empty;
    private static bool _loaded;

    public static event Action? AlarmEventsChanged;

    public static AlarmEvent Raise(
        AlarmSeverity severity,
        string source,
        string operatorMessage,
        string recommendedAction,
        string engineerDetails = "",
        bool isSimulatedOrMock = false,
        string idempotencyKey = "")
    {
        AlarmEvent alarm;
        var changed = false;
        lock (Sync)
        {
            EnsureLoadedForCurrentRoot();
            alarm = !string.IsNullOrWhiteSpace(idempotencyKey)
                ? Events.FirstOrDefault(item => item.IsActive && item.IdempotencyKey.Equals(idempotencyKey, StringComparison.OrdinalIgnoreCase)) ?? new AlarmEvent()
                : new AlarmEvent();

            var isNew = string.IsNullOrWhiteSpace(alarm.AlarmId);
            if (isNew)
            {
                alarm.AlarmId = $"ALM-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid().ToString("N")[..6]}";
                alarm.TimestampUtc = DateTime.UtcNow;
                alarm.IdempotencyKey = idempotencyKey.Trim();
                Events.Add(alarm);
                changed = true;
            }

            var safeMessage = ToOperatorMessage(operatorMessage, source);
            var safeAction = string.IsNullOrWhiteSpace(recommendedAction)
                ? "Review the alarm details and continue only when the condition is understood."
                : ToSingleLine(recommendedAction);
            var safeDetails = CrashReportService.RedactDiagnosticText(engineerDetails);

            changed |= alarm.Severity != severity ||
                !alarm.Source.Equals(NormalizeSource(source), StringComparison.Ordinal) ||
                !alarm.Message.Equals(safeMessage, StringComparison.Ordinal) ||
                !alarm.RecommendedAction.Equals(safeAction, StringComparison.Ordinal) ||
                !alarm.EngineerDetails.Equals(safeDetails, StringComparison.Ordinal) ||
                alarm.IsSimulatedOrMock != isSimulatedOrMock;

            alarm.Severity = severity;
            alarm.Source = NormalizeSource(source);
            alarm.Message = safeMessage;
            alarm.RecommendedAction = safeAction;
            alarm.EngineerDetails = safeDetails;
            alarm.IsSimulatedOrMock = isSimulatedOrMock;
            alarm.IsOperatorVisible = true;

            if (changed)
                PersistUnsafe();
        }

        if (changed)
            AlarmEventsChanged?.Invoke();

        return alarm;
    }

    public static AlarmEvent RaiseFromException(
        string operationName,
        string source,
        Exception exception,
        AlarmSeverity severity = AlarmSeverity.Alarm,
        string recommendedAction = "",
        string reportPath = "")
    {
        var action = string.IsNullOrWhiteSpace(recommendedAction)
            ? "Retry the action once. If it repeats, stop the workflow and export a support bundle for Engineering/Admin review."
            : recommendedAction;
        var report = string.IsNullOrWhiteSpace(reportPath) ? string.Empty : $" Diagnostic report: {reportPath}.";
        return Raise(
            severity,
            source,
            PlainLanguageGlossaryService.SafeOperatorError(operationName) + report,
            action,
            CrashReportService.RedactDiagnosticText(exception.ToString()),
            idempotencyKey: string.Empty);
    }

    public static void SetSimulationBoundaryWarning(bool active, string detail, string operatorId = "SYSTEM")
    {
        if (active)
        {
            Raise(
                AlarmSeverity.Warning,
                "SimulationBoundary",
                $"SIMULATED / MOCK / NOT VALIDATED source active. {detail}",
                "Keep all customer and factory-readiness evidence labeled as simulated until real hardware/MES validation is recorded.",
                detail,
                isSimulatedOrMock: true,
                idempotencyKey: SimulationBoundaryKey);
            return;
        }

        ResolveByKey(SimulationBoundaryKey, operatorId);
    }

    public static bool Acknowledge(string alarmId, string operatorId)
    {
        lock (Sync)
        {
            EnsureLoadedForCurrentRoot();
            var alarm = Events.FirstOrDefault(item => item.AlarmId.Equals(alarmId, StringComparison.OrdinalIgnoreCase));
            if (alarm is null || alarm.AcknowledgementState == AlarmAcknowledgementState.Resolved)
                return false;

            alarm.AcknowledgementState = AlarmAcknowledgementState.Acknowledged;
            alarm.AcknowledgedAtUtc = DateTime.UtcNow;
            alarm.AcknowledgedBy = string.IsNullOrWhiteSpace(operatorId) ? "UNKNOWN" : operatorId.Trim();
            PersistUnsafe();
        }

        AlarmEventsChanged?.Invoke();
        return true;
    }

    public static bool Resolve(string alarmId, string operatorId)
    {
        lock (Sync)
        {
            EnsureLoadedForCurrentRoot();
            var alarm = Events.FirstOrDefault(item => item.AlarmId.Equals(alarmId, StringComparison.OrdinalIgnoreCase));
            if (alarm is null)
                return false;

            ResolveUnsafe(alarm, operatorId);
            PersistUnsafe();
        }

        AlarmEventsChanged?.Invoke();
        return true;
    }

    public static IReadOnlyList<AlarmEvent> GetEvents(AlarmEventQuery? query = null)
    {
        lock (Sync)
        {
            EnsureLoadedForCurrentRoot();
            query ??= new AlarmEventQuery();
            IEnumerable<AlarmEvent> result = Events;
            if (query.ActiveOnly)
                result = result.Where(item => item.IsActive);
            if (query.OperatorVisibleOnly)
                result = result.Where(item => item.IsOperatorVisible);
            if (query.Severity is { } severity)
                result = result.Where(item => item.Severity == severity);
            if (!string.IsNullOrWhiteSpace(query.SourceContains))
                result = result.Where(item => item.Source.Contains(query.SourceContains.Trim(), StringComparison.OrdinalIgnoreCase));

            result = query.SortOrder switch
            {
                AlarmSortOrder.OldestFirst => result.OrderBy(item => item.TimestampUtc),
                AlarmSortOrder.SeverityAscending => result.OrderBy(item => item.Severity).ThenByDescending(item => item.TimestampUtc),
                AlarmSortOrder.SeverityDescending => result.OrderByDescending(item => item.Severity).ThenByDescending(item => item.TimestampUtc),
                _ => result.OrderByDescending(item => item.TimestampUtc),
            };

            return result.Select(Clone).ToArray();
        }
    }

    public static IReadOnlyList<AlarmEvent> GetActiveCriticalAlarms()
        => GetEvents(new AlarmEventQuery { ActiveOnly = true, Severity = AlarmSeverity.Critical, SortOrder = AlarmSortOrder.NewestFirst });

    public static IReadOnlyList<AlarmEvent> GetUnacknowledgedAlarmLevelEvents()
        => GetEvents(new AlarmEventQuery { ActiveOnly = true, SortOrder = AlarmSortOrder.SeverityDescending })
            .Where(item => item.Severity == AlarmSeverity.Alarm && item.AcknowledgementState == AlarmAcknowledgementState.Unacknowledged)
            .ToArray();

    public static AlarmEventExportResult ExportLog(string? outputRoot = null, bool activeOnly = false)
    {
        var events = GetEvents(new AlarmEventQuery { ActiveOnly = activeOnly, OperatorVisibleOnly = false, SortOrder = AlarmSortOrder.NewestFirst });
        var folder = string.IsNullOrWhiteSpace(outputRoot)
            ? Path.Combine(AoiDatabase.StorageRoot, "exports", "alarm_events", $"alarm_log_{DateTime.UtcNow:yyyyMMdd_HHmmss}")
            : outputRoot.Trim();
        Directory.CreateDirectory(folder);

        var jsonPath = Path.Combine(folder, "alarm_events.json");
        var csvPath = Path.Combine(folder, "alarm_events.csv");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(events, JsonOptions), Encoding.UTF8);
        File.WriteAllText(csvPath, BuildCsv(events), Encoding.UTF8);
        ExportVerificationService.RecordVerifiedExport("AlarmEventLog", folder, "OK", WorkflowState.Instance.OperatorWithRole);
        return new AlarmEventExportResult(folder, jsonPath, csvPath);
    }

    public static void ClearForTests()
    {
        lock (Sync)
        {
            Events.Clear();
            _loadedStorageRoot = AoiDatabase.StorageRoot;
            _loaded = true;
            PersistUnsafe();
        }

        AlarmEventsChanged?.Invoke();
    }

    public static string ToOperatorMessage(string? rawMessage, string operationName = "operation")
    {
        var message = ToSingleLine(CrashReportService.RedactDiagnosticText(rawMessage));
        if (string.IsNullOrWhiteSpace(message))
            return "The system needs attention. The app remains available.";

        if (LooksTechnical(message))
            return $"The {operationName} action needs attention. The app remains available. Open Details as Engineer/Admin or export the alarm log for technical review.";

        return message;
    }

    private static void ResolveByKey(string key, string operatorId)
    {
        var changed = false;
        lock (Sync)
        {
            EnsureLoadedForCurrentRoot();
            foreach (var alarm in Events.Where(item => item.IsActive && item.IdempotencyKey.Equals(key, StringComparison.OrdinalIgnoreCase)))
            {
                ResolveUnsafe(alarm, operatorId);
                changed = true;
            }

            if (changed)
                PersistUnsafe();
        }

        if (changed)
            AlarmEventsChanged?.Invoke();
    }

    private static void ResolveUnsafe(AlarmEvent alarm, string operatorId)
    {
        if (alarm.AcknowledgedAtUtc is null)
        {
            alarm.AcknowledgedAtUtc = DateTime.UtcNow;
            alarm.AcknowledgedBy = string.IsNullOrWhiteSpace(operatorId) ? "UNKNOWN" : operatorId.Trim();
        }

        alarm.AcknowledgementState = AlarmAcknowledgementState.Resolved;
        alarm.ResolvedAtUtc = DateTime.UtcNow;
        alarm.ResolvedBy = string.IsNullOrWhiteSpace(operatorId) ? "UNKNOWN" : operatorId.Trim();
    }

    private static void EnsureLoadedForCurrentRoot()
    {
        var root = AoiDatabase.StorageRoot;
        if (_loaded && _loadedStorageRoot.Equals(root, StringComparison.OrdinalIgnoreCase))
            return;

        Events.Clear();
        _loadedStorageRoot = root;
        _loaded = true;

        var path = SnapshotPath();
        if (!File.Exists(path))
            return;

        try
        {
            var loaded = JsonSerializer.Deserialize<List<AlarmEvent>>(File.ReadAllText(path), JsonOptions);
            if (loaded is not null)
                Events.AddRange(loaded.Where(item => !string.IsNullOrWhiteSpace(item.AlarmId)));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"Alarm event state load failed; clearing in-memory alarm cache: {ex.Message}");
            Events.Clear();
        }
    }

    private static void PersistUnsafe()
    {
        try
        {
            var path = SnapshotPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(Events, JsonOptions), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"Alarm event persistence failed: {ex.Message}");
        }
    }

    private static string SnapshotPath()
        => Path.Combine(AoiDatabase.StorageRoot, "exports", "alarm_events", "alarm_events_state.json");

    private static string BuildCsv(IReadOnlyList<AlarmEvent> events)
    {
        var sb = new StringBuilder();
        sb.AppendLine("TimestampUtc,Severity,Source,Message,RecommendedAction,AcknowledgementState,AcknowledgedAtUtc,AcknowledgedBy,ResolvedAtUtc,ResolvedBy,IsSimulatedOrMock,EngineerDetails");
        foreach (var alarm in events)
        {
            sb.AppendLine(string.Join(",",
                Csv(alarm.TimestampUtc.ToString("O")),
                Csv(alarm.Severity.ToString()),
                Csv(alarm.Source),
                Csv(alarm.Message),
                Csv(alarm.RecommendedAction),
                Csv(alarm.AcknowledgementState.ToString()),
                Csv(alarm.AcknowledgedAtUtc?.ToString("O") ?? string.Empty),
                Csv(alarm.AcknowledgedBy),
                Csv(alarm.ResolvedAtUtc?.ToString("O") ?? string.Empty),
                Csv(alarm.ResolvedBy),
                Csv(alarm.IsSimulatedOrMock.ToString()),
                Csv(alarm.EngineerDetails)));
        }

        return sb.ToString();
    }

    private static string Csv(string? value)
        => "\"" + (value ?? string.Empty).Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string NormalizeSource(string source)
        => string.IsNullOrWhiteSpace(source) ? "System" : ToSingleLine(source);

    private static string ToSingleLine(string? value)
        => string.Join(" ", (value ?? string.Empty).Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();

    private static bool LooksTechnical(string message)
        => message.Contains("Exception", StringComparison.OrdinalIgnoreCase) ||
           message.Contains("StackTrace", StringComparison.OrdinalIgnoreCase) ||
           message.Contains(" at ", StringComparison.OrdinalIgnoreCase) ||
           message.Contains("System.", StringComparison.OrdinalIgnoreCase) ||
           message.Contains(":line ", StringComparison.OrdinalIgnoreCase);

    private static AlarmEvent Clone(AlarmEvent source)
        => new()
        {
            AlarmId = source.AlarmId,
            TimestampUtc = source.TimestampUtc,
            Severity = source.Severity,
            Source = source.Source,
            Message = source.Message,
            RecommendedAction = source.RecommendedAction,
            AcknowledgementState = source.AcknowledgementState,
            AcknowledgedAtUtc = source.AcknowledgedAtUtc,
            AcknowledgedBy = source.AcknowledgedBy,
            ResolvedAtUtc = source.ResolvedAtUtc,
            ResolvedBy = source.ResolvedBy,
            EngineerDetails = source.EngineerDetails,
            IsSimulatedOrMock = source.IsSimulatedOrMock,
            IsOperatorVisible = source.IsOperatorVisible,
            IdempotencyKey = source.IdempotencyKey,
        };
}
