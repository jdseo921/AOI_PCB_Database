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

        if (_inspectionRows.Count == 0 && _reviewRows.Count == 0)
        {
            MessageBox.Show("No filtered log records are available for a customer validation package.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!ConfirmExport("Create a customer validation package from the current filtered logs?"))
            return;

        var dialog = new OpenFolderDialog
        {
            Title = "Select customer package output folder",
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
                var packageDir = Path.Combine(dialog.FolderName, $"customer_validation_{DateTime.Now:yyyyMMdd_HHmmss}");
                Directory.CreateDirectory(packageDir);
                var overlayDir = Path.Combine(packageDir, "annotated_overlays");
                Directory.CreateDirectory(overlayDir);

                ((IProgress<WorkProgress>)progress).Report(new WorkProgress(1, 4, "Writing package CSV files..."));
                File.WriteAllText(Path.Combine(packageDir, "inspection_history.csv"), BuildInspectionCsv(inspectionRows), CsvEncoding);
                File.WriteAllText(Path.Combine(packageDir, "review_log.csv"), BuildReviewCsv(reviewRows), CsvEncoding);

                var overlays = ExportAnnotatedOverlays(
                    inspectionRows.Where(r => File.Exists(r.SampleImagePath)).ToArray(),
                    overlayDir,
                    cts.Token,
                    progress,
                    completedOffset: 1,
                    totalOffset: 3);

                cts.Token.ThrowIfCancellationRequested();
                ((IProgress<WorkProgress>)progress).Report(new WorkProgress(4, 4, "Writing package manifest..."));
                File.WriteAllText(
                    Path.Combine(packageDir, "manifest.txt"),
                    BuildPackageManifest(packageDir, overlays.Count, inspectionRows.Length, reviewRows.Length, filter),
                    CsvEncoding);

                return new PackageOutcome(packageDir, overlays.Count, overlays.Errors);
            }, cts.Token);

            AoiDatabase.RecordExport("CustomerValidationPackage", result.PackageDir, result.Errors.Count == 0 ? "OK" : "WARN");
            WorkflowState.Instance.AddEvent("EXPORT", $"Customer validation package exported: {Path.GetFileName(result.PackageDir)}, overlays={result.OverlayCount}, issues={result.Errors.Count}.");
            LogErrors("EXPORT_ERROR", result.Errors);
            RefreshAfterExport($"Customer validation package exported: {result.PackageDir}");
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Customer package export canceled.";
            WorkflowState.Instance.AddEvent("EXPORT", "Customer package export canceled by user.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            HandleWorkError("Customer package export failed", ex, "EXPORT_ERROR");
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

    private static string BuildPackageManifest(string packageDir, int overlayCount, int inspectionRows, int reviewRows, LogFilter filter)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Customer Validation Package");
        sb.AppendLine($"GeneratedLocal: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"PackagePath: {packageDir}");
        sb.AppendLine($"InspectionRows: {inspectionRows}");
        sb.AppendLine($"ReviewRows: {reviewRows}");
        sb.AppendLine($"AnnotatedOverlays: {overlayCount}");
        sb.AppendLine($"FromDate: {filter.FromDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "none"}");
        sb.AppendLine($"ToDate: {filter.ToDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "none"}");
        sb.AppendLine($"BoardProgram: {filter.BoardProgram ?? "ALL"}");
        sb.AppendLine($"Operator: {filter.OperatorId ?? "ALL"}");
        sb.AppendLine($"Result: {filter.Result ?? "ALL"}");
        sb.AppendLine("ArchivePolicy: Logs older than 30 days are copy-archived to LogArchive. Source rows are not deleted.");
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

    private static string EscapeCsv(string value)
        => $"\"{value.Replace("\"", "\"\"")}\"";

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

    private sealed record PackageOutcome(string PackageDir, int OverlayCount, IReadOnlyList<string> Errors);

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
