using System.Reflection;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

/// <summary>
/// Options for the headless Stage 1 batch-inspection soak run. All failure thresholds
/// are configurable here (AGENTS.md rule 12); the CLI exposes the operator-relevant ones.
/// </summary>
public sealed record BatchSoakOptions
{
    /// <summary>Folder of PNG/JPG/JPEG images processed by every pass.</summary>
    public string ImageFolder { get; init; } = string.Empty;

    /// <summary>Optional ground-truth manifest CSV (same formats as the AI Model Test screen).</summary>
    public string? ManifestPath { get; init; }

    /// <summary>Requested soak duration; the run also ends when <see cref="MaxPasses"/> is reached.</summary>
    public TimeSpan Duration { get; init; } = TimeSpan.FromHours(8);

    /// <summary>Idle delay between passes.</summary>
    public TimeSpan DelayBetweenPasses { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Engine key; blank selects the configured engine (matches InspectionEngineFactory.Create).</summary>
    public string EngineKey { get; init; } = string.Empty;

    /// <summary>Detection priority passed to every analysis; recorded in the evidence.</summary>
    public DetectionPriority DetectionPriority { get; init; } = DetectionPriority.Balanced;

    /// <summary>Folder that receives the HTML/JSON/CSV evidence artifacts.</summary>
    public string OutputFolder { get; init; } = string.Empty;

    /// <summary>Operator recorded in the evidence and export-history rows.</summary>
    public string OperatorId { get; init; } = "UNKNOWN";

    /// <summary>Board program recorded in the evidence.</summary>
    public string BoardModel { get; init; } = "TBOX-MAIN";

    /// <summary>Lot identifier recorded in the evidence.</summary>
    public string LotId { get; init; } = "BATCH-SOAK";

    /// <summary>Optional pass cap for smoke runs and tests.</summary>
    public int? MaxPasses { get; init; }

    /// <summary>When true (default), every pass is persisted as a SQLite batch test run.</summary>
    public bool PersistBatchRuns { get; init; } = true;

    /// <summary>Watchdog timeout for a single image inspection; exceeding it fails the run as StuckIteration.</summary>
    public TimeSpan StuckImageTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Managed-memory trend slope above which (together with the floor) the run fails.</summary>
    public double MemorySlopeFailMegabytesPerHour { get; init; } = 64;

    /// <summary>Absolute managed-memory growth floor that must also be exceeded for a trend failure.</summary>
    public double MemoryGrowthFailFloorMegabytes { get; init; } = 256;

    /// <summary>Warm-up period before the memory trend may fail the run; null means Duration / 4.</summary>
    public TimeSpan? MemoryTrendWarmup { get; init; }
}

/// <summary>Per-pass progress snapshot reported to the driver.</summary>
public sealed record BatchSoakProgress(
    int PassNumber,
    TimeSpan Elapsed,
    TimeSpan Remaining,
    int ImagesProcessed,
    int TotalErrors,
    double WorkingSetMegabytes,
    string Message);

/// <summary>One completed (possibly partial) batch pass with its stability metrics.</summary>
public sealed record BatchSoakPassRecord(
    int PassNumber,
    DateTime StartedAtUtc,
    double DurationMilliseconds,
    int ImagesProcessed,
    int OkCount,
    int NgCount,
    int ReviewCount,
    int ErrorCount,
    double AverageInspectionMilliseconds,
    double MaxInspectionMilliseconds,
    int CountOverOneSecond,
    double ManagedMemoryMegabytes,
    double WorkingSetMegabytes,
    int HandleCount,
    int ThreadCount,
    double DatabaseSizeMegabytes,
    long? BatchRunId,
    string FirstError);

/// <summary>One managed-memory trend sample (x = monotonic elapsed hours at sampling time).</summary>
public sealed record BatchSoakMemoryTrendSample(double ElapsedHours, double ManagedMegabytes);

/// <summary>Least-squares managed-memory trend verdict over the second half of the samples.</summary>
public sealed record BatchSoakMemoryTrend(
    int SamplesEvaluated,
    double SlopeMegabytesPerHour,
    double StartMegabytes,
    double EndMegabytes,
    double GrowthMegabytes,
    bool Exceeded,
    string Description);

/// <summary>Engine and model configuration captured for evidence reproducibility.</summary>
public sealed record BatchSoakEngineConfig(
    string EngineKey,
    string EngineName,
    string EngineVersion,
    string DetectionPriority,
    bool OnnxSelected,
    string ActiveModelId,
    string ActiveModelSha256,
    double ConfidenceThreshold);

/// <summary>Paths of the three evidence artifacts written for a run.</summary>
public sealed record BatchSoakReportPaths(string HtmlPath, string JsonPath, string PassesCsvPath);

/// <summary>
/// Full result of a batch soak run. <see cref="Status"/> is PASS only when at least one
/// pass completed, nothing was canceled, and no failure condition fired.
/// </summary>
public sealed class BatchSoakResult
{
    /// <summary>Unique run identifier stamped into every artifact.</summary>
    public string RunId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>Truthful evidence-scope statement embedded in every artifact.</summary>
    public string ScopeStatement => BatchSoakTestService.ScopeStatement;

