using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Xunit;

namespace AOI_Monitor.Tests;

public sealed class IntegrationContractsTests
{
    [Fact]
    public async Task NullIntegrationBoundariesReportNotConnectedAndRejectCommands()
    {
        var lighting = new NullLightingController();
        var robot = new NullRobotController();
        var mes = new NullMesClient();
        var traceability = new NullTraceabilityUploader();
        var emergencyStop = new NullEmergencyStopMonitor();

        Assert.Equal(IntegrationConnectionStatus.NotConnected, lighting.Status);
        Assert.Equal(IntegrationConnectionStatus.NotConnected, robot.Status);
        Assert.Equal(IntegrationConnectionStatus.NotConnected, mes.Status);
        Assert.Equal(IntegrationConnectionStatus.NotConnected, traceability.Status);
        Assert.Equal(IntegrationConnectionStatus.NotConnected, emergencyStop.Status);
        Assert.False(emergencyStop.IsEmergencyStopActive);

        var load = await robot.LoadAsync(new LoadCommand("B1", "TBOX", "LOT-1", "AOI-LIB-01"));
        var inspect = await robot.InspectAsync(new InspectCommand("B1", "TBOX", "LOT-1", "AOI-LIB-01", "Top"));
        var unload = await robot.UnloadAsync(new UnloadCommand("B1", "TBOX", "LOT-1", "AOI-LIB-01", "Review"));
        var reset = await robot.ResetAsync();
        var light = await lighting.SetProgramAsync("Top", "POC");
        var uploadResult = await mes.UploadResultAsync(new UploadResultCommand("I1", "B1", "TBOX", "LOT-1", "OK", "Operator01 [Operator]", DateTime.UtcNow));
        var uploadImage = await traceability.UploadImageAsync(new UploadImageCommand("I1", "B1", @"C:\temp\image.png", "sample", DateTime.UtcNow));

        Assert.All(new[] { load, inspect, unload, reset, light, uploadResult, uploadImage }, result =>
        {
            Assert.False(result.Accepted);
            Assert.Equal(IntegrationConnectionStatus.NotConnected, result.Status);
            Assert.NotEmpty(result.Message);
        });
    }

    [Fact]
    public async Task SimulatedRobotControllerRunsCycleAndEmergencyStopBlocksCommands()
    {
        var robot = new SimulatedRobotController();
        var emergencyStop = new SimulatedEmergencyStopMonitor(robot);

        Assert.Equal(IntegrationConnectionStatus.Simulated, robot.Status);
        Assert.Equal(IntegrationConnectionStatus.Simulated, emergencyStop.Status);
        Assert.False(emergencyStop.IsEmergencyStopActive);

        var load = await robot.LoadAsync(new LoadCommand("B1", "TBOX", "LOT-1", "AOI-LIB-01"));
        var inspect = await robot.InspectAsync(new InspectCommand("B1", "TBOX", "LOT-1", "AOI-LIB-01", "Top"));
        var unload = await robot.UnloadAsync(new UnloadCommand("B1", "TBOX", "LOT-1", "AOI-LIB-01", "Review"));

        Assert.True(load.Accepted);
        Assert.True(inspect.Accepted);
        Assert.True(unload.Accepted);
        Assert.False(robot.IsBoardLoaded);

        robot.TriggerEmergencyStop();
        Assert.True(emergencyStop.IsEmergencyStopActive);
        Assert.Equal(IntegrationConnectionStatus.Error, robot.Status);

        var blocked = await robot.LoadAsync(new LoadCommand("B2", "TBOX", "LOT-1", "AOI-LIB-01"));
        Assert.False(blocked.Accepted);
        Assert.Equal(IntegrationConnectionStatus.Error, blocked.Status);

        var reset = await robot.ResetAsync();
        Assert.True(reset.Accepted);
        Assert.False(emergencyStop.IsEmergencyStopActive);

        var recovered = await robot.LoadAsync(new LoadCommand("B3", "TBOX", "LOT-1", "AOI-LIB-01"));
        Assert.True(recovered.Accepted);
        Assert.True(robot.IsBoardLoaded);
    }

