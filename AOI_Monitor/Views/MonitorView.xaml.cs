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

    private ICameraSource _cameraSource = CameraSourceFactory.ActiveSource;
    private bool _isRunning;
    private bool _currentResultSaved;
    private bool _robotCycleRunning;
    private bool _updatingCalibrationProfiles;
    private int _importedQueueIndex = -1;
    private string? _currentImagePath;
    private string _currentBoardModel = "TBOX-MAIN";
    private string _currentLotId = "POC-LOT";
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
        DefectGrid.ItemsSource = _defects;
        AlarmGrid.ItemsSource = _alarms;
        WorkflowState.Instance.StateChanged += OnWorkflowStateChanged;
        InspectionModelConfigurationService.ConfigurationChanged += OnEngineConfigurationChanged;
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
        if (ReferenceEquals(IntegrationBoundaryRegistry.RobotController, _robotController))
            IntegrationBoundaryRegistry.RobotController = new NullRobotController();
        if (ReferenceEquals(IntegrationBoundaryRegistry.EmergencyStopMonitor, _emergencyStopMonitor))
            IntegrationBoundaryRegistry.EmergencyStopMonitor = new NullEmergencyStopMonitor();
    }

    private void OnWorkflowStateChanged() => Dispatcher.Invoke(RefreshFromState);
    private void OnEngineConfigurationChanged() => Dispatcher.Invoke(RefreshHeader);

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
        if (_robotCycleRunning)
        {
            LogEvent("ROBOT SIM", "Manual load ignored because a simulated robot cycle is already running.");
            return;
        }

        if (!_robotController.IsBoardLoaded)
            LoadNextBoard(runInspection: false);

        if (string.IsNullOrWhiteSpace(_currentImagePath))
        {
            LogEvent("ROBOT LOAD", "Manual simulated load stopped because no board image is available.");
            return;
        }

        await RunRobotLoadAsync();
    }

    private async void OnRobotInspectClick(object sender, RoutedEventArgs e)
    {
        if (_robotCycleRunning)
        {
            LogEvent("ROBOT SIM", "Manual inspect ignored because a simulated robot cycle is already running.");
            return;
        }

        var result = await RunRobotInspectAsync();
        if (result.Accepted && !string.IsNullOrWhiteSpace(_currentImagePath))
            RunInspection(_currentImagePath);
    }

    private async void OnRobotUnloadClick(object sender, RoutedEventArgs e)
    {
        if (_robotCycleRunning)
        {
            LogEvent("ROBOT SIM", "Manual unload ignored because a simulated robot cycle is already running.");
            return;
        }

        await RunRobotUnloadAsync();
    }

    private async void OnRobotResetClick(object sender, RoutedEventArgs e)
    {
        if (_robotCycleRunning)
        {
            LogEvent("ROBOT SIM", "Manual reset ignored because a simulated robot cycle is already running.");
            return;
        }

        await ExecuteRobotCommandAsync("ROBOT RESET", () => _robotController.ResetAsync());
    }

    private void OnRobotEmergencyStopClick(object sender, RoutedEventArgs e)
    {
        _robotController.TriggerEmergencyStop();
        UpdateRobotSimulationStatus();
        LogEvent("ROBOT E-STOP", "Emergency stop simulation triggered. No real safety hardware is connected.");
    }

    private async void OnRobotCycleClick(object sender, RoutedEventArgs e)
    {
        if (_robotCycleRunning)
        {
            LogEvent("ROBOT CYCLE", "A simulated robot cycle is already running.");
            return;
        }

        await RunRobotCycleAsync();
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
        if (!_cameraSource.IsAcquiring)
            _cameraSource.StartAcquisition();

        var frame = _cameraSource.GetNextFrame();
        if (frame is not null)
        {
            LogEvent("FRAME", $"{frame.SourceName} supplied {frame.ViewType} frame {frame.FrameId}: {Path.GetFileName(frame.SourcePath)}.");
            return new BoardImageContext(frame.SourcePath, null, frame.BoardModel, frame.LotId);
        }

        var imported = GetNextImportedImage();
        if (imported is not null)
        {
            LogEvent("QUEUE", $"Loaded imported image queue item: {imported.FileName}.");
            return new BoardImageContext(imported.VaultPath, imported, imported.BoardModel, imported.LotId);
        }

        return string.IsNullOrWhiteSpace(WorkflowState.Instance.SampleImagePath)
            ? null
            : new BoardImageContext(WorkflowState.Instance.SampleImagePath, null, WorkflowState.Instance.BoardProgram, "POC-LOT");
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

    private void RunInspection(string imagePath)
    {
        var state = WorkflowState.Instance;
        var engine = InspectionEngineFactory.Create();
        RefreshHeader();

        var analysis = engine.Analyze(imagePath, state.GoldenImagePath, state.DetectionPriority);
        analysis.BoardProgram = BoardModelText.Text;
        analysis.OperatorId = state.OperatorWithRole;

        var selectedView = SelectedView();
        foreach (var defect in analysis.Defects)
            defect.SideOrViewType = selectedView;

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

            Canvas.SetLeft(rect, imageArea.X + box.X * imageArea.Width);
            Canvas.SetTop(rect, imageArea.Y + box.Y * imageArea.Height);
            DefectOverlayCanvas.Children.Add(rect);

            var label = $"{defect.DefectType} {defect.Confidence:P0}";
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

    private async Task RunRobotCycleAsync()
    {
        _robotCycleRunning = true;
        UpdateRobotSimulationStatus();
        var cycleWatch = Stopwatch.StartNew();

        try
        {
            if (_robotController.IsEmergencyStopActive)
            {
                LogEvent("ROBOT CYCLE", "Cycle blocked because emergency stop simulation is active. Press Reset to clear it.");
                return;
            }

            LoadNextBoard(runInspection: false);
            if (string.IsNullOrWhiteSpace(_currentImagePath))
            {
                LogEvent("ROBOT CYCLE", "Cycle stopped because no simulated board image is available.");
                return;
            }

            var load = await RunRobotLoadAsync();
            if (!load.Accepted)
                return;

            var inspect = await RunRobotInspectAsync();
            if (!inspect.Accepted)
                return;

            RunInspection(_currentImagePath);
            if (!SaveCurrentResultFromRobotCycle())
                return;

            var unload = await RunRobotUnloadAsync();
            if (!unload.Accepted)
                return;

            cycleWatch.Stop();
            LogEvent("ROBOT CYCLE", $"Simulated Load -> Inspect -> Save -> Unload cycle completed in {cycleWatch.Elapsed.TotalMilliseconds:F0} ms.");
            RobotCycleTimeText.Text = $"Last cycle {cycleWatch.Elapsed.TotalMilliseconds:F0} ms";
        }
        catch (Exception ex)
        {
            LogEvent("ROBOT ERROR", $"Simulated robot cycle failed: {ex.Message}");
        }
        finally
        {
            cycleWatch.Stop();
            _robotCycleRunning = false;
            UpdateRobotSimulationStatus();
        }
    }

    private Task<IntegrationCommandResult> RunRobotLoadAsync()
        => ExecuteRobotCommandAsync(
            "ROBOT LOAD",
            () => _robotController.LoadAsync(new LoadCommand(CurrentBoardId(), BoardModelText.Text, LotText.Text, StationText.Text)));

    private Task<IntegrationCommandResult> RunRobotInspectAsync()
        => ExecuteRobotCommandAsync(
            "ROBOT INSPECT",
            () => _robotController.InspectAsync(new InspectCommand(CurrentBoardId(), BoardModelText.Text, LotText.Text, StationText.Text, SelectedView())));

    private Task<IntegrationCommandResult> RunRobotUnloadAsync()
        => ExecuteRobotCommandAsync(
            "ROBOT UNLOAD",
            () => _robotController.UnloadAsync(new UnloadCommand(CurrentBoardId(), BoardModelText.Text, LotText.Text, StationText.Text, "Simulated output tray")));

    private async Task<IntegrationCommandResult> ExecuteRobotCommandAsync(
        string eventName,
        Func<Task<IntegrationCommandResult>> command)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var result = await command();
            watch.Stop();
            LogEvent(eventName, $"{result.Message} Status={result.Status}; step time={watch.Elapsed.TotalMilliseconds:F0} ms.");
            UpdateRobotSimulationStatus();
            return result;
        }
        catch (OperationCanceledException)
        {
            watch.Stop();
            var result = new IntegrationCommandResult(false, IntegrationConnectionStatus.Error, "Simulated robot command canceled.");
            LogEvent(eventName, $"{result.Message} Step time={watch.Elapsed.TotalMilliseconds:F0} ms.");
            UpdateRobotSimulationStatus();
            return result;
        }
        catch (Exception ex)
        {
            watch.Stop();
            var result = new IntegrationCommandResult(false, IntegrationConnectionStatus.Error, $"Simulated robot command failed: {ex.Message}");
            LogEvent("ROBOT ERROR", $"{result.Message} Step time={watch.Elapsed.TotalMilliseconds:F0} ms.");
            UpdateRobotSimulationStatus();
            return result;
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

        RobotSimStatusText.Text = _robotController.IsEmergencyStopActive
            ? "E-STOP ACTIVE (simulation)"
            : _robotController.IsBoardLoaded
                ? $"Loaded: {_robotController.LoadedBoardId}"
                : "Ready: simulation only";
        RobotSimStatusText.Foreground = _robotController.IsEmergencyStopActive
            ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F27777"))
            : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#DCE5EB"));
        RobotBoardStateText.Text = _robotController.IsBoardLoaded
            ? "Simulated board loaded"
            : "No simulated board loaded";
        RobotCycleModeText.Text = _robotCycleRunning
            ? "Cycle running"
            : "No real robot connected";
        RunRobotCycleButton.IsEnabled = !_robotCycleRunning;
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
            CameraSourceStatus.Simulated => "Simulated",
            CameraSourceStatus.Error => "Error",
            _ => "Not Connected",
        };

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

    public sealed record DefectRow(int No, string Type, double Score, string Side, double X, double Y, double? BoardX, double? BoardY)
    {
        public string ScoreDisplay => Score.ToString("P0", CultureInfo.InvariantCulture);
        public string XDisplay => X.ToString("P0", CultureInfo.InvariantCulture);
        public string YDisplay => Y.ToString("P0", CultureInfo.InvariantCulture);
        public string BoardXDisplay => BoardX is null ? "--" : BoardX.Value.ToString("F2", CultureInfo.InvariantCulture);
        public string BoardYDisplay => BoardY is null ? "--" : BoardY.Value.ToString("F2", CultureInfo.InvariantCulture);
    }

    public sealed record AlarmRow(string Time, string Event, string Message);

    private sealed record BoardImageContext(string ImagePath, ImportedImage? ImportedImage, string BoardModel, string LotId);

    private sealed record CalibrationProfileListItem(string DisplayName, CalibrationProfileRecord? Profile);
}
