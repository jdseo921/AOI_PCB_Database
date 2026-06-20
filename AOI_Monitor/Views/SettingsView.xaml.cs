using System.Windows;
using System.Windows.Controls;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Text.Json;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.Win32;
using AOI_Monitor.Models;
using AOI_Monitor.Data;
using AOI_Monitor.Services;
using AOI_Monitor.ViewModels;

namespace AOI_Monitor.Views;

public partial class SettingsView : UserControl
{
    private readonly MainViewModel _vm;
    private readonly ObservableCollection<ModelRegistryRow> _modelRegistryRows = new();
    private bool _isKorean;

    public SettingsView(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        ModelRegistryGrid.ItemsSource = _modelRegistryRows;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        WorkflowState.Instance.StateChanged += OnWorkflowStateChanged;
        InspectionModelConfigurationService.ConfigurationChanged += OnInspectionConfigurationChanged;
        ModelRegistryService.RegistryChanged += OnModelRegistryChanged;
        CameraSourceSettingsService.SettingsChanged += OnCameraSourceSettingsChanged;
        LightingSettingsService.SettingsChanged += OnLightingSettingsChanged;
        MesIntegrationSettingsService.SettingsChanged += OnMesIntegrationSettingsChanged;
        RefreshWorkflowUi();
        ApplyLanguageVisuals();
        ApplyFontPreset();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        WorkflowState.Instance.StateChanged -= OnWorkflowStateChanged;
        InspectionModelConfigurationService.ConfigurationChanged -= OnInspectionConfigurationChanged;
        ModelRegistryService.RegistryChanged -= OnModelRegistryChanged;
        CameraSourceSettingsService.SettingsChanged -= OnCameraSourceSettingsChanged;
        LightingSettingsService.SettingsChanged -= OnLightingSettingsChanged;
        MesIntegrationSettingsService.SettingsChanged -= OnMesIntegrationSettingsChanged;
    }

    private void OnWorkflowStateChanged() => Dispatcher.Invoke(RefreshWorkflowUi);
    private void OnInspectionConfigurationChanged() => Dispatcher.Invoke(RefreshInspectionConfigurationUi);
    private void OnModelRegistryChanged() => Dispatcher.Invoke(RefreshModelRegistryUi);
    private void OnCameraSourceSettingsChanged() => Dispatcher.Invoke(RefreshCameraSourceUi);
    private void OnLightingSettingsChanged() => Dispatcher.Invoke(RefreshLightingUi);
    private void OnMesIntegrationSettingsChanged() => Dispatcher.Invoke(RefreshMesIntegrationUi);

