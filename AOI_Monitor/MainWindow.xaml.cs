using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.IO;
using System.Diagnostics;
using AOI_Monitor.Data;
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
        ["monitor"]  = "MAIN INSPECTION / REVIEW WORKFLOW",
        ["review"]   = "MAIN INSPECTION / DISPOSITION",
        ["compare"]  = "MAIN INSPECTION / GOLDEN COMPARE",
        ["library"]  = "MAIN INSPECTION / IMAGE LIBRARY",
        ["recipe"]   = "RECIPE EDITOR",
        ["modeltest"] = "AI MODEL TEST / STAGE 1 CUSTOMER VALIDATION",
        ["reports"]  = "LOG & EXPORT / TRACEABILITY PACKAGE",
        ["spc"]      = "LOG & EXPORT / DATABASE HEALTH",
        ["profile"]  = "3D PROFILE VIEWER / SAMPLE DATA MODE",
        ["settings"] = "SETTINGS",
        ["install"]  = "SETTINGS / GUIDE / INSTALLATION NOTES",
        ["guide"]    = "SETTINGS / GUIDE",
    };

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            AoiDatabase.Initialize();
            WorkflowState.Instance.AddEvent("STORAGE", $"SQLite ready: {AoiDatabase.DatabasePath}");
            UpdateReadinessPanel(databaseConnected: File.Exists(AoiDatabase.DatabasePath), vaultAvailable: Directory.Exists(AoiDatabase.ImageVaultPath));
        }
        catch (Exception ex)
        {
            UpdateReadinessPanel(databaseConnected: false, vaultAvailable: false);
            MessageBox.Show($"Local database initialization failed:\n{ex.Message}", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        _vm = (MainViewModel)DataContext;
        RoleCombo.SelectedIndex = WorkflowState.Instance.CurrentRole switch
        {
            UserRole.Admin => 2,
            UserRole.Engineer => 1,
            _ => 0,
        };
        _vm.RefreshRolePermissions(WorkflowState.Instance.CurrentRole);
        RefreshRoleUi();
        _vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.CurrentPage))
                SwitchPage(_vm.CurrentPage);
        };

        WorkflowState.Instance.StateChanged += OnWorkflowStateChanged;
        InspectionModelConfigurationService.ConfigurationChanged += OnInspectionConfigurationChanged;
        CameraSourceFactory.ActiveSourceChanged += OnCameraSourceChanged;
        Closed += (_, _) => WorkflowState.Instance.StateChanged -= OnWorkflowStateChanged;
        Closed += (_, _) => InspectionModelConfigurationService.ConfigurationChanged -= OnInspectionConfigurationChanged;
        Closed += (_, _) => CameraSourceFactory.ActiveSourceChanged -= OnCameraSourceChanged;

        SwitchPage("monitor");
        UpdateWorkflowPanel();
    }

    private void SwitchPage(string key)
    {
        if (!EnsurePageAccess(key))
            return;

        if (!_pageCache.TryGetValue(key, out var page))
        {
            page = key switch
            {
                "monitor"  => new MonitorView(),
                "review"   => new ReviewView(),
                "compare"  => new CompareView(),
                "library"  => new LibraryView(),
                "recipe"   => new RecipeView(),
                "modeltest" => new AIModelTestView(),
                "profile"  => new ProfileView(),
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
        PlayNavigationTransition();
    }

    private void PlayNavigationTransition()
    {
        // Subtle transition to reduce abrupt page swaps.
        PageContent.Opacity = 0;
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };

        if (PageContent.RenderTransform is not TranslateTransform translate)
        {
            translate = new TranslateTransform();
            PageContent.RenderTransform = translate;
        }

        var slide = new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };

        PageContent.BeginAnimation(OpacityProperty, fade);
        translate.BeginAnimation(TranslateTransform.YProperty, slide);
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
        else if (PageContent.Content is AIModelTestView modelTest)
        {
            modelTest.RefreshFromState();
        }
        else if (PageContent.Content is RecipeView recipe)
        {
            recipe.RefreshFromState();
        }
        else if (PageContent.Content is ReportsView reports)
        {
            reports.RefreshFromState();
        }
        else if (PageContent.Content is SpcView spc)
        {
            spc.RefreshFromState();
        }

        MessageBox.Show("View refreshed.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnMenuFileClick(object sender, RoutedEventArgs e)
    {
        if (!EnsurePermission(RoleAuthorization.CanExportLogs, "Opening the export folder"))
            return;

        var exportDir = Path.Combine(AppContext.BaseDirectory, "exports");
        Directory.CreateDirectory(exportDir);
        Process.Start(new ProcessStartInfo
        {
            FileName = exportDir,
            UseShellExecute = true,
        });
    }

    private void OnLockRecipeClick(object sender, RoutedEventArgs e)
    {
        if (!EnsurePermission(RoleAuthorization.CanUseMaintenanceActions, "Recipe lock maintenance"))
            return;

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
        if (!EnsurePermission(RoleAuthorization.CanExportLogs, "Export"))
            return;

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

        if (PageContent.Content is AIModelTestView modelTest)
        {
            modelTest.ExportResults();
            return;
        }

        MessageBox.Show("Export is available on Golden Compare, Image Library, AI Model Test, and Log & Export pages.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnWorkflowStateChanged()
    {
        Dispatcher.Invoke(() =>
        {
            _vm.RefreshRolePermissions(WorkflowState.Instance.CurrentRole);
            RefreshRoleUi();
            UpdateWorkflowPanel();
        });
    }

    private void OnRoleSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
            return;

        var role = RoleCombo.SelectedIndex switch
        {
            2 => UserRole.Admin,
            1 => UserRole.Engineer,
            _ => UserRole.Operator,
        };

        WorkflowState.Instance.SetRole(role);
        _vm.RefreshRolePermissions(role);
        RefreshRoleUi();
        MessageBox.Show($"Local role set to {role}.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void RefreshRoleUi()
    {
        var isAdmin = WorkflowState.Instance.CurrentRole == UserRole.Admin;
        FileMenuBtn.IsEnabled = isAdmin;
        LockRecipeBtn.IsEnabled = isAdmin;
        ExportBtn.IsEnabled = isAdmin;
    }

    private bool EnsurePageAccess(string key)
    {
        var role = WorkflowState.Instance.CurrentRole;
        if (RoleAuthorization.CanAccessPage(role, key))
            return true;

        var message = RoleAuthorization.DeniedMessage(role, $"Opening {PageTitles.GetValueOrDefault(key, key)}");
        WorkflowState.Instance.AddEvent("ACCESS_DENIED", message);
        MessageBox.Show(message, "Permission Denied", MessageBoxButton.OK, MessageBoxImage.Warning);
        _vm.RefreshRolePermissions(role);
        if (_vm.CurrentPage != "monitor")
            _vm.CurrentPage = "monitor";
        return false;
    }

    private bool EnsurePermission(Func<UserRole, bool> permission, string action)
    {
        if (WorkflowState.Instance.TryAuthorize(permission, action, out var message))
            return true;

        MessageBox.Show(message, "Permission Denied", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private void OnInspectionConfigurationChanged()
    {
        Dispatcher.Invoke(() => UpdateInspectionEngineStatus());
    }

    private void OnCameraSourceChanged()
    {
        Dispatcher.Invoke(UpdateCameraStatus);
    }

    private void UpdateWorkflowPanel()
    {
        var state = WorkflowState.Instance;

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

    private void UpdateReadinessPanel(bool databaseConnected, bool vaultAvailable)
    {
        DatabaseStatusText.Text = databaseConnected ? "Connected" : "Not Connected";
        DatabaseStatusText.Foreground = StatusBrush(databaseConnected);

        ImageVaultStatusText.Text = vaultAvailable ? "Available" : "Not Available";
        ImageVaultStatusText.Foreground = StatusBrush(vaultAvailable);

        UpdateInspectionEngineStatus();

        UpdateCameraStatus();

        RobotStatusText.Text = "Not Connected";
        RobotStatusText.Foreground = StatusBrush(false);

        MesStatusText.Text = "Not Connected";
        MesStatusText.Foreground = StatusBrush(false);
    }

    private static Brush StatusBrush(bool ok)
        => new SolidColorBrush((Color)ColorConverter.ConvertFromString(ok ? "#50F56E" : "#F27777"));

    private void UpdateInspectionEngineStatus()
    {
        var status = InspectionModelConfigurationService.GetStatus();
        var statusText = InspectionModelConfigurationService.GetStatusText();
        InspectionEngineStatusText.Text = statusText;
        HeaderEngineText.Text = statusText;
        InspectionEngineStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(status switch
        {
            Models.InspectionEngineStatus.MlModelConfigured => "#50F56E",
            Models.InspectionEngineStatus.MlRuntimeError => "#F27777",
            _ => "#E1A334",
        }));
    }

    private void UpdateCameraStatus()
    {
        var status = CameraSourceFactory.ActiveSource.ConnectionStatus;
        CameraStatusText.Text = status switch
        {
            CameraConnectionStatus.Simulated => "Simulated",
            CameraConnectionStatus.Error => "Error",
            _ => "Not Connected",
        };
        CameraStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(status switch
        {
            CameraConnectionStatus.Simulated => "#E1A334",
            CameraConnectionStatus.Error => "#F27777",
            _ => "#F27777",
        }));
    }
}
