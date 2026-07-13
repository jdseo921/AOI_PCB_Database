using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using AOI_Monitor.Services;

namespace AOI_Monitor;

public partial class App : Application
{
    // Two instances sharing the same SQLite database, alarm snapshot, and export folders
    // race each other (last-writer-wins on the JSON snapshots), so the workstation app is
    // strictly single-instance per user session.
    private static Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(initiallyOwned: true, @"Local\AOI_Monitor_SingleInstance", out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show(
                "AOI Monitor is already running on this workstation. Use the existing window; a second instance would corrupt shared inspection data.",
                "AOI Monitor",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            Shutdown();
            return;
        }

        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // The mutex belongs to the losing instance (never acquired); nothing to release.
        }

        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var report = CrashReportService.WriteReport(new CrashReportRequest
        {
            Exception = e.Exception,
            OperationName = "WPF Dispatcher",
            CurrentPage = TryGetCurrentPage(),
            IsFatal = false,
            IsUiThread = true,
        });

        e.Handled = true;
        ShowFactorySafeError(report.OperatorMessage);
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception ?? new InvalidOperationException(e.ExceptionObject?.ToString() ?? "Unknown unhandled exception.");
        var report = CrashReportService.WriteReport(new CrashReportRequest
        {
            Exception = exception,
            OperationName = "AppDomain",
            CurrentPage = TryGetCurrentPage(),
            IsFatal = e.IsTerminating,
            IsUiThread = false,
        });

        if (!e.IsTerminating)
            Dispatcher.BeginInvoke(() => ShowFactorySafeError(report.OperatorMessage));
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        var report = CrashReportService.WriteReport(new CrashReportRequest
        {
            Exception = e.Exception,
            OperationName = "Unobserved Task",
            CurrentPage = TryGetCurrentPage(),
            IsFatal = false,
            IsUiThread = false,
        });

        e.SetObserved();
        Dispatcher.BeginInvoke(() => ShowFactorySafeError(report.OperatorMessage));
    }

    private static string TryGetCurrentPage()
    {
        try
        {
            var app = Current;
            if (app is null)
                return "UNKNOWN";

            if (app.Dispatcher.CheckAccess())
                return app.MainWindow is MainWindow mainWindow ? mainWindow.CurrentPageKey : "UNKNOWN";

            return app.Dispatcher.Invoke(() => app.MainWindow is MainWindow mainWindow ? mainWindow.CurrentPageKey : "UNKNOWN");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"Current page lookup failed during exception handling: {ex.Message}");
            return "UNKNOWN";
        }
    }

    private static void ShowFactorySafeError(string message)
    {
        try
        {
            MessageBox.Show(
                $"{message}\n\nThe application will remain open. Review the operation status before continuing production work.",
                "AOI Monitor Recoverable Error",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"Last-resort factory-safe error display failed: {ex.Message}");
        }
    }
}