    [Fact]
    public async Task RobotCycleServiceRunsValidTransitionSequence()
    {
        await WithSimulatedRobotCycleServiceAsync(async service =>
        {
            var load = await service.LoadAsync(new LoadCommand("B1", "TBOX", "LOT-1", "AOI-LIB-01"));
            var move = await service.MoveToInspectAsync(new InspectCommand("B1", "TBOX", "LOT-1", "AOI-LIB-01", "Top"));
            var analysis = await service.RunInspectionStepAsync(_ => Task.FromResult<AnalysisResult?>(new AnalysisResult { Verdict = "OK" }));
            var unload = await service.UnloadAsync(new UnloadCommand("B1", "TBOX", "LOT-1", "AOI-LIB-01", "Review"));

            Assert.True(load.Accepted);
            Assert.True(move.Accepted);
            Assert.NotNull(analysis);
            Assert.True(unload.Accepted);
            Assert.Equal(RobotCycleState.Completed, service.CurrentState);
            Assert.Equal(
                new[]
                {
                    RobotCycleState.Loading,
                    RobotCycleState.Loaded,
                    RobotCycleState.MovingToInspect,
                    RobotCycleState.ReadyToInspect,
                    RobotCycleState.Inspecting,
                    RobotCycleState.Unloading,
                    RobotCycleState.Completed,
                },
                service.Transitions.Select(transition => transition.To).ToArray());
        });
    }

    [Fact]
    public async Task RobotCycleServiceRejectsUnloadBeforeLoadAndKeepsIdleState()
    {
        await WithSimulatedRobotCycleServiceAsync(async service =>
        {
            var result = await service.UnloadAsync(new UnloadCommand("B1", "TBOX", "LOT-1", "AOI-LIB-01", "Review"));

            Assert.False(result.Accepted);
            Assert.Equal(RobotCycleState.Idle, service.CurrentState);
            Assert.Contains("Cannot unload", result.Message);
        });
    }

