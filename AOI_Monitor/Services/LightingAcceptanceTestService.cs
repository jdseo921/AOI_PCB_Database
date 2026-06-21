using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using AOI_Monitor.Data;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public static class LightingAcceptanceTestService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static async Task<LightingAcceptanceRun> RunAsync(
        LightingSettings settings,
        LightingAcceptanceCriteria? criteria = null,
        ILightingController? controller = null,
        ICameraSource? cameraSource = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        criteria ??= new LightingAcceptanceCriteria();
        NormalizeCriteria(criteria);
        controller ??= LightingControllerFactory.Create(settings);

        var run = new LightingAcceptanceRun
        {
            CreatedAtUtc = DateTime.UtcNow,
            ControllerName = controller.Name,
            Mode = LightingSettingsService.NormalizeMode(settings.Mode),
            SettingsSummary = BuildSettingsSummary(settings),
            Criteria = criteria,
            IsSimulated = controller.Status == IntegrationConnectionStatus.Simulated ||
                string.Equals(LightingSettingsService.NormalizeMode(settings.Mode), LightingModes.Simulated, StringComparison.OrdinalIgnoreCase),
        };

        foreach (var view in criteria.RequiredViews)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Testing lighting sync for {view}...");
            var step = await RunStepAsync(settings, criteria, controller, cameraSource, view, cancellationToken).ConfigureAwait(false);
            run.Steps.Add(step);
        }

        run.StepCount = run.Steps.Count;
        run.PassedStepCount = run.Steps.Count(step => step.Status == "PASS");
        run.FailedStepCount = run.Steps.Count(step => step.Status == "FAIL");
        run.MaxCommandLatencyMs = run.Steps.Count == 0 ? 0 : run.Steps.Max(step => step.CommandLatencyMs);
        run.MaxTriggerToFrameLatencyMs = run.Steps.Count == 0 ? 0 : run.Steps.Max(step => step.TriggerToFrameLatencyMs);
        run.Failures = run.Steps
            .Where(step => step.Status == "FAIL")
            .Select(step => $"{step.ViewType}: {step.Message}")
            .ToList();

        if (run.IsSimulated)
            run.Warnings.Add("Lighting acceptance was run in simulated mode. This validates workflow timing only and does not prove real lighting-controller readiness.");
        if (cameraSource is null)
            run.Warnings.Add("No camera source was supplied; trigger-to-frame timing used fake acquisition evidence.");

        run.Status = run.Failures.Count > 0
            ? "FAIL"
            : run.Steps.Any(step => step.Status == "WARN")
                ? "WARN"
                : "PASS";
        return run;
    }

    public static async Task<long> RunAndPersistAsync(
        LightingSettings settings,
        LightingAcceptanceCriteria? criteria = null,
        ILightingController? controller = null,
        ICameraSource? cameraSource = null,
        string? operatorId = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var run = await RunAsync(settings, criteria, controller, cameraSource, progress, cancellationToken).ConfigureAwait(false);
        return AoiDatabase.RecordLightingAcceptanceRun(run, operatorId);
    }

    public static LightingAcceptanceReportExport ExportReport(LightingAcceptanceRun run, string? outputRoot = null)
    {
        var root = string.IsNullOrWhiteSpace(outputRoot)
            ? Path.Combine(AoiDatabase.StorageRoot, "exports", "lighting_acceptance")
            : outputRoot;
        Directory.CreateDirectory(root);

        var stamp = run.CreatedAtUtc.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var jsonPath = Path.Combine(root, $"lighting_acceptance_{stamp}.json");
        var htmlPath = Path.Combine(root, $"lighting_acceptance_{stamp}.html");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(run, JsonOptions), Encoding.UTF8);
        File.WriteAllText(htmlPath, BuildHtml(run), Encoding.UTF8);
        return new LightingAcceptanceReportExport(jsonPath, htmlPath);
    }

    public static string BuildHtml(LightingAcceptanceRun run)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html><html><head><meta charset=\"utf-8\"><title>Lighting Acceptance</title>");
        sb.AppendLine("<style>body{font-family:Segoe UI,Arial,sans-serif;background:#f7f9fb;color:#17212b;margin:24px}.card{background:white;border:1px solid #d8e0e7;padding:16px;margin:0 0 16px}table{border-collapse:collapse;width:100%}td,th{border:1px solid #d8e0e7;padding:6px;text-align:left}.warn{color:#8a5a00}.fail{color:#a12626}.ok{color:#176b3a}</style></head><body>");
        sb.AppendLine("<h1>Stage 2 Lighting / Trigger Synchronization Acceptance</h1>");
        sb.AppendLine($"<div class=\"card\"><strong>Status:</strong> {Escape(run.Status)}<br><strong>Controller:</strong> {Escape(run.ControllerName)}<br><strong>Mode:</strong> {Escape(run.Mode)}<br><strong>Simulated:</strong> {run.IsSimulated}</div>");
        if (run.Warnings.Count > 0)
        {
            sb.AppendLine("<div class=\"card warn\"><h2>Warnings / Limitations</h2><ul>");
            foreach (var warning in run.Warnings)
                sb.AppendLine($"<li>{Escape(warning)}</li>");
            sb.AppendLine("</ul></div>");
        }

        if (run.Failures.Count > 0)
        {
            sb.AppendLine("<div class=\"card fail\"><h2>Failures</h2><ul>");
            foreach (var failure in run.Failures)
                sb.AppendLine($"<li>{Escape(failure)}</li>");
            sb.AppendLine("</ul></div>");
        }

        sb.AppendLine("<div class=\"card\"><h2>Per-View Timing</h2><table><tr><th>View</th><th>Program</th><th>Status</th><th>Command ms</th><th>Trigger-to-frame ms</th><th>Frame</th><th>Message</th></tr>");
        foreach (var step in run.Steps)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"<tr><td>{Escape(step.ViewType)}</td><td>{Escape(step.ProgramName)}</td><td>{Escape(step.Status)}</td><td>{step.CommandLatencyMs:F1}</td><td>{step.TriggerToFrameLatencyMs:F1}</td><td>{Escape(step.FrameId)}</td><td>{Escape(step.Message)}</td></tr>");
        }

        sb.AppendLine("</table></div>");
        sb.AppendLine("<p>No real TCP or serial lighting command is sent unless that mode is explicitly configured.</p>");
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    private static async Task<LightingAcceptanceStep> RunStepAsync(
        LightingSettings settings,
        LightingAcceptanceCriteria criteria,
        ILightingController controller,
        ICameraSource? cameraSource,
        string view,
        CancellationToken cancellationToken)
    {
        var program = LightingCommandFormatter.ProgramForView(settings, view);
        var step = new LightingAcceptanceStep
        {
            ViewType = view,
            ProgramName = program,
            CommandText = LightingCommandFormatter.BuildCommand(settings.CommandTemplate, view, program),
        };

        var commandWatch = Stopwatch.StartNew();
        var result = await LightingSynchronizationService.SynchronizeAsync(controller, settings, view, cancellationToken).ConfigureAwait(false);
        commandWatch.Stop();
        step.CommandLatencyMs = commandWatch.Elapsed.TotalMilliseconds;
        step.CommandAccepted = result.Accepted;

        if (!result.Accepted || result.Status is IntegrationConnectionStatus.NotConnected or IntegrationConnectionStatus.Error)
        {
            step.Status = "FAIL";
            step.Message = result.Message;
            return step;
        }

        if (step.CommandLatencyMs > criteria.MaxCommandLatencyMs)
        {
            step.Status = "FAIL";
            step.Message = $"Lighting command latency {step.CommandLatencyMs:F1} ms exceeds {criteria.MaxCommandLatencyMs:F1} ms.";
            return step;
        }

        if (cameraSource is null)
        {
            step.FrameReceived = false;
            step.TriggerToFrameLatencyMs = 0;
            step.Status = "PASS";
            step.Message = $"{result.Message} Fake acquisition used; no camera source supplied.";
            return step;
        }

        cameraSource.SelectedView = ParseView(view);
        var frameWatch = Stopwatch.StartNew();
        try
        {
            cameraSource.StartAcquisition();
            var frame = cameraSource.GetNextFrame();
            cameraSource.StopAcquisition();
            frameWatch.Stop();
            step.TriggerToFrameLatencyMs = frameWatch.Elapsed.TotalMilliseconds;
            if (frame is null)
            {
                step.Status = criteria.RequireFrameWhenCameraSourceProvided ? "FAIL" : "WARN";
                step.Message = $"{result.Message} Camera source returned no frame.";
                return step;
            }

            step.FrameReceived = true;
            step.FrameId = frame.FrameId;
            step.CameraId = frame.CameraId;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            frameWatch.Stop();
            step.TriggerToFrameLatencyMs = frameWatch.Elapsed.TotalMilliseconds;
            step.Status = "FAIL";
            step.Message = $"{result.Message} Camera acquisition failed: {ex.Message}";
            return step;
        }

        if (step.TriggerToFrameLatencyMs > criteria.MaxTriggerToFrameLatencyMs)
        {
            step.Status = "FAIL";
            step.Message = $"Trigger-to-frame latency {step.TriggerToFrameLatencyMs:F1} ms exceeds {criteria.MaxTriggerToFrameLatencyMs:F1} ms.";
            return step;
        }

        step.Status = "PASS";
        step.Message = result.Message;
        return step;
    }

    private static void NormalizeCriteria(LightingAcceptanceCriteria criteria)
    {
        if (criteria.RequiredViews.Count == 0)
            criteria.RequiredViews.Add("Top");
        criteria.RequiredViews = criteria.RequiredViews
            .Where(view => !string.IsNullOrWhiteSpace(view))
            .Select(view => ParseView(view).ToString())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        criteria.MaxCommandLatencyMs = Math.Max(1, criteria.MaxCommandLatencyMs);
        criteria.MaxTriggerToFrameLatencyMs = Math.Max(1, criteria.MaxTriggerToFrameLatencyMs);
    }

    private static CameraViewType ParseView(string view)
        => Enum.TryParse<CameraViewType>(view, ignoreCase: true, out var parsed)
            ? parsed
            : CameraViewType.Top;

    private static string BuildSettingsSummary(LightingSettings settings)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"mode={settings.Mode}; tcp={settings.TcpHost}:{settings.TcpPort}; serial={settings.SerialPortName}@{settings.BaudRate}; top={settings.TopProgram}; side={settings.SideProgram}; bottom={settings.BottomProgram}; timeoutMs={settings.ResponseTimeoutMs}");

    private static string Escape(string value)
        => System.Net.WebUtility.HtmlEncode(value ?? string.Empty);
}

public sealed record LightingAcceptanceReportExport(string JsonPath, string HtmlPath);
