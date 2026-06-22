using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Xunit;

namespace AOI_Monitor.Tests;

public sealed class UiNavigationSoakTestServiceTests : IDisposable
{
    private readonly string _root;

    public UiNavigationSoakTestServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AOI_Monitor_UiNavigationSoak_Tests", Guid.NewGuid().ToString("N"));
        StorageRootSettingsService.ConfigureSettingsDirectoryForTests(_root);
        AoiDatabase.ConfigureStorageRoot(_root);
        AoiDatabase.Initialize();
    }

    public void Dispose()
    {
        StorageRootSettingsService.ConfigureSettingsDirectoryForTests(null);
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"Test cleanup failed for {nameof(UiNavigationSoakTestServiceTests)}: {ex.Message}");
        }
    }

    [Fact]
    public async Task SmokeProfileRunsQuicklyInTestModeAndWritesReports()
    {
        var options = UiNavigationSoakTestService.CreateProfileOptions(
            UiNavigationSoakProfile.FiveMinuteSmoke,
            Path.Combine(_root, "ui_stability"),
            testMode: true,
            operatorId: "TestOperator [Admin]");

        var result = await UiNavigationSoakTestService.RunAsync(options, null, null, CancellationToken.None);
        var export = UiNavigationSoakTestService.WriteReports(result, options.OutputFolder);

        Assert.True(result.PageAttempts > 0);
        Assert.True(result.CyclesCompleted >= 1);
        Assert.Equal(0, result.CrashCount);
        Assert.True(result.MemoryGrowthMegabytes < options.MemoryGrowthFailMegabytes);
        Assert.True(File.Exists(export.HtmlPath));
        Assert.True(File.Exists(export.PdfPath));
        Assert.True(File.Exists(export.JsonPath));
        Assert.True(File.Exists(export.CsvPath));
    }

    [Fact]
    public async Task ForcedPageExceptionIsCapturedAsEvidence()
    {
        var options = UiNavigationSoakTestService.CreateProfileOptions(
            UiNavigationSoakProfile.FiveMinuteSmoke,
            Path.Combine(_root, "ui_stability"),
            testMode: true);

        var result = await UiNavigationSoakTestService.RunAsync(
            options,
            (page, _) => page.Contains("AI Model", StringComparison.OrdinalIgnoreCase)
                ? throw new InvalidOperationException("forced page crash")
                : Task.CompletedTask,
            null,
            CancellationToken.None);

        Assert.Equal("FAIL", result.Status);
        Assert.True(result.CrashCount > 0);
        Assert.Contains(result.Events, item => item.Status == "ERROR" && item.PageName.Contains("AI Model", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, error => error.Contains("navigation exception", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SlowPageAndMemoryThresholdsProduceWarnings()
    {
        var options = UiNavigationSoakTestService.CreateProfileOptions(
            UiNavigationSoakProfile.FiveMinuteSmoke,
            Path.Combine(_root, "ui_stability"),
            testMode: true) with
        {
            SlowNavigationThresholdMs = 1,
            MemoryGrowthWarningMegabytes = -1,
            MemoryGrowthFailMegabytes = 1_000_000,
        };

        var result = await UiNavigationSoakTestService.RunAsync(
            options,
            async (_, token) => await Task.Delay(5, token),
            null,
            CancellationToken.None);

        Assert.Equal("WARN", result.Status);
        Assert.True(result.SlowNavigationCount > 0);
        Assert.Contains(result.Warnings, warning => warning.Contains("Working set grew", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ThirtyMinuteOrBetterPassRecognizedForClientGate()
    {
        var started = DateTime.UtcNow.AddMinutes(-31);
        var result = new UiNavigationSoakResult
        {
            StartedAtUtc = started,
            CompletedAtUtc = started.AddMinutes(30),
            Profile = UiNavigationSoakProfile.ThirtyMinuteLocalStability,
            RequestedDuration = TimeSpan.FromMinutes(30),
            Status = "PASS",
        };

        Assert.True(UiNavigationSoakTestService.IsThirtyMinuteOrBetterPass(result));
    }

    [Fact]
    public void TestModeSmokeDoesNotSatisfyClientDemoStabilityEvidence()
    {
        var started = DateTime.UtcNow.AddSeconds(-2);
        var result = new UiNavigationSoakResult
        {
            StartedAtUtc = started,
            CompletedAtUtc = started.AddSeconds(2),
            Profile = UiNavigationSoakProfile.FiveMinuteSmoke,
            RequestedDuration = TimeSpan.FromSeconds(2),
            Status = "PASS",
        };

        Assert.False(UiNavigationSoakTestService.IsAcceptedShorterSmokePass(result));
        Assert.False(UiNavigationSoakTestService.IsThirtyMinuteOrBetterPass(result));
    }

    [Fact]
    public void FiveMinuteSmokeCanBeAcceptedAsShorterDemoEvidence()
    {
        var started = DateTime.UtcNow.AddMinutes(-5);
        var result = new UiNavigationSoakResult
        {
            StartedAtUtc = started,
            CompletedAtUtc = started.AddMinutes(5),
            Profile = UiNavigationSoakProfile.FiveMinuteSmoke,
            RequestedDuration = TimeSpan.FromMinutes(5),
            Status = "PASS",
        };

        Assert.True(UiNavigationSoakTestService.IsAcceptedShorterSmokePass(result));
    }
}