    [Fact]
    public async Task RobotCycleServiceEmergencyStopMovesToEmergencyStoppedAndBlocksCommands()
    {
        await WithSimulatedRobotCycleServiceAsync(async service =>
        {
            ((SimulatedRobotController)IntegrationBoundaryRegistry.RobotController).TriggerEmergencyStop();

            var result = await service.LoadAsync(new LoadCommand("B1", "TBOX", "LOT-1", "AOI-LIB-01"));

            Assert.False(result.Accepted);
            Assert.Equal(RobotCycleState.EmergencyStopped, service.CurrentState);
            Assert.Contains("emergency stop", service.LastError, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task RobotCycleServiceResetClearsSimulatedEmergencyStop()
    {
        await WithSimulatedRobotCycleServiceAsync(async service =>
        {
            var robot = (SimulatedRobotController)IntegrationBoundaryRegistry.RobotController;
            robot.TriggerEmergencyStop();
            await service.LoadAsync(new LoadCommand("B1", "TBOX", "LOT-1", "AOI-LIB-01"));

            var reset = await service.ResetAsync();

            Assert.True(reset.Accepted);
            Assert.Equal(RobotCycleState.Idle, service.CurrentState);
            Assert.False(robot.IsEmergencyStopActive);
            Assert.True(string.IsNullOrWhiteSpace(service.LastError));
        });
    }

    [Fact]
    public async Task RobotAcceptanceSimulatedRobotPassesExpectedTransitions()
    {
        await WithSimulatedRobotBoundariesAsync(async () =>
        {
            var run = await RobotAcceptanceTestService.RunAsync();

            Assert.Equal("PASS", run.Status);
            Assert.Equal("Simulated", run.SourceKind);
            Assert.Equal("Completed", run.FinalState);
            Assert.True(run.InvalidTransitionRejected);
            Assert.True(run.EmergencyStopBlocked);
            Assert.True(run.ResetReturnedIdle);
            Assert.Contains(run.Steps, step => step.StepName == "Load" && step.Status == "PASS");
            Assert.Contains(run.Warnings, warning => warning.Contains("Simulation evidence only", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public async Task RobotAcceptanceEmergencyStopTestPassesWithSimulatedStop()
    {
        await WithSimulatedRobotBoundariesAsync(async () =>
        {
            var run = await RobotAcceptanceTestService.RunAsync();

            Assert.True(run.EmergencyStopBlocked);
            Assert.Contains(run.Steps, step => step.StepName == "EmergencyStopBlock" && step.Status == "PASS");
        });
    }

    [Fact]
    public async Task RobotAcceptanceNullRobotFailsWithNotConnected()
    {
        await WithRobotBoundariesAsync(new NullRobotController(), new NullEmergencyStopMonitor(), async () =>
        {
            var run = await RobotAcceptanceTestService.RunAsync(new RobotAcceptanceCriteria
            {
                RequireEmergencyStopTest = false,
            });

            Assert.Equal("FAIL", run.Status);
            Assert.Equal("NotConnected", run.SourceKind);
            Assert.Contains(run.Failures, failure => failure.Contains("No robot commands", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public async Task RobotAcceptanceInvalidTransitionDetectionIsRecorded()
    {
        await WithSimulatedRobotBoundariesAsync(async () =>
        {
            var run = await RobotAcceptanceTestService.RunAsync(new RobotAcceptanceCriteria
            {
                RequireEmergencyStopTest = false,
            });

            Assert.True(run.InvalidTransitionRejected);
            Assert.Contains(run.Steps, step =>
                step.StepName == "InvalidTransition" &&
                step.Status == "PASS" &&
                step.Message.Contains("Cannot unload", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public async Task RobotCycleServiceSafetyFaultBlocksLoad()
    {
        var robot = new SimulatedRobotController();
        var plc = new SimulatedPlcSafetyController();
        plc.SetGuardDoorClosed(false);
        await WithRobotBoundariesAsync(robot, new PlcEmergencyStopMonitor(plc), plc, async () =>
        {
            var service = new RobotCycleService((_, _) => { }, new RobotCycleOptions { PermitSafetyBypassForSimulation = false });

            var result = await service.LoadAsync(new LoadCommand("B1", "TBOX", "LOT-1", "AOI-LIB-01"));

            Assert.False(result.Accepted);
            Assert.Equal(RobotCycleState.Faulted, service.CurrentState);
            Assert.Contains("PLC safety interlock", result.Message);
            Assert.Contains("guard door open", result.Message);
        });
    }

    [Fact]
    public async Task SimulatedPlcSafetyResetClearsFault()
    {
        var plc = new SimulatedPlcSafetyController();
        plc.SetBoardClampReady(false);
        plc.SetEmergencyStopActive(true);
        plc.SetLightCurtainClear(false);

        var beforeReset = plc.GetSafetyStatus();
        Assert.False(beforeReset.IsSafeToMove);
        Assert.Contains(beforeReset.ActiveFaults, fault => fault.Contains("light curtain", StringComparison.OrdinalIgnoreCase));

        var reset = await plc.ResetSafetyFaultAsync();

        Assert.True(reset.Accepted);
        Assert.True(plc.GetSafetyStatus().IsOk);
        Assert.True(plc.GetSafetyStatus().IsSafeToMove);
        Assert.False(plc.IsEmergencyStopActive);
        Assert.True(plc.IsLightCurtainClear);
    }

    [Fact]
    public async Task RobotCellAcceptanceEmergencyStopCausesNoGoEvidenceWhenNotBlocked()
    {
        await WithRobotBoundariesAsync(new NullRobotController(), new NullEmergencyStopMonitor(), new NullPlcSafetyController(), async () =>
        {
            var run = await RobotCellAcceptanceTestService.RunAsync(new RobotAcceptanceCriteria
            {
                RequireEmergencyStopTest = true,
                RequireInvalidTransitionTest = true,
            });

            Assert.Equal("FAIL", run.Status);
            Assert.False(run.EmergencyStopBlocked);
            Assert.Contains(run.Failures, failure => failure.Contains("Load failed", StringComparison.OrdinalIgnoreCase) || failure.Contains("Robot cycle ended", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public async Task RobotCellAcceptanceReportDistinguishesSimulatedSafetySource()
    {
        var robot = new SimulatedRobotController();
        var plc = new SimulatedPlcSafetyController();
        await WithRobotBoundariesAsync(robot, new PlcEmergencyStopMonitor(plc), plc, async () =>
        {
            var run = await RobotCellAcceptanceTestService.RunAsync();
            var html = RobotCellAcceptanceTestService.BuildHtml(run);

            Assert.Equal("PASS", run.Status);
            Assert.Equal("Simulated", run.SafetySourceKind);
            Assert.True(run.SafetyFaultBlocked);
            Assert.Contains(run.Steps, step => step.StepName == "LightCurtainBlocked" && step.Status == "PASS");
            Assert.Contains("Simulated PLC Safety Controller", html);
            Assert.Contains("not safety certification", html, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task SimulatedLightingControllerRecordsProgramPerView()
    {
        var settings = new LightingSettings
        {
            Mode = LightingModes.Simulated,
            TopProgram = "TOP-BRIGHT",
            BottomProgram = "BOTTOM-LOW",
            CommandTemplate = "SET {view} {program}\\n",
        };
        var controller = new SimulatedLightingController(settings);

        var result = await controller.SetProgramAsync("Bottom", string.Empty);

        Assert.True(result.Accepted);
        Assert.Equal(IntegrationConnectionStatus.Simulated, result.Status);
        Assert.Equal("BOTTOM-LOW", controller.SelectedPrograms["Bottom"]);
        Assert.Equal("SET Bottom BOTTOM-LOW\n", controller.LastCommand);
    }

    [Fact]
    public void LightingControllerFactoryReturnsNullAndSimulatedControllers()
    {
        var none = LightingControllerFactory.Create(new LightingSettings { Mode = LightingModes.None });
        var simulated = LightingControllerFactory.Create(new LightingSettings { Mode = LightingModes.Simulated });

        Assert.IsType<NullLightingController>(none);
        Assert.IsType<SimulatedLightingController>(simulated);
        Assert.Equal(IntegrationConnectionStatus.NotConnected, none.Status);
        Assert.Equal(IntegrationConnectionStatus.Simulated, simulated.Status);
    }

    [Fact]
    public void LightingCommandTemplateSubstitutionWorks()
    {
        var command = LightingCommandFormatter.BuildCommand(
            "LIGHT {view}:{program}\\r\\n",
            "Side",
            "SIDE-AOI");

        Assert.Equal("LIGHT Side:SIDE-AOI\r\n", command);
    }

    [Fact]
    public void LightingSettingsValidationRejectsInvalidExplicitEndpoint()
    {
        var settings = new LightingSettings
        {
            Mode = LightingModes.TcpText,
            TcpHost = string.Empty,
            TcpPort = 70000,
            ResponseTimeoutMs = 0,
        };

        var errors = LightingSettingsService.Validate(settings);

        Assert.Contains(errors, error => error.Contains("host", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("port", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("timeout", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LightingSynchronizationServiceReturnsErrorWithoutCrashingOnFailure()
    {
        var settings = new LightingSettings
        {
            Mode = LightingModes.Simulated,
            TopProgram = "TOP",
            ResponseTimeoutMs = 100,
        };

        var result = await LightingSynchronizationService.SynchronizeAsync(new ThrowingLightingController(), settings, "Top");

        Assert.False(result.Accepted);
        Assert.Equal(IntegrationConnectionStatus.Error, result.Status);
        Assert.Contains("failed safely", result.Message);
    }

    private sealed class ThrowingLightingController : ILightingController
    {
        public string Name => "Throwing Lighting Controller";
        public IntegrationConnectionStatus Status => IntegrationConnectionStatus.Error;
        public string StatusMessage => "Test failure";

        public Task<IntegrationCommandResult> SetProgramAsync(
            string viewType,
            string programName,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Simulated lighting failure");
    }

    private static async Task WithSimulatedRobotCycleServiceAsync(Func<RobotCycleService, Task> action)
    {
        var previousRobot = IntegrationBoundaryRegistry.RobotController;
        var previousEmergencyStop = IntegrationBoundaryRegistry.EmergencyStopMonitor;
        var previousPlc = IntegrationBoundaryRegistry.PlcSafetyController;
        var robot = new SimulatedRobotController();

        try
        {
            IntegrationBoundaryRegistry.RobotController = robot;
            IntegrationBoundaryRegistry.EmergencyStopMonitor = new SimulatedEmergencyStopMonitor(robot);
            IntegrationBoundaryRegistry.PlcSafetyController = new NullPlcSafetyController();
            // Pure-simulation harness (no PLC, simulated robot): opt into the audited safety bypass,
            // which is off by default per the safety boundary (Docs/standard, §34). This exercises the
            // cycle state machine without hardware; production paths keep the fail-safe default.
            var service = new RobotCycleService((_, _) => { }, new RobotCycleOptions { PermitSafetyBypassForSimulation = true });
            await action(service);
        }
        finally
        {
            IntegrationBoundaryRegistry.RobotController = previousRobot;
            IntegrationBoundaryRegistry.EmergencyStopMonitor = previousEmergencyStop;
            IntegrationBoundaryRegistry.PlcSafetyController = previousPlc;
        }
    }

    private static Task WithSimulatedRobotBoundariesAsync(Func<Task> action)
    {
        var robot = new SimulatedRobotController();
        return WithRobotBoundariesAsync(robot, new SimulatedEmergencyStopMonitor(robot), action);
    }

    private static async Task WithRobotBoundariesAsync(
        IRobotController robot,
        IEmergencyStopMonitor emergencyStop,
        Func<Task> action)
        => await WithRobotBoundariesAsync(robot, emergencyStop, new NullPlcSafetyController(), action);

    private static async Task WithRobotBoundariesAsync(
        IRobotController robot,
        IEmergencyStopMonitor emergencyStop,
        IPlcSafetyController plc,
        Func<Task> action)
    {
        var previousRobot = IntegrationBoundaryRegistry.RobotController;
        var previousEmergencyStop = IntegrationBoundaryRegistry.EmergencyStopMonitor;
        var previousPlc = IntegrationBoundaryRegistry.PlcSafetyController;

        try
        {
            IntegrationBoundaryRegistry.RobotController = robot;
            IntegrationBoundaryRegistry.EmergencyStopMonitor = emergencyStop;
            IntegrationBoundaryRegistry.PlcSafetyController = plc;
            await action();
        }
        finally
        {
            IntegrationBoundaryRegistry.RobotController = previousRobot;
            IntegrationBoundaryRegistry.EmergencyStopMonitor = previousEmergencyStop;
            IntegrationBoundaryRegistry.PlcSafetyController = previousPlc;
        }
    }
}
