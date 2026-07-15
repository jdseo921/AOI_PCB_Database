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

public partial class SettingsView
{
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
        RefreshRetentionUi();

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

}
