using AOI_Monitor.Data;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public sealed class InspectionLatencyTraceBuilder
{
    private readonly InspectionLatencyTrace _trace;

    public InspectionLatencyTraceBuilder(string sourceKind, string engine, string modelId, int imageWidth, int imageHeight, DateTime? frameCapturedAtUtc = null)
    {
        var now = DateTime.UtcNow;
        _trace = new InspectionLatencyTrace
        {
            TraceId = Guid.NewGuid().ToString("N"),
            CreatedAtUtc = now,
            FrameCapturedAtUtc = frameCapturedAtUtc?.ToUniversalTime() ?? now,
            FrameReceivedAtUtc = now,
            SourceKind = sourceKind,
            Engine = engine,
            ModelId = modelId,
            ImageWidth = imageWidth,
            ImageHeight = imageHeight,
        };
    }

    public InspectionLatencyTrace Trace => _trace;

    public void StartSpan(string name, DateTime? timestampUtc = null)
        => SetSpan(name, start: true, timestampUtc?.ToUniversalTime() ?? DateTime.UtcNow);

    public void StopSpan(string name, DateTime? timestampUtc = null)
        => SetSpan(name, start: false, timestampUtc?.ToUniversalTime() ?? DateTime.UtcNow);

    public InspectionLatencyTrace Complete(bool saved)
        => InspectionLatencyService.FinalizeTrace(_trace, saved);

    private void SetSpan(string name, bool start, DateTime timestampUtc)
    {
        switch (Normalize(name))
        {
            case "preprocessing":
                if (start) _trace.PreprocessingStartUtc = timestampUtc; else _trace.PreprocessingEndUtc = timestampUtc;
                break;
            case "inference":
                if (start) _trace.InferenceStartUtc = timestampUtc; else _trace.InferenceEndUtc = timestampUtc;
                break;
            case "postprocess":
                if (start) _trace.PostprocessStartUtc = timestampUtc; else _trace.PostprocessEndUtc = timestampUtc;
                break;
            case "overlay":
            case "overlayrender":
                if (start) _trace.OverlayRenderStartUtc = timestampUtc; else _trace.OverlayRenderEndUtc = timestampUtc;
                break;
            case "persist":
            case "resultpersist":
            case "save":
                if (start) _trace.ResultPersistStartUtc = timestampUtc; else _trace.ResultPersistEndUtc = timestampUtc;
                break;
            default:
                _trace.Warnings.Add($"Unknown latency span '{name}' was ignored.");
                break;
        }
    }

    private static string Normalize(string name)
        => new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}

public static class InspectionLatencyService
{
    private static readonly object Gate = new();
    private static readonly List<InspectionLatencyTrace> SessionTraces = new();

    public static InspectionLatencyTraceBuilder StartTrace(
        string sourceKind,
        string engine,
        string modelId,
        int imageWidth,
        int imageHeight,
        DateTime? frameCapturedAtUtc = null)
        => new(sourceKind, engine, modelId, imageWidth, imageHeight, frameCapturedAtUtc);

    public static void StartSpan(InspectionLatencyTraceBuilder builder, string name, DateTime? timestampUtc = null)
        => builder.StartSpan(name, timestampUtc);

    public static void EndSpan(InspectionLatencyTraceBuilder builder, string name, DateTime? timestampUtc = null)
        => builder.StopSpan(name, timestampUtc);

    public static InspectionLatencyTrace CompleteTrace(InspectionLatencyTraceBuilder builder, bool saved)
        => builder.Complete(saved);

    public static InspectionLatencyTrace Persist(InspectionLatencyTraceBuilder builder, bool saved)
        => Persist(builder.Complete(saved));

    public static InspectionLatencyTrace Persist(InspectionLatencyTrace trace)
    {
        FinalizeTrace(trace, trace.ResultPersistEndUtc is not null);
        trace.Id = AoiDatabase.RecordInspectionLatencyTrace(trace);
        lock (Gate)
        {
            SessionTraces.Add(trace);
        }

        return trace;
    }

    public static InspectionLatencyTrace FinalizeTrace(InspectionLatencyTrace trace, bool saved)
    {
        var frameStart = trace.FrameCapturedAtUtc ?? trace.FrameReceivedAtUtc ?? trace.CreatedAtUtc;
        if (trace.FrameCapturedAtUtc is null)
            trace.Warnings.Add("Frame captured timestamp is missing; frame received timestamp was used.");
        if (trace.FrameReceivedAtUtc is null)
            trace.Warnings.Add("Frame received timestamp is missing.");

        ValidateSpan(trace.PreprocessingStartUtc, trace.PreprocessingEndUtc, "preprocessing", trace.Warnings);
        ValidateSpan(trace.InferenceStartUtc, trace.InferenceEndUtc, "inference", trace.Warnings);
        ValidateSpan(trace.PostprocessStartUtc, trace.PostprocessEndUtc, "postprocess", trace.Warnings);
        ValidateSpan(trace.OverlayRenderStartUtc, trace.OverlayRenderEndUtc, "overlay render", trace.Warnings);
        if (saved)
            ValidateSpan(trace.ResultPersistStartUtc, trace.ResultPersistEndUtc, "result persist", trace.Warnings);

        trace.TotalFrameToOverlayMs = trace.OverlayRenderEndUtc is { } overlayEnd
            ? Math.Max(0, (overlayEnd - frameStart).TotalMilliseconds)
            : 0;
        trace.TotalFrameToSavedResultMs = trace.ResultPersistEndUtc is { } persistEnd
            ? Math.Max(0, (persistEnd - frameStart).TotalMilliseconds)
            : 0;
        if (trace.TotalFrameToOverlayMs <= 0)
            trace.Warnings.Add("Frame-to-overlay total could not be calculated because overlay end is missing.");
        if (saved && trace.TotalFrameToSavedResultMs <= 0)
            trace.Warnings.Add("Frame-to-saved-result total could not be calculated because persist end is missing.");

        trace.Warnings = trace.Warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return trace;
    }

