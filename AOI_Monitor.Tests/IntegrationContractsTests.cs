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
}
