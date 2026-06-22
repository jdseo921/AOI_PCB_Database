using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using AOI_Monitor.Data;

namespace AOI_Monitor.Services;

public enum UiNavigationSoakProfile
{
    FiveMinuteSmoke,
    ThirtyMinuteLocalStability,
    FourHourClientDemoReadiness,
    EightHourFactoryPoC,
    Custom,
}

public sealed record UiNavigationSoakOptions(
    UiNavigationSoakProfile Profile,
    TimeSpan Duration,
    string OutputFolder,
    bool IncludeHeavyPageLoads = false,
    int? MaxCycles = null,
    long SlowNavigationThresholdMs = UiPerformanceMonitorService.CachedPageSwitchWarningMilliseconds,
    double MemoryGrowthWarningMegabytes = 250,
    double MemoryGrowthFailMegabytes = 500,
    string OperatorId = "UNKNOWN");

public sealed record UiNavigationSoakProgress(
    int ElapsedSeconds,
    int TotalSeconds,
    string Message,
    int CyclesCompleted,
    int SlowNavigationCount,
    long MaxPageLoadMilliseconds,
    double StartMemoryMegabytes,
    double CurrentMemoryMegabytes,
    double MaxMemoryMegabytes,
    int CrashCount,
    int OpenHighCriticalIssues);

public sealed class UiNavigationSoakEvent
{
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
    public int Cycle { get; init; }
    public string PageName { get; init; } = string.Empty;
    public string EventType { get; init; } = "NAVIGATION";
    public long PageLoadMilliseconds { get; init; }
    public long HeartbeatMilliseconds { get; init; }
    public double WorkingSetMegabytes { get; init; }
    public string Status { get; init; } = "OK";
    public string Message { get; init; } = string.Empty;
}

public sealed class UiNavigationSoakResult
{
    public string RunId { get; init; } = Guid.NewGuid().ToString("N");
    public DateTime StartedAtUtc { get; init; } = DateTime.UtcNow;
    public DateTime CompletedAtUtc { get; set; } = DateTime.UtcNow;
    public UiNavigationSoakProfile Profile { get; init; }
    public TimeSpan RequestedDuration { get; init; }
    public TimeSpan ActualDuration => CompletedAtUtc - StartedAtUtc;
    public bool IncludeHeavyPageLoads { get; init; }
    public bool WasCanceled { get; set; }
    public string Status { get; set; } = "PASS";
    public string OperatorId { get; init; } = "UNKNOWN";
    public int CyclesCompleted { get; set; }
    public int PageAttempts { get; set; }
    public int SlowNavigationCount { get; set; }
    public long MaxPageLoadMilliseconds { get; set; }
    public int CrashCount { get; set; }
    public double StartWorkingSetMegabytes { get; init; }
    public double EndWorkingSetMegabytes { get; set; }
    public double MaxWorkingSetMegabytes { get; set; }
    public double MemoryGrowthMegabytes => MaxWorkingSetMegabytes - StartWorkingSetMegabytes;
    public int OpenHighCriticalIssues { get; set; }
    public List<string> Warnings { get; } = new();
    public List<string> Errors { get; } = new();
    public List<UiNavigationSoakEvent> Events { get; } = new();
    public string HtmlPath { get; set; } = string.Empty;
    public string PdfPath { get; set; } = string.Empty;
    public string JsonPath { get; set; } = string.Empty;
    public string CsvPath { get; set; } = string.Empty;
}

public sealed record UiNavigationSoakExportResult(string Folder, string HtmlPath, string PdfPath, string JsonPath, string CsvPath);

