using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using AOI_Monitor.ViewModels;
using Microsoft.Win32;

namespace AOI_Monitor.Views;

public partial class MonitorView : UserControl
{
    private readonly ObservableCollection<DefectRow> _defects = new();
    private readonly ObservableCollection<AlarmRow> _alarms = new();
    private readonly Dictionary<CameraViewType, string> _viewFolders = new();
    private readonly List<ImportedImage> _importedQueue = new();
    private readonly SimulatedRobotController _robotController = new();
    private readonly SimulatedEmergencyStopMonitor _emergencyStopMonitor;
    private readonly RobotCycleService _robotCycleService;
    private RobotAcceptanceRun? _lastRobotAcceptanceRun;

    private ICameraSource _cameraSource = CameraSourceFactory.ActiveSource;
    private bool _isRunning;
    private bool _currentResultSaved;
    private bool _updatingCalibrationProfiles;
    private CancellationTokenSource? _robotCycleCancellation;
    private int _importedQueueIndex = -1;
    private string? _currentImagePath;
    private string _currentBoardModel = "TBOX-MAIN";
    private string _currentLotId = "POC-LOT";
    private string _currentSourceFrameId = string.Empty;
    private string _currentSourceKind = "File";
    private string _currentFrameMetadata = "No frame";
    private string _lastLightingResult = "Lighting: Disabled / Not Connected";
    private bool _currentIsSimulatedSource;
    private BitmapSource? _currentBitmap;
    private ImportedImage? _currentImportedImage;
    private AnalysisResult? _currentAnalysis;
    private CalibrationProfileRecord? _selectedCalibrationProfile;

    public MonitorView()
    {
        InitializeComponent();
        _emergencyStopMonitor = new SimulatedEmergencyStopMonitor(_robotController);
        IntegrationBoundaryRegistry.RobotController = _robotController;
        IntegrationBoundaryRegistry.EmergencyStopMonitor = _emergencyStopMonitor;
        _robotCycleService = new RobotCycleService();
        _robotCycleService.StateChanged += OnRobotCycleStateChanged;
        LightingSettingsService.ApplyIntegrationBoundary();
        DefectGrid.ItemsSource = _defects;
        AlarmGrid.ItemsSource = _alarms;
        WorkflowState.Instance.StateChanged += OnWorkflowStateChanged;
        InspectionModelConfigurationService.ConfigurationChanged += OnEngineConfigurationChanged;
        LightingSettingsService.SettingsChanged += OnLightingSettingsChanged;
        Unloaded += OnUnloaded;
        ReloadImportedQueue();
        RefreshCalibrationProfiles();
        RefreshHeader();
        UpdateRobotSimulationStatus();
        LogEvent("ROBOT SIM", "Robot/handler simulation is available. No real robot hardware is connected.");
        LogEvent("READY", "Main Inspection ready. Use folder simulation or Image Library imported images.");
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        WorkflowState.Instance.StateChanged -= OnWorkflowStateChanged;
        InspectionModelConfigurationService.ConfigurationChanged -= OnEngineConfigurationChanged;
        LightingSettingsService.SettingsChanged -= OnLightingSettingsChanged;
        _robotCycleService.StateChanged -= OnRobotCycleStateChanged;
        _robotCycleCancellation?.Cancel();
        _robotCycleCancellation?.Dispose();
        if (ReferenceEquals(IntegrationBoundaryRegistry.RobotController, _robotController))
            IntegrationBoundaryRegistry.RobotController = new NullRobotController();
        if (ReferenceEquals(IntegrationBoundaryRegistry.EmergencyStopMonitor, _emergencyStopMonitor))
            IntegrationBoundaryRegistry.EmergencyStopMonitor = new NullEmergencyStopMonitor();
    }

    private void OnWorkflowStateChanged() => Dispatcher.Invoke(RefreshFromState);
    private void OnEngineConfigurationChanged() => Dispatcher.Invoke(RefreshHeader);
    private void OnLightingSettingsChanged() => Dispatcher.Invoke(RefreshHeader);
    private void OnRobotCycleStateChanged(RobotCycleState state) => Dispatcher.Invoke(UpdateRobotSimulationStatus);

    private void OnOpenDispositionClick(object sender, RoutedEventArgs e) => Navigate("review");
    private void OnOpenCompareClick(object sender, RoutedEventArgs e) => Navigate("compare");
    private void OnOpenLibraryClick(object sender, RoutedEventArgs e) => Navigate("library");

    private void Navigate(string key)
    {
        if (Window.GetWindow(this)?.DataContext is MainViewModel vm)
            vm.CurrentPage = key;
    }

    private void OnSelectTopFolderClick(object sender, RoutedEventArgs e) => SelectFolderForView(CameraViewType.Top);
    private void OnSelectSideFolderClick(object sender, RoutedEventArgs e) => SelectFolderForView(CameraViewType.Side);
    private void OnSelectBottomFolderClick(object sender, RoutedEventArgs e) => SelectFolderForView(CameraViewType.Bottom);

