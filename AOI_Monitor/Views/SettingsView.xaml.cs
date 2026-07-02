using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using AOI_Monitor.ViewModels;
using Microsoft.Win32;

namespace AOI_Monitor.Views;

public partial class SettingsView : UserControl, IAsyncNavigationPage
{
    private readonly MainViewModel _vm;
    private readonly ObservableCollection<ModelRegistryRow> _modelRegistryRows = new();
    private readonly ObservableCollection<LearnedVisualModelRow> _learnedVisualModelRows = new();
    private readonly ObservableCollection<ThresholdProfileRow> _thresholdProfileRows = new();
    private readonly ObservableCollection<ModelAcceptanceRunRow> _modelAcceptanceRows = new();
    private CancellationTokenSource? _modelAcceptanceCancellation;
    private CancellationTokenSource? _cameraAcceptanceCancellation;
    private CameraAcceptanceRun? _lastCameraAcceptanceRun;
    private CancellationTokenSource? _lightingAcceptanceCancellation;
    private LightingAcceptanceRun? _lastLightingAcceptanceRun;
    private CancellationTokenSource? _robotAcceptanceCancellation;
    private RobotAcceptanceRun? _lastRobotAcceptanceRun;
    private string? _pendingRestoreBackupPath;
    private bool _isKorean;

    public SettingsView(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        ModelRegistryGrid.ItemsSource = _modelRegistryRows;
        LearnedVisualModelGrid.ItemsSource = _learnedVisualModelRows;
        ThresholdProfilesGrid.ItemsSource = _thresholdProfileRows;
        ModelAcceptanceRunsGrid.ItemsSource = _modelAcceptanceRows;
        LoadUiPreferenceSelection();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        WorkflowState.Instance.StateChanged += OnWorkflowStateChanged;
        InspectionModelConfigurationService.ConfigurationChanged += OnInspectionConfigurationChanged;
        ModelRegistryService.RegistryChanged += OnModelRegistryChanged;
        LearnedVisualModelRegistryService.ActiveModelChanged += OnLearnedVisualModelRegistryChanged;
        CameraSourceSettingsService.SettingsChanged += OnCameraSourceSettingsChanged;
        LightingSettingsService.SettingsChanged += OnLightingSettingsChanged;
        MesIntegrationSettingsService.SettingsChanged += OnMesIntegrationSettingsChanged;
        CentralSyncSettingsService.SettingsChanged += OnCentralSyncSettingsChanged;
        OperatingModeSettingsService.SettingsChanged += OnOperatingModeSettingsChanged;
        ApplyLanguageVisuals();
        ApplyFontPreset();
        _ = RefreshAsync(CancellationToken.None);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        WorkflowState.Instance.StateChanged -= OnWorkflowStateChanged;
        InspectionModelConfigurationService.ConfigurationChanged -= OnInspectionConfigurationChanged;
        ModelRegistryService.RegistryChanged -= OnModelRegistryChanged;
        LearnedVisualModelRegistryService.ActiveModelChanged -= OnLearnedVisualModelRegistryChanged;
        CameraSourceSettingsService.SettingsChanged -= OnCameraSourceSettingsChanged;
        LightingSettingsService.SettingsChanged -= OnLightingSettingsChanged;
        MesIntegrationSettingsService.SettingsChanged -= OnMesIntegrationSettingsChanged;
        CentralSyncSettingsService.SettingsChanged -= OnCentralSyncSettingsChanged;
        OperatingModeSettingsService.SettingsChanged -= OnOperatingModeSettingsChanged;
    }

    private void OnWorkflowStateChanged() => UiDispatcher.InvokeIfAvailable(Dispatcher, RefreshWorkflowUi);
    private void OnInspectionConfigurationChanged() => UiDispatcher.InvokeIfAvailable(Dispatcher, () =>
    {
        RefreshInspectionConfigurationUi();
        _ = RefreshLearnedVisualModelRegistryUiAsync();
    });
    private void OnModelRegistryChanged() => UiDispatcher.InvokeIfAvailable(Dispatcher, () => _ = RefreshModelRegistryUiAsync());
    private void OnLearnedVisualModelRegistryChanged() => UiDispatcher.InvokeIfAvailable(Dispatcher, () => _ = RefreshLearnedVisualModelRegistryUiAsync());
    private void OnCameraSourceSettingsChanged() => UiDispatcher.InvokeIfAvailable(Dispatcher, RefreshCameraSourceUi);
    private void OnLightingSettingsChanged() => UiDispatcher.InvokeIfAvailable(Dispatcher, RefreshLightingUi);
    private void OnMesIntegrationSettingsChanged() => UiDispatcher.InvokeIfAvailable(Dispatcher, RefreshMesIntegrationUi);
    private void OnCentralSyncSettingsChanged() => UiDispatcher.InvokeIfAvailable(Dispatcher, RefreshCentralSyncUi);
    private void OnOperatingModeSettingsChanged() => UiDispatcher.InvokeIfAvailable(Dispatcher, RefreshOperatingModeUi);

