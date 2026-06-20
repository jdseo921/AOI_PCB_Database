using System.IO;
using System.Windows;
using System.Windows.Controls;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Microsoft.Win32;

namespace AOI_Monitor.Views;

public partial class FirstRunWizardView : Window
{
    private readonly Grid[] _steps;
    private readonly string[] _titles =
    {
        "Storage Root",
        "Local Diagnostics",
        "Camera Source",
        "Inspection Engine",
        "Stage 1 Workflow",
        "Finish",
    };

    private readonly string[] _subtitles =
    {
        "Confirm where local SQLite, image vault, exports, and settings are stored.",
        "Verify local storage, SQLite, vault folders, engine, camera, and integration boundaries.",
        "Choose no camera or folder simulation for evaluator demos.",
        "Use Pixel Difference for Stage 1 or select ONNX when a local model is configured.",
        "Review the repeatable Stage 1 validation/demo path.",
        "Save first-run completion and write an audit event.",
    };

    private int _stepIndex;
    private SystemDiagnosticReport? _lastReport;

    public FirstRunWizardView()
    {
        InitializeComponent();
        _steps = new[] { StepStorage, StepDiagnostics, StepCamera, StepEngine, StepWorkflow, StepFinish };
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        StorageRootText.Text = AoiDatabase.StorageRoot;
        var cameraSettings = CameraSourceSettingsService.Load();
        CameraModeCombo.SelectedIndex = CameraSourceFactory.NormalizeSourceKey(cameraSettings.SourceKey) == CameraSourceFactory.FolderSimulationSourceKey ? 1 : 0;
        CameraFolderText.Text = cameraSettings.TopFolder;

        var engineConfig = InspectionModelConfigurationService.Load();
        EngineCombo.SelectedIndex = engineConfig.IsOnnxSelected ? 1 : 0;
        EngineStatusText.Text = $"Current status: {InspectionModelConfigurationService.GetStatusText(InspectionModelConfigurationService.GetStatus(engineConfig))}";

        RunDiagnostics();
        ShowStep(0);
    }