    /// <summary>AOI Monitor assembly version that produced this evidence.</summary>
    public string SoftwareVersion { get; init; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

    /// <summary>Operating-system version string of the machine that ran the soak.</summary>
    public string OsInfo { get; init; } = Environment.OSVersion.VersionString;

    /// <summary>Machine that ran the soak.</summary>
    public string MachineName { get; init; } = Environment.MachineName;

    /// <summary>Wall-clock start (display only; durations are measured monotonically).</summary>
    public DateTime StartedAtUtc { get; init; } = DateTime.UtcNow;

    /// <summary>Wall-clock completion (display only).</summary>
    public DateTime CompletedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Requested soak duration.</summary>
    public TimeSpan RequestedDuration { get; init; }

    /// <summary>Monotonic (Stopwatch-based) run duration, immune to system clock steps.</summary>
    public TimeSpan ActualDuration { get; set; }

    /// <summary>Engine/model configuration in effect for the run.</summary>
    public BatchSoakEngineConfig EngineConfig { get; set; } =
        new(string.Empty, string.Empty, string.Empty, string.Empty, false, string.Empty, string.Empty, 0);

    /// <summary>Threshold profile applied by the engine, when one was reported.</summary>
    public string ThresholdProfileId { get; set; } = string.Empty;

    /// <summary>Revision of the applied threshold profile, when reported.</summary>
    public string ThresholdProfileRevision { get; set; } = string.Empty;

    /// <summary>Image count found at run start.</summary>
    public int DatasetImageCountAtStart { get; set; }

    /// <summary>SHA-256 over the sorted start-of-run file list (name|size); a metadata fingerprint, not a content hash.</summary>
    public string DatasetFingerprintSha256 { get; set; } = string.Empty;

    public string ImageFolder { get; init; } = string.Empty;
    public string ManifestPath { get; init; } = string.Empty;
    public string OutputFolder { get; init; } = string.Empty;
    public string OperatorId { get; init; } = "UNKNOWN";
    public string BoardModel { get; init; } = "TBOX-MAIN";
    public string LotId { get; init; } = "BATCH-SOAK";
    public bool BatchRunsPersisted { get; init; }
    public int TotalPasses { get; set; }
    public int TotalImagesProcessed { get; set; }
    public int TotalOkCount { get; set; }
    public int TotalNgCount { get; set; }
    public int TotalReviewCount { get; set; }
    public int TotalErrorCount { get; set; }
    public double AverageInspectionMilliseconds { get; set; }
    public double MaxInspectionMilliseconds { get; set; }
    public double P95InspectionMilliseconds { get; set; }
    public int CountOverOneSecond { get; set; }
    public double AveragePassMilliseconds { get; set; }
    public double MaxPassMilliseconds { get; set; }
    public double StartManagedMemoryMegabytes { get; init; }
    public double EndManagedMemoryMegabytes { get; set; }
    public double PeakManagedMemoryMegabytes { get; set; }
    public double StartWorkingSetMegabytes { get; init; }
    public double EndWorkingSetMegabytes { get; set; }
    public double PeakWorkingSetMegabytes { get; set; }
    public int StartHandleCount { get; init; }
    public int EndHandleCount { get; set; }
    public int PeakHandleCount { get; set; }
    public double StartDatabaseSizeMegabytes { get; init; }
    public double EndDatabaseSizeMegabytes { get; set; }

    /// <summary>SQLite growth across the run, including WAL/SHM sidecars.</summary>
    public double DatabaseGrowthMegabytes => EndDatabaseSizeMegabytes - StartDatabaseSizeMegabytes;

    /// <summary>Final managed-memory trend evaluation (informational when the warm-up gate was not reached).</summary>
    public BatchSoakMemoryTrend MemoryTrend { get; set; } = new(0, 0, 0, 0, 0, false, "Not evaluated.");

    public int NewAlarmEventCount { get; set; }
    public int ActiveCriticalAlarmCountAtEnd { get; set; }
    public List<string> AlarmSummaries { get; } = new();

    /// <summary>Triggered failure conditions (see BatchSoakTestService.FailReason* constants).</summary>
    public List<string> FailReasons { get; } = new();

    public bool WasCanceled { get; set; }
    public string CancellationReason { get; set; } = string.Empty;

    /// <summary>Operator-safe error messages (type + message only; full traces go to the engineer debug file).</summary>
    public List<string> Errors { get; } = new();

    public List<BatchSoakPassRecord> Passes { get; } = new();

    /// <summary>PASS, FAIL, or CANCELED. A canceled run is never acceptance evidence.</summary>
    public string Status => WasCanceled
        ? "CANCELED"
        : FailReasons.Count == 0 && TotalPasses > 0 ? "PASS" : "FAIL";

    /// <summary>
    /// True only for a PASS run whose requested AND monotonically measured actual duration
    /// both reach 8 hours. Scope stays uploaded-image pipeline evidence — never camera,
    /// hardware, or production readiness.
    /// </summary>
    public bool IsEightHourUploadedImagePoCEvidence =>
        Status == "PASS" &&
        RequestedDuration >= TimeSpan.FromHours(8) &&
        ActualDuration >= TimeSpan.FromHours(8);
}
