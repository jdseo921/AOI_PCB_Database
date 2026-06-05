using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.IO;
using AOI_Monitor.Services;
using AOI_Monitor.ViewModels;
using AOI_Monitor.Views;

namespace AOI_Monitor;

public partial class MainWindow : Window
{
    private readonly Dictionary<string, UserControl> _pageCache = new();
    private MainViewModel _vm = null!;

    private static readonly Dictionary<string, string> PageTitles = new()
    {
        ["monitor"]  = "INSTALLATION MONITOR / AOI SERVICE STATUS",
        ["review"]   = "REVIEW / DISPOSITION STATION",
        ["compare"]  = "SIDE-BY-SIDE GOLDEN COMPARISON",
        ["library"]  = "DEFECT IMAGE LIBRARY / DATABASE BROWSER",
        ["recipe"]   = "INSPECTION PROGRAM / RECIPE MATRIX",
        ["spc"]      = "SPC / TRACEABILITY / DATABASE HEALTH",
        ["reports"]  = "REPORTS / EXPORT / MAINTENANCE UTILITIES",
        ["install"]  = "INSTALLATION PLAN / BACKGROUND RUNTIME MODEL",
        ["settings"] = "SYSTEM SETTINGS / LOCALIZATION",
        ["guide"]    = "OPERATION GUIDE / INDUSTRIAL WORKFLOW",
    };

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _vm = (MainViewModel)DataContext;
        _vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.CurrentPage))
                SwitchPage(_vm.CurrentPage);
        };

        WorkflowState.Instance.StateChanged += OnWorkflowStateChanged;
        Closed += (_, _) => WorkflowState.Instance.StateChanged -= OnWorkflowStateChanged;

        SwitchPage("monitor");
        UpdateWorkflowPanel();
    }

    private void SwitchPage(string key)
    {
        if (!_pageCache.TryGetValue(key, out var page))
        {
            page = key switch
            {
                "monitor"  => new MonitorView(),
                "review"   => new ReviewView(),
                "compare"  => new CompareView(),
                "library"  => new LibraryView(),
                "recipe"   => new RecipeView(),
                "spc"      => new SpcView(),
                "reports"  => new ReportsView(),
                "install"  => new InstallView(),
                "settings" => new SettingsView(_vm),
                "guide"    => new GuideView(),
                _          => new MonitorView(),
            };
            _pageCache[key] = page;
        }

        PageContent.Content = page;
        PageTitleText.Text  = PageTitles.TryGetValue(key, out var t) ? t : key.ToUpperInvariant();
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        if (PageContent.Content is CompareView compare)
        {
            compare.RefreshFromState();
        }
        else if (PageContent.Content is ReviewView review)
        {
            review.RefreshFromState();
        }
        else if (PageContent.Content is LibraryView library)
        {
            library.RefreshFromState();
        }

        MessageBox.Show("View refreshed.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnLockRecipeClick(object sender, RoutedEventArgs e)
    {
        var state = WorkflowState.Instance;
        state.IsRecipeLocked = !state.IsRecipeLocked;
        state.AddEvent("SYSTEM", state.IsRecipeLocked ? "Recipe locked." : "Recipe unlocked.");

        MessageBox.Show(
            state.IsRecipeLocked ? "Recipe is now locked." : "Recipe lock released.",
            "AOI Monitor",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (PageContent.Content is CompareView compare)
        {
            compare.ExportPair();
            return;
        }

        if (PageContent.Content is LibraryView library)
        {
            library.ExportSelectedRecord();
            return;
        }

        MessageBox.Show("Export is available on Compare and Library pages.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnWorkflowStateChanged()
    {
        Dispatcher.Invoke(UpdateWorkflowPanel);
    }

    private void UpdateWorkflowPanel()
    {
        var state = WorkflowState.Instance;
        ReviewPolicyText.Text = WorkflowState.ToDisplay(state.DetectionPriority);

        WorkflowSampleText.Text = string.IsNullOrWhiteSpace(state.SampleImagePath)
            ? "none"
            : Path.GetFileName(state.SampleImagePath);

        WorkflowGoldenText.Text = string.IsNullOrWhiteSpace(state.GoldenImagePath)
            ? "none"
            : Path.GetFileName(state.GoldenImagePath);

        if (state.LastAnalysis is null)
        {
            WorkflowScoreText.Text = "--";
            WorkflowVerdictText.Text = "REVIEW";
            WorkflowVerdictText.Foreground = Brushes.LightGray;
            WorkflowVerdictBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2A2E31"));
            WorkflowVerdictBorder.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#555E66"));
            return;
        }

        WorkflowScoreText.Text = $"{state.LastAnalysis.DifferenceScore:F1}%";
        WorkflowVerdictText.Text = state.LastAnalysis.Verdict;

        if (state.LastAnalysis.Verdict == "NG")
        {
            WorkflowVerdictText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFBFC1"));
            WorkflowVerdictBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#35191B"));
            WorkflowVerdictBorder.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9A393E"));
        }
        else if (state.LastAnalysis.Verdict == "OK")
        {
            WorkflowVerdictText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#C6FFD0"));
            WorkflowVerdictBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#14311D"));
            WorkflowVerdictBorder.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#377849"));
        }
        else
        {
            WorkflowVerdictText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFE0A7"));
            WorkflowVerdictBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#372914"));
            WorkflowVerdictBorder.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8C6C35"));
        }
    }
}