    private void OnBrowseStorageClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select AOI Monitor storage root",
            Multiselect = false,
        };

        if (Directory.Exists(StorageRootText.Text))
            dialog.InitialDirectory = StorageRootText.Text;

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
            StorageRootText.Text = dialog.FolderName;
    }

    private void OnBrowseCameraFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select simulated camera image folder",
            Multiselect = false,
        };

        if (Directory.Exists(CameraFolderText.Text))
            dialog.InitialDirectory = CameraFolderText.Text;

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            CameraFolderText.Text = dialog.FolderName;
            CameraModeCombo.SelectedIndex = 1;
        }
    }

    private void OnRunDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        ApplyStorageRootFromWizard(showWarnings: true);
        RunDiagnostics();
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
        => ShowStep(Math.Max(0, _stepIndex - 1));

    private void OnNextClick(object sender, RoutedEventArgs e)
    {
        if (_stepIndex == 0 && !ApplyStorageRootFromWizard(showWarnings: true))
            return;

        if (_stepIndex == 1)
            RunDiagnostics();

        if (_stepIndex == 2)
            ApplyCameraSelection();

        if (_stepIndex == 3)
            ApplyEngineSelection();

        ShowStep(Math.Min(_steps.Length - 1, _stepIndex + 1));
    }

    private void OnSkipClick(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "Skip first-run setup? The app will remain usable, but evaluator handoff should still run diagnostics from Settings or Guide.",
            "AOI Monitor Setup",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
            return;

        FirstRunSettingsService.MarkCompleted();
        WorkflowState.Instance.AddEvent("FIRST_RUN", "First-run setup wizard skipped by user. App remains usable; diagnostics can be rerun from Settings or Guide.");
        DialogResult = false;
        Close();
    }

    private void OnFinishClick(object sender, RoutedEventArgs e)
    {
        ApplyStorageRootFromWizard(showWarnings: true);
        ApplyCameraSelection();
        ApplyEngineSelection();
        RunDiagnostics();
        FirstRunSettingsService.MarkCompleted();
        WorkflowState.Instance.AddEvent(
            "FIRST_RUN",
            $"First-run setup completed. Diagnostics OK={_lastReport?.OkCount ?? 0}, Warn={_lastReport?.WarnCount ?? 0}, Error={_lastReport?.ErrorCount ?? 0}.");
        DialogResult = true;
        Close();
    }

    private bool ApplyStorageRootFromWizard(bool showWarnings)
    {
        var requested = string.IsNullOrWhiteSpace(StorageRootText.Text)
            ? AoiDatabase.DefaultStorageRoot
            : StorageRootText.Text.Trim();

        try
        {
            if (File.Exists(requested))
                throw new IOException("The selected storage root is a file. Choose a folder.");

            StorageRootSettingsService.SaveStorageRoot(requested);
            AoiDatabase.ConfigureStorageRoot(requested);
            AoiDatabase.Initialize();
            CameraSourceSettingsService.ApplyActiveSource();
            LightingSettingsService.ApplyIntegrationBoundary();
            MesIntegrationSettingsService.ApplyIntegrationBoundary();
            WizardStatusText.Text = $"Storage root ready: {AoiDatabase.StorageRoot}";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            WizardStatusText.Text = $"Storage root could not be applied: {ex.Message}";
            if (showWarnings)
                MessageBox.Show(WizardStatusText.Text, "AOI Monitor Setup", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private void ApplyCameraSelection()
    {
        var settings = CameraSourceSettingsService.Load();
        settings.SourceKey = CameraModeCombo.SelectedIndex == 1
            ? CameraSourceFactory.FolderSimulationSourceKey
            : CameraSourceFactory.NullSourceKey;
        settings.TopFolder = CameraFolderText.Text.Trim();
        settings.SideFolder = settings.SideFolder ?? string.Empty;
        settings.BottomFolder = settings.BottomFolder ?? string.Empty;
        CameraSourceSettingsService.Save(settings);
        CameraSourceSettingsService.ApplyActiveSource();
        WorkflowState.Instance.AddEvent("FIRST_RUN", $"Camera source set from setup wizard: {settings.SourceKey}.");
    }

    private void ApplyEngineSelection()
    {
        var configuration = InspectionModelConfigurationService.Load();
        configuration.SelectedEngineKey = EngineCombo.SelectedIndex == 1
            ? InspectionEngineFactory.OnnxEngineKey
            : InspectionEngineFactory.DefaultEngineKey;
        InspectionModelConfigurationService.Save(configuration);
        EngineStatusText.Text = $"Current status: {InspectionModelConfigurationService.GetStatusText(InspectionModelConfigurationService.GetStatus(configuration))}";
        WorkflowState.Instance.AddEvent("FIRST_RUN", $"Inspection engine set from setup wizard: {configuration.SelectedEngineKey}.");
    }

    private void RunDiagnostics()
    {
        _lastReport = SystemDiagnosticService.RunDiagnostics(StorageRootText.Text);
        DiagnosticsGrid.ItemsSource = _lastReport.Checks;
        DiagnosticsSummaryText.Text = $"OK={_lastReport.OkCount} / Warn={_lastReport.WarnCount} / Error={_lastReport.ErrorCount}";
        FinishSummaryText.Text = $"Diagnostics summary: OK={_lastReport.OkCount}, Warn={_lastReport.WarnCount}, Error={_lastReport.ErrorCount}. Optional integrations may remain warnings for Stage 1.";
    }

    private void ShowStep(int index)
    {
        _stepIndex = Math.Clamp(index, 0, _steps.Length - 1);
        for (var i = 0; i < _steps.Length; i++)
            _steps[i].Visibility = i == _stepIndex ? Visibility.Visible : Visibility.Collapsed;

        StepList.SelectedIndex = _stepIndex;
        StepCounterText.Text = $"Step {_stepIndex + 1} / {_steps.Length}";
        StepTitleText.Text = _titles[_stepIndex];
        StepSubtitleText.Text = _subtitles[_stepIndex];
        BackButton.IsEnabled = _stepIndex > 0;
        NextButton.IsEnabled = _stepIndex < _steps.Length - 1;
        FinishButton.IsEnabled = _stepIndex == _steps.Length - 1;
    }
}