public static class UiNavigationSoakTestService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static readonly string[] MajorPages =
    [
        "Main Inspection",
        "Recipe Editor",
        "AI Model Test",
        "Log & Export",
        "Settings",
        "Calibration",
        "3D Profile Viewer",
        "Management Dashboard",
        "Factory Readiness",
        "Factory Acceptance Checklist",
        "Model Registry / Acceptance",
        "MES Queue",
        "Central Sync",
        "Hardware Acceptance",
    ];

    public static string DefaultOutputRoot => Path.Combine(AoiDatabase.StorageRoot, "exports", "ui_stability");

    public static UiNavigationSoakOptions CreateProfileOptions(
        UiNavigationSoakProfile profile,
        string? outputFolder = null,
        bool testMode = false,
        string operatorId = "UNKNOWN")
    {
        var duration = profile switch
        {
            UiNavigationSoakProfile.FiveMinuteSmoke => TimeSpan.FromMinutes(5),
            UiNavigationSoakProfile.ThirtyMinuteLocalStability => TimeSpan.FromMinutes(30),
            UiNavigationSoakProfile.FourHourClientDemoReadiness => TimeSpan.FromHours(4),
            UiNavigationSoakProfile.EightHourFactoryPoC => TimeSpan.FromHours(8),
            _ => TimeSpan.FromMinutes(5),
        };

        return new UiNavigationSoakOptions(
            profile,
            testMode ? TimeSpan.FromSeconds(2) : duration,
            string.IsNullOrWhiteSpace(outputFolder) ? DefaultOutputRoot : outputFolder.Trim(),
            IncludeHeavyPageLoads: profile is UiNavigationSoakProfile.FourHourClientDemoReadiness or UiNavigationSoakProfile.EightHourFactoryPoC,
            MaxCycles: testMode ? 1 : null,
            OperatorId: operatorId);
    }

    public static async Task<UiNavigationSoakResult> RunAsync(
        UiNavigationSoakOptions options,
        Func<string, CancellationToken, Task>? pageAction,
        IProgress<UiNavigationSoakProgress>? progress,
        CancellationToken cancellationToken)
    {
        var process = Process.GetCurrentProcess();
        var started = DateTime.UtcNow;
        var startMemory = BytesToMb(process.WorkingSet64);
        var result = new UiNavigationSoakResult
        {
            StartedAtUtc = started,
            CompletedAtUtc = started,
            Profile = options.Profile,
            RequestedDuration = options.Duration,
            IncludeHeavyPageLoads = options.IncludeHeavyPageLoads,
            OperatorId = options.OperatorId,
            StartWorkingSetMegabytes = startMemory,
            EndWorkingSetMegabytes = startMemory,
            MaxWorkingSetMegabytes = startMemory,
            OpenHighCriticalIssues = CountOpenHighCriticalIssues(),
        };

        var cycle = 0;
        try
        {
            while (DateTime.UtcNow - started < options.Duration)
            {
                cancellationToken.ThrowIfCancellationRequested();
                cycle++;
                foreach (var page in MajorPages)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await RunPageAsync(options, pageAction, result, cycle, page, cancellationToken).ConfigureAwait(false);
                    ReportProgress(options, progress, result, started, page);
                    if (DateTime.UtcNow - started >= options.Duration)
                        break;
                }

                result.CyclesCompleted = cycle;
                if (options.MaxCycles is { } maxCycles && cycle >= maxCycles)
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            result.WasCanceled = true;
            result.Warnings.Add("UI stability test was canceled by the operator.");
        }
        finally
        {
            process.Refresh();
            result.CompletedAtUtc = DateTime.UtcNow;
            result.EndWorkingSetMegabytes = BytesToMb(process.WorkingSet64);
            result.MaxWorkingSetMegabytes = Math.Max(result.MaxWorkingSetMegabytes, result.EndWorkingSetMegabytes);
            FinalizeStatus(options, result);
            ReportProgress(options, progress, result, started, "complete");
        }

        return result;
    }

    public static UiNavigationSoakExportResult WriteReports(UiNavigationSoakResult result, string? outputRoot = null)
    {
        var root = string.IsNullOrWhiteSpace(outputRoot) ? DefaultOutputRoot : outputRoot.Trim();
        var folder = EnsureUniqueFolder(Path.Combine(root, $"ui_stability_{DateTime.UtcNow:yyyyMMdd_HHmmss}"));
        Directory.CreateDirectory(folder);

        var htmlPath = Path.Combine(folder, "ui_stability_report.html");
        var pdfPath = Path.Combine(folder, "ui_stability_report.pdf");
        var jsonPath = Path.Combine(folder, "ui_stability_report.json");
        var csvPath = Path.Combine(folder, "ui_stability_events.csv");

        result.HtmlPath = htmlPath;
        result.PdfPath = pdfPath;
        result.JsonPath = jsonPath;
        result.CsvPath = csvPath;

        File.WriteAllText(htmlPath, BuildHtml(result), Encoding.UTF8);
        PdfExportService.ExportHtmlFileToPdf(htmlPath, pdfPath, "UI Stability Test Report");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(result, JsonOptions), Encoding.UTF8);
        File.WriteAllText(csvPath, BuildCsv(result), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        ExportVerificationService.RecordVerifiedExport("UiStabilityReport", htmlPath, result.Status);
        ExportVerificationService.RecordVerifiedExport("UiStabilityJsonReport", jsonPath, result.Status);
        ExportVerificationService.RecordVerifiedExport("UiStabilityEventsCsv", csvPath, result.Status);
        AoiDatabase.RecordAuditEvent(
            "UI_STABILITY_TEST",
            $"UI stability {result.Status}: profile={result.Profile}; cycles={result.CyclesCompleted}; crashes={result.CrashCount}; slow={result.SlowNavigationCount}; maxLoad={result.MaxPageLoadMilliseconds} ms.",
            relatedEntityType: "UiStabilityReport",
            relatedPath: folder);

        return new UiNavigationSoakExportResult(folder, htmlPath, pdfPath, jsonPath, csvPath);
    }

    public static UiNavigationSoakResult? GetLatestReport()
    {
        var folder = DefaultOutputRoot;
        if (!Directory.Exists(folder))
            return null;

        var latest = Directory.EnumerateFiles(folder, "ui_stability_report.json", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (latest is null)
            return null;

        try
        {
            return JsonSerializer.Deserialize<UiNavigationSoakResult>(File.ReadAllText(latest, Encoding.UTF8), JsonOptions);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"UI navigation soak latest report load failed: {ex.Message}");
            return null;
        }
    }

    public static bool IsThirtyMinuteOrBetterPass(UiNavigationSoakResult? result)
        => result is not null &&
           IsPassingCompletedResult(result) &&
           ProfileRank(result.Profile) >= ProfileRank(UiNavigationSoakProfile.ThirtyMinuteLocalStability) &&
           MeetsMinimumDuration(result, TimeSpan.FromMinutes(30));

    public static bool IsAcceptedShorterSmokePass(UiNavigationSoakResult? result)
        => result is not null &&
           IsPassingCompletedResult(result) &&
           result.Profile == UiNavigationSoakProfile.FiveMinuteSmoke &&
           MeetsMinimumDuration(result, TimeSpan.FromMinutes(5));

    private static async Task RunPageAsync(
        UiNavigationSoakOptions options,
        Func<string, CancellationToken, Task>? pageAction,
        UiNavigationSoakResult result,
        int cycle,
        string page,
        CancellationToken cancellationToken)
    {
        var before = Process.GetCurrentProcess();
        before.Refresh();
        var stopwatch = Stopwatch.StartNew();
        var heartbeat = await MeasureHeartbeatAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (pageAction is not null)
                await pageAction(page, cancellationToken).ConfigureAwait(false);
            else
                await DefaultPageExerciseAsync(options, page, cancellationToken).ConfigureAwait(false);

            stopwatch.Stop();
            AddEvent(options, result, cycle, page, stopwatch.ElapsedMilliseconds, heartbeat, "OK", "Navigation completed.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            result.CrashCount++;
            var message = $"{page} failed safely: {ex.GetType().Name}.";
            result.Errors.Add(message);
            AddEvent(options, result, cycle, page, stopwatch.ElapsedMilliseconds, heartbeat, "ERROR", message);
        }
    }

    private static async Task DefaultPageExerciseAsync(UiNavigationSoakOptions options, string page, CancellationToken cancellationToken)
    {
        var delay = options.IncludeHeavyPageLoads && IsHeavyPage(page)
            ? TimeSpan.FromMilliseconds(35)
            : TimeSpan.FromMilliseconds(8);
        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long> MeasureHeartbeatAsync(CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        await Task.Delay(1, cancellationToken).ConfigureAwait(false);
        sw.Stop();
        return sw.ElapsedMilliseconds;
    }

    private static void AddEvent(
        UiNavigationSoakOptions options,
        UiNavigationSoakResult result,
        int cycle,
        string page,
        long durationMs,
        long heartbeatMs,
        string status,
        string message)
    {
        var process = Process.GetCurrentProcess();
        process.Refresh();
        var memory = BytesToMb(process.WorkingSet64);
        result.PageAttempts++;
        result.MaxPageLoadMilliseconds = Math.Max(result.MaxPageLoadMilliseconds, durationMs);
        result.MaxWorkingSetMegabytes = Math.Max(result.MaxWorkingSetMegabytes, memory);
        if (durationMs > options.SlowNavigationThresholdMs)
        {
            result.SlowNavigationCount++;
            result.Warnings.Add($"{page} navigation took {durationMs} ms, over threshold {options.SlowNavigationThresholdMs} ms.");
        }

        if (heartbeatMs > UiPerformanceMonitorService.UiThreadBlockingWarningMilliseconds)
            result.Warnings.Add($"UI responsiveness heartbeat took {heartbeatMs} ms on {page}.");

        result.Events.Add(new UiNavigationSoakEvent
        {
            Cycle = cycle,
            PageName = page,
            PageLoadMilliseconds = durationMs,
            HeartbeatMilliseconds = heartbeatMs,
            WorkingSetMegabytes = memory,
            Status = status,
            Message = message,
        });
    }

    private static void FinalizeStatus(UiNavigationSoakOptions options, UiNavigationSoakResult result)
    {
        result.OpenHighCriticalIssues = CountOpenHighCriticalIssues();
        var memoryGrowth = result.MemoryGrowthMegabytes;
        if (memoryGrowth > options.MemoryGrowthWarningMegabytes)
            result.Warnings.Add($"Working set grew by {memoryGrowth:F1} MB.");
        if (memoryGrowth > options.MemoryGrowthFailMegabytes)
            result.Errors.Add($"Working set growth exceeded fail threshold: {memoryGrowth:F1} MB.");
        if (result.CrashCount > 0)
            result.Errors.Add($"{result.CrashCount} navigation exception(s) were captured.");

        result.Status = result.WasCanceled
            ? "CANCELED"
            : result.Errors.Count > 0
                ? "FAIL"
                : result.Warnings.Count > 0 ? "WARN" : "PASS";
    }

    private static void ReportProgress(
        UiNavigationSoakOptions options,
        IProgress<UiNavigationSoakProgress>? progress,
        UiNavigationSoakResult result,
        DateTime startedUtc,
        string page)
    {
        var elapsed = DateTime.UtcNow - startedUtc;
        var process = Process.GetCurrentProcess();
        process.Refresh();
        var currentMemory = BytesToMb(process.WorkingSet64);
        progress?.Report(new UiNavigationSoakProgress(
            Math.Max(0, (int)elapsed.TotalSeconds),
            Math.Max(1, (int)options.Duration.TotalSeconds),
            $"UI stability: {page}; cycles={result.CyclesCompleted}; slow={result.SlowNavigationCount}; crashes={result.CrashCount}; memory={currentMemory:F1}/{result.MaxWorkingSetMegabytes:F1} MB.",
            result.CyclesCompleted,
            result.SlowNavigationCount,
            result.MaxPageLoadMilliseconds,
            result.StartWorkingSetMegabytes,
            currentMemory,
            result.MaxWorkingSetMegabytes,
            result.CrashCount,
            result.OpenHighCriticalIssues));
    }

    private static string BuildHtml(UiNavigationSoakResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html><html><head><meta charset=\"utf-8\"><title>UI Stability Test Report</title>");
        sb.AppendLine("<style>body{font-family:Segoe UI,Arial;margin:24px;color:#17212b}.PASS{color:#176b3a}.WARN{color:#8a5a00}.FAIL,.CANCELED{color:#a12626}table{border-collapse:collapse;width:100%}td,th{border:1px solid #cfd8df;padding:6px;text-align:left;vertical-align:top}th{background:#edf2f5}</style></head><body>");
        sb.AppendLine($"<h1>UI Stability Test Report</h1><p><strong class=\"{Html(result.Status)}\">{Html(result.Status)}</strong><br>Profile: {Html(result.Profile.ToString())}<br>Requested: {result.RequestedDuration}<br>Actual: {result.ActualDuration}<br>Operator: {Html(result.OperatorId)}</p>");
        sb.AppendLine("<table><tr><th>Cycles</th><th>Pages</th><th>Slow nav</th><th>Max load</th><th>Crashes</th><th>Memory start/end/max</th><th>Open high/critical issues</th></tr>");
        sb.AppendLine($"<tr><td>{result.CyclesCompleted}</td><td>{result.PageAttempts}</td><td>{result.SlowNavigationCount}</td><td>{result.MaxPageLoadMilliseconds} ms</td><td>{result.CrashCount}</td><td>{result.StartWorkingSetMegabytes:F1} / {result.EndWorkingSetMegabytes:F1} / {result.MaxWorkingSetMegabytes:F1} MB</td><td>{result.OpenHighCriticalIssues}</td></tr></table>");
        AppendList(sb, "Warnings", result.Warnings);
        AppendList(sb, "Errors", result.Errors);
        sb.AppendLine("<h2>Events</h2><table><tr><th>UTC</th><th>Cycle</th><th>Page</th><th>Status</th><th>Load ms</th><th>Heartbeat ms</th><th>Memory MB</th><th>Message</th></tr>");
        foreach (var item in result.Events.TakeLast(500))
            sb.AppendLine($"<tr><td>{item.TimestampUtc:O}</td><td>{item.Cycle}</td><td>{Html(item.PageName)}</td><td>{Html(item.Status)}</td><td>{item.PageLoadMilliseconds}</td><td>{item.HeartbeatMilliseconds}</td><td>{item.WorkingSetMegabytes:F1}</td><td>{Html(item.Message)}</td></tr>");
        sb.AppendLine("</table></body></html>");
        return sb.ToString();
    }

    private static string BuildCsv(UiNavigationSoakResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("TimestampUtc,Cycle,PageName,EventType,PageLoadMilliseconds,HeartbeatMilliseconds,WorkingSetMegabytes,Status,Message");
        foreach (var item in result.Events)
        {
            sb.AppendLine(string.Join(",",
                Csv(item.TimestampUtc.ToString("O", CultureInfo.InvariantCulture)),
                item.Cycle.ToString(CultureInfo.InvariantCulture),
                Csv(item.PageName),
                Csv(item.EventType),
                item.PageLoadMilliseconds.ToString(CultureInfo.InvariantCulture),
                item.HeartbeatMilliseconds.ToString(CultureInfo.InvariantCulture),
                item.WorkingSetMegabytes.ToString("F1", CultureInfo.InvariantCulture),
                Csv(item.Status),
                Csv(item.Message)));
        }

        return sb.ToString();
    }

    private static void AppendList(StringBuilder sb, string title, IReadOnlyCollection<string> items)
    {
        sb.AppendLine($"<h2>{Html(title)}</h2>");
        if (items.Count == 0)
        {
            sb.AppendLine("<p>None.</p>");
            return;
        }

        sb.AppendLine("<ul>");
        foreach (var item in items.Take(100))
            sb.AppendLine($"<li>{Html(item)}</li>");
        sb.AppendLine("</ul>");
    }

    private static int CountOpenHighCriticalIssues()
    {
        try
        {
            return PilotIssueService.Search(new Models.PilotIssueFilter { OpenOnly = true })
                .Count(issue => PilotIssueService.IsHighOrCritical(issue.Severity));
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"UI navigation soak pilot issue count fallback used: {ex.Message}");
            return 0;
        }
    }

    private static bool IsHeavyPage(string page)
        => page.Contains("AI Model", StringComparison.OrdinalIgnoreCase) ||
           page.Contains("Log", StringComparison.OrdinalIgnoreCase) ||
           page.Contains("Management", StringComparison.OrdinalIgnoreCase) ||
           page.Contains("3D", StringComparison.OrdinalIgnoreCase) ||
           page.Contains("Model Registry", StringComparison.OrdinalIgnoreCase);

    private static int ProfileRank(UiNavigationSoakProfile profile)
        => profile switch
        {
            UiNavigationSoakProfile.FiveMinuteSmoke => 1,
            UiNavigationSoakProfile.ThirtyMinuteLocalStability => 2,
            UiNavigationSoakProfile.FourHourClientDemoReadiness => 3,
            UiNavigationSoakProfile.EightHourFactoryPoC => 4,
            UiNavigationSoakProfile.Custom => 0,
            _ => 0,
        };

    private static bool IsPassingCompletedResult(UiNavigationSoakResult result)
        => result.Status.Equals("PASS", StringComparison.OrdinalIgnoreCase) &&
           !result.WasCanceled &&
           result.CrashCount == 0;

    private static bool MeetsMinimumDuration(UiNavigationSoakResult result, TimeSpan minimum)
    {
        var tolerance = TimeSpan.FromSeconds(15);
        return result.RequestedDuration >= minimum &&
            result.ActualDuration + tolerance >= minimum;
    }

    private static string EnsureUniqueFolder(string path)
    {
        if (!Directory.Exists(path))
            return path;
        for (var i = 1; i < 1000; i++)
        {
            var candidate = $"{path}_{i:D3}";
            if (!Directory.Exists(candidate))
                return candidate;
        }

        return $"{path}_{Guid.NewGuid():N}";
    }

    private static double BytesToMb(long bytes)
        => bytes / 1024d / 1024d;

    private static string Html(string value)
        => System.Net.WebUtility.HtmlEncode(value ?? string.Empty);

    private static string Csv(string value)
        => $"\"{(value ?? string.Empty).Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
