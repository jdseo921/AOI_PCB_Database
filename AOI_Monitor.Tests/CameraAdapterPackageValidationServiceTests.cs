using AOI_Monitor.Services;
using CameraAdapterTemplate;
using Xunit;

namespace AOI_Monitor.Tests;

public sealed class CameraAdapterPackageValidationServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aoi_camera_adapter_validation_tests", Guid.NewGuid().ToString("N"));

    public CameraAdapterPackageValidationServiceTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void CameraAdapterPackageValidationFailsWhenManifestFieldsAreMissing()
    {
        var folder = CreateFolder("missing-fields");
        File.WriteAllText(
            Path.Combine(folder, "camera_adapter_manifest.json"),
            """
            {
              "displayName": "Incomplete Camera Adapter"
            }
            """);
        var output = Path.Combine(_root, "out-missing-fields");

        var result = CameraAdapterPackageValidationService.Run(new CameraAdapterPackageValidationRequest
        {
            AdapterFolder = folder,
            OutputFolder = output,
        });

        Assert.Equal("FAIL", result.Status);
        Assert.Equal("FAIL", result.ManifestStatus);
        Assert.False(result.AdapterLoaded);
        Assert.Contains(result.Failures, failure => failure.Contains("adapterId is required", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Failures, failure => failure.Contains("assemblyFile is required", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Failures, failure => failure.Contains("supportedViews", StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(result.SummaryJsonPath));
        Assert.True(File.Exists(result.SummaryHtmlPath));
    }

    [Fact]
    public void CameraAdapterPackageValidationReturnsFailWhenDllIsMissing()
    {
        var folder = CreateTemplatePackage("missing-dll", copyAssembly: false);
        var output = Path.Combine(_root, "out-missing-dll");

        var result = CameraAdapterPackageValidationService.Run(new CameraAdapterPackageValidationRequest
        {
            AdapterFolder = folder,
            OutputFolder = output,
        });

        Assert.Equal("FAIL", result.Status);
        Assert.Equal("PASS", result.ManifestStatus);
        Assert.Equal("FAIL", result.LoadStatus);
        Assert.False(result.AdapterLoaded);
        Assert.Contains(result.Failures, failure => failure.Contains("assembly was not found", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CameraAdapterPackageValidationRunsFakeCameraButKeepsReadinessNotValidated()
    {
        var folder = CreateTemplatePackage("fake-camera");
        var output = Path.Combine(_root, "out-fake-camera");

        var result = CameraAdapterPackageValidationService.Run(new CameraAdapterPackageValidationRequest
        {
            AdapterFolder = folder,
            OutputFolder = output,
        });

        Assert.Equal("WARN", result.Status);
        Assert.Equal("PASS", result.ManifestStatus);
        Assert.Equal("PASS", result.LoadStatus);
        Assert.True(result.AdapterLoaded);
        Assert.Equal("PASS", result.DiscoveryStatus);
        Assert.Equal(1, result.DiscoveredDeviceCount);
        Assert.Equal("PASS", result.AcceptanceStatus);
        Assert.Equal("NOT VALIDATED", result.FactoryReadinessStatus);
        Assert.False(result.IsRealHardwareReady);
        Assert.True(result.IsSimulatedEvidence);
        Assert.NotNull(result.AcceptanceRun);
        var run = result.AcceptanceRun!;
        Assert.NotEmpty(run.Frames);
        Assert.All(run.Frames, frame => Assert.True(frame.IsSimulated));
        Assert.True(File.Exists(result.AcceptanceJsonPath));
        Assert.True(File.Exists(result.AcceptanceHtmlPath));
    }

    [Fact]
    public void CameraAdapterPackageValidationExportsSimulatedEvidenceClassification()
    {
        var folder = CreateTemplatePackage("simulated-classification");
        var output = Path.Combine(_root, "out-simulated-classification");

        var result = CameraAdapterPackageValidationService.Run(new CameraAdapterPackageValidationRequest
        {
            AdapterFolder = folder,
            OutputFolder = output,
        });

        var summaryJson = File.ReadAllText(result.SummaryJsonPath);
        var summaryHtml = File.ReadAllText(result.SummaryHtmlPath);
        var acceptanceJson = File.ReadAllText(result.AcceptanceJsonPath);

        Assert.Contains("\"isSimulatedEvidence\": true", summaryJson);
        Assert.Contains("\"factoryReadinessStatus\": \"NOT VALIDATED\"", summaryJson);
        Assert.Contains("\"isRealHardware\": false", acceptanceJson);
        Assert.Contains("Fake, template, folder, replay, null, or metadata-only evidence is not real camera hardware readiness", summaryHtml);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException ex)
        {
            System.Diagnostics.Trace.WriteLine($"Camera adapter validation test cleanup skipped: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            System.Diagnostics.Trace.WriteLine($"Camera adapter validation test cleanup skipped: {ex.Message}");
        }
    }

    private string CreateTemplatePackage(string name, bool copyAssembly = true)
    {
        var folder = CreateFolder(name);
        File.Copy(
            Path.Combine(FindRepoRoot(), "Templates", "CameraAdapterTemplate", "camera_adapter_manifest.json"),
            Path.Combine(folder, "camera_adapter_manifest.json"));
        if (copyAssembly)
        {
            File.Copy(
                typeof(FakeVisionCameraAdapterFactory).Assembly.Location,
                Path.Combine(folder, "CameraAdapterTemplate.dll"));
        }

        return folder;
    }

    private string CreateFolder(string name)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        return folder;
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
