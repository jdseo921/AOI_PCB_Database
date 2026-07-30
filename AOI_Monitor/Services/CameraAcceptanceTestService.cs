using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using AOI_Monitor.Data;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public static class CameraAcceptanceTestService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static CameraAcceptanceRun Run(
        CameraSourceSettings settings,
        CameraAcceptanceCriteria? criteria = null,
        IVisionCameraAdapter? adapter = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default,
        ICameraSource? sourceOverride = null)
    {
        criteria ??= new CameraAcceptanceCriteria();
        NormalizeCriteria(criteria);

        var source = sourceOverride ?? CameraSourceFactory.Create(settings, adapter);
        var run = new CameraAcceptanceRun
        {
            CreatedAtUtc = DateTime.UtcNow,
            AdapterName = source.Name,
            SourceKey = CameraSourceFactory.NormalizeSourceKey(settings.SourceKey),
            SettingsSummary = BuildSettingsSummary(settings),
            Criteria = criteria,
        };

        foreach (var viewName in criteria.RequiredViews)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var view = ParseView(viewName);
            progress?.Report($"Testing {view} camera view...");
            var viewMetrics = TestView(source, view, criteria, settings, run, progress, cancellationToken);
            run.ViewMetrics.Add(viewMetrics);
        }

        run.TotalRequestedFrames = run.ViewMetrics.Sum(view => view.RequestedFrames);
        run.TotalReceivedFrames = run.ViewMetrics.Sum(view => view.ReceivedFrames);
        run.DroppedFrameCount = run.ViewMetrics.Sum(view => view.DroppedFrameCount);
        run.TriggerFailureCount = run.ViewMetrics.Sum(view => view.TriggerFailureCount);
        run.TimeoutCount = run.ViewMetrics.Sum(view => view.TimeoutCount);
        run.MaxConnectMs = run.ViewMetrics.Count == 0 ? 0 : run.ViewMetrics.Max(view => view.ConnectMs);
        run.MaxFirstFrameMs = run.ViewMetrics.Count == 0 ? 0 : run.ViewMetrics.Max(view => view.FirstFrameMs);
        run.AverageFrameIntervalMs = run.ViewMetrics
            .Where(view => view.AverageFrameIntervalMs > 0)
            .DefaultIfEmpty(new CameraAcceptanceViewMetrics())
            .Average(view => view.AverageFrameIntervalMs);
        run.Warnings = run.ViewMetrics.SelectMany(view => view.Warnings).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        run.Failures = run.ViewMetrics.SelectMany(view => view.Failures).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var technicalWarningCount = run.Warnings.Count;

        run.IsRealHardware = run.TotalReceivedFrames > 0 &&
            run.Frames.All(frame => !frame.IsSimulated) &&
            !string.Equals(run.SourceKey, CameraSourceFactory.FolderSimulationSourceKey, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(run.SourceKey, CameraSourceFactory.NullSourceKey, StringComparison.OrdinalIgnoreCase);
        if (!run.IsRealHardware)
            run.Warnings.Add("Camera acceptance was run against a fake, null, or folder source. This is simulation evidence only and does not validate real GigE/USB3 hardware.");

        run.Status = run.Failures.Count > 0
            ? "FAIL"
            : technicalWarningCount > 0
                ? "WARN"
                : "PASS";
        run.FactoryReadinessStatus = run.IsRealHardware && run.Status is "PASS" or "WARN"
            ? run.Status
            : "NOT VALIDATED";

        return run;
    }

    public static long RunAndPersist(
        CameraSourceSettings settings,
        CameraAcceptanceCriteria? criteria = null,
        IVisionCameraAdapter? adapter = null,
        string? operatorId = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var run = Run(settings, criteria, adapter, progress, cancellationToken);
        return AoiDatabase.RecordCameraAcceptanceRun(run, operatorId);
    }

    public static CameraAcceptanceSummary ToSummary(CameraAcceptanceRun? run)
    {
        if (run is null)
        {
            return new CameraAcceptanceSummary
            {
                Status = "NOT VALIDATED",
                AcceptanceStatus = "NOT RUN",
                Messages = { "No real camera acceptance run has been recorded." },
            };
        }

        var summary = new CameraAcceptanceSummary
        {
            Status = run.IsRealHardware ? run.FactoryReadinessStatus : "NOT VALIDATED",
            AcceptanceStatus = run.Status,
            IsRealHardware = run.IsRealHardware,
            CreatedAtUtc = run.CreatedAtUtc,
            AdapterName = run.AdapterName,
            SourceKey = run.SourceKey,
            TotalRequestedFrames = run.TotalRequestedFrames,
            TotalReceivedFrames = run.TotalReceivedFrames,
            DroppedFrameCount = run.DroppedFrameCount,
            TriggerFailureCount = run.TriggerFailureCount,
            TimeoutCount = run.TimeoutCount,
        };

        summary.Messages.AddRange(run.Failures);
        summary.Messages.AddRange(run.Warnings);
        if (!run.IsRealHardware)
            summary.Messages.Insert(0, "Latest camera acceptance evidence is simulation-only; real camera readiness is NOT VALIDATED.");
        if (summary.Messages.Count == 0)
            summary.Messages.Add("Camera acceptance criteria passed on real hardware.");
        return summary;
    }

    public static CameraAcceptanceReportExport ExportReport(CameraAcceptanceRun run, string? outputRoot = null)
    {
        var root = string.IsNullOrWhiteSpace(outputRoot)
            ? Path.Combine(AoiDatabase.StorageRoot, "exports", "camera_acceptance")
            : outputRoot;
        Directory.CreateDirectory(root);

        var stamp = run.CreatedAtUtc.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var jsonPath = Path.Combine(root, $"camera_acceptance_{stamp}.json");
        var htmlPath = Path.Combine(root, $"camera_acceptance_{stamp}.html");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(run, JsonOptions), Encoding.UTF8);
        File.WriteAllText(htmlPath, BuildHtml(run), Encoding.UTF8);
        return new CameraAcceptanceReportExport(jsonPath, htmlPath);
    }

    public static string BuildHtml(CameraAcceptanceRun run)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html><html><head><meta charset=\"utf-8\"><title>Camera Acceptance</title>");
        sb.AppendLine("<style>body{font-family:Segoe UI,Arial,sans-serif;background:#f7f9fb;color:#17212b;margin:24px}.card{background:white;border:1px solid #d8e0e7;padding:16px;margin:0 0 16px}table{border-collapse:collapse;width:100%}td,th{border:1px solid #d8e0e7;padding:6px;text-align:left}.warn{color:#8a5a00}.fail{color:#a12626}.ok{color:#176b3a}</style></head><body>");
        sb.AppendLine("<h1>Stage 2 Camera Acceptance Test</h1>");
        sb.AppendLine($"<div class=\"card\"><strong>Status:</strong> {Escape(run.Status)}<br><strong>Factory readiness:</strong> {Escape(run.FactoryReadinessStatus)}<br><strong>Adapter:</strong> {Escape(run.AdapterName)}<br><strong>Source:</strong> {Escape(run.SourceKey)}<br><strong>Real hardware:</strong> {run.IsRealHardware}</div>");
        sb.AppendLine("<div class=\"card\"><h2>Metrics</h2><table><tr><th>Requested</th><th>Received</th><th>Dropped</th><th>Trigger Failures</th><th>Timeouts</th><th>Max Connect ms</th><th>Max First Frame ms</th><th>Avg Interval ms</th></tr>");
        sb.AppendLine(CultureInfo.InvariantCulture, $"<tr><td>{run.TotalRequestedFrames}</td><td>{run.TotalReceivedFrames}</td><td>{run.DroppedFrameCount}</td><td>{run.TriggerFailureCount}</td><td>{run.TimeoutCount}</td><td>{run.MaxConnectMs:F1}</td><td>{run.MaxFirstFrameMs:F1}</td><td>{run.AverageFrameIntervalMs:F1}</td></tr></table></div>");
        AppendMessages(sb, "Blocking Failures", run.Failures, "fail");
        AppendMessages(sb, "Warnings / Limitations", run.Warnings, "warn");
        sb.AppendLine("<div class=\"card\"><h2>Per View / Trigger Synchronization Evidence</h2><table><tr><th>View</th><th>Status</th><th>Received</th><th>Dropped</th><th>Trigger Failures</th><th>Timeouts</th><th>Connect ms</th><th>First ms</th><th>Avg interval ms</th><th>Trigger mode</th><th>Trigger-to-frame ms</th><th>Lighting program</th><th>Lighting command ms</th><th>Sync status</th></tr>");
        foreach (var view in run.ViewMetrics)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"<tr><td>{Escape(view.ViewType)}</td><td>{Escape(view.Status)}</td><td>{view.ReceivedFrames}/{view.RequestedFrames}</td><td>{view.DroppedFrameCount}</td><td>{view.TriggerFailureCount}</td><td>{view.TimeoutCount}</td><td>{view.ConnectMs:F1}</td><td>{view.FirstFrameMs:F1}</td><td>{view.AverageFrameIntervalMs:F1}</td><td>{Escape(view.TriggerMode)}</td><td>{view.TriggerToFrameLatencyMs:F1}</td><td>{Escape(view.LightingProgramSelected)}</td><td>{view.LightingCommandLatencyMs:F1}</td><td>{Escape(view.SyncStatus)}</td></tr>");
        }

        sb.AppendLine("</table></div>");
        sb.AppendLine("<p>This report does not claim real camera support when generated from fake, null, or folder simulation adapters.</p>");
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    private static CameraAcceptanceViewMetrics TestView(
        ICameraSource source,
        CameraViewType view,
        CameraAcceptanceCriteria criteria,
        CameraSourceSettings settings,
        CameraAcceptanceRun run,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var metrics = new CameraAcceptanceViewMetrics
        {
            ViewType = view.ToString(),
            RequestedFrames = criteria.FramesPerView,
            TriggerMode = settings.AcquisitionMode.ToString(),
            LightingProgramSelected = "Not controlled by camera acceptance",
            SyncStatus = settings.AcquisitionMode == CameraAcquisitionMode.Continuous ? "FRAME TIMING ONLY" : "TRIGGER TIMING RECORDED",
        };
        source.SelectedView = view;

        var connectWatch = Stopwatch.StartNew();
        try
        {
            source.StartAcquisition();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            metrics.Failures.Add($"{view}: camera acquisition start failed: {ex.Message}");
        }

        connectWatch.Stop();
        metrics.ConnectMs = connectWatch.Elapsed.TotalMilliseconds;
        if (!source.IsAcquiring)
            metrics.Failures.Add($"{view}: camera source did not enter acquisition state ({source.ConnectionStatus}: {source.StatusMessage}).");
        if (metrics.ConnectMs > criteria.MaxConnectMs)
            metrics.Failures.Add($"{view}: connect time {metrics.ConnectMs:F1} ms exceeds {criteria.MaxConnectMs:F1} ms.");

        DateTime? previousCaptured = null;
        var missingSourcePathCount = 0;
        var firstFrameWatch = Stopwatch.StartNew();
        for (var i = 0; i < criteria.FramesPerView; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frameWatch = Stopwatch.StartNew();
            CameraFrame? frame = null;
            try
            {
                frame = source.GetNextFrame();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                metrics.Failures.Add($"{view}: frame {i + 1} acquisition failed: {ex.Message}");
            }

            frameWatch.Stop();
            if (frame is null)
            {
                metrics.DroppedFrameCount++;
                if (settings.AcquisitionMode == CameraAcquisitionMode.SoftwareTrigger)
                    metrics.TriggerFailureCount++;
                else
                    metrics.TimeoutCount++;
                continue;
            }

            if (metrics.ReceivedFrames == 0)
                metrics.FirstFrameMs = firstFrameWatch.Elapsed.TotalMilliseconds;
            metrics.ReceivedFrames++;
            var intervalMs = previousCaptured is null
                ? 0
                : Math.Max(0, (frame.EffectiveCapturedAtUtc - previousCaptured.Value).TotalMilliseconds);
            previousCaptured = frame.EffectiveCapturedAtUtc;
            if (string.IsNullOrWhiteSpace(frame.SourcePath) || !File.Exists(frame.SourcePath))
                missingSourcePathCount++;
            var metadataMessage = ValidateMetadata(frame, view, criteria);
            var record = new CameraAcceptanceFrameRecord
            {
                ViewType = view.ToString(),
                Sequence = i + 1,
                FrameId = frame.FrameId,
                CameraId = frame.CameraId,
                CapturedAtUtc = frame.EffectiveCapturedAtUtc,
                Width = frame.Width,
                Height = frame.Height,
                PixelFormat = frame.PixelFormat,
                SourceKind = frame.SourceKind,
                IsSimulated = frame.IsSimulated || source.ConnectionStatus == CameraSourceStatus.Simulated,
                LatencyMs = frameWatch.Elapsed.TotalMilliseconds,
                IntervalMs = intervalMs,
                MetadataValid = metadataMessage.Length == 0,
                Message = metadataMessage,
            };
            run.Frames.Add(record);
            if (!record.MetadataValid)
                metrics.Failures.Add($"{view} frame {record.Sequence}: {metadataMessage}");
            progress?.Report($"{view}: {metrics.ReceivedFrames}/{criteria.FramesPerView} frames");
        }

        try
        {
            source.StopAcquisition();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            metrics.Warnings.Add($"{view}: camera acquisition stop reported: {ex.Message}");
        }

        // The inspection pipeline consumes frames as image files on disk (MonitorView,
        // benchmark, and every engine reject a frame whose SourcePath does not exist),
        // so a metadata-only adapter passes acceptance yet delivers nothing inspectable.
        if (missingSourcePathCount > 0)
            metrics.Warnings.Add($"{view}: {missingSourcePathCount} frame(s) had no readable on-disk SourcePath. The inspection pipeline consumes frames as image files and will reject such frames; the adapter must persist each frame and set SourcePath.");

        var viewFrames = run.Frames.Where(frame => string.Equals(frame.ViewType, view.ToString(), StringComparison.OrdinalIgnoreCase)).ToArray();
        metrics.AverageFrameIntervalMs = viewFrames.Count(frame => frame.IntervalMs > 0) == 0
            ? 0
            : viewFrames.Where(frame => frame.IntervalMs > 0).Average(frame => frame.IntervalMs);
        metrics.TriggerToFrameLatencyMs = viewFrames.Length == 0 ? 0 : viewFrames.Average(frame => frame.LatencyMs);
        metrics.LightingCommandLatencyMs = 0;
        metrics.DroppedFrameRate = metrics.RequestedFrames == 0 ? 0 : metrics.DroppedFrameCount / (double)metrics.RequestedFrames;
        metrics.TriggerFailureRate = metrics.RequestedFrames == 0 ? 0 : metrics.TriggerFailureCount / (double)metrics.RequestedFrames;

        if (metrics.ReceivedFrames == 0)
            metrics.Failures.Add($"{view}: no frames were received.");
        if (metrics.FirstFrameMs > criteria.MaxFirstFrameMs)
            metrics.Failures.Add($"{view}: first-frame latency {metrics.FirstFrameMs:F1} ms exceeds {criteria.MaxFirstFrameMs:F1} ms.");
        if (metrics.AverageFrameIntervalMs > criteria.MaxAverageFrameIntervalMs)
            metrics.Failures.Add($"{view}: average frame interval {metrics.AverageFrameIntervalMs:F1} ms exceeds {criteria.MaxAverageFrameIntervalMs:F1} ms.");
        if (metrics.DroppedFrameRate > criteria.MaxDroppedFrameRate)
            metrics.Failures.Add($"{view}: dropped-frame rate {metrics.DroppedFrameRate:P1} exceeds {criteria.MaxDroppedFrameRate:P1}.");
        else if (metrics.DroppedFrameCount > 0)
            metrics.Warnings.Add($"{view}: {metrics.DroppedFrameCount} dropped frame(s) observed.");
        if (metrics.TriggerFailureRate > criteria.MaxTriggerFailureRate)
            metrics.Failures.Add($"{view}: trigger failure rate {metrics.TriggerFailureRate:P1} exceeds {criteria.MaxTriggerFailureRate:P1}.");
        else if (metrics.TriggerFailureCount > 0)
            metrics.Warnings.Add($"{view}: {metrics.TriggerFailureCount} trigger failure(s) observed.");

        metrics.Status = metrics.Failures.Count > 0
            ? "FAIL"
            : metrics.Warnings.Count > 0
                ? "WARN"
                : "PASS";
        return metrics;
    }

    private static string ValidateMetadata(CameraFrame frame, CameraViewType expectedView, CameraAcceptanceCriteria criteria)
    {
        var issues = new List<string>();
        if (string.IsNullOrWhiteSpace(frame.FrameId))
            issues.Add("frameId is missing");
        if (string.IsNullOrWhiteSpace(frame.CameraId))
            issues.Add("cameraId is missing");
        if (frame.ViewType != expectedView)
            issues.Add($"viewType is {frame.ViewType}, expected {expectedView}");
        if (frame.EffectiveCapturedAtUtc == default)
            issues.Add("timestamp is missing");
        if (frame.Width < criteria.MinimumWidth || frame.Height < criteria.MinimumHeight)
            issues.Add($"dimensions {frame.Width}x{frame.Height} are below {criteria.MinimumWidth}x{criteria.MinimumHeight}");
        if (string.IsNullOrWhiteSpace(frame.PixelFormat))
            issues.Add("pixel format is missing");
        else if (criteria.RequiredPixelFormats.Count > 0 &&
                 !criteria.RequiredPixelFormats.Contains(frame.PixelFormat, StringComparer.OrdinalIgnoreCase))
            issues.Add($"pixel format {frame.PixelFormat} is not in required set");
        if (string.IsNullOrWhiteSpace(frame.SourceKind))
            issues.Add("source kind is missing");
        return string.Join("; ", issues);
    }

    private static void NormalizeCriteria(CameraAcceptanceCriteria criteria)
    {
        criteria.FramesPerView = Math.Max(1, criteria.FramesPerView);
        if (criteria.RequiredViews.Count == 0)
            criteria.RequiredViews.Add("Top");
        criteria.RequiredViews = criteria.RequiredViews
            .Where(view => !string.IsNullOrWhiteSpace(view))
            .Select(view => ParseView(view).ToString())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static CameraViewType ParseView(string view)
        => Enum.TryParse<CameraViewType>(view, ignoreCase: true, out var parsed)
            ? parsed
            : CameraViewType.Top;

    private static string BuildSettingsSummary(CameraSourceSettings settings)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"source={settings.SourceKey}; acquisition={settings.AcquisitionMode}; top={settings.TopDeviceId}; side={settings.SideDeviceId}; bottom={settings.BottomDeviceId}; exposureMs={settings.ExposureMs:F2}; gain={settings.Gain:F2}; frameTimeoutMs={settings.FrameTimeoutMs}; triggerTimeoutMs={settings.TriggerTimeoutMs}");

    private static void AppendMessages(StringBuilder sb, string title, IReadOnlyList<string> messages, string cssClass)
    {
        if (messages.Count == 0)
            return;

        sb.AppendLine($"<div class=\"card {cssClass}\"><h2>{Escape(title)}</h2><ul>");
        foreach (var message in messages)
            sb.AppendLine($"<li>{Escape(message)}</li>");
        sb.AppendLine("</ul></div>");
    }

    private static string Escape(string value)
        => System.Net.WebUtility.HtmlEncode(value ?? string.Empty);
}

public sealed record CameraAcceptanceReportExport(string JsonPath, string HtmlPath);
