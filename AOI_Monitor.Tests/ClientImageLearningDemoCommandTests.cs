using System.Diagnostics;
using AOI_Monitor.Data;
using AOI_Monitor.Tools;
using Xunit;

namespace AOI_Monitor.Tests;

public sealed class ClientImageLearningDemoCommandTests : IDisposable
{
    private readonly string _root;

    public ClientImageLearningDemoCommandTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AOI_Monitor_ClientImageLearningDemo_Tests", Guid.NewGuid().ToString("N"));
        AoiDatabase.ConfigureStorageRoot(Path.Combine(_root, "storage"));
        AoiDatabase.AuditOperatorProvider = null;
        AoiDatabase.AuditUserIdProvider = null;
        AoiDatabase.AuditUserRoleProvider = null;
        AoiDatabase.AuditStationProvider = null;
        AoiDatabase.Initialize();
    }

    public void Dispose()
    {
        AoiDatabase.AuditOperatorProvider = null;
        AoiDatabase.AuditUserIdProvider = null;
        AoiDatabase.AuditUserRoleProvider = null;
        AoiDatabase.AuditStationProvider = null;
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            System.Diagnostics.Trace.WriteLine("Client image-learning demo test cleanup skipped because the temporary folder was still in use.");
        }
        catch (UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.WriteLine("Client image-learning demo test cleanup skipped because the temporary folder was not accessible.");
        }
    }

    [Fact]
    public void ClientImageLearningDemoSyntheticCompletesAndPrintsSummary()
    {
        var outputFolder = Path.Combine(_root, "client_package");
        var (exitCode, output, error) = RunSynthetic(outputFolder);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error);
        Assert.Contains(ClientImageLearningDemoCommand.SyntheticDemoNotice, output, StringComparison.Ordinal);
        Assert.Contains("projectId:", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("images learned from:", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("false calls before learning:", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("false calls after learning:", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("recommended threshold:", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("report path:", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClientImageLearningDemoRealModeMissingFoldersFailsClearly()
    {
        var projectFolder = Path.Combine(_root, "real_project");
        Directory.CreateDirectory(Path.Combine(projectFolder, "ok_learning"));
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = ClientImageLearningDemoCommand.Execute(
            [
                "client-image-learning-demo",
                "--output",
                Path.Combine(_root, "real_output"),
                "--operator",
                "Engineer01 [Engineer]",
                "--project-folder",
                projectFolder,
            ],
            output,
            error);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Missing required folder", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("golden", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ok_validation", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("inspection", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClientImageLearningDemoOutputFilesExist()
    {
        var outputFolder = Path.Combine(_root, "client_package_files");
        var (exitCode, _, error) = RunSynthetic(outputFolder);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error);
        Assert.True(File.Exists(Path.Combine(outputFolder, "README_CLIENT_IMAGE_LEARNING_DEMO.txt")));
        Assert.True(File.Exists(Path.Combine(outputFolder, "visual_learning_report.html")));
        Assert.True(File.Exists(Path.Combine(outputFolder, "visual_learning_report.json")));
        Assert.True(File.Exists(Path.Combine(outputFolder, "learned_reference.png")));
        Assert.True(File.Exists(Path.Combine(outputFolder, "learned_tolerance_map.png")));
        Assert.True(File.Exists(Path.Combine(outputFolder, "before_after_false_call_report.html")));
        Assert.True(File.Exists(Path.Combine(outputFolder, "before_after_results.csv")));
        Assert.True(File.Exists(Path.Combine(outputFolder, "threshold_sweep.csv")));
        Assert.True(File.Exists(Path.Combine(outputFolder, "inspection_results.csv")));
        Assert.True(Directory.Exists(Path.Combine(outputFolder, "annotated_overlays")));
        Assert.True(Directory.Exists(Path.Combine(outputFolder, "heatmaps")));
        Assert.True(Directory.Exists(Path.Combine(outputFolder, "example_images")));
        Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(outputFolder, "annotated_overlays"), "*.png", SearchOption.AllDirectories));
        Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(outputFolder, "heatmaps"), "*.png", SearchOption.AllDirectories));
    }

    [Fact]
    public void ClientImageLearningDemoReportContainsNoProductionReadyClaim()
    {
        var outputFolder = Path.Combine(_root, "client_package_truthful");
        var (exitCode, _, error) = RunSynthetic(outputFolder);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error);
        var html = File.ReadAllText(Path.Combine(outputFolder, "visual_learning_report.html"));
        var readme = File.ReadAllText(Path.Combine(outputFolder, "README_CLIENT_IMAGE_LEARNING_DEMO.txt"));

        Assert.DoesNotContain("production ready", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("factory accepted", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("certified safe", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("production ready", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Formal acceptance requires customer/evaluator dataset", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClientImageLearningDemoGeneratedArtifactsStayInsideRequestedOutputFolder()
    {
        var outputFolder = Path.Combine(_root, "client_package_location");
        var (exitCode, _, error) = RunSynthetic(outputFolder);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error);
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var outputRoot = Path.GetFullPath(outputFolder);
        var packageFiles = Directory.EnumerateFiles(outputFolder, "*", SearchOption.AllDirectories).ToArray();

        Assert.All(packageFiles, file => Assert.StartsWith(outputRoot, Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(packageFiles, file => Path.GetFullPath(file).StartsWith(repositoryRoot, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ClientImageLearningDemoSampleGeneratorCreatesProjectAndCommandConsumesOutput()
    {
        var repositoryRoot = FindRepositoryRoot();
        var generator = Path.Combine(repositoryRoot, "SampleData", "generate_image_learning_demo_project.ps1");
        var projectFolder = Path.Combine(_root, "generated-image-learning-project");
        var packageFolder = Path.Combine(_root, "generated-image-learning-output");

        var script = RunPowerShellScript(
            generator,
            "-OutputRoot",
            projectFolder,
            "-GoldenCount",
            "2",
            "-OkLearningCount",
            "5",
            "-OkValidationCount",
            "3",
            "-InspectionCount",
            "2",
            "-NgValidationCount",
            "2",
            "-Seed",
            "42");

        Assert.Equal(0, script.ExitCode);
        Assert.Contains("Synthetic image-only learning demo project generated", script.Output, StringComparison.OrdinalIgnoreCase);
        AssertGeneratedRoleCount(projectFolder, "golden", 2);
        AssertGeneratedRoleCount(projectFolder, "ok_learning", 5);
        AssertGeneratedRoleCount(projectFolder, "ok_validation", 3);
        AssertGeneratedRoleCount(projectFolder, "inspection", 2);
        AssertGeneratedRoleCount(projectFolder, "ng_validation", 2);

        var truthPath = Path.Combine(projectFolder, "image_truth.csv");
        Assert.True(File.Exists(truthPath));
        Assert.True(File.ReadAllLines(truthPath).Length >= 8);
        Assert.Contains("README_SYNTHETIC_IMAGE_LEARNING_DEMO.txt", Directory.EnumerateFiles(projectFolder).Select(Path.GetFileName));

        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = ClientImageLearningDemoCommand.Execute(
            [
                "client-image-learning-demo",
                "--project-folder",
                projectFolder,
                "--output",
                packageFolder,
                "--operator",
                "ci-demo",
                "--false-call-target",
                "0.05",
            ],
            output,
            error);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        Assert.True(File.Exists(Path.Combine(packageFolder, "visual_learning_report.html")));
        Assert.True(File.Exists(Path.Combine(packageFolder, "before_after_results.csv")));
    }

    private static (int ExitCode, string Output, string Error) RunSynthetic(string outputFolder)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = ClientImageLearningDemoCommand.Execute(
            [
                "client-image-learning-demo",
                "--output",
                outputFolder,
                "--operator",
                "Engineer01 [Engineer]",
                "--synthetic",
                "--false-call-target",
                "0.05",
            ],
            output,
            error);

        return (exitCode, output.ToString(), error.ToString());
    }

    private static void AssertGeneratedRoleCount(string projectFolder, string roleFolder, int expected)
    {
        var folder = Path.Combine(projectFolder, roleFolder);
        Assert.True(Directory.Exists(folder), $"Expected role folder: {folder}");
        Assert.Equal(expected, Directory.EnumerateFiles(folder, "synthetic_*.png").Count());
    }

    private static (int ExitCode, string Output, string Error) RunPowerShellScript(string scriptPath, params string[] arguments)
    {
        Assert.True(File.Exists(scriptPath), $"PowerShell script not found: {scriptPath}");
        var startInfo = new ProcessStartInfo("pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start PowerShell image-learning demo generator.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(90000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("PowerShell image-learning demo generator did not finish within 90 seconds.");
        }

        return (process.ExitCode, output, error);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AOI_PCB_Database.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find repository root from test output folder.");
    }
}
