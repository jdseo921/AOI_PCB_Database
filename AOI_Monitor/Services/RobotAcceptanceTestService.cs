using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using AOI_Monitor.Data;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public static class RobotAcceptanceTestService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    // This service generates SIMULATED robot-acceptance evidence and explicitly labels every run
    // "Simulation evidence only". It therefore opts into the safety bypass, which — per the safety
    // boundary in the AOI Software Architecture, Secure Development, and Change-Control Standard
    // (Docs/standard, §34) — is off by default. The bypass activates ONLY when no PLC is configured
    // and no robot reports Ready (pure simulation); with real hardware present it is inert, so this
    // opt-in cannot grant motion on a production cell. Every bypass is audited as ROBOT_SAFETY_BYPASS.
    private static RobotCycleOptions SimulationCycleOptions()
        => new() { PermitSafetyBypassForSimulation = true };

    public static async Task<RobotAcceptanceRun> RunAsync(
        RobotAcceptanceCriteria? criteria = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        criteria ??= new RobotAcceptanceCriteria();
        var robot = IntegrationBoundaryRegistry.RobotController;
        var emergencyStop = IntegrationBoundaryRegistry.EmergencyStopMonitor;
        var safety = IntegrationBoundaryRegistry.PlcSafetyController;
        var auditEvents = new List<string>();
        var run = new RobotAcceptanceRun
        {
            CreatedAtUtc = DateTime.UtcNow,
            ControllerName = robot.Name,
            EmergencyStopName = emergencyStop.Name,
            SafetyControllerName = safety.Name,
            SafetySourceKind = DetermineSafetySourceKind(safety),
            SourceKind = DetermineSourceKind(robot),
            Criteria = criteria,
        };
        if (run.SourceKind == "Simulated")
            run.Warnings.Add("Simulation evidence only; no production robot movement was validated.");
        if (run.SafetySourceKind == "Simulated")
            run.Warnings.Add("PLC/safety interlock evidence is simulated only and is not safety-certified validation.");

        var service = new RobotCycleService((category, message) => auditEvents.Add($"{category}: {message}"), SimulationCycleOptions());
        var fullCycleWatch = Stopwatch.StartNew();

        progress?.Report("Running robot load step...");
        run.LoadMs = await TimeStepAsync(
            run,
            "Load",
            service.CurrentState.ToString(),
            async () => await service.LoadAsync(LoadCommand(), cancellationToken).ConfigureAwait(false),
            criteria.MaxLoadMs,
            cancellationToken).ConfigureAwait(false);

        progress?.Report("Running robot move-to-inspect step...");
        run.MoveToInspectMs = await TimeStepAsync(
            run,
            "MoveToInspect",
            service.CurrentState.ToString(),
            async () => await service.MoveToInspectAsync(InspectCommand(), cancellationToken).ConfigureAwait(false),
            criteria.MaxMoveToInspectMs,
            cancellationToken).ConfigureAwait(false);

        progress?.Report("Running inspection state step...");
        var inspectionWatch = Stopwatch.StartNew();
        var inspection = await service.RunInspectionStepAsync(
            _ => Task.FromResult<AnalysisResult?>(new AnalysisResult
            {
                BoardId = "ROBOT-ACCEPTANCE",
                Verdict = "OK",
                SourceKind = run.SourceKind,
                IsSimulatedSource = run.SourceKind == "Simulated",
            }),
            cancellationToken).ConfigureAwait(false);
        inspectionWatch.Stop();
        run.InspectionMs = inspectionWatch.Elapsed.TotalMilliseconds;
        AddStep(
            run,
            "Inspection",
            RobotCycleState.ReadyToInspect.ToString(),
            service.CurrentState.ToString(),
            run.InspectionMs,
            inspection is not null,
            inspection is not null ? "Inspection action returned analysis evidence." : service.LastError,
            criteria.MaxInspectionMs);

        progress?.Report("Running robot unload step...");
        run.UnloadMs = await TimeStepAsync(
            run,
            "Unload",
            service.CurrentState.ToString(),
            async () => await service.UnloadAsync(UnloadCommand(), cancellationToken).ConfigureAwait(false),
            criteria.MaxUnloadMs,
            cancellationToken).ConfigureAwait(false);

        fullCycleWatch.Stop();
        run.FullCycleMs = fullCycleWatch.Elapsed.TotalMilliseconds;
        run.FinalState = service.CurrentState.ToString();

        if (service.CurrentState != RobotCycleState.Completed)
            run.Failures.Add($"Robot cycle ended in {service.CurrentState}, expected Completed.");
        if (run.FullCycleMs > criteria.MaxFullCycleMs)
            run.Failures.Add($"Full cycle time {run.FullCycleMs:F1} ms exceeds {criteria.MaxFullCycleMs:F1} ms.");

        if (criteria.RequireInvalidTransitionTest)
            await RunInvalidTransitionCheckAsync(run, cancellationToken).ConfigureAwait(false);

        if (criteria.RequireEmergencyStopTest)
            await RunEmergencyStopCheckAsync(run, cancellationToken).ConfigureAwait(false);

        var resetService = new RobotCycleService((category, message) => auditEvents.Add($"{category}: {message}"), SimulationCycleOptions());
        var reset = await resetService.ResetAsync(cancellationToken).ConfigureAwait(false);
        run.ResetReturnedIdle = reset.Accepted && resetService.CurrentState == RobotCycleState.Idle;
        AddStep(
            run,
            "Reset",
            RobotCycleState.Completed.ToString(),
            resetService.CurrentState.ToString(),
            0,
            run.ResetReturnedIdle,
            reset.Message,
            maxElapsedMs: null);
        if (!run.ResetReturnedIdle)
            run.Failures.Add("Robot reset did not return the cycle service to Idle.");

        run.AuditEventCount = auditEvents.Count;
        if (criteria.RequireAuditEvents && run.AuditEventCount == 0)
            run.Failures.Add("Robot acceptance did not record audit events.");

        run.Status = run.Failures.Count > 0
            ? "FAIL"
            : "PASS";
        return run;
    }

    public static async Task<long> RunAndPersistAsync(
        RobotAcceptanceCriteria? criteria = null,
        string? operatorId = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var run = await RunAsync(criteria, progress, cancellationToken).ConfigureAwait(false);
        return AoiDatabase.RecordRobotAcceptanceRun(run, operatorId);
    }

    public static RobotAcceptanceSummary ToSummary(RobotAcceptanceRun? run)
    {
        if (run is null)
        {
            return new RobotAcceptanceSummary
            {
                Status = "NOT VALIDATED",
                SourceKind = "NotValidated",
                Messages = { "No robot cell acceptance run has been recorded." },
            };
        }

        var summary = new RobotAcceptanceSummary
        {
            Status = run.Status,
            SourceKind = run.SourceKind,
            CreatedAtUtc = run.CreatedAtUtc,
            ControllerName = run.ControllerName,
            SafetyControllerName = run.SafetyControllerName,
            SafetySourceKind = run.SafetySourceKind,
            FullCycleMs = run.FullCycleMs,
            EmergencyStopBlocked = run.EmergencyStopBlocked,
            SafetyFaultBlocked = run.SafetyFaultBlocked,
            InvalidTransitionRejected = run.InvalidTransitionRejected,
            ResetReturnedIdle = run.ResetReturnedIdle,
        };
        summary.Messages.AddRange(run.Failures);
        summary.Messages.AddRange(run.Warnings);
        if (summary.Messages.Count == 0)
            summary.Messages.Add("Robot acceptance criteria passed.");
        return summary;
    }

    public static RobotAcceptanceReportExport ExportReport(RobotAcceptanceRun run, string? outputRoot = null)
    {
        var root = string.IsNullOrWhiteSpace(outputRoot)
            ? Path.Combine(AoiDatabase.StorageRoot, "exports", "robot_acceptance")
            : outputRoot;
        Directory.CreateDirectory(root);

        var stamp = run.CreatedAtUtc.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var jsonPath = Path.Combine(root, $"robot_acceptance_{stamp}.json");
        var htmlPath = Path.Combine(root, $"robot_acceptance_{stamp}.html");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(run, JsonOptions), Encoding.UTF8);
        File.WriteAllText(htmlPath, BuildHtml(run), Encoding.UTF8);
        return new RobotAcceptanceReportExport(jsonPath, htmlPath);
    }

    public static string BuildHtml(RobotAcceptanceRun run)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html><html><head><meta charset=\"utf-8\"><title>Robot Cell Acceptance</title>");
        sb.AppendLine("<style>body{font-family:Segoe UI,Arial,sans-serif;background:#f7f9fb;color:#17212b;margin:24px}.card{background:white;border:1px solid #d8e0e7;padding:16px;margin:0 0 16px}table{border-collapse:collapse;width:100%}td,th{border:1px solid #d8e0e7;padding:6px;text-align:left}.warn{color:#8a5a00}.fail{color:#a12626}.ok{color:#176b3a}</style></head><body>");
        sb.AppendLine("<h1>Robot Cell Acceptance Report</h1>");
        sb.AppendLine($"<div class=\"card\"><strong>Status:</strong> {Escape(run.Status)}<br><strong>Source:</strong> {Escape(run.SourceKind)}<br><strong>Controller:</strong> {Escape(run.ControllerName)}<br><strong>Safety:</strong> {Escape(run.SafetyControllerName)} ({Escape(run.SafetySourceKind)})<br><strong>Full cycle:</strong> {run.FullCycleMs:F1} ms</div>");
        if (run.SourceKind == "Simulated")
            sb.AppendLine("<div class=\"card warn\"><strong>Simulation evidence only; no production robot movement was validated.</strong></div>");
        AppendMessages(sb, "Failures", run.Failures, "fail");
        AppendMessages(sb, "Warnings / Limitations", run.Warnings, "warn");
        sb.AppendLine("<div class=\"card\"><h2>Cycle Timing</h2><table><tr><th>Step</th><th>From</th><th>To</th><th>Status</th><th>Accepted</th><th>Elapsed ms</th><th>Message</th></tr>");
        foreach (var step in run.Steps)
            sb.AppendLine(CultureInfo.InvariantCulture, $"<tr><td>{Escape(step.StepName)}</td><td>{Escape(step.FromState)}</td><td>{Escape(step.ToState)}</td><td>{Escape(step.Status)}</td><td>{step.Accepted}</td><td>{step.ElapsedMs:F1}</td><td>{Escape(step.Message)}</td></tr>");
        sb.AppendLine("</table></div>");
        sb.AppendLine("<p>This report does not imply safety-certified emergency-stop validation or vendor robot SDK support.</p>");
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    private static async Task<double> TimeStepAsync(
        RobotAcceptanceRun run,
        string stepName,
        string fromState,
        Func<Task<IntegrationCommandResult>> action,
        double maxElapsedMs,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var watch = Stopwatch.StartNew();
        var result = await action().ConfigureAwait(false);
        watch.Stop();
        AddStep(run, stepName, fromState, string.Empty, watch.Elapsed.TotalMilliseconds, result.Accepted, result.Message, maxElapsedMs);
        return watch.Elapsed.TotalMilliseconds;
    }

    private static async Task RunInvalidTransitionCheckAsync(RobotAcceptanceRun run, CancellationToken cancellationToken)
    {
        var service = new RobotCycleService((_, _) => { }, SimulationCycleOptions());
        var watch = Stopwatch.StartNew();
        var result = await service.UnloadAsync(UnloadCommand(), cancellationToken).ConfigureAwait(false);
        watch.Stop();
        run.InvalidTransitionRejected = !result.Accepted && service.CurrentState == RobotCycleState.Idle;
        AddStep(run, "InvalidTransition", RobotCycleState.Idle.ToString(), service.CurrentState.ToString(), watch.Elapsed.TotalMilliseconds, run.InvalidTransitionRejected, result.Message, null);
        if (!run.InvalidTransitionRejected)
            run.Failures.Add("Invalid unload-before-load transition was not rejected.");
    }

    private static async Task RunEmergencyStopCheckAsync(RobotAcceptanceRun run, CancellationToken cancellationToken)
    {
        if (IntegrationBoundaryRegistry.PlcSafetyController is SimulatedPlcSafetyController simulatedPlc)
        {
            simulatedPlc.SetEmergencyStopActive(true);
            var plcService = new RobotCycleService((_, _) => { }, SimulationCycleOptions());
            var plcWatch = Stopwatch.StartNew();
            var plcResult = await plcService.LoadAsync(LoadCommand("E-STOP-TEST"), cancellationToken).ConfigureAwait(false);
            plcWatch.Stop();
            run.EmergencyStopBlocked = !plcResult.Accepted && plcService.CurrentState == RobotCycleState.EmergencyStopped;
            AddStep(run, "EmergencyStopBlock", RobotCycleState.Idle.ToString(), plcService.CurrentState.ToString(), plcWatch.Elapsed.TotalMilliseconds, run.EmergencyStopBlocked, plcResult.Message, null);
            if (!run.EmergencyStopBlocked)
                run.Failures.Add("PLC emergency stop did not block the robot cycle.");
            await simulatedPlc.ResetSafetyFaultAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (IntegrationBoundaryRegistry.RobotController is not SimulatedRobotController simulated)
        {
            run.Warnings.Add("Emergency stop acceptance check could not actuate the active controller. No safety-certified e-stop validation is claimed.");
            return;
        }

        simulated.TriggerEmergencyStop();
        var service = new RobotCycleService((_, _) => { }, SimulationCycleOptions());
        var watch = Stopwatch.StartNew();
        var result = await service.LoadAsync(LoadCommand("E-STOP-TEST"), cancellationToken).ConfigureAwait(false);
        watch.Stop();
        run.EmergencyStopBlocked = !result.Accepted && service.CurrentState == RobotCycleState.EmergencyStopped;
        AddStep(run, "EmergencyStopBlock", RobotCycleState.Idle.ToString(), service.CurrentState.ToString(), watch.Elapsed.TotalMilliseconds, run.EmergencyStopBlocked, result.Message, null);
        if (!run.EmergencyStopBlocked)
            run.Failures.Add("Emergency stop did not block the robot cycle.");

        await service.ResetAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddStep(
        RobotAcceptanceRun run,
        string stepName,
        string fromState,
        string toState,
        double elapsedMs,
        bool accepted,
        string message,
        double? maxElapsedMs)
    {
        var status = accepted ? "PASS" : "FAIL";
        if (accepted && maxElapsedMs is { } max && elapsedMs > max)
        {
            status = "FAIL";
            run.Failures.Add($"{stepName} time {elapsedMs:F1} ms exceeds {max:F1} ms.");
        }
        else if (!accepted && stepName is not "InvalidTransition" and not "EmergencyStopBlock")
        {
            run.Failures.Add($"{stepName} failed: {message}");
        }

        run.Steps.Add(new RobotAcceptanceStep
        {
            StepName = stepName,
            FromState = fromState,
            ToState = toState,
            ElapsedMs = elapsedMs,
            Accepted = accepted,
            Status = status,
            Message = message,
        });
    }

    private static string DetermineSourceKind(IRobotController controller)
        => controller.Status == IntegrationConnectionStatus.Simulated ||
           controller.Name.Contains("simulated", StringComparison.OrdinalIgnoreCase)
            ? "Simulated"
            : controller.Status == IntegrationConnectionStatus.Ready ? "Real" : "NotConnected";

    private static string DetermineSafetySourceKind(IPlcSafetyController controller)
        => controller.Status == IntegrationConnectionStatus.Simulated ||
           controller.Name.Contains("simulated", StringComparison.OrdinalIgnoreCase)
            ? "Simulated"
            : controller.Status == IntegrationConnectionStatus.Ready ? "Real" : "NotConnected";

    private static LoadCommand LoadCommand(string boardId = "ROBOT-ACCEPTANCE")
        => new(boardId, "TBOX", "LOT-ROBOT", "AOI-LIB-01");

    private static InspectCommand InspectCommand()
        => new("ROBOT-ACCEPTANCE", "TBOX", "LOT-ROBOT", "AOI-LIB-01", "Top");

    private static UnloadCommand UnloadCommand()
        => new("ROBOT-ACCEPTANCE", "TBOX", "LOT-ROBOT", "AOI-LIB-01", "Review");

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

public sealed record RobotAcceptanceReportExport(string JsonPath, string HtmlPath);
