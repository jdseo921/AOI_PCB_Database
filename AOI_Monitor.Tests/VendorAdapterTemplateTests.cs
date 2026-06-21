using AOI_Monitor.Models;
using AOI_Monitor.Services;
using CameraAdapterTemplate;
using Xunit;

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
    public void VendorGuideDocumentsSafetyAndPackagingBoundaries()
    {
        var guide = File.ReadAllText(Path.Combine(FindRepoRoot(), "Docs", "Vendor_Adapter_Implementation_Guide.md"));

        Assert.Contains("Camera Adapter Requirements", guide);
        Assert.Contains("Simulated vs Real Hardware", guide);
        Assert.Contains("Timing Requirements", guide);
        Assert.Contains("Safety Warnings", guide);
        Assert.Contains("Packaging Plugin Folder", guide);
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
}
