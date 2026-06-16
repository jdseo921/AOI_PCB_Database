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
    private DateTime? _currentRunCreatedAtUtc;
    private string _currentEngineDisplay = "Pixel Difference / PIXEL_DIFF_0.1";
    private string _lastAnnotatedImageFolder = string.Empty;
    private CancellationTokenSource? _workCts;

    public AIModelTestView()
    {
        InitializeComponent();
        ResultsGrid.ItemsSource = _rows;
        InspectionModelConfigurationService.ConfigurationChanged += OnInspectionConfigurationChanged;
        Unloaded += (_, _) => InspectionModelConfigurationService.ConfigurationChanged -= OnInspectionConfigurationChanged;
        RefreshEngineText();
        LoadLatestRun();
    }

    public void RefreshFromState()
    {
        RefreshEngineText();
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
            Title = "Select validation manifest or ground-truth CSV",
            Filter = "CSV files|*.csv|All files|*.*",
        };

        if (dialog.ShowDialog() != true)
            return;

        _groundTruthCsvPath = dialog.FileName;
        GroundTruthPathText.Text = _groundTruthCsvPath;
        StatusText.Text = $"Loaded validation CSV: {Path.GetFileName(_groundTruthCsvPath)}";
    }

    private async void OnRunBatchClick(object sender, RoutedEventArgs e)
    {
        if (_workCts is not null)
        {
            MessageBox.Show("A validation/export task is already running.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(_selectedFolder) || !Directory.Exists(_selectedFolder))
        {
            MessageBox.Show("Select a valid test image folder first.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var cts = BeginWork("Preparing validation batch...");
        var progress = new Progress<WorkProgress>(UpdateProgress);

        try
        {
            var batch = await Task.Run(() =>
            {
                var imageFiles = Directory.EnumerateFiles(_selectedFolder, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(path => SupportedImageExtensions.Contains(Path.GetExtension(path)))
                    .OrderBy(Path.GetFileName)
                    .ToArray();

                if (imageFiles.Length == 0)
                    return BatchRunOutcome.Empty("The selected folder does not contain PNG/JPG/JPEG images.");

                var errors = new List<string>();
                ValidationManifest manifest;
                try
                {
                    manifest = LoadValidationManifest(_groundTruthCsvPath, _selectedFolder);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    errors.Add($"Invalid ground-truth CSV: {ex.Message}");
                    manifest = new ValidationManifest(new Dictionary<string, GroundTruthEntry>(), new List<GroundTruthEntry>(), false);
                }

                var engine = InspectionEngineFactory.Create();
                var engineDisplay = $"{engine.Name} / {engine.Version}";
                var runItems = BuildRunItems(imageFiles, manifest);
                var rows = new List<BatchTestRow>();

                for (var i = 0; i < runItems.Count; i++)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    var item = runItems[i];
                    ((IProgress<WorkProgress>)progress).Report(new WorkProgress(i, runItems.Count, $"Inspecting {Path.GetFileName(item.ImagePath)}..."));

                    try
                    {
                        if (!File.Exists(item.ImagePath))
                        {
                            var message = $"Missing image file: {item.ImagePath}";
                            errors.Add(message);
                            rows.Add(ToErrorRow(item.ImagePath, item.Manifest, message));
                            continue;
                        }

                        var analysis = engine.Analyze(
                            item.ImagePath,
                            string.IsNullOrWhiteSpace(item.Manifest.GoldenPath) ? null : item.Manifest.GoldenPath,
                            WorkflowState.Instance.DetectionPriority);

                        rows.Add(ToRow(item.ImagePath, item.Manifest, analysis));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
                    {
                        var message = FriendlyFileError(item.ImagePath, ex);
                        errors.Add(message);
                        rows.Add(ToErrorRow(item.ImagePath, item.Manifest, message));
                    }
                }

                var metrics = CalculateMetrics(rows);
                long runId;
                try
                {
                    runId = AoiDatabase.RecordBatchTestRun(
                        _selectedFolder,
                        _groundTruthCsvPath,
                        engineDisplay,
                        metrics.Accuracy,
                        metrics.Precision,
                        metrics.Recall,
                        metrics.FalseCallRate,
                        rows.Select(r => r.ToRecord()).ToArray());
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    errors.Add($"Database write failure: {ex.Message}");
                    runId = 0;
                }

                ((IProgress<WorkProgress>)progress).Report(new WorkProgress(runItems.Count, runItems.Count, "Validation batch complete."));
                return new BatchRunOutcome(rows, metrics, manifest.IsFormalManifest, engineDisplay, runId, errors, null);
            }, cts.Token);

            if (batch.Message is not null)
            {
                MessageBox.Show(batch.Message, "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _currentRunId = batch.RunId > 0 ? batch.RunId : null;
            _currentRunCreatedAtUtc = DateTime.UtcNow;
            _currentEngineDisplay = batch.EngineDisplay;
            _lastAnnotatedImageFolder = string.Empty;

            _rows.Clear();
            foreach (var row in batch.Rows)
                _rows.Add(row);

            ApplyMetrics(batch.Metrics);
            RunSummaryText.Text = $"{batch.Rows.Count} images / {batch.Rows.Count(r => r.IsFailed)} failed / run {(_currentRunId?.ToString(CultureInfo.InvariantCulture) ?? "not saved")}";
            StatusText.Text = batch.IsFormalManifest
                ? $"Formal manifest validation complete. {_rows.Count} row(s), {batch.Errors.Count} issue(s)."
                : $"Batch inspection complete. {_rows.Count} row(s), {batch.Errors.Count} issue(s).";
            WorkflowState.Instance.AddEvent("MODEL_TEST", $"Stage 1 validation run {_currentRunId?.ToString(CultureInfo.InvariantCulture) ?? "not saved"}: {_rows.Count} images, {_rows.Count(r => r.IsFailed)} failed, {batch.Errors.Count} issue(s).");
            foreach (var error in batch.Errors.Take(20))
                WorkflowState.Instance.AddEvent("MODEL_TEST_ERROR", error);

            if (batch.Errors.Count > 0)
                MessageBox.Show(string.Join(Environment.NewLine, batch.Errors.Take(8)), "Validation completed with skipped rows", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Validation batch canceled.";
            WorkflowState.Instance.AddEvent("MODEL_TEST", "Stage 1 validation batch canceled by user.");
        }
        catch (Exception ex)
        {
            StatusText.Text = "Validation batch failed. The app is still usable.";
            WorkflowState.Instance.AddEvent("MODEL_TEST_ERROR", $"Validation batch failed: {ex.Message}");
            MessageBox.Show($"Validation batch failed:\n{ex.Message}", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            EndWork();
        }
    }

    private void OnResultSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is BatchTestRow row)
            StatusText.Text = $"{row.Image}: {row.EngineResult}, score {row.ScoreDisplay}, {row.PassFail}.";
    }

    private void OnInspectionConfigurationChanged() => Dispatcher.Invoke(RefreshEngineText);

    private void RefreshEngineText()
    {
        var engine = InspectionEngineFactory.Create();
        var status = InspectionModelConfigurationService.GetStatusText();
        EngineText.Text = $"{engine.Name} / {engine.Version} / {status}";
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

    private async void OnExportAnnotatedImagesClick(object sender, RoutedEventArgs e)
    {
        if (_workCts is not null)
        {
            MessageBox.Show("A validation/export task is already running.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

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

        var cts = BeginWork("Exporting annotated images...");
        var progress = new Progress<WorkProgress>(UpdateProgress);
        try
        {
            var rows = _rows.ToArray();
            var result = await Task.Run(() =>
            {
                var errors = new List<string>();
                var exported = 0;
                Directory.CreateDirectory(dialog.FolderName);

                for (var i = 0; i < rows.Length; i++)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    var row = rows[i];
                    ((IProgress<WorkProgress>)progress).Report(new WorkProgress(i, rows.Length, $"Exporting {row.Image}..."));

                    try
                    {
                        if (!File.Exists(row.ImagePath))
                        {
                            errors.Add($"Missing image file: {row.ImagePath}");
                            continue;
                        }

                        var annotated = CreateAnnotatedBitmap(row);
                        var target = Path.Combine(
                            dialog.FolderName,
                            $"{Path.GetFileNameWithoutExtension(row.Image)}_{row.PassFail.ToLowerInvariant()}_overlay.png");

                        SavePng(annotated, target);
                        exported++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
                    {
                        errors.Add(FriendlyFileError(row.ImagePath, ex));
                    }
                }

                ((IProgress<WorkProgress>)progress).Report(new WorkProgress(rows.Length, rows.Length, "Annotated image export complete."));
                return new ExportOutcome(exported, errors);
            }, cts.Token);

            AoiDatabase.RecordExport("Stage1AnnotatedImages", dialog.FolderName, result.Errors.Count == 0 ? "OK" : "WARN");
            _lastAnnotatedImageFolder = dialog.FolderName;
            StatusText.Text = $"Annotated image export complete: {result.Count} file(s), {result.Errors.Count} issue(s).";
            WorkflowState.Instance.AddEvent("EXPORT", $"Stage 1 annotated images exported: {result.Count} file(s), {result.Errors.Count} issue(s).");
            foreach (var error in result.Errors.Take(20))
                WorkflowState.Instance.AddEvent("EXPORT_ERROR", error);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Annotated image export canceled.";
            WorkflowState.Instance.AddEvent("EXPORT", "Stage 1 annotated image export canceled by user.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusText.Text = "Annotated image export failed. Check export folder permissions.";
            WorkflowState.Instance.AddEvent("EXPORT_ERROR", $"Stage 1 annotated export failed: {ex.Message}");
            MessageBox.Show($"Annotated image export failed:\n{ex.Message}", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            EndWork();
        }
    }

    private void OnCancelWorkClick(object sender, RoutedEventArgs e)
    {
        _workCts?.Cancel();
        StatusText.Text = "Cancel requested. Finishing current file...";
    }

    private void OnGenerateReportClick(object sender, RoutedEventArgs e)
    {
        if (_rows.Count == 0 || _currentRunId is null)
        {
            MessageBox.Show("Run a validation batch before generating the customer report.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Generate customer validation report",
            Filter = "Markdown report|*.md|HTML report|*.html",
            FileName = $"customer_validation_run_{_currentRunId}_{DateTime.Now:yyyyMMdd_HHmmss}.md",
        };

        if (dialog.ShowDialog() != true)
            return;

        var metrics = CalculateMetrics(_rows);
        var extension = Path.GetExtension(dialog.FileName);
        var report = string.Equals(extension, ".html", StringComparison.OrdinalIgnoreCase)
            ? BuildHtmlReport(metrics)
            : BuildMarkdownReport(metrics);

        File.WriteAllText(dialog.FileName, report, Encoding.UTF8);
        AoiDatabase.RecordExport("CustomerValidationReport", dialog.FileName);
        StatusText.Text = $"Customer validation report saved: {dialog.FileName}";
        WorkflowState.Instance.AddEvent("EXPORT", $"Customer validation report exported for run {_currentRunId}: {Path.GetFileName(dialog.FileName)}");
    }

    private void LoadLatestRun()
    {
        var run = AoiDatabase.GetLatestBatchTestRun();
        if (run is null)
            return;

        _currentRunId = run.Id;
        _currentRunCreatedAtUtc = run.CreatedAtUtc;
        _currentEngineDisplay = run.EngineName;
        _selectedFolder = run.ImageFolder;
        _groundTruthCsvPath = run.GroundTruthCsvPath;
        FolderPathText.Text = run.ImageFolder;
        GroundTruthPathText.Text = string.IsNullOrWhiteSpace(run.GroundTruthCsvPath)
            ? "No CSV selected"
            : run.GroundTruthCsvPath;

        _rows.Clear();
        foreach (var result in AoiDatabase.GetBatchTestResults(run.Id))
            _rows.Add(BatchTestRow.FromRecord(result));

        ApplyMetrics(CalculateMetrics(_rows));
        RunSummaryText.Text = $"{run.TotalImages} images / {run.FailedCount} failed / run {run.Id}";
        StatusText.Text = $"Loaded latest persisted Stage 1 validation run: {run.Id}.";
    }

    private static BatchTestRow ToRow(string imagePath, GroundTruthEntry manifest, AnalysisResult analysis)
    {
        var defect = analysis.Defects.FirstOrDefault();
        var expected = string.IsNullOrWhiteSpace(manifest.Label) ? "UNKNOWN" : manifest.Label.Trim().ToUpperInvariant();
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
            DefectType = string.IsNullOrWhiteSpace(manifest.DefectType)
                ? defect?.DefectType ?? analysis.SuggestedDefect
                : manifest.DefectType,
            Side = manifest.Side,
            RefDes = manifest.RefDes,
            LotId = manifest.LotId,
            BoardModel = manifest.BoardModel,
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
            return new BatchMetrics(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, rows.Count);

        var tp = known.Count(r => NormalizeBinaryLabel(r.GroundTruth) == "NG" && NormalizeBinaryLabel(r.EngineResult) == "NG");
        var tn = known.Count(r => NormalizeBinaryLabel(r.GroundTruth) == "OK" && NormalizeBinaryLabel(r.EngineResult) == "OK");
        var fp = known.Count(r => NormalizeBinaryLabel(r.GroundTruth) == "OK" && NormalizeBinaryLabel(r.EngineResult) == "NG");
        var fn = known.Count(r => NormalizeBinaryLabel(r.GroundTruth) == "NG" && NormalizeBinaryLabel(r.EngineResult) == "OK");

        var accuracy = (tp + tn) / (double)known.Length;
        var precision = tp + fp == 0 ? 0 : tp / (double)(tp + fp);
        var recall = tp + fn == 0 ? 0 : tp / (double)(tp + fn);
        var falseCallRate = fp + tn == 0 ? 0 : fp / (double)(fp + tn);
        var unknown = rows.Count(r => NormalizeBinaryLabel(r.GroundTruth) == "UNKNOWN");
        return new BatchMetrics(accuracy, precision, recall, falseCallRate, tp, tn, fp, fn, fp, fn, tp, unknown);
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
        TpText.Text = metrics.TruePositive.ToString(CultureInfo.InvariantCulture);
        TnText.Text = metrics.TrueNegative.ToString(CultureInfo.InvariantCulture);
        FpText.Text = metrics.FalsePositive.ToString(CultureInfo.InvariantCulture);
        FnText.Text = metrics.FalseNegative.ToString(CultureInfo.InvariantCulture);
        FalseCallText.Text = metrics.FalseCall.ToString(CultureInfo.InvariantCulture);
        PossibleEscapeText.Text = metrics.PossibleEscape.ToString(CultureInfo.InvariantCulture);
        VerifiedNgText.Text = metrics.VerifiedNg.ToString(CultureInfo.InvariantCulture);
        UnknownText.Text = metrics.Unknown.ToString(CultureInfo.InvariantCulture);
    }

    private static IReadOnlyList<RunItem> BuildRunItems(IReadOnlyList<string> imageFiles, ValidationManifest manifest)
    {
        if (manifest.IsFormalManifest && manifest.OrderedEntries.Count > 0)
        {
            return manifest.OrderedEntries
                .Where(entry => !string.IsNullOrWhiteSpace(entry.ImagePath))
                .Select(entry => new RunItem(entry.ImagePath!, entry))
                .ToArray();
        }

        return imageFiles
            .Select(path =>
            {
                var imageName = Path.GetFileName(path);
                return new RunItem(
                    path,
                    manifest.ByImageName.TryGetValue(imageName, out var entry)
                        ? entry
                        : GroundTruthEntry.Unknown);
            })
            .ToArray();
    }

    private static ValidationManifest LoadValidationManifest(string? csvPath, string imageFolder)
    {
        var entries = new Dictionary<string, GroundTruthEntry>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<GroundTruthEntry>();
        if (string.IsNullOrWhiteSpace(csvPath) || !File.Exists(csvPath))
            return new ValidationManifest(entries, ordered, false);

        var lines = File.ReadAllLines(csvPath);
        if (lines.Length < 2)
            throw new InvalidDataException("Ground-truth CSV has no data rows.");

        var headers = SplitCsvLine(lines[0]).Select(NormalizeHeader).ToArray();
        var imageIndex = FindHeader(headers, "image", "filename", "file", "image_name", "sample");
        var truthIndex = FindHeader(headers, "groundtruth", "ground_truth", "gt", "label", "verdict", "expected");
        var goldenIndex = FindHeader(headers, "golden", "goldenpath", "golden_path", "goldenimage", "golden_image");
        var defectIndex = FindHeader(headers, "defecttype", "defect_type", "defect");
        var sideIndex = FindHeader(headers, "side", "view", "viewtype", "view_type");
        var refDesIndex = FindHeader(headers, "refdes", "ref_des", "reference", "reference_designator");
        var lotIndex = FindHeader(headers, "lotid", "lot_id", "lot");
        var boardIndex = FindHeader(headers, "boardmodel", "board_model", "model", "board");
        var isFormalManifest = HasHeader(headers, "image")
            && HasHeader(headers, "ground_truth", "groundtruth")
            && HasHeader(headers, "golden_image", "goldenimage")
            && HasHeader(headers, "defect_type", "defecttype")
            && HasHeader(headers, "side")
            && HasHeader(headers, "refdes")
            && HasHeader(headers, "lot_id", "lotid")
            && HasHeader(headers, "board_model", "boardmodel");

        if (imageIndex < 0 || truthIndex < 0)
            throw new InvalidDataException("Ground-truth CSV must include image and ground_truth/label columns.");

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
            var imagePath = ResolveOptionalPath(cells[imageIndex].Trim(), csvDir, imageFolder);
            var goldenPath = goldenIndex >= 0 && cells.Count > goldenIndex
                ? ResolveOptionalPath(cells[goldenIndex].Trim(), csvDir, imageFolder)
                : null;
            var entry = new GroundTruthEntry(
                label,
                goldenPath,
                Cell(cells, defectIndex),
                Cell(cells, sideIndex),
                Cell(cells, refDesIndex),
                Cell(cells, lotIndex),
                Cell(cells, boardIndex),
                imagePath);

            if (!string.IsNullOrWhiteSpace(imageName))
            {
                entries[imageName] = entry;
                ordered.Add(entry);
            }
        }

        return new ValidationManifest(entries, ordered, isFormalManifest);
    }

    private static bool HasHeader(string[] headers, params string[] names)
        => names.Any(name => headers.Contains(name, StringComparer.OrdinalIgnoreCase));

    private static string Cell(IReadOnlyList<string> cells, int index)
        => index >= 0 && cells.Count > index ? cells[index].Trim() : string.Empty;

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
        sb.AppendLine("Image,Ground Truth,AI/Engine Result,Score,Pass/Fail,Defect Type,Side,RefDes,LotId,BoardModel,Image Path,RoiX,RoiY,RoiWidth,RoiHeight");
        foreach (var row in rows)
        {
            sb.AppendLine(string.Join(",",
                EscapeCsv(row.Image),
                EscapeCsv(row.GroundTruth),
                EscapeCsv(row.EngineResult),
                row.Score.ToString("F4", CultureInfo.InvariantCulture),
                EscapeCsv(row.PassFail),
                EscapeCsv(row.DefectType),
                EscapeCsv(row.Side),
                EscapeCsv(row.RefDes),
                EscapeCsv(row.LotId),
                EscapeCsv(row.BoardModel),
                EscapeCsv(row.ImagePath),
                row.RoiX.ToString("F4", CultureInfo.InvariantCulture),
                row.RoiY.ToString("F4", CultureInfo.InvariantCulture),
                row.RoiWidth.ToString("F4", CultureInfo.InvariantCulture),
                row.RoiHeight.ToString("F4", CultureInfo.InvariantCulture)));
        }

        return sb.ToString();
    }

    private string BuildMarkdownReport(BatchMetrics metrics)
    {
        var failed = _rows.Where(r => r.PassFail == "FAIL").ToArray();
        var generatedAt = _currentRunCreatedAtUtc is { } runTime
            ? (runTime.Kind == DateTimeKind.Utc ? runTime.ToLocalTime() : runTime)
            : DateTime.Now;

        var sb = new StringBuilder();
        sb.AppendLine("# Stage 1 Customer Validation Report");
        sb.AppendLine();
        sb.AppendLine($"- Run ID: {_currentRunId}");
        sb.AppendLine($"- Date/time: {generatedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- Model/engine name: {EscapeMarkdown(GetEngineNamePart())}");
        sb.AppendLine($"- Model version: {EscapeMarkdown(GetEngineVersionPart())}");
        sb.AppendLine($"- Dataset folder: {EscapeMarkdown(_selectedFolder ?? string.Empty)}");
        sb.AppendLine($"- Total images: {_rows.Count}");
        sb.AppendLine($"- Accuracy: {FormatPercent(metrics.Accuracy)}");
        sb.AppendLine($"- Precision: {FormatPercent(metrics.Precision)}");
        sb.AppendLine($"- Recall: {FormatPercent(metrics.Recall)}");
        sb.AppendLine($"- False call rate: {FormatPercent(metrics.FalseCallRate)}");
        sb.AppendLine($"- Annotated-image folder: {EscapeMarkdown(string.IsNullOrWhiteSpace(_lastAnnotatedImageFolder) ? "Not exported" : _lastAnnotatedImageFolder)}");
        sb.AppendLine();
        sb.AppendLine("## Confusion Matrix");
        sb.AppendLine();
        sb.AppendLine("| TP | TN | FP | FN |");
        sb.AppendLine("|---:|---:|---:|---:|");
        sb.AppendLine($"| {metrics.TruePositive} | {metrics.TrueNegative} | {metrics.FalsePositive} | {metrics.FalseNegative} |");
        sb.AppendLine();
        sb.AppendLine("## Review Categories");
        sb.AppendLine();
        sb.AppendLine("| False Call | Possible Escape | Verified NG | Unknown / Unlabeled |");
        sb.AppendLine("|---:|---:|---:|---:|");
        sb.AppendLine($"| {metrics.FalseCall} | {metrics.PossibleEscape} | {metrics.VerifiedNg} | {metrics.Unknown} |");
        sb.AppendLine();
        sb.AppendLine("## Failed Samples");
        sb.AppendLine();
        if (failed.Length == 0)
        {
            sb.AppendLine("No failed samples.");
        }
        else
        {
            sb.AppendLine("| Image | Ground Truth | Engine Result | Defect Type | Side | RefDes | Lot ID | Board Model |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|");
            foreach (var row in failed)
            {
                sb.AppendLine($"| {EscapeMarkdown(row.Image)} | {EscapeMarkdown(row.GroundTruth)} | {EscapeMarkdown(row.EngineResult)} | {EscapeMarkdown(row.DefectType)} | {EscapeMarkdown(row.Side)} | {EscapeMarkdown(row.RefDes)} | {EscapeMarkdown(row.LotId)} | {EscapeMarkdown(row.BoardModel)} |");
            }
        }

        return sb.ToString();
    }

    private string BuildHtmlReport(BatchMetrics metrics)
    {
        var failedRows = _rows.Where(r => r.PassFail == "FAIL")
            .Select(row => $"<tr><td>{EscapeHtml(row.Image)}</td><td>{EscapeHtml(row.GroundTruth)}</td><td>{EscapeHtml(row.EngineResult)}</td><td>{EscapeHtml(row.DefectType)}</td><td>{EscapeHtml(row.Side)}</td><td>{EscapeHtml(row.RefDes)}</td><td>{EscapeHtml(row.LotId)}</td><td>{EscapeHtml(row.BoardModel)}</td></tr>");

        var failedTableRows = string.Join(Environment.NewLine, failedRows);
        if (string.IsNullOrWhiteSpace(failedTableRows))
            failedTableRows = "<tr><td colspan=\"8\">No failed samples.</td></tr>";

        var runDateTime = _currentRunCreatedAtUtc is { } runTime
            ? (runTime.Kind == DateTimeKind.Utc ? runTime.ToLocalTime() : runTime)
            : DateTime.Now;

        return $$"""
        <!doctype html>
        <html>
        <head>
          <meta charset="utf-8">
          <title>Stage 1 Customer Validation Report</title>
          <style>
            body { font-family: Segoe UI, Arial, sans-serif; margin: 32px; color: #1d252c; }
            table { border-collapse: collapse; width: 100%; margin: 14px 0 24px; }
            th, td { border: 1px solid #b8c1c8; padding: 7px 9px; text-align: left; }
            th { background: #edf2f5; }
            .metrics td:first-child { font-weight: 700; width: 240px; }
          </style>
        </head>
        <body>
          <h1>Stage 1 Customer Validation Report</h1>
          <table class="metrics">
            <tr><td>Run ID</td><td>{{_currentRunId}}</td></tr>
            <tr><td>Date/time</td><td>{{runDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}}</td></tr>
            <tr><td>Model/engine name</td><td>{{EscapeHtml(GetEngineNamePart())}}</td></tr>
            <tr><td>Model version</td><td>{{EscapeHtml(GetEngineVersionPart())}}</td></tr>
            <tr><td>Dataset folder</td><td>{{EscapeHtml(_selectedFolder ?? string.Empty)}}</td></tr>
            <tr><td>Total images</td><td>{{_rows.Count}}</td></tr>
            <tr><td>Accuracy</td><td>{{FormatPercent(metrics.Accuracy)}}</td></tr>
            <tr><td>Precision</td><td>{{FormatPercent(metrics.Precision)}}</td></tr>
            <tr><td>Recall</td><td>{{FormatPercent(metrics.Recall)}}</td></tr>
            <tr><td>False call rate</td><td>{{FormatPercent(metrics.FalseCallRate)}}</td></tr>
            <tr><td>Annotated-image folder</td><td>{{EscapeHtml(string.IsNullOrWhiteSpace(_lastAnnotatedImageFolder) ? "Not exported" : _lastAnnotatedImageFolder)}}</td></tr>
          </table>
          <h2>Confusion Matrix</h2>
          <table><tr><th>TP</th><th>TN</th><th>FP</th><th>FN</th></tr><tr><td>{{metrics.TruePositive}}</td><td>{{metrics.TrueNegative}}</td><td>{{metrics.FalsePositive}}</td><td>{{metrics.FalseNegative}}</td></tr></table>
          <h2>Review Categories</h2>
          <table><tr><th>False Call</th><th>Possible Escape</th><th>Verified NG</th><th>Unknown / Unlabeled</th></tr><tr><td>{{metrics.FalseCall}}</td><td>{{metrics.PossibleEscape}}</td><td>{{metrics.VerifiedNg}}</td><td>{{metrics.Unknown}}</td></tr></table>
          <h2>Failed Samples</h2>
          <table><tr><th>Image</th><th>Ground Truth</th><th>Engine Result</th><th>Defect Type</th><th>Side</th><th>RefDes</th><th>Lot ID</th><th>Board Model</th></tr>{{failedTableRows}}</table>
        </body>
        </html>
        """;
    }

    private string GetEngineNamePart()
        => _currentEngineDisplay.Split(" / ", StringSplitOptions.None).FirstOrDefault() ?? _currentEngineDisplay;

    private string GetEngineVersionPart()
    {
        var parts = _currentEngineDisplay.Split(" / ", StringSplitOptions.None);
        return parts.Length > 1 ? parts[1] : "UNKNOWN";
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
        => double.IsNaN(value) ? "--" : value.ToString("P1", CultureInfo.InvariantCulture);

    private static string EscapeCsv(string value)
        => $"\"{value.Replace("\"", "\"\"")}\"";

    private static string EscapeMarkdown(string value)
        => value.Replace("|", "\\|", StringComparison.Ordinal).Replace(Environment.NewLine, " ", StringComparison.Ordinal);

    private static string EscapeHtml(string value)
        => value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);

    private CancellationTokenSource BeginWork(string message)
    {
        _workCts = new CancellationTokenSource();
        WorkProgressBar.Value = 0;
        CancelWorkButton.IsEnabled = true;
        StatusText.Text = message;
        return _workCts;
    }

    private void EndWork()
    {
        _workCts?.Dispose();
        _workCts = null;
        CancelWorkButton.IsEnabled = false;
        WorkProgressBar.Value = 0;
    }

    private void UpdateProgress(WorkProgress progress)
    {
        WorkProgressBar.Value = progress.Total <= 0 ? 0 : Math.Min(100, progress.Completed * 100.0 / progress.Total);
        StatusText.Text = progress.Message;
    }

    private static BatchTestRow ToErrorRow(string imagePath, GroundTruthEntry manifest, string message)
    {
        return new BatchTestRow
        {
            ImagePath = imagePath,
            Image = string.IsNullOrWhiteSpace(imagePath) ? "(missing)" : Path.GetFileName(imagePath),
            GroundTruth = string.IsNullOrWhiteSpace(manifest.Label) ? "UNKNOWN" : manifest.Label.Trim().ToUpperInvariant(),
            EngineResult = "REVIEW",
            Score = 0,
            PassFail = "N/A",
            DefectType = message,
            Side = manifest.Side,
            RefDes = manifest.RefDes,
            LotId = manifest.LotId,
            BoardModel = manifest.BoardModel,
        };
    }

    private static string FriendlyFileError(string path, Exception ex)
    {
        var name = string.IsNullOrWhiteSpace(path) ? "(unknown file)" : Path.GetFileName(path);
        return ex switch
        {
            UnauthorizedAccessException => $"Permission denied or locked file: {name}",
            NotSupportedException => $"Unsupported image format: {name}",
            IOException => $"File could not be read or written: {name} ({ex.Message})",
            _ => $"{name}: {ex.Message}",
        };
    }

    private sealed record WorkProgress(int Completed, int Total, string Message);

    private sealed record ExportOutcome(int Count, IReadOnlyList<string> Errors);

    private sealed record BatchRunOutcome(
        IReadOnlyList<BatchTestRow> Rows,
        BatchMetrics Metrics,
        bool IsFormalManifest,
        string EngineDisplay,
        long RunId,
        IReadOnlyList<string> Errors,
        string? Message)
    {
        public static BatchRunOutcome Empty(string message)
            => new(Array.Empty<BatchTestRow>(), new BatchMetrics(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0), false, string.Empty, 0, Array.Empty<string>(), message);
    }

    private sealed record ValidationManifest(
        IReadOnlyDictionary<string, GroundTruthEntry> ByImageName,
        IReadOnlyList<GroundTruthEntry> OrderedEntries,
        bool IsFormalManifest);

    private sealed record RunItem(string ImagePath, GroundTruthEntry Manifest);

    private sealed record GroundTruthEntry(
        string Label,
        string? GoldenPath,
        string DefectType,
        string Side,
        string RefDes,
        string LotId,
        string BoardModel,
        string? ImagePath)
    {
        public static GroundTruthEntry Unknown { get; } = new("UNKNOWN", null, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, null);
    }

    private sealed record BatchMetrics(
        double Accuracy,
        double Precision,
        double Recall,
        double FalseCallRate,
        int TruePositive,
        int TrueNegative,
        int FalsePositive,
        int FalseNegative,
        int FalseCall,
        int PossibleEscape,
        int VerifiedNg,
        int Unknown);

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
        public string Side { get; set; } = string.Empty;
        public string RefDes { get; set; } = string.Empty;
        public string LotId { get; set; } = string.Empty;
        public string BoardModel { get; set; } = string.Empty;
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
                Side,
                RefDes,
                LotId,
                BoardModel,
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
                Side = record.Side,
                RefDes = record.RefDes,
                LotId = record.LotId,
                BoardModel = record.BoardModel,
                RoiX = record.RoiX,
                RoiY = record.RoiY,
                RoiWidth = record.RoiWidth,
                RoiHeight = record.RoiHeight,
            };
        }
    }
}
