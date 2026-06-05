using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;
using Microsoft.Win32;
using AOI_Monitor.Services;

namespace AOI_Monitor.Views;

public partial class CompareView : UserControl
{
    private bool _defectOverlayVisible = true;
    private bool _goldenOverlayVisible = true;
    private bool _zoomed;

    private static readonly object[] Findings =
    {
        new { Region = "U107 pin row B",    Defect = "Bridge-like solder mass",  Golden = "Separated joints",      Judgement = "NG" },
        new { Region = "U107 lower-right",  Defect = "Excess highlight",          Golden = "Normal pad edge",        Judgement = "Review" },
        new { Region = "Board fiducial",    Defect = "Aligned",                   Golden = "Aligned",                Judgement = "OK" },
        new { Region = "Connector CN8",     Defect = "No difference",             Golden = "No difference",          Judgement = "OK" },
    };

    public CompareView()
    {
        InitializeComponent();
        FindingsGrid.ItemsSource = Findings;
        WorkflowState.Instance.StateChanged += OnStateChanged;
        Unloaded += (_, _) => WorkflowState.Instance.StateChanged -= OnStateChanged;
        RefreshFromState();
    }

    public void RefreshFromState()
    {
        var state = WorkflowState.Instance;
        if (!string.IsNullOrWhiteSpace(state.SampleImagePath))
            DefectSubtitleText.Text = $"{Path.GetFileName(state.SampleImagePath)} / loaded sample";

        if (!string.IsNullOrWhiteSpace(state.GoldenImagePath))
            GoldenSubtitleText.Text = $"{Path.GetFileName(state.GoldenImagePath)} / loaded golden";

        if (state.LastAnalysis is { } a)
        {
            DiffScoreText.Text = $"{a.DifferenceScore:F0}%";
            DiffSummaryText.Text = $"{a.Verdict} - {a.SuggestedDefect}";

            FindingsGrid.ItemsSource = new[]
            {
                new { Region = "Hotspot ROI", Defect = "Highest pixel delta region", Golden = "Reference mismatch", Judgement = a.Verdict },
                new { Region = "Mean brightness", Defect = $"{a.MeanBrightness:F1}", Golden = "calculated", Judgement = a.DifferenceScore > 8 ? "Review" : "OK" },
            };
        }
    }

    public void ExportPair()
    {
        OnExportPairClick(this, new RoutedEventArgs());
    }

    private void OnStateChanged() => Dispatcher.Invoke(RefreshFromState);

    private void OnSyncZoomClick(object sender, RoutedEventArgs e)
    {
        _zoomed = !_zoomed;
        var scale = _zoomed ? 1.2 : 1.0;
        DefectZoomTransform.ScaleX = scale;
        DefectZoomTransform.ScaleY = scale;
        GoldenZoomTransform.ScaleX = scale;
        GoldenZoomTransform.ScaleY = scale;
    }

    private void OnSyncPanClick(object sender, RoutedEventArgs e)
    {
        DefectZoomTransform.ScaleX = 1;
        DefectZoomTransform.ScaleY = 1;
        GoldenZoomTransform.ScaleX = 1;
        GoldenZoomTransform.ScaleY = 1;
        _zoomed = false;
    }

    private void OnShowDifferenceClick(object sender, RoutedEventArgs e)
    {
        var state = WorkflowState.Instance;
        if (string.IsNullOrWhiteSpace(state.SampleImagePath) || string.IsNullOrWhiteSpace(state.GoldenImagePath))
        {
            MessageBox.Show("Load sample and golden images from Library > Open Record / Compare Golden first.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var result = ImageAnalysisService.Analyze(state.SampleImagePath!, state.GoldenImagePath, state.DetectionPriority);
        state.SetAnalysis(result);
        RefreshFromState();
    }

    private void OnAiOverlayClick(object sender, RoutedEventArgs e)
    {
        _defectOverlayVisible = !_defectOverlayVisible;
        DefectCanvasViewbox.Opacity = _defectOverlayVisible ? 1.0 : 0.72;
    }

    private void OnGtOverlayClick(object sender, RoutedEventArgs e)
    {
        _goldenOverlayVisible = !_goldenOverlayVisible;
        GoldenCanvasViewbox.Opacity = _goldenOverlayVisible ? 0.88 : 0.62;
    }

    private void OnExportPairClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export comparison snapshot",
            Filter = "PNG image|*.png",
            FileName = $"compare_pair_{DateTime.Now:yyyyMMdd_HHmmss}.png",
        };

        if (dialog.ShowDialog() != true) return;

        var rtb = new RenderTargetBitmap((int)Math.Max(1, ActualWidth), (int)Math.Max(1, ActualHeight), 96, 96, PixelFormats.Pbgra32);
        rtb.Render(this);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(dialog.FileName);
        encoder.Save(fs);

        WorkflowState.Instance.AddEvent("EXPORT", $"Comparison pair exported: {Path.GetFileName(dialog.FileName)}");
    }
}
