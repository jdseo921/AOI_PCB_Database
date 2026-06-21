using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using AOI_Monitor.Data;
using System.Text;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public sealed record SoakTestOptions(
    string ImageFolder,
    TimeSpan Duration,
    TimeSpan DelayBetweenInspections,
    string EngineKey,
    string OutputFolder,
    string OperatorId,
    string BoardModel,
    string LotId)
{
    public int? MaxIterations { get; init; }
}

public enum SoakTestProfile
{
    QuickSmoke,
    ShortStability,
    FactoryPoc,
    Custom,
}

public sealed record SoakTestProgress(
    int ElapsedSeconds,
    int TotalSeconds,
    string Message,
    TimeSpan? EstimatedRemaining = null,
    int PassCount = 0,
    int FailCount = 0,
    double MaxInspectionMilliseconds = 0,
    double PeakWorkingSetMegabytes = 0,
    string CancellationReason = "");

public sealed record SoakTestCycleRecord(
    int CycleNumber,
    string FrameId,
    string ImagePath,
    string Verdict,
    double TotalMilliseconds,
    bool Success,
    string Message,
    DateTime? TimestampUtc = null,
    string EngineName = "",
    double WorkingSetMegabytes = 0,
    string Error = "",
    double TotalCycleMilliseconds = 0,
    string ExceptionCategory = "");

public sealed class SoakTestResult
{
    public long Id { get; set; }
    public string RunId { get; init; } = Guid.NewGuid().ToString("N");
    public DateTime StartTime { get; init; } = DateTime.Now;
    public DateTime EndTime { get; set; } = DateTime.Now;
    public string ImageFolder { get; init; } = string.Empty;
    public string OutputFolder { get; init; } = string.Empty;
    public string EngineName { get; set; } = "Unknown";
    public string EngineVersion { get; set; } = "UNKNOWN";
    public string EngineKey { get; init; } = InspectionEngineFactory.DefaultEngineKey;
    public string SourceKind { get; set; } = "Simulated source";
    public bool IsRealCameraSource { get; set; }
    public string ProfileName { get; init; } = SoakTestProfile.Custom.ToString();
    public TimeSpan RequestedDuration { get; init; }
    public TimeSpan ActualDuration => EndTime - StartTime;
    public TimeSpan DelayBetweenInspections { get; init; }
    public string OperatorId { get; init; } = "UNKNOWN";
    public string BoardModel { get; init; } = "TBOX-MAIN";
    public string LotId { get; init; } = "SOAK-TEST";
    public bool WasCanceled { get; set; }
    public int TotalCycles { get; set; }
    public int SuccessfulCycles { get; set; }
    public int FailedCycles { get; set; }
    public double AverageInspectionMilliseconds { get; set; }
    public double MinInspectionMilliseconds { get; set; }
    public double MaxInspectionMilliseconds { get; set; }
    public double P95InspectionMilliseconds { get; set; }
    public double AverageTotalCycleMilliseconds { get; set; }
    public double MaxTotalCycleMilliseconds { get; set; }
    public double P95TotalCycleMilliseconds { get; set; }
    public int CountOverOneSecond { get; set; }
    public double StartManagedMemoryMegabytes { get; init; }
    public double EndManagedMemoryMegabytes { get; set; }
    public double StartWorkingSetMegabytes { get; init; }
    public double EndWorkingSetMegabytes { get; set; }
    public double PeakWorkingSetMegabytes { get; set; }
    public string CancellationReason { get; set; } = string.Empty;
    public string FirstCriticalError { get; set; } = string.Empty;
    public bool IsCompletedFactoryEvidence => !WasCanceled &&
        FailedCycles == 0 &&
        IsRealCameraSource &&
        RequestedDuration >= TimeSpan.FromHours(8) &&
        ActualDuration >= TimeSpan.FromHours(8);
    public List<string> Errors { get; } = new();
    public List<string> MemoryWarnings { get; } = new();
    public List<SoakTestCycleRecord> Cycles { get; } = new();
}

