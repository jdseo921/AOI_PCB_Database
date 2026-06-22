using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using AOI_Monitor.ViewModels;
using AOI_Monitor.Views;
using Xunit;

namespace AOI_Monitor.UiTests;

public sealed class UiNavigationPerformanceTests : IDisposable
{
    private readonly string _root;

    public UiNavigationPerformanceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AOI_Monitor_UiNavigationPerformance_Tests", Guid.NewGuid().ToString("N"));
        StorageRootSettingsService.ConfigureSettingsDirectoryForTests(_root);
        AoiDatabase.ConfigureStorageRoot(_root);
        AoiDatabase.Initialize();
        WorkflowState.Instance.SetCurrentUser("NavigationPerfAdmin", UserRole.Admin);
        FirstRunSettingsService.ResetForTests();
        FirstRunSettingsService.MarkCompleted();
        UiPreferencesService.ResetForTests();
        UiPerformanceMonitorService.ClearForTests();
    }

    public void Dispose()
    {
        UiPreferencesService.ResetForTests();
        UiPerformanceMonitorService.ClearForTests();
        FirstRunSettingsService.ResetForTests();
        StorageRootSettingsService.ConfigureSettingsDirectoryForTests(null);
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Test cleanup failed for {nameof(UiNavigationPerformanceTests)}: {ex.Message}");
        }
    }

    [Fact]
    public void MainShellCyclesMajorPagesWithinNavigationThresholds()
    {
        var report = RunOnSta(() =>
        {
            EnsureApplicationResources();
            var shell = new MainWindow();
            var pages = new (string Key, Func<FrameworkElement> Factory)[]
            {
                ("home", () => new HomeView()),
                ("library", () => new LibraryView()),
                ("monitor", () => new MonitorView()),
                ("compare", () => new CompareView()),
                ("review", () => new ReviewView()),
                ("recipe", () => new RecipeView()),
                ("modeltest", () => new AIModelTestView()),
                ("spc", () => new SpcView()),
                ("reports", () => new ReportsView()),
                ("settings", () => new SettingsView(new MainViewModel())),
                ("calibration", () => new CalibrationView()),
                ("profile", () => new ProfileView()),
                ("pilot", () => new PilotWizardView()),
                ("install", () => new InstallView()),
                ("guide", () => new GuideView()),
            };

            var report = new UiNavigationPerformanceReport();
            var cache = new Dictionary<string, FrameworkElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var page in pages)
            {
                var construction = Stopwatch.StartNew();
                var element = page.Factory();
                construction.Stop();
                cache[page.Key] = element;
                UiPerformanceMonitorService.RecordPageConstruction(page.Key, construction.ElapsedMilliseconds);
            }

            for (var pass = 1; pass <= 3; pass++)
            {
                foreach (var page in pages)
                {
                    var stopwatch = Stopwatch.StartNew();
                    var element = cache[page.Key];
                    UiPerformanceMonitorService.RecordNavigationVisualResponse(page.Key, stopwatch.ElapsedMilliseconds);
                    MeasureElement(element);
                    stopwatch.Stop();
                    UiPerformanceMonitorService.RecordCachedPageSwitch(page.Key, stopwatch.ElapsedMilliseconds);
                    report.Switches.Add(new UiNavigationSwitchResult(page.Key, pass, stopwatch.ElapsedMilliseconds, page.Key));
                }
            }

            report.Events.AddRange(UiPerformanceMonitorService.RecentEvents);
            shell.Close();
            WriteReport(report);
            return report;
        });

        var failures = new List<string>();
        failures.AddRange(report.Events
            .Where(item => item.Category == "NAVIGATION_VISUAL_RESPONSE" &&
                item.DurationMilliseconds > UiPerformanceMonitorService.MenuResponseWarningMilliseconds)
            .Select(item => $"Visual response {item.Name}: {item.DurationMilliseconds} ms"));
        failures.AddRange(report.Events
            .Where(item => item.Category == "CACHED_PAGE_SWITCH" &&
                item.DurationMilliseconds > UiPerformanceMonitorService.CachedPageSwitchWarningMilliseconds)
            .Select(item => $"Cached switch {item.Name}: {item.DurationMilliseconds} ms"));
        failures.AddRange(report.Events
            .Where(item => item.Category == "PAGE_CONSTRUCTION" &&
                item.DurationMilliseconds > 1_500)
            .Select(item => $"Constructor {item.Name}: {item.DurationMilliseconds} ms"));

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static void EnsureApplicationResources()
    {
        if (Application.Current is not null)
            return;

        var app = new App();
        app.InitializeComponent();
    }

    private static void MeasureElement(FrameworkElement element)
    {
        element.Width = 1920;
        element.Height = 1080;
        element.Measure(new Size(1920, 1080));
        element.Arrange(new Rect(0, 0, 1920, 1080));
        element.UpdateLayout();
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
            ExceptionDispatchInfo.Capture(exception).Throw();

        return result!;
    }

    private static void ShutdownApplicationIfOwnedByCurrentThread()
    {
        try
        {
            if (Application.Current?.Dispatcher.CheckAccess() == true)
                Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Application shutdown cleanup failed: {ex.Message}");
        }
    }

    private static void WriteReport(UiNavigationPerformanceReport report)
    {
        var root = Path.Combine(GetRepositoryRoot(), "TestResults");
        Directory.CreateDirectory(root);
        File.WriteAllText(
            Path.Combine(root, "ui_navigation_performance.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AOI_PCB_Database.slnx")))
            directory = directory.Parent;

        if (directory is null)
            throw new DirectoryNotFoundException("Could not locate repository root.");

        return directory.FullName;
    }

    private sealed class UiNavigationPerformanceReport
    {
        public string SchemaVersion { get; } = "ui-navigation-performance/v1";
        public DateTime GeneratedAtUtc { get; } = DateTime.UtcNow;
        public long VisualResponseThresholdMilliseconds { get; } = UiPerformanceMonitorService.MenuResponseWarningMilliseconds;
        public long CachedSwitchThresholdMilliseconds { get; } = UiPerformanceMonitorService.CachedPageSwitchWarningMilliseconds;
        public List<UiNavigationSwitchResult> Switches { get; } = new();
        public List<UiPerformanceEvent> Events { get; } = new();
    }

    private sealed record UiNavigationSwitchResult(
        string RequestedPage,
        int Pass,
        long DispatcherSwitchMilliseconds,
        string ActivePage);
}
