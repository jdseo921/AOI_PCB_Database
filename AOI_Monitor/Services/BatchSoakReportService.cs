using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AOI_Monitor.Services;

/// <summary>
/// Writes the batch-soak evidence artifacts (HTML, JSON, per-pass CSV) with invariant
/// number formatting and the truthful uploaded-image scope statement in every artifact,
/// and records a verified export for the run folder.
/// </summary>
public static class BatchSoakReportService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>Writes all three artifacts into <paramref name="outputFolder"/> and records a verified export.</summary>
    public static BatchSoakReportPaths WriteReports(BatchSoakResult result, string outputFolder)
    {
        Directory.CreateDirectory(outputFolder);
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var htmlPath = Path.Combine(outputFolder, $"batch_soak_report_{stamp}.html");
        var jsonPath = Path.Combine(outputFolder, $"batch_soak_report_{stamp}.json");
        var csvPath = Path.Combine(outputFolder, $"batch_soak_passes_{stamp}.csv");
        File.WriteAllText(htmlPath, BuildHtmlReport(result), Encoding.UTF8);
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(result, JsonOptions), Encoding.UTF8);
        File.WriteAllText(csvPath, BuildPassesCsv(result), Encoding.UTF8);
        ExportVerificationService.RecordVerifiedExport(
            "Stage1BatchSoak",
            outputFolder,
            result.Status == "PASS" ? "OK" : "WARN",
            result.OperatorId);
        return new BatchSoakReportPaths(htmlPath, jsonPath, csvPath);
    }

    /// <summary>Builds the per-pass CSV; the leading comment lines carry the scope statement and run identity.</summary>
    public static string BuildPassesCsv(BatchSoakResult result)
    {
        var sb = new StringBuilder();
        sb.Append("# ").AppendLine(BatchSoakTestService.ScopeStatement);
        sb.Append("# run_id=").Append(result.RunId)
          .Append(" software_version=").Append(result.SoftwareVersion)
          .Append(" status=").AppendLine(result.Status);
        sb.AppendLine("run_id,pass_number,started_at_utc,duration_ms,images,ok,ng,review,errors,avg_inspection_ms,max_inspection_ms,over_one_second,managed_mb,working_set_mb,handle_count,thread_count,database_mb,batch_run_id,first_error");
        foreach (var pass in result.Passes)
        {
            sb.AppendLine(string.Join(",", new[]
            {
                Csv(result.RunId),
                pass.PassNumber.ToString(CultureInfo.InvariantCulture),
                Csv(pass.StartedAtUtc.ToString("O", CultureInfo.InvariantCulture)),
                pass.DurationMilliseconds.ToString("F1", CultureInfo.InvariantCulture),
                pass.ImagesProcessed.ToString(CultureInfo.InvariantCulture),
                pass.OkCount.ToString(CultureInfo.InvariantCulture),
                pass.NgCount.ToString(CultureInfo.InvariantCulture),
                pass.ReviewCount.ToString(CultureInfo.InvariantCulture),
                pass.ErrorCount.ToString(CultureInfo.InvariantCulture),
                pass.AverageInspectionMilliseconds.ToString("F1", CultureInfo.InvariantCulture),
                pass.MaxInspectionMilliseconds.ToString("F1", CultureInfo.InvariantCulture),
                pass.CountOverOneSecond.ToString(CultureInfo.InvariantCulture),
                pass.ManagedMemoryMegabytes.ToString("F2", CultureInfo.InvariantCulture),
                pass.WorkingSetMegabytes.ToString("F2", CultureInfo.InvariantCulture),
                pass.HandleCount.ToString(CultureInfo.InvariantCulture),
                pass.ThreadCount.ToString(CultureInfo.InvariantCulture),
                pass.DatabaseSizeMegabytes.ToString("F2", CultureInfo.InvariantCulture),
                pass.BatchRunId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                Csv(pass.FirstError),
            }));
        }

        return sb.ToString();
    }

    /// <summary>Builds the operator-facing HTML report (invariant formatting, no stack traces).</summary>
    public static string BuildHtmlReport(BatchSoakResult result)
    {
        var failRows = result.FailReasons.Count == 0
            ? "<tr><td colspan=\"2\">No failure conditions were triggered.</td></tr>"
            : string.Join(Environment.NewLine, result.FailReasons.Distinct(StringComparer.Ordinal).Select((reason, index) =>
                Invariant($"<tr><td>{index + 1}</td><td>{Html(reason)}</td></tr>")));

        var errorRows = result.Errors.Count == 0
            ? "<tr><td colspan=\"2\">No errors recorded.</td></tr>"
            : string.Join(Environment.NewLine, result.Errors.Take(100).Select((error, index) =>
                Invariant($"<tr><td>{index + 1}</td><td>{Html(error)}</td></tr>")));

        var alarmRows = result.AlarmSummaries.Count == 0
            ? "<tr><td>No new alarm events were raised during the run.</td></tr>"
            : string.Join(Environment.NewLine, result.AlarmSummaries.Take(50).Select(summary =>
                Invariant($"<tr><td>{Html(summary)}</td></tr>")));

        var passRows = result.Passes.Count == 0
            ? "<tr><td colspan=\"16\">No batch passes were recorded.</td></tr>"
            : string.Join(Environment.NewLine, result.Passes.Take(300).Select(pass =>
                Invariant($"<tr><td>{pass.PassNumber}</td><td>{pass.StartedAtUtc.ToString("O", CultureInfo.InvariantCulture)}</td><td>{pass.DurationMilliseconds / 1000.0:F1} s</td><td>{pass.ImagesProcessed}</td><td>{pass.OkCount}</td><td>{pass.NgCount}</td><td>{pass.ReviewCount}</td><td>{pass.ErrorCount}</td><td>{pass.AverageInspectionMilliseconds:F0} ms</td><td>{pass.MaxInspectionMilliseconds:F0} ms</td><td>{pass.CountOverOneSecond}</td><td>{pass.ManagedMemoryMegabytes:F1} MB</td><td>{pass.WorkingSetMegabytes:F1} MB</td><td>{pass.HandleCount}</td><td>{pass.DatabaseSizeMegabytes:F2} MB</td><td>{Html(pass.FirstError)}</td></tr>")));

        var statusClass = result.Status == "PASS" ? "ok" : string.Empty;
        var head = """
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <title>AOI Monitor Stage 1 Batch Soak Report</title>
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
        """;

        var body = Invariant($"""
          <h1>AOI Monitor Stage 1 Batch Soak Report</h1>
          <p>Run ID: {Html(result.RunId)}</p>
          <div class="notice">{Html(BatchSoakTestService.ScopeStatement)}</div>
          <div class="notice {statusClass}">Result: {Html(result.Status)}{(result.WasCanceled ? " - " + Html(result.CancellationReason) : string.Empty)}. 8-hour uploaded-image PoC evidence: {(result.IsEightHourUploadedImagePoCEvidence ? "YES" : "NO")}.</div>

          <h2>Run Summary</h2>
          <table>
            <tr><th>Software version</th><td>{Html(result.SoftwareVersion)}</td></tr>
            <tr><th>Machine / OS</th><td>{Html(result.MachineName)} / {Html(result.OsInfo)}</td></tr>
            <tr><th>Started (UTC)</th><td>{result.StartedAtUtc.ToString("O", CultureInfo.InvariantCulture)}</td></tr>
            <tr><th>Completed (UTC)</th><td>{result.CompletedAtUtc.ToString("O", CultureInfo.InvariantCulture)}</td></tr>
            <tr><th>Requested / actual duration</th><td>{FormatDuration(result.RequestedDuration)} / {FormatDuration(result.ActualDuration)} (actual measured monotonically)</td></tr>
            <tr><th>Engine</th><td>{Html(result.EngineConfig.EngineName)} / {Html(result.EngineConfig.EngineVersion)} (key: {Html(result.EngineConfig.EngineKey)})</td></tr>
            <tr><th>Detection priority</th><td>{Html(result.EngineConfig.DetectionPriority)}</td></tr>
            <tr><th>Model configuration</th><td>ONNX selected: {(result.EngineConfig.OnnxSelected ? "yes" : "no")}; active model: {Html(BlankAs(result.EngineConfig.ActiveModelId, "none"))}; SHA-256: {Html(BlankAs(result.EngineConfig.ActiveModelSha256, "n/a"))}; confidence threshold: {result.EngineConfig.ConfidenceThreshold.ToString("F2", CultureInfo.InvariantCulture)}</td></tr>
            <tr><th>Threshold profile</th><td>{Html(BlankAs(result.ThresholdProfileId, "none reported"))}{(string.IsNullOrWhiteSpace(result.ThresholdProfileRevision) ? string.Empty : " / revision " + Html(result.ThresholdProfileRevision))}</td></tr>
            <tr><th>Image folder</th><td>{Html(result.ImageFolder)}</td></tr>
            <tr><th>Dataset at start</th><td>{result.DatasetImageCountAtStart} images; file-list fingerprint (names+sizes, SHA-256): {Html(result.DatasetFingerprintSha256)}</td></tr>
            <tr><th>Manifest</th><td>{Html(BlankAs(result.ManifestPath, "none (unlabeled soak)"))}</td></tr>
            <tr><th>Operator</th><td>{Html(result.OperatorId)}</td></tr>
            <tr><th>Board model / lot</th><td>{Html(result.BoardModel)} / {Html(result.LotId)}</td></tr>
            <tr><th>Batch runs persisted to SQLite</th><td>{(result.BatchRunsPersisted ? "yes" : "no")}</td></tr>
          </table>

          <h2>Stability Metrics</h2>
          <table>
            <tr><th>Total passes</th><td>{result.TotalPasses}</td></tr>
            <tr><th>Total images processed</th><td>{result.TotalImagesProcessed} (OK {result.TotalOkCount} / NG {result.TotalNgCount} / REVIEW {result.TotalReviewCount} / ERROR {result.TotalErrorCount})</td></tr>
            <tr><th>Per-image inspection time</th><td>avg {FormatMilliseconds(result.AverageInspectionMilliseconds)}, max {FormatMilliseconds(result.MaxInspectionMilliseconds)}, p95 {FormatMilliseconds(result.P95InspectionMilliseconds)}, over 1 s: {result.CountOverOneSecond}</td></tr>
            <tr><th>Pass duration</th><td>avg {FormatMilliseconds(result.AveragePassMilliseconds)}, max {FormatMilliseconds(result.MaxPassMilliseconds)}</td></tr>
            <tr><th>Managed memory start/end/peak</th><td>{result.StartManagedMemoryMegabytes:F1} / {result.EndManagedMemoryMegabytes:F1} / {result.PeakManagedMemoryMegabytes:F1} MB</td></tr>
            <tr><th>Working set start/end/peak</th><td>{result.StartWorkingSetMegabytes:F1} / {result.EndWorkingSetMegabytes:F1} / {result.PeakWorkingSetMegabytes:F1} MB</td></tr>
            <tr><th>Handle count start/end/peak</th><td>{result.StartHandleCount} / {result.EndHandleCount} / {result.PeakHandleCount}</td></tr>
            <tr><th>SQLite size start/end/growth</th><td>{result.StartDatabaseSizeMegabytes:F2} / {result.EndDatabaseSizeMegabytes:F2} / {result.DatabaseGrowthMegabytes:F2} MB</td></tr>
            <tr><th>Managed-memory trend</th><td>{Html(result.MemoryTrend.Description)}</td></tr>
            <tr><th>New alarm events during run</th><td>{result.NewAlarmEventCount} (active critical at end: {result.ActiveCriticalAlarmCountAtEnd})</td></tr>
            <tr><th>Error count</th><td>{result.Errors.Count}</td></tr>
          </table>

          <h2>Failure Conditions</h2>
          <table><tr><th>No</th><th>Reason</th></tr>{failRows}</table>

          <h2>Alarm Events</h2>
          <table><tr><th>Summary</th></tr>{alarmRows}</table>

          <h2>Errors</h2>
          <p>Messages are operator-safe (type and message only). Full technical traces, when any, are in {Html(BatchSoakTestService.EngineerDebugFileName)} in the run folder.</p>
          <table><tr><th>No</th><th>Message</th></tr>{errorRows}</table>

          <h2>Pass Samples</h2>
          <p>First 300 pass records are shown for readability. The full series is in the JSON report and passes CSV.</p>
          <table><tr><th>Pass</th><th>Started (UTC)</th><th>Duration</th><th>Images</th><th>OK</th><th>NG</th><th>REVIEW</th><th>ERR</th><th>Avg</th><th>Max</th><th>&gt;1 s</th><th>Managed</th><th>Working set</th><th>Handles</th><th>DB size</th><th>First error</th></tr>{passRows}</table>
        </body>
        </html>
        """);

        return head + body;
    }

    private static string Invariant(FormattableString value)
        => value.ToString(CultureInfo.InvariantCulture);

    private static string BlankAs(string value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static string Csv(string value)
        => $"\"{(value ?? string.Empty).Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static string FormatMilliseconds(double value)
        => value <= 0 ? "--" : value.ToString("F0", CultureInfo.InvariantCulture) + " ms";

    private static string FormatDuration(TimeSpan value)
        => value.TotalMilliseconds < 1000
            ? value.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture) + " ms"
            : value.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);

    private static string Html(string? value)
        => (value ?? string.Empty)
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);
}
