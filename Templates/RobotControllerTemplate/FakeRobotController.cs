using AOI_Monitor.Services;

namespace RobotControllerTemplate;

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

        // Vendor robot SDK or PLC/fieldbus calls belong here:
        // Epson/Fanuc/Yamaha/UR/Denso command dispatch, motion state polling,
        // timeout handling, and explicit safety interlock checks.
        return Task.FromResult(new IntegrationCommandResult(true, IntegrationConnectionStatus.Simulated, $"{message} No real motion was commanded."));
    }
}

public sealed class FakePlcSafetyController : IPlcSafetyController
{
    public string Name => "Template Fake PLC Safety Controller";
    public IntegrationConnectionStatus Status => IntegrationConnectionStatus.Simulated;
    public string StatusMessage => "Template fake PLC safety controller. This is not safety-rated evidence.";
    public bool IsGuardDoorClosed => true;
    public bool IsEmergencyStopActive => false;
    public bool IsAirPressureOk => true;
    public bool IsRobotServoReady => true;
    public bool IsBoardClampReady => true;
    public bool IsLightCurtainClear => true;

    public Task<IntegrationCommandResult> ResetSafetyFaultAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new IntegrationCommandResult(true, IntegrationConnectionStatus.Simulated, "Fake PLC safety reset accepted. No real safety circuit was reset."));
    }

    public SafetyStatus GetSafetyStatus()
        => new()
        {
            Message = "Fake safety interlocks OK. Simulation-only evidence.",
        };

    public SafetyStatus GetDiagnostics() => GetSafetyStatus();
}

public static class RobotTemplateRegistration
{
    public static void RegisterFakeControllers()
    {
        // Registration boundary for a host/plugin bootstrap:
        // IntegrationBoundaryRegistry.RobotController = new FakeRobotController();
        // IntegrationBoundaryRegistry.PlcSafetyController = new FakePlcSafetyController();
        //
        // Keep this explicit so real robot/PLC integration cannot be enabled by
        // dropping binaries into a folder without a reviewed commissioning step.
        IntegrationBoundaryRegistry.RobotController = new FakeRobotController();
        IntegrationBoundaryRegistry.PlcSafetyController = new FakePlcSafetyController();
    }
}