    public async Task OnNavigatedToAsync(CancellationToken cancellationToken)
        => await RefreshAsync(cancellationToken);

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await Dispatcher.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            RefreshWorkflowUi();
            _ = RefreshThresholdProfilesUiAsync();
        });
    }

    public void CancelWork()
    {
        _modelAcceptanceCancellation?.Cancel();
        _cameraAcceptanceCancellation?.Cancel();
        _lightingAcceptanceCancellation?.Cancel();
        _robotAcceptanceCancellation?.Cancel();
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        var state = WorkflowState.Instance;
        var existingConfig = InspectionModelConfigurationService.Load();
        var newConfig = BuildConfigurationFromUi();
        var existingUiPreferences = UiPreferencesService.Load();
        var newUiPreferences = BuildUiPreferencesFromUi();
        var existingCamera = CameraSourceSettingsService.Load();
        var newCamera = BuildCameraSourceSettingsFromUi();
        var existingLighting = LightingSettingsService.Load();
        var newLighting = BuildLightingSettingsFromUi();
        var existingMes = MesIntegrationSettingsService.Load();
        var newMes = BuildMesIntegrationSettingsFromUi();
        var existingCentralSync = CentralSyncSettingsService.Load();
        var newCentralSync = BuildCentralSyncSettingsFromUi();
        var existingDeploymentProfile = DeploymentProfileSettingsService.Load();
        var newDeploymentProfile = ComboToDeploymentProfile(DeploymentProfileCombo.SelectedIndex);
        var existingOperatingMode = OperatingModeSettingsService.Load();
        var newOperatingMode = ComboToOperatingMode(OperatingModeCombo.SelectedIndex);
        var newStorageRoot = string.IsNullOrWhiteSpace(StorageRootText.Text)
            ? AoiDatabase.DefaultStorageRoot
            : StorageRootText.Text.Trim();
        var storageRootChanged = !string.Equals(
            Path.GetFullPath(AoiDatabase.StorageRoot),
            Path.GetFullPath(newStorageRoot),
            StringComparison.OrdinalIgnoreCase);
        var modelConfigChanged =
            !string.Equals(existingConfig.ModelFilePath, newConfig.ModelFilePath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(existingConfig.ModelVersion, newConfig.ModelVersion, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(existingConfig.LabelMapPath, newConfig.LabelMapPath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(existingConfig.SelectedEngineKey, newConfig.SelectedEngineKey, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(existingConfig.InputTensorName, newConfig.InputTensorName, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(existingConfig.OutputTensorName, newConfig.OutputTensorName, StringComparison.OrdinalIgnoreCase) ||
            existingConfig.InputImageWidth != newConfig.InputImageWidth ||
            existingConfig.InputImageHeight != newConfig.InputImageHeight;
        var cameraConfigChanged =
            !string.Equals(existingCamera.SourceKey, newCamera.SourceKey, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(existingCamera.TopFolder, newCamera.TopFolder, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(existingCamera.SideFolder, newCamera.SideFolder, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(existingCamera.BottomFolder, newCamera.BottomFolder, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(existingCamera.TopDeviceId, newCamera.TopDeviceId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(existingCamera.SideDeviceId, newCamera.SideDeviceId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(existingCamera.BottomDeviceId, newCamera.BottomDeviceId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(existingCamera.AdapterFolder, newCamera.AdapterFolder, StringComparison.OrdinalIgnoreCase) ||
            existingCamera.AcquisitionMode != newCamera.AcquisitionMode ||
            Math.Abs(existingCamera.ExposureMs - newCamera.ExposureMs) > 0.0001 ||
            Math.Abs(existingCamera.Gain - newCamera.Gain) > 0.0001 ||
            existingCamera.TriggerTimeoutMs != newCamera.TriggerTimeoutMs ||
            existingCamera.FrameTimeoutMs != newCamera.FrameTimeoutMs ||
            !string.Equals(existingCamera.BoardModel, newCamera.BoardModel, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(existingCamera.LotId, newCamera.LotId, StringComparison.OrdinalIgnoreCase);
        var mesConfigChanged =
            existingMes.Mode != newMes.Mode ||
            !string.Equals(existingMes.MockEndpointUrl, newMes.MockEndpointUrl, StringComparison.OrdinalIgnoreCase) ||
            existingMes.UploadTimeoutSeconds != newMes.UploadTimeoutSeconds ||
            existingMes.AutoUploadEnabled != newMes.AutoUploadEnabled ||
            !string.Equals(existingMes.BaseUrl, newMes.BaseUrl, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(existingMes.UploadResultPath, newMes.UploadResultPath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(existingMes.UploadImagePath, newMes.UploadImagePath, StringComparison.OrdinalIgnoreCase) ||
            existingMes.AuthMode != newMes.AuthMode ||
            !string.Equals(existingMes.ApiKeyHeaderName, newMes.ApiKeyHeaderName, StringComparison.Ordinal) ||
            !string.Equals(existingMes.ApiKey, newMes.ApiKey, StringComparison.Ordinal) ||
            !string.Equals(existingMes.BearerToken, newMes.BearerToken, StringComparison.Ordinal) ||
            !string.Equals(existingMes.Username, newMes.Username, StringComparison.Ordinal) ||
            !string.Equals(existingMes.Password, newMes.Password, StringComparison.Ordinal) ||
            existingMes.MaxRetryCount != newMes.MaxRetryCount ||
            existingMes.RetryBackoffMs != newMes.RetryBackoffMs;
        var centralSyncConfigChanged =
            existingCentralSync.Mode != newCentralSync.Mode ||
            !string.Equals(existingCentralSync.EndpointOrFolder, newCentralSync.EndpointOrFolder, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(existingCentralSync.StationId, newCentralSync.StationId, StringComparison.OrdinalIgnoreCase) ||
            existingCentralSync.SyncIntervalSeconds != newCentralSync.SyncIntervalSeconds ||
            existingCentralSync.IncludeImages != newCentralSync.IncludeImages ||
            existingCentralSync.RedactOperatorId != newCentralSync.RedactOperatorId ||
            existingCentralSync.RedactImagePaths != newCentralSync.RedactImagePaths ||
            existingCentralSync.RedactEndpointInExports != newCentralSync.RedactEndpointInExports ||
            existingCentralSync.MaxRetryCount != newCentralSync.MaxRetryCount ||
            !string.Equals(existingCentralSync.SharedSecret, newCentralSync.SharedSecret, StringComparison.Ordinal);
        var lightingConfigChanged =
            !string.Equals(existingLighting.Mode, newLighting.Mode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(existingLighting.TcpHost, newLighting.TcpHost, StringComparison.OrdinalIgnoreCase) ||
            existingLighting.TcpPort != newLighting.TcpPort ||
            !string.Equals(existingLighting.SerialPortName, newLighting.SerialPortName, StringComparison.OrdinalIgnoreCase) ||
            existingLighting.BaudRate != newLighting.BaudRate ||
            !string.Equals(existingLighting.TopProgram, newLighting.TopProgram, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(existingLighting.SideProgram, newLighting.SideProgram, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(existingLighting.BottomProgram, newLighting.BottomProgram, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(existingLighting.CommandTemplate, newLighting.CommandTemplate, StringComparison.Ordinal) ||
            existingLighting.ResponseTimeoutMs != newLighting.ResponseTimeoutMs;
        var deploymentProfileChanged = existingDeploymentProfile != newDeploymentProfile;
        var operatingModeChanged = existingOperatingMode != newOperatingMode;
        var uiPreferencesChanged = !UiPreferencesService.AreEquivalent(existingUiPreferences, newUiPreferences);
        var thresholdChanged =
            ComboToPriority(DetectionPriorityCombo.SelectedIndex) != state.DetectionPriority ||
            Math.Abs(existingConfig.ConfidenceThreshold - newConfig.ConfidenceThreshold) > 0.0001;

        if ((storageRootChanged || modelConfigChanged || cameraConfigChanged || lightingConfigChanged || mesConfigChanged || centralSyncConfigChanged || deploymentProfileChanged || operatingModeChanged || uiPreferencesChanged) && !Authorize(RoleAuthorization.CanManageSettings, "Changing database/vault/model paths, selected model engine, deployment target, operating mode, display assets, camera source, lighting sync, MES integration, or central sync settings"))
            return;

        if (thresholdChanged && !Authorize(RoleAuthorization.CanChangeThresholds, "Changing inspection thresholds or detection priority"))
            return;

        UiPreferencesService.Save(newUiPreferences);
        UiPreferencesService.ApplyToApplication(newUiPreferences, resizeWindowToPreset: true);
        ApplyLanguageVisuals();
        if (!ApplyStorageRoot(newStorageRoot, storageRootChanged))
            return;

        SaveInspectionConfiguration(newConfig);
        SaveCameraSourceSettings(newCamera);
        SaveLightingSettings(newLighting);
        SaveMesIntegrationSettings(newMes);
        SaveCentralSyncSettings(newCentralSync);
        if (deploymentProfileChanged)
            DeploymentProfileSettingsService.Save(newDeploymentProfile);
        if (operatingModeChanged)
            OperatingModeSettingsService.Save(newOperatingMode, WorkflowState.Instance.OperatorWithRole);

        if (!state.TrySetDetectionPriority(ComboToPriority(DetectionPriorityCombo.SelectedIndex), out var message))
        {
            MessageBox.Show(BuildSettingsAppliedMessage(message), "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
            RefreshWorkflowUi();
            return;
        }

        RefreshWorkflowUi();
        MessageBox.Show(BuildSettingsAppliedMessage(message), "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        LoadUiPreferenceSelection();
        RefreshWorkflowUi();
        ApplyLanguageVisuals();
        ApplyFontPreset();
        MessageBox.Show(
            TextFor("Unapplied settings were discarded.", "\uC801\uC6A9\uD558\uC9C0 \uC54A\uC740 \uC124\uC815\uC744 \uCDE8\uC18C\uD588\uC2B5\uB2C8\uB2E4."),
            "AOI Monitor",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Resetting local settings"))
            return;

        LangCombo.SelectedIndex = 0;
        FontCombo.SelectedIndex = 1;
        ResolutionCombo.SelectedIndex = 0;
        ThemeCombo.SelectedIndex = 0;
        ConsoleTitleText.Text = UiPreferenceDefaults.ConsoleTitle;
        StationNameText.Text = UiPreferenceDefaults.StationDisplayName;
        StationSubtitleText.Text = UiPreferenceDefaults.StationSubtitle;
        AccentColorText.Text = UiPreferenceDefaults.AccentColor;
        BrandLogoPathText.Text = string.Empty;
        DetectionPriorityCombo.SelectedIndex = 0;
        DeploymentProfileCombo.SelectedIndex = 0;
        OperatingModeCombo.SelectedIndex = 0;
        InspectionEngineCombo.SelectedIndex = 0;
        ModelPathText.Text = string.Empty;
        StorageRootText.Text = AoiDatabase.DefaultStorageRoot;
        ModelVersionText.Text = "UNCONFIGURED";
        LabelMapPathText.Text = string.Empty;
        ConfidenceThresholdText.Text = "0.65";
        InputWidthText.Text = "640";
        InputHeightText.Text = "640";
        InputTensorNameText.Text = string.Empty;
        OutputTensorNameText.Text = string.Empty;
        StorageRootSettingsService.ResetStorageRoot();
        AoiDatabase.ConfigureStorageRoot(AoiDatabase.DefaultStorageRoot);
        AoiDatabase.Initialize();
        InspectionModelConfigurationService.Save(new InspectionModelConfiguration());
        CameraSourceSettingsService.Save(new CameraSourceSettings());
        CameraSourceSettingsService.ApplyActiveSource();
        MesModeCombo.SelectedIndex = 0;
        CameraTopDeviceIdText.Text = string.Empty;
        CameraSideDeviceIdText.Text = string.Empty;
        CameraBottomDeviceIdText.Text = string.Empty;
        CameraAdapterFolderText.Text = string.Empty;
        CameraAcquisitionModeCombo.SelectedIndex = 0;
        CameraExposureMsText.Text = "5.0";
        CameraGainText.Text = "1.0";
        CameraTriggerTimeoutMsText.Text = "250";
        CameraFrameTimeoutMsText.Text = "1000";
        LightingModeCombo.SelectedIndex = 0;
        LightingTcpHostText.Text = string.Empty;
        LightingTcpPortText.Text = "5025";
        LightingSerialPortText.Text = string.Empty;
        LightingBaudRateText.Text = "9600";
        LightingTopProgramText.Text = "TOP";
        LightingSideProgramText.Text = "SIDE";
        LightingBottomProgramText.Text = "BOTTOM";
        LightingCommandTemplateText.Text = "SET {view} {program}\\n";
        LightingTimeoutMsText.Text = "500";
        LightingSettingsService.Save(new LightingSettings());
        LightingSettingsService.ApplyIntegrationBoundary();
        MesEndpointText.Text = string.Empty;
        MesRestBaseUrlText.Text = string.Empty;
        MesResultPathText.Text = "/api/aoi/results";
        MesImagePathText.Text = "/api/aoi/images";
        MesAuthModeCombo.SelectedIndex = 0;
        MesApiKeyHeaderText.Text = "X-API-Key";
        MesApiKeyBox.Password = string.Empty;
        MesBearerTokenBox.Password = string.Empty;
        MesUsernameText.Text = string.Empty;
        MesPasswordBox.Password = string.Empty;
        MesTimeoutText.Text = "10";
        MesMaxRetryText.Text = "2";
        MesRetryBackoffText.Text = "500";
        MesAutoUploadCheck.IsChecked = false;
        MesIntegrationSettingsService.Save(new MesIntegrationSettings());
        CentralSyncModeCombo.SelectedIndex = 0;
        CentralSyncEndpointText.Text = string.Empty;
        CentralSyncStationIdText.Text = Environment.MachineName;
        CentralSyncIntervalText.Text = "300";
        CentralSyncMaxRetryText.Text = "5";
        CentralSyncSecretBox.Password = string.Empty;
        CentralSyncIncludeImagesCheck.IsChecked = false;
        CentralSyncRedactOperatorCheck.IsChecked = true;
        CentralSyncRedactImagePathsCheck.IsChecked = true;
        CentralSyncRedactEndpointCheck.IsChecked = true;
        CentralSyncSettingsService.Save(new CentralSyncSettings());
        DeploymentProfileSettingsService.Save(DeploymentProfile.Stage1ImageValidation);
        OperatingModeSettingsService.Save(OperatingMode.Demo, WorkflowState.Instance.OperatorWithRole);

        SaveUiPreferenceSelection();
        ApplyLanguageVisuals();

        var state = WorkflowState.Instance;
        if (!state.TrySetDetectionPriority(Models.DetectionPriority.MinimizeFalsePositives, out var message))
        {
            MessageBox.Show($"Display settings reset.\n{message}", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
            RefreshWorkflowUi();
            return;
        }

        RefreshWorkflowUi();
        MessageBox.Show("Settings reset to defaults.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnRunSetupWizardClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Running setup wizard"))
            return;

        var wizard = new FirstRunWizardView
        {
            Owner = Window.GetWindow(this),
        };
        wizard.ShowDialog();
        RefreshWorkflowUi();
    }

    private void OnOpenInstallNotesClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Opening installation notes"))
            return;

        _vm.CurrentPage = "install";
    }

    private void OnOpenGuideClick(object sender, RoutedEventArgs e)
        => _vm.CurrentPage = "guide";

    private void OnExportDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Exporting diagnostics report"))
            return;

        try
        {
            var report = SystemDiagnosticService.RunDiagnostics();
            var export = SystemDiagnosticService.ExportReport(report);
            WorkflowState.Instance.AddEvent("DIAGNOSTICS", $"Diagnostics report exported: {Path.GetFileName(export.JsonPath)}");
            MessageBox.Show(
                $"Diagnostics exported.\n\nJSON: {export.JsonPath}\nHTML: {export.HtmlPath}\nText: {export.TextPath}",
                "AOI Monitor Diagnostics",
                MessageBoxButton.OK,
                report.ErrorCount == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show($"Diagnostics export failed:\n{ex.Message}", "AOI Monitor Diagnostics", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnBackupConfigurationClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Backing up workstation configuration"))
            return;

        var dialog = new OpenFolderDialog
        {
            Title = "Select configuration backup folder",
            InitialDirectory = AoiDatabase.StorageRoot,
        };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var result = ConfigurationBackupService.Export(dialog.FolderName, WorkflowState.Instance.OperatorWithRole);
            MessageBox.Show(
                $"Configuration backup exported.\n\n{result.BackupPath}\n\nRaw/customer images are excluded by default.",
                "AOI Monitor Configuration Backup",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            MessageBox.Show($"Configuration backup failed:\n{ex.Message}", "AOI Monitor Configuration Backup", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnRestoreConfigurationPreviewClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Previewing and restoring workstation configuration"))
            return;

        var dialog = new OpenFileDialog
        {
            Title = "Select AOI configuration backup",
            Filter = "AOI configuration backup (*.json)|*.json|All files (*.*)|*.*",
            InitialDirectory = AoiDatabase.StorageRoot,
        };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var preview = ConfigurationBackupService.Preview(dialog.FileName);
            var details = BuildRestorePreviewMessage(preview);
            _pendingRestoreBackupPath = preview.IsCompatible ? dialog.FileName : null;
            ApplyRestoreBtn.IsEnabled = preview.IsCompatible && RoleAuthorization.CanManageSettings(WorkflowState.Instance.CurrentRole);
            if (!preview.IsCompatible)
            {
                MessageBox.Show(details, "AOI Monitor Restore Preview", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            MessageBox.Show(
                $"{details}\n\nUse Apply Restore to apply this backup.",
                "AOI Monitor Restore Preview",
                MessageBoxButton.OK,
                preview.Warnings.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            _pendingRestoreBackupPath = null;
            ApplyRestoreBtn.IsEnabled = false;
            MessageBox.Show($"Configuration restore preview failed:\n{ex.Message}", "AOI Monitor Restore Preview", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnApplyRestoreClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Applying workstation configuration restore"))
            return;
        if (string.IsNullOrWhiteSpace(_pendingRestoreBackupPath) || !File.Exists(_pendingRestoreBackupPath))
        {
            MessageBox.Show("Run Restore Configuration Preview and select a compatible backup before applying restore.", "AOI Monitor Restore", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            $"Apply configuration restore from:\n{_pendingRestoreBackupPath}\n\nThis will update settings, model registry metadata, threshold profiles, recipe revisions, and deployment profile. Restart the app before production use.",
            "Apply Configuration Restore",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
            return;

        try
        {
            var preview = ConfigurationBackupService.Import(_pendingRestoreBackupPath, WorkflowState.Instance.OperatorWithRole);
            if (!preview.IsCompatible)
            {
                MessageBox.Show(BuildRestorePreviewMessage(preview), "AOI Monitor Restore", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _pendingRestoreBackupPath = null;
            ApplyRestoreBtn.IsEnabled = false;
            RefreshWorkflowUi();
            _ = RefreshThresholdProfilesUiAsync();
            MessageBox.Show("Configuration restored. Restart the app before production use so all integration boundaries reload cleanly.", "AOI Monitor Restore", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            MessageBox.Show($"Configuration restore failed:\n{ex.Message}", "AOI Monitor Restore", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnRollbackRestoreClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Rolling back workstation configuration restore"))
            return;

        var info = ConfigurationBackupService.GetLastRollbackInfo();
        if (info is null)
        {
            MessageBox.Show("No restore rollback package is available.", "AOI Monitor Restore Rollback", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            $"Rollback the last configuration restore using:\n{info.RollbackBackupPath}\n\nThis re-applies the configuration backup captured immediately before the last restore.",
            "Rollback Configuration Restore",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
            return;

        try
        {
            var preview = ConfigurationBackupService.RollbackLastRestore(WorkflowState.Instance.OperatorWithRole);
            if (!preview.IsCompatible)
            {
                MessageBox.Show(BuildRestorePreviewMessage(preview), "AOI Monitor Restore Rollback", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _pendingRestoreBackupPath = null;
            ApplyRestoreBtn.IsEnabled = false;
            RefreshWorkflowUi();
            _ = RefreshThresholdProfilesUiAsync();
            MessageBox.Show("Configuration restore rolled back. Restart the app before production use so all integration boundaries reload cleanly.", "AOI Monitor Restore Rollback", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            MessageBox.Show($"Configuration restore rollback failed:\n{ex.Message}", "AOI Monitor Restore Rollback", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnExportSupportBundleClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Exporting support bundle"))
            return;

        if (SupportBundleIncludeModelsCheck.IsChecked == true)
        {
            var confirm = MessageBox.Show(
                "Including model files may expose customer or vendor model IP. Continue with model files included?",
                "Export Support Bundle",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes)
                return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = "Select support bundle export folder",
            InitialDirectory = AoiDatabase.StorageRoot,
        };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var result = SupportBundleService.Export(new SupportBundleOptions
            {
                OutputRoot = dialog.FolderName,
                IncludeModelFiles = SupportBundleIncludeModelsCheck.IsChecked == true,
                RedactCustomerImagePaths = SupportBundleRedactPathsCheck.IsChecked != false,
                RedactStorageRoot = SupportBundleRedactPathsCheck.IsChecked != false,
            }, WorkflowState.Instance.OperatorWithRole);
            WorkflowState.Instance.AddEvent("SUPPORT_BUNDLE", $"Support bundle exported: {Path.GetFileName(result.ZipPath)}.");
            MessageBox.Show(
                $"Support bundle exported.\n\n{result.ZipPath}\n\nRaw customer images and secrets are excluded/redacted by default.",
                "Export Support Bundle",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            MessageBox.Show($"Support bundle export failed:\n{ex.Message}", "Export Support Bundle", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnStartTrainingClick(object sender, RoutedEventArgs e)
    {
        var state = WorkflowState.Instance;
        if (state.Training.IsRunning)
        {
            MessageBox.Show("Training set export preparation is already active.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        state.StartTraining();
    }

    private void OnRunEpochClick(object sender, RoutedEventArgs e)
    {
        var state = WorkflowState.Instance;
        if (!state.Training.IsRunning)
        {
            MessageBox.Show("Prepare the training set export before validating the list.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var validation = state.DetectionPriority switch
        {
            Models.DetectionPriority.MinimizeFalsePositives => 95.0,
            Models.DetectionPriority.Balanced => 92.5,
            Models.DetectionPriority.MaximizeDefectRecall => 90.5,
            _ => 92.5,
        };

        // Add small deterministic drift to avoid a static value across epochs.
        validation = Math.Max(80, validation - (state.Training.EpochsCompleted % 4) * 0.4);
        state.CompleteTrainingEpoch(validation);
    }

    private void OnStopTrainingClick(object sender, RoutedEventArgs e)
    {
        var state = WorkflowState.Instance;
        if (!state.Training.IsRunning)
        {
            MessageBox.Show("Training set export preparation is already stopped.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        state.StopTraining();
    }

    private void OnBrowseModelClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Changing model path"))
            return;

        var dialog = new OpenFileDialog
        {
            Title = "Select offline ONNX model",
            Filter = "ONNX model|*.onnx|All files|*.*",
        };

        if (dialog.ShowDialog() != true)
            return;

        ModelPathText.Text = dialog.FileName;
        if (string.IsNullOrWhiteSpace(ModelVersionText.Text) || ModelVersionText.Text == "UNCONFIGURED")
            ModelVersionText.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
        InspectionEngineCombo.SelectedIndex = 1;
        RefreshInspectionConfigurationUi(BuildConfigurationFromUi());
    }

    private void OnBrowseStorageRootClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Changing local database and image-vault storage path"))
            return;

        var dialog = new OpenFolderDialog
        {
            Title = "Select local AOI storage root",
            Multiselect = false,
        };

        if (Directory.Exists(StorageRootText.Text))
            dialog.InitialDirectory = StorageRootText.Text;

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
            StorageRootText.Text = dialog.FolderName;
    }

    private void OnBrowseBrandLogoClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Changing the client-visible logo asset"))
            return;

        var dialog = new OpenFileDialog
        {
            Title = "Select client logo image",
            Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp|All files|*.*",
        };

        if (dialog.ShowDialog() == true)
            BrandLogoPathText.Text = dialog.FileName;
    }

    private void OnBrowseLabelMapClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Changing label map path"))
            return;

        var dialog = new OpenFileDialog
        {
            Title = "Select inspection label map",
            Filter = "Label map|*.json;*.txt;*.csv|All files|*.*",
        };

        if (dialog.ShowDialog() != true)
            return;

        LabelMapPathText.Text = dialog.FileName;
        RefreshInspectionConfigurationUi(BuildConfigurationFromUi());
    }

    private void OnImportTaxonomyClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanChangeThresholds, "Importing defect taxonomy CSV"))
            return;

        var dialog = new OpenFileDialog
        {
            Title = "Import defect taxonomy CSV",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var taxonomy = DefectTaxonomyService.ImportCsv(dialog.FileName, WorkflowState.Instance.CurrentRole, WorkflowState.Instance.OperatorWithRole);
            WorkflowState.Instance.AddEvent("DEFECT_TAXONOMY", $"Imported active defect taxonomy {taxonomy.Taxonomy.Name} with {taxonomy.Entries.Count} class(es).");
            RefreshDefectTaxonomyUi();
            MessageBox.Show("Defect taxonomy imported and activated.", "Defect Taxonomy", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show($"Defect taxonomy import failed:\n{ex.Message}", "Defect Taxonomy", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnExportTaxonomyClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanChangeThresholds, "Exporting defect taxonomy CSV"))
            return;

        var dialog = new SaveFileDialog
        {
            Title = "Export defect taxonomy CSV",
            FileName = $"defect_taxonomy_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var path = DefectTaxonomyService.ExportCsv(dialog.FileName);
            WorkflowState.Instance.AddEvent("DEFECT_TAXONOMY", $"Exported active defect taxonomy CSV: {Path.GetFileName(path)}.");
            MessageBox.Show($"Defect taxonomy exported.\n\n{path}", "Defect Taxonomy", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show($"Defect taxonomy export failed:\n{ex.Message}", "Defect Taxonomy", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnRegisterModelClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Registering local ONNX model"))
            return;

        try
        {
            var request = BuildModelRegistrationRequestFromUi();
            var entry = ModelRegistryService.Register(request);
            _ = RefreshModelRegistryUiAsync();
            ModelRegistryGrid.SelectedItem = _modelRegistryRows.FirstOrDefault(row => row.ModelId == entry.ModelId);
            WorkflowState.Instance.AddEvent("MODEL_REGISTRY", $"Registered model {entry.ModelId}; version {entry.Version}; status {entry.ValidationStatus}.");
            MessageBox.Show(
                $"Model registered.\n\nModel ID: {entry.ModelId}\nSHA-256: {entry.Sha256}",
                "Model Registry",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or JsonException)
        {
            MessageBox.Show($"Model registration failed:\n{ex.Message}", "Model Registry", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnValidateRegisteredModelClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanTestModelConfiguration, "Validating registered model"))
            return;

        if (ModelRegistryGrid.SelectedItem is not ModelRegistryRow row)
        {
            MessageBox.Show("Select a registered model first.", "Model Registry", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var result = ModelLifecycleService.ValidateRuntime(row.ModelId, WorkflowState.Instance.CurrentRole, WorkflowState.Instance.OperatorWithRole);
            RefreshInspectionConfigurationUi(InspectionModelConfigurationService.Load());
            _ = RefreshModelRegistryUiAsync();
            WorkflowState.Instance.AddEvent("MODEL_CHECK", $"Registered model validation: {row.ModelId}; {result.DisplayStatus}. {result.Message}");
            MessageBox.Show(
                $"{result.DisplayStatus}\n\n{result.Message}",
                "Registered Model Validation",
                MessageBoxButton.OK,
                result.Status == ModelConfigurationTestStatus.Ready ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show($"Registered model validation failed:\n{ex.Message}", "Model Registry", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnSetActiveModelClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Setting active ONNX model"))
            return;

        if (ModelRegistryGrid.SelectedItem is not ModelRegistryRow row)
        {
            MessageBox.Show("Select a registered model first.", "Model Registry", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            if (!ModelRegistryService.SetActiveModel(row.ModelId))
            {
                MessageBox.Show("The selected model registry entry could not be found.", "Model Registry", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(ex.Message, "Model Registry", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        RefreshInspectionConfigurationUi(InspectionModelConfigurationService.Load());
        _ = RefreshModelRegistryUiAsync();
        WorkflowState.Instance.AddEvent("MODEL_DEPLOYMENT", $"Active model set to {row.ModelId}. ONNX inference remains gated by validation status.");
        MessageBox.Show(
            $"Active model set.\n\nModel ID: {row.ModelId}\nRun Validate before using it for accepted ONNX inference.",
            "Model Registry",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void OnSetActiveLearnedVisualModelClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanSetActiveLearnedVisualModel, "Setting active learned visual model"))
            return;

        if (LearnedVisualModelGrid.SelectedItem is not LearnedVisualModelRow row)
        {
            MessageBox.Show("Select a learned PCB visual model first.", "Learned Visual Model", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var active = LearnedVisualModelRegistryService.SetActiveLearnedVisualModel(
                row.ModelId,
                WorkflowState.Instance.CurrentRole,
                WorkflowState.Instance.OperatorWithRole);
            RefreshInspectionConfigurationUi(InspectionModelConfigurationService.Load());
            _ = RefreshLearnedVisualModelRegistryUiAsync();
            WorkflowState.Instance.AddEvent("MODEL_DEPLOYMENT", $"Active learned visual model set to {active.ModelId}. Image-only Stage 1 evidence; not live camera validation.");
            MessageBox.Show(
                $"Active learned visual model set.\n\nModel ID: {active.ModelId}\nTraining project: {active.ProjectName}\n\nImage-only Stage 1 learning; not live camera validation.",
                "Learned Visual Model",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or IOException)
        {
            MessageBox.Show(ex.Message, "Learned Visual Model", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void OnRunModelAcceptanceClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanTestModelConfiguration, "Running model acceptance"))
            return;

        if (_modelAcceptanceCancellation is not null)
            return;

        var datasetDialog = new OpenFolderDialog { Title = "Select model acceptance validation dataset folder", Multiselect = false };
        if (datasetDialog.ShowDialog() != true || string.IsNullOrWhiteSpace(datasetDialog.FolderName))
            return;

        var csvDialog = new OpenFileDialog
        {
            Title = "Select formal ground-truth CSV",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
        };
        if (csvDialog.ShowDialog() != true)
            return;

        try
        {
            _modelAcceptanceCancellation = new CancellationTokenSource();
            RefreshRoleControls();
            ModelAcceptanceProgressBar.IsIndeterminate = true;
            ModelAcceptanceProgressText.Text = "Starting model acceptance...";
            ModelCheckMessageText.Text = "Model acceptance running...";
            var datasetFolder = datasetDialog.FolderName;
            var csvPath = csvDialog.FileName;
            var operatorId = WorkflowState.Instance.OperatorWithRole;
            var progress = new Progress<string>(message =>
            {
                ModelAcceptanceProgressText.Text = message;
                ModelCheckMessageText.Text = message;
            });
            var token = _modelAcceptanceCancellation.Token;
            var run = await Task.Run(() => ModelAcceptanceService.RunAcceptance(datasetFolder, csvPath, operatorId: operatorId, role: WorkflowState.Instance.CurrentRole, progress: progress, cancellationToken: token), token);
            RefreshModelAcceptanceRunsUi();
            WorkflowState.Instance.AddEvent("MODEL_ACCEPTANCE", $"Model acceptance {run.Status}: model={run.ModelId}; run={run.Id}; dataset={run.DatasetName}.");
            ModelAcceptanceProgressBar.IsIndeterminate = false;
            ModelAcceptanceProgressBar.Value = 100;
            ModelAcceptanceProgressText.Text = $"Model acceptance {run.Status}: run {run.Id}.";
            MessageBox.Show($"Model acceptance {run.Status}.\n\nRun ID: {run.Id}\n{string.Join(Environment.NewLine, run.Messages.Take(5))}", "Model Acceptance", MessageBoxButton.OK, run.Status == "PASS" ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (OperationCanceledException)
        {
            WorkflowState.Instance.AddEvent("MODEL_ACCEPTANCE", "Model acceptance canceled by user before acceptance evidence was recorded.");
            ModelCheckMessageText.Text = "Model acceptance canceled.";
            ModelAcceptanceProgressText.Text = "Model acceptance canceled.";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or InvalidDataException)
        {
            MessageBox.Show($"Model acceptance failed:\n{ex.Message}", "Model Acceptance", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _modelAcceptanceCancellation?.Dispose();
            _modelAcceptanceCancellation = null;
            ModelAcceptanceProgressBar.IsIndeterminate = false;
            RefreshRoleControls();
        }
    }

    private void OnCancelModelAcceptanceClick(object sender, RoutedEventArgs e)
    {
        _modelAcceptanceCancellation?.Cancel();
        ModelAcceptanceProgressText.Text = "Cancel requested. Finishing current validation step...";
        ModelCheckMessageText.Text = "Cancel requested. Finishing current validation step...";
    }

    private void OnCreateModelReleasePackageClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanChangeThresholds, "Creating model release packages"))
            return;

        var latest = AoiDatabase.GetLatestModelAcceptanceRun();
        if (latest is null)
        {
            MessageBox.Show("Run model acceptance before creating a release package.", "Model Release", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var outputDialog = new OpenFolderDialog { Title = "Select model release package output folder", Multiselect = false };
        if (outputDialog.ShowDialog() != true || string.IsNullOrWhiteSpace(outputDialog.FolderName))
            return;

        try
        {
            var package = ModelAcceptanceService.CreateReleasePackage(latest.Id, outputDialog.FolderName, WorkflowState.Instance.OperatorWithRole);
            RefreshModelAcceptanceRunsUi();
            _ = RefreshModelRegistryUiAsync();
            WorkflowState.Instance.AddEvent("MODEL_RELEASE_PACKAGE", $"Model release package created: model={package.ModelId}; status={package.Status}.");
            MessageBox.Show($"Model release package created:\n{package.PackagePath}", "Model Release", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            MessageBox.Show($"Model release package failed:\n{ex.Message}", "Model Release", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnPromoteProductionCandidateClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanChangeThresholds, "Promoting production candidate models"))
            return;

        var latest = AoiDatabase.GetLatestModelAcceptanceRun();
        if (latest is null)
        {
            MessageBox.Show("Run model acceptance before promoting a production candidate.", "Model Acceptance", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            $"Promote model acceptance run {latest.Id} for {latest.ModelId} to production candidate?\n\nThis remains limited to the validation dataset and does not prove universal production accuracy.",
            "Confirm Production Candidate",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
            return;

        try
        {
            ModelAcceptanceService.PromoteToProductionCandidate(latest.Id, WorkflowState.Instance.CurrentRole, WorkflowState.Instance.OperatorWithRole);
            RefreshModelAcceptanceRunsUi();
            _ = RefreshModelRegistryUiAsync();
            WorkflowState.Instance.AddEvent("MODEL_PRODUCTION_CANDIDATE", $"Promoted model acceptance run {latest.Id} for {latest.ModelId}.");
            MessageBox.Show("Model acceptance run promoted to production candidate.", "Model Acceptance", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            MessageBox.Show(ex.Message, "Model Acceptance", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnViewModelAcceptanceRunsClick(object sender, RoutedEventArgs e)
        => RefreshModelAcceptanceRunsUi(showMessage: true);

    private void OnDeployModelClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Deploying model lifecycle state"))
            return;

        if (ModelRegistryGrid.SelectedItem is not ModelRegistryRow row)
        {
            MessageBox.Show("Select a registered model first.", "Model Lifecycle", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var model = ModelRegistryService.GetModel(row.ModelId);
            var latestAcceptance = AoiDatabase.GetLatestModelAcceptanceRun(row.ModelId);
            if (model is null)
            {
                MessageBox.Show("The selected model registry entry could not be found.", "Model Lifecycle", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var confirm = MessageBox.Show(
                $"Deploy model {model.DisplayName} v{model.Version}?\n\nSHA-256: {model.Sha256}\nAcceptance run ID: {latestAcceptance?.Id.ToString(CultureInfo.InvariantCulture) ?? "none"}\nAcceptance status: {latestAcceptance?.Status ?? "none"}\n\nFull automation readiness still requires PASS acceptance with no active waiver and the remaining factory evidence.",
                "Confirm Deploy Model",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes)
                return;

            ModelLifecycleService.DeployModel(row.ModelId, WorkflowState.Instance.CurrentRole, WorkflowState.Instance.OperatorWithRole);
            RefreshInspectionConfigurationUi(InspectionModelConfigurationService.Load());
            _ = RefreshModelRegistryUiAsync();
            WorkflowState.Instance.AddEvent("MODEL_DEPLOYMENT", $"Deployed model {row.ModelId} through lifecycle approval.");
            MessageBox.Show("Model deployed. Full automation readiness still depends on PASS acceptance and other factory evidence.", "Model Lifecycle", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            MessageBox.Show(ex.Message, "Model Lifecycle", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnWaiveDeployModelClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Deploying model with Admin waiver"))
            return;

        if (ModelRegistryGrid.SelectedItem is not ModelRegistryRow row)
        {
            MessageBox.Show("Select a registered model first.", "Model Lifecycle", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var reason = PromptForText("Deployment Waiver", "Admin waiver reason");
        if (string.IsNullOrWhiteSpace(reason))
            return;
        var expiryText = PromptForText("Deployment Waiver Expiry", "Expiry date/time UTC, for example 2026-07-21T00:00:00Z");
        if (!DateTime.TryParse(
                expiryText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var waiverExpiresAtUtc))
        {
            MessageBox.Show("A valid future waiver expiry date is required.", "Model Lifecycle", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var riskClassification = PromptForText("Deployment Waiver Risk", "Risk classification, for example Low / Medium / High / Safety Critical");
        if (string.IsNullOrWhiteSpace(riskClassification))
            return;

        try
        {
            ModelLifecycleService.DeployModel(row.ModelId, WorkflowState.Instance.CurrentRole, WorkflowState.Instance.OperatorWithRole, reason, waiverExpiresAtUtc, riskClassification);
            RefreshInspectionConfigurationUi(InspectionModelConfigurationService.Load());
            _ = RefreshModelRegistryUiAsync();
            WorkflowState.Instance.AddEvent("MODEL_DEPLOYMENT_WAIVER", $"Deployed model {row.ModelId} with Admin waiver risk={riskClassification}; expiring {waiverExpiresAtUtc:O}. Full automation remains blocked without real PASS evidence.");
            MessageBox.Show("Model deployed with waiver. Readiness packages will show this waiver and will not claim full production readiness.", "Model Lifecycle", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show(ex.Message, "Model Lifecycle", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnRetireModelClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Retiring model lifecycle state"))
            return;

        if (ModelRegistryGrid.SelectedItem is not ModelRegistryRow row)
        {
            MessageBox.Show("Select a registered model first.", "Model Lifecycle", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var reason = PromptForText("Retire Model", "Retirement reason");
        if (string.IsNullOrWhiteSpace(reason))
            return;

        try
        {
            ModelLifecycleService.RetireModel(row.ModelId, WorkflowState.Instance.CurrentRole, WorkflowState.Instance.OperatorWithRole, reason);
            RefreshInspectionConfigurationUi(InspectionModelConfigurationService.Load());
            _ = RefreshModelRegistryUiAsync();
            WorkflowState.Instance.AddEvent("MODEL_RETIRED", $"Retired model {row.ModelId}.");
            MessageBox.Show("Model retired and removed from active use.", "Model Lifecycle", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            MessageBox.Show(ex.Message, "Model Lifecycle", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnBrowseCameraTopClick(object sender, RoutedEventArgs e) => BrowseCameraFolder(CameraViewType.Top);
    private void OnBrowseCameraSideClick(object sender, RoutedEventArgs e) => BrowseCameraFolder(CameraViewType.Side);
    private void OnBrowseCameraBottomClick(object sender, RoutedEventArgs e) => BrowseCameraFolder(CameraViewType.Bottom);

    private void BrowseCameraFolder(CameraViewType viewType)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Changing camera simulation folder"))
            return;

        var dialog = new OpenFolderDialog
        {
            Title = $"Select {viewType} camera simulation folder",
            Multiselect = false,
        };

        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FolderName))
            return;

        switch (viewType)
        {
            case CameraViewType.Side:
                CameraSideFolderText.Text = dialog.FolderName;
                break;
            case CameraViewType.Bottom:
                CameraBottomFolderText.Text = dialog.FolderName;
                break;
            default:
                CameraTopFolderText.Text = dialog.FolderName;
                break;
        }

        CameraSourceCombo.SelectedIndex = 1;
        RefreshCameraSourceUi(BuildCameraSourceSettingsFromUi());
    }

    private void OnTestCameraSourceClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Testing camera source"))
            return;

        var settings = BuildCameraSourceSettingsFromUi();
        var source = CameraSourceFactory.Create(settings);
        source.SelectedView = CameraViewType.Top;
        CameraFrame? frame = null;
        try
        {
            source.StartAcquisition();
            frame = source.GetNextFrame();
            source.StopAcquisition();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            CameraDiagnosticsText.Text = $"Camera test failed: {ex.Message}";
            MessageBox.Show(CameraDiagnosticsText.Text, "Camera Source Test", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var diagnostics = source is ICameraStatusDiagnostics statusDiagnostics
            ? string.Join(" ", statusDiagnostics.GetMessages())
            : source.StatusMessage;
        var frameMessage = frame is null
            ? "No frame returned."
            : $"Frame {frame.FrameId}, {frame.Width}x{frame.Height}, {frame.PixelFormat}, {frame.SourceKind}.";
        CameraDiagnosticsText.Text = $"{source.Name}: {CameraStatusDisplay(source.ConnectionStatus)}. {source.StatusMessage} {frameMessage} {diagnostics}";
        MessageBox.Show(
            CameraDiagnosticsText.Text,
            "Camera Source Test",
            MessageBoxButton.OK,
            source.ConnectionStatus is CameraSourceStatus.Ready or CameraSourceStatus.Simulated ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void OnBrowseCameraAdapterClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select external camera adapter plugin folder",
            Multiselect = false,
        };

        if (dialog.ShowDialog() == true)
        {
            CameraAdapterFolderText.Text = dialog.FolderName;
            CameraSourceCombo.SelectedIndex = 2;
            RefreshCameraSourceUi(BuildCameraSourceSettingsFromUi());
        }
    }

    private void OnDiscoverCameraAdaptersClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Discovering camera adapters"))
            return;

        var settings = BuildCameraSourceSettingsFromUi();
        var load = CameraAdapterPluginService.LoadFactory(settings.AdapterFolder);
        if (!load.Success || load.Factory is null)
        {
            CameraDiagnosticsText.Text = load.Message;
            MessageBox.Show(load.Message, "Camera Adapter Discovery", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        CameraSourceCombo.SelectedIndex = 2;
        CameraDiagnosticsText.Text =
            $"Loaded adapter {load.Factory.DisplayName} {load.Factory.Version}. " +
            $"Interfaces: {string.Join(", ", load.Factory.SupportedInterfaces)}. " +
            $"Pixel formats: {string.Join(", ", load.Factory.SupportedPixelFormats)}. " +
            "Adapter loading is opt-in and does not claim real hardware readiness until camera acceptance captures real hardware frames.";
        MessageBox.Show(CameraDiagnosticsText.Text, "Camera Adapter Discovery", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnDiscoverCamerasClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Discovering cameras"))
            return;

        var settings = BuildCameraSourceSettingsFromUi();
        var discovery = VisionCameraPluginLoader.CreateDiscovery(settings, out var loadMessage);
        IReadOnlyList<VisionDeviceInfo> devices;
        try
        {
            devices = discovery.DiscoverDevices();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            CameraDiagnosticsText.Text = $"Camera discovery failed safely: {ex.Message}";
            MessageBox.Show(CameraDiagnosticsText.Text, "Camera Discovery", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        AssignDiscoveredDevices(devices);
        var summary = devices.Count == 0
            ? "No cameras discovered."
            : string.Join(Environment.NewLine, devices.Select(device => $"{device.SuggestedView}: {device.DeviceId} {device.Vendor} {device.Model} {device.InterfaceType} {device.Status} {string.Join("/", device.Capabilities ?? Array.Empty<string>())}"));
        CameraDiagnosticsText.Text = $"{loadMessage} Discovered {devices.Count} device(s). {summary}";
        MessageBox.Show(CameraDiagnosticsText.Text, "Camera Discovery", MessageBoxButton.OK, devices.Any(d => !string.IsNullOrWhiteSpace(d.DeviceId)) ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void OnExportHardwareAcceptanceClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Exporting hardware acceptance package"))
            return;

        try
        {
            var root = Path.Combine(AoiDatabase.StorageRoot, "exports", "hardware_acceptance", DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(root);
            var camera = _lastCameraAcceptanceRun ?? AoiDatabase.GetLatestCameraAcceptanceRun();
            var lighting = _lastLightingAcceptanceRun ?? AoiDatabase.GetLatestLightingAcceptanceRun();
            var manifest = new
            {
                schemaVersion = "hardware-acceptance-package/v1",
                generatedAtUtc = DateTime.UtcNow,
                cameraIncluded = camera is not null,
                lightingIncluded = lighting is not null,
                limitation = "Fake, null, folder, or simulated evidence does not validate real GigE/USB3 cameras or real lighting controllers.",
            };

            File.WriteAllText(Path.Combine(root, "hardware_acceptance_manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            if (camera is not null)
                File.WriteAllText(Path.Combine(root, "latest_camera_acceptance.json"), JsonSerializer.Serialize(camera, new JsonSerializerOptions { WriteIndented = true }));
            if (lighting is not null)
                File.WriteAllText(Path.Combine(root, "latest_lighting_acceptance.json"), JsonSerializer.Serialize(lighting, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(root, "README.txt"), "Hardware acceptance evidence is scoped to the recorded adapters/controllers. Simulation-only evidence must not be presented as real production hardware validation.");

            MessageBox.Show($"Hardware acceptance package exported:\n{root}", "Hardware Acceptance", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show($"Hardware acceptance export failed:\n{ex.Message}", "Hardware Acceptance", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void AssignDiscoveredDevices(IReadOnlyList<VisionDeviceInfo> devices)
    {
        foreach (var device in devices.Where(device => !string.IsNullOrWhiteSpace(device.DeviceId)))
        {
            switch (device.SuggestedView.Trim().ToLowerInvariant())
            {
                case "side":
                    CameraSideDeviceIdText.Text = device.DeviceId;
                    break;
                case "bottom":
                    CameraBottomDeviceIdText.Text = device.DeviceId;
                    break;
                case "top":
                    CameraTopDeviceIdText.Text = device.DeviceId;
                    break;
            }
        }
    }

    private async void OnRunCameraAcceptanceClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Running camera acceptance test"))
            return;

        if (_cameraAcceptanceCancellation is not null)
            return;

        var settings = BuildCameraSourceSettingsFromUi();
        var criteria = new CameraAcceptanceCriteria
        {
            FramesPerView = 5,
            RequiredViews = new() { "Top", "Side", "Bottom" },
        };
        _cameraAcceptanceCancellation = new CancellationTokenSource();
        RefreshRoleControls();
        CameraAcceptanceStatusText.Text = "Acceptance: RUNNING";
        CameraAcceptanceStatusText.Foreground = Brushes.Gold;
        CameraDiagnosticsText.Text = "Camera acceptance test running...";

        var progress = new Progress<string>(message => CameraDiagnosticsText.Text = message);
        try
        {
            var token = _cameraAcceptanceCancellation.Token;
            var run = await Task.Run(() => CameraAcceptanceTestService.Run(settings, criteria, progress: progress, cancellationToken: token), token);
            AoiDatabase.RecordCameraAcceptanceRun(run, WorkflowState.Instance.OperatorWithRole);
            _lastCameraAcceptanceRun = run;
            WorkflowState.Instance.AddEvent("CAMERA_ACCEPTANCE", $"Camera acceptance: {run.Status}; readiness {run.FactoryReadinessStatus}; frames {run.TotalReceivedFrames}/{run.TotalRequestedFrames}.");
            CameraAcceptanceStatusText.Text = $"Acceptance: {run.Status} / {run.FactoryReadinessStatus}";
            CameraAcceptanceStatusText.Foreground = run.Status switch
            {
                "PASS" => Brushes.LightGreen,
                "WARN" => Brushes.Gold,
                _ => Brushes.IndianRed,
            };
            CameraDiagnosticsText.Text = BuildCameraAcceptanceUiSummary(run);
            MessageBox.Show(
                CameraDiagnosticsText.Text,
                "Camera Acceptance Test",
                MessageBoxButton.OK,
                run.Status == "FAIL" ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            CameraAcceptanceStatusText.Text = "Acceptance: CANCELED";
            CameraAcceptanceStatusText.Foreground = Brushes.Gold;
            CameraDiagnosticsText.Text = "Camera acceptance test canceled.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            CameraAcceptanceStatusText.Text = "Acceptance: ERROR";
            CameraAcceptanceStatusText.Foreground = Brushes.IndianRed;
            CameraDiagnosticsText.Text = $"Camera acceptance failed: {ex.Message}";
            MessageBox.Show(CameraDiagnosticsText.Text, "Camera Acceptance Test", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _cameraAcceptanceCancellation?.Dispose();
            _cameraAcceptanceCancellation = null;
            RefreshRoleControls();
        }
    }

    private void OnCancelCameraAcceptanceClick(object sender, RoutedEventArgs e)
    {
        _cameraAcceptanceCancellation?.Cancel();
    }

    private void OnExportCameraAcceptanceClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Exporting camera acceptance report"))
            return;

        var run = _lastCameraAcceptanceRun ?? AoiDatabase.GetLatestCameraAcceptanceRun();
        if (run is null)
        {
            MessageBox.Show("No camera acceptance run is available to export.", "Camera Acceptance", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var export = CameraAcceptanceTestService.ExportReport(run);
            WorkflowState.Instance.AddEvent("CAMERA_ACCEPTANCE_EXPORT", $"Camera acceptance report exported: {Path.GetFileName(export.JsonPath)}.");
            MessageBox.Show(
                $"Camera acceptance report exported.\n\nJSON: {export.JsonPath}\nHTML: {export.HtmlPath}",
                "Camera Acceptance",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show($"Camera acceptance export failed:\n{ex.Message}", "Camera Acceptance", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string BuildCameraAcceptanceUiSummary(CameraAcceptanceRun run)
    {
        var firstMessage = run.Failures.Concat(run.Warnings).FirstOrDefault();
        var evidenceBoundary = run.IsRealHardware
            ? "Real hardware acceptance evidence recorded."
            : "Simulation-only evidence; real GigE/USB3 camera readiness is NOT VALIDATED.";
        return $"Camera acceptance {run.Status}; readiness {run.FactoryReadinessStatus}; frames {run.TotalReceivedFrames}/{run.TotalRequestedFrames}; dropped {run.DroppedFrameCount}; trigger failures {run.TriggerFailureCount}; timeouts {run.TimeoutCount}. {evidenceBoundary} {firstMessage}";
    }

    private void OnOpenTrainingFolderClick(object sender, RoutedEventArgs e)
    {
        var trainingDir = AoiDatabase.TrainingVaultPath;
        Directory.CreateDirectory(trainingDir);

        Process.Start(new ProcessStartInfo
        {
            FileName = trainingDir,
            UseShellExecute = true,
        });
    }

    private void RefreshWorkflowUi()
    {
        var state = WorkflowState.Instance;

        DetectionPriorityCombo.SelectedIndex = state.DetectionPriority switch
        {
            Models.DetectionPriority.MinimizeFalsePositives => 0,
            Models.DetectionPriority.Balanced => 1,
            Models.DetectionPriority.MaximizeDefectRecall => 2,
            _ => 0,
        };

        ReviewDefaultText.Text = DetectionPriorityDisplay(state.DetectionPriority, _isKorean);
        DeploymentProfileCombo.SelectedIndex = DeploymentProfileToCombo(DeploymentProfileSettingsService.Load());
        RefreshOperatingModeUi();
        TrainingStatusText.Text = state.Training.IsRunning ? "RUNNING" : "IDLE";
        TrainingQueueText.Text = state.Training.QueuedSamples.ToString();
        TrainingEpochText.Text = state.Training.EpochsCompleted.ToString();
        TrainingValidationText.Text = state.Training.LastCompletedAt is null
            ? "--"
            : $"{state.Training.LastValidationScore:F1}%";
        StorageRootText.Text = AoiDatabase.StorageRoot;

        RefreshRoleControls();
        RefreshInspectionConfigurationUi();
        _ = RefreshModelRegistryUiAsync();
        _ = RefreshLearnedVisualModelRegistryUiAsync();
        RefreshModelAcceptanceRunsUi();
        RefreshCameraSourceUi();
        RefreshLightingUi();
        RefreshMesIntegrationUi();
        RefreshCentralSyncUi();
        RefreshDefectTaxonomyUi();
    }

    private void RefreshRoleControls()
    {
        var role = WorkflowState.Instance.CurrentRole;
        var canManageSettings = RoleAuthorization.CanManageSettings(role);
        var canChangeThresholds = RoleAuthorization.CanChangeThresholds(role);

        DetectionPriorityCombo.IsEnabled = canChangeThresholds;
        DeploymentProfileCombo.IsEnabled = canManageSettings;
        OperatingModeCombo.IsEnabled = canManageSettings;
        InspectionEngineCombo.IsEnabled = canManageSettings;
        CameraSourceCombo.IsEnabled = canManageSettings;
        CameraTopFolderText.IsEnabled = canManageSettings;
        CameraSideFolderText.IsEnabled = canManageSettings;
        CameraBottomFolderText.IsEnabled = canManageSettings;
        CameraTopDeviceIdText.IsEnabled = canManageSettings;
        CameraSideDeviceIdText.IsEnabled = canManageSettings;
        CameraBottomDeviceIdText.IsEnabled = canManageSettings;
        CameraAdapterFolderText.IsEnabled = canManageSettings;
        BrowseCameraAdapterBtn.IsEnabled = canManageSettings;
        CameraAcquisitionModeCombo.IsEnabled = canManageSettings;
        CameraExposureMsText.IsEnabled = canManageSettings;
        CameraGainText.IsEnabled = canManageSettings;
        CameraTriggerTimeoutMsText.IsEnabled = canManageSettings;
        CameraFrameTimeoutMsText.IsEnabled = canManageSettings;
        CameraBoardModelText.IsEnabled = canManageSettings;
        CameraLotIdText.IsEnabled = canManageSettings;
        LightingModeCombo.IsEnabled = canManageSettings;
        LightingTcpHostText.IsEnabled = canManageSettings;
        LightingTcpPortText.IsEnabled = canManageSettings;
        LightingSerialPortText.IsEnabled = canManageSettings;
        LightingBaudRateText.IsEnabled = canManageSettings;
        LightingTopProgramText.IsEnabled = canManageSettings;
        LightingSideProgramText.IsEnabled = canManageSettings;
        LightingBottomProgramText.IsEnabled = canManageSettings;
        LightingCommandTemplateText.IsEnabled = canManageSettings;
        LightingTimeoutMsText.IsEnabled = canManageSettings;
        RunLightingAcceptanceBtn.IsEnabled = canManageSettings && _lightingAcceptanceCancellation is null;
        CancelLightingAcceptanceBtn.IsEnabled = _lightingAcceptanceCancellation is not null;
        ExportLightingAcceptanceBtn.IsEnabled = canManageSettings;
        RunRobotCellAcceptanceBtn.IsEnabled = canManageSettings && _robotAcceptanceCancellation is null;
        CancelRobotCellAcceptanceBtn.IsEnabled = _robotAcceptanceCancellation is not null;
        ExportRobotCellAcceptanceBtn.IsEnabled = canManageSettings;
        StorageRootText.IsEnabled = canManageSettings;
        BrowseStorageRootBtn.IsEnabled = canManageSettings;
        MesModeCombo.IsEnabled = canManageSettings;
        MesEndpointText.IsEnabled = canManageSettings;
        MesRestBaseUrlText.IsEnabled = canManageSettings;
        MesResultPathText.IsEnabled = canManageSettings;
        MesImagePathText.IsEnabled = canManageSettings;
        MesAuthModeCombo.IsEnabled = canManageSettings;
        MesApiKeyHeaderText.IsEnabled = canManageSettings;
        MesApiKeyBox.IsEnabled = canManageSettings;
        MesBearerTokenBox.IsEnabled = canManageSettings;
        MesUsernameText.IsEnabled = canManageSettings;
        MesPasswordBox.IsEnabled = canManageSettings;
        MesTimeoutText.IsEnabled = canManageSettings;
        MesMaxRetryText.IsEnabled = canManageSettings;
        MesRetryBackoffText.IsEnabled = canManageSettings;
        MesAutoUploadCheck.IsEnabled = canManageSettings;
        TestMesRestBtn.IsEnabled = canManageSettings;
        ConsoleTitleText.IsEnabled = canManageSettings;
        StationNameText.IsEnabled = canManageSettings;
        StationSubtitleText.IsEnabled = canManageSettings;
        AccentColorText.IsEnabled = canManageSettings;
        BrandLogoPathText.IsEnabled = canManageSettings;
        BrowseBrandLogoBtn.IsEnabled = canManageSettings;
        CentralSyncModeCombo.IsEnabled = canManageSettings;
        CentralSyncEndpointText.IsEnabled = canManageSettings;
        CentralSyncStationIdText.IsEnabled = canManageSettings;
        CentralSyncIntervalText.IsEnabled = canManageSettings;
        CentralSyncMaxRetryText.IsEnabled = canManageSettings;
        CentralSyncSecretBox.IsEnabled = canManageSettings;
        CentralSyncIncludeImagesCheck.IsEnabled = canManageSettings;
        CentralSyncRedactOperatorCheck.IsEnabled = canManageSettings;
        CentralSyncRedactImagePathsCheck.IsEnabled = canManageSettings;
        CentralSyncRedactEndpointCheck.IsEnabled = canManageSettings;
        BrowseCameraTopBtn.IsEnabled = canManageSettings;
        BrowseCameraSideBtn.IsEnabled = canManageSettings;
        BrowseCameraBottomBtn.IsEnabled = canManageSettings;
        TestCameraSourceBtn.IsEnabled = canManageSettings;
        DiscoverCameraAdaptersBtn.IsEnabled = canManageSettings;
        DiscoverCamerasBtn.IsEnabled = canManageSettings;
        RunCameraAcceptanceBtn.IsEnabled = canManageSettings && _cameraAcceptanceCancellation is null;
        CancelCameraAcceptanceBtn.IsEnabled = _cameraAcceptanceCancellation is not null;
        ExportCameraAcceptanceBtn.IsEnabled = canManageSettings;
        ExportHardwareAcceptanceBtn.IsEnabled = canManageSettings;
        TestLightingBtn.IsEnabled = canManageSettings;
        ModelPathText.IsEnabled = canManageSettings;
        ModelVersionText.IsEnabled = canManageSettings;
        LabelMapPathText.IsEnabled = canManageSettings;
        InputWidthText.IsEnabled = canManageSettings;
        InputHeightText.IsEnabled = canManageSettings;
        InputTensorNameText.IsEnabled = canManageSettings;
        OutputTensorNameText.IsEnabled = canManageSettings;
        BrowseModelBtn.IsEnabled = canManageSettings;
        BrowseLabelMapBtn.IsEnabled = canManageSettings;
        RegisterModelBtn.IsEnabled = canManageSettings;
        SetActiveModelBtn.IsEnabled = canManageSettings;
        SetActiveLearnedVisualModelBtn.IsEnabled = RoleAuthorization.CanSetActiveLearnedVisualModel(role);
        RunSetupWizardBtn.IsEnabled = canManageSettings;
        OpenInstallNotesBtn.IsEnabled = canManageSettings;
        OpenGuideBtn.IsEnabled = true;
        ExportDiagnosticsBtn.IsEnabled = canManageSettings;
        BackupConfigurationBtn.IsEnabled = canManageSettings;
        RestoreConfigurationPreviewBtn.IsEnabled = canManageSettings;
        ApplyRestoreBtn.IsEnabled = canManageSettings && !string.IsNullOrWhiteSpace(_pendingRestoreBackupPath);
        RollbackRestoreBtn.IsEnabled = canManageSettings && ConfigurationBackupService.GetLastRollbackInfo() is not null;
        ExportSupportBundleBtn.IsEnabled = canManageSettings;
        SupportBundleRedactPathsCheck.IsEnabled = canManageSettings;
        SupportBundleIncludeModelsCheck.IsEnabled = canManageSettings;
        ConfidenceThresholdText.IsEnabled = canChangeThresholds;
        TestModelBtn.IsEnabled = RoleAuthorization.CanTestModelConfiguration(role);
        ValidateRegisteredModelBtn.IsEnabled = RoleAuthorization.CanTestModelConfiguration(role);
        RunModelAcceptanceBtn.IsEnabled = RoleAuthorization.CanTestModelConfiguration(role) && _modelAcceptanceCancellation is null;
        CancelModelAcceptanceBtn.IsEnabled = _modelAcceptanceCancellation is not null;
        ViewModelAcceptanceRunsBtn.IsEnabled = canManageSettings;
        CreateModelReleasePackageBtn.IsEnabled = canChangeThresholds;
        PromoteProductionCandidateBtn.IsEnabled = canChangeThresholds;
        DeployModelBtn.IsEnabled = canManageSettings;
        WaiveDeployModelBtn.IsEnabled = canManageSettings;
        RetireModelBtn.IsEnabled = canManageSettings;
        ApproveThresholdProfileBtn.IsEnabled = canChangeThresholds;
        DeployThresholdProfileBtn.IsEnabled = canChangeThresholds;
        ImportTaxonomyBtn.IsEnabled = canChangeThresholds;
        ExportTaxonomyBtn.IsEnabled = canChangeThresholds;
    }

    private void RefreshOperatingModeUi()
    {
        var mode = OperatingModeSettingsService.Load();
        OperatingModeCombo.SelectedIndex = OperatingModeToCombo(mode);
        OperatingModePolicyText.Text = mode switch
        {
            OperatingMode.Production => "Production Mode blocks demo/fallback rows and enforces authentication, production model, real hardware, MES, export, and signoff gates.",
            OperatingMode.Pilot => "Pilot Mode hides demo rows by default, requires customer dataset preflight and readiness evidence, and labels simulated hardware clearly.",
            _ => "Demo Mode allows sample data, demo role selection, and simulated sources.",
        };
        OperatingModePolicyText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(mode switch
        {
            OperatingMode.Production => "#FFBFC1",
            OperatingMode.Pilot => "#FFE0A7",
            _ => "#E1A334",
        }));
    }

    private void RefreshDefectTaxonomyUi()
    {
        try
        {
            var taxonomy = DefectTaxonomyService.GetActiveTaxonomy();
            var classes = taxonomy.Entries.Count(entry => entry.IsActive);
            TaxonomySummaryText.Text = $"{taxonomy.Taxonomy.Name} ({taxonomy.Taxonomy.CustomerName}); classes={classes}; aliases={taxonomy.Aliases.Count}; MES mappings={taxonomy.MesMappings.Count}.";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            TaxonomySummaryText.Text = $"Active taxonomy unavailable: {ex.Message}";
        }
    }

    private async Task RefreshThresholdProfilesUiAsync()
    {
        var selected = (ThresholdProfilesGrid.SelectedItem as ThresholdProfileRow)?.ProfileId;
        _thresholdProfileRows.Clear();
        try
        {
            var boardProgram = WorkflowState.Instance.BoardProgram;
            var snapshot = await Task.Run(() =>
            {
                var profiles = AoiDatabase.GetThresholdProfiles().Select(profile => new ThresholdProfileRow(profile)).ToArray();
                var active = AoiDatabase.GetActiveThresholdProfile("ANY", boardProgram, "ANY")
                    ?? AoiDatabase.GetActiveThresholdProfile("ANY", "ANY", "ANY");
                return (Profiles: profiles, Active: active);
            });

            foreach (var profile in snapshot.Profiles)
                _thresholdProfileRows.Add(profile);

            if (!string.IsNullOrWhiteSpace(selected))
                ThresholdProfilesGrid.SelectedItem = _thresholdProfileRows.FirstOrDefault(row => row.ProfileId == selected);

            ActiveThresholdProfileText.Text = snapshot.Active is null
                ? "Active profile: none"
                : $"Active profile: {snapshot.Active.ProfileId} / {snapshot.Active.Revision} ({snapshot.Active.Status})";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            ActiveThresholdProfileText.Text = $"Active profile: unavailable ({ex.Message})";
        }
    }

    private void OnApproveThresholdProfileClick(object sender, RoutedEventArgs e)
    {
        if (ThresholdProfilesGrid.SelectedItem is not ThresholdProfileRow row)
        {
            MessageBox.Show("Select a threshold profile first.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            ThresholdProfileService.ApproveProfile(row.ProfileId, row.Revision, WorkflowState.Instance.CurrentRole, WorkflowState.Instance.OperatorWithRole);
            _ = RefreshThresholdProfilesUiAsync();
            MessageBox.Show("Threshold profile approved.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            MessageBox.Show(ex.Message, "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnDeployThresholdProfileClick(object sender, RoutedEventArgs e)
    {
        if (ThresholdProfilesGrid.SelectedItem is not ThresholdProfileRow row)
        {
            MessageBox.Show("Select a threshold profile first.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var confirm = MessageBox.Show(
                $"Deploy threshold profile {row.ProfileId}/{row.Revision}?\n\nFuture inspections matching its board/recipe scope will use this active profile. This is customer-dataset calibration evidence, not universal production proof.",
                "Confirm Threshold Profile Deployment",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes)
                return;

            ThresholdProfileService.DeployProfile(row.ProfileId, row.Revision, WorkflowState.Instance.CurrentRole, WorkflowState.Instance.OperatorWithRole);
            _ = RefreshThresholdProfilesUiAsync();
            MessageBox.Show("Threshold profile deployed.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            MessageBox.Show(ex.Message, "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RefreshInspectionConfigurationUi()
        => RefreshInspectionConfigurationUi(InspectionModelConfigurationService.Load());

    private void RefreshInspectionConfigurationUi(InspectionModelConfiguration configuration)
    {
        InspectionEngineCombo.SelectedIndex = configuration.IsLearnedVisualModelSelected
            ? 2
            : configuration.IsOnnxSelected ? 1 : 0;
        ModelPathText.Text = configuration.ModelFilePath;
        ModelVersionText.Text = configuration.ModelVersion;
        LabelMapPathText.Text = configuration.LabelMapPath;
        ConfidenceThresholdText.Text = configuration.ConfidenceThreshold.ToString("0.###", CultureInfo.InvariantCulture);
        InputWidthText.Text = configuration.InputImageWidth.ToString(CultureInfo.InvariantCulture);
        InputHeightText.Text = configuration.InputImageHeight.ToString(CultureInfo.InvariantCulture);
        InputTensorNameText.Text = configuration.InputTensorName;
        OutputTensorNameText.Text = configuration.OutputTensorName;

        var status = InspectionModelConfigurationService.GetStatus(configuration);
        EngineRuntimeStatusText.Text = InspectionModelConfigurationService.GetStatusText(status);
        EngineRuntimeStatusText.Foreground = StatusBrush(status);
        EngineVersionText.Text = configuration.IsOnnxSelected || configuration.IsLearnedVisualModelSelected
            ? configuration.EffectiveModelVersion
            : "PIXEL_DIFF_0.1";
        ModelCheckResultText.Text = ModelConfigurationValidator.ToDisplay(configuration.LastModelCheckResult);
        ModelCheckResultText.Foreground = StatusBrush(configuration.LastModelCheckResult);
        ModelCheckTimestampText.Text = configuration.LastModelCheckTimestampUtc is { } timestamp
            ? timestamp.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture)
            : "--";
        ModelCheckMessageText.Text = configuration.LastModelCheckMessage;
    }

    private async Task RefreshModelRegistryUiAsync()
    {
        var selectedModelId = (ModelRegistryGrid.SelectedItem as ModelRegistryRow)?.ModelId;
        _modelRegistryRows.Clear();

        try
        {
            var models = await Task.Run(() => ModelRegistryService.GetModels().Select(model => new ModelRegistryRow(model)).ToArray());
            foreach (var model in models)
                _modelRegistryRows.Add(model);

            if (!string.IsNullOrWhiteSpace(selectedModelId))
            {
                ModelRegistryGrid.SelectedItem = _modelRegistryRows.FirstOrDefault(row =>
                    string.Equals(row.ModelId, selectedModelId, StringComparison.OrdinalIgnoreCase));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ModelCheckMessageText.Text = $"Model registry could not be loaded: {ex.Message}";
        }
    }

    private async Task RefreshLearnedVisualModelRegistryUiAsync()
    {
        var selectedModelId = (LearnedVisualModelGrid.SelectedItem as LearnedVisualModelRow)?.ModelId;
        _learnedVisualModelRows.Clear();

        try
        {
            var models = await Task.Run(() => LearnedVisualModelRegistryService.ListLearnedModels().Select(model => new LearnedVisualModelRow(model)).ToArray());
            foreach (var model in models)
                _learnedVisualModelRows.Add(model);

            if (!string.IsNullOrWhiteSpace(selectedModelId))
            {
                LearnedVisualModelGrid.SelectedItem = _learnedVisualModelRows.FirstOrDefault(row =>
                    string.Equals(row.ModelId, selectedModelId, StringComparison.OrdinalIgnoreCase));
            }

            LearnedVisualModelStatusText.Text = LearnedVisualModelRegistryService.GetActiveLearnedVisualModel() is { } active
                ? $"Active learned visual model: {active.ModelId} / {active.ProjectName}."
                : "No learned visual model is active.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            LearnedVisualModelStatusText.Text = $"Learned visual model registry could not be loaded: {ex.Message}";
        }
    }

    private void RefreshModelAcceptanceRunsUi(bool showMessage = false)
    {
        _modelAcceptanceRows.Clear();
        try
        {
            if (AoiDatabase.GetLatestModelAcceptanceRun() is { } latest)
                _modelAcceptanceRows.Add(new ModelAcceptanceRunRow(latest));

            if (showMessage)
            {
                MessageBox.Show(
                    _modelAcceptanceRows.Count == 0
                        ? "No model acceptance runs are recorded."
                        : "Latest model acceptance run loaded.",
                    "Model Acceptance",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ModelCheckMessageText.Text = $"Model acceptance runs could not be loaded: {ex.Message}";
        }
    }

    private void SaveInspectionConfiguration(InspectionModelConfiguration? preparedConfiguration = null)
    {
        var configuration = preparedConfiguration ?? BuildConfigurationFromUi();
        var existing = InspectionModelConfigurationService.Load();
        if (string.IsNullOrWhiteSpace(configuration.ActiveModelId) &&
            !string.IsNullOrWhiteSpace(existing.ActiveModelId) &&
            string.Equals(existing.ModelFilePath, configuration.ModelFilePath, StringComparison.OrdinalIgnoreCase))
        {
            configuration.ActiveModelId = existing.ActiveModelId;
            configuration.ActiveModelSha256 = existing.ActiveModelSha256;
            configuration.ActiveModelValidationStatus = existing.ActiveModelValidationStatus;
        }

        if (string.Equals(
                ModelConfigurationValidator.ComputeConfigurationHash(existing),
                ModelConfigurationValidator.ComputeConfigurationHash(configuration),
                StringComparison.OrdinalIgnoreCase))
        {
            configuration.LastModelCheckTimestampUtc = existing.LastModelCheckTimestampUtc;
            configuration.LastModelCheckResult = existing.LastModelCheckResult;
            configuration.LastModelCheckMessage = existing.LastModelCheckMessage;
            configuration.LastModelCheckConfigurationHash = existing.LastModelCheckConfigurationHash;
        }

        InspectionModelConfigurationService.Save(configuration);

        var state = WorkflowState.Instance;
        state.AddEvent(
            "ENGINE_CONFIG",
            configuration.IsLearnedVisualModelSelected
                ? $"Inspection engine set to Learned PCB Visual Model; active model {configuration.ActiveModelId}; image-only Stage 1 evidence."
                : configuration.IsOnnxSelected
                ? $"Inspection engine set to ONNX ML Model; status {EngineRuntimeStatusText.Text}; version {configuration.EffectiveModelVersion}."
                : "Inspection engine set to Pixel Difference Prototype Engine.");
    }

    private static bool ApplyStorageRoot(string storageRoot, bool storageRootChanged)
    {
        if (!storageRootChanged)
            return true;

        try
        {
            StorageRootSettingsService.SaveStorageRoot(storageRoot);
            AoiDatabase.ConfigureStorageRoot(storageRoot);
            AoiDatabase.Initialize();
            WorkflowState.Instance.AddEvent("STORAGE_CONFIG", $"Local storage root changed to {AoiDatabase.StorageRoot}.");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            MessageBox.Show(
                $"Storage path could not be applied:\n{ex.Message}",
                "AOI Monitor",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }
    }

    private void OnTestModelConfigurationClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanTestModelConfiguration, "Testing model configuration"))
            return;

        var configuration = BuildConfigurationFromUi();
        var existing = InspectionModelConfigurationService.Load();
        if (!RoleAuthorization.CanManageSettings(WorkflowState.Instance.CurrentRole) &&
            HasAdminOnlyModelConfigurationChange(existing, configuration))
        {
            MessageBox.Show(
                "Only Admin can test unsaved model path, tensor, label-map, or input-size changes. Apply or ask an Admin to save the model configuration first.",
                "Permission Denied",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var numericError = ValidateRawModelCheckFields();
        ModelConfigurationTestResult result;
        if (configuration.IsLearnedVisualModelSelected)
        {
            if (string.IsNullOrWhiteSpace(configuration.ActiveModelId) &&
                existing.IsLearnedVisualModelSelected &&
                !string.IsNullOrWhiteSpace(existing.ActiveModelId))
            {
                configuration.ActiveModelId = existing.ActiveModelId;
                configuration.ActiveModelSha256 = existing.ActiveModelSha256;
                configuration.ActiveModelValidationStatus = existing.ActiveModelValidationStatus;
            }

            var status = InspectionModelConfigurationService.GetStatus(configuration);
            var ready = status == InspectionEngineStatus.LearnedVisualModelReady;
            result = new ModelConfigurationTestResult(
                ready ? ModelConfigurationTestStatus.Ready : ModelConfigurationTestStatus.MissingModel,
                DateTime.UtcNow,
                ready
                    ? "Learned PCB Visual Model metadata and reference/tolerance artifacts are available. Image-only Stage 1 learning; not live camera validation."
                    : "No active learned PCB visual model with required artifacts is available.",
                string.Empty);
            configuration.LastModelCheckTimestampUtc = result.TimestampUtc;
            configuration.LastModelCheckResult = result.Status;
            configuration.LastModelCheckMessage = result.Message;
            configuration.LastModelCheckConfigurationHash = string.Empty;
            InspectionModelConfigurationService.Save(configuration);
        }
        else if (numericError is not null)
        {
            result = new ModelConfigurationTestResult(
                ModelConfigurationTestStatus.RuntimeError,
                DateTime.UtcNow,
                numericError,
                ModelConfigurationValidator.ComputeConfigurationHash(configuration));
            configuration.LastModelCheckTimestampUtc = result.TimestampUtc;
            configuration.LastModelCheckResult = result.Status;
            configuration.LastModelCheckMessage = result.Message;
            configuration.LastModelCheckConfigurationHash = result.ConfigurationHash;
            InspectionModelConfigurationService.Save(configuration);
        }
        else
        {
            result = InspectionModelConfigurationService.TestAndSave(configuration);
        }

        WorkflowState.Instance.AddEvent(
            "MODEL_CHECK",
            $"Model configuration test: {result.DisplayStatus}. {result.Message}");

        RefreshInspectionConfigurationUi(InspectionModelConfigurationService.Load());
        MessageBox.Show(
            $"{result.DisplayStatus}\n\n{result.Message}",
            "Model Configuration Test",
            MessageBoxButton.OK,
            result.Status == ModelConfigurationTestStatus.Ready ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void SaveCameraSourceSettings(CameraSourceSettings? preparedSettings = null)
    {
        var settings = preparedSettings ?? BuildCameraSourceSettingsFromUi();
        CameraSourceSettingsService.Save(settings);
        CameraSourceSettingsService.ApplyActiveSource();

        WorkflowState.Instance.AddEvent(
            "CAMERA_CONFIG",
            settings.SourceKey switch
            {
                CameraSourceFactory.FolderSimulationSourceKey => $"Camera source set to Folder Simulation; status {CameraSourceFactory.ActiveSource.ConnectionStatus}.",
                CameraSourceFactory.GenericVisionAdapterSourceKey => $"Camera source set to Generic Vision Adapter; status {CameraSourceFactory.ActiveSource.ConnectionStatus}. Adapter configured does not imply camera connected.",
                _ => "Camera source set to No Camera / Not Connected.",
            });
    }

    private void RefreshCameraSourceUi()
        => RefreshCameraSourceUi(CameraSourceSettingsService.Load());

    private void RefreshCameraSourceUi(CameraSourceSettings settings)
    {
        CameraSourceCombo.SelectedIndex = settings.SourceKey switch
        {
            CameraSourceFactory.FolderSimulationSourceKey => 1,
            CameraSourceFactory.GenericVisionAdapterSourceKey => 2,
            _ => 0,
        };
        CameraTopFolderText.Text = settings.TopFolder;
        CameraSideFolderText.Text = settings.SideFolder;
        CameraBottomFolderText.Text = settings.BottomFolder;
        CameraTopDeviceIdText.Text = settings.TopDeviceId;
        CameraSideDeviceIdText.Text = settings.SideDeviceId;
        CameraBottomDeviceIdText.Text = settings.BottomDeviceId;
        CameraAdapterFolderText.Text = settings.AdapterFolder;
        CameraAcquisitionModeCombo.SelectedIndex = settings.AcquisitionMode switch
        {
            CameraAcquisitionMode.SoftwareTrigger => 1,
            CameraAcquisitionMode.HardwareTrigger => 2,
            _ => 0,
        };
        CameraExposureMsText.Text = settings.ExposureMs.ToString("0.###", CultureInfo.InvariantCulture);
        CameraGainText.Text = settings.Gain.ToString("0.###", CultureInfo.InvariantCulture);
        CameraTriggerTimeoutMsText.Text = settings.TriggerTimeoutMs.ToString(CultureInfo.InvariantCulture);
        CameraFrameTimeoutMsText.Text = settings.FrameTimeoutMs.ToString(CultureInfo.InvariantCulture);
        CameraBoardModelText.Text = settings.BoardModel;
        CameraLotIdText.Text = settings.LotId;

        var source = CameraSourceFactory.Create(settings);
        CameraSourceStatusText.Text = $"Camera: {CameraStatusDisplay(source.ConnectionStatus)}";
        CameraSourceStatusText.Foreground = StatusBrush(source.ConnectionStatus);
        var latest = _lastCameraAcceptanceRun ?? AoiDatabase.GetLatestCameraAcceptanceRun();
        CameraAcceptanceStatusText.Text = latest is null
            ? "Acceptance: Not validated"
            : $"Acceptance: {latest.Status} / {latest.FactoryReadinessStatus}";
        CameraAcceptanceStatusText.Foreground = latest?.Status switch
        {
            "PASS" => Brushes.LightGreen,
            "WARN" => Brushes.Gold,
            "FAIL" => Brushes.IndianRed,
            _ => Brushes.Gold,
        };
        CameraDiagnosticsText.Text = latest is null
            ? source.StatusMessage
            : $"{source.StatusMessage} Latest acceptance: {latest.Status}; readiness {latest.FactoryReadinessStatus}; frames {latest.TotalReceivedFrames}/{latest.TotalRequestedFrames}.";
    }

    private CameraSourceSettings BuildCameraSourceSettingsFromUi()
        => new()
        {
            SourceKey = CameraSourceCombo.SelectedIndex switch
            {
                1 => CameraSourceFactory.FolderSimulationSourceKey,
                2 => CameraSourceFactory.GenericVisionAdapterSourceKey,
                _ => CameraSourceFactory.NullSourceKey,
            },
            TopFolder = CameraTopFolderText.Text.Trim(),
            SideFolder = CameraSideFolderText.Text.Trim(),
            BottomFolder = CameraBottomFolderText.Text.Trim(),
            TopDeviceId = CameraTopDeviceIdText.Text.Trim(),
            SideDeviceId = CameraSideDeviceIdText.Text.Trim(),
            BottomDeviceId = CameraBottomDeviceIdText.Text.Trim(),
            AdapterFolder = CameraAdapterFolderText.Text.Trim(),
            AcquisitionMode = CameraAcquisitionModeCombo.SelectedIndex switch
            {
                1 => CameraAcquisitionMode.SoftwareTrigger,
                2 => CameraAcquisitionMode.HardwareTrigger,
                _ => CameraAcquisitionMode.Continuous,
            },
            ExposureMs = double.TryParse(CameraExposureMsText.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var exposureMs) ? exposureMs : 5.0,
            Gain = double.TryParse(CameraGainText.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var gain) ? gain : 1.0,
            TriggerTimeoutMs = int.TryParse(CameraTriggerTimeoutMsText.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var triggerTimeoutMs) ? triggerTimeoutMs : 250,
            FrameTimeoutMs = int.TryParse(CameraFrameTimeoutMsText.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var frameTimeoutMs) ? frameTimeoutMs : 1000,
            BoardModel = CameraBoardModelText.Text.Trim(),
            LotId = CameraLotIdText.Text.Trim(),
        };

    private void SaveLightingSettings(LightingSettings? preparedSettings = null)
    {
        var settings = preparedSettings ?? BuildLightingSettingsFromUi();
        var validation = LightingSettingsService.Validate(settings);
        if (validation.Count > 0)
        {
            LightingDiagnosticsText.Text = string.Join(" ", validation);
            WorkflowState.Instance.AddEvent("LIGHTING_CONFIG_WARNING", LightingDiagnosticsText.Text);
            return;
        }

        LightingSettingsService.Save(settings);
        LightingSettingsService.ApplyIntegrationBoundary();

        WorkflowState.Instance.AddEvent(
            "LIGHTING_CONFIG",
            settings.Mode switch
            {
                LightingModes.Simulated => "Lighting set to Simulated. No real lighting command will be sent.",
                LightingModes.TcpText => $"Lighting set to TCP text protocol endpoint {settings.TcpHost}:{settings.TcpPort}.",
                LightingModes.SerialText => $"Lighting set to Serial text protocol endpoint {settings.SerialPortName}.",
                _ => "Lighting set to None / Not Connected.",
            });
    }

    private void RefreshLightingUi()
        => RefreshLightingUi(LightingSettingsService.Load());

    private void RefreshLightingUi(LightingSettings settings)
    {
        LightingModeCombo.SelectedIndex = settings.Mode switch
        {
            LightingModes.Simulated => 1,
            LightingModes.TcpText => 2,
            LightingModes.SerialText => 3,
            _ => 0,
        };
        LightingTcpHostText.Text = settings.TcpHost;
        LightingTcpPortText.Text = settings.TcpPort.ToString(CultureInfo.InvariantCulture);
        LightingSerialPortText.Text = settings.SerialPortName;
        LightingBaudRateText.Text = settings.BaudRate.ToString(CultureInfo.InvariantCulture);
        LightingTopProgramText.Text = settings.TopProgram;
        LightingSideProgramText.Text = settings.SideProgram;
        LightingBottomProgramText.Text = settings.BottomProgram;
        LightingCommandTemplateText.Text = settings.CommandTemplate;
        LightingTimeoutMsText.Text = settings.ResponseTimeoutMs.ToString(CultureInfo.InvariantCulture);

        var controller = LightingControllerFactory.Create(settings);
        LightingSettingsStatusText.Text = $"Lighting: {IntegrationStatusDisplay(controller.Status)}";
        LightingSettingsStatusText.Foreground = IntegrationStatusBrush(controller.Status);
        var validation = LightingSettingsService.Validate(settings);
        var latest = _lastLightingAcceptanceRun ?? AoiDatabase.GetLatestLightingAcceptanceRun();
        LightingAcceptanceStatusText.Text = latest is null
            ? "Sync: Not validated"
            : $"Sync: {latest.Status} ({(latest.IsSimulated ? "simulated" : "configured")})";
        LightingAcceptanceStatusText.Foreground = latest?.Status switch
        {
            "PASS" => Brushes.LightGreen,
            "WARN" => Brushes.Gold,
            "FAIL" => Brushes.IndianRed,
            _ => Brushes.Gold,
        };
        var baseMessage = validation.Count == 0
            ? controller.StatusMessage
            : string.Join(" ", validation);
        LightingDiagnosticsText.Text = latest is null
            ? baseMessage
            : $"{baseMessage} Latest sync acceptance: {latest.Status}; steps {latest.PassedStepCount}/{latest.StepCount}; simulated={latest.IsSimulated}.";
    }

    private async void OnTestLightingClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Testing lighting synchronization"))
            return;

        var settings = BuildLightingSettingsFromUi();
        var validation = LightingSettingsService.Validate(settings);
        if (validation.Count > 0)
        {
            LightingDiagnosticsText.Text = string.Join(" ", validation);
            MessageBox.Show(LightingDiagnosticsText.Text, "Lighting Test", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var button = sender as Button;
        await ErrorBoundaryService.SafeAsyncCommand(
            "Test lighting synchronization",
            "Settings",
            async token =>
            {
                var controller = LightingControllerFactory.Create(settings);
                var result = await LightingSynchronizationService.SynchronizeAsync(controller, settings, "Top", token);
                LightingDiagnosticsText.Text = $"{result.Message} Status={result.Status}.";
                LightingSettingsStatusText.Text = $"Lighting: {IntegrationStatusDisplay(result.Status)}";
                LightingSettingsStatusText.Foreground = IntegrationStatusBrush(result.Status);
                WorkflowState.Instance.AddEvent("LIGHTING_TEST", LightingDiagnosticsText.Text);
                MessageBox.Show(
                    LightingDiagnosticsText.Text,
                    "Lighting Test",
                    MessageBoxButton.OK,
                    result.Status is IntegrationConnectionStatus.Ready or IntegrationConnectionStatus.Simulated ? MessageBoxImage.Information : MessageBoxImage.Warning);
            },
            running =>
            {
                if (button is not null)
                    button.IsEnabled = !running;
            },
            message =>
            {
                LightingDiagnosticsText.Text = message;
                LightingSettingsStatusText.Text = "Lighting: Error";
                LightingSettingsStatusText.Foreground = Brushes.IndianRed;
            });
    }

    private async void OnRunLightingAcceptanceClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Running lighting sync acceptance test"))
            return;

        if (_lightingAcceptanceCancellation is not null)
            return;

        var settings = BuildLightingSettingsFromUi();
        var validation = LightingSettingsService.Validate(settings);
        if (validation.Count > 0)
        {
            LightingDiagnosticsText.Text = string.Join(" ", validation);
            MessageBox.Show(LightingDiagnosticsText.Text, "Lighting Sync Acceptance", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _lightingAcceptanceCancellation = new CancellationTokenSource();
        RefreshRoleControls();
        LightingAcceptanceStatusText.Text = "Sync: RUNNING";
        LightingAcceptanceStatusText.Foreground = Brushes.Gold;
        LightingDiagnosticsText.Text = "Lighting sync acceptance test running...";

        var progress = new Progress<string>(message => LightingDiagnosticsText.Text = message);
        try
        {
            var token = _lightingAcceptanceCancellation.Token;
            var run = await LightingAcceptanceTestService.RunAsync(
                settings,
                new LightingAcceptanceCriteria { RequiredViews = new() { "Top", "Side", "Bottom" } },
                cameraSource: CameraSourceFactory.ActiveSource,
                progress: progress,
                cancellationToken: token);
            AoiDatabase.RecordLightingAcceptanceRun(run, WorkflowState.Instance.OperatorWithRole);
            _lastLightingAcceptanceRun = run;
            WorkflowState.Instance.AddEvent("LIGHTING_ACCEPTANCE", $"Lighting sync acceptance: {run.Status}; steps {run.PassedStepCount}/{run.StepCount}; simulated={run.IsSimulated}.");
            LightingAcceptanceStatusText.Text = $"Sync: {run.Status} ({(run.IsSimulated ? "simulated" : "configured")})";
            LightingAcceptanceStatusText.Foreground = run.Status switch
            {
                "PASS" => Brushes.LightGreen,
                "WARN" => Brushes.Gold,
                _ => Brushes.IndianRed,
            };
            LightingDiagnosticsText.Text = BuildLightingAcceptanceUiSummary(run);
            MessageBox.Show(
                LightingDiagnosticsText.Text,
                "Lighting Sync Acceptance",
                MessageBoxButton.OK,
                run.Status == "FAIL" ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            LightingAcceptanceStatusText.Text = "Sync: CANCELED";
            LightingAcceptanceStatusText.Foreground = Brushes.Gold;
            LightingDiagnosticsText.Text = "Lighting sync acceptance test canceled.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            LightingAcceptanceStatusText.Text = "Sync: ERROR";
            LightingAcceptanceStatusText.Foreground = Brushes.IndianRed;
            LightingDiagnosticsText.Text = $"Lighting sync acceptance failed: {ex.Message}";
            MessageBox.Show(LightingDiagnosticsText.Text, "Lighting Sync Acceptance", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _lightingAcceptanceCancellation?.Dispose();
            _lightingAcceptanceCancellation = null;
            RefreshRoleControls();
        }
    }

    private void OnCancelLightingAcceptanceClick(object sender, RoutedEventArgs e)
    {
        _lightingAcceptanceCancellation?.Cancel();
    }

    private void OnExportLightingAcceptanceClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Exporting lighting sync acceptance report"))
            return;

        var run = _lastLightingAcceptanceRun ?? AoiDatabase.GetLatestLightingAcceptanceRun();
        if (run is null)
        {
            MessageBox.Show("No lighting sync acceptance run is available to export.", "Lighting Sync Acceptance", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var export = LightingAcceptanceTestService.ExportReport(run);
            WorkflowState.Instance.AddEvent("LIGHTING_ACCEPTANCE_EXPORT", $"Lighting sync acceptance report exported: {Path.GetFileName(export.JsonPath)}.");
            MessageBox.Show(
                $"Lighting sync acceptance report exported.\n\nJSON: {export.JsonPath}\nHTML: {export.HtmlPath}",
                "Lighting Sync Acceptance",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show($"Lighting sync acceptance export failed:\n{ex.Message}", "Lighting Sync Acceptance", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string BuildLightingAcceptanceUiSummary(LightingAcceptanceRun run)
    {
        var firstMessage = run.Failures.Concat(run.Warnings).FirstOrDefault();
        var boundary = run.IsSimulated
            ? "Simulated result; real lighting controller readiness is not claimed."
            : "Configured controller result; verify physical wiring and light output externally.";
        return $"Lighting sync acceptance {run.Status}; steps {run.PassedStepCount}/{run.StepCount}; max command {run.MaxCommandLatencyMs:F1} ms; max trigger-to-frame {run.MaxTriggerToFrameLatencyMs:F1} ms. {boundary} {firstMessage}";
    }

    private async void OnRunRobotCellAcceptanceClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Running robot cell acceptance"))
            return;

        if (_robotAcceptanceCancellation is not null)
            return;

        _robotAcceptanceCancellation = new CancellationTokenSource();
        RefreshRoleControls();
        RobotAcceptanceStatusText.Text = "Acceptance: RUNNING";
        RobotAcceptanceStatusText.Foreground = Brushes.Gold;
        RobotAcceptanceDiagnosticsText.Text = "Robot cell acceptance running...";
        var progress = new Progress<string>(message => RobotAcceptanceDiagnosticsText.Text = message);

        try
        {
            var token = _robotAcceptanceCancellation.Token;
            var run = await RobotCellAcceptanceTestService.RunAsync(progress: progress, cancellationToken: token);
            AoiDatabase.RecordRobotAcceptanceRun(run, WorkflowState.Instance.OperatorWithRole);
            _lastRobotAcceptanceRun = run;
            WorkflowState.Instance.AddEvent("ROBOT_CELL_ACCEPTANCE", $"Robot cell acceptance {run.Status}; source={run.SourceKind}; safety={run.SafetySourceKind}; eStopBlocked={run.EmergencyStopBlocked}; safetyFaultBlocked={run.SafetyFaultBlocked}.");
            RobotAcceptanceStatusText.Text = $"Acceptance: {run.Status} ({run.SourceKind}/{run.SafetySourceKind})";
            RobotAcceptanceStatusText.Foreground = run.Status == "PASS" ? Brushes.LightGreen : Brushes.IndianRed;
            RobotAcceptanceDiagnosticsText.Text = BuildRobotAcceptanceUiSummary(run);
            MessageBox.Show(
                RobotAcceptanceDiagnosticsText.Text,
                "Robot Cell Acceptance",
                MessageBoxButton.OK,
                run.Status == "PASS" ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (OperationCanceledException)
        {
            RobotAcceptanceStatusText.Text = "Acceptance: CANCELED";
            RobotAcceptanceStatusText.Foreground = Brushes.Gold;
            RobotAcceptanceDiagnosticsText.Text = "Robot cell acceptance canceled.";
            WorkflowState.Instance.AddEvent("ROBOT_CELL_ACCEPTANCE", "Robot cell acceptance canceled by user.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            RobotAcceptanceStatusText.Text = "Acceptance: ERROR";
            RobotAcceptanceStatusText.Foreground = Brushes.IndianRed;
            RobotAcceptanceDiagnosticsText.Text = $"Robot cell acceptance failed: {ex.Message}";
            WorkflowState.Instance.AddEvent("ROBOT_CELL_ACCEPTANCE_ERROR", RobotAcceptanceDiagnosticsText.Text);
            MessageBox.Show(RobotAcceptanceDiagnosticsText.Text, "Robot Cell Acceptance", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _robotAcceptanceCancellation?.Dispose();
            _robotAcceptanceCancellation = null;
            RefreshRoleControls();
        }
    }

    private void OnCancelRobotCellAcceptanceClick(object sender, RoutedEventArgs e)
    {
        _robotAcceptanceCancellation?.Cancel();
    }

    private void OnExportRobotCellAcceptanceClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Exporting robot cell acceptance report"))
            return;

        var run = _lastRobotAcceptanceRun ?? AoiDatabase.GetLatestRobotAcceptanceRun();
        if (run is null)
        {
            MessageBox.Show("No robot cell acceptance run is available to export.", "Robot Cell Acceptance", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var export = RobotCellAcceptanceTestService.ExportReport(run);
            ExportVerificationService.RecordVerifiedExport("RobotCellAcceptanceJson", export.JsonPath, run.Status == "PASS" ? "OK" : "WARN");
            ExportVerificationService.RecordVerifiedExport("RobotCellAcceptanceHtml", export.HtmlPath, run.Status == "PASS" ? "OK" : "WARN");
            WorkflowState.Instance.AddEvent("ROBOT_CELL_ACCEPTANCE_EXPORT", $"Robot cell acceptance report exported: {Path.GetFileName(export.JsonPath)}.");
            MessageBox.Show(
                $"Robot cell acceptance report exported.\n\nJSON: {export.JsonPath}\nHTML: {export.HtmlPath}",
                "Robot Cell Acceptance",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show($"Robot cell acceptance export failed:\n{ex.Message}", "Robot Cell Acceptance", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string BuildRobotAcceptanceUiSummary(RobotAcceptanceRun run)
    {
        var firstMessage = run.Failures.Concat(run.Warnings).FirstOrDefault() ?? string.Empty;
        var boundary = run.SourceKind == "Real" && run.SafetySourceKind == "Real"
            ? "Configured real robot/PLC boundary evidence recorded; safety certification remains external."
            : "Simulation/not-connected evidence only; no real production robot movement or safety certification was validated.";
        return $"Robot cell acceptance {run.Status}; source={run.SourceKind}; safety={run.SafetySourceKind}; fullCycle={run.FullCycleMs:F1} ms; emergencyStopBlocked={run.EmergencyStopBlocked}; safetyFaultBlocked={run.SafetyFaultBlocked}. {boundary} {firstMessage}";
    }

    private LightingSettings BuildLightingSettingsFromUi()
        => new()
        {
            Mode = LightingModeCombo.SelectedIndex switch
            {
                1 => LightingModes.Simulated,
                2 => LightingModes.TcpText,
                3 => LightingModes.SerialText,
                _ => LightingModes.None,
            },
            TcpHost = LightingTcpHostText.Text.Trim(),
            TcpPort = int.TryParse(LightingTcpPortText.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tcpPort) ? tcpPort : 0,
            SerialPortName = LightingSerialPortText.Text.Trim(),
            BaudRate = int.TryParse(LightingBaudRateText.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var baudRate) ? baudRate : 0,
            TopProgram = LightingTopProgramText.Text.Trim(),
            SideProgram = LightingSideProgramText.Text.Trim(),
            BottomProgram = LightingBottomProgramText.Text.Trim(),
            CommandTemplate = LightingCommandTemplateText.Text,
            ResponseTimeoutMs = int.TryParse(LightingTimeoutMsText.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var timeout) ? timeout : 0,
        };

    private void SaveMesIntegrationSettings(MesIntegrationSettings? preparedSettings = null)
    {
        var settings = preparedSettings ?? BuildMesIntegrationSettingsFromUi();
        var validation = MesIntegrationSettingsService.Validate(settings);
        if (validation.Count > 0)
        {
            MesDiagnosticsText.Text = string.Join(" ", validation);
            WorkflowState.Instance.AddEvent("MES_CONFIG_WARNING", MesDiagnosticsText.Text);
            return;
        }

        MesIntegrationSettingsService.Save(settings);

        WorkflowState.Instance.AddEvent(
            "MES_CONFIG",
            $"MES/ERP settings updated: {MesIntegrationSettingsService.RedactedSummary(settings)}");
    }

    private void RefreshMesIntegrationUi()
        => RefreshMesIntegrationUi(MesIntegrationSettingsService.Load());

    private void RefreshMesIntegrationUi(MesIntegrationSettings settings)
    {
        MesModeCombo.SelectedIndex = settings.Mode switch
        {
            MesIntegrationMode.MockRest => 1,
            MesIntegrationMode.Rest => 2,
            _ => 0,
        };
        MesEndpointText.Text = settings.MockEndpointUrl;
        MesRestBaseUrlText.Text = settings.BaseUrl;
        MesResultPathText.Text = settings.UploadResultPath;
        MesImagePathText.Text = settings.UploadImagePath;
        MesAuthModeCombo.SelectedIndex = settings.AuthMode switch
        {
            MesRestAuthMode.ApiKey => 1,
            MesRestAuthMode.Bearer => 2,
            MesRestAuthMode.Basic => 3,
            _ => 0,
        };
        MesApiKeyHeaderText.Text = settings.ApiKeyHeaderName;
        MesApiKeyBox.Password = settings.ApiKey;
        MesBearerTokenBox.Password = settings.BearerToken;
        MesUsernameText.Text = settings.Username;
        MesPasswordBox.Password = settings.Password;
        MesTimeoutText.Text = settings.UploadTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        MesMaxRetryText.Text = settings.MaxRetryCount.ToString(CultureInfo.InvariantCulture);
        MesRetryBackoffText.Text = settings.RetryBackoffMs.ToString(CultureInfo.InvariantCulture);
        MesAutoUploadCheck.IsChecked = settings.AutoUploadEnabled;

        MesIntegrationSettingsService.ApplyIntegrationBoundary();
        var status = IntegrationBoundaryRegistry.MesClient.Status;
        MesMockStatusText.Text = settings.Mode switch
        {
            MesIntegrationMode.MockRest => string.IsNullOrWhiteSpace(settings.MockEndpointUrl)
                ? "Mock MES: Local JSON"
                : "Mock MES: REST Configured",
            MesIntegrationMode.Rest => status == IntegrationConnectionStatus.Ready ? "MES REST: Ready" : "MES REST: Config Error",
            _ => "MES: Not Connected",
        };
        MesMockStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(status switch
        {
            IntegrationConnectionStatus.Simulated => "#E1A334",
            IntegrationConnectionStatus.Ready => "#50F56E",
            IntegrationConnectionStatus.Error => "#F27777",
            _ => "#F27777",
        }));
        MesDiagnosticsText.Text = MesIntegrationSettingsService.RedactedSummary(settings);
    }

    private void SaveCentralSyncSettings(CentralSyncSettings? preparedSettings = null)
    {
        var settings = preparedSettings ?? BuildCentralSyncSettingsFromUi();
        var validation = CentralSyncSettingsService.Validate(settings);
        if (validation.Count > 0)
        {
            CentralSyncStatusText.Text = string.Join(" ", validation);
            WorkflowState.Instance.AddEvent("CENTRAL_SYNC_CONFIG_WARNING", CentralSyncStatusText.Text);
            return;
        }

        CentralSyncSettingsService.Save(settings);
        WorkflowState.Instance.AddEvent(
            "CENTRAL_SYNC_CONFIG",
            $"Central sync settings updated: {CentralSyncSettingsService.RedactedSummary(settings)}");
    }

    private void RefreshCentralSyncUi()
        => RefreshCentralSyncUi(CentralSyncSettingsService.Load());

    private void RefreshCentralSyncUi(CentralSyncSettings settings)
    {
        CentralSyncModeCombo.SelectedIndex = settings.Mode switch
        {
            CentralSyncMode.FileDrop => 1,
            CentralSyncMode.RestApi => 2,
            CentralSyncMode.ProductionDatabaseBoundary or CentralSyncMode.PostgreSqlBoundary => 3,
            _ => 0,
        };
        CentralSyncEndpointText.Text = settings.EndpointOrFolder;
        CentralSyncStationIdText.Text = settings.StationId;
        CentralSyncIntervalText.Text = settings.SyncIntervalSeconds.ToString(CultureInfo.InvariantCulture);
        CentralSyncMaxRetryText.Text = settings.MaxRetryCount.ToString(CultureInfo.InvariantCulture);
        CentralSyncSecretBox.Password = settings.SharedSecret;
        CentralSyncIncludeImagesCheck.IsChecked = settings.IncludeImages;
        CentralSyncRedactOperatorCheck.IsChecked = settings.RedactOperatorId;
        CentralSyncRedactImagePathsCheck.IsChecked = settings.RedactImagePaths;
        CentralSyncRedactEndpointCheck.IsChecked = settings.RedactEndpointInExports;
        CentralSyncStatusText.Text = CentralSyncSettingsService.RedactedSummary(settings);
    }

    private async void OnTestMesRestClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Testing MES integration"))
            return;

        var settings = BuildMesIntegrationSettingsFromUi();
        var validation = MesIntegrationSettingsService.Validate(settings);
        if (validation.Count > 0)
        {
            MesDiagnosticsText.Text = string.Join(" ", validation);
            MessageBox.Show(MesDiagnosticsText.Text, "MES Integration Test", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var payload = new TraceabilityPayload
        {
            IntegrationMode = TraceabilityUploadService.ToDisplay(settings.Mode),
            LotId = "TEST-LOT",
            BoardModel = WorkflowState.Instance.BoardProgram,
            SerialNumber = "TEST-BOARD",
            StationId = WorkflowState.Instance.StationId,
            OperatorId = WorkflowState.Instance.OperatorWithRole,
            Result = "REVIEW",
            TimestampUtc = DateTime.UtcNow,
            DefectSummary = "Settings test payload; no production result.",
            SourceNotice = "Settings test payload generated by AOI Monitor. No production inspection result.",
        };

        var button = sender as Button;
        await ErrorBoundaryService.SafeAsyncCommand(
            "Test MES integration",
            "Settings",
            async token =>
            {
                MesIntegrationSettingsService.Save(settings);
                var outcome = await TraceabilityUploadService.UploadAsync(payload, token);
                MesDiagnosticsText.Text = $"{outcome.Result.Message} Payload={outcome.PayloadPath}";
                WorkflowState.Instance.AddEvent("MES_TEST", MesDiagnosticsText.Text);
                MessageBox.Show(
                    MesDiagnosticsText.Text,
                    "MES Integration Test",
                    MessageBoxButton.OK,
                    outcome.Result.Accepted ? MessageBoxImage.Information : MessageBoxImage.Warning);
            },
            running =>
            {
                if (button is not null)
                    button.IsEnabled = !running;
            },
            message => MesDiagnosticsText.Text = message);
    }

    private MesIntegrationSettings BuildMesIntegrationSettingsFromUi()
        => new()
        {
            Mode = MesModeCombo.SelectedIndex switch
            {
                1 => MesIntegrationMode.MockRest,
                2 => MesIntegrationMode.Rest,
                _ => MesIntegrationMode.NotConnected,
            },
            MockEndpointUrl = MesEndpointText.Text.Trim(),
            AutoUploadEnabled = MesAutoUploadCheck.IsChecked == true,
            BaseUrl = MesRestBaseUrlText.Text.Trim(),
            UploadResultPath = MesResultPathText.Text.Trim(),
            UploadImagePath = MesImagePathText.Text.Trim(),
            AuthMode = MesAuthModeCombo.SelectedIndex switch
            {
                1 => MesRestAuthMode.ApiKey,
                2 => MesRestAuthMode.Bearer,
                3 => MesRestAuthMode.Basic,
                _ => MesRestAuthMode.None,
            },
            ApiKeyHeaderName = MesApiKeyHeaderText.Text.Trim(),
            ApiKey = MesApiKeyBox.Password,
            BearerToken = MesBearerTokenBox.Password,
            Username = MesUsernameText.Text.Trim(),
            Password = MesPasswordBox.Password,
            UploadTimeoutSeconds = int.TryParse(
                MesTimeoutText.Text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var timeout)
                ? Math.Clamp(timeout, 1, 300)
                : 10,
            TimeoutSeconds = int.TryParse(
                MesTimeoutText.Text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var restTimeout)
                ? restTimeout
                : 10,
            MaxRetryCount = int.TryParse(
                MesMaxRetryText.Text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var retries)
                ? retries
                : 2,
            RetryBackoffMs = int.TryParse(
                MesRetryBackoffText.Text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var backoff)
                ? backoff
                : 500,
        };

    private CentralSyncSettings BuildCentralSyncSettingsFromUi()
        => new()
        {
            Mode = CentralSyncModeCombo.SelectedIndex switch
            {
                1 => CentralSyncMode.FileDrop,
                2 => CentralSyncMode.RestApi,
                3 => CentralSyncMode.ProductionDatabaseBoundary,
                _ => CentralSyncMode.Disabled,
            },
            EndpointOrFolder = CentralSyncEndpointText.Text.Trim(),
            EndpointUrl = CentralSyncModeCombo.SelectedIndex == 1 ? string.Empty : CentralSyncEndpointText.Text.Trim(),
            FileDropFolder = CentralSyncModeCombo.SelectedIndex == 1 ? CentralSyncEndpointText.Text.Trim() : string.Empty,
            StationId = CentralSyncStationIdText.Text.Trim(),
            SyncIntervalSeconds = int.TryParse(
                CentralSyncIntervalText.Text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var interval)
                ? interval
                : 300,
            MaxRetryCount = int.TryParse(
                CentralSyncMaxRetryText.Text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var maxRetry)
                ? maxRetry
                : 5,
            IncludeImages = CentralSyncIncludeImagesCheck.IsChecked == true,
            RedactOperatorId = CentralSyncRedactOperatorCheck.IsChecked == true,
            RedactImagePaths = CentralSyncRedactImagePathsCheck.IsChecked == true,
            RedactEndpointInExports = CentralSyncRedactEndpointCheck.IsChecked == true,
            SharedSecret = CentralSyncSecretBox.Password,
        };

    private InspectionModelConfiguration BuildConfigurationFromUi()
    {
        var modelPath = ModelPathText.Text.Trim();
        var version = string.IsNullOrWhiteSpace(ModelVersionText.Text)
            ? string.Empty
            : ModelVersionText.Text.Trim();

        if (string.IsNullOrWhiteSpace(version))
        {
            version = string.IsNullOrWhiteSpace(modelPath)
            ? "UNCONFIGURED"
            : Path.GetFileNameWithoutExtension(modelPath);
        }

        return new InspectionModelConfiguration
        {
            SelectedEngineKey = InspectionEngineCombo.SelectedIndex switch
            {
                1 => InspectionEngineFactory.OnnxEngineKey,
                2 => InspectionEngineFactory.LearnedVisualEngineKey,
                _ => InspectionEngineFactory.DefaultEngineKey,
            },
            ModelFilePath = modelPath,
            ModelVersion = version,
            LabelMapPath = LabelMapPathText.Text.Trim(),
            InputImageWidth = int.TryParse(
                InputWidthText.Text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var inputWidth)
                ? Math.Clamp(inputWidth, 32, 8192)
                : 640,
            InputImageHeight = int.TryParse(
                InputHeightText.Text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var inputHeight)
                ? Math.Clamp(inputHeight, 32, 8192)
                : 640,
            InputTensorName = InputTensorNameText.Text.Trim(),
            OutputTensorName = OutputTensorNameText.Text.Trim(),
            ConfidenceThreshold = double.TryParse(
                ConfidenceThresholdText.Text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var threshold)
                ? Math.Clamp(threshold, 0.0, 1.0)
                : 0.65,
        };
    }

    private ModelRegistrationRequest BuildModelRegistrationRequestFromUi()
    {
        var configuration = BuildConfigurationFromUi();
        return new ModelRegistrationRequest
        {
            ModelFilePath = configuration.ModelFilePath,
            LabelMapPath = configuration.LabelMapPath,
            DisplayName = string.IsNullOrWhiteSpace(configuration.ModelFilePath)
                ? configuration.ModelVersion
                : Path.GetFileNameWithoutExtension(configuration.ModelFilePath),
            Version = configuration.ModelVersion,
            InputTensorName = configuration.InputTensorName,
            OutputTensorName = configuration.OutputTensorName,
            InputWidth = configuration.InputImageWidth,
            InputHeight = configuration.InputImageHeight,
            ConfidenceThreshold = configuration.ConfidenceThreshold,
            Notes = "Registered from Settings. No training is performed by AOI Monitor.",
        };
    }

    private static Brush StatusBrush(InspectionEngineStatus status)
    {
        var color = status switch
        {
            InspectionEngineStatus.MlModelReady => "#50F56E",
            InspectionEngineStatus.MlModelMissing => "#E1A334",
            InspectionEngineStatus.MlModelNotTested => "#E1A334",
            InspectionEngineStatus.MlInvalidLabelMap => "#F27777",
            InspectionEngineStatus.MlRuntimeError => "#F27777",
            InspectionEngineStatus.MlUnsupportedOutputFormat => "#F27777",
            InspectionEngineStatus.LearnedVisualModelReady => "#C084FC",
            InspectionEngineStatus.LearnedVisualModelMissing => "#E1A334",
            _ => "#E1A334",
        };

        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    }

    private static Brush StatusBrush(ModelConfigurationTestStatus status)
    {
        var color = status switch
        {
            ModelConfigurationTestStatus.Ready => "#50F56E",
            ModelConfigurationTestStatus.MissingModel => "#E1A334",
            ModelConfigurationTestStatus.InvalidLabelMap => "#F27777",
            ModelConfigurationTestStatus.RuntimeError => "#F27777",
            ModelConfigurationTestStatus.UnsupportedOutputFormat => "#F27777",
            _ => "#E1A334",
        };

        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    }

    private static Brush StatusBrush(CameraSourceStatus status)
    {
        var color = status switch
        {
            CameraSourceStatus.Ready => "#50F56E",
            CameraSourceStatus.Simulated => "#E1A334",
            CameraSourceStatus.Error => "#F27777",
            _ => "#F27777",
        };

        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    }

    private static string CameraStatusDisplay(CameraSourceStatus status)
        => status switch
        {
            CameraSourceStatus.Ready => "Connected",
            CameraSourceStatus.Simulated => "Simulated",
            CameraSourceStatus.Error => "Error",
            _ => "Not Connected",
        };

    private static Brush IntegrationStatusBrush(IntegrationConnectionStatus status)
    {
        var color = status switch
        {
            IntegrationConnectionStatus.Ready => "#50F56E",
            IntegrationConnectionStatus.Simulated => "#E1A334",
            IntegrationConnectionStatus.Error => "#F27777",
            _ => "#F27777",
        };

        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    }

    private static string IntegrationStatusDisplay(IntegrationConnectionStatus status)
        => status switch
        {
            IntegrationConnectionStatus.Ready => "Ready",
            IntegrationConnectionStatus.Simulated => "Simulated",
            IntegrationConnectionStatus.Error => "Error",
            _ => "Not Connected",
        };

    private string? ValidateRawModelCheckFields()
    {
        if (!int.TryParse(InputWidthText.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) ||
            !int.TryParse(InputHeightText.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var height) ||
            width < 32 ||
            width > 8192 ||
            height < 32 ||
            height > 8192)
        {
            return "Input width and height must be whole numbers between 32 and 8192.";
        }

        if (!double.TryParse(ConfidenceThresholdText.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold) ||
            threshold < 0 ||
            threshold > 1)
        {
            return "Confidence threshold must be a number between 0 and 1.";
        }

        return null;
    }

    private static bool HasAdminOnlyModelConfigurationChange(
        InspectionModelConfiguration existing,
        InspectionModelConfiguration candidate)
        => !string.Equals(existing.SelectedEngineKey, candidate.SelectedEngineKey, StringComparison.OrdinalIgnoreCase) ||
           !string.Equals(existing.ModelFilePath, candidate.ModelFilePath, StringComparison.OrdinalIgnoreCase) ||
           !string.Equals(existing.ModelVersion, candidate.ModelVersion, StringComparison.OrdinalIgnoreCase) ||
           !string.Equals(existing.LabelMapPath, candidate.LabelMapPath, StringComparison.OrdinalIgnoreCase) ||
           !string.Equals(existing.InputTensorName, candidate.InputTensorName, StringComparison.OrdinalIgnoreCase) ||
           !string.Equals(existing.OutputTensorName, candidate.OutputTensorName, StringComparison.OrdinalIgnoreCase) ||
           existing.InputImageWidth != candidate.InputImageWidth ||
           existing.InputImageHeight != candidate.InputImageHeight;

    private void ApplyLanguageVisuals()
    {
        _isKorean = LangCombo.SelectedIndex == 1;
        var culture = _isKorean ? new CultureInfo("ko-KR") : new CultureInfo("en-US");

        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;

        if (Application.Current.MainWindow is Window mainWindow)
        {
            mainWindow.Language = XmlLanguage.GetLanguage(culture.IetfLanguageTag);
            mainWindow.FontFamily = _isKorean ? new FontFamily("Malgun Gothic, Segoe UI") : new FontFamily("Segoe UI");
        }

        var languageFont = _isKorean ? new FontFamily("Malgun Gothic, Segoe UI") : new FontFamily("Segoe UI");
        LangCombo.FontFamily = languageFont;
        FontCombo.FontFamily = languageFont;
        ResolutionCombo.FontFamily = languageFont;
        ThemeCombo.FontFamily = languageFont;
        DetectionPriorityCombo.FontFamily = languageFont;
        InspectionEngineCombo.FontFamily = languageFont;
        MesModeCombo.FontFamily = languageFont;

        DisplayLanguageHeaderText.Text = TextFor("Display / Language", "\uD654\uBA74 / \uC5B8\uC5B4");
        LanguageLabelText.Text = TextFor("Language", "\uC5B8\uC5B4");
        ResolutionLabelText.Text = TextFor("Resolution", "\uD574\uC0C1\uB3C4");
        ThemeLabelText.Text = TextFor("Theme", "\uD14C\uB9C8");
        FontSizeLabelText.Text = TextFor("Font Size", "\uAE00\uC790 \uD06C\uAE30");
        ProgramAssetsHeaderText.Text = TextFor("Program Assets", "\uD504\uB85C\uADF8\uB7A8 \uC790\uC0B0");
        ConsoleTitleLabelText.Text = TextFor("Console Title", "\uCF58\uC194 \uC81C\uBAA9");
        StationLabelText.Text = TextFor("Station Label", "\uC2A4\uD14C\uC774\uC158 \uB77C\uBCA8");
        StationSubtitleLabelText.Text = TextFor("Station Subtitle", "\uC2A4\uD14C\uC774\uC158 \uC124\uBA85");
        AccentColorLabelText.Text = TextFor("Accent Color", "\uAC15\uC870 \uC0C9\uC0C1");
        LogoImageLabelText.Text = TextFor("Logo Image", "\uB85C\uACE0 \uC774\uBBF8\uC9C0");
        BrowseBrandLogoBtn.Content = TextFor("Browse", "\uCC3E\uC544\uBCF4\uAE30");
        ProgramAssetsPolicyText.Text = TextFor(
            "Changes are staged until Apply. Cancel discards edits and restores the last saved asset settings.",
            "\uBCC0\uACBD\uC0AC\uD56D\uC740 \uC801\uC6A9\uC744 \uB204\uB974\uAE30 \uC804\uAE4C\uC9C0 \uB300\uAE30\uB429\uB2C8\uB2E4. \uCDE8\uC18C\uB294 \uD3B8\uC9D1\uC744 \uBC84\uB9AC\uACE0 \uB9C8\uC9C0\uB9C9\uC73C\uB85C \uC800\uC7A5\uB41C \uC790\uC0B0 \uC124\uC815\uC744 \uBCF5\uC6D0\uD569\uB2C8\uB2E4.");
        StoragePathLabelText.Text = TextFor("Storage Path", "\uC800\uC7A5 \uACBD\uB85C");
        ReviewDefaultLabelText.Text = TextFor("Review Default", "\uAC80\uD1A0 \uAE30\uBCF8\uAC12");
        DetectionPriorityLabelText.Text = TextFor("Detection Priority", "\uAC80\uCD9C \uC6B0\uC120\uC21C\uC704");
        RunSetupWizardBtn.Content = TextFor("Run Setup Wizard Again", "\uC124\uCE58 \uB9C8\uBC95\uC0AC \uB2E4\uC2DC \uC2E4\uD589");
        OpenInstallNotesBtn.Content = TextFor("Installation Notes", "\uC124\uCE58 \uB178\uD2B8");
        OpenGuideBtn.Content = TextFor("Open Guide", "\uAC00\uC774\uB4DC \uC5F4\uAE30");
        ExportDiagnosticsBtn.Content = TextFor("Export Diagnostics Report", "\uC9C4\uB2E8 \uBCF4\uACE0\uC11C \uB0B4\uBCF4\uB0B4\uAE30");
        BackupConfigurationBtn.Content = TextFor("Backup Configuration", "\uAD6C\uC131 \uBC31\uC5C5");
        RestoreConfigurationPreviewBtn.Content = TextFor("Restore Configuration Preview", "\uAD6C\uC131 \uBCF5\uC6D0 \uBBF8\uB9AC\uBCF4\uAE30");
        ApplyRestoreBtn.Content = TextFor("Apply Restore", "\uBCF5\uC6D0 \uC801\uC6A9");
        RollbackRestoreBtn.Content = TextFor("Rollback Last Restore", "\uB9C8\uC9C0\uB9C9 \uBCF5\uC6D0 \uB864\uBC31");
        ExportSupportBundleBtn.Content = TextFor("Export Support Bundle", "\uC9C0\uC6D0 \uBC88\uB4E4 \uB0B4\uBCF4\uB0B4\uAE30");
        ApplyBtn.Content = TextFor("Apply", "\uC801\uC6A9");
        CancelBtn.Content = TextFor("Cancel", "\uCDE8\uC18C");
        ResetBtn.Content = TextFor("Reset", "\uCD08\uAE30\uD654");
        var localizationScope = TextFor(
            "Language/font changes are applied after pressing Apply. Supported shell, page, table, and Settings labels update immediately; technical codes and evidence IDs remain English for traceability.",
            "\uC5B8\uC5B4/\uAE00\uAF34 \uC124\uC815\uC740 \uC801\uC6A9\uC744 \uB204\uB978 \uB4A4 \uBC18\uC601\uB429\uB2C8\uB2E4. \uC9C0\uC6D0\uB418\uB294 \uC178, \uD398\uC774\uC9C0, \uD45C, \uC124\uC815 \uB77C\uBCA8\uC740 \uC989\uC2DC \uBC14\uB00C\uBA70, \uAE30\uC220 \uCF54\uB4DC\uC640 \uC99D\uBE59 ID\uB294 \uCD94\uC801\uC131\uC744 \uC704\uD574 \uC601\uC5B4\uB85C \uC720\uC9C0\uB429\uB2C8\uB2E4.");
        LocalizationStatusText.Text = localizationScope;
        LocalizationScopeText.Text = localizationScope;

        SetComboItemText(LangCombo, 0, "English");
        SetComboItemText(LangCombo, 1, TextFor("Korean", "\uD55C\uAD6D\uC5B4"));

        if (_isKorean)
        {
            SetComboItemText(FontCombo, 0, "\uAE30\uBCF8 14px");
            SetComboItemText(FontCombo, 1, "\uD45C\uC900 15px");
            SetComboItemText(FontCombo, 2, "\uD06C\uAC8C 17px");
            SetComboItemText(ResolutionCombo, 0, "1920 x 1080 \uCD5C\uC18C HMI");
            SetComboItemText(ResolutionCombo, 1, "2560 x 1440 \uC5D4\uC9C0\uB2C8\uC5B4\uB9C1 \uBAA8\uB2C8\uD130");
            SetComboItemText(ResolutionCombo, 2, "3840 x 2160 \uBCBD\uBA74 \uD45C\uC2DC");
            SetComboItemText(ThemeCombo, 0, "\uC0B0\uC5C5\uC6A9 \uB2E4\uD06C");
            SetComboItemText(ThemeCombo, 1, "\uC0B0\uC5C5\uC6A9 \uB77C\uC774\uD2B8");

            SetComboItemText(DetectionPriorityCombo, 0, DetectionPriorityDisplay(Models.DetectionPriority.MinimizeFalsePositives, true));
            SetComboItemText(DetectionPriorityCombo, 1, DetectionPriorityDisplay(Models.DetectionPriority.Balanced, true));
            SetComboItemText(DetectionPriorityCombo, 2, DetectionPriorityDisplay(Models.DetectionPriority.MaximizeDefectRecall, true));
        }
        else
        {
            SetComboItemText(FontCombo, 0, "Compact 14 px base");
            SetComboItemText(FontCombo, 1, "Standard 15 px base");
            SetComboItemText(FontCombo, 2, "Large 17 px base");
            SetComboItemText(ResolutionCombo, 0, "1920 x 1080 minimum HMI");
            SetComboItemText(ResolutionCombo, 1, "2560 x 1440 engineering monitor");
            SetComboItemText(ResolutionCombo, 2, "3840 x 2160 wall display");
            SetComboItemText(ThemeCombo, 0, "Industrial Dark");
            SetComboItemText(ThemeCombo, 1, "Industrial Light");

            SetComboItemText(DetectionPriorityCombo, 0, "Minimize False Positives");
            SetComboItemText(DetectionPriorityCombo, 1, "Balanced");
            SetComboItemText(DetectionPriorityCombo, 2, "Maximize Defect Recall");
        }

        ReviewDefaultText.Text = DetectionPriorityDisplay(ComboToPriority(DetectionPriorityCombo.SelectedIndex), _isKorean);
    }

    private string BuildSettingsAppliedMessage(string workflowMessage)
    {
        var languageMessage = TextFor(
            "Language/font settings applied. Settings and supported runtime text update immediately; some workstation labels and technical codes remain English for traceability.",
            "\uC5B8\uC5B4/\uAE00\uAF34 \uC124\uC815\uC774 \uC801\uC6A9\uB418\uC5C8\uC2B5\uB2C8\uB2E4. \uC124\uC815 \uD654\uBA74\uACFC \uC9C0\uC6D0\uB418\uB294 \uC2E4\uD589 \uD14D\uC2A4\uD2B8\uB294 \uC989\uC2DC \uBC18\uC601\uB418\uBA70, \uC77C\uBD80 \uC791\uC5C5\uC790 \uD654\uBA74 \uB77C\uBCA8\uACFC \uAE30\uC220 \uCF54\uB4DC\uB294 \uCD94\uC801\uC131\uC744 \uC704\uD574 \uC601\uC5B4\uB85C \uC720\uC9C0\uB429\uB2C8\uB2E4.");
        var title = TextFor("Display settings applied.", "\uD654\uBA74 \uC124\uC815\uC774 \uC801\uC6A9\uB418\uC5C8\uC2B5\uB2C8\uB2E4.");
        return $"{title}\n{workflowMessage}\n\n{languageMessage}";
    }

    private void ApplyFontPreset()
    {
        UiPreferencesService.ApplyToApplication(BuildUiPreferencesFromUi());
    }

    private void LoadUiPreferenceSelection()
    {
        var preferences = UiPreferencesService.Load();
        LangCombo.SelectedIndex = preferences.Language == UiLanguage.Korean ? 1 : 0;
        FontCombo.SelectedIndex = preferences.FontPreset switch
        {
            UiFontPreset.Compact => 0,
            UiFontPreset.Large => 2,
            _ => 1,
        };
        ResolutionCombo.SelectedIndex = preferences.ResolutionPreset switch
        {
            UiResolutionPreset.Qhd2560x1440 => 1,
            UiResolutionPreset.Uhd3840x2160 => 2,
            _ => 0,
        };
        ThemeCombo.SelectedIndex = preferences.Theme == UiTheme.IndustrialLight ? 1 : 0;
        ConsoleTitleText.Text = preferences.ConsoleTitle;
        StationNameText.Text = preferences.StationDisplayName;
        StationSubtitleText.Text = preferences.StationSubtitle;
        AccentColorText.Text = preferences.AccentColor;
        BrandLogoPathText.Text = preferences.BrandLogoPath;
    }

    private void SaveUiPreferenceSelection()
        => UiPreferencesService.Save(BuildUiPreferencesFromUi());

    private UiPreferences BuildUiPreferencesFromUi()
        => new()
        {
            Language = LangCombo.SelectedIndex == 1 ? UiLanguage.Korean : UiLanguage.English,
            FontPreset = FontCombo.SelectedIndex switch
            {
                0 => UiFontPreset.Compact,
                2 => UiFontPreset.Large,
                _ => UiFontPreset.Standard,
            },
            ResolutionPreset = ResolutionCombo.SelectedIndex switch
            {
                1 => UiResolutionPreset.Qhd2560x1440,
                2 => UiResolutionPreset.Uhd3840x2160,
                _ => UiResolutionPreset.FullHd1920x1080,
            },
            Theme = ThemeCombo.SelectedIndex == 1 ? UiTheme.IndustrialLight : UiTheme.IndustrialDark,
            ConsoleTitle = ConsoleTitleText.Text,
            StationDisplayName = StationNameText.Text,
            StationSubtitle = StationSubtitleText.Text,
            AccentColor = AccentColorText.Text,
            BrandLogoPath = BrandLogoPathText.Text,
        };

    private static void SetComboItemText(ComboBox comboBox, int index, string text)
    {
        if (index < 0 || index >= comboBox.Items.Count)
            return;

        if (comboBox.Items[index] is ComboBoxItem item)
            item.Content = text;
    }

    private string TextFor(string english, string korean) => _isKorean ? korean : english;

    private static bool Authorize(Func<UserRole, bool> permission, string action)
    {
        if (WorkflowState.Instance.TryAuthorize(permission, action, out var message))
            return true;

        MessageBox.Show(message, "Permission Denied", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private static string DetectionPriorityDisplay(Models.DetectionPriority priority, bool isKorean)
    {
        if (!isKorean)
            return WorkflowState.ToDisplay(priority);

        return priority switch
        {
            Models.DetectionPriority.MinimizeFalsePositives => "\uC624\uAC80\uCD9C \uCD5C\uC18C\uD654",
            Models.DetectionPriority.Balanced => "\uADE0\uD615",
            Models.DetectionPriority.MaximizeDefectRecall => "\uACB0\uD568 \uAC80\uCD9C \uCD5C\uB300\uD654",
            _ => "\uADE0\uD615",
        };
    }

    private static Models.DetectionPriority ComboToPriority(int selectedIndex) => selectedIndex switch
    {
        0 => Models.DetectionPriority.MinimizeFalsePositives,
        1 => Models.DetectionPriority.Balanced,
        2 => Models.DetectionPriority.MaximizeDefectRecall,
        _ => Models.DetectionPriority.MinimizeFalsePositives,
    };

    private static DeploymentProfile ComboToDeploymentProfile(int selectedIndex) => selectedIndex switch
    {
        0 => DeploymentProfile.Stage1ImageValidation,
        1 => DeploymentProfile.Stage2CameraPilot,
        2 => DeploymentProfile.Stage3RobotPilot,
        3 => DeploymentProfile.Stage4MesPilot,
        4 => DeploymentProfile.FullFactoryAutomation,
        _ => DeploymentProfile.Stage1ImageValidation,
    };

    private static int DeploymentProfileToCombo(DeploymentProfile profile) => profile switch
    {
        DeploymentProfile.Stage1ImageValidation => 0,
        DeploymentProfile.Stage2CameraPilot => 1,
        DeploymentProfile.Stage3RobotPilot => 2,
        DeploymentProfile.Stage4MesPilot => 3,
        DeploymentProfile.FullFactoryAutomation => 4,
        _ => 0,
    };

    private static OperatingMode ComboToOperatingMode(int selectedIndex) => selectedIndex switch
    {
        1 => OperatingMode.Pilot,
        2 => OperatingMode.Production,
        _ => OperatingMode.Demo,
    };

    private static int OperatingModeToCombo(OperatingMode mode) => mode switch
    {
        OperatingMode.Pilot => 1,
        OperatingMode.Production => 2,
        _ => 0,
    };

    private static string PromptForText(string title, string label)
    {
        var input = new TextBox
        {
            Margin = new Thickness(0, 6, 0, 10),
            MinWidth = 360,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinLines = 3,
            MaxLines = 5,
        };
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
                    input,
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
        return dialog.ShowDialog() == true ? input.Text.Trim() : string.Empty;
    }

    private static string BuildRestorePreviewMessage(ConfigurationRestorePreview preview)
    {
        var lines = new List<string>
        {
            preview.Summary,
            $"Schema: {preview.SchemaVersion}",
            $"Database schema: {preview.DatabaseSchemaVersion}",
            $"Target storage path: {preview.TargetStoragePath}",
            $"Settings: {string.Join(", ", preview.SettingsKeys.DefaultIfEmpty("--"))}",
        };

        AddPreviewSection(lines, "Settings that will change:", preview.SettingsChanges);
        AddPreviewSection(lines, "Conflicts:", preview.Conflicts);
        AddPreviewSection(lines, "Missing model files:", preview.MissingModelFiles);
        AddPreviewSection(lines, "Missing plugin folders:", preview.MissingPluginFolders);

        if (preview.Warnings.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Warnings:");
            lines.AddRange(preview.Warnings.Select(warning => $"- {warning}"));
        }

        if (preview.SecretProtectionStatus.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Secret protection:");
            lines.AddRange(preview.SecretProtectionStatus.Select(status => $"- {status}"));
        }

        if (preview.BlockingIssues.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Blocking issues:");
            lines.AddRange(preview.BlockingIssues.Select(issue => $"- {issue}"));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static void AddPreviewSection(List<string> lines, string title, IReadOnlyCollection<string> items)
    {
        if (items.Count == 0)
            return;

        lines.Add(string.Empty);
        lines.Add(title);
        lines.AddRange(items.Select(item => $"- {item}"));
    }

    private sealed class ModelRegistryRow
    {
        public ModelRegistryRow(ModelRegistryEntry entry)
        {
            ModelId = entry.ModelId;
            DisplayName = entry.DisplayName;
            Version = entry.Version;
            ValidationStatus = ModelConfigurationValidator.ToDisplay(entry.ValidationStatus);
            LifecycleState = entry.LifecycleState.ToString();
            LatestAcceptanceStatus = string.IsNullOrWhiteSpace(entry.LatestAcceptanceStatus) ? "--" : entry.LatestAcceptanceStatus;
            LatestAcceptanceRunDisplay = entry.LatestAcceptanceRunId?.ToString(CultureInfo.InvariantCulture) ?? "--";
            ReleasePackageDisplay = string.IsNullOrWhiteSpace(entry.LatestReleasePackagePath)
                ? "--"
                : $"{entry.LatestReleasePackagePath} (#{entry.LatestReleasePackageId?.ToString(CultureInfo.InvariantCulture) ?? "--"})";
            WaiverDisplay = string.IsNullOrWhiteSpace(entry.DeploymentWaiverReason)
                ? "--"
                : $"{entry.DeploymentWaiverRiskClassification}; expires {entry.WaiverExpiresAtUtc?.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "--"}";
            ThresholdDisplay = entry.ConfidenceThreshold.ToString("P0", CultureInfo.InvariantCulture);
            LastValidatedDisplay = entry.LastValidatedAtUtc is { } timestamp
                ? timestamp.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture)
                : "--";
            ActiveDisplay = entry.IsActive ? "Yes" : string.Empty;
        }

        public string ModelId { get; }
        public string DisplayName { get; }
        public string Version { get; }
        public string ValidationStatus { get; }
        public string LifecycleState { get; }
        public string LatestAcceptanceStatus { get; }
        public string LatestAcceptanceRunDisplay { get; }
        public string ReleasePackageDisplay { get; }
        public string WaiverDisplay { get; }
        public string ThresholdDisplay { get; }
        public string LastValidatedDisplay { get; }
        public string ActiveDisplay { get; }
    }

    private sealed class LearnedVisualModelRow
    {
        public LearnedVisualModelRow(LearnedVisualModelRegistryEntry entry)
        {
            ModelId = entry.ModelId;
            ModelVersion = entry.ModelVersion;
            ProjectName = string.IsNullOrWhiteSpace(entry.ProjectName) ? entry.ProjectId : entry.ProjectName;
            BoardModel = string.IsNullOrWhiteSpace(entry.BoardModel) ? "--" : entry.BoardModel;
            ImagesLearnedFrom = entry.ImagesLearnedFrom.ToString(CultureInfo.InvariantCulture);
            OkValidationCount = entry.OkValidationCount.ToString(CultureInfo.InvariantCulture);
            FalseCallTargetDisplay = entry.FalseCallTargetDisplay;
            RecommendedThresholdDisplay = entry.RecommendedThresholdDisplay;
            EvidenceMode = entry.EvidenceMode;
            ArtifactStatus = entry.ArtifactStatus;
            ActiveDisplay = entry.ActiveDisplay;
        }

        public string ModelId { get; }
        public string ModelVersion { get; }
        public string ProjectName { get; }
        public string BoardModel { get; }
        public string ImagesLearnedFrom { get; }
        public string OkValidationCount { get; }
        public string FalseCallTargetDisplay { get; }
        public string RecommendedThresholdDisplay { get; }
        public string EvidenceMode { get; }
        public string ArtifactStatus { get; }
        public string ActiveDisplay { get; }
    }

    private sealed class ModelAcceptanceRunRow
    {
        public ModelAcceptanceRunRow(ModelAcceptanceRun run)
        {
            Id = run.Id;
            ModelId = string.IsNullOrWhiteSpace(run.ModelId) ? "--" : run.ModelId;
            Status = run.Status;
            DatasetName = run.DatasetName;
            CandidateDisplay = run.IsProductionCandidate ? "Yes" : "No";
        }

        public long Id { get; }
        public string ModelId { get; }
        public string Status { get; }
        public string DatasetName { get; }
        public string CandidateDisplay { get; }
    }

    private sealed class ThresholdProfileRow
    {
        public ThresholdProfileRow(ThresholdProfile profile)
        {
            ProfileId = profile.ProfileId;
            Revision = profile.Revision;
            BoardModel = profile.BoardModel;
            BoardProgram = profile.BoardProgram;
            RecipeName = profile.RecipeName;
            Status = profile.Status;
            RuleCount = profile.Rules.Count;
            CreatedBy = profile.CreatedBy;
            ApprovedBy = string.IsNullOrWhiteSpace(profile.ApprovedBy) ? "--" : profile.ApprovedBy;
            Deployed = string.Equals(profile.Status, "Deployed", StringComparison.OrdinalIgnoreCase) ? "Yes" : "No";
            SourceFalseCallReductionRunDisplay = profile.SourceFalseCallReductionRunId?.ToString(CultureInfo.InvariantCulture) ?? "--";
            SourceValidationRunDisplay = profile.SourceValidationRunId?.ToString(CultureInfo.InvariantCulture) ?? "--";
        }

        public string ProfileId { get; }
        public string Revision { get; }
        public string BoardModel { get; }
        public string BoardProgram { get; }
        public string RecipeName { get; }
        public string Status { get; }
        public int RuleCount { get; }
        public string CreatedBy { get; }
        public string ApprovedBy { get; }
        public string Deployed { get; }
        public string SourceFalseCallReductionRunDisplay { get; }
        public string SourceValidationRunDisplay { get; }
    }
}
