using System.Globalization;

namespace AOI_Monitor.Models;

public enum AlarmSeverity
{
    Info = 0,
    Warning = 1,
    Alarm = 2,
    Critical = 3,
}

public enum AlarmAcknowledgementState
{
    Unacknowledged,
    Acknowledged,
    Resolved,
}

public enum AlarmSortOrder
{
    NewestFirst,
    OldestFirst,
    SeverityDescending,
    SeverityAscending,
}

public sealed class AlarmEvent
{
    public string AlarmId { get; set; } = string.Empty;
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public AlarmSeverity Severity { get; set; } = AlarmSeverity.Info;
    public string Source { get; set; } = "System";
    public string Message { get; set; } = string.Empty;
    public string RecommendedAction { get; set; } = "Review the event and continue only when the condition is understood.";
    public AlarmAcknowledgementState AcknowledgementState { get; set; } = AlarmAcknowledgementState.Unacknowledged;
    public DateTime? AcknowledgedAtUtc { get; set; }
    public string AcknowledgedBy { get; set; } = string.Empty;
    public DateTime? ResolvedAtUtc { get; set; }
    public string ResolvedBy { get; set; } = string.Empty;
    public string EngineerDetails { get; set; } = string.Empty;
    public bool IsSimulatedOrMock { get; set; }
    public bool IsOperatorVisible { get; set; } = true;
    public string IdempotencyKey { get; set; } = string.Empty;

    public bool IsActive => AcknowledgementState != AlarmAcknowledgementState.Resolved;
    public bool RequiresAcknowledgement => Severity is AlarmSeverity.Alarm or AlarmSeverity.Critical;
    public string TimestampLocal => TimestampUtc.ToLocalTime().ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    public string Summary => $"{TimestampLocal}  {Severity}  {Source}: {Message}";
    public string ActionSummary => $"Action: {RecommendedAction}";
}

public sealed class AlarmEventQuery
{
    public AlarmSeverity? Severity { get; set; }
    public string SourceContains { get; set; } = string.Empty;
    public bool ActiveOnly { get; set; }
    public bool OperatorVisibleOnly { get; set; } = true;
    public AlarmSortOrder SortOrder { get; set; } = AlarmSortOrder.SeverityDescending;
}

public sealed record AlarmEventExportResult(string Folder, string JsonPath, string CsvPath);