    private void OnApply(object sender, RoutedEventArgs e)
    {
        var state = WorkflowState.Instance;
        var existingConfig = InspectionModelConfigurationService.Load();
        var newConfig = BuildConfigurationFromUi();
        var existingCamera = CameraSourceSettingsService.Load();
        var newCamera = BuildCameraSourceSettingsFromUi();
        var existingLighting = LightingSettingsService.Load();
        var newLighting = BuildLightingSettingsFromUi();
        var existingMes = MesIntegrationSettingsService.Load();
        var newMes = BuildMesIntegrationSettingsFromUi();
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
        var thresholdChanged =
            ComboToPriority(DetectionPriorityCombo.SelectedIndex) != state.DetectionPriority ||
            Math.Abs(existingConfig.ConfidenceThreshold - newConfig.ConfidenceThreshold) > 0.0001;

        if ((storageRootChanged || modelConfigChanged || cameraConfigChanged || lightingConfigChanged || mesConfigChanged) && !Authorize(RoleAuthorization.CanManageSettings, "Changing database/vault/model paths, selected model engine, camera source, lighting sync, or MES integration settings"))
            return;

        if (thresholdChanged && !Authorize(RoleAuthorization.CanChangeThresholds, "Changing inspection thresholds or detection priority"))
            return;

        ApplyLanguageVisuals();
        ApplyFontPreset();
        if (!ApplyStorageRoot(newStorageRoot, storageRootChanged))
            return;

        SaveInspectionConfiguration(newConfig);
        SaveCameraSourceSettings(newCamera);
        SaveLightingSettings(newLighting);
        SaveMesIntegrationSettings(newMes);

        if (!state.TrySetDetectionPriority(ComboToPriority(DetectionPriorityCombo.SelectedIndex), out var message))
        {
            MessageBox.Show($"Display settings applied.\n{message}", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
            RefreshWorkflowUi();
            return;
        }

        RefreshWorkflowUi();
        MessageBox.Show($"Display settings applied.\n{message}", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Resetting local settings"))
            return;

        LangCombo.SelectedIndex = 0;
        FontCombo.SelectedIndex = 1;
        DetectionPriorityCombo.SelectedIndex = 0;
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

        ApplyLanguageVisuals();
        ApplyFontPreset();

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

    private void OnRegisterModelClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Registering local ONNX model"))
            return;

        try
        {
            var request = BuildModelRegistrationRequestFromUi();
            var entry = ModelRegistryService.Register(request);
            RefreshModelRegistryUi();
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
            var result = ModelRegistryService.Validate(row.ModelId);
            RefreshInspectionConfigurationUi(InspectionModelConfigurationService.Load());
            RefreshModelRegistryUi();
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

        if (!ModelRegistryService.SetActiveModel(row.ModelId))
        {
            MessageBox.Show("The selected model registry entry could not be found.", "Model Registry", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        RefreshInspectionConfigurationUi(InspectionModelConfigurationService.Load());
        RefreshModelRegistryUi();
        WorkflowState.Instance.AddEvent("MODEL_DEPLOYMENT", $"Active model set to {row.ModelId}. ONNX inference remains gated by validation status.");
        MessageBox.Show(
            $"Active model set.\n\nModel ID: {row.ModelId}\nRun Validate before using it for accepted ONNX inference.",
            "Model Registry",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
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
        TrainingStatusText.Text = state.Training.IsRunning ? "RUNNING" : "IDLE";
        TrainingQueueText.Text = state.Training.QueuedSamples.ToString();
        TrainingEpochText.Text = state.Training.EpochsCompleted.ToString();
        TrainingValidationText.Text = state.Training.LastCompletedAt is null
            ? "--"
            : $"{state.Training.LastValidationScore:F1}%";
        StorageRootText.Text = AoiDatabase.StorageRoot;

        RefreshRoleControls();
        RefreshInspectionConfigurationUi();
        RefreshModelRegistryUi();
        RefreshCameraSourceUi();
        RefreshLightingUi();
        RefreshMesIntegrationUi();
    }

    private void RefreshRoleControls()
    {
        var role = WorkflowState.Instance.CurrentRole;
        var canManageSettings = RoleAuthorization.CanManageSettings(role);
        var canChangeThresholds = RoleAuthorization.CanChangeThresholds(role);

        DetectionPriorityCombo.IsEnabled = canChangeThresholds;
        InspectionEngineCombo.IsEnabled = canManageSettings;
        CameraSourceCombo.IsEnabled = canManageSettings;
        CameraTopFolderText.IsEnabled = canManageSettings;
        CameraSideFolderText.IsEnabled = canManageSettings;
        CameraBottomFolderText.IsEnabled = canManageSettings;
        CameraTopDeviceIdText.IsEnabled = canManageSettings;
        CameraSideDeviceIdText.IsEnabled = canManageSettings;
        CameraBottomDeviceIdText.IsEnabled = canManageSettings;
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
        BrowseCameraTopBtn.IsEnabled = canManageSettings;
        BrowseCameraSideBtn.IsEnabled = canManageSettings;
        BrowseCameraBottomBtn.IsEnabled = canManageSettings;
        TestCameraSourceBtn.IsEnabled = canManageSettings;
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
        RunSetupWizardBtn.IsEnabled = canManageSettings;
        ExportDiagnosticsBtn.IsEnabled = canManageSettings;
        ConfidenceThresholdText.IsEnabled = canChangeThresholds;
        TestModelBtn.IsEnabled = RoleAuthorization.CanTestModelConfiguration(role);
        ValidateRegisteredModelBtn.IsEnabled = RoleAuthorization.CanTestModelConfiguration(role);
    }

    private void RefreshInspectionConfigurationUi()
        => RefreshInspectionConfigurationUi(InspectionModelConfigurationService.Load());

    private void RefreshInspectionConfigurationUi(InspectionModelConfiguration configuration)
    {
        InspectionEngineCombo.SelectedIndex = configuration.IsOnnxSelected ? 1 : 0;
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
        EngineVersionText.Text = configuration.IsOnnxSelected
            ? configuration.EffectiveModelVersion
            : "PIXEL_DIFF_0.1";
        ModelCheckResultText.Text = ModelConfigurationValidator.ToDisplay(configuration.LastModelCheckResult);
        ModelCheckResultText.Foreground = StatusBrush(configuration.LastModelCheckResult);
        ModelCheckTimestampText.Text = configuration.LastModelCheckTimestampUtc is { } timestamp
            ? timestamp.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture)
            : "--";
        ModelCheckMessageText.Text = configuration.LastModelCheckMessage;
    }

    private void RefreshModelRegistryUi()
    {
        var selectedModelId = (ModelRegistryGrid.SelectedItem as ModelRegistryRow)?.ModelId;
        _modelRegistryRows.Clear();

        try
        {
            foreach (var model in ModelRegistryService.GetModels())
                _modelRegistryRows.Add(new ModelRegistryRow(model));

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
            configuration.IsOnnxSelected
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
        if (numericError is not null)
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
        CameraDiagnosticsText.Text = source.StatusMessage;
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
        LightingDiagnosticsText.Text = validation.Count == 0
            ? controller.StatusMessage
            : string.Join(" ", validation);
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

        var controller = LightingControllerFactory.Create(settings);
        var result = await LightingSynchronizationService.SynchronizeAsync(controller, settings, "Top");
        LightingDiagnosticsText.Text = $"{result.Message} Status={result.Status}.";
        LightingSettingsStatusText.Text = $"Lighting: {IntegrationStatusDisplay(result.Status)}";
        LightingSettingsStatusText.Foreground = IntegrationStatusBrush(result.Status);
        MessageBox.Show(
            LightingDiagnosticsText.Text,
            "Lighting Test",
            MessageBoxButton.OK,
            result.Status is IntegrationConnectionStatus.Ready or IntegrationConnectionStatus.Simulated ? MessageBoxImage.Information : MessageBoxImage.Warning);
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

        MesIntegrationSettingsService.Save(settings);
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

        try
        {
            var outcome = await TraceabilityUploadService.UploadAsync(payload);
            MesDiagnosticsText.Text = $"{outcome.Result.Message} Payload={outcome.PayloadPath}";
            MessageBox.Show(
                MesDiagnosticsText.Text,
                "MES Integration Test",
                MessageBoxButton.OK,
                outcome.Result.Accepted ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MesDiagnosticsText.Text = $"MES test failed safely: {ex.Message}";
            MessageBox.Show(MesDiagnosticsText.Text, "MES Integration Test", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
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
            SelectedEngineKey = InspectionEngineCombo.SelectedIndex == 1
                ? InspectionEngineFactory.OnnxEngineKey
                : InspectionEngineFactory.DefaultEngineKey,
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
        DetectionPriorityCombo.FontFamily = languageFont;
        InspectionEngineCombo.FontFamily = languageFont;
        MesModeCombo.FontFamily = languageFont;

        DisplayLanguageHeaderText.Text = TextFor("Display / Language", "\uD654\uBA74 / \uC5B8\uC5B4");
        LanguageLabelText.Text = TextFor("Language", "\uC5B8\uC5B4");
        FontSizeLabelText.Text = TextFor("Font Size", "\uAE00\uC790 \uD06C\uAE30");
        StoragePathLabelText.Text = TextFor("Storage Path", "\uC800\uC7A5 \uACBD\uB85C");
        ReviewDefaultLabelText.Text = TextFor("Review Default", "\uAC80\uD1A0 \uAE30\uBCF8\uAC12");
        DetectionPriorityLabelText.Text = TextFor("Detection Priority", "\uAC80\uCD9C \uC6B0\uC120\uC21C\uC704");
        ApplyBtn.Content = TextFor("Apply", "\uC801\uC6A9");
        ResetBtn.Content = TextFor("Reset", "\uCD08\uAE30\uD654");

        SetComboItemText(LangCombo, 0, "English");
        SetComboItemText(LangCombo, 1, TextFor("Korean", "\uD55C\uAD6D\uC5B4"));

        if (_isKorean)
        {
            SetComboItemText(FontCombo, 0, "\uC791\uAC8C");
            SetComboItemText(FontCombo, 1, "\uAE30\uBCF8");
            SetComboItemText(FontCombo, 2, "\uD06C\uAC8C");

            SetComboItemText(DetectionPriorityCombo, 0, DetectionPriorityDisplay(Models.DetectionPriority.MinimizeFalsePositives, true));
            SetComboItemText(DetectionPriorityCombo, 1, DetectionPriorityDisplay(Models.DetectionPriority.Balanced, true));
            SetComboItemText(DetectionPriorityCombo, 2, DetectionPriorityDisplay(Models.DetectionPriority.MaximizeDefectRecall, true));
        }
        else
        {
            SetComboItemText(FontCombo, 0, "Compact");
            SetComboItemText(FontCombo, 1, "Standard");
            SetComboItemText(FontCombo, 2, "Large");

            SetComboItemText(DetectionPriorityCombo, 0, "Minimize False Positives");
            SetComboItemText(DetectionPriorityCombo, 1, "Balanced");
            SetComboItemText(DetectionPriorityCombo, 2, "Maximize Defect Recall");
        }

        ReviewDefaultText.Text = DetectionPriorityDisplay(ComboToPriority(DetectionPriorityCombo.SelectedIndex), _isKorean);
    }

    private void ApplyFontPreset()
    {
        if (Application.Current.MainWindow is not Window mainWindow)
            return;

        var scale = FontCombo.SelectedIndex switch
        {
            0 => 0.92,
            2 => 1.08,
            _ => 1.0,
        };

        if (mainWindow.Content is FrameworkElement root)
            root.LayoutTransform = new ScaleTransform(scale, scale);

        mainWindow.FontSize = FontCombo.SelectedIndex switch
        {
            0 => 12,
            2 => 14,
            _ => 13,
        };
    }

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

    private sealed class ModelRegistryRow
    {
        public ModelRegistryRow(ModelRegistryEntry entry)
        {
            ModelId = entry.ModelId;
            DisplayName = entry.DisplayName;
            Version = entry.Version;
            ValidationStatus = ModelConfigurationValidator.ToDisplay(entry.ValidationStatus);
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
        public string ThresholdDisplay { get; }
        public string LastValidatedDisplay { get; }
        public string ActiveDisplay { get; }
    }
}
