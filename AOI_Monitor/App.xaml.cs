using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using AOI_Monitor.Services;

namespace AOI_Monitor;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
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
