using AOI_Monitor.Services;

namespace RobotPlcAdapterTemplate;

public sealed class FakeRobotController : IRobotController
{
    public string Name => "Template Fake Robot Controller";
    public IntegrationConnectionStatus Status => IntegrationConnectionStatus.Simulated;
    public string StatusMessage => "Template fake robot controller. No real robot, motion controller, or fieldbus is connected.";

    public Task<IntegrationCommandResult> LoadAsync(LoadCommand command, CancellationToken cancellationToken = default)
        => SimulatedAsync($"Fake load accepted for board {command.BoardId}.", cancellationToken);

    public Task<IntegrationCommandResult> InspectAsync(InspectCommand command, CancellationToken cancellationToken = default)
        => SimulatedAsync($"Fake inspect accepted for board {command.BoardId}, view {command.ViewType}.", cancellationToken);

    public Task<IntegrationCommandResult> UnloadAsync(UnloadCommand command, CancellationToken cancellationToken = default)
        => SimulatedAsync($"Fake unload accepted for board {command.BoardId} to {command.Destination}.", cancellationToken);

    public Task<IntegrationCommandResult> ResetAsync(CancellationToken cancellationToken = default)
        => SimulatedAsync("Fake robot reset accepted.", cancellationToken);

    private static Task<IntegrationCommandResult> SimulatedAsync(string message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Vendor robot SDK calls belong here:
        // - dispatch load/inspect/unload commands through the robot controller SDK,
        // - poll motion completion and alarm state,
        // - surface timeout, servo, grip, clamp, and path errors,
        // - never bypass PLC safety status before motion.
        return Task.FromResult(new IntegrationCommandResult(
            true,
            IntegrationConnectionStatus.Simulated,
            $"{message} No real motion was commanded."));
    }
}

public sealed class FakePlcSafetyController : IPlcSafetyController
{
    public string Name => "Template Fake PLC Safety Controller";
    public IntegrationConnectionStatus Status => IntegrationConnectionStatus.Simulated;
    public string StatusMessage => "Template fake PLC safety controller. Simulated safety statuses only; not safety-rated evidence.";
    public bool IsGuardDoorClosed => true;
    public bool IsEmergencyStopActive => false;
    public bool IsAirPressureOk => true;
    public bool IsRobotServoReady => true;
    public bool IsBoardClampReady => true;
    public bool IsLightCurtainClear => true;

    public Task<IntegrationCommandResult> ResetSafetyFaultAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Vendor PLC/safety calls belong here:
        // read safety relays, e-stop channels, light curtain, guard door,
        // air pressure, clamp ready, servo enable, and reset acknowledgement.
        return Task.FromResult(new IntegrationCommandResult(
            true,
            IntegrationConnectionStatus.Simulated,
            "Fake PLC safety reset accepted. No real safety circuit was reset."));
    }

    public SafetyStatus GetSafetyStatus()
        => new()
        {
            Message = "Fake PLC safety interlocks OK. Simulation-only evidence; do not use for real hardware readiness.",
        };

    public SafetyStatus GetDiagnostics() => GetSafetyStatus();
}

public static class RobotPlcTemplateRegistration
{
    public static void RegisterFakeControllers()
    {
        // Reviewed commissioning/bootstrap code may assign real implementations here.
        // The main app intentionally does not auto-load robot/PLC motion plugins from
        // a folder, because robot and safety integration requires site validation.
        IntegrationBoundaryRegistry.RobotController = new FakeRobotController();
        IntegrationBoundaryRegistry.PlcSafetyController = new FakePlcSafetyController();
    }
}
