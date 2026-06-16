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
using AOI_Monitor.ViewModels;
using Microsoft.Win32;

namespace AOI_Monitor.Views;

public partial class ReportsView : UserControl
{
    private static readonly Encoding CsvEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff",
    };

    private readonly ObservableCollection<InspectionLogRow> _inspectionRows = new();
    private readonly ObservableCollection<ReviewLogRow> _reviewRows = new();
    private readonly ObservableCollection<ExportHistoryRow> _exportRows = new();
    private CancellationTokenSource? _workCts;

    public ReportsView()
    {
        InitializeComponent();
        InspectionGrid.ItemsSource = _inspectionRows;
        ReviewGrid.ItemsSource = _reviewRows;
        ExportGrid.ItemsSource = _exportRows;
        FromDatePicker.SelectedDate = DateTime.Today.AddDays(-30);
        ToDatePicker.SelectedDate = DateTime.Today;
        LoadLogs();
    }

    public void RefreshFromState() => LoadLogs();

    private void OnApplyFiltersClick(object sender, RoutedEventArgs e) => LoadLogs();

    private void OnClearFiltersClick(object sender, RoutedEventArgs e)
    {
        FromDatePicker.SelectedDate = DateTime.Today.AddDays(-30);
        ToDatePicker.SelectedDate = DateTime.Today;
        BoardFilterText.Text = string.Empty;
        OperatorFilterText.Text = string.Empty;
        ResultFilterCombo.SelectedIndex = 0;
        LoadLogs();
    }

    private void LoadLogs()
    {
        var filter = BuildFilter();
        var inspections = AoiDatabase.GetInspectionHistory(filter).Select(InspectionLogRow.FromRecord).ToArray();
        var reviews = AoiDatabase.GetReviewEvents(filter).Select(ReviewLogRow.FromRecord).ToArray();
        var exports = AoiDatabase.GetExportHistory().Select(ExportHistoryRow.FromRecord).ToArray();

        ReplaceRows(_inspectionRows, inspections);
        ReplaceRows(_reviewRows, reviews);
        ReplaceRows(_exportRows, exports);

        LogSummaryText.Text = $"{inspections.Length} inspections / {reviews.Length} review events / {exports.Length} exports";
        StatusText.Text = "Loaded real SQLite log records.";
    }

    private LogFilter BuildFilter()
    {
        return new LogFilter
        {
            FromDate = FromDatePicker.SelectedDate,
            ToDate = ToDatePicker.SelectedDate,
            BoardProgram = NullIfBlank(BoardFilterText.Text),
            OperatorId = NullIfBlank(OperatorFilterText.Text),
            Result = (ResultFilterCombo.SelectedItem as ComboBoxItem)?.Content?.ToString(),
        };
    }

    private void OnExportInspectionHistoryClick(object sender, RoutedEventArgs e)
    {
        if (_inspectionRows.Count == 0)
        {
            MessageBox.Show("No inspection history rows match the current filters.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!ConfirmExport("Export filtered inspection history to CSV?"))
            return;

        var dialog = SaveCsvDialog("inspection_history");
        if (dialog.ShowDialog() != true)
            return;

        File.WriteAllText(dialog.FileName, BuildInspectionCsv(_inspectionRows), CsvEncoding);
        AoiDatabase.RecordExport("InspectionHistoryCsv", dialog.FileName);
        WorkflowState.Instance.AddEvent("EXPORT", $"Inspection history CSV exported: {Path.GetFileName(dialog.FileName)}");
        RefreshAfterExport($"Inspection history CSV exported: {dialog.FileName}");
    }

    private void OnExportReviewLogClick(object sender, RoutedEventArgs e)
    {
        if (_reviewRows.Count == 0)
        {
            MessageBox.Show("No review log rows match the current filters.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!ConfirmExport("Export filtered review/disposition log to CSV?"))
            return;

        var dialog = SaveCsvDialog("review_log");
        if (dialog.ShowDialog() != true)
            return;

        File.WriteAllText(dialog.FileName, BuildReviewCsv(_reviewRows), CsvEncoding);
        AoiDatabase.RecordExport("ReviewLogCsv", dialog.FileName);
        WorkflowState.Instance.AddEvent("EXPORT", $"Review log CSV exported: {Path.GetFileName(dialog.FileName)}");
        RefreshAfterExport($"Review log CSV exported: {dialog.FileName}");
    }

    private async void OnExportAnnotatedOverlaysClick(object sender, RoutedEventArgs e)
    {
        if (_workCts is not null)
        {
            MessageBox.Show("An export or utility task is already running.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var rows = _inspectionRows.Where(r => File.Exists(r.SampleImagePath)).ToArray();
        if (rows.Length == 0)
        {
            MessageBox.Show("No inspection rows with accessible sample images match the current filters.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!ConfirmExport($"Export annotated overlays for {rows.Length} filtered inspection image(s)?"))
            return;

        var dialog = new OpenFolderDialog
        {
            Title = "Select annotated overlay export folder",
            Multiselect = false,
        };

        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FolderName))
            return;

        var cts = BeginWork("Exporting annotated overlays...");
        var progress = new Progress<WorkProgress>(UpdateProgress);
        try
        {
            var result = await Task.Run(() => ExportAnnotatedOverlays(rows, dialog.FolderName, cts.Token, progress), cts.Token);
            AoiDatabase.RecordExport("AnnotatedImageOverlays", dialog.FolderName, result.Errors.Count == 0 ? "OK" : "WARN");
            WorkflowState.Instance.AddEvent("EXPORT", $"Annotated overlays exported: {result.Count} image(s), {result.Errors.Count} issue(s).");
            LogErrors("EXPORT_ERROR", result.Errors);
            RefreshAfterExport($"Annotated overlays exported: {result.Count} image(s), {result.Errors.Count} issue(s) to {dialog.FolderName}");
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Annotated overlay export canceled.";
            WorkflowState.Instance.AddEvent("EXPORT", "Annotated overlay export canceled by user.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            HandleWorkError("Annotated overlay export failed", ex, "EXPORT_ERROR");
        }
        finally
        {
            EndWork();
        }
    }

    private async void OnExportCustomerPackageClick(object sender, RoutedEventArgs e)
    {
        if (_workCts is not null)
        {
            MessageBox.Show("An export or utility task is already running.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!ConfirmExport("Create a Stage 1 customer-demo evidence package from the current filtered logs and latest validation run?"))
            return;

        var dialog = new OpenFolderDialog
        {
            Title = "Select Stage 1 customer package output folder",
            Multiselect = false,
        };

        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FolderName))
            return;

        var cts = BeginWork("Creating customer package...");
        var progress = new Progress<WorkProgress>(UpdateProgress);
        var inspectionRows = _inspectionRows.ToArray();
        var reviewRows = _reviewRows.ToArray();
        var filter = BuildFilter();

        try
        {
            var result = await Task.Run(() =>
            {
                cts.Token.ThrowIfCancellationRequested();
                var warnings = new List<string>();
                var packageDir = Path.Combine(dialog.FolderName, $"stage1_customer_package_{DateTime.Now:yyyyMMdd_HHmmss}");
                var validationDir = Path.Combine(packageDir, "validation");
                var logsDir = Path.Combine(packageDir, "logs");
                var overlayDir = Path.Combine(packageDir, "annotated_overlays");
                var summariesDir = Path.Combine(packageDir, "summaries");

                Directory.CreateDirectory(packageDir);
                Directory.CreateDirectory(validationDir);
                Directory.CreateDirectory(logsDir);
                Directory.CreateDirectory(overlayDir);
                Directory.CreateDirectory(summariesDir);

                ((IProgress<WorkProgress>)progress).Report(new WorkProgress(1, 6, "Loading latest validation results..."));
                var latestRun = AoiDatabase.GetLatestBatchTestRun();
                var validationRows = latestRun is null
                    ? Array.Empty<BatchTestRow>()
                    : AoiDatabase.GetBatchTestResults(latestRun.Id).Select(BatchTestRow.FromRecord).ToArray();

                if (latestRun is null)
                    warnings.Add("No Stage 1 validation batch run was found. validation/customer_validation_report.md and validation/validation_results.csv were generated with no validation rows.");
                else if (validationRows.Length == 0)
                    warnings.Add($"Latest Stage 1 validation run {latestRun.Id} has no persisted result rows.");

                ((IProgress<WorkProgress>)progress).Report(new WorkProgress(2, 6, "Writing validation artifacts..."));
                File.WriteAllText(Path.Combine(validationDir, "customer_validation_report.md"), BuildStage1ValidationReport(latestRun, validationRows, warnings), CsvEncoding);
                File.WriteAllText(Path.Combine(validationDir, "validation_results.csv"), BatchValidationService.BuildResultsCsv(validationRows), CsvEncoding);

                if (inspectionRows.Length == 0)
                    warnings.Add("No inspection history rows matched the current filters. logs/inspection_history.csv contains only headers.");
                if (reviewRows.Length == 0)
                    warnings.Add("No review/disposition rows matched the current filters. logs/review_disposition_log.csv contains only headers.");

                ((IProgress<WorkProgress>)progress).Report(new WorkProgress(3, 6, "Writing log CSV files..."));
                File.WriteAllText(Path.Combine(logsDir, "inspection_history.csv"), BuildInspectionCsv(inspectionRows), CsvEncoding);
                File.WriteAllText(Path.Combine(logsDir, "review_disposition_log.csv"), BuildReviewCsv(reviewRows), CsvEncoding);

                var overlays = ExportAnnotatedOverlays(
                    inspectionRows.Where(r => File.Exists(r.SampleImagePath)).ToArray(),
                    overlayDir,
                    cts.Token,
                    progress,
                    completedOffset: 3,
                    totalOffset: 6);
                warnings.AddRange(overlays.Errors);
                if (overlays.Count == 0)
                    warnings.Add("No annotated overlay images were generated. This usually means no filtered inspection rows had accessible sample image paths.");

                cts.Token.ThrowIfCancellationRequested();
                ((IProgress<WorkProgress>)progress).Report(new WorkProgress(5, 6, "Writing configuration and database summaries..."));
                var configuration = InspectionModelConfigurationService.Load();
                File.WriteAllText(Path.Combine(summariesDir, "model_engine_configuration.txt"), BuildModelConfigurationSummary(configuration, warnings), CsvEncoding);
                File.WriteAllText(Path.Combine(summariesDir, "database_health_summary.txt"), BuildDatabaseHealthSummary(inspectionRows.Length, reviewRows.Length, warnings), CsvEncoding);

                cts.Token.ThrowIfCancellationRequested();
                ((IProgress<WorkProgress>)progress).Report(new WorkProgress(6, 6, "Writing package README..."));
                File.WriteAllText(Path.Combine(validationDir, "customer_validation_report.md"), BuildStage1ValidationReport(latestRun, validationRows, warnings), CsvEncoding);
                File.WriteAllText(
                    Path.Combine(packageDir, "README.md"),
                    BuildStage1PackageReadme(packageDir, latestRun, validationRows.Length, overlays.Count, inspectionRows.Length, reviewRows.Length, warnings, filter),
                    CsvEncoding);
                File.WriteAllText(Path.Combine(packageDir, "warnings.txt"), BuildWarningsText(warnings), CsvEncoding);

                return new PackageOutcome(packageDir, overlays.Count, warnings);
            }, cts.Token);

            AoiDatabase.RecordExport("Stage1CustomerPackage", result.PackageDir, result.Warnings.Count == 0 ? "OK" : "WARN");
            WorkflowState.Instance.AddEvent("EXPORT", $"Stage 1 customer package exported: {Path.GetFileName(result.PackageDir)}, overlays={result.OverlayCount}, warnings={result.Warnings.Count}.");
            LogErrors("EXPORT_WARNING", result.Warnings);
            RefreshAfterExport($"Stage 1 customer package exported: {result.PackageDir}. Warnings: {result.Warnings.Count}.");
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Stage 1 customer package export canceled.";
            WorkflowState.Instance.AddEvent("EXPORT", "Stage 1 customer package export canceled by user.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            HandleWorkError("Stage 1 customer package export failed", ex, "EXPORT_ERROR");
        }
        finally
        {
            EndWork();
        }
    }

    private void OnVerifyImagePathsClick(object sender, RoutedEventArgs e)
    {
        if (!ConfirmExport("Run image path verification and record the utility report?"))
            return;

        var exportsDir = EnsureExportsDir();
        var reportPath = Path.Combine(exportsDir, $"image_path_verification_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        var sb = new StringBuilder();
        var inaccessible = 0;

        foreach (var row in _inspectionRows)
        {
            CheckPath("Sample", row.SampleImagePath, row.Id, sb, ref inaccessible);
            if (!string.IsNullOrWhiteSpace(row.GoldenImagePath))
                CheckPath("Golden", row.GoldenImagePath, row.Id, sb, ref inaccessible);
        }

        if (sb.Length == 0)
            sb.AppendLine("No inaccessible image paths detected for the current filtered inspection rows.");

        sb.AppendLine();
        sb.AppendLine($"CheckedAt: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"RowsChecked: {_inspectionRows.Count}");
        sb.AppendLine($"Issues: {inaccessible}");
        File.WriteAllText(reportPath, sb.ToString());

        AoiDatabase.RecordExport("ImagePathVerification", reportPath, inaccessible == 0 ? "OK" : "WARN");
        WorkflowState.Instance.AddEvent("UTILITY", $"Image path verification completed. Issues={inaccessible}.");
        RefreshAfterExport($"Image path verification complete. Issues={inaccessible}. Report: {reportPath}");
    }

    private async void OnRunDbIntegrityCheckClick(object sender, RoutedEventArgs e)
    {
        if (_workCts is not null)
        {
            MessageBox.Show("An export or utility task is already running.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!ConfirmExport("Run SQLite integrity check and record the report?"))
            return;

        var cts = BeginWork("Running SQLite integrity check...");
        try
        {
            var result = await Task.Run(() =>
            {
                cts.Token.ThrowIfCancellationRequested();
                var exportsDir = EnsureExportsDir();
                var reportPath = Path.Combine(exportsDir, $"db_integrity_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                var integrity = AoiDatabase.RunIntegrityCheck();
                var status = string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase) ? "OK" : "WARN";

                var sb = new StringBuilder();
                sb.AppendLine($"SQLiteIntegrityCheck: {integrity}");
                sb.AppendLine($"DatabasePath: {AoiDatabase.DatabasePath}");
                sb.AppendLine($"ImageVaultPath: {AoiDatabase.ImageVaultPath}");
                sb.AppendLine($"InspectionRowsVisible: {_inspectionRows.Count}");
                sb.AppendLine($"ReviewRowsVisible: {_reviewRows.Count}");
                sb.AppendLine($"AutoArchivePolicy: copy-only archive for logs older than 30 days; source rows remain queryable.");
                sb.AppendLine($"CheckedAt: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                File.WriteAllText(reportPath, sb.ToString());
                return new IntegrityOutcome(reportPath, integrity, status);
            }, cts.Token);

            AoiDatabase.RecordExport("DatabaseIntegrityReport", result.ReportPath, result.Status);
            WorkflowState.Instance.AddEvent("UTILITY", $"DB integrity check result: {result.Integrity}.");
            RefreshAfterExport($"DB integrity check complete: {result.Integrity}. Report: {result.ReportPath}");
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "DB integrity check canceled.";
            WorkflowState.Instance.AddEvent("UTILITY", "DB integrity check canceled by user.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            HandleWorkError("DB integrity check failed", ex, "UTILITY_ERROR");
        }
        finally
        {
            EndWork();
        }
    }

    private void OnOpenDatabaseHealthClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this)?.DataContext is MainViewModel vm)
            vm.CurrentPage = "spc";
    }

    private async void OnRebuildImageIndexClick(object sender, RoutedEventArgs e)
    {
        if (_workCts is not null)
        {
            MessageBox.Show("An export or utility task is already running.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!ConfirmExport("Rebuild the image index for the current image vault and filtered inspection paths?"))
            return;

        var cts = BeginWork("Rebuilding image index...");
        var progress = new Progress<WorkProgress>(UpdateProgress);
        var rows = _inspectionRows.ToArray();

        try
        {
            var result = await Task.Run(() => RebuildImageIndex(rows, cts.Token, progress), cts.Token);
            AoiDatabase.RecordExport("ImageIndex", result.Path, result.Errors.Count == 0 ? "OK" : "WARN");
            WorkflowState.Instance.AddEvent("UTILITY", $"Image index rebuilt with {result.Count} entries and {result.Errors.Count} issue(s).");
            LogErrors("UTILITY_ERROR", result.Errors);
            RefreshAfterExport($"Image index rebuilt with {result.Count} entries: {result.Path}");
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Image index rebuild canceled.";
            WorkflowState.Instance.AddEvent("UTILITY", "Image index rebuild canceled by user.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            HandleWorkError("Image index rebuild failed", ex, "UTILITY_ERROR");
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

    private void OnLockActiveRecipeClick(object sender, RoutedEventArgs e)
    {
        var state = WorkflowState.Instance;
        state.IsRecipeLocked = !state.IsRecipeLocked;
        state.AddEvent("SYSTEM", state.IsRecipeLocked ? "Active recipe locked from Reports." : "Active recipe unlocked from Reports.");
        StatusText.Text = state.IsRecipeLocked ? "Active recipe locked." : "Active recipe unlocked.";
        MessageBox.Show(StatusText.Text, "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private ExportOutcome ExportAnnotatedOverlays(
        IReadOnlyCollection<InspectionLogRow> rows,
        string folder,
        CancellationToken token,
        IProgress<WorkProgress> progress,
        int completedOffset = 0,
        int totalOffset = 0)
    {
        Directory.CreateDirectory(folder);
        var errors = new List<string>();
        var exported = 0;
        var index = 0;
        foreach (var row in rows)
        {
            token.ThrowIfCancellationRequested();
            progress.Report(new WorkProgress(
                completedOffset + index,
                Math.Max(1, totalOffset + rows.Count),
                $"Exporting overlay {index + 1} of {rows.Count}..."));

            try
            {
                if (!File.Exists(row.SampleImagePath))
                {
                    errors.Add($"Missing sample image for inspection {row.Id}: {row.SampleImagePath}");
                    continue;
                }

                var bitmap = CreateAnnotatedBitmap(row);
                var path = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(row.ImageName)}_{row.Verdict}_{row.Id}_overlay.png");
                SavePng(bitmap, path);
                exported++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
            {
                errors.Add(FriendlyFileError(row.SampleImagePath, ex));
            }
            finally
            {
                index++;
            }
        }

        progress.Report(new WorkProgress(completedOffset + rows.Count, Math.Max(1, totalOffset + rows.Count), "Annotated overlay export complete."));
        return new ExportOutcome(exported, errors);
    }

    private static RenderTargetBitmap CreateAnnotatedBitmap(InspectionLogRow row)
    {
        var source = LoadBitmap(row.SampleImagePath);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(source, new Rect(0, 0, source.PixelWidth, source.PixelHeight));

            var roi = new Rect(
                row.HotspotX * source.PixelWidth,
                row.HotspotY * source.PixelHeight,
                Math.Max(2, row.HotspotWidth * source.PixelWidth),
                Math.Max(2, row.HotspotHeight * source.PixelHeight));

            var color = row.Verdict == "NG" ? Colors.Red : row.Verdict == "OK" ? Colors.LimeGreen : Colors.Orange;
            var brush = new SolidColorBrush(color);
            var pen = new Pen(brush, Math.Max(3, source.PixelWidth / 300.0));
            dc.DrawRectangle(null, pen, roi);

            var text = new FormattedText(
                $"{row.Verdict} / {row.ScoreDisplay}",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                Math.Max(16, source.PixelWidth / 34.0),
                brush,
                1.0);

            var origin = new Point(Math.Max(0, roi.X), Math.Max(0, roi.Y - text.Height - 8));
            dc.DrawRectangle(Brushes.Black, null, new Rect(origin.X, origin.Y, text.Width + 10, text.Height + 6));
            dc.DrawText(text, new Point(origin.X + 5, origin.Y + 3));
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

    private void RefreshAfterExport(string message)
    {
        LoadLogs();
        StatusText.Text = message;
        MessageBox.Show(message, "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static string BuildInspectionCsv(IEnumerable<InspectionLogRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Id,TimestampUtc,BoardProgram,Operator,Result,Score,Confidence,Engine,ModelVersion,Defect,SampleImage,GoldenImage,DecisionReason");
        foreach (var row in rows)
        {
            sb.AppendLine(string.Join(",",
                row.Id,
                EscapeCsv(row.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture)),
                EscapeCsv(row.BoardProgram),
                EscapeCsv(row.OperatorId),
                EscapeCsv(row.Verdict),
                row.DifferenceScore.ToString("F4", CultureInfo.InvariantCulture),
                row.Confidence.ToString("F4", CultureInfo.InvariantCulture),
                EscapeCsv(row.InspectionEngine),
                EscapeCsv(row.ModelVersion),
                EscapeCsv(row.SuggestedDefect),
                EscapeCsv(row.SampleImagePath),
                EscapeCsv(row.GoldenImagePath),
                EscapeCsv(row.DecisionReason)));
        }

        return sb.ToString();
    }

    private static string BuildReviewCsv(IEnumerable<ReviewLogRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Id,TimestampUtc,Category,Operator,Disposition,Message");
        foreach (var row in rows)
        {
            sb.AppendLine(string.Join(",",
                row.Id,
                EscapeCsv(row.EventTimeUtc.ToString("O", CultureInfo.InvariantCulture)),
                EscapeCsv(row.Category),
                EscapeCsv(row.OperatorId),
                EscapeCsv(row.Disposition),
                EscapeCsv(row.Message)));
        }

        return sb.ToString();
    }

    private static string BuildStage1ValidationReport(BatchTestRunRecord? run, IReadOnlyCollection<BatchTestRow> rows, IReadOnlyList<string> warnings)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Stage 1 Customer Validation Report");
        sb.AppendLine();
        sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Validation run: {(run is null ? "Not available" : run.Id.ToString(CultureInfo.InvariantCulture))}");
        sb.AppendLine($"Engine: {run?.EngineName ?? "Not available"}");
        sb.AppendLine($"ModelVersion: {run?.ModelVersion ?? "Not available"}");
        sb.AppendLine($"Dataset folder: {run?.ImageFolder ?? "Not available"}");
        sb.AppendLine($"Ground-truth CSV: {run?.GroundTruthCsvPath ?? "Not available"}");
        sb.AppendLine($"Total validation rows: {rows.Count}");
        sb.AppendLine();
        sb.AppendLine("## Metrics");
        sb.AppendLine();
        if (run is null)
        {
            sb.AppendLine("No persisted Stage 1 validation run was found.");
        }
        else
        {
            sb.AppendLine($"- Accuracy: {FormatPercent(run.Accuracy)}");
            sb.AppendLine($"- Precision: {FormatPercent(run.Precision)}");
            sb.AppendLine($"- Recall: {FormatPercent(run.Recall)}");
            sb.AppendLine($"- False call rate: {FormatPercent(run.FalseCallRate)}");
            sb.AppendLine($"- Failed rows: {run.FailedCount} of {run.TotalImages}");
        }

        sb.AppendLine();
        sb.AppendLine("## Failed Samples");
        sb.AppendLine();
        var failed = rows.Where(r => r.PassFail == "FAIL").ToArray();
        if (failed.Length == 0)
        {
            sb.AppendLine("No failed validation rows are present in this package.");
        }
        else
        {
            sb.AppendLine("| Image | Ground Truth | Engine Result | Defect Type | Side | RefDes | Lot ID | Board Model |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|");
            foreach (var row in failed)
                sb.AppendLine($"| {EscapeMarkdown(row.Image)} | {EscapeMarkdown(row.GroundTruth)} | {EscapeMarkdown(row.EngineResult)} | {EscapeMarkdown(row.DefectType)} | {EscapeMarkdown(row.Side)} | {EscapeMarkdown(row.RefDes)} | {EscapeMarkdown(row.LotId)} | {EscapeMarkdown(row.BoardModel)} |");
        }

        sb.AppendLine();
        sb.AppendLine("## Warnings");
        sb.AppendLine();
        if (warnings.Count == 0)
            sb.AppendLine("No warnings were recorded while creating the package.");
        else
            foreach (var warning in warnings)
                sb.AppendLine($"- {EscapeMarkdown(warning)}");

        return sb.ToString();
    }

    private static string BuildModelConfigurationSummary(InspectionModelConfiguration configuration, ICollection<string> warnings)
    {
        var status = InspectionModelConfigurationService.GetStatusText();
        if (!configuration.IsOnnxSelected)
            warnings.Add("Inspection engine is the deterministic pixel-difference prototype engine, not a trained production ML model.");
        if (configuration.IsOnnxSelected && !configuration.HasModelFile)
            warnings.Add($"ONNX engine is selected, but the configured model file is missing: {configuration.ModelFilePath}");
        if (!string.IsNullOrWhiteSpace(configuration.LabelMapPath) && !File.Exists(configuration.LabelMapPath))
            warnings.Add($"Configured label-map file is missing: {configuration.LabelMapPath}");

        var sb = new StringBuilder();
        sb.AppendLine("Model / Engine Configuration Summary");
        sb.AppendLine($"GeneratedLocal: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"EngineKey: {configuration.SelectedEngineKey}");
        sb.AppendLine($"EngineStatus: {status}");
        sb.AppendLine($"ModelVersion: {configuration.EffectiveModelVersion}");
        sb.AppendLine($"ModelFilePath: {NullIfEmpty(configuration.ModelFilePath)}");
        sb.AppendLine($"ModelFileExists: {configuration.HasModelFile}");
        sb.AppendLine($"InputImageWidth: {configuration.InputImageWidth}");
        sb.AppendLine($"InputImageHeight: {configuration.InputImageHeight}");
        sb.AppendLine($"ConfidenceThreshold: {configuration.ConfidenceThreshold.ToString("F3", CultureInfo.InvariantCulture)}");
        sb.AppendLine($"LabelMapPath: {NullIfEmpty(configuration.LabelMapPath)}");
        sb.AppendLine($"LabelMapFileExists: {!string.IsNullOrWhiteSpace(configuration.LabelMapPath) && File.Exists(configuration.LabelMapPath)}");
        sb.AppendLine("BuiltInLabelMap:");
        foreach (var label in configuration.BuiltInLabelMap.OrderBy(kvp => kvp.Key))
            sb.AppendLine($"  {label.Key}: {label.Value}");
        sb.AppendLine();
        sb.AppendLine("PrototypeNotice: Stage 1 is a local PoC. The default pixel-difference engine is deterministic evidence generation, not production ML inference.");
        return sb.ToString();
    }

    private static string BuildDatabaseHealthSummary(int visibleInspectionRows, int visibleReviewRows, ICollection<string> warnings)
    {
        var integrity = AoiDatabase.RunIntegrityCheck();
        if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
            warnings.Add($"SQLite integrity check returned '{integrity}'.");

        var sb = new StringBuilder();
        sb.AppendLine("Database Health Summary");
        sb.AppendLine($"GeneratedLocal: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"DatabasePath: {AoiDatabase.DatabasePath}");
        sb.AppendLine($"ImageVaultPath: {AoiDatabase.ImageVaultPath}");
        sb.AppendLine($"SQLiteIntegrityCheck: {integrity}");
        sb.AppendLine($"VisibleInspectionRowsInPackage: {visibleInspectionRows}");
        sb.AppendLine($"VisibleReviewRowsInPackage: {visibleReviewRows}");
        sb.AppendLine("AutoArchivePolicy: Logs older than 30 days are copied into LogArchive during startup. Source rows remain in place.");
        sb.AppendLine();
        sb.AppendLine("Table Counts:");
        foreach (var row in AoiDatabase.GetDatabaseHealthRows())
            sb.AppendLine($"- {row.Table}: {row.Count} ({row.Status})");
        return sb.ToString();
    }

    private static string BuildStage1PackageReadme(
        string packageDir,
        BatchTestRunRecord? run,
        int validationRows,
        int overlayCount,
        int inspectionRows,
        int reviewRows,
        IReadOnlyList<string> warnings,
        LogFilter filter)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Stage 1 Customer Demo Evidence Package");
        sb.AppendLine();
        sb.AppendLine("This folder contains the current Stage 1 PoC evidence exported from AOI Monitor for customer review. It is safe to open outside the application.");
        sb.AppendLine();
        sb.AppendLine("## Contents");
        sb.AppendLine();
        sb.AppendLine("- `validation/customer_validation_report.md` - Summary report for the latest persisted Stage 1 validation run.");
        sb.AppendLine("- `validation/validation_results.csv` - Row-level validation results from the latest persisted Stage 1 validation run.");
        sb.AppendLine("- `annotated_overlays/` - Generated PNG overlays for filtered inspection rows with accessible sample images.");
        sb.AppendLine("- `logs/inspection_history.csv` - Filtered SQLite inspection history.");
        sb.AppendLine("- `logs/review_disposition_log.csv` - Filtered review and disposition event log.");
        sb.AppendLine("- `summaries/model_engine_configuration.txt` - Active model/engine configuration and prototype status.");
        sb.AppendLine("- `summaries/database_health_summary.txt` - SQLite health, table counts, and archive policy summary.");
        sb.AppendLine("- `warnings.txt` - Missing optional items or non-blocking export issues.");
        sb.AppendLine();
        sb.AppendLine("## Package Summary");
        sb.AppendLine();
        sb.AppendLine($"- Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- Package path: `{packageDir}`");
        sb.AppendLine($"- Validation run: {(run is null ? "Not available" : run.Id.ToString(CultureInfo.InvariantCulture))}");
        sb.AppendLine($"- Validation rows: {validationRows}");
        sb.AppendLine($"- Annotated overlays: {overlayCount}");
        sb.AppendLine($"- Inspection history rows: {inspectionRows}");
        sb.AppendLine($"- Review/disposition rows: {reviewRows}");
        sb.AppendLine($"- Warnings: {warnings.Count}");
        sb.AppendLine();
        sb.AppendLine("## Applied Log Filters");
        sb.AppendLine();
        sb.AppendLine($"- From date: {filter.FromDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "none"}");
        sb.AppendLine($"- To date: {filter.ToDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "none"}");
        sb.AppendLine($"- Board/model: {filter.BoardProgram ?? "ALL"}");
        sb.AppendLine($"- Operator: {filter.OperatorId ?? "ALL"}");
        sb.AppendLine($"- Result: {filter.Result ?? "ALL"}");
        sb.AppendLine();
        sb.AppendLine("## Prototype / Planned Scope");
        sb.AppendLine();
        sb.AppendLine("Stage 1 is a local proof of concept focused on review workflow, deterministic image comparison, SQLite persistence, and evidence export. Live AOI hardware, cameras, lighting control, PLC/robot/handler integration, MES/ERP integration, production database integration, and trained ML inference are planned later-stage work unless separately configured.");
        sb.AppendLine();
        sb.AppendLine("Missing optional inputs, such as absent validation runs or inaccessible image paths, are recorded in `warnings.txt` and do not prevent package creation.");
        return sb.ToString();
    }

    private static string BuildWarningsText(IReadOnlyList<string> warnings)
    {
        if (warnings.Count == 0)
            return "No warnings were recorded while creating this Stage 1 customer package." + Environment.NewLine;

        var sb = new StringBuilder();
        sb.AppendLine("Stage 1 Customer Package Warnings");
        sb.AppendLine();
        foreach (var warning in warnings)
            sb.AppendLine($"- {warning}");
        return sb.ToString();
    }

    private static SaveFileDialog SaveCsvDialog(string stem)
    {
        return new SaveFileDialog
        {
            Title = $"Export {stem.Replace('_', ' ')}",
            Filter = "CSV file|*.csv",
            FileName = $"{stem}_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
        };
    }

    private static bool ConfirmExport(string message)
    {
        return MessageBox.Show(
            message,
            "Confirm Export",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question) == MessageBoxResult.Yes;
    }

    private static string EnsureExportsDir()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "exports");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void ReplaceRows<T>(ObservableCollection<T> target, IEnumerable<T> rows)
    {
        target.Clear();
        foreach (var row in rows)
            target.Add(row);
    }

    private static string? NullIfBlank(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NullIfEmpty(string value)
        => string.IsNullOrWhiteSpace(value) ? "(not configured)" : value;

    private static string FormatPercent(double value)
        => value.ToString("P1", CultureInfo.InvariantCulture);

    private static string EscapeCsv(string value)
        => $"\"{value.Replace("\"", "\"\"")}\"";

    private static string EscapeMarkdown(string value)
        => value.Replace("|", "\\|", StringComparison.Ordinal).Replace(Environment.NewLine, " ", StringComparison.Ordinal);

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

    private void HandleWorkError(string title, Exception ex, string category)
    {
        var message = ex is UnauthorizedAccessException
            ? $"{title}: export folder or database access was denied."
            : $"{title}: {ex.Message}";
        StatusText.Text = message;
        WorkflowState.Instance.AddEvent(category, message);
        MessageBox.Show(message, "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static void LogErrors(string category, IReadOnlyList<string> errors)
    {
        foreach (var error in errors.Take(40))
            WorkflowState.Instance.AddEvent(category, error);
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

    private static IndexOutcome RebuildImageIndex(IReadOnlyCollection<InspectionLogRow> rows, CancellationToken token, IProgress<WorkProgress> progress)
    {
        var exportsDir = EnsureExportsDir();
        var file = Path.Combine(exportsDir, $"image_index_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();

        if (Directory.Exists(AoiDatabase.ImageVaultPath))
        {
            try
            {
                foreach (var path in Directory.EnumerateFiles(AoiDatabase.ImageVaultPath, "*.*", SearchOption.AllDirectories))
                {
                    token.ThrowIfCancellationRequested();
                    if (ImageExtensions.Contains(Path.GetExtension(path)))
                        paths.Add(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"Image vault cannot be fully indexed: {ex.Message}");
            }
        }
        else
        {
            errors.Add($"Image vault folder is not available: {AoiDatabase.ImageVaultPath}");
        }

        var checkedRows = 0;
        foreach (var row in rows)
        {
            token.ThrowIfCancellationRequested();
            progress.Report(new WorkProgress(checkedRows, Math.Max(1, rows.Count), $"Indexing inspection image paths {checkedRows + 1} of {rows.Count}..."));
            AddExistingPath(row.SampleImagePath, paths, errors, row.Id, "sample");
            AddExistingPath(row.GoldenImagePath, paths, errors, row.Id, "golden");
            checkedRows++;
        }

        var sb = new StringBuilder();
        sb.AppendLine("Path,Bytes,LastWriteUtc");
        var written = 0;
        foreach (var path in paths.OrderBy(p => p))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(path);
                sb.AppendLine(string.Join(",",
                    EscapeCsv(path),
                    info.Length.ToString(CultureInfo.InvariantCulture),
                    EscapeCsv(info.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture))));
                written++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add(FriendlyFileError(path, ex));
            }
        }

        File.WriteAllText(file, sb.ToString(), CsvEncoding);
        progress.Report(new WorkProgress(rows.Count, Math.Max(1, rows.Count), "Image index rebuild complete."));
        return new IndexOutcome(file, written, errors);
    }

    private static void AddExistingPath(string path, ISet<string> paths, ICollection<string> errors, long inspectionId, string label)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        if (File.Exists(path))
            paths.Add(path);
        else
            errors.Add($"Missing {label} image for inspection {inspectionId}: {path}");
    }

    private static void CheckPath(string label, string path, long id, StringBuilder sb, ref int inaccessible)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            inaccessible++;
            sb.AppendLine($"[MISSING] Inspection {id} {label}: {path}");
        }
    }

    private sealed record WorkProgress(int Completed, int Total, string Message);

    private sealed record ExportOutcome(int Count, IReadOnlyList<string> Errors);

    private sealed record PackageOutcome(string PackageDir, int OverlayCount, IReadOnlyList<string> Warnings);

    private sealed record IntegrityOutcome(string ReportPath, string Integrity, string Status);

    private sealed record IndexOutcome(string Path, int Count, IReadOnlyList<string> Errors);

    public sealed class InspectionLogRow
    {
        public long Id { get; init; }
        public DateTime CreatedAtUtc { get; init; }
        public string TimestampLocal => CreatedAtUtc == DateTime.MinValue ? "--" : CreatedAtUtc.ToLocalTime().ToString("MM-dd HH:mm");
        public string BoardProgram { get; init; } = "UNKNOWN";
        public string OperatorId { get; init; } = "UNKNOWN";
        public string InspectionEngine { get; init; } = "Pixel Difference";
        public string ModelVersion { get; init; } = "UNKNOWN";
        public string SampleImagePath { get; init; } = string.Empty;
        public string GoldenImagePath { get; init; } = string.Empty;
        public string ImageName => string.IsNullOrWhiteSpace(SampleImagePath) ? "--" : Path.GetFileName(SampleImagePath);
        public string Verdict { get; init; } = "REVIEW";
        public double DifferenceScore { get; init; }
        public string ScoreDisplay => $"{DifferenceScore:F1}%";
        public double Confidence { get; init; }
        public string ConfidenceDisplay => Confidence.ToString("P0", CultureInfo.InvariantCulture);
        public string SuggestedDefect { get; init; } = string.Empty;
        public string DecisionReason { get; init; } = string.Empty;
        public double HotspotX { get; init; }
        public double HotspotY { get; init; }
        public double HotspotWidth { get; init; }
        public double HotspotHeight { get; init; }

        public static InspectionLogRow FromRecord(InspectionHistoryRecord record)
        {
            return new InspectionLogRow
            {
                Id = record.Id,
                CreatedAtUtc = record.CreatedAtUtc,
                BoardProgram = record.BoardProgram,
                OperatorId = record.OperatorId,
                InspectionEngine = record.InspectionEngine,
                ModelVersion = record.ModelVersion,
                SampleImagePath = record.SampleImagePath,
                GoldenImagePath = record.GoldenImagePath,
                Verdict = record.Verdict,
                DifferenceScore = record.DifferenceScore,
                Confidence = record.Confidence,
                SuggestedDefect = record.SuggestedDefect,
                DecisionReason = record.DecisionReason,
                HotspotX = record.HotspotX,
                HotspotY = record.HotspotY,
                HotspotWidth = record.HotspotWidth,
                HotspotHeight = record.HotspotHeight,
            };
        }
    }

    public sealed class ReviewLogRow
    {
        public long Id { get; init; }
        public DateTime EventTimeUtc { get; init; }
        public string TimestampLocal => EventTimeUtc == DateTime.MinValue ? "--" : EventTimeUtc.ToLocalTime().ToString("MM-dd HH:mm");
        public string Category { get; init; } = string.Empty;
        public string OperatorId { get; init; } = "UNKNOWN";
        public string Disposition { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;

        public static ReviewLogRow FromRecord(ReviewEventRecord record)
        {
            return new ReviewLogRow
            {
                Id = record.Id,
                EventTimeUtc = record.EventTimeUtc,
                Category = record.Category,
                OperatorId = record.OperatorId,
                Disposition = record.Disposition,
                Message = record.Message,
            };
        }
    }

    public sealed class ExportHistoryRow
    {
        public long Id { get; init; }
        public DateTime CreatedAtUtc { get; init; }
        public string TimestampLocal => CreatedAtUtc == DateTime.MinValue ? "--" : CreatedAtUtc.ToLocalTime().ToString("MM-dd HH:mm");
        public string ExportType { get; init; } = string.Empty;
        public string FilePath { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;

        public static ExportHistoryRow FromRecord(ExportHistoryRecord record)
        {
            return new ExportHistoryRow
            {
                Id = record.Id,
                CreatedAtUtc = record.CreatedAtUtc,
                ExportType = record.ExportType,
                FilePath = record.FilePath,
                Status = record.Status,
            };
        }
    }
}