    private void SelectFolderForView(CameraViewType viewType)
    {
        var dialog = new OpenFolderDialog
        {
            Title = $"Select simulated {viewType} camera image folder",
            Multiselect = false,
        };

        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FolderName))
            return;

        _viewFolders[viewType] = dialog.FolderName;
        var source = CameraSourceFactory.CreateFolder(_viewFolders);
        source.SelectedView = SelectedCameraView();
        _cameraSource = source;
        CameraSourceFactory.SetActiveSource(source);

        LogEvent("CAMERA SOURCE", $"Configured simulated {viewType} folder: {dialog.FolderName}.");
    }

    private void OnStartClick(object sender, RoutedEventArgs e)
    {
        _isRunning = true;
        _cameraSource.SelectedView = SelectedCameraView();
        SynchronizeLightingForSelectedView();
        _cameraSource.StartAcquisition();
        ModeText.Text = "RUNNING";
        ModeText.Foreground = Brushes.LightGreen;
        LogEvent("START", $"Simulated inspection mode started. Camera status: {CameraStatusText()}.");

        if (_currentImagePath is null)
            LoadNextBoard();
    }

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        _isRunning = false;
        _cameraSource.StopAcquisition();
        ModeText.Text = "STOPPED";
        ModeText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E1A334"));
        LogEvent("STOP", "Simulated inspection mode paused.");
    }

    private void OnNextBoardClick(object sender, RoutedEventArgs e)
    {
        if (!_isRunning)
            LogEvent("NEXT BOARD", "Manual next-board inspection requested while simulated mode is paused.");

        LoadNextBoard();
    }

    private void OnSaveResultClick(object sender, RoutedEventArgs e)
    {
        if (_currentAnalysis is null)
        {
            LogEvent("ERROR", "No inspection result is available to save.");
            MessageBox.Show("Run inspection before saving a result.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_currentResultSaved)
        {
            LogEvent("SAVE", "Current result was already saved.");
            MessageBox.Show("Current inspection result is already saved.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            WorkflowState.Instance.SetAnalysis(_currentAnalysis, persist: true);
            _currentResultSaved = true;
            LogEvent("SAVE", $"Inspection result saved to SQLite: {_currentAnalysis.Verdict}.");
        }
        catch (Exception ex)
        {
            LogEvent("ERROR", $"Save failed: {ex.Message}");
            MessageBox.Show($"Save failed:\n{ex.Message}", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void OnRobotLoadClick(object sender, RoutedEventArgs e)
    {
        if (!TryBeginRobotOperation("ROBOT LOAD", out var token))
            return;

        try
        {
            if (!_robotController.IsBoardLoaded)
                LoadNextBoard(runInspection: false);

            if (string.IsNullOrWhiteSpace(_currentImagePath))
            {
                LogEvent("ROBOT LOAD", "Manual load stopped because no board image is available.");
                return;
            }

            var result = await _robotCycleService.LoadAsync(BuildLoadCommand(), token);
            LogRobotCommandResult("ROBOT LOAD", result);
        }
        finally
        {
            EndRobotOperation();
        }
    }

    private async void OnRobotInspectClick(object sender, RoutedEventArgs e)
    {
        if (!TryBeginRobotOperation("ROBOT INSPECT", out var token))
            return;

        try
        {
            if (string.IsNullOrWhiteSpace(_currentImagePath))
            {
                LogEvent("ROBOT INSPECT", "Manual inspect stopped because no board image is available.");
                return;
            }

            var move = await _robotCycleService.MoveToInspectAsync(BuildInspectCommand(), token);
            LogRobotCommandResult("ROBOT INSPECT", move);
            if (!move.Accepted)
                return;

            var analysis = await _robotCycleService.RunInspectionStepAsync(
                _ => Task.FromResult(RunInspection(_currentImagePath)),
                token);
            if (analysis is not null)
                LogEvent("ROBOT INSPECT", $"Inspection step completed through cycle service: {analysis.Verdict}.");
        }
        finally
        {
            EndRobotOperation();
        }
    }

    private async void OnRobotUnloadClick(object sender, RoutedEventArgs e)
    {
        if (!TryBeginRobotOperation("ROBOT UNLOAD", out var token))
            return;

        try
        {
            var result = await _robotCycleService.UnloadAsync(BuildUnloadCommand(), token);
            LogRobotCommandResult("ROBOT UNLOAD", result);
        }
        finally
        {
            EndRobotOperation();
        }
    }

    private void OnRobotCancelClick(object sender, RoutedEventArgs e)
    {
        if (_robotCycleCancellation is null)
        {
            LogEvent("ROBOT CANCEL", "No robot cycle action is currently running.");
            return;
        }

        _robotCycleCancellation.Cancel();
        LogEvent("ROBOT CANCEL", "Robot cycle cancellation requested.");
        UpdateRobotSimulationStatus();
    }

    private async void OnRobotResetClick(object sender, RoutedEventArgs e)
    {
        if (!TryBeginRobotOperation("ROBOT RESET", out var token))
            return;

        try
        {
            var result = await _robotCycleService.ResetAsync(token);
            LogRobotCommandResult("ROBOT RESET", result);
        }
        finally
        {
            EndRobotOperation();
        }
    }

    private void OnRobotEmergencyStopClick(object sender, RoutedEventArgs e)
    {
        if (IntegrationBoundaryRegistry.RobotController is SimulatedRobotController simulatedRobot)
        {
            simulatedRobot.TriggerEmergencyStop();
            _robotCycleService.RefreshEmergencyStopStatus("Emergency stop simulation triggered. No real safety hardware is connected.");
            LogEvent("ROBOT E-STOP", "Emergency stop simulation triggered. No real safety hardware is connected.");
        }
        else
        {
            LogEvent("ROBOT E-STOP", "Emergency stop trigger is available only for the local simulated robot controller.");
        }

        UpdateRobotSimulationStatus();
    }

    private async void OnRobotCycleClick(object sender, RoutedEventArgs e)
    {
        if (!TryBeginRobotOperation("ROBOT CYCLE", out var token))
            return;

        try
        {
            await RunRobotCycleAsync(token);
        }
        finally
        {
            EndRobotOperation();
        }
    }

    private async void OnRobotAcceptanceClick(object sender, RoutedEventArgs e)
    {
        if (!TryBeginRobotOperation("ROBOT ACCEPTANCE", out var token))
            return;

        try
        {
            RobotAcceptanceStatusText.Text = "Robot acceptance: running...";
            var run = await RobotAcceptanceTestService.RunAsync(progress: new Progress<string>(message => RobotAcceptanceStatusText.Text = message), cancellationToken: token);
            AoiDatabase.RecordRobotAcceptanceRun(run, WorkflowState.Instance.OperatorWithRole);
            _lastRobotAcceptanceRun = run;
            RobotAcceptanceStatusText.Text = BuildRobotAcceptanceStatus(run);
            LogEvent("ROBOT ACCEPTANCE", RobotAcceptanceStatusText.Text);
            MessageBox.Show(
                RobotAcceptanceStatusText.Text,
                "Robot Acceptance Test",
                MessageBoxButton.OK,
                run.Status == "FAIL" ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            RobotAcceptanceStatusText.Text = "Robot acceptance: canceled";
            LogEvent("ROBOT ACCEPTANCE", "Robot acceptance canceled.");
        }
        finally
        {
            EndRobotOperation();
        }
    }

    private void OnExportRobotAcceptanceClick(object sender, RoutedEventArgs e)
    {
        var run = _lastRobotAcceptanceRun ?? AoiDatabase.GetLatestRobotAcceptanceRun();
        if (run is null)
        {
            MessageBox.Show("No robot acceptance run is available to export.", "Robot Acceptance", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var export = RobotAcceptanceTestService.ExportReport(run);
            LogEvent("ROBOT ACCEPTANCE", $"Robot acceptance report exported: {Path.GetFileName(export.JsonPath)}.");
            MessageBox.Show(
                $"Robot acceptance report exported.\n\nJSON: {export.JsonPath}\nHTML: {export.HtmlPath}",
                "Robot Acceptance",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show($"Robot acceptance export failed:\n{ex.Message}", "Robot Acceptance", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnViewSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _cameraSource.SelectedView = SelectedCameraView();
        CameraSourceFactory.ActiveSource.SelectedView = SelectedCameraView();
        RefreshHeader();
        RefreshDefectRows();
        RenderOverlay();
    }

    private void OnOverlayChanged(object sender, RoutedEventArgs e)
    {
        DefectOverlayCanvas.Visibility = OverlayCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnCalibrationProfileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingCalibrationProfiles)
            return;

        _selectedCalibrationProfile = (CalibrationProfileCombo.SelectedItem as CalibrationProfileListItem)?.Profile;
        RefreshDefectRows();

        if (_selectedCalibrationProfile is null)
            LogEvent("CALIBRATION", "No 2D calibration profile selected. Defect coordinates remain image-space only.");
        else
            LogEvent("CALIBRATION", $"Selected 2D calibration profile '{_selectedCalibrationProfile.ProfileName}' for approximate board-mm display. Stage 2 preparation only.");
    }

    private void LoadNextBoard(bool runInspection = true)
    {
        try
        {
            var nextBoard = GetNextBoard();
            if (nextBoard is null || string.IsNullOrWhiteSpace(nextBoard.ImagePath) || !File.Exists(nextBoard.ImagePath))
            {
                LogEvent("ERROR", "No simulated board image is available. Select a folder or import an image first.");
                MessageBox.Show("Select a simulated image folder or load an image from Image Library first.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _currentImagePath = nextBoard.ImagePath;
            _currentImportedImage = nextBoard.ImportedImage;
            _currentBoardModel = nextBoard.BoardModel;
            _currentLotId = nextBoard.LotId;
            _currentSourceFrameId = nextBoard.SourceFrameId;
            _currentSourceKind = nextBoard.SourceKind;
            _currentFrameMetadata = nextBoard.FrameMetadata;
            _currentIsSimulatedSource = nextBoard.IsSimulatedSource;
            _currentResultSaved = false;
            WorkflowState.Instance.SetSampleImage(nextBoard.ImagePath);
            LoadImage(nextBoard.ImagePath);
            RefreshHeader();
            if (runInspection)
            {
                RunInspection(nextBoard.ImagePath);
            }
            else
            {
                _currentAnalysis = null;
                _defects.Clear();
                DefectOverlayCanvas.Children.Clear();
                TimingText.Text = "--";
                SetResultStatus("REVIEW");
                LogEvent("BOARD READY", "Simulated board image loaded for Simulated Robot cycle. Inspection has not run yet.");
            }
        }
        catch (Exception ex)
        {
            LogEvent("ERROR", $"Next board failed: {ex.Message}");
            MessageBox.Show($"Next board failed:\n{ex.Message}", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private BoardImageContext? GetNextBoard()
    {
        _cameraSource.SelectedView = SelectedCameraView();
        SynchronizeLightingForSelectedView();
        if (!_cameraSource.IsAcquiring)
            _cameraSource.StartAcquisition();

        var frame = _cameraSource.GetNextFrame();
        if (frame is not null)
        {
            var frameMetadata = FrameMetadataDisplay(frame);
            LogEvent("FRAME", $"{frame.SourceName} supplied {frame.ViewType} frame {frame.FrameId}: {Path.GetFileName(frame.SourcePath)}. {frameMetadata}");
            return new BoardImageContext(frame.SourcePath, null, frame.BoardModel, frame.LotId, frame.FrameId, frame.ViewType.ToString(), frame.SourceKind, frame.IsSimulated, frameMetadata);
        }

        if (_cameraSource is GenericVisionCameraSource && _cameraSource.ConnectionStatus != CameraSourceStatus.Ready)
            LogEvent("CAMERA WARNING", $"{_cameraSource.Name}: {_cameraSource.StatusMessage} Main Inspection will continue with imported/file fallback if available.");

        var imported = GetNextImportedImage();
        if (imported is not null)
        {
            LogEvent("QUEUE", $"Loaded imported image queue item: {imported.FileName}.");
            var importedView = string.Equals(imported.ViewType, "sample", StringComparison.OrdinalIgnoreCase)
                ? SelectedView()
                : imported.ViewType;
            return new BoardImageContext(imported.VaultPath, imported, imported.BoardModel, imported.LotId, $"IMG-{imported.Id}", importedView, "ImageVault", false, $"Image vault {imported.FileName}");
        }

        return string.IsNullOrWhiteSpace(WorkflowState.Instance.SampleImagePath)
            ? null
            : new BoardImageContext(WorkflowState.Instance.SampleImagePath, null, WorkflowState.Instance.BoardProgram, "POC-LOT", Path.GetFileName(WorkflowState.Instance.SampleImagePath), SelectedView(), "File", false, "Workflow file source");
    }

    private ImportedImage? GetNextImportedImage()
    {
        ReloadImportedQueue();
        if (_importedQueue.Count == 0)
            return null;

        var selectedView = SelectedView();
        var candidates = _importedQueue
            .Where(image =>
                !string.Equals(image.ViewType, "golden", StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(image.ViewType, "sample", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(image.ViewType, selectedView, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        if (candidates.Length == 0)
            candidates = _importedQueue.Where(image => !string.Equals(image.ViewType, "golden", StringComparison.OrdinalIgnoreCase)).ToArray();

        if (candidates.Length == 0)
            return null;

        _importedQueueIndex = (_importedQueueIndex + 1) % candidates.Length;
        return candidates[_importedQueueIndex];
    }

    private void ReloadImportedQueue()
    {
        _importedQueue.Clear();
        _importedQueue.AddRange(AoiDatabase.GetImportedImages().Where(image => File.Exists(image.VaultPath)));
    }

    private void LoadImage(string imagePath)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(imagePath, UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();

        _currentBitmap = bitmap;
        InspectionImage.Source = bitmap;
        EmptyImageText.Visibility = Visibility.Collapsed;
        ImageStatusText.Text = Path.GetFileName(imagePath);
        LogEvent("NEXT BOARD", $"Loaded simulated board image: {Path.GetFileName(imagePath)}.");
    }

    private AnalysisResult? RunInspection(string imagePath)
    {
        var state = WorkflowState.Instance;
        var engine = InspectionEngineFactory.Create();
        RefreshHeader();

        var analysis = engine.Analyze(imagePath, state.GoldenImagePath, state.DetectionPriority);
        analysis.BoardProgram = BoardModelText.Text;
        analysis.BoardId = CurrentBoardId();
        analysis.LotId = LotText.Text;
        analysis.StationId = StationText.Text;
        analysis.SourceFrameId = string.IsNullOrWhiteSpace(_currentSourceFrameId) ? Path.GetFileName(imagePath) : _currentSourceFrameId;
        analysis.SourceKind = _currentSourceKind;
        analysis.IsSimulatedSource = _currentIsSimulatedSource;
        analysis.OperatorId = state.OperatorWithRole;

        var selectedView = SelectedView();
        analysis.ViewType = selectedView;
        foreach (var defect in analysis.Defects)
        {
            defect.SideOrViewType = selectedView;
            if (string.IsNullOrWhiteSpace(defect.SourceRoiId))
                defect.SourceRoiId = defect.RoiId;
        }

        _currentAnalysis = analysis;
        var autoSave = AutoSaveCheck.IsChecked == true;
        RefreshDefectRows();
        var overlayMs = RenderOverlay();
        analysis.Timing.OverlayRenderingMilliseconds = overlayMs;
        analysis.Timing.RecalculateTotal();
        UpdateTimingDisplay(analysis.Timing);
        SetResultStatus(analysis.Verdict);
        if (analysis.Timing.IsOverOneSecond)
            LogEvent("PERFORMANCE WARNING", $"Visualization target exceeded: {analysis.Timing.TotalInspectionMilliseconds:F0} ms (limit 1000 ms).");

        state.SetAnalysis(analysis, persist: autoSave);
        _currentResultSaved = autoSave;

        LogEvent("INSPECTION COMPLETE", $"{analysis.Verdict}: {analysis.SuggestedDefect}, score {analysis.DifferenceScore:F1}%, confidence {analysis.Confidence:P0}, total {analysis.Timing.TotalInspectionMilliseconds:F0} ms.");

        if (autoSave)
            LogEvent("SAVE", "Auto-save wrote inspection result to SQLite.");

        return analysis;
    }

    private void RefreshFromState()
    {
        RefreshCalibrationProfiles(_selectedCalibrationProfile?.Id);
        RefreshHeader();
        var state = WorkflowState.Instance;
        if (_currentImagePath is null && !string.IsNullOrWhiteSpace(state.SampleImagePath) && File.Exists(state.SampleImagePath))
        {
            _currentImagePath = state.SampleImagePath;
            LoadImage(state.SampleImagePath);
        }

        if (state.LastAnalysis is not null && !ReferenceEquals(state.LastAnalysis, _currentAnalysis))
        {
            _currentAnalysis = state.LastAnalysis;
            _currentResultSaved = true;
            RefreshDefectRows();
            RenderOverlay();
            UpdateTimingDisplay(state.LastAnalysis.Timing);
            SetResultStatus(state.LastAnalysis.Verdict);
        }
    }

    private void RefreshHeader()
    {
        var state = WorkflowState.Instance;
        var engine = InspectionEngineFactory.Create();
        _cameraSource = CameraSourceFactory.ActiveSource;
        StationText.Text = state.StationId;
        BoardModelText.Text = string.IsNullOrWhiteSpace(_currentBoardModel) ? state.BoardProgram : _currentBoardModel;
        LotText.Text = string.IsNullOrWhiteSpace(_currentLotId) ? "POC-LOT" : _currentLotId;
        OperatorText.Text = state.OperatorWithRole;
        EngineText.Text = $"{engine.Name} | Camera: {CameraStatusText()}";
        ModelVersionText.Text = engine.Version;
        CameraSourceText.Text = $"{_cameraSource.Name}: {CameraStatusText()}";
        CameraFrameMetadataText.Text = _currentFrameMetadata;
        LightingSyncText.Text = _lastLightingResult;
        if (_currentAnalysis is null)
            TimingText.Text = "--";
        UpdateRobotSimulationStatus();
    }

    private void RefreshCalibrationProfiles(long? preferredProfileId = null)
    {
        try
        {
            _updatingCalibrationProfiles = true;
            var selectedId = preferredProfileId ?? _selectedCalibrationProfile?.Id;
            var profileItems = AoiDatabase.GetCalibrationProfiles()
                .Select(profile => new CalibrationProfileListItem(profile.DisplayName, profile))
                .ToList();

            profileItems.Insert(0, new CalibrationProfileListItem("No 2D profile (Stage 2 prep)", null));
            CalibrationProfileCombo.ItemsSource = profileItems;
            CalibrationProfileCombo.SelectedItem = profileItems.FirstOrDefault(item => item.Profile?.Id == selectedId) ?? profileItems[0];
            _selectedCalibrationProfile = (CalibrationProfileCombo.SelectedItem as CalibrationProfileListItem)?.Profile;
        }
        catch (Exception ex)
        {
            _selectedCalibrationProfile = null;
            LogEvent("CALIBRATION ERROR", $"Could not load calibration profiles: {ex.Message}");
        }
        finally
        {
            _updatingCalibrationProfiles = false;
        }
    }

    private void RefreshDefectRows()
    {
        _defects.Clear();
        if (_currentAnalysis is null)
            return;

        var side = SelectedView();
        var index = 1;
        foreach (var defect in _currentAnalysis.Defects)
        {
            double? boardXMillimeters = null;
            double? boardYMillimeters = null;
            if (TryMapDefectToBoard(defect, out var mappedBoardX, out var mappedBoardY))
            {
                boardXMillimeters = mappedBoardX;
                boardYMillimeters = mappedBoardY;
            }

            _defects.Add(new DefectRow(
                index++,
                defect.DefectType,
                defect.RoiId,
                defect.RoiType,
                defect.Confidence,
                string.IsNullOrWhiteSpace(defect.SideOrViewType) || defect.SideOrViewType == "sample" ? side : defect.SideOrViewType,
                defect.XPosition,
                defect.YPosition,
                boardXMillimeters,
                boardYMillimeters));
        }
    }

    private bool TryMapDefectToBoard(DefectResult defect, out double boardXMillimeters, out double boardYMillimeters)
    {
        boardXMillimeters = 0;
        boardYMillimeters = 0;
        if (_currentBitmap is null)
            return false;

        var imageX = ToImagePixelCoordinate(defect.XPosition, _currentBitmap.PixelWidth);
        var imageY = ToImagePixelCoordinate(defect.YPosition, _currentBitmap.PixelHeight);
        return CalibrationTransformService.TryConvertImageToBoard(_selectedCalibrationProfile, imageX, imageY, out boardXMillimeters, out boardYMillimeters);
    }

    private static double ToImagePixelCoordinate(double coordinate, int pixelSize)
        => coordinate is >= 0 and <= 1 ? coordinate * pixelSize : coordinate;

    private double RenderOverlay()
    {
        var watch = Stopwatch.StartNew();
        DefectOverlayCanvas.Children.Clear();
        if (_currentAnalysis is null || _currentBitmap is null)
            return StopElapsed(watch);

        var imageArea = CalculateImageArea(_currentBitmap.PixelWidth, _currentBitmap.PixelHeight, 1000, 700);
        foreach (var defect in _currentAnalysis.Defects)
        {
            var box = defect.BoundingBox;
            var rect = new System.Windows.Shapes.Rectangle
            {
                Width = Math.Max(4, box.Width * imageArea.Width),
                Height = Math.Max(4, box.Height * imageArea.Height),
                Stroke = new SolidColorBrush(ToVerdictColor(_currentAnalysis.Verdict)),
                StrokeThickness = 3,
                Fill = new SolidColorBrush(Color.FromArgb(32, ToVerdictColor(_currentAnalysis.Verdict).R, ToVerdictColor(_currentAnalysis.Verdict).G, ToVerdictColor(_currentAnalysis.Verdict).B)),
            };
            if (!string.IsNullOrWhiteSpace(defect.SourceRoiId) && defect.SourceRoiId != "UNASSIGNED")
                rect.StrokeDashArray = new DoubleCollection { 5, 3 };

            Canvas.SetLeft(rect, imageArea.X + box.X * imageArea.Width);
            Canvas.SetTop(rect, imageArea.Y + box.Y * imageArea.Height);
            DefectOverlayCanvas.Children.Add(rect);

            var roiLabel = string.IsNullOrWhiteSpace(defect.RoiId) ? string.Empty : $" [{defect.RoiId}]";
            var label = $"{defect.DefectType}{roiLabel} {defect.Confidence:P0}";
            var text = new TextBlock
            {
                Text = label,
                Background = new SolidColorBrush(Color.FromArgb(210, 5, 6, 7)),
                Foreground = new SolidColorBrush(ToVerdictColor(_currentAnalysis.Verdict)),
                FontWeight = FontWeights.Bold,
                FontSize = 18,
                Padding = new Thickness(5, 2, 5, 2),
            };

            Canvas.SetLeft(text, imageArea.X + box.X * imageArea.Width);
            Canvas.SetTop(text, Math.Max(0, imageArea.Y + box.Y * imageArea.Height - 30));
            DefectOverlayCanvas.Children.Add(text);
        }

        return StopElapsed(watch);
    }

    private void UpdateTimingDisplay(InspectionTiming timing)
    {
        TimingText.Text = $"Total {timing.TotalInspectionMilliseconds:F0} ms | load {timing.ImageLoadMilliseconds:F0}, prep {timing.PreprocessingMilliseconds:F0}, inspect {timing.InferenceMilliseconds:F0}, overlay {timing.OverlayRenderingMilliseconds:F0}";
        TimingText.Foreground = timing.IsOverOneSecond
            ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E1A334"))
            : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#DCE5EB"));
    }

    private void SetResultStatus(string verdict)
    {
        var normalized = verdict.ToUpperInvariant();
        if (normalized == "OK")
        {
            ResultStatusText.Text = "OK";
            ResultStatusBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#14311D"));
            ResultStatusBorder.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#50F56E"));
        }
        else if (normalized == "NG")
        {
            ResultStatusText.Text = "NG";
            ResultStatusBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#35191B"));
            ResultStatusBorder.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F13B3F"));
        }
        else
        {
            ResultStatusText.Text = normalized == "WARNING" ? "WARNING" : "REVIEW";
            ResultStatusBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#372914"));
            ResultStatusBorder.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E1A334"));
        }
    }

    private void LogEvent(string eventName, string message)
    {
        _alarms.Insert(0, new AlarmRow(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture), eventName, message));
        if (_alarms.Count > 80)
            _alarms.RemoveAt(_alarms.Count - 1);

        WorkflowState.Instance.AddEvent("MAIN_INSPECTION", $"{eventName}: {message}");
    }

    private bool TryBeginRobotOperation(string eventName, out CancellationToken cancellationToken)
    {
        if (_robotCycleCancellation is not null)
        {
            cancellationToken = CancellationToken.None;
            LogEvent(eventName, "Robot cycle action ignored because another robot action is running.");
            return false;
        }

        _robotCycleCancellation = new CancellationTokenSource();
        cancellationToken = _robotCycleCancellation.Token;
        UpdateRobotSimulationStatus();
        return true;
    }

    private void EndRobotOperation()
    {
        _robotCycleCancellation?.Dispose();
        _robotCycleCancellation = null;
        UpdateRobotSimulationStatus();
    }

    private void LogRobotCommandResult(string eventName, IntegrationCommandResult result)
    {
        var elapsedMs = _robotCycleService.Elapsed.TotalMilliseconds;
        LogEvent(
            result.Accepted ? eventName : $"{eventName} WARNING",
            $"{result.Message} Status={result.Status}; state={_robotCycleService.CurrentState}; elapsed={elapsedMs:F0} ms.");
    }

    private LoadCommand BuildLoadCommand()
        => new(CurrentBoardId(), BoardModelText.Text, LotText.Text, StationText.Text);

    private InspectCommand BuildInspectCommand()
        => new(CurrentBoardId(), BoardModelText.Text, LotText.Text, StationText.Text, SelectedView());

    private UnloadCommand BuildUnloadCommand()
        => new(CurrentBoardId(), BoardModelText.Text, LotText.Text, StationText.Text, "Simulated output tray");

    private async Task RunRobotCycleAsync(CancellationToken cancellationToken)
    {
        UpdateRobotSimulationStatus();

        try
        {
            if (IntegrationBoundaryRegistry.EmergencyStopMonitor.IsEmergencyStopActive)
            {
                _robotCycleService.RefreshEmergencyStopStatus("Cycle blocked because emergency stop simulation is active. Press Reset to clear it.");
                LogEvent("ROBOT CYCLE", "Cycle blocked because emergency stop simulation is active. Press Reset to clear it.");
                return;
            }

            LoadNextBoard(runInspection: false);
            if (string.IsNullOrWhiteSpace(_currentImagePath))
            {
                LogEvent("ROBOT CYCLE", "Cycle stopped because no simulated board image is available.");
                return;
            }

            var run = await _robotCycleService.RunFullCycleAsync(
                BuildLoadCommand(),
                BuildInspectCommand(),
                _ =>
                {
                    var analysis = RunInspection(_currentImagePath);
                    if (analysis is not null && !SaveCurrentResultFromRobotCycle())
                        return Task.FromResult<AnalysisResult?>(null);
                    return Task.FromResult(analysis);
                },
                BuildUnloadCommand(),
                cancellationToken);

            LogEvent(
                run.Accepted ? "ROBOT CYCLE" : "ROBOT CYCLE WARNING",
                $"{run.Message} State={run.State}; elapsed={run.Elapsed.TotalMilliseconds:F0} ms.");
        }
        catch (OperationCanceledException)
        {
            LogEvent("ROBOT CYCLE", "Robot cycle canceled.");
        }
        catch (Exception ex)
        {
            LogEvent("ROBOT ERROR", $"Robot cycle failed: {ex.Message}");
        }
        finally
        {
            UpdateRobotSimulationStatus();
        }
    }

    private bool SaveCurrentResultFromRobotCycle()
    {
        if (_currentAnalysis is null)
        {
            LogEvent("ROBOT CYCLE", "Cycle stopped because inspection did not produce a result to save.");
            return false;
        }

        if (_currentResultSaved)
            return true;

        try
        {
            WorkflowState.Instance.SetAnalysis(_currentAnalysis, persist: true);
            _currentResultSaved = true;
            LogEvent("ROBOT SAVE", $"Simulated cycle saved inspection result to SQLite: {_currentAnalysis.Verdict}.");
            return true;
        }
        catch (Exception ex)
        {
            LogEvent("ROBOT ERROR", $"Simulated cycle save failed: {ex.Message}");
            return false;
        }
    }

    private void UpdateRobotSimulationStatus()
    {
        if (RobotSimStatusText is null)
            return;

        var controller = IntegrationBoundaryRegistry.RobotController;
        var emergencyStop = IntegrationBoundaryRegistry.EmergencyStopMonitor;
        var emergencyActive = emergencyStop.IsEmergencyStopActive;
        var state = _robotCycleService.CurrentState;
        var busy = _robotCycleCancellation is not null;

        RobotSimStatusText.Text = emergencyActive
            ? "E-STOP ACTIVE (simulation)"
            : controller.Status switch
            {
                IntegrationConnectionStatus.Ready => "Robot controller ready",
                IntegrationConnectionStatus.Simulated => "Ready: simulation only",
                IntegrationConnectionStatus.Error => "Robot boundary error",
                _ => "No robot connected",
            };
        RobotSimStatusText.Foreground = IntegrationStatusBrush(emergencyActive ? IntegrationConnectionStatus.Error : controller.Status);

        RobotBoardStateText.Text = !string.IsNullOrWhiteSpace(_robotCycleService.BoardId)
            ? $"Board: {_robotCycleService.BoardId}"
            : _robotController.IsBoardLoaded
                ? $"Simulated board loaded: {_robotController.LoadedBoardId}"
                : "No simulated board loaded";

        RobotCycleModeText.Text = busy
            ? $"State: {state} / action running"
            : $"State: {state}";

        var elapsed = _robotCycleService.Elapsed;
        RobotCycleTimeText.Text = elapsed > TimeSpan.Zero
            ? $"Cycle {elapsed.TotalMilliseconds:F0} ms"
            : "Last cycle --";

        RobotFaultText.Text = string.IsNullOrWhiteSpace(_robotCycleService.LastError)
            ? "Fault: none"
            : $"Fault: {_robotCycleService.LastError}";
        RobotFaultText.Foreground = string.IsNullOrWhiteSpace(_robotCycleService.LastError)
            ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9AA6AF"))
            : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F27777"));

        var latestAcceptance = _lastRobotAcceptanceRun ?? AoiDatabase.GetLatestRobotAcceptanceRun();
        RobotAcceptanceStatusText.Text = latestAcceptance is null
            ? "Robot acceptance: not validated"
            : BuildRobotAcceptanceStatus(latestAcceptance);
        RobotAcceptanceStatusText.Foreground = latestAcceptance?.Status switch
        {
            "PASS" => Brushes.LightGreen,
            "WARN" => Brushes.Gold,
            "FAIL" => Brushes.IndianRed,
            _ => Brushes.Gold,
        };

        RobotLoadButton.IsEnabled = !busy && (state is RobotCycleState.Idle or RobotCycleState.Completed or RobotCycleState.Canceled);
        RobotInspectButton.IsEnabled = !busy && (state is RobotCycleState.Loaded or RobotCycleState.ReadyToInspect);
        RobotUnloadButton.IsEnabled = !busy && (state is RobotCycleState.Loaded or RobotCycleState.ReadyToInspect or RobotCycleState.Inspecting);
        RobotCancelButton.IsEnabled = busy;
        RobotResetButton.IsEnabled = !busy;
        RobotEmergencyStopButton.IsEnabled = controller is SimulatedRobotController;
        RunRobotCycleButton.IsEnabled = !busy && (state is RobotCycleState.Idle or RobotCycleState.Completed or RobotCycleState.Canceled);
        RunRobotAcceptanceButton.IsEnabled = !busy && (state is RobotCycleState.Idle or RobotCycleState.Completed or RobotCycleState.Canceled);
        ExportRobotAcceptanceButton.IsEnabled = !busy;
    }

    private static string BuildRobotAcceptanceStatus(RobotAcceptanceRun run)
    {
        var boundary = run.SourceKind == "Simulated"
            ? " Simulation evidence only; no production robot movement was validated."
            : string.Empty;
        return $"Robot acceptance: {run.Status}; {run.SourceKind}; full cycle {run.FullCycleMs:F0} ms; e-stop {(run.EmergencyStopBlocked ? "blocked" : "not verified")}; invalid transition {(run.InvalidTransitionRejected ? "rejected" : "not verified")}.{boundary}";
    }

    private string CurrentBoardId()
    {
        var fileName = string.IsNullOrWhiteSpace(_currentImagePath)
            ? string.Empty
            : Path.GetFileNameWithoutExtension(_currentImagePath);

        return string.IsNullOrWhiteSpace(fileName)
            ? $"SIM-{DateTime.Now:yyyyMMddHHmmss}"
            : fileName;
    }

    private string SelectedView()
        => (ViewSelector.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Top";

    private CameraViewType SelectedCameraView()
        => SelectedView() switch
        {
            "Side" => CameraViewType.Side,
            "Bottom" => CameraViewType.Bottom,
            _ => CameraViewType.Top,
        };

    private string CameraStatusText()
        => _cameraSource.ConnectionStatus switch
        {
            CameraSourceStatus.Ready => "Connected",
            CameraSourceStatus.Simulated => "Simulated",
            CameraSourceStatus.Error => "Error",
            _ => "Not Connected",
        };

    private IntegrationCommandResult SynchronizeLightingForSelectedView()
    {
        var settings = LightingSettingsService.Load();
        var controller = LightingControllerFactory.Create(settings);
        IntegrationBoundaryRegistry.LightingController = controller;

        var view = SelectedView();
        var program = LightingCommandFormatter.ProgramForView(settings, view);
        var result = LightingSynchronizationService
            .SynchronizeAsync(controller, settings, view)
            .GetAwaiter()
            .GetResult();

        _lastLightingResult = FormatLightingResult(controller, view, program, result);
        LightingSyncText.Text = _lastLightingResult;
        LightingSyncText.Foreground = IntegrationStatusBrush(result.Status);

        if (!string.Equals(settings.Mode, LightingModes.None, StringComparison.OrdinalIgnoreCase))
            LogEvent(result.Accepted ? "LIGHTING" : "LIGHTING WARNING", result.Message);

        return result;
    }

    private static string FormatLightingResult(
        ILightingController controller,
        string view,
        string program,
        IntegrationCommandResult result)
    {
        var status = IntegrationStatusDisplay(result.Status);
        return $"{controller.Name}: {status} | {view} -> {program}";
    }

    private static Brush IntegrationStatusBrush(IntegrationConnectionStatus status)
    {
        var color = status switch
        {
            IntegrationConnectionStatus.Ready => "#50F56E",
            IntegrationConnectionStatus.Simulated => "#E1A334",
            IntegrationConnectionStatus.Error => "#F27777",
            _ => "#9AA6AF",
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

    private static string FrameMetadataDisplay(CameraFrame frame)
    {
        var size = frame.Width > 0 && frame.Height > 0
            ? $"{frame.Width}x{frame.Height}"
            : "size unknown";
        var pixelFormat = string.IsNullOrWhiteSpace(frame.PixelFormat) ? "format unknown" : frame.PixelFormat;
        var cameraId = string.IsNullOrWhiteSpace(frame.CameraId) ? "camera unknown" : frame.CameraId;
        return $"{frame.FrameId} | {cameraId} | {frame.ViewType} | {size} | {pixelFormat} | {frame.SourceKind}";
    }

    private static Rect CalculateImageArea(double imageWidth, double imageHeight, double hostWidth, double hostHeight)
    {
        var scale = Math.Min(hostWidth / imageWidth, hostHeight / imageHeight);
        var width = imageWidth * scale;
        var height = imageHeight * scale;
        return new Rect((hostWidth - width) / 2.0, (hostHeight - height) / 2.0, width, height);
    }

    private static double StopElapsed(Stopwatch watch)
    {
        watch.Stop();
        return watch.Elapsed.TotalMilliseconds;
    }

    private static Color ToVerdictColor(string verdict) => verdict.ToUpperInvariant() switch
    {
        "OK" => Colors.LimeGreen,
        "NG" => Colors.Red,
        _ => Colors.Orange,
    };

    public sealed record DefectRow(int No, string Type, string RoiId, string RoiType, double Score, string Side, double X, double Y, double? BoardX, double? BoardY)
    {
        public string ScoreDisplay => Score.ToString("P0", CultureInfo.InvariantCulture);
        public string XDisplay => X.ToString("P0", CultureInfo.InvariantCulture);
        public string YDisplay => Y.ToString("P0", CultureInfo.InvariantCulture);
        public string BoardXDisplay => BoardX is null ? "--" : BoardX.Value.ToString("F2", CultureInfo.InvariantCulture);
        public string BoardYDisplay => BoardY is null ? "--" : BoardY.Value.ToString("F2", CultureInfo.InvariantCulture);
    }

    public sealed record AlarmRow(string Time, string Event, string Message);

    private sealed record BoardImageContext(
        string ImagePath,
        ImportedImage? ImportedImage,
        string BoardModel,
        string LotId,
        string SourceFrameId,
        string ViewType,
        string SourceKind,
        bool IsSimulatedSource,
        string FrameMetadata);

    private sealed record CalibrationProfileListItem(string DisplayName, CalibrationProfileRecord? Profile);
}
