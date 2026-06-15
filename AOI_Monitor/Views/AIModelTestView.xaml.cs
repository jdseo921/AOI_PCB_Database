using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Microsoft.Win32;

namespace AOI_Monitor.Views;

public partial class AIModelTestView : UserControl
{
    private static readonly HashSet<string> SupportedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg",
    };

    private readonly ObservableCollection<BatchTestRow> _rows = new();
    private string? _selectedFolder;
    private string? _groundTruthCsvPath;
    private long? _currentRunId;

    public AIModelTestView()
    {
        InitializeComponent();
        ResultsGrid.ItemsSource = _rows;
        LoadLatestRun();
    }

    public void RefreshFromState()
    {
        LoadLatestRun();
    }

    public void ExportResults()
    {
        OnExportCsvClick(this, new RoutedEventArgs());
    }

    private void OnSelectFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Stage 1 validation image folder",
            Multiselect = false,
        };

        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FolderName))
            return;

        _selectedFolder = dialog.FolderName;
        FolderPathText.Text = _selectedFolder;
        StatusText.Text = $"Selected folder: {Path.GetFileName(_selectedFolder)}";
    }

    private void OnSelectGroundTruthClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select ground-truth CSV",
            Filter = "CSV files|*.csv|All files|*.*",
        };

        if (dialog.ShowDialog() != true)
            return;

        _groundTruthCsvPath = dialog.FileName;
        GroundTruthPathText.Text = _groundTruthCsvPath;
        StatusText.Text = $"Loaded ground-truth CSV: {Path.GetFileName(_groundTruthCsvPath)}";
    }

    private void OnRunBatchClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_selectedFolder) || !Directory.Exists(_selectedFolder))
        {
            MessageBox.Show("Select a valid test image folder first.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var imageFiles = Directory.EnumerateFiles(_selectedFolder, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path => SupportedImageExtensions.Contains(Path.GetExtension(path)))
            .OrderBy(Path.GetFileName)
            .ToArray();

        if (imageFiles.Length == 0)
        {
            MessageBox.Show("The selected folder does not contain PNG/JPG/JPEG images.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var truth = LoadGroundTruth(_groundTruthCsvPath, _selectedFolder);
        var engine = InspectionEngineFactory.Create();
        var rows = new List<BatchTestRow>();

        foreach (var imagePath in imageFiles)
        {
            var imageName = Path.GetFileName(imagePath);
            if (!truth.TryGetValue(imageName, out var truthEntry))
                truthEntry = new GroundTruthEntry("UNKNOWN", null);

            var analysis = engine.Analyze(
                imagePath,
                string.IsNullOrWhiteSpace(truthEntry.GoldenPath) ? null : truthEntry.GoldenPath,
                WorkflowState.Instance.DetectionPriority);

            rows.Add(ToRow(imagePath, truthEntry.Label, analysis));
        }

        var metrics = CalculateMetrics(rows);
        var records = rows.Select(r => r.ToRecord()).ToArray();
        _currentRunId = AoiDatabase.RecordBatchTestRun(
            _selectedFolder,
            _groundTruthCsvPath,
            "Pixel Difference / Prototype Engine",
            metrics.Accuracy,
            metrics.Precision,
            metrics.Recall,
            metrics.FalseCallRate,
            records);

        _rows.Clear();
        foreach (var row in rows)
            _rows.Add(row);

        ApplyMetrics(metrics);
        RunSummaryText.Text = $"{rows.Count} images / {rows.Count(r => r.IsFailed)} failed / run {_currentRunId}";
        StatusText.Text = $"Batch inspection complete. Results stored in SQLite run {_currentRunId}.";
        WorkflowState.Instance.AddEvent("MODEL_TEST", $"Stage 1 validation run {_currentRunId}: {rows.Count} images, {rows.Count(r => r.IsFailed)} failed.");
    }

    private void OnResultSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is BatchTestRow row)
            StatusText.Text = $"{row.Image}: {row.EngineResult}, score {row.ScoreDisplay}, {row.PassFail}.";
    }

    private void OnPreviewSelectedClick(object sender, RoutedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is not BatchTestRow row)
        {
            MessageBox.Show("Select a result row first.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        PreviewRow(row);
    }

    private void OnExportCsvClick(object sender, RoutedEventArgs e)
    {
        if (_rows.Count == 0)
        {
            MessageBox.Show("No batch results are available to export.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export Stage 1 test results",
            Filter = "CSV file|*.csv",
            FileName = $"stage1_validation_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
        };

        if (dialog.ShowDialog() != true)
            return;

        File.WriteAllText(dialog.FileName, BuildResultsCsv(_rows), Encoding.UTF8);
        AoiDatabase.RecordExport("Stage1ValidationCsv", dialog.FileName);
        StatusText.Text = $"CSV exported: {dialog.FileName}";
    }

    private void OnExportAnnotatedImagesClick(object sender, RoutedEventArgs e)
    {
        if (_rows.Count == 0)
        {
            MessageBox.Show("No batch results are available to export.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = "Select folder for annotated validation images",
            Multiselect = false,
        };

        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FolderName))
            return;

        var exported = 0;
        foreach (var row in _rows)
        {
            if (!File.Exists(row.ImagePath))
                continue;

            var annotated = CreateAnnotatedBitmap(row);
            var target = Path.Combine(
                dialog.FolderName,
                $"{Path.GetFileNameWithoutExtension(row.Image)}_{row.PassFail.ToLowerInvariant()}_overlay.png");

            SavePng(annotated, target);
            exported++;
        }

        AoiDatabase.RecordExport("Stage1AnnotatedImages", dialog.FolderName);
        StatusText.Text = $"Annotated image export complete: {exported} file(s).";
    }

    private void LoadLatestRun()
    {
        var run = AoiDatabase.GetLatestBatchTestRun();
        if (run is null)
            return;

        _currentRunId = run.Id;
        _selectedFolder = run.ImageFolder;
        _groundTruthCsvPath = run.GroundTruthCsvPath;
        FolderPathText.Text = run.ImageFolder;
        GroundTruthPathText.Text = string.IsNullOrWhiteSpace(run.GroundTruthCsvPath)
            ? "No CSV selected"
            : run.GroundTruthCsvPath;

        _rows.Clear();
        foreach (var result in AoiDatabase.GetBatchTestResults(run.Id))
            _rows.Add(BatchTestRow.FromRecord(result));

        ApplyMetrics(new BatchMetrics(run.Accuracy, run.Precision, run.Recall, run.FalseCallRate));
        RunSummaryText.Text = $"{run.TotalImages} images / {run.FailedCount} failed / run {run.Id}";
        StatusText.Text = $"Loaded latest persisted Stage 1 validation run: {run.Id}.";
    }

    private static BatchTestRow ToRow(string imagePath, string? groundTruth, AnalysisResult analysis)
    {
        var defect = analysis.Defects.FirstOrDefault();
        var expected = string.IsNullOrWhiteSpace(groundTruth) ? "UNKNOWN" : groundTruth.Trim().ToUpperInvariant();
        var passFail = CalculatePassFail(expected, analysis.Verdict);
        var roi = defect?.BoundingBox ?? analysis.Hotspot;

        return new BatchTestRow
        {
            ImagePath = imagePath,
            Image = Path.GetFileName(imagePath),
            GroundTruth = expected,
            EngineResult = analysis.Verdict,
            Score = analysis.DifferenceScore,
            PassFail = passFail,
            DefectType = defect?.DefectType ?? analysis.SuggestedDefect,
            RoiX = roi.X,
            RoiY = roi.Y,
            RoiWidth = roi.Width,
            RoiHeight = roi.Height,
        };
    }

    private static string CalculatePassFail(string groundTruth, string engineResult)
    {
        var expected = NormalizeBinaryLabel(groundTruth);
        if (expected == "UNKNOWN")
            return "N/A";

        var actual = NormalizeBinaryLabel(engineResult);
        return expected == actual ? "PASS" : "FAIL";
    }

    private static BatchMetrics CalculateMetrics(IReadOnlyCollection<BatchTestRow> rows)
    {
        var known = rows.Where(r => NormalizeBinaryLabel(r.GroundTruth) != "UNKNOWN").ToArray();
        if (known.Length == 0)
            return new BatchMetrics(0, 0, 0, 0);

        var tp = known.Count(r => NormalizeBinaryLabel(r.GroundTruth) == "NG" && NormalizeBinaryLabel(r.EngineResult) == "NG");
        var tn = known.Count(r => NormalizeBinaryLabel(r.GroundTruth) == "OK" && NormalizeBinaryLabel(r.EngineResult) == "OK");
        var fp = known.Count(r => NormalizeBinaryLabel(r.GroundTruth) == "OK" && NormalizeBinaryLabel(r.EngineResult) == "NG");
        var fn = known.Count(r => NormalizeBinaryLabel(r.GroundTruth) == "NG" && NormalizeBinaryLabel(r.EngineResult) == "OK");

        var accuracy = (tp + tn) / (double)known.Length;
        var precision = tp + fp == 0 ? 0 : tp / (double)(tp + fp);
        var recall = tp + fn == 0 ? 0 : tp / (double)(tp + fn);
        var falseCallRate = fp + tn == 0 ? 0 : fp / (double)(fp + tn);
        return new BatchMetrics(accuracy, precision, recall, falseCallRate);
    }

    private static string NormalizeBinaryLabel(string label)
    {
        var normalized = label.Trim().ToUpperInvariant();
        return normalized switch
        {
            "OK" or "PASS" or "GOOD" or "TRUE_NEGATIVE" => "OK",
            "NG" or "FAIL" or "FAILED" or "DEFECT" or "DEFECTIVE" or "BAD" or "REVIEW" => "NG",
            _ => "UNKNOWN",
        };
    }

    private void ApplyMetrics(BatchMetrics metrics)
    {
        AccuracyText.Text = FormatPercent(metrics.Accuracy);
        PrecisionText.Text = FormatPercent(metrics.Precision);
        RecallText.Text = FormatPercent(metrics.Recall);
        FalseCallRateText.Text = FormatPercent(metrics.FalseCallRate);
    }

    private static Dictionary<string, GroundTruthEntry> LoadGroundTruth(string? csvPath, string imageFolder)
    {
        var entries = new Dictionary<string, GroundTruthEntry>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(csvPath) || !File.Exists(csvPath))
            return entries;

        var lines = File.ReadAllLines(csvPath);
        if (lines.Length < 2)
            return entries;

        var headers = SplitCsvLine(lines[0]).Select(NormalizeHeader).ToArray();
        var imageIndex = FindHeader(headers, "image", "filename", "file", "image_name", "sample");
        var truthIndex = FindHeader(headers, "groundtruth", "ground_truth", "gt", "label", "verdict", "expected");
        var goldenIndex = FindHeader(headers, "golden", "goldenpath", "golden_path", "goldenimage", "golden_image");

        if (imageIndex < 0 || truthIndex < 0)
            return entries;

        var csvDir = Path.GetDirectoryName(csvPath) ?? imageFolder;
        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var cells = SplitCsvLine(line);
            if (cells.Count <= Math.Max(imageIndex, truthIndex))
                continue;

            var imageName = Path.GetFileName(cells[imageIndex].Trim());
            var label = cells[truthIndex].Trim();
            var goldenPath = goldenIndex >= 0 && cells.Count > goldenIndex
                ? ResolveOptionalPath(cells[goldenIndex].Trim(), csvDir, imageFolder)
                : null;

            if (!string.IsNullOrWhiteSpace(imageName))
                entries[imageName] = new GroundTruthEntry(label, goldenPath);
        }

        return entries;
    }

    private static string? ResolveOptionalPath(string path, string csvDir, string imageFolder)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        if (Path.IsPathRooted(path))
            return File.Exists(path) ? path : null;

        var csvRelative = Path.Combine(csvDir, path);
        if (File.Exists(csvRelative))
            return csvRelative;

        var folderRelative = Path.Combine(imageFolder, path);
        return File.Exists(folderRelative) ? folderRelative : null;
    }

    private static int FindHeader(string[] headers, params string[] names)
    {
        for (var i = 0; i < headers.Length; i++)
        {
            if (names.Contains(headers[i], StringComparer.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    private static string NormalizeHeader(string value)
        => value.Trim().Replace(" ", "", StringComparison.Ordinal).Replace("-", "_", StringComparison.Ordinal).ToLowerInvariant();

    private static List<string> SplitCsvLine(string line)
    {
        var cells = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                cells.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }

        cells.Add(sb.ToString());
        return cells;
    }

    private static string BuildResultsCsv(IEnumerable<BatchTestRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Image,Ground Truth,AI/Engine Result,Score,Pass/Fail,Defect Type,Image Path,RoiX,RoiY,RoiWidth,RoiHeight");
        foreach (var row in rows)
        {
            sb.AppendLine(string.Join(",",
                EscapeCsv(row.Image),
                EscapeCsv(row.GroundTruth),
                EscapeCsv(row.EngineResult),
                row.Score.ToString("F4", CultureInfo.InvariantCulture),
                EscapeCsv(row.PassFail),
                EscapeCsv(row.DefectType),
                EscapeCsv(row.ImagePath),
                row.RoiX.ToString("F4", CultureInfo.InvariantCulture),
                row.RoiY.ToString("F4", CultureInfo.InvariantCulture),
                row.RoiWidth.ToString("F4", CultureInfo.InvariantCulture),
                row.RoiHeight.ToString("F4", CultureInfo.InvariantCulture)));
        }

        return sb.ToString();
    }

    private void PreviewRow(BatchTestRow row)
    {
        if (!File.Exists(row.ImagePath))
        {
            MessageBox.Show($"Image file is missing:\n{row.ImagePath}", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var bitmap = CreateAnnotatedBitmap(row);
        var image = new Image
        {
            Source = bitmap,
            Stretch = Stretch.Uniform,
        };

        var window = new Window
        {
            Title = $"Validation Preview - {row.Image}",
            Width = 920,
            Height = 680,
            Content = new Border
            {
                Background = Brushes.Black,
                Padding = new Thickness(10),
                Child = image,
            },
            Owner = Window.GetWindow(this),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        window.Closed += (_, _) => image.Source = null;
        window.Show();
    }

    private static RenderTargetBitmap CreateAnnotatedBitmap(BatchTestRow row)
    {
        var source = LoadBitmap(row.ImagePath);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(source, new Rect(0, 0, source.PixelWidth, source.PixelHeight));

            var roi = new Rect(
                row.RoiX * source.PixelWidth,
                row.RoiY * source.PixelHeight,
                Math.Max(2, row.RoiWidth * source.PixelWidth),
                Math.Max(2, row.RoiHeight * source.PixelHeight));

            var color = row.IsFailed ? Colors.Red : Colors.Orange;
            var pen = new Pen(new SolidColorBrush(color), Math.Max(3, source.PixelWidth / 300.0));
            dc.DrawRectangle(null, pen, roi);

            var label = $"{row.EngineResult} / {row.PassFail}";
            var text = new FormattedText(
                label,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                Math.Max(16, source.PixelWidth / 32.0),
                new SolidColorBrush(color),
                1.0);

            var textOrigin = new Point(Math.Max(0, roi.X), Math.Max(0, roi.Y - text.Height - 6));
            dc.DrawRectangle(Brushes.Black, null, new Rect(textOrigin.X, textOrigin.Y, text.Width + 10, text.Height + 6));
            dc.DrawText(text, new Point(textOrigin.X + 5, textOrigin.Y + 3));
        }

        var target = new RenderTargetBitmap(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    private static BitmapSource LoadBitmap(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.UriSource = new Uri(path, UriKind.Absolute);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private static void SavePng(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static string FormatPercent(double value)
        => value <= 0 ? "--" : value.ToString("P1", CultureInfo.InvariantCulture);

    private static string EscapeCsv(string value)
        => $"\"{value.Replace("\"", "\"\"")}\"";

    private sealed record GroundTruthEntry(string Label, string? GoldenPath);

    private sealed record BatchMetrics(double Accuracy, double Precision, double Recall, double FalseCallRate);

    public sealed class BatchTestRow
    {
        public string ImagePath { get; set; } = string.Empty;
        public string Image { get; set; } = string.Empty;
        public string GroundTruth { get; set; } = "UNKNOWN";
        public string EngineResult { get; set; } = "REVIEW";
        public double Score { get; set; }
        public string ScoreDisplay => $"{Score:F1}%";
        public string PassFail { get; set; } = "N/A";
        public bool IsFailed => PassFail == "FAIL";
        public string DefectType { get; set; } = "Unknown";
        public double RoiX { get; set; }
        public double RoiY { get; set; }
        public double RoiWidth { get; set; }
        public double RoiHeight { get; set; }

        public BatchTestResultRecord ToRecord()
        {
            return new BatchTestResultRecord(
                0,
                0,
                ImagePath,
                Image,
                GroundTruth,
                EngineResult,
                Score,
                PassFail,
                DefectType,
                RoiX,
                RoiY,
                RoiWidth,
                RoiHeight,
                DateTime.UtcNow);
        }

        public static BatchTestRow FromRecord(BatchTestResultRecord record)
        {
            return new BatchTestRow
            {
                ImagePath = record.ImagePath,
                Image = record.ImageName,
                GroundTruth = record.GroundTruth,
                EngineResult = record.EngineResult,
                Score = record.Score,
                PassFail = record.PassFail,
                DefectType = record.DefectType,
                RoiX = record.RoiX,
                RoiY = record.RoiY,
                RoiWidth = record.RoiWidth,
                RoiHeight = record.RoiHeight,
            };
        }
    }
}
