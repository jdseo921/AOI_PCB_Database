using System.IO;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Xunit;

namespace AOI_Monitor.UiTests;

public sealed class HmiLayoutAuditTests : IDisposable
{
    private readonly string _root;

    public HmiLayoutAuditTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AOI_Monitor_HmiLayoutAudit_Tests", Guid.NewGuid().ToString("N"));
        StorageRootSettingsService.ConfigureSettingsDirectoryForTests(_root);
        AoiDatabase.ConfigureStorageRoot(_root);
        AoiDatabase.Initialize();
        WorkflowState.Instance.SetCurrentUser("HmiAuditAdmin", UserRole.Admin);
        UiPreferencesService.ResetForTests();
    }

    public void Dispose()
    {
        UiPreferencesService.ResetForTests();
        StorageRootSettingsService.ConfigureSettingsDirectoryForTests(null);
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"Test cleanup failed for {nameof(HmiLayoutAuditTests)}: {ex.Message}");
        }
    }

    [Fact]
    public void MajorOperatorViewsPassHmiLayoutAudit()
    {
        var report = RunOnSta(() =>
        {
            var audit = HmiLayoutAuditService.RunAudit(new HmiLayoutAuditOptions
            {
                Width = 1920,
                Height = 1080,
                DpiScales = new[] { 1.0, 1.25, 1.5 },
            });
            HmiLayoutAuditService.WriteAuditArtifacts(audit);
            return audit;
        });

        var failures = report.Issues
            .Where(issue => issue.Severity == HmiLayoutIssueSeverity.Fail && !issue.Approved)
            .Select(issue => $"{issue.ViewName} {issue.DpiScale:P0} {issue.IssueType} {issue.Target}: {issue.Message}")
            .ToArray();

        Assert.True(failures.Length == 0, string.Join(Environment.NewLine, failures));
    }

    private static T RunOnSta<T>(Func<T> action)
    {
        T? result = default;
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
            finally
            {
                ShutdownApplicationIfOwnedByCurrentThread();
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (exception is not null)
            throw exception;

        return result!;
    }

    private static void ShutdownApplicationIfOwnedByCurrentThread()
    {
        try
        {
            if (System.Windows.Application.Current?.Dispatcher.CheckAccess() == true)
                System.Windows.Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"Application shutdown cleanup failed: {ex.Message}");
        }
    }
}
