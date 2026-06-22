using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace AOI_Monitor.Services;

public sealed record UiPerformanceEvent(
    DateTime TimestampUtc,
    string Category,
    string Name,
    long DurationMilliseconds,
    string Message);

public sealed class UiPerformanceScope : IDisposable
{
    private readonly string _category;
    private readonly string _name;
    private readonly long _warningThresholdMilliseconds;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private bool _disposed;

    internal UiPerformanceScope(string category, string name, long warningThresholdMilliseconds)
    {
        _category = category;
        _name = name;
        _warningThresholdMilliseconds = warningThresholdMilliseconds;
    }

    public long ElapsedMilliseconds => _stopwatch.ElapsedMilliseconds;

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _stopwatch.Stop();
        UiPerformanceMonitorService.Record(
            _category,
            _name,
            _stopwatch.ElapsedMilliseconds,
            _stopwatch.ElapsedMilliseconds >= _warningThresholdMilliseconds
                ? $"Slow {_category}: {_name} took {_stopwatch.ElapsedMilliseconds} ms."
                : $"{_category}: {_name} took {_stopwatch.ElapsedMilliseconds} ms.");
    }
}

public static class UiPerformanceMonitorService
{
    public const long MenuResponseWarningMilliseconds = 100;
    public const long CachedPageSwitchWarningMilliseconds = 300;
    public const long UiThreadBlockingWarningMilliseconds = 100;
    public const long PageConstructorWarningMilliseconds = 300;

    private static readonly ConcurrentQueue<UiPerformanceEvent> Events = new();

    public static UiPerformanceScope BeginNavigation(string pageKey)
        => new("NAVIGATION", pageKey, CachedPageSwitchWarningMilliseconds);

    public static UiPerformanceScope BeginPageConstruction(string pageName)
        => new("PAGE_CONSTRUCTION", pageName, PageConstructorWarningMilliseconds);

    public static UiPerformanceScope BeginPageRefresh(string pageName)
        => new("PAGE_REFRESH", pageName, CachedPageSwitchWarningMilliseconds);

    public static UiPerformanceScope BeginPageLoad(string pageName)
        => BeginPageRefresh(pageName);

    public static UiPerformanceScope BeginUiThreadWork(string operationName)
        => new("UI_THREAD", operationName, UiThreadBlockingWarningMilliseconds);

    public static UiPerformanceScope BeginSlowOperation(string operationName)
        => new("SLOW_OPERATION", operationName, CachedPageSwitchWarningMilliseconds);

    public static IReadOnlyList<UiPerformanceEvent> RecentEvents => Events.ToArray();

    public static void ClearForTests()
    {
        while (Events.TryDequeue(out _))
        {
        }
    }

    public static void RecordSlowPageLoad(string pageName, long durationMilliseconds)
        => Record("PAGE_LOAD", pageName, durationMilliseconds, $"Slow page load: {pageName} took {durationMilliseconds} ms.");

    public static void RecordNavigationStart(string pageKey)
        => Record("NAVIGATION_START", pageKey, 0, $"Navigation started: {pageKey}.");

    public static void RecordNavigationVisualResponse(string pageKey, long durationMilliseconds)
        => Record(
            "NAVIGATION_VISUAL_RESPONSE",
            pageKey,
            durationMilliseconds,
            durationMilliseconds > MenuResponseWarningMilliseconds
                ? $"Slow navigation visual response: {pageKey} took {durationMilliseconds} ms."
                : $"Navigation visual response: {pageKey} took {durationMilliseconds} ms.");

    public static void RecordCachedPageSwitch(string pageKey, long durationMilliseconds)
        => Record(
            "CACHED_PAGE_SWITCH",
            pageKey,
            durationMilliseconds,
            durationMilliseconds > CachedPageSwitchWarningMilliseconds
                ? $"Slow cached page switch: {pageKey} took {durationMilliseconds} ms."
                : $"Cached page switch: {pageKey} took {durationMilliseconds} ms.");

    public static void RecordPageConstruction(string pageName, long durationMilliseconds)
        => Record(
            "PAGE_CONSTRUCTION",
            pageName,
            durationMilliseconds,
            durationMilliseconds > PageConstructorWarningMilliseconds
                ? $"Slow page construction: {pageName} took {durationMilliseconds} ms."
                : $"Page construction: {pageName} took {durationMilliseconds} ms.");

    public static void RecordPageRefresh(string pageName, long durationMilliseconds)
        => Record(
            "PAGE_REFRESH",
            pageName,
            durationMilliseconds,
            durationMilliseconds > CachedPageSwitchWarningMilliseconds
                ? $"Slow page refresh: {pageName} took {durationMilliseconds} ms."
                : $"Page refresh: {pageName} took {durationMilliseconds} ms.");

    public static void RecordUiThreadBlockingWarning(string operationName, long durationMilliseconds)
        => Record("UI_THREAD", operationName, durationMilliseconds, $"Potential UI-thread blocking: {operationName} took {durationMilliseconds} ms.");

    public static void RecordSlowOperation(string operationName, long durationMilliseconds, string message = "")
        => Record(
            "SLOW_OPERATION",
            operationName,
            durationMilliseconds,
            string.IsNullOrWhiteSpace(message) ? $"Slow operation: {operationName} took {durationMilliseconds} ms." : message);

    internal static void Record(string category, string name, long durationMilliseconds, string message)
    {
        var entry = new UiPerformanceEvent(DateTime.UtcNow, category, name, durationMilliseconds, message);
        Events.Enqueue(entry);
        while (Events.Count > 250 && Events.TryDequeue(out _))
        {
        }

        Trace.WriteLine($"[AOI_UI_PERF] {entry.TimestampUtc:O} {entry.Category} {entry.Name} {entry.DurationMilliseconds} ms {entry.Message}");
        TryAppendDiagnosticLog(entry);
        TryCreatePilotIssue(entry);
    }

    private static void TryAppendDiagnosticLog(UiPerformanceEvent entry)
    {
        try
        {
            var root = Path.Combine(AppContext.BaseDirectory, "exports", "diagnostics");
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "ui_performance.log");
            File.AppendAllText(
                path,
                $"{entry.TimestampUtc:O}\t{entry.Category}\t{entry.Name}\t{entry.DurationMilliseconds}\t{entry.Message}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[AOI_UI_PERF] Diagnostic log append failed: {ex.Message}");
        }
    }

    private static void TryCreatePilotIssue(UiPerformanceEvent entry)
    {
        try
        {
            if (entry.Category == "NAVIGATION" &&
                entry.DurationMilliseconds >= CachedPageSwitchWarningMilliseconds)
            {
                PilotIssueService.CreateNavigationPerformanceIssue(entry.Name, entry.DurationMilliseconds, entry.Message);
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[AOI_UI_PERF] Pilot issue creation failed: {ex.Message}");
        }
    }
}
