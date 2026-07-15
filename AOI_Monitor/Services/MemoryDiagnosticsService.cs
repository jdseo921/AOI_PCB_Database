using System.Diagnostics;

namespace AOI_Monitor.Services;

public sealed class MemoryDiagnosticsSnapshot
{
    public DateTime CapturedAtUtc { get; init; } = DateTime.UtcNow;
    public long WorkingSetBytes { get; init; }
    public long PrivateMemoryBytes { get; init; }
    public long ManagedBytes { get; init; }
    public int Gen0Collections { get; init; }
    public int Gen1Collections { get; init; }
    public int Gen2Collections { get; init; }
    public int ImageCacheCount { get; init; }
    public int ThumbnailCacheCount { get; init; }
    public long ImageCacheBytes { get; init; }
    public int LargeBitmapCount { get; init; }
    public int ActivePageCount { get; init; }
    public int PageCacheCount { get; init; }
    public string LatestExportOperation { get; init; } = string.Empty;
    public int LatestExportProcessedCount { get; init; }
    public long LatestExportWorkingSetPeakBytes { get; init; }
    public long LatestExportManagedPeakBytes { get; init; }
    public DateTime? LatestExportCapturedAtUtc { get; init; }
    public IReadOnlyList<string> ExportMemoryWarnings { get; init; } = Array.Empty<string>();
}

public static class MemoryDiagnosticsService
{
    private const long DefaultWorkingSetWarningBytes = 1_500L * 1024 * 1024;
    private const long DefaultManagedWarningBytes = 650L * 1024 * 1024;
    private static readonly object Gate = new();
    private static readonly Queue<string> ExportWarnings = new();
    private static int _pageCacheCount;
    private static string _latestExportOperation = string.Empty;
    private static int _latestExportProcessedCount;
    private static long _latestExportWorkingSetPeakBytes;
    private static long _latestExportManagedPeakBytes;
    private static DateTime? _latestExportCapturedAtUtc;

    public static void SetPageCacheCount(int count)
        => SetActivePageCount(count);

    public static void SetActivePageCount(int count)
    {
        lock (Gate)
            _pageCacheCount = Math.Max(0, count);
    }

    public static MemoryDiagnosticsSnapshot Capture()
    {
        var process = Process.GetCurrentProcess();
        var cache = ImageCacheService.GetDiagnostics();
        lock (Gate)
        {
            return new MemoryDiagnosticsSnapshot
            {
                WorkingSetBytes = process.WorkingSet64,
                PrivateMemoryBytes = process.PrivateMemorySize64,
                ManagedBytes = GC.GetTotalMemory(forceFullCollection: false),
                Gen0Collections = GC.CollectionCount(0),
                Gen1Collections = GC.CollectionCount(1),
                Gen2Collections = GC.CollectionCount(2),
                ImageCacheCount = cache.ImageCacheCount,
                ThumbnailCacheCount = cache.ThumbnailCacheCount,
                ImageCacheBytes = cache.EstimatedBytes,
                LargeBitmapCount = cache.LargeBitmapCount,
                ActivePageCount = _pageCacheCount,
                PageCacheCount = _pageCacheCount,
                LatestExportOperation = _latestExportOperation,
                LatestExportProcessedCount = _latestExportProcessedCount,
                LatestExportWorkingSetPeakBytes = _latestExportWorkingSetPeakBytes,
                LatestExportManagedPeakBytes = _latestExportManagedPeakBytes,
                LatestExportCapturedAtUtc = _latestExportCapturedAtUtc,
                ExportMemoryWarnings = ExportWarnings.ToArray(),
            };
        }
    }


    public static void RecordExportCheckpoint(string operation, int processedCount)
    {
        var snapshot = Capture();
        lock (Gate)
        {
            if (!_latestExportOperation.Equals(operation, StringComparison.OrdinalIgnoreCase))
            {
                _latestExportOperation = operation;
                _latestExportWorkingSetPeakBytes = 0;
                _latestExportManagedPeakBytes = 0;
            }

            _latestExportProcessedCount = Math.Max(0, processedCount);
            _latestExportWorkingSetPeakBytes = Math.Max(_latestExportWorkingSetPeakBytes, snapshot.WorkingSetBytes);
            _latestExportManagedPeakBytes = Math.Max(_latestExportManagedPeakBytes, snapshot.ManagedBytes);
            _latestExportCapturedAtUtc = DateTime.UtcNow;
        }

        if (snapshot.WorkingSetBytes < DefaultWorkingSetWarningBytes &&
            snapshot.ManagedBytes < DefaultManagedWarningBytes)
        {
            return;
        }

        RecordExportMemoryWarning(
            $"{operation}: processed={processedCount}; workingSetMB={ToMb(snapshot.WorkingSetBytes):F0}; managedMB={ToMb(snapshot.ManagedBytes):F0}; imageCacheMB={ToMb(snapshot.ImageCacheBytes):F0}.");
    }

    public static void RecordExportMemoryWarning(string message)
    {
        lock (Gate)
        {
            ExportWarnings.Enqueue($"{DateTime.UtcNow:O} {message}");
            while (ExportWarnings.Count > 50)
                ExportWarnings.Dequeue();
        }
    }

    public static void ClearForTests()
    {
        lock (Gate)
        {
            _pageCacheCount = 0;
            _latestExportOperation = string.Empty;
            _latestExportProcessedCount = 0;
            _latestExportWorkingSetPeakBytes = 0;
            _latestExportManagedPeakBytes = 0;
            _latestExportCapturedAtUtc = null;
            ExportWarnings.Clear();
        }
    }

    private static double ToMb(long bytes) => bytes / 1024d / 1024d;
}
