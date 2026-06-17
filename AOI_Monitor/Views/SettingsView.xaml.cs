using System.Windows;
using System.Windows.Controls;
using System.Diagnostics;
using System.IO;
using System.Globalization;
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
    private bool _isKorean;

    public SettingsView(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        WorkflowState.Instance.StateChanged += OnWorkflowStateChanged;
        InspectionModelConfigurationService.ConfigurationChanged += OnInspectionConfigurationChanged;
        CameraSourceSettingsService.SettingsChanged += OnCameraSourceSettingsChanged;
        RefreshWorkflowUi();
        ApplyLanguageVisuals();
        ApplyFontPreset();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        WorkflowState.Instance.StateChanged -= OnWorkflowStateChanged;
        InspectionModelConfigurationService.ConfigurationChanged -= OnInspectionConfigurationChanged;
        CameraSourceSettingsService.SettingsChanged -= OnCameraSourceSettingsChanged;
    }

    private void OnWorkflowStateChanged() => Dispatcher.Invoke(RefreshWorkflowUi);
    private void OnInspectionConfigurationChanged() => Dispatcher.Invoke(RefreshInspectionConfigurationUi);
    private void OnCameraSourceSettingsChanged() => Dispatcher.Invoke(RefreshCameraSourceUi);

    private void OnApply(object sender, RoutedEventArgs e)
    {
        var state = WorkflowState.Instance;
        var existingConfig = InspectionModelConfigurationService.Load();
        var newConfig = BuildConfigurationFromUi();
        var existingCamera = CameraSourceSettingsService.Load();
        var newCamera = BuildCameraSourceSettingsFromUi();
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
            !string.Equals(existingCamera.BoardModel, newCamera.BoardModel, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(existingCamera.LotId, newCamera.LotId, StringComparison.OrdinalIgnoreCase);
        var thresholdChanged =
            ComboToPriority(DetectionPriorityCombo.SelectedIndex) != state.DetectionPriority ||
            Math.Abs(existingConfig.ConfidenceThreshold - newConfig.ConfidenceThreshold) > 0.0001;

        if ((modelConfigChanged || cameraConfigChanged) && !Authorize(RoleAuthorization.CanManageSettings, "Changing database/vault/model paths, selected model engine, or camera source"))
            return;

        if (thresholdChanged && !Authorize(RoleAuthorization.CanChangeThresholds, "Changing inspection thresholds or detection priority"))
            return;

        ApplyLanguageVisuals();
        ApplyFontPreset();
        SaveInspectionConfiguration(newConfig);
        SaveCameraSourceSettings(newCamera);

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
        ModelVersionText.Text = "UNCONFIGURED";
        LabelMapPathText.Text = string.Empty;
        ConfidenceThresholdText.Text = "0.65";
        InputWidthText.Text = "640";
        InputHeightText.Text = "640";
        InputTensorNameText.Text = string.Empty;
        OutputTensorNameText.Text = string.Empty;
        InspectionModelConfigurationService.Save(new InspectionModelConfiguration());
        CameraSourceSettingsService.Save(new CameraSourceSettings());
        CameraSourceSettingsService.ApplyActiveSource();

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

        RefreshRoleControls();
        RefreshInspectionConfigurationUi();
        RefreshCameraSourceUi();
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
        CameraBoardModelText.IsEnabled = canManageSettings;
        CameraLotIdText.IsEnabled = canManageSettings;
        BrowseCameraTopBtn.IsEnabled = canManageSettings;
        BrowseCameraSideBtn.IsEnabled = canManageSettings;
        BrowseCameraBottomBtn.IsEnabled = canManageSettings;
        ModelPathText.IsEnabled = canManageSettings;
        ModelVersionText.IsEnabled = canManageSettings;
        LabelMapPathText.IsEnabled = canManageSettings;
        InputWidthText.IsEnabled = canManageSettings;
        InputHeightText.IsEnabled = canManageSettings;
        InputTensorNameText.IsEnabled = canManageSettings;
        OutputTensorNameText.IsEnabled = canManageSettings;
        BrowseModelBtn.IsEnabled = canManageSettings;
        BrowseLabelMapBtn.IsEnabled = canManageSettings;
        ConfidenceThresholdText.IsEnabled = canChangeThresholds;
        TestModelBtn.IsEnabled = RoleAuthorization.CanTestModelConfiguration(role);
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

    private void SaveInspectionConfiguration(InspectionModelConfiguration? preparedConfiguration = null)
    {
        var configuration = preparedConfiguration ?? BuildConfigurationFromUi();
        var existing = InspectionModelConfigurationService.Load();
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
                : "Inspection engine set to Pixel Difference / Prototype Engine.");
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
            settings.IsFolderSimulation
                ? $"Camera source set to Folder Simulation; status {CameraSourceFactory.ActiveSource.ConnectionStatus}."
                : "Camera source set to No Camera / Not Connected.");
    }

    private void RefreshCameraSourceUi()
        => RefreshCameraSourceUi(CameraSourceSettingsService.Load());

    private void RefreshCameraSourceUi(CameraSourceSettings settings)
    {
        CameraSourceCombo.SelectedIndex = settings.IsFolderSimulation ? 1 : 0;
        CameraTopFolderText.Text = settings.TopFolder;
        CameraSideFolderText.Text = settings.SideFolder;
        CameraBottomFolderText.Text = settings.BottomFolder;
        CameraBoardModelText.Text = settings.BoardModel;
        CameraLotIdText.Text = settings.LotId;

        var source = CameraSourceFactory.Create(settings);
        CameraSourceStatusText.Text = source.ConnectionStatus switch
        {
            CameraSourceStatus.Simulated => "Camera: Simulated",
            CameraSourceStatus.Error => "Camera: Error",
            _ => "Camera: Not Connected",
        };
        CameraSourceStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(source.ConnectionStatus switch
        {
            CameraSourceStatus.Simulated => "#E1A334",
            CameraSourceStatus.Error => "#F27777",
            _ => "#F27777",
        }));
    }

    private CameraSourceSettings BuildCameraSourceSettingsFromUi()
        => new()
        {
            SourceKey = CameraSourceCombo.SelectedIndex == 1
                ? CameraSourceFactory.FolderSimulationSourceKey
                : CameraSourceFactory.NullSourceKey,
            TopFolder = CameraTopFolderText.Text.Trim(),
            SideFolder = CameraSideFolderText.Text.Trim(),
            BottomFolder = CameraBottomFolderText.Text.Trim(),
            BoardModel = CameraBoardModelText.Text.Trim(),
            LotId = CameraLotIdText.Text.Trim(),
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
}
