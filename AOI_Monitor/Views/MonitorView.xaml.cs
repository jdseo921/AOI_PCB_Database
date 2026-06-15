using System.Collections.ObjectModel;
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
    private static readonly HashSet<string> SupportedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff",
    };

    private readonly ObservableCollection<DefectRow> _defects = new();
    private readonly ObservableCollection<AlarmRow> _alarms = new();
    private readonly List<string> _imageQueue = new();

    private int _queueIndex = -1;
    private bool _isRunning;
    private bool _currentResultSaved;
    private string? _currentImagePath;
    private BitmapSource? _currentBitmap;
    private AnalysisResult? _currentAnalysis;

    public MonitorView()
    {
        InitializeComponent();
        DefectGrid.ItemsSource = _defects;
        AlarmGrid.ItemsSource = _alarms;
        WorkflowState.Instance.StateChanged += OnWorkflowStateChanged;
        InspectionModelConfigurationService.ConfigurationChanged += OnEngineConfigurationChanged;
        Unloaded += OnUnloaded;
        RefreshHeader();
        LogEvent("READY", "Main Inspection ready. Use folder simulation or Image Library imported images.");
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        WorkflowState.Instance.StateChanged -= OnWorkflowStateChanged;
        InspectionModelConfigurationService.ConfigurationChanged -= OnEngineConfigurationChanged;
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

    private void OnSelectFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select simulated inspection image folder",
            Multiselect = false,
        };

        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FolderName))
            return;

        _imageQueue.Clear();
        _imageQueue.AddRange(Directory
            .EnumerateFiles(dialog.FolderName, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path => SupportedImageExtensions.Contains(Path.GetExtension(path)))
            .OrderBy(Path.GetFileName));

        _queueIndex = -1;
        LogEvent("QUEUE", _imageQueue.Count == 0
            ? $"No supported images found in {dialog.FolderName}."
            : $"Loaded {_imageQueue.Count} simulated board image(s) from {dialog.FolderName}.");
    }

    private void OnStartClick(object sender, RoutedEventArgs e)
    {
        _isRunning = true;
        ModeText.Text = "RUNNING";
        ModeText.Foreground = Brushes.LightGreen;
        LogEvent("START", "Simulated inspection mode started.");

        if (_currentImagePath is null)
            LoadNextBoard();
    }

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        _isRunning = false;
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

    private void OnViewSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshDefectRows();
        RenderOverlay();
    }

    private void OnOverlayChanged(object sender, RoutedEventArgs e)
    {
        DefectOverlayCanvas.Visibility = OverlayCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LoadNextBoard()
    {
        try
        {
            var nextImage = GetNextImagePath();
            if (string.IsNullOrWhiteSpace(nextImage) || !File.Exists(nextImage))
            {
                LogEvent("ERROR", "No simulated board image is available. Select a folder or import an image first.");
                MessageBox.Show("Select a simulated image folder or load an image from Image Library first.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _currentImagePath = nextImage;
            _currentResultSaved = false;
            WorkflowState.Instance.SetSampleImage(nextImage);
            LoadImage(nextImage);
            RunInspection(nextImage);
        }
        catch (Exception ex)
        {
            LogEvent("ERROR", $"Next board failed: {ex.Message}");
            MessageBox.Show($"Next board failed:\n{ex.Message}", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private string? GetNextImagePath()
    {
        if (_imageQueue.Count > 0)
        {
            _queueIndex = (_queueIndex + 1) % _imageQueue.Count;
            return _imageQueue[_queueIndex];
        }

        return WorkflowState.Instance.SampleImagePath;
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
        analysis.BoardProgram = state.BoardProgram;
        analysis.OperatorId = state.OperatorId;

        var selectedView = SelectedView();
        foreach (var defect in analysis.Defects)
            defect.SideOrViewType = selectedView;

        _currentAnalysis = analysis;
        var autoSave = AutoSaveCheck.IsChecked == true;
        state.SetAnalysis(analysis, persist: autoSave);
        _currentResultSaved = autoSave;

        RefreshDefectRows();
        RenderOverlay();
        SetResultStatus(analysis.Verdict);
        LogEvent("INSPECTION COMPLETE", $"{analysis.Verdict}: {analysis.SuggestedDefect}, score {analysis.DifferenceScore:F1}%, confidence {analysis.Confidence:P0}.");

        if (autoSave)
            LogEvent("SAVE", "Auto-save wrote inspection result to SQLite.");
    }

    private void RefreshFromState()
    {
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
            SetResultStatus(state.LastAnalysis.Verdict);
        }
    }

    private void RefreshHeader()
    {
        var state = WorkflowState.Instance;
        var engine = InspectionEngineFactory.Create();
        BoardModelText.Text = state.BoardProgram;
        OperatorText.Text = state.OperatorId;
        EngineText.Text = $"{engine.Name} / {engine.Version}";
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
            _defects.Add(new DefectRow(
                index++,
                defect.DefectType,
                defect.Confidence,
                string.IsNullOrWhiteSpace(defect.SideOrViewType) || defect.SideOrViewType == "sample" ? side : defect.SideOrViewType,
                defect.XPosition,
                defect.YPosition));
        }
    }

    private void RenderOverlay()
    {
        DefectOverlayCanvas.Children.Clear();
        if (_currentAnalysis is null || _currentBitmap is null)
            return;

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
        }
    }

    private void SetResultStatus(string verdict)
    {
        var normalized = verdict.ToUpperInvariant();
        if (normalized == "OK")
        {
            ResultStatusText.Text = "OK";
            ResultStatusBorder.Style = (Style)Application.Current.FindResource("ChipGreen");
        }
        else if (normalized == "NG")
        {
            ResultStatusText.Text = "NG";
            ResultStatusBorder.Style = (Style)Application.Current.FindResource("ChipRed");
        }
        else
        {
            ResultStatusText.Text = "WARNING";
            ResultStatusBorder.Style = (Style)Application.Current.FindResource("ChipAmber");
        }
    }

    private void LogEvent(string eventName, string message)
    {
        _alarms.Insert(0, new AlarmRow(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture), eventName, message));
        if (_alarms.Count > 80)
            _alarms.RemoveAt(_alarms.Count - 1);

        WorkflowState.Instance.AddEvent("MAIN_INSPECTION", $"{eventName}: {message}");
    }

    private string SelectedView()
        => (ViewSelector.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Top";

    private static Rect CalculateImageArea(double imageWidth, double imageHeight, double hostWidth, double hostHeight)
    {
        var scale = Math.Min(hostWidth / imageWidth, hostHeight / imageHeight);
        var width = imageWidth * scale;
        var height = imageHeight * scale;
        return new Rect((hostWidth - width) / 2.0, (hostHeight - height) / 2.0, width, height);
    }

    private static Color ToVerdictColor(string verdict) => verdict.ToUpperInvariant() switch
    {
        "OK" => Colors.LimeGreen,
        "NG" => Colors.Red,
        _ => Colors.Orange,
    };

    public sealed record DefectRow(int No, string Type, double Score, string Side, double X, double Y)
    {
        public string ScoreDisplay => Score.ToString("P0", CultureInfo.InvariantCulture);
        public string XDisplay => X.ToString("P0", CultureInfo.InvariantCulture);
        public string YDisplay => Y.ToString("P0", CultureInfo.InvariantCulture);
    }

    public sealed record AlarmRow(string Time, string Event, string Message);
}
