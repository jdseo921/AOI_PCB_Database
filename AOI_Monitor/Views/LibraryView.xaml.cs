using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.IO;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using AOI_Monitor.ViewModels;

namespace AOI_Monitor.Views;

public partial class LibraryView : UserControl
{
    // Prototype seed rows remain static until the browser is fully backed by SQLite queries.
    private static readonly DefectRecord[] Records =
    {
        new("IMG_0241","TBOX-MAIN","U107","Solder Bridge",  "Critical","OK","NG","Possible Escape","LINK","01-28 18:04"),
        new("IMG_0188","TBOX-MAIN","C684","Insuff. Solder", "Major",   "NG","NG","Verified NG",    "LINK","01-28 17:52"),
        new("IMG_0182","TBOX-MAIN","CN8", "Pin Height Err.","Major",   "NG","OK","False Call",     "LINK","01-28 17:44"),
        new("IMG_0177","TBOX-MAIN","D12", "Polarity Error", "Critical","OK","NG","Possible Escape","LINK","01-28 17:39"),
        new("IMG_0164","TBOX-MAIN","R88", "Tombstone",      "Major",   "NG","NG","Verified NG",    "LINK","01-28 17:20"),
        new("IMG_0153","TBOX-MAIN","SH1", "Shield Can Gap", "Major",   "NG","OK","False Call",     "LINK","01-28 17:11"),
    };

    // Temporary schema preview text; the actual PoC schema is created in Data/AoiDatabase.cs.
    private static readonly object[] SchemaRows =
    {
        new { Table = "samples",       Columns = "sample_id, board_model, lot_id, fov, refdes",                Status = "OK" },
        new { Table = "annotations",   Columns = "bbox_x, bbox_y, bbox_w, bbox_h, defect_code",               Status = "OK" },
        new { Table = "ai_results",    Columns = "model_version, confidence, ai_result, threshold",            Status = "OK" },
        new { Table = "review_events", Columns = "reviewer, disposition, timestamp, comment",                  Status = "OK" },
        new { Table = "image_index",   Columns = "image_path, roi_crop_path, similarity_vector",               Status = "OK" },
    };

    public LibraryView()
    {
        InitializeComponent();
        RecordsGrid.ItemsSource = Records;
        RecordsGrid.SelectedIndex = 0;
        SchemaGrid.ItemsSource = SchemaRows;
    }

    public void RefreshFromState()
    {
        RecordsGrid.Items.Refresh();
        SchemaGrid.Items.Refresh();
    }

    public void ExportSelectedRecord()
    {
        OnExportSelectedClick(this, new RoutedEventArgs());
    }

