using System.Text.Json;
using AOI_Monitor.Data;
using AOI_Monitor.Services;
using AOI_Monitor.ViewModels;
using AOI_Monitor.Views;
using Xunit;

namespace AOI_Monitor.Tests;

public sealed class CrashSafetyTests : IDisposable
{
    private readonly string _root;

    public CrashSafetyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AOI_Monitor_CrashSafety_Tests", Guid.NewGuid().ToString("N"));
        StorageRootSettingsService.ConfigureSettingsDirectoryForTests(_root);
        AoiDatabase.ConfigureStorageRoot(_root);
        AoiDatabase.Initialize();
        UiPreferencesService.ResetForTests();
        CrashReportService.ClearForTests();
    }

    public void Dispose()
    {
        CrashReportService.ClearForTests();
        UiPreferencesService.ResetForTests();
        StorageRootSettingsService.ConfigureSettingsDirectoryForTests(null);
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"Test cleanup failed for {nameof(CrashSafetyTests)}: {ex.Message}");
        }
    }

    [Fact]
    public async Task PageRefreshExceptionIsCapturedAsRecoverableBoundaryResult()
    {
        var result = await UiErrorBoundaryService.RunAsync(
            "Simulated refresh",
            "test-page",
            _ => throw new InvalidOperationException("synthetic page refresh failure"),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(File.Exists(result.ReportPath));
        Assert.Contains("failed safely", result.OperatorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CrashReportRedactsSecretsAndCustomerImagePaths()
    {
        MesIntegrationSettingsService.Save(new Models.MesIntegrationSettings
        {
            AuthMode = Models.MesRestAuthMode.ApiKey,
            ApiKey = "top-secret-api-key",
        });

        var raw = Path.Combine(AoiDatabase.ImageVaultPath, "customer-board.png") + " top-secret-api-key";
        var redacted = CrashReportService.RedactForTests(raw);

        Assert.DoesNotContain("top-secret-api-key", redacted);
        Assert.DoesNotContain("customer-board.png", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[IMAGE_PATH]", redacted);
    }

    [Fact]
    public void CrashReportRedactsExceptionMessagesAndRecordsRedactionStatus()
    {
        const string secret = "mes-secret-token-123";
        MesIntegrationSettingsService.Save(new Models.MesIntegrationSettings
        {
            AuthMode = Models.MesRestAuthMode.ApiKey,
            ApiKey = secret,
        });

        var imagePath = Path.Combine(AoiDatabase.ImageVaultPath, "customer-board.png");
        var report = CrashReportService.WriteReport(new CrashReportRequest
        {
            Exception = new InvalidOperationException(
                $"Outer failure used {secret} and {imagePath}",
                new InvalidOperationException($"Inner failure used {secret} and {imagePath}")),
            OperationName = "Secret redaction test",
            CurrentPage = "test-page",
            IsFatal = true,
            IsUiThread = false,
        });

        var json = File.ReadAllText(report.ReportPath);
        Assert.DoesNotContain(secret, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("customer-board.png", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("redactionStatus", json);

        using var document = JsonDocument.Parse(json);
        var status = document.RootElement.GetProperty("redactionStatus");
        Assert.True(status.GetProperty("secretsRedacted").GetBoolean());
        Assert.True(status.GetProperty("customerImagePathsRedacted").GetBoolean());
        Assert.True(status.GetProperty("storageRootRedacted").GetBoolean());
    }

    [Fact]
    public async Task ErrorBoundarySafeAsyncCommandContainsRecoverableCommandFailure()
    {
        var running = false;
        var warning = string.Empty;

        var result = await ErrorBoundaryService.SafeAsyncCommand(
            "Synthetic async command",
            "test-page",
            _ => throw new InvalidOperationException("synthetic command failure"),
            isRunning => running = isRunning,
            message => warning = message);

        Assert.False(result.Success);
        Assert.False(running);
        Assert.True(File.Exists(result.ReportPath));
        Assert.Contains("failed safely", warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InvalidPluginLoadFailsSafely()
    {
        var result = VisionCameraPluginLoader.LoadFactory(Path.Combine(_root, "missing-plugin"));

        Assert.False(result.Success);
        Assert.Contains("does not exist", result.Message);
    }

    [Fact]
    public void MalformedImagePathDoesNotCrashExportVerification()
    {
        var result = ExportVerificationService.Verify(Path.Combine(_root, "missing-image.png"), "AnnotatedImageOverlay");

        Assert.Equal(Models.ExportVerificationStatus.ERROR, result.Status);
        Assert.Contains(result.Messages, message => message.Contains("does not exist", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FailedExportLogsErrorAndLeavesServiceUsable()
    {
        var missing = Path.Combine(_root, "missing-report.csv");

        var result = ExportVerificationService.RecordVerifiedExport("InspectionHistoryCsv", missing, "EXPORT_ERROR", "CrashSafetyTest");

        Assert.Contains("ERROR", result.ExportStatus, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.ExportHistoryId > 0);
        Assert.Equal(Models.ExportVerificationStatus.ERROR, result.Verification.Status);
    }

    [Fact]
    public void AsyncVoidHandlersUseExplicitBoundaryOrTryBlock()
    {
        var root = FindRepositoryRoot();
        var files = Directory.EnumerateFiles(Path.Combine(root, "AOI_Monitor"), "*.xaml.cs", SearchOption.AllDirectories)
            .Append(Path.Combine(root, "AOI_Monitor", "MainWindow.xaml.cs"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var offenders = new List<string>();

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            var searchIndex = 0;
            while ((searchIndex = text.IndexOf("async void", searchIndex, StringComparison.Ordinal)) >= 0)
            {
                var openBrace = text.IndexOf('{', searchIndex);
                if (openBrace < 0)
                {
                    offenders.Add($"{Path.GetFileName(file)}:{LineNumber(text, searchIndex)} has no method body.");
                    break;
                }

                var closeBrace = FindMatchingBrace(text, openBrace);
                if (closeBrace < 0)
                {
                    offenders.Add($"{Path.GetFileName(file)}:{LineNumber(text, searchIndex)} has an unterminated method body.");
                    break;
                }

                var body = text[(openBrace + 1)..closeBrace];
                if (!body.Contains("try", StringComparison.Ordinal) &&
                    !body.Contains("ErrorBoundaryService.", StringComparison.Ordinal) &&
                    !body.Contains("SafeAsyncCommand", StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetFileName(file)}:{LineNumber(text, searchIndex)}");
                }

                searchIndex = closeBrace + 1;
            }
        }

        Assert.True(offenders.Count == 0, "async void handlers require try/catch/finally or ErrorBoundaryService: " + string.Join(", ", offenders));
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

        throw new DirectoryNotFoundException("Could not find repository root from test output directory.");
    }

    private static int FindMatchingBrace(string text, int openBrace)
    {
        var depth = 0;
        for (var i = openBrace; i < text.Length; i++)
        {
            if (text[i] == '{')
                depth++;
            else if (text[i] == '}')
                depth--;

            if (depth == 0)
                return i;
        }

        return -1;
    }

    private static int LineNumber(string text, int index)
        => text.Take(index).Count(ch => ch == '\n') + 1;
}
