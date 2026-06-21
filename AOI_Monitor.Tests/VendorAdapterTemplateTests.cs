using AOI_Monitor.Models;
using AOI_Monitor.Services;
using CameraAdapterTemplate;
using LightingAdapterTemplate;
using RobotControllerTemplate;
using RobotPlcAdapterTemplate;
using Xunit;
using LegacyFakePlcSafetyController = RobotControllerTemplate.FakePlcSafetyController;
using LegacyFakeRobotController = RobotControllerTemplate.FakeRobotController;
using LegacyRobotTemplateRegistration = RobotControllerTemplate.RobotTemplateRegistration;
using RobotPlcFakePlcSafetyController = RobotPlcAdapterTemplate.FakePlcSafetyController;
using RobotPlcFakeRobotController = RobotPlcAdapterTemplate.FakeRobotController;

namespace AOI_Monitor.Tests;

public sealed class VendorAdapterTemplateTests
{
    [Fact]
    public void TemplateProjectsDoNotDeclareVendorSdkPackages()
    {
        var repo = FindRepoRoot();
        foreach (var project in Directory.EnumerateFiles(Path.Combine(repo, "Templates"), "*.csproj", SearchOption.AllDirectories))
        {
            var xml = File.ReadAllText(project);
            Assert.DoesNotContain("<PackageReference", xml, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void MainAppDoesNotDeclareVendorSdkPackages()
    {
        var project = File.ReadAllText(Path.Combine(FindRepoRoot(), "AOI_Monitor", "AOI_Monitor.csproj"));
        var forbiddenSdkNames = new[]
        {
            "Basler",
            "Pylon",
            "Hikrobot",
            "MvCam",
            "Cognex",
            "Keyence",
            "Spinnaker",
            "Sapera",
            "Fanuc",
            "Epson",
            "Yamaha",
            "Denso",
            "UniversalRobots",
            "OpcUa",
            "Plc",
        };

        foreach (var sdkName in forbiddenSdkNames)
        {
            Assert.DoesNotContain($"PackageReference Include=\"{sdkName}", project, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void CameraTemplateManifestLoadsThroughPluginLoader()
    {
        var manifest = CreatePluginFolder(
            "camera",
            Path.Combine(FindRepoRoot(), "Templates", "CameraAdapterTemplate", "camera_adapter_manifest.json"),
            typeof(FakeVisionCameraAdapterFactory).Assembly.Location);
        var result = CameraAdapterPluginService.LoadFactoryFromManifest(manifest);

        Assert.True(result.Success, result.Message);
        Assert.NotNull(result.Factory);
        Assert.Equal("template.fake-camera", result.Factory.AdapterId);

        var discovery = result.Factory.CreateDeviceDiscovery(new CameraSourceSettings());
        var devices = Assert.IsAssignableFrom<IReadOnlyList<VisionDeviceInfo>>(discovery!.DiscoverDevices());
        var device = Assert.Single(devices);
        Assert.Equal("Simulated", device.Status);
        Assert.Contains("simulation", device.Capabilities ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void InvalidCameraTemplateManifestFailsSafely()
    {
        var folder = Path.Combine(Path.GetTempPath(), "aoi_vendor_template_tests", $"invalid_camera_{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var manifest = Path.Combine(folder, "camera_adapter_manifest.json");
        File.WriteAllText(manifest, """
        {
          "adapterId": "template.invalid-camera",
          "displayName": "Invalid Camera Adapter"
        }
        """);

        var result = CameraAdapterPluginService.LoadFactoryFromManifest(manifest);

        Assert.False(result.Success);
        Assert.Null(result.Factory);
        Assert.Contains("Version is required", result.Message);
        Assert.Contains("AssemblyFile is required", result.Message);
        Assert.Contains("FactoryTypeName is required", result.Message);
    }

    [Fact]
    public void FakeCameraTemplateLoadsButRemainsSimulationEvidence()
    {
        var settings = new CameraSourceSettings
        {
            SourceKey = CameraSourceFactory.GenericVisionAdapterSourceKey,
            TopDeviceId = "FAKE-CAM-TOP",
            BoardModel = "TEMPLATE-BOARD",
            LotId = "TEMPLATE-LOT",
            AcquisitionMode = CameraAcquisitionMode.SoftwareTrigger,
        };
        var factory = new FakeVisionCameraAdapterFactory();
        var adapter = factory.CreateAdapter(settings);
        var run = CameraAcceptanceTestService.Run(
            settings,
            new CameraAcceptanceCriteria
            {
                FramesPerView = 1,
                RequiredViews = ["Top"],
                RequiredPixelFormats = ["Mono8"],
                MinimumWidth = 640,
                MinimumHeight = 480,
            },
            adapter);

        Assert.Equal("template.fake-camera", factory.AdapterId);
        Assert.False(run.IsRealHardware);
        Assert.Equal("NOT VALIDATED", run.FactoryReadinessStatus);
        Assert.Contains(run.Warnings, warning => warning.Contains("simulation evidence", StringComparison.OrdinalIgnoreCase));
        Assert.All(run.Frames, frame => Assert.True(frame.IsSimulated));
    }

    [Fact]
    public async Task LightingTemplateManifestLoadsAndRemainsSimulationEvidence()
    {
        var manifest = CreatePluginFolder(
            "lighting",
            Path.Combine(FindRepoRoot(), "Templates", "LightingAdapterTemplate", "lighting_adapter_manifest.json"),
            typeof(FakeLightingControllerFactory).Assembly.Location);
        var result = LightingAdapterPluginService.LoadFactoryFromManifest(manifest);

        Assert.True(result.Success, result.Message);
        Assert.NotNull(result.Factory);
        Assert.Equal("template.fake-lighting", result.Factory.DriverId);

        var controller = result.Factory.Create(new LightingSettings { Mode = LightingModes.Simulated });
        var command = await controller.SetProgramAsync("Top", "TEMPLATE_TOP");

        Assert.True(command.Accepted);
        Assert.Equal(IntegrationConnectionStatus.Simulated, command.Status);
        Assert.Equal(IntegrationConnectionStatus.Simulated, controller.Status);
        Assert.Contains("No real strobe", controller.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No real hardware command", command.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RobotTemplateRegistrationKeepsRobotAndSafetySimulationOnly()
    {
        var previousRobot = IntegrationBoundaryRegistry.RobotController;
        var previousSafety = IntegrationBoundaryRegistry.PlcSafetyController;
        try
        {
            LegacyRobotTemplateRegistration.RegisterFakeControllers();

            Assert.IsType<LegacyFakeRobotController>(IntegrationBoundaryRegistry.RobotController);
            Assert.IsType<LegacyFakePlcSafetyController>(IntegrationBoundaryRegistry.PlcSafetyController);
            Assert.Equal(IntegrationConnectionStatus.Simulated, IntegrationBoundaryRegistry.RobotController.Status);
            Assert.Equal(IntegrationConnectionStatus.Simulated, IntegrationBoundaryRegistry.PlcSafetyController.Status);

            var load = await IntegrationBoundaryRegistry.RobotController.LoadAsync(
                new LoadCommand("BOARD-1", "MODEL-A", "LOT-1", "STATION-1"));
            var safety = IntegrationBoundaryRegistry.PlcSafetyController.GetSafetyStatus();

            Assert.True(load.Accepted);
            Assert.Equal(IntegrationConnectionStatus.Simulated, load.Status);
            Assert.Contains("No real motion", load.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(safety.IsOk);
            Assert.Contains("Simulation-only", safety.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            IntegrationBoundaryRegistry.RobotController = previousRobot;
            IntegrationBoundaryRegistry.PlcSafetyController = previousSafety;
        }
    }

    [Fact]
    public async Task RobotPlcTemplateRegistrationKeepsRobotAndPlcSafetySimulationOnly()
    {
        var previousRobot = IntegrationBoundaryRegistry.RobotController;
        var previousSafety = IntegrationBoundaryRegistry.PlcSafetyController;
        try
        {
            RobotPlcTemplateRegistration.RegisterFakeControllers();

            Assert.IsType<RobotPlcFakeRobotController>(IntegrationBoundaryRegistry.RobotController);
            Assert.IsType<RobotPlcFakePlcSafetyController>(IntegrationBoundaryRegistry.PlcSafetyController);
            Assert.Equal(IntegrationConnectionStatus.Simulated, IntegrationBoundaryRegistry.RobotController.Status);
            Assert.Equal(IntegrationConnectionStatus.Simulated, IntegrationBoundaryRegistry.PlcSafetyController.Status);

            var inspect = await IntegrationBoundaryRegistry.RobotController.InspectAsync(
                new InspectCommand("BOARD-1", "MODEL-A", "LOT-1", "STATION-1", "Top"));
            var safety = IntegrationBoundaryRegistry.PlcSafetyController.GetSafetyStatus();

            Assert.True(inspect.Accepted);
            Assert.Equal(IntegrationConnectionStatus.Simulated, inspect.Status);
            Assert.Contains("No real motion", inspect.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(safety.IsOk);
            Assert.Contains("Simulation-only", safety.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            IntegrationBoundaryRegistry.RobotController = previousRobot;
            IntegrationBoundaryRegistry.PlcSafetyController = previousSafety;
        }
    }

    [Fact]
    public void VendorGuideDocumentsSafetyAndPackagingBoundaries()
    {
        var guide = File.ReadAllText(Path.Combine(FindRepoRoot(), "Docs", "Vendor_Adapter_Implementation_Guide.md"));

        Assert.Contains("Camera Adapter Requirements", guide);
        Assert.Contains("manifest schema", guide, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("camera frame metadata", guide, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Simulated vs Real Hardware", guide);
        Assert.Contains("Timing Requirements", guide);
        Assert.Contains("Safety Warnings", guide);
        Assert.Contains("Run Camera Acceptance Test", guide);
        Assert.Contains("Run Lighting Sync Test", guide);
        Assert.Contains("Run Robot Cell Acceptance", guide);
        Assert.Contains("Packaging Plugin Folder", guide);
    }

    [Fact]
    public void HardwareInTheLoopChecklistCoversRequiredCommissioningEvidence()
    {
        var checklist = File.ReadAllText(Path.Combine(FindRepoRoot(), "Docs", "Hardware_In_The_Loop_Checklist.md"));

        foreach (var required in new[]
        {
            "Camera Discovery",
            "Top / Side / Bottom Assignment",
            "Lighting Program Validation",
            "Trigger-To-Frame Timing",
            "3D Profile Acquisition",
            "Robot Load / Inspect / Unload",
            "PLC Safety Interlock Tests",
            "E-Stop Test",
            "Final Pass / Fail Criteria",
            "Evidence Package",
        })
        {
            Assert.Contains(required, checklist, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("Template adapters and simulated evidence are not real hardware validation", checklist, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AOI_PCB_Database.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    private static string CreatePluginFolder(string prefix, string manifestPath, string assemblyPath)
    {
        var folder = Path.Combine(Path.GetTempPath(), "aoi_vendor_template_tests", $"{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var copiedManifest = Path.Combine(folder, Path.GetFileName(manifestPath));
        File.Copy(manifestPath, copiedManifest);
        File.Copy(assemblyPath, Path.Combine(folder, Path.GetFileName(assemblyPath)));
        return copiedManifest;
    }
}