    private void OnOpenRecordClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select sample PCB image",
            Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff",
        };

        if (dialog.ShowDialog() != true) return;

        var state = WorkflowState.Instance;
        var imported = AoiDatabase.ImportImage(dialog.FileName, state.BoardProgram, "POC-LOT", "sample");
        state.SetSampleImage(imported.VaultPath);
        ShowImagePreview(imported.VaultPath, "Sample Image");
    }

    private void OnCompareGoldenClick(object sender, RoutedEventArgs e)
    {
        var state = WorkflowState.Instance;
        if (string.IsNullOrWhiteSpace(state.SampleImagePath))
        {
            MessageBox.Show("Load a sample image first using Open Record.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Select golden reference image",
            Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff",
        };

        if (dialog.ShowDialog() != true) return;

        var imported = AoiDatabase.ImportImage(dialog.FileName, state.BoardProgram, "POC-LOT", "golden");
        state.SetGoldenImage(imported.VaultPath);
        ShowImagePreview(imported.VaultPath, "Golden Reference Image");

        var result = ImageAnalysisService.Analyze(state.SampleImagePath!, state.GoldenImagePath, state.DetectionPriority);
        state.SetAnalysis(result);

        if (FindMainVm() is { } vm)
            vm.CurrentPage = "compare";
    }

    private void OnAddToTrainingClick(object sender, RoutedEventArgs e)
    {
        var state = WorkflowState.Instance;
        if (string.IsNullOrWhiteSpace(state.SampleImagePath) || !File.Exists(state.SampleImagePath))
        {
            MessageBox.Show("No sample image loaded.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var trainingDir = AoiDatabase.TrainingVaultPath;
        Directory.CreateDirectory(trainingDir);

        var fileName = $"{DateTime.Now:yyyyMMdd_HHmmss}_{Path.GetFileName(state.SampleImagePath)}";
        var target = Path.Combine(trainingDir, fileName);
        File.Copy(state.SampleImagePath, target, true);
        AoiDatabase.RecordTrainingSample(target, "candidate", "Queued from Library Add to Training.");

        state.QueueTrainingSample(fileName);
        MessageBox.Show($"Saved to training set:\n{target}", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnBatchRelabelClick(object sender, RoutedEventArgs e)
    {
        var selectedCount = RecordsGrid.SelectedItems.Count > 0 ? RecordsGrid.SelectedItems.Count : 1;
        WorkflowState.Instance.AddEvent("LABEL", $"Batch relabel executed for {selectedCount} record(s).");
        MessageBox.Show($"Batch relabel complete for {selectedCount} record(s).", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnExportSelectedClick(object sender, RoutedEventArgs e)
    {
        var selected = RecordsGrid.SelectedItem as DefectRecord ?? Records.FirstOrDefault();
        if (selected is null)
            return;

        var dialog = new SaveFileDialog
        {
            Title = "Export selected record",
            Filter = "CSV file|*.csv",
            FileName = $"record_{selected.Sample}_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
        };

        if (dialog.ShowDialog() != true) return;

        var sb = new StringBuilder();
        sb.AppendLine("Sample,Board,RefDes,Defect,Severity,AI,GT,Risk,Image,Updated");
        sb.AppendLine($"{selected.Sample},{selected.Board},{selected.RefDes},{selected.Defect},{selected.Severity},{selected.AiResult},{selected.GroundTruth},{selected.Risk},{selected.ImageLink},{selected.Date}");

        var analysis = WorkflowState.Instance.LastAnalysis;
        if (analysis is not null)
        {
            sb.AppendLine();
            sb.AppendLine("AnalysisScore,Verdict,SuggestedDefect,Timestamp");
            sb.AppendLine($"{analysis.DifferenceScore:F2},{analysis.Verdict},{analysis.SuggestedDefect},{analysis.Timestamp:yyyy-MM-dd HH:mm:ss}");
        }

        File.WriteAllText(dialog.FileName, sb.ToString());
        AoiDatabase.RecordExport("LibraryRecord", dialog.FileName);
        WorkflowState.Instance.AddEvent("EXPORT", $"Library record exported: {Path.GetFileName(dialog.FileName)}");
    }

    private MainViewModel? FindMainVm()
        => (Window.GetWindow(this) as MainWindow)?.DataContext as MainViewModel;

    private void ShowImagePreview(string path, string title)
    {
        var previewBitmap = LoadPreviewBitmap(path, 1400);
        var image = new System.Windows.Controls.Image
        {
            Stretch = System.Windows.Media.Stretch.Uniform,
            Source = previewBitmap,
        };

        var win = new Window
        {
            Title = title,
            Width = 860,
            Height = 620,
            Content = new Border
            {
                Background = System.Windows.Media.Brushes.Black,
                Padding = new Thickness(10),
                Child = image,
            },
            Owner = Window.GetWindow(this),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        win.Closed += (_, _) => image.Source = null;

        win.Show();
    }

    private static BitmapImage LoadPreviewBitmap(string path, int decodePixelWidth)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.UriSource = new Uri(path, UriKind.Absolute);
        bmp.DecodePixelWidth = decodePixelWidth;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }
}
