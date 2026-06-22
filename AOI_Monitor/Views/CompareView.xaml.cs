using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Microsoft.Win32;

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
            DiffSummaryText.Text = $"{a.Verdict} - {a.SuggestedDefect} | confidence {a.Confidence:P0}";

            var judgement = ToChipVerdict(a.Verdict);
            var topEvidence = a.Evidence.Take(3).ToArray();

            var rows = new List<object>();
            rows.AddRange(a.Defects.Select(defect => new
            {
                Region = string.IsNullOrWhiteSpace(defect.RoiId) ? "Defect ROI" : defect.RoiId,
                Defect = $"{defect.DefectType} {defect.Confidence:P0}",
                Golden = string.IsNullOrWhiteSpace(defect.RoiType) ? $"Box {defect.BoundingBox.X:P0},{defect.BoundingBox.Y:P0}" : defect.RoiType,
                Judgement = ToChipVerdict(defect.JudgmentStatus),
            }));
            rows.Add(new { Region = "Decision", Defect = a.DecisionReason, Golden = $"Policy: {a.PolicyName}", Judgement = judgement });
            rows.Add(new { Region = "Score vs Threshold", Defect = $"{a.DifferenceScore:F1}%", Golden = $"R {a.ReviewThreshold:F1}% / NG {a.NgThreshold:F1}%", Judgement = judgement });
            rows.Add(new { Region = "Hotspot ROI", Defect = $"x={a.Hotspot.X:P0}, y={a.Hotspot.Y:P0}", Golden = $"w={a.Hotspot.Width:P0}, h={a.Hotspot.Height:P0}", Judgement = judgement });
            rows.Add(new { Region = "Evidence 1", Defect = topEvidence.Length > 0 ? topEvidence[0] : "-", Golden = "", Judgement = judgement });
            rows.Add(new { Region = "Evidence 2", Defect = topEvidence.Length > 1 ? topEvidence[1] : "-", Golden = "", Judgement = judgement });
            rows.Add(new { Region = "Evidence 3", Defect = topEvidence.Length > 2 ? topEvidence[2] : "-", Golden = "", Judgement = judgement });
            FindingsGrid.ItemsSource = rows;

            FindingsSourceText.Text = "Analysis Result";
            FindingsSourceText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#C6FFD0"));
            FindingsSourceChip.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#14311D"));
            FindingsSourceChip.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#377849"));
        }
        else
        {
            FindingsSourceText.Text = "Demo Data";
            FindingsSourceText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFE0A7"));
            FindingsSourceChip.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#372914"));
            FindingsSourceChip.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8C6C35"));
        }
    }

    private static string ToChipVerdict(string verdict)
    {
        return verdict.ToUpperInvariant() switch
        {
            "NG" => "NG",
            "OK" => "OK",
            _ => "Review",
        };
    }

    public void ExportPair()
    {
        OnExportPairClick(this, new RoutedEventArgs());
    }

    private void OnStateChanged() => UiDispatcher.InvokeIfAvailable(Dispatcher, RefreshFromState);

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
        using (var fs = File.Create(dialog.FileName))
        {
            encoder.Save(fs);
        }

        var verified = ExportVerificationService.RecordVerifiedExport("ComparisonSnapshot", dialog.FileName);
        WorkflowState.Instance.AddEvent("EXPORT", $"Comparison pair exported: {Path.GetFileName(dialog.FileName)}; verification={verified.Verification.Status}.");
    }
}
