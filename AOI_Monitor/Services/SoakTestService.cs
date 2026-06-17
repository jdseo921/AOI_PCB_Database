using System.Diagnostics;
using System.Globalization;
using System.IO;
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
    string LotId);

public sealed record SoakTestProgress(
    int ElapsedSeconds,
    int TotalSeconds,
    string Message);

public sealed record SoakTestCycleRecord(
    int CycleNumber,
    string FrameId,
    string ImagePath,
    string Verdict,
    double TotalMilliseconds,
    bool Success,
    string Message);

public sealed class SoakTestResult
{
    public string RunId { get; init; } = Guid.NewGuid().ToString("N");
    public DateTime StartTime { get; init; } = DateTime.Now;
    public DateTime EndTime { get; set; } = DateTime.Now;
    public string ImageFolder { get; init; } = string.Empty;
    public string OutputFolder { get; init; } = string.Empty;
    public string EngineName { get; set; } = "Unknown";
    public string EngineVersion { get; set; } = "UNKNOWN";
    public string EngineKey { get; init; } = InspectionEngineFactory.DefaultEngineKey;
    public TimeSpan RequestedDuration { get; init; }
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
    public int CountOverOneSecond { get; set; }
    public double StartManagedMemoryMegabytes { get; init; }
    public double EndManagedMemoryMegabytes { get; set; }
    public double StartWorkingSetMegabytes { get; init; }
    public double EndWorkingSetMegabytes { get; set; }
    public double PeakWorkingSetMegabytes { get; set; }
    public List<string> Errors { get; } = new();
    public List<SoakTestCycleRecord> Cycles { get; } = new();
}

public static class SoakTestService
{
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

        var engine = InspectionEngineFactory.Create(options.EngineKey);
        result.EngineName = engine.Name;
        result.EngineVersion = engine.Version;

        if (source.ConnectionStatus != CameraSourceStatus.Simulated)
        {
            result.Errors.Add($"Folder camera simulator is not ready: {source.StatusMessage}");
            CompleteResult(result, process);
            progress?.Report(new SoakTestProgress(0, Math.Max(1, (int)options.Duration.TotalSeconds), "Soak test could not start; no readable simulation images."));
            return result;
        }

        var deadline = started + options.Duration;
        var cycleTimings = new List<double>();
        var totalSeconds = Math.Max(1, (int)Math.Ceiling(options.Duration.TotalSeconds));

