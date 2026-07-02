using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AOI_Monitor.Data;
using AOI_Monitor.Tools;
using Xunit;

namespace AOI_Monitor.Tests;

public sealed class LearnFromImagesCommandTests : IDisposable
{
    private readonly string _root;

    public LearnFromImagesCommandTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AOI_Monitor_LearnFromImages_Tests", Guid.NewGuid().ToString("N"));
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
            Trace.WriteLine("Learn-from-images command test cleanup skipped because the temporary folder was still in use.");
        }
        catch (UnauthorizedAccessException)
        {
            Trace.WriteLine("Learn-from-images command test cleanup skipped because the temporary folder was not accessible.");
        }
    }

    [Fact]
    public void LearnFromImagesGeneratedImageOnlyProjectCreatesReportAndSummary()
    {
        var projectFolder = GenerateSyntheticProject("generated-customer-images");
        var outputFolder = Path.Combine(_root, "learn-from-images-output");

        var (exitCode, output, error) = RunCommand(projectFolder, outputFolder, "--board-model", "CUSTOMER-BOARD-A");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error);
        Assert.Matches("^(PASS|WARN) ", output);
        Assert.Contains("images learned:", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("false-call rate:", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("possible escape status:", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("output report:", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("manual labels required: none", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("camera hardware required: none", output, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(outputFolder, "visual_learning_report.html")));
        Assert.True(File.Exists(Path.Combine(outputFolder, "visual_learning_report.json")));
        Assert.True(File.Exists(Path.Combine(outputFolder, "learn_from_images_summary.json")));
        Assert.True(File.Exists(Path.Combine(outputFolder, "README_LEARN_FROM_IMAGES.txt")));
        Assert.True(File.Exists(Path.Combine(outputFolder, "before_after_results.csv")));
        Assert.True(File.Exists(Path.Combine(outputFolder, "inspection_results.csv")));
        Assert.True(File.Exists(Path.Combine(outputFolder, "visual_evidence", "visual_evidence_manifest.json")));
        Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(outputFolder, "visual_evidence"), "*.png", SearchOption.AllDirectories));

        var readme = File.ReadAllText(Path.Combine(outputFolder, "README_LEARN_FROM_IMAGES.txt"));
        Assert.Contains("No defect labels were required", readme, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("production ready", readme, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LearnFromImagesMissingLearningImagesFailsClearly()
    {
        var projectFolder = CreateConventionFolder("missing-learning");
        WritePng(Path.Combine(projectFolder, "ok_validation"), "ok-validation.png", 80);
        WritePng(Path.Combine(projectFolder, "inspection"), "inspection.png", 90);

        var (exitCode, _, error) = RunCommand(projectFolder, Path.Combine(_root, "missing-learning-output"));

        Assert.NotEqual(0, exitCode);
        Assert.Contains("FAIL", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Missing Golden / Reference or OK Learning images", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("golden/", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ok_learning/", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LearnFromImagesMissingOkValidationImagesFailsClearly()
    {
        var projectFolder = CreateConventionFolder("missing-ok-validation");
        WritePng(Path.Combine(projectFolder, "golden"), "golden.png", 100);
        WritePng(Path.Combine(projectFolder, "inspection"), "inspection.png", 110);

        var (exitCode, _, error) = RunCommand(projectFolder, Path.Combine(_root, "missing-ok-validation-output"));

        Assert.NotEqual(0, exitCode);
        Assert.Contains("FAIL", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Missing OK Validation images", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ok_validation/", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LearnFromImagesUnsupportedAndUnreadableImagesAreReportedAsWarnings()
    {
        var projectFolder = CreateConventionFolder("warning-images");
        WritePng(Path.Combine(projectFolder, "golden"), "golden.png", 120);
        WritePng(Path.Combine(projectFolder, "ok_validation"), "ok-validation.png", 122);
        WritePng(Path.Combine(projectFolder, "inspection"), "inspection.png", 124);
        File.WriteAllText(Path.Combine(projectFolder, "ok_validation", "bad.png"), "this is not a readable image");
        File.WriteAllText(Path.Combine(projectFolder, "inspection", "notes.txt"), "unsupported text file");

        var (exitCode, output, error) = RunCommand(projectFolder, Path.Combine(_root, "warning-output"));

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error);
        Assert.StartsWith("WARN ", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Unreadable image skipped", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Unsupported image skipped", output, StringComparison.OrdinalIgnoreCase);
    }

    private string GenerateSyntheticProject(string name)
    {
        var repositoryRoot = FindRepositoryRoot();
        var generator = Path.Combine(repositoryRoot, "SampleData", "generate_image_learning_demo_project.ps1");
        var projectFolder = Path.Combine(_root, name);
        var result = RunPowerShellScript(
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

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Error);
        return projectFolder;
    }

    private static (int ExitCode, string Output, string Error) RunCommand(string projectFolder, string outputFolder, params string[] extraArgs)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var args = new List<string>
        {
            "learn-from-images",
            "--project-folder",
            projectFolder,
            "--output",
            outputFolder,
            "--operator",
            "ci-customer",
            "--false-call-target",
            "0.05",
        };
        args.AddRange(extraArgs);

        var exitCode = LearnFromImagesCommand.Execute(args.ToArray(), output, error);
        return (exitCode, output.ToString(), error.ToString());
    }

    private string CreateConventionFolder(string name)
    {
        var projectFolder = Path.Combine(_root, name);
        foreach (var folder in new[] { "golden", "ok_learning", "ok_validation", "inspection", "ng_validation" })
            Directory.CreateDirectory(Path.Combine(projectFolder, folder));

        return projectFolder;
    }

    private static string WritePng(string folder, string fileName, int value)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, fileName);
        const int width = 64;
        const int height = 64;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 4;
                var trace = x is >= 18 and <= 45 && y is >= 28 and <= 34;
                var component = x is >= 10 and <= 22 && y is >= 10 and <= 22;
                pixels[offset] = (byte)Math.Clamp(value + (trace ? 18 : 0), 0, 255);
                pixels[offset + 1] = (byte)Math.Clamp(value + 35 + (component ? 28 : 0), 0, 255);
                pixels[offset + 2] = (byte)Math.Clamp(value + 8 + (trace ? 55 : 0), 0, 255);
                pixels[offset + 3] = 255;
            }
        }

        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
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
