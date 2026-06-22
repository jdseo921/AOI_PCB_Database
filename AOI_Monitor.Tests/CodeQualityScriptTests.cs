using System.Diagnostics;
using Xunit;

namespace AOI_Monitor.Tests;

public sealed class CodeQualityScriptTests : IDisposable
{
    private readonly string _root;

    public CodeQualityScriptTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AOI_Monitor_CodeQuality_Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Test cleanup failed for {nameof(CodeQualityScriptTests)}: {ex.Message}");
        }
    }

    [Fact]
    public async Task CheckCodeQualityScriptFailsOnIntentionallyBadSample()
    {
        var samplePath = Path.Combine(_root, "BadSample.cs");
        var badSample = string.Join(Environment.NewLine, new[]
        {
            "using System;",
            "using System.Threading.Tasks;",
            "",
            "public sealed class BadSample",
            "{",
            "    public async void Helper()",
            "    {",
            "        await Task.Delay(1);",
            "    }",
            "",
            "    public void Swallow()",
            "    {",
            "        try",
            "        {",
            "        }",
            "        catch",
            "        {",
            "        }",
            "    }",
            "",
            "    public void ShowRawStack(Exception ex)",
            "    {",
            "        MessageBox.Show(ex.ToString());",
            "    }",
            "",
            "    public void HardCodedSecret()",
            "    {",
            "        var password = \"ReallySecretValue123\";",
            "    }",
            "}",
            "",
        });
        await File.WriteAllTextAsync(samplePath, badSample);

        var repositoryRoot = FindRepositoryRoot();
        var reportPath = Path.Combine(_root, "code_quality_report.json");
        using var process = StartPowerShell(
            Path.Combine(repositoryRoot, "Scripts", "check-code-quality.ps1"),
            "-SkipFormat",
            "-SkipBuild",
            "-SourceRoots",
            _root,
            "-ReportPath",
            reportPath);

        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.NotEqual(0, process.ExitCode);
        Assert.True(File.Exists(reportPath));
        var combined = output + error + await File.ReadAllTextAsync(reportPath);
        Assert.Contains("CQ-CATCH-001", combined);
        Assert.Contains("CQ-ASYNC-001", combined);
        Assert.Contains("CQ-MSG-001", combined);
        Assert.Contains("CQ-SEC-001", combined);
    }

    private static Process StartPowerShell(string scriptPath, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "pwsh",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add(scriptPath);
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        return Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start pwsh.");
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AOI_PCB_Database.slnx")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not find repository root.");
    }
}