public static class SoakTestService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static SoakTestOptions CreateProfileOptions(
        SoakTestProfile profile,
        string imageFolder,
        string engineKey,
        string outputFolder,
        string operatorId,
        string boardModel,
        string lotId)
    {
        var duration = profile switch
        {
            SoakTestProfile.QuickSmoke => TimeSpan.FromMinutes(5),
            SoakTestProfile.ShortStability => TimeSpan.FromMinutes(30),
            SoakTestProfile.FactoryPoc => TimeSpan.FromHours(8),
            _ => TimeSpan.FromMinutes(2),
        };

        return new SoakTestOptions(
            imageFolder,
            duration,
            TimeSpan.FromMilliseconds(250),
            engineKey,
            outputFolder,
            operatorId,
            boardModel,
            lotId);
    }

    public static async Task<SoakTestResult> RunAsync(
        SoakTestOptions options,
        IProgress<SoakTestProgress>? progress,
        CancellationToken cancellationToken)
    {
        var started = DateTime.Now;
        var process = Process.GetCurrentProcess();
        var result = new SoakTestResult
        {
            StartTime = started,
            EndTime = started,
            ImageFolder = options.ImageFolder,
            OutputFolder = options.OutputFolder,
            EngineKey = InspectionEngineFactory.NormalizeEngineKey(options.EngineKey),
            ProfileName = InferProfile(options.Duration).ToString(),
            RequestedDuration = options.Duration,
            DelayBetweenInspections = options.DelayBetweenInspections,
            OperatorId = options.OperatorId,
            BoardModel = string.IsNullOrWhiteSpace(options.BoardModel) ? "TBOX-MAIN" : options.BoardModel,
            LotId = string.IsNullOrWhiteSpace(options.LotId) ? "SOAK-TEST" : options.LotId,
            StartManagedMemoryMegabytes = BytesToMegabytes(GC.GetTotalMemory(forceFullCollection: false)),
            StartWorkingSetMegabytes = BytesToMegabytes(process.WorkingSet64),
            PeakWorkingSetMegabytes = BytesToMegabytes(process.WorkingSet64),
        };

        var source = new FolderCameraSource(
            new Dictionary<CameraViewType, string> { [CameraViewType.Top] = options.ImageFolder },
            result.BoardModel,
            result.LotId)
        {
            SelectedView = CameraViewType.Top,
        };
        source.StartAcquisition();
        result.SourceKind = source.ConnectionStatus == CameraSourceStatus.Simulated
            ? "FolderSimulation"
            : "RealCamera";
        result.IsRealCameraSource = source.ConnectionStatus == CameraSourceStatus.Ready;

        var engine = InspectionEngineFactory.Create(options.EngineKey);
        result.EngineName = engine.Name;
        result.EngineVersion = engine.Version;

        if (source.ConnectionStatus != CameraSourceStatus.Simulated)
        {
            result.Errors.Add($"Folder Camera Simulation is not ready: {source.StatusMessage}");
            CompleteResult(result, process);
            progress?.Report(new SoakTestProgress(0, Math.Max(1, (int)options.Duration.TotalSeconds), "Soak test could not start; no readable simulation images.", options.Duration, result.SuccessfulCycles, result.FailedCycles));
            return result;
        }

        var deadline = started + options.Duration;
        var cycleTimings = new List<double>();
        var totalCycleTimings = new List<double>();
        var totalSeconds = Math.Max(1, (int)Math.Ceiling(options.Duration.TotalSeconds));

        while (DateTime.Now < deadline && (options.MaxIterations is null || result.TotalCycles < options.MaxIterations.Value))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                result.WasCanceled = true;
                result.CancellationReason = "Cancellation requested before next iteration.";
                break;
            }

            var elapsedSeconds = Math.Min(totalSeconds, (int)Math.Max(0, (DateTime.Now - started).TotalSeconds));
            progress?.Report(new SoakTestProgress(
                elapsedSeconds,
                totalSeconds,
                $"Soak test running: elapsed={FormatDuration(DateTime.Now - started)}, remaining={FormatDuration(deadline - DateTime.Now)}, cycle {result.TotalCycles + 1}, pass={result.SuccessfulCycles}, fail={result.FailedCycles}.",
                deadline - DateTime.Now,
                result.SuccessfulCycles,
                result.FailedCycles,
                result.MaxInspectionMilliseconds,
                result.PeakWorkingSetMegabytes,
                result.CancellationReason));

            var cycleStopwatch = Stopwatch.StartNew();
            var frame = source.GetNextFrame();
            if (frame is null)
            {
                cycleStopwatch.Stop();
                result.TotalCycles++;
                result.FailedCycles++;
                AddCriticalError(result, "Frame acquisition returned no frame.");
                result.Cycles.Add(new SoakTestCycleRecord(
                    result.TotalCycles,
                    string.Empty,
                    string.Empty,
                    "ERROR",
                    0,
                    false,
                    "Frame acquisition returned no frame.",
                    DateTime.UtcNow,
                    result.EngineName,
                    CurrentWorkingSetMegabytes(process),
                    "No frame returned",
                    cycleStopwatch.Elapsed.TotalMilliseconds,
                    "NoFrame"));
                break;
            }

            result.TotalCycles++;
            try
            {
                var analysis = await Task.Run(
                    () => engine.Analyze(frame.ImagePath, null, DetectionPriority.Balanced),
                    cancellationToken);
                cycleStopwatch.Stop();
                analysis.BoardProgram = result.BoardModel;
                analysis.OperatorId = result.OperatorId;

                var totalMs = analysis.Timing.TotalInspectionMilliseconds;
                cycleTimings.Add(totalMs);
                totalCycleTimings.Add(cycleStopwatch.Elapsed.TotalMilliseconds);
                result.SuccessfulCycles++;
                if (analysis.Timing.IsOverOneSecond)
                    result.CountOverOneSecond++;

                result.Cycles.Add(new SoakTestCycleRecord(
                    result.TotalCycles,
                    frame.FrameId,
                    frame.ImagePath,
                    analysis.Verdict,
                    totalMs,
                    true,
                    analysis.DecisionReason,
                    DateTime.UtcNow,
                    result.EngineName,
                    CurrentWorkingSetMegabytes(process),
                    string.Empty,
                    cycleStopwatch.Elapsed.TotalMilliseconds));
            }
            catch (OperationCanceledException)
            {
                cycleStopwatch.Stop();
                result.WasCanceled = true;
                result.CancellationReason = "Cancellation requested during inspection.";
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or ArgumentException)
            {
                cycleStopwatch.Stop();
                result.FailedCycles++;
                var message = $"{Path.GetFileName(frame.ImagePath)}: {ex.GetType().Name} - {ex.Message}";
                AddCriticalError(result, message);
                totalCycleTimings.Add(cycleStopwatch.Elapsed.TotalMilliseconds);
                result.Cycles.Add(new SoakTestCycleRecord(
                    result.TotalCycles,
                    frame.FrameId,
                    frame.ImagePath,
                    "ERROR",
                    0,
                    false,
                    message,
                    DateTime.UtcNow,
                    result.EngineName,
                    CurrentWorkingSetMegabytes(process),
                    message,
                    cycleStopwatch.Elapsed.TotalMilliseconds,
                    ex.GetType().Name));
            }

            process.Refresh();
            result.PeakWorkingSetMegabytes = Math.Max(result.PeakWorkingSetMegabytes, BytesToMegabytes(process.WorkingSet64));
            if (result.PeakWorkingSetMegabytes > 0 &&
                result.StartWorkingSetMegabytes > 0 &&
                result.PeakWorkingSetMegabytes > result.StartWorkingSetMegabytes * 1.5 &&
                result.PeakWorkingSetMegabytes - result.StartWorkingSetMegabytes > 250)
            {
                var warning = $"Working set grew from {result.StartWorkingSetMegabytes:F1} MB to {result.PeakWorkingSetMegabytes:F1} MB.";
                if (!result.MemoryWarnings.Contains(warning, StringComparer.Ordinal))
                    result.MemoryWarnings.Add(warning);
            }

            if (options.DelayBetweenInspections > TimeSpan.Zero && DateTime.Now < deadline)
            {
                try
                {
                    await Task.Delay(options.DelayBetweenInspections, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    result.WasCanceled = true;
                    result.CancellationReason = "Cancellation requested during delay between inspections.";
                    break;
                }
            }
        }

        if (cycleTimings.Count > 0)
        {
            result.AverageInspectionMilliseconds = cycleTimings.Average();
            result.MinInspectionMilliseconds = cycleTimings.Min();
            result.MaxInspectionMilliseconds = cycleTimings.Max();
            result.P95InspectionMilliseconds = Percentile(cycleTimings, 0.95);
        }
        if (totalCycleTimings.Count > 0)
        {
            result.AverageTotalCycleMilliseconds = totalCycleTimings.Average();
            result.MaxTotalCycleMilliseconds = totalCycleTimings.Max();
            result.P95TotalCycleMilliseconds = Percentile(totalCycleTimings, 0.95);
        }

        CompleteResult(result, process);
        progress?.Report(new SoakTestProgress(
            totalSeconds,
            totalSeconds,
            result.WasCanceled ? $"Soak test canceled; partial report ready. {result.CancellationReason}" : "Soak test complete.",
            TimeSpan.Zero,
            result.SuccessfulCycles,
            result.FailedCycles,
            result.MaxInspectionMilliseconds,
            result.PeakWorkingSetMegabytes,
            result.CancellationReason));
        return result;
    }

    public static long Persist(SoakTestResult result, string? operatorId = null)
        => AoiDatabase.RecordSoakTestRun(result, operatorId);

    public static string WriteHtmlReport(SoakTestResult result, string outputFolder)
    {
        Directory.CreateDirectory(outputFolder);
        var path = Path.Combine(outputFolder, $"soak_test_report_{DateTime.Now:yyyyMMdd_HHmmss}.html");
        File.WriteAllText(path, BuildHtmlReport(result), Encoding.UTF8);
        return path;
    }

    public static string WriteJsonReport(SoakTestResult result, string outputFolder)
    {
        Directory.CreateDirectory(outputFolder);
        var path = Path.Combine(outputFolder, $"soak_test_report_{DateTime.Now:yyyyMMdd_HHmmss}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(result, JsonOptions), Encoding.UTF8);
        return path;
    }

    public static string WriteIterationsCsv(SoakTestResult result, string outputFolder)
    {
        Directory.CreateDirectory(outputFolder);
        var path = Path.Combine(outputFolder, $"soak_test_iterations_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
        var sb = new StringBuilder();
        sb.AppendLine("run_id,cycle_number,timestamp_utc,frame_id,image_path,verdict,engine,total_inspection_ms,total_cycle_ms,working_set_mb,success,exception_category,message,error");
        foreach (var cycle in result.Cycles)
        {
            sb.AppendLine(string.Join(",", new[]
            {
                Csv(result.RunId),
                cycle.CycleNumber.ToString(CultureInfo.InvariantCulture),
                Csv(cycle.TimestampUtc?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty),
                Csv(cycle.FrameId),
                Csv(cycle.ImagePath),
                Csv(cycle.Verdict),
                Csv(cycle.EngineName),
                cycle.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture),
                cycle.TotalCycleMilliseconds.ToString("F3", CultureInfo.InvariantCulture),
                cycle.WorkingSetMegabytes.ToString("F3", CultureInfo.InvariantCulture),
                cycle.Success ? "true" : "false",
                Csv(cycle.ExceptionCategory),
                Csv(cycle.Message),
                Csv(cycle.Error),
            }));
        }

        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        return path;
    }

    public static string BuildHtmlReport(SoakTestResult result)
    {
        var errorRows = result.Errors.Count == 0
            ? "<tr><td colspan=\"2\">No errors recorded.</td></tr>"
            : string.Join(Environment.NewLine, result.Errors.Take(100).Select((error, index) =>
                $"<tr><td>{index + 1}</td><td>{Html(error)}</td></tr>"));

        var cycleRows = result.Cycles.Count == 0
            ? "<tr><td colspan=\"12\">No inspection cycles were recorded.</td></tr>"
            : string.Join(Environment.NewLine, result.Cycles.Take(200).Select(cycle =>
                $"<tr><td>{cycle.CycleNumber}</td><td>{(cycle.TimestampUtc?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty)}</td><td>{Html(cycle.FrameId)}</td><td>{Html(Path.GetFileName(cycle.ImagePath))}</td><td>{Html(cycle.EngineName)}</td><td>{Html(cycle.Verdict)}</td><td>{cycle.TotalMilliseconds:F0} ms</td><td>{cycle.TotalCycleMilliseconds:F0} ms</td><td>{cycle.WorkingSetMegabytes:F1} MB</td><td>{(cycle.Success ? "Success" : "Failed")}</td><td>{Html(cycle.ExceptionCategory)}</td><td>{Html(string.IsNullOrWhiteSpace(cycle.Error) ? cycle.Message : cycle.Error)}</td></tr>"));

        return $$"""
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <title>AOI Monitor Soak Test Report</title>
          <style>
            body { font-family: Segoe UI, Arial, sans-serif; margin: 32px; color: #1d252c; line-height: 1.45; }
            h1 { margin-bottom: 4px; } h2 { margin-top: 28px; border-bottom: 2px solid #d7e0e6; padding-bottom: 6px; }
            table { border-collapse: collapse; width: 100%; margin: 12px 0 22px; }
            th, td { border: 1px solid #b8c1c8; padding: 7px 9px; text-align: left; vertical-align: top; }
            th { background: #edf2f5; }
            .notice { border-left: 5px solid #d9951b; background: #fff8e8; padding: 12px 14px; margin: 18px 0; }
            .ok { border-left-color: #2c8a45; background: #edf8f0; }
          </style>
        </head>
        <body>
          <h1>AOI Monitor Soak Test Report</h1>
          <p>Run ID: {{Html(result.RunId)}}</p>
          <div class="notice {{(result.Errors.Count == 0 && !result.WasCanceled ? "ok" : string.Empty)}}">This is controlled PoC stability evidence marked as {{Html(result.SourceKind)}}. Simulated-source evidence does not indicate live camera, lighting, production robot, PLC, production MES, or Stage 2 Planned Hardware Integration completion.</div>

          <h2>Run Summary</h2>
          <table>
            <tr><th>Start time</th><td>{{result.StartTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}}</td></tr>
            <tr><th>End time</th><td>{{result.EndTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}}</td></tr>
            <tr><th>Requested duration</th><td>{{FormatDuration(result.RequestedDuration)}}</td></tr>
            <tr><th>Delay between inspections</th><td>{{FormatDuration(result.DelayBetweenInspections)}}</td></tr>
            <tr><th>Status</th><td>{{(result.WasCanceled ? "Canceled by user" : "Completed")}}</td></tr>
            <tr><th>Cancellation reason</th><td>{{Html(result.CancellationReason)}}</td></tr>
            <tr><th>Factory PoC evidence accepted</th><td>{{(result.IsCompletedFactoryEvidence ? "YES" : "NO")}}</td></tr>
            <tr><th>Source kind</th><td>{{Html(result.SourceKind)}}</td></tr>
            <tr><th>Operator</th><td>{{Html(result.OperatorId)}}</td></tr>
            <tr><th>Board model</th><td>{{Html(result.BoardModel)}}</td></tr>
            <tr><th>Lot ID</th><td>{{Html(result.LotId)}}</td></tr>
            <tr><th>Image folder</th><td>{{Html(result.ImageFolder)}}</td></tr>
            <tr><th>Engine</th><td>{{Html(result.EngineName)}} / {{Html(result.EngineVersion)}}</td></tr>
          </table>

          <h2>Stability Metrics</h2>
          <table>
            <tr><th>Total cycles</th><td>{{result.TotalCycles}}</td></tr>
            <tr><th>Successful cycles</th><td>{{result.SuccessfulCycles}}</td></tr>
            <tr><th>Failed cycles</th><td>{{result.FailedCycles}}</td></tr>
            <tr><th>Average inspection time</th><td>{{FormatMilliseconds(result.AverageInspectionMilliseconds)}}</td></tr>
            <tr><th>Min inspection time</th><td>{{FormatMilliseconds(result.MinInspectionMilliseconds)}}</td></tr>
            <tr><th>Max inspection time</th><td>{{FormatMilliseconds(result.MaxInspectionMilliseconds)}}</td></tr>
            <tr><th>P95 inspection time</th><td>{{FormatMilliseconds(result.P95InspectionMilliseconds)}}</td></tr>
            <tr><th>Average total cycle time</th><td>{{FormatMilliseconds(result.AverageTotalCycleMilliseconds)}}</td></tr>
            <tr><th>Max total cycle time</th><td>{{FormatMilliseconds(result.MaxTotalCycleMilliseconds)}}</td></tr>
            <tr><th>P95 total cycle time</th><td>{{FormatMilliseconds(result.P95TotalCycleMilliseconds)}}</td></tr>
            <tr><th>Count over 1 second</th><td>{{result.CountOverOneSecond}}</td></tr>
            <tr><th>Managed memory start/end</th><td>{{result.StartManagedMemoryMegabytes:F1}} MB / {{result.EndManagedMemoryMegabytes:F1}} MB</td></tr>
            <tr><th>Working set start/end/peak</th><td>{{result.StartWorkingSetMegabytes:F1}} MB / {{result.EndWorkingSetMegabytes:F1}} MB / {{result.PeakWorkingSetMegabytes:F1}} MB</td></tr>
            <tr><th>GC / memory warnings</th><td>{{Html(result.MemoryWarnings.Count == 0 ? "None" : string.Join(" ", result.MemoryWarnings))}}</td></tr>
            <tr><th>First critical error</th><td>{{Html(result.FirstCriticalError)}}</td></tr>
            <tr><th>Error count</th><td>{{result.Errors.Count}}</td></tr>
          </table>

          <h2>Errors / Exceptions</h2>
          <table><tr><th>No</th><th>Message</th></tr>{{errorRows}}</table>

          <h2>Cycle Samples</h2>
          <p>First 200 cycle records are shown for readability. Full stability summary is in the metrics above.</p>
          <table><tr><th>Cycle</th><th>UTC timestamp</th><th>Frame</th><th>Image</th><th>Engine</th><th>Verdict</th><th>Inspection time</th><th>Total cycle</th><th>Working set</th><th>Status</th><th>Exception</th><th>Error / Message</th></tr>{{cycleRows}}</table>
        </body>
        </html>
        """;
    }

    private static void AddCriticalError(SoakTestResult result, string message)
    {
        result.Errors.Add(message);
        if (string.IsNullOrWhiteSpace(result.FirstCriticalError))
            result.FirstCriticalError = message;
    }

    private static string Csv(string value)
        => $"\"{(value ?? string.Empty).Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static void CompleteResult(SoakTestResult result, Process process)
    {
        result.EndTime = DateTime.Now;
        result.EndManagedMemoryMegabytes = BytesToMegabytes(GC.GetTotalMemory(forceFullCollection: false));
        process.Refresh();
        result.EndWorkingSetMegabytes = BytesToMegabytes(process.WorkingSet64);
        result.PeakWorkingSetMegabytes = Math.Max(result.PeakWorkingSetMegabytes, result.EndWorkingSetMegabytes);
    }

    private static double BytesToMegabytes(long bytes)
        => bytes / 1024.0 / 1024.0;

    private static double CurrentWorkingSetMegabytes(Process process)
    {
        process.Refresh();
        return BytesToMegabytes(process.WorkingSet64);
    }

    public static double Percentile(IEnumerable<double> values, double percentile)
    {
        var ordered = values.Where(value => value >= 0).OrderBy(value => value).ToArray();
        if (ordered.Length == 0)
            return 0;

        var rank = Math.Clamp(percentile, 0, 1) * (ordered.Length - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        if (lower == upper)
            return ordered[lower];

        var weight = rank - lower;
        return ordered[lower] + ((ordered[upper] - ordered[lower]) * weight);
    }

    private static SoakTestProfile InferProfile(TimeSpan duration)
    {
        if (duration == TimeSpan.FromMinutes(5))
            return SoakTestProfile.QuickSmoke;
        if (duration == TimeSpan.FromMinutes(30))
            return SoakTestProfile.ShortStability;
        if (duration == TimeSpan.FromHours(8))
            return SoakTestProfile.FactoryPoc;
        return SoakTestProfile.Custom;
    }

    private static string FormatMilliseconds(double value)
        => value <= 0 ? "--" : $"{value:F0} ms";

    private static string FormatDuration(TimeSpan value)
        => value.TotalMilliseconds < 1000
            ? $"{value.TotalMilliseconds:F0} ms"
            : value.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);

    private static string Html(string? value)
        => (value ?? string.Empty)
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);
}
