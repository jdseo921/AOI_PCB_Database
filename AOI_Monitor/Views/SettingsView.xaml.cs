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

public partial class SettingsView : UserControl, IAsyncNavigationPage, IDisposable
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
        // Do NOT ApplyFontPreset() here: page load must be side-effect free. Applying the
        // UI-built preferences on load pushed staged-but-unapproved combo values to the whole
        // application and re-ran shell-wide localization, resetting dynamic header/footer text.
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

}
