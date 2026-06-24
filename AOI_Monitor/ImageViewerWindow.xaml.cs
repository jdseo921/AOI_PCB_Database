using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Microsoft.Win32;

namespace AOI_Monitor;

public partial class ImageViewerWindow : Window
{
    private const double MinimumZoom = 0.05;
    private const double MaximumZoom = 8.0;
    private readonly BitmapSource _source;
    private double _zoom = 1.0;

    public ImageViewerWindow(string title, BitmapSource source)
    {
        InitializeComponent();
        _source = source ?? throw new ArgumentNullException(nameof(source));
        Title = title;
        ViewerTitleText.Text = title;
        ViewerImage.Source = _source;
        ViewerImageSizeText.Text = $"{_source.PixelWidth:N0} x {_source.PixelHeight:N0}px";
        Loaded += (_, _) => FitToWindow();
        SizeChanged += (_, _) =>
        {
            if (_zoom < 1.0)
                FitToWindow();
        };
        AlarmEventService.AlarmEventsChanged += OnAlarmEventsChanged;
        Closed += OnClosed;
        RefreshCriticalStatus();
    }

    public static void ShowFromVisual(FrameworkElement owner, string title, FrameworkElement visual)
    {
        try
        {
            var snapshot = CaptureVisual(visual);
            var window = new ImageViewerWindow(title, snapshot);
            if (Window.GetWindow(owner) is { IsVisible: true } ownerWindow)
                window.Owner = ownerWindow;

            window.Show();
            window.Activate();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            var alarm = AlarmEventService.RaiseFromException(
                "Open image viewer",
                "ImageViewer",
                ex,
                AlarmSeverity.Warning,
                "Confirm the image area is visible, then try opening the large image view again.");
            MessageBox.Show(alarm.Message, "AOI Image Viewer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static BitmapSource CaptureVisual(FrameworkElement visual)
    {
        visual.UpdateLayout();
        var width = visual.ActualWidth;
        var height = visual.ActualHeight;
        if (width < 1 || height < 1)
            throw new InvalidOperationException("The selected image view is not visible yet.");

        var dpi = VisualTreeHelper.GetDpi(visual);
        var pixelWidth = Math.Max(1, (int)Math.Ceiling(width * dpi.DpiScaleX));
        var pixelHeight = Math.Max(1, (int)Math.Ceiling(height * dpi.DpiScaleY));
        var bitmap = new RenderTargetBitmap(
            pixelWidth,
            pixelHeight,
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);
        bitmap.Render(visual);
        if (bitmap.CanFreeze)
            bitmap.Freeze();
        return bitmap;
    }

    private void OnAlarmEventsChanged()
        => UiDispatcher.InvokeIfAvailable(Dispatcher, RefreshCriticalStatus);

    private void RefreshCriticalStatus()
    {
        var activeAlarms = AlarmEventService.GetEvents(new AlarmEventQuery
        {
            ActiveOnly = true,
            SortOrder = AlarmSortOrder.SeverityDescending,
        });

        var critical = activeAlarms.Count(alarm => alarm.Severity == AlarmSeverity.Critical);
        var alarmLevel = activeAlarms.Count(alarm => alarm.Severity == AlarmSeverity.Alarm);
        var warnings = activeAlarms.Count(alarm => alarm.Severity == AlarmSeverity.Warning);

        ViewerCriticalText.Text = activeAlarms.Count == 0
            ? "none"
            : $"{critical} critical / {alarmLevel} alarm / {warnings} warning";
        ViewerCriticalText.Foreground = Brush(critical > 0 || alarmLevel > 0 ? "#FFBFC1" : warnings > 0 ? "#FFE0A7" : "#DCE5EB");
    }

    private void OnFitClick(object sender, RoutedEventArgs e) => FitToWindow();

    private void OnActualSizeClick(object sender, RoutedEventArgs e) => SetZoom(1.0);

    private void OnZoomOutClick(object sender, RoutedEventArgs e) => SetZoom(_zoom / 1.25);

    private void OnZoomInClick(object sender, RoutedEventArgs e) => SetZoom(_zoom * 1.25);

    private void FitToWindow()
    {
        var viewportWidth = ViewerScrollHost.ViewportWidth > 0 ? ViewerScrollHost.ViewportWidth : Math.Max(1, ActualWidth - 32);
        var viewportHeight = ViewerScrollHost.ViewportHeight > 0 ? ViewerScrollHost.ViewportHeight : Math.Max(1, ActualHeight - 120);
        var sourceWidth = Math.Max(1, _source.Width);
        var sourceHeight = Math.Max(1, _source.Height);
        var fitZoom = Math.Min((viewportWidth - 24) / sourceWidth, (viewportHeight - 24) / sourceHeight);
        SetZoom(fitZoom);
    }

    private void SetZoom(double zoom)
    {
        _zoom = Math.Clamp(zoom, MinimumZoom, MaximumZoom);
        ViewerScale.ScaleX = _zoom;
        ViewerScale.ScaleY = _zoom;
        ViewerZoomText.Text = _zoom.ToString("P0", System.Globalization.CultureInfo.InvariantCulture);
        ZoomOutButton.IsEnabled = _zoom > MinimumZoom + 0.001;
        ZoomInButton.IsEnabled = _zoom < MaximumZoom - 0.001;
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save AOI image snapshot",
            Filter = "PNG image|*.png",
            FileName = $"{SafeFileName(ViewerTitleText.Text)}_{DateTime.Now:yyyyMMdd_HHmmss}.png",
        };

        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(_source));
            using (var stream = File.Create(dialog.FileName))
            {
                encoder.Save(stream);
            }

            var verified = ExportVerificationService.RecordVerifiedExport("ImageViewerSnapshot", dialog.FileName);
            WorkflowState.Instance.AddEvent(
                "EXPORT",
                $"Image viewer snapshot saved: {Path.GetFileName(dialog.FileName)}; verification={verified.Verification.Status}.");
            ViewerMessageText.Text = $"Saved {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            var alarm = AlarmEventService.RaiseFromException(
                "Save image viewer snapshot",
                "ImageViewer",
                ex,
                AlarmSeverity.Warning,
                "Select a writable folder and try saving the image snapshot again.");
            MessageBox.Show(alarm.Message, "AOI Image Viewer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnClosed(object? sender, EventArgs e)
        => AlarmEventService.AlarmEventsChanged -= OnAlarmEventsChanged;

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        safe = safe.Replace(' ', '_');
        return string.IsNullOrWhiteSpace(safe) ? "aoi_image_view" : safe;
    }

    private static SolidColorBrush Brush(string color)
        => new((Color)ColorConverter.ConvertFromString(color));
}
