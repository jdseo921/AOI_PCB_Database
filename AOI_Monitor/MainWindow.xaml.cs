using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Globalization;
using System.IO;
using System.Diagnostics;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
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
        ["pilot"]    = "CUSTOMER PILOT WIZARD / STAGE 1-2 EVIDENCE",
        ["spc"]      = "LOG & EXPORT / DATABASE HEALTH",
        ["profile"]  = "3D PROFILE VIEWER / SAMPLE DATA MODE",
        ["calibration"] = "2D CALIBRATION PROFILE / STAGE 2 PREPARATION",
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
            StorageRootSettingsService.ApplySavedStorageRoot();
            AoiDatabase.Initialize();
            CameraSourceSettingsService.ApplyActiveSource();
            LightingSettingsService.ApplyIntegrationBoundary();
            MesIntegrationSettingsService.ApplyIntegrationBoundary();
            WorkflowState.Instance.SetAuthenticationMode(AuthenticationSettingsService.CurrentMode);
            WorkflowState.Instance.AddEvent("STORAGE", $"SQLite ready: {AoiDatabase.DatabasePath}");
            UpdateReadinessPanel(databaseConnected: File.Exists(AoiDatabase.DatabasePath), vaultAvailable: Directory.Exists(AoiDatabase.ImageVaultPath));
            UpdateFooterStatus();
        }
        catch (Exception ex)
        {
            UpdateReadinessPanel(databaseConnected: false, vaultAvailable: false);
            UpdateFooterStatus();
            MessageBox.Show($"Local database initialization failed:\n{ex.Message}", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        _vm = (MainViewModel)DataContext;
        UserIdText.Text = WorkflowState.Instance.OperatorId;
        RoleCombo.SelectedIndex = WorkflowState.Instance.CurrentRole switch
        {
            UserRole.Admin => 2,
            UserRole.Engineer => 1,
            _ => 0,
        };
        AuthModeCombo.SelectedIndex = AuthenticationModeToCombo(WorkflowState.Instance.AuthenticationMode);
        _vm.RefreshRolePermissions(WorkflowState.Instance.CurrentRole);
        RefreshRoleUi();
        RefreshOperatingModeBanner();
        _vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.CurrentPage))
                SwitchPage(_vm.CurrentPage);
        };

        WorkflowState.Instance.StateChanged += OnWorkflowStateChanged;
        InspectionModelConfigurationService.ConfigurationChanged += OnInspectionConfigurationChanged;
        CameraSourceFactory.ActiveSourceChanged += OnCameraSourceChanged;
        LightingSettingsService.SettingsChanged += OnLightingSettingsChanged;
        MesIntegrationSettingsService.SettingsChanged += OnMesIntegrationSettingsChanged;
        AuthenticationSettingsService.AuthenticationChanged += OnAuthenticationChanged;
        OperatingModeSettingsService.SettingsChanged += OnOperatingModeSettingsChanged;
        Closed += (_, _) => WorkflowState.Instance.StateChanged -= OnWorkflowStateChanged;
        Closed += (_, _) => InspectionModelConfigurationService.ConfigurationChanged -= OnInspectionConfigurationChanged;
        Closed += (_, _) => CameraSourceFactory.ActiveSourceChanged -= OnCameraSourceChanged;
        Closed += (_, _) => LightingSettingsService.SettingsChanged -= OnLightingSettingsChanged;
        Closed += (_, _) => MesIntegrationSettingsService.SettingsChanged -= OnMesIntegrationSettingsChanged;
        Closed += (_, _) => AuthenticationSettingsService.AuthenticationChanged -= OnAuthenticationChanged;
        Closed += (_, _) => OperatingModeSettingsService.SettingsChanged -= OnOperatingModeSettingsChanged;

        SwitchPage("monitor");
        UpdateWorkflowPanel();
        Dispatcher.BeginInvoke(new Action(ShowFirstRunWizardIfNeeded));
    }

    private void ShowFirstRunWizardIfNeeded()
    {
        try
        {
            if (FirstRunSettingsService.IsCompleted())
                return;

            var wizard = new FirstRunWizardView
            {
                Owner = this,
            };
            wizard.ShowDialog();
            UpdateReadinessPanel(databaseConnected: File.Exists(AoiDatabase.DatabasePath), vaultAvailable: Directory.Exists(AoiDatabase.ImageVaultPath));
            UpdateFooterStatus();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            WorkflowState.Instance.AddEvent("FIRST_RUN", $"First-run setup wizard could not be shown: {ex.Message}");
            MessageBox.Show($"Setup wizard could not be shown. The app remains usable.\n{ex.Message}", "AOI Monitor Setup", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
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
                "calibration" => new CalibrationView(),
                "spc"      => new SpcView(),
                "reports"  => new ReportsView(),
                "pilot"    => new PilotWizardView(),
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
        else if (PageContent.Content is PilotWizardView pilot)
        {
            pilot.RefreshFromState();
        }
        else if (PageContent.Content is SpcView spc)
        {
            spc.RefreshFromState();
        }
        else if (PageContent.Content is CalibrationView calibration)
        {
            calibration.RefreshFromState();
        }

        UpdateFooterStatus();
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
            UpdateFooterStatus();
        });
    }

    private void OnRoleSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
            return;
        if (!OperatingModePolicyService.IsDemoRoleSelectorAllowed())
            return;
        if (WorkflowState.Instance.AuthenticationMode != AuthenticationMode.DemoLocalRoleSelector)
            return;

        var role = RoleCombo.SelectedIndex switch
        {
            2 => UserRole.Admin,
            1 => UserRole.Engineer,
            _ => UserRole.Operator,
        };

        WorkflowState.Instance.SetCurrentUser(UserIdText.Text, role);
        _vm.RefreshRolePermissions(role);
        RefreshRoleUi();
        MessageBox.Show($"Demo local role selector set to {WorkflowState.Instance.OperatorWithRole}. This is not production authentication.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnAuthModeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
            return;

        if (!EnsurePermission(RoleAuthorization.CanManageSettings, "Changing authentication mode"))
        {
            AuthModeCombo.SelectedIndex = AuthenticationModeToCombo(WorkflowState.Instance.AuthenticationMode);
            return;
        }

        var mode = ComboToAuthenticationMode(AuthModeCombo.SelectedIndex);
        if (mode == AuthenticationMode.DemoLocalRoleSelector &&
            !OperatingModePolicyService.IsDemoRoleSelectorAllowed())
        {
            MessageBox.Show("DemoLocalRoleSelector is available only in Demo Mode. Select LocalUsers or the MES authentication boundary for Pilot/Production.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
            AuthModeCombo.SelectedIndex = AuthenticationModeToCombo(WorkflowState.Instance.AuthenticationMode);
            return;
        }

        AuthenticationSettingsService.Save(new AuthenticationSettings { Mode = mode }, WorkflowState.Instance.OperatorWithRole);
        WorkflowState.Instance.SetAuthenticationMode(mode);
        RefreshRoleUi();
        MessageBox.Show($"Authentication mode set to {mode}.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnLocalLoginClick(object sender, RoutedEventArgs e)
    {
        var mode = WorkflowState.Instance.AuthenticationMode;
        if (mode == AuthenticationMode.DemoLocalRoleSelector &&
            !OperatingModePolicyService.IsDemoRoleSelectorAllowed())
        {
            MessageBox.Show("Demo role selection is disabled outside Demo Mode. Switch authentication to LocalUsers or MES boundary.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (mode == AuthenticationMode.LocalUsers)
        {
            if (!LocalUserService.TryAuthenticate(UserIdText.Text, LoginPasswordBox.Password, out var user))
            {
                WorkflowState.Instance.AddEvent("ACCESS_DENIED", $"LocalUsers login failed for {UserIdText.Text.Trim()}.");
                MessageBox.Show("Local user login failed.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            WorkflowState.Instance.SetCurrentUser(user.UserId, user.Role, AuthenticationMode.LocalUsers);
            RoleCombo.SelectedIndex = user.Role switch
            {
                UserRole.Admin => 2,
                UserRole.Engineer => 1,
                _ => 0,
            };
            _vm.RefreshRolePermissions(user.Role);
            RefreshRoleUi();
            MessageBox.Show($"Local user authenticated as {WorkflowState.Instance.OperatorWithRole}.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (mode == AuthenticationMode.MesAuthenticationBoundary)
        {
            WorkflowState.Instance.SetCurrentUser(UserIdText.Text, UserRole.Operator, AuthenticationMode.MesAuthenticationBoundary);
            _vm.RefreshRolePermissions(UserRole.Operator);
            RefreshRoleUi();
            MessageBox.Show(
                "MES authentication boundary is documented for Stage 4 integration. No production MES identity provider is active in this PoC, so the session is limited to Operator.",
                "AOI Monitor",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var role = RoleCombo.SelectedIndex switch
        {
            2 => UserRole.Admin,
            1 => UserRole.Engineer,
            _ => UserRole.Operator,
        };

        WorkflowState.Instance.SetCurrentUser(UserIdText.Text, role, AuthenticationMode.DemoLocalRoleSelector);
        _vm.RefreshRolePermissions(role);
        RefreshRoleUi();
        MessageBox.Show(
            $"Demo local role selector set to {WorkflowState.Instance.OperatorWithRole}.\nThis is not production authentication.",
            "AOI Monitor",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private void OnCreateLocalUserClick(object sender, RoutedEventArgs e)
    {
        if (!EnsurePermission(RoleAuthorization.CanManageSettings, "Creating local users"))
            return;

        var password = PromptForPassword("Create Local User", $"Password for {UserIdText.Text.Trim()}");
        if (string.IsNullOrWhiteSpace(password))
            return;

        try
        {
            var role = RoleCombo.SelectedIndex switch
            {
                2 => UserRole.Admin,
                1 => UserRole.Engineer,
                _ => UserRole.Operator,
            };
            var user = LocalUserService.CreateUser(UserIdText.Text, role, password, WorkflowState.Instance.CurrentRole, WorkflowState.Instance.OperatorWithRole);
            MessageBox.Show($"Local user created: {user.UserId} [{user.Role}]. Password was stored as a salted hash.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException or IOException)
        {
            MessageBox.Show($"Local user could not be created:\n{ex.Message}", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnChangeLocalPasswordClick(object sender, RoutedEventArgs e)
    {
        if (!EnsurePermission(RoleAuthorization.CanManageSettings, "Changing local user password"))
            return;

        var password = PromptForPassword("Set Local User Password", $"New password for {UserIdText.Text.Trim()}");
        if (string.IsNullOrWhiteSpace(password))
            return;

        try
        {
            var user = LocalUserService.ChangePassword(UserIdText.Text, password, WorkflowState.Instance.CurrentRole, WorkflowState.Instance.OperatorWithRole);
            MessageBox.Show($"Password updated for local user {user.UserId}.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException or IOException)
        {
            MessageBox.Show($"Local user password could not be changed:\n{ex.Message}", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnDisableLocalUserClick(object sender, RoutedEventArgs e)
    {
        if (!EnsurePermission(RoleAuthorization.CanManageSettings, "Disabling local users"))
            return;

        var targetUserId = UserIdText.Text.Trim();
        if (string.IsNullOrWhiteSpace(targetUserId))
            return;

        try
        {
            var user = LocalUserService.DisableUser(targetUserId, WorkflowState.Instance.CurrentRole, WorkflowState.Instance.OperatorWithRole, "Disabled from local user management UI.");
            MessageBox.Show($"Local user disabled: {user.UserId}.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or IOException)
        {
            MessageBox.Show($"Local user could not be disabled:\n{ex.Message}", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnDeleteLocalUserClick(object sender, RoutedEventArgs e)
    {
        if (!EnsurePermission(RoleAuthorization.CanManageSettings, "Deleting local users"))
            return;

        var targetUserId = UserIdText.Text.Trim();
        if (string.IsNullOrWhiteSpace(targetUserId))
            return;

        if (MessageBox.Show($"Delete local user {targetUserId}? This removes the local login record.", "AOI Monitor", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        try
        {
            LocalUserService.DeleteUser(targetUserId, WorkflowState.Instance.CurrentRole, WorkflowState.Instance.OperatorWithRole);
            MessageBox.Show($"Local user deleted: {targetUserId}.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or IOException)
        {
            MessageBox.Show($"Local user could not be deleted:\n{ex.Message}", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RefreshRoleUi()
    {
        var isAdmin = WorkflowState.Instance.CurrentRole == UserRole.Admin;
        FileMenuBtn.IsEnabled = isAdmin;
        LockRecipeBtn.IsEnabled = isAdmin;
        ExportBtn.IsEnabled = isAdmin;
        var canManageLocalUsers = isAdmin && WorkflowState.Instance.AuthenticationMode == AuthenticationMode.LocalUsers;
        CreateUserBtn.IsEnabled = canManageLocalUsers;
        ChangePasswordBtn.IsEnabled = canManageLocalUsers;
        DisableUserBtn.IsEnabled = canManageLocalUsers;
        DeleteUserBtn.IsEnabled = canManageLocalUsers;
        LoginPasswordBox.IsEnabled = WorkflowState.Instance.AuthenticationMode == AuthenticationMode.LocalUsers;
        RoleCombo.IsEnabled = WorkflowState.Instance.AuthenticationMode == AuthenticationMode.DemoLocalRoleSelector &&
            OperatingModePolicyService.IsDemoRoleSelectorAllowed();
        AuthModeCombo.IsEnabled = isAdmin;
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

    private void OnMesIntegrationSettingsChanged()
    {
        Dispatcher.Invoke(UpdateIntegrationBoundaryStatus);
    }

    private void OnAuthenticationChanged()
    {
        Dispatcher.Invoke(() =>
        {
            WorkflowState.Instance.SetAuthenticationMode(AuthenticationSettingsService.CurrentMode);
            AuthModeCombo.SelectedIndex = AuthenticationModeToCombo(WorkflowState.Instance.AuthenticationMode);
            RefreshRoleUi();
        });
    }

    private void OnOperatingModeSettingsChanged()
    {
        Dispatcher.Invoke(() =>
        {
            RefreshOperatingModeBanner();
            RefreshRoleUi();
            if (PageContent.Content is LibraryView library)
                library.RefreshFromState();
        });
    }

    private void RefreshOperatingModeBanner()
    {
        var mode = OperatingModeSettingsService.Load();
        OperatingModeBannerText.Text = mode switch
        {
            OperatingMode.Production => "Production Mode",
            OperatingMode.Pilot => "Pilot Mode",
            _ => "Demo Mode",
        };
        var (bg, border, fg, tooltip) = mode switch
        {
            OperatingMode.Production => ("#35191B", "#9A393E", "#FFBFC1", "Production Mode: demo/fallback rows are blocked and factory readiness gates are enforced."),
            OperatingMode.Pilot => ("#372914", "#8C6C35", "#FFE0A7", "Pilot Mode: demo rows are hidden by default and simulated hardware must be clearly labeled."),
            _ => ("#14311D", "#377849", "#C6FFD0", "Demo Mode: sample data, demo role selector, and simulated sources are allowed."),
        };
        OperatingModeBanner.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(bg));
        OperatingModeBanner.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(border));
        OperatingModeBannerText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fg));
        OperatingModeBanner.ToolTip = tooltip;
    }

    private void OnLightingSettingsChanged()
    {
        Dispatcher.Invoke(() =>
        {
            LightingSettingsService.ApplyIntegrationBoundary();
            UpdateIntegrationBoundaryStatus();
        });
    }

    private void UpdateWorkflowPanel()
    {
        var state = WorkflowState.Instance;
        FooterStationText.Text = state.StationId;

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

    private void UpdateFooterStatus()
    {
        FooterStationText.Text = WorkflowState.Instance.StationId;

        try
        {
            var inspections = AoiDatabase.GetInspectionHistory(new Models.LogFilter());
            var images = AoiDatabase.GetImportedImages();
            var linkedImages = images.Count(image => File.Exists(image.VaultPath));
            var brokenLinks = images.Count - linkedImages;

            FooterRecordCountText.Text = inspections.Count.ToString("N0", CultureInfo.InvariantCulture);
            FooterImageLinkText.Text = $"{linkedImages:N0}/{images.Count:N0}";
            FooterIndexText.Text = brokenLinks == 0 ? "Images OK" : $"{brokenLinks:N0} Missing";
            FooterIndexText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(brokenLinks == 0 ? "#50F56E" : "#F27777"));

            if (File.Exists(AoiDatabase.DatabasePath))
            {
                var dbWriteTime = File.GetLastWriteTime(AoiDatabase.DatabasePath);
                FooterDbUpdatedText.Text = dbWriteTime.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
                FooterDbRevText.Text = "SQLite";
            }
            else
            {
                FooterDbUpdatedText.Text = "--";
                FooterDbRevText.Text = "missing";
            }
        }
        catch
        {
            FooterRecordCountText.Text = "--";
            FooterImageLinkText.Text = "--";
            FooterIndexText.Text = "Unavailable";
            FooterIndexText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F27777"));
            FooterDbUpdatedText.Text = "--";
            FooterDbRevText.Text = "local";
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

        UpdateIntegrationBoundaryStatus();
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
            Models.InspectionEngineStatus.MlModelReady => "#50F56E",
            Models.InspectionEngineStatus.MlModelMissing => "#E1A334",
            Models.InspectionEngineStatus.MlModelNotTested => "#E1A334",
            Models.InspectionEngineStatus.MlRuntimeError => "#F27777",
            Models.InspectionEngineStatus.MlInvalidLabelMap => "#F27777",
            Models.InspectionEngineStatus.MlUnsupportedOutputFormat => "#F27777",
            _ => "#E1A334",
        }));
    }

    private void UpdateCameraStatus()
    {
        var status = CameraSourceFactory.ActiveSource.ConnectionStatus;
        CameraStatusText.Text = status switch
        {
            CameraSourceStatus.Ready => "Connected",
            CameraSourceStatus.Simulated => "Simulated",
            CameraSourceStatus.Error => "Error",
            _ => "Not Connected",
        };
        CameraStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(status switch
        {
            CameraSourceStatus.Ready => "#50F56E",
            CameraSourceStatus.Simulated => "#E1A334",
            CameraSourceStatus.Error => "#F27777",
            _ => "#F27777",
        }));
    }

    private void UpdateIntegrationBoundaryStatus()
    {
        SetIntegrationStatus(LightingStatusText, IntegrationBoundaryRegistry.LightingController);
        SetIntegrationStatus(RobotStatusText, IntegrationBoundaryRegistry.RobotController);
        SetIntegrationStatus(
            MesStatusText,
            "MES / Traceability Boundary",
            CombineStatuses(
                IntegrationBoundaryRegistry.MesClient.Status,
                IntegrationBoundaryRegistry.TraceabilityUploader.Status),
            $"{IntegrationBoundaryRegistry.MesClient.StatusMessage} {IntegrationBoundaryRegistry.TraceabilityUploader.StatusMessage}");
        SetIntegrationStatus(EmergencyStopStatusText, IntegrationBoundaryRegistry.EmergencyStopMonitor);
    }

    private static void SetIntegrationStatus(TextBlock target, IIntegrationEndpoint endpoint)
        => SetIntegrationStatus(target, endpoint.Name, endpoint.Status, endpoint.StatusMessage);

    private static void SetIntegrationStatus(
        TextBlock target,
        string name,
        IntegrationConnectionStatus status,
        string statusMessage)
    {
        target.Text = ToStatusDisplay(status);
        target.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(status switch
        {
            IntegrationConnectionStatus.Ready => "#50F56E",
            IntegrationConnectionStatus.Simulated => "#E1A334",
            IntegrationConnectionStatus.Error => "#F27777",
            _ => "#F27777",
        }));
        target.ToolTip = $"{name}: {statusMessage}";
    }

    private static IntegrationConnectionStatus CombineStatuses(
        IntegrationConnectionStatus first,
        IntegrationConnectionStatus second)
    {
        if (first == IntegrationConnectionStatus.Error || second == IntegrationConnectionStatus.Error)
            return IntegrationConnectionStatus.Error;
        if (first == IntegrationConnectionStatus.Ready && second == IntegrationConnectionStatus.Ready)
            return IntegrationConnectionStatus.Ready;
        if (first == IntegrationConnectionStatus.Simulated || second == IntegrationConnectionStatus.Simulated)
            return IntegrationConnectionStatus.Simulated;
        return IntegrationConnectionStatus.NotConnected;
    }

    private static string ToStatusDisplay(IntegrationConnectionStatus status) => status switch
    {
        IntegrationConnectionStatus.Ready => "Ready",
        IntegrationConnectionStatus.Simulated => "Simulated",
        IntegrationConnectionStatus.Error => "Error",
        _ => "Not Connected",
    };

    private static AuthenticationMode ComboToAuthenticationMode(int selectedIndex) => selectedIndex switch
    {
        1 => AuthenticationMode.LocalUsers,
        2 => AuthenticationMode.MesAuthenticationBoundary,
        _ => AuthenticationMode.DemoLocalRoleSelector,
    };

    private static int AuthenticationModeToCombo(AuthenticationMode mode) => mode switch
    {
        AuthenticationMode.LocalUsers => 1,
        AuthenticationMode.MesAuthenticationBoundary => 2,
        _ => 0,
    };

    private static string PromptForPassword(string title, string label)
    {
        var password = new PasswordBox { MinWidth = 300, MinHeight = 26, Margin = new Thickness(0, 6, 0, 10) };
        var ok = new Button { Content = "OK", Width = 78, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "Cancel", Width = 78, IsCancel = true };
        var dialog = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Children =
                {
                    new TextBlock { Text = label, FontWeight = FontWeights.SemiBold },
                    password,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { ok, cancel },
                    },
                },
            },
        };
        ok.Click += (_, _) =>
        {
            dialog.DialogResult = true;
            dialog.Close();
        };
        return dialog.ShowDialog() == true ? password.Password : string.Empty;
    }
}