    public static InspectionLatencySummary GetCurrentSessionSummary()
    {
        lock (Gate)
        {
            return Summarize(SessionTraces);
        }
    }

    public static InspectionLatencySummary GetRecentSummary(int limit = 500)
        => Summarize(AoiDatabase.GetInspectionLatencyTraces(limit));

    public static InspectionLatencySummary GetSummary(DateTime fromUtc, DateTime toUtc, int limit = 10000)
        => Summarize(AoiDatabase.GetInspectionLatencyTraces(fromUtc, toUtc, limit));

    public static InspectionLatencySummary SummarizeBatchRows(IEnumerable<BatchTestRow> rows)
    {
        var traces = rows.Select(row => new InspectionLatencyTrace
        {
            TraceId = Guid.NewGuid().ToString("N"),
            CreatedAtUtc = DateTime.UtcNow,
            TotalFrameToOverlayMs = row.TotalInspectionMilliseconds,
            TotalFrameToSavedResultMs = row.TotalInspectionMilliseconds,
            InferenceStartUtc = DateTime.UtcNow,
            InferenceEndUtc = DateTime.UtcNow.AddMilliseconds(Math.Max(0, row.InferenceMilliseconds)),
            OverlayRenderStartUtc = DateTime.UtcNow,
            OverlayRenderEndUtc = DateTime.UtcNow.AddMilliseconds(Math.Max(0, row.OverlayRenderingMilliseconds)),
            SourceKind = "BatchValidation",
            Engine = row.InspectionEngine,
            ModelId = row.ModelVersion,
            Verdict = row.EngineResult,
            Warnings = { "Batch validation row timing does not include live UI result-persist span." },
        });
        return Summarize(traces);
    }

    public static InspectionLatencySummary Summarize(IEnumerable<InspectionLatencyTrace> traces)
    {
        var items = traces.ToArray();
        var overlayTotals = items.Select(item => item.TotalFrameToOverlayMs).Where(value => value > 0).OrderBy(value => value).ToArray();
        var savedTotals = items.Select(item => item.TotalFrameToSavedResultMs).Where(value => value > 0).OrderBy(value => value).ToArray();
        return new InspectionLatencySummary
        {
            TraceCount = items.Length,
            OverOneSecondCount = items.Count(item => item.TotalFrameToOverlayMs > 1000 || item.TotalFrameToSavedResultMs > 1000),
            P50FrameToOverlayMs = Percentile(overlayTotals, 0.50),
            P95FrameToOverlayMs = Percentile(overlayTotals, 0.95),
            MaxFrameToOverlayMs = overlayTotals.Length == 0 ? 0 : overlayTotals[^1],
            P50FrameToSavedResultMs = Percentile(savedTotals, 0.50),
            P95FrameToSavedResultMs = Percentile(savedTotals, 0.95),
            MaxFrameToSavedResultMs = savedTotals.Length == 0 ? 0 : savedTotals[^1],
            P95InferenceMs = Percentile(items.Select(item => Duration(item.InferenceStartUtc, item.InferenceEndUtc)).Where(value => value > 0).OrderBy(value => value).ToArray(), 0.95),
            P95OverlayMs = Percentile(items.Select(item => Duration(item.OverlayRenderStartUtc, item.OverlayRenderEndUtc)).Where(value => value > 0).OrderBy(value => value).ToArray(), 0.95),
            P95SaveMs = Percentile(items.Select(item => Duration(item.ResultPersistStartUtc, item.ResultPersistEndUtc)).Where(value => value > 0).OrderBy(value => value).ToArray(), 0.95),
            Warnings = items.SelectMany(item => item.Warnings).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        };
    }

    private static void ValidateSpan(DateTime? start, DateTime? end, string name, List<string> warnings)
    {
        if (start is null || end is null)
        {
            warnings.Add($"{name} span is incomplete.");
            return;
        }

        if (end < start)
            warnings.Add($"{name} span end is earlier than start.");
    }

    private static double Duration(DateTime? start, DateTime? end)
        => start is { } s && end is { } e && e >= s ? (e - s).TotalMilliseconds : 0;

    private static double Percentile(IReadOnlyList<double> sortedValues, double percentile)
    {
        if (sortedValues.Count == 0)
            return 0;
        if (sortedValues.Count == 1)
            return sortedValues[0];

        var position = (sortedValues.Count - 1) * percentile;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper)
            return sortedValues[lower];

        var fraction = position - lower;
        return sortedValues[lower] + (sortedValues[upper] - sortedValues[lower]) * fraction;
    }
}
