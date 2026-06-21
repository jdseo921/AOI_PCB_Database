using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using AOI_Monitor.Data;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public static class RobotCellAcceptanceTestService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static async Task<RobotAcceptanceRun> RunAsync(
        RobotAcceptanceCriteria? criteria = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var run = await RobotAcceptanceTestService.RunAsync(criteria, progress, cancellationToken).ConfigureAwait(false);
        await RunSafetyFaultChecksAsync(run, cancellationToken).ConfigureAwait(false);

        run.Status = run.Failures.Count > 0 ? "FAIL" : "PASS";
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

    public static RobotAcceptanceReportExport ExportReport(RobotAcceptanceRun run, string? outputRoot = null)
    {
        var root = string.IsNullOrWhiteSpace(outputRoot)
            ? Path.Combine(AoiDatabase.StorageRoot, "exports", "robot_cell_acceptance")
            : outputRoot;
        Directory.CreateDirectory(root);

        var stamp = run.CreatedAtUtc.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var jsonPath = Path.Combine(root, $"robot_cell_acceptance_report_{stamp}.json");
        var htmlPath = Path.Combine(root, $"robot_cell_acceptance_report_{stamp}.html");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(run, JsonOptions), Encoding.UTF8);
        File.WriteAllText(htmlPath, BuildHtml(run), Encoding.UTF8);
        return new RobotAcceptanceReportExport(jsonPath, htmlPath);
    }

    public static string BuildHtml(RobotAcceptanceRun run)
    {
        var sb = new StringBuilder(RobotAcceptanceTestService.BuildHtml(run));
        sb.AppendLine("<!-- Robot cell acceptance includes PLC/safety interlock simulation checks. This is not safety certification. -->");
        return sb.ToString();
    }

    private static async Task RunSafetyFaultChecksAsync(RobotAcceptanceRun run, CancellationToken cancellationToken)
    {
        if (IntegrationBoundaryRegistry.PlcSafetyController is not SimulatedPlcSafetyController simulated)
        {
            run.Warnings.Add("Guard-door-open and clamp-not-ready checks require a simulated PLC safety controller or a real PLC test adapter. No safety certification is claimed.");
            return;
        }

        var guardBlocked = await CheckSafetyFaultAsync(
            run,
            "GuardDoorOpenBlock",
            () => simulated.SetGuardDoorClosed(false),
            cancellationToken).ConfigureAwait(false);
        await simulated.ResetSafetyFaultAsync(cancellationToken).ConfigureAwait(false);

        var clampBlocked = await CheckSafetyFaultAsync(
            run,
            "ClampNotReadyBlock",
            () => simulated.SetBoardClampReady(false),
            cancellationToken).ConfigureAwait(false);
        await simulated.ResetSafetyFaultAsync(cancellationToken).ConfigureAwait(false);

        var lightCurtainBlocked = await CheckSafetyFaultAsync(
            run,
            "LightCurtainBlocked",
            () => simulated.SetLightCurtainClear(false),
            cancellationToken).ConfigureAwait(false);
        await simulated.ResetSafetyFaultAsync(cancellationToken).ConfigureAwait(false);

        run.SafetyFaultBlocked = guardBlocked && clampBlocked && lightCurtainBlocked;
        if (!run.SafetyFaultBlocked)
            run.Failures.Add("Robot cell acceptance did not prove simulated guard, clamp, and light-curtain safety faults blocked movement.");
    }

    private static async Task<bool> CheckSafetyFaultAsync(
        RobotAcceptanceRun run,
        string stepName,
        Action injectFault,
        CancellationToken cancellationToken)
    {
        injectFault();
        var service = new RobotCycleService((_, _) => { }, new RobotCycleOptions { PermitSafetyBypassForSimulation = false });
        var watch = Stopwatch.StartNew();
        var result = await service.LoadAsync(new LoadCommand(stepName, "TBOX", "LOT-ROBOT", "AOI-LIB-01"), cancellationToken).ConfigureAwait(false);
        watch.Stop();
        var blocked = !result.Accepted && service.CurrentState == RobotCycleState.Faulted;
        run.Steps.Add(new RobotAcceptanceStep
        {
            StepName = stepName,
            FromState = RobotCycleState.Idle.ToString(),
            ToState = service.CurrentState.ToString(),
            ElapsedMs = watch.Elapsed.TotalMilliseconds,
            Accepted = blocked,
            Status = blocked ? "PASS" : "FAIL",
            Message = result.Message,
        });
        return blocked;
    }
}