        while (DateTime.Now < deadline)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                result.WasCanceled = true;
                break;
            }

            var elapsedSeconds = Math.Min(totalSeconds, (int)Math.Max(0, (DateTime.Now - started).TotalSeconds));
            progress?.Report(new SoakTestProgress(elapsedSeconds, totalSeconds, $"Soak test running: cycle {result.TotalCycles + 1}, success={result.SuccessfulCycles}, failed={result.FailedCycles}."));

            var frame = source.GetNextFrame();
            if (frame is null)
            {
                result.FailedCycles++;
                result.Errors.Add("Folder camera simulator returned no frame.");
                break;
            }

            result.TotalCycles++;
            try
            {
                var analysis = await Task.Run(
                    () => engine.Analyze(frame.ImagePath, null, DetectionPriority.Balanced),
                    cancellationToken);
                analysis.BoardProgram = result.BoardModel;
                analysis.OperatorId = result.OperatorId;

                var totalMs = analysis.Timing.TotalInspectionMilliseconds;
                cycleTimings.Add(totalMs);
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
                    analysis.DecisionReason));
            }
            catch (OperationCanceledException)
            {
                result.WasCanceled = true;
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
            {
                result.FailedCycles++;
                var message = $"{Path.GetFileName(frame.ImagePath)}: {ex.GetType().Name} - {ex.Message}";
                result.Errors.Add(message);
                result.Cycles.Add(new SoakTestCycleRecord(
                    result.TotalCycles,
                    frame.FrameId,
                    frame.ImagePath,
                    "ERROR",
                    0,
                    false,
                    message));
            }

            process.Refresh();
            result.PeakWorkingSetMegabytes = Math.Max(result.PeakWorkingSetMegabytes, BytesToMegabytes(process.WorkingSet64));

            if (options.DelayBetweenInspections > TimeSpan.Zero && DateTime.Now < deadline)
            {
                try
                {
                    await Task.Delay(options.DelayBetweenInspections, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    result.WasCanceled = true;
                    break;
                }
            }
        }

        if (cycleTimings.Count > 0)
        {
            result.AverageInspectionMilliseconds = cycleTimings.Average();
            result.MinInspectionMilliseconds = cycleTimings.Min();
            result.MaxInspectionMilliseconds = cycleTimings.Max();
        }

        CompleteResult(result, process);
        progress?.Report(new SoakTestProgress(totalSeconds, totalSeconds, result.WasCanceled ? "Soak test canceled; partial report ready." : "Soak test complete."));
        return result;
    }

    public static string WriteHtmlReport(SoakTestResult result, string outputFolder)
    {
        Directory.CreateDirectory(outputFolder);
        var path = Path.Combine(outputFolder, $"soak_test_report_{DateTime.Now:yyyyMMdd_HHmmss}.html");
        File.WriteAllText(path, BuildHtmlReport(result), Encoding.UTF8);
        return path;
    }

    public static string BuildHtmlReport(SoakTestResult result)
    {
        var errorRows = result.Errors.Count == 0
            ? "<tr><td colspan=\"2\">No errors recorded.</td></tr>"
            : string.Join(Environment.NewLine, result.Errors.Take(100).Select((error, index) =>
                $"<tr><td>{index + 1}</td><td>{Html(error)}</td></tr>"));

        var cycleRows = result.Cycles.Count == 0
            ? "<tr><td colspan=\"7\">No inspection cycles were recorded.</td></tr>"
            : string.Join(Environment.NewLine, result.Cycles.Take(200).Select(cycle =>
                $"<tr><td>{cycle.CycleNumber}</td><td>{Html(cycle.FrameId)}</td><td>{Html(Path.GetFileName(cycle.ImagePath))}</td><td>{Html(cycle.Verdict)}</td><td>{cycle.TotalMilliseconds:F0} ms</td><td>{(cycle.Success ? "Success" : "Failed")}</td><td>{Html(cycle.Message)}</td></tr>"));

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
          <div class="notice {{(result.Errors.Count == 0 && !result.WasCanceled ? "ok" : string.Empty)}}">This is controlled local PoC stability evidence using folder-simulated camera frames. It does not indicate live camera, lighting, robot, PLC, MES, or production hardware integration.</div>

          <h2>Run Summary</h2>
          <table>
            <tr><th>Start time</th><td>{{result.StartTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}}</td></tr>
            <tr><th>End time</th><td>{{result.EndTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}}</td></tr>
            <tr><th>Requested duration</th><td>{{FormatDuration(result.RequestedDuration)}}</td></tr>
            <tr><th>Delay between inspections</th><td>{{FormatDuration(result.DelayBetweenInspections)}}</td></tr>
            <tr><th>Status</th><td>{{(result.WasCanceled ? "Canceled by user" : "Completed")}}</td></tr>
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
            <tr><th>Count over 1 second</th><td>{{result.CountOverOneSecond}}</td></tr>
            <tr><th>Managed memory start/end</th><td>{{result.StartManagedMemoryMegabytes:F1}} MB / {{result.EndManagedMemoryMegabytes:F1}} MB</td></tr>
            <tr><th>Working set start/end/peak</th><td>{{result.StartWorkingSetMegabytes:F1}} MB / {{result.EndWorkingSetMegabytes:F1}} MB / {{result.PeakWorkingSetMegabytes:F1}} MB</td></tr>
            <tr><th>Error count</th><td>{{result.Errors.Count}}</td></tr>
          </table>

          <h2>Errors / Exceptions</h2>
          <table><tr><th>No</th><th>Message</th></tr>{{errorRows}}</table>

          <h2>Cycle Samples</h2>
          <p>First 200 cycle records are shown for readability. Full stability summary is in the metrics above.</p>
          <table><tr><th>Cycle</th><th>Frame</th><th>Image</th><th>Verdict</th><th>Total time</th><th>Status</th><th>Message</th></tr>{{cycleRows}}</table>
        </body>
        </html>
        """;
    }

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
