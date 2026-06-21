namespace AOI_Monitor.Models;

public enum CentralSyncMode
{
    Disabled,
    FileDrop,
    RestApi,
    PostgreSqlBoundary,
}

public enum CentralSyncStatus
{
    Pending,
    Sent,
    Failed,
    Skipped,
}

public sealed class CentralSyncSettings
{
    public CentralSyncMode Mode { get; set; } = CentralSyncMode.Disabled;
    public string EndpointOrFolder { get; set; } = string.Empty;
    public string StationId { get; set; } = Environment.MachineName;
    public int SyncIntervalSeconds { get; set; } = 300;
    public bool IncludeImages { get; set; }
    public bool RedactOperatorId { get; set; } = true;
    public bool RedactImagePaths { get; set; } = true;
    public bool RedactEndpointInExports { get; set; } = true;
    public int MaxRetryCount { get; set; } = 5;
    public int RetryBackoffMs { get; set; } = 5000;
    public string SharedSecret { get; set; } = string.Empty;
}

public sealed class CentralSyncPayload
{
    public string SchemaVersion { get; set; } = "central-sync/v1";
    public string ItemType { get; set; } = string.Empty;
    public string ItemId { get; set; } = string.Empty;
    public string StationId { get; set; } = string.Empty;
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
    public Dictionary<string, object?> Data { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> IncludedFiles { get; set; } = new();
    public List<string> MappingNotes { get; set; } = new();
}

public sealed record CentralSyncQueueRecord(
    long Id,
    DateTime CreatedAtUtc,
    DateTime? LastAttemptAtUtc,
    DateTime? NextAttemptAtUtc,
    string ItemType,
    string ItemId,
    string PayloadJson,
    string PayloadPath,
    string EndpointOrFolder,
    string StationId,
    int RetryCount,
    int MaxRetryCount,
    string Status,
    string LastError);

public sealed record CentralSyncAttemptRecord(
    long Id,
    long QueueId,
    DateTime AttemptedAtUtc,
    string Mode,
    string EndpointOrFolder,
    string Status,
    string Message);

public sealed record CentralSyncRetrySummary(
    int Attempted,
    int Sent,
    int Failed,
    int Skipped);

public sealed record CentralSyncReportExport(
    string JsonPath,
    string HtmlPath);

public sealed class CentralSyncReadinessSummary
{
    public string Status { get; set; } = "Central Sync Disabled";
    public string Mode { get; set; } = CentralSyncMode.Disabled.ToString();
    public int PendingCount { get; set; }
    public int FailedCount { get; set; }
    public int SentCount { get; set; }
    public int SkippedCount { get; set; }
    public List<string> Messages { get; set; } = new();
}
