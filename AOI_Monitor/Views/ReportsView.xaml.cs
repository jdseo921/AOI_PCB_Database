using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
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
    private readonly ObservableCollection<AuditLogRow> _auditRows = new();
    private readonly ObservableCollection<MesSpoolQueueRow> _mesSpoolRows = new();
    private readonly ObservableCollection<FactoryReadinessRow> _factoryReadinessRows = new();
    private CancellationTokenSource? _workCts;

    public ReportsView()
    {
        InitializeComponent();
        InspectionGrid.ItemsSource = _inspectionRows;
        ReviewGrid.ItemsSource = _reviewRows;
        ExportGrid.ItemsSource = _exportRows;
        AuditGrid.ItemsSource = _auditRows;
        MesSpoolGrid.ItemsSource = _mesSpoolRows;
        FactoryReadinessGrid.ItemsSource = _factoryReadinessRows;
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
        RoleFilterCombo.SelectedIndex = 0;
        ActionTypeFilterText.Text = string.Empty;
        LoadLogs();
    }

    private void LoadLogs()
    {
        var filter = BuildFilter();
        var inspections = AoiDatabase.GetInspectionHistory(filter).Select(InspectionLogRow.FromRecord).ToArray();
        var reviews = AoiDatabase.GetReviewEvents(filter).Select(ReviewLogRow.FromRecord).ToArray();
        var exports = AoiDatabase.GetExportHistory()
            .Select(record => ExportHistoryRow.FromRecord(record, AoiDatabase.GetLatestExportVerification(record.Id)))
            .ToArray();
        var audits = AoiDatabase.GetAuditEvents(filter).Select(AuditLogRow.FromRecord).ToArray();
        var mesSpool = AoiDatabase.GetMesSpoolQueue().Select(MesSpoolQueueRow.FromRecord).ToArray();
        var readinessReport = FactoryReadinessService.Evaluate();
        var readiness = readinessReport.Categories.Select(FactoryReadinessRow.FromCategory).ToArray();

        ReplaceRows(_inspectionRows, inspections);
        ReplaceRows(_reviewRows, reviews);
        ReplaceRows(_exportRows, exports);
        ReplaceRows(_auditRows, audits);
        ReplaceRows(_mesSpoolRows, mesSpool);
        ReplaceRows(_factoryReadinessRows, readiness);

        LogSummaryText.Text = $"{inspections.Length} inspections / {reviews.Length} review events / {exports.Length} exports / {audits.Length} audit rows / {mesSpool.Length} MES spool / readiness {readinessReport.OverallStatus}";
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
            UserRole = (RoleFilterCombo.SelectedItem as ComboBoxItem)?.Content?.ToString(),
            ActionCategory = NullIfBlank(ActionTypeFilterText.Text),
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

        try
        {
            File.WriteAllText(dialog.FileName, BuildInspectionCsv(_inspectionRows), CsvEncoding);
            var verified = ExportVerificationService.RecordVerifiedExport("InspectionHistoryCsv", dialog.FileName);
            WorkflowState.Instance.AddEvent("EXPORT", $"Inspection history CSV exported: {Path.GetFileName(dialog.FileName)}");
            RefreshAfterExport($"Inspection history CSV exported: {dialog.FileName}. Verification: {verified.Verification.Status}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            HandleWorkError("Inspection history CSV export failed", ex, "EXPORT_ERROR");
        }
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

        try
        {
            File.WriteAllText(dialog.FileName, BuildReviewCsv(_reviewRows), CsvEncoding);
            var verified = ExportVerificationService.RecordVerifiedExport("ReviewLogCsv", dialog.FileName);
            WorkflowState.Instance.AddEvent("EXPORT", $"Review log CSV exported: {Path.GetFileName(dialog.FileName)}");
            RefreshAfterExport($"Review log CSV exported: {dialog.FileName}. Verification: {verified.Verification.Status}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            HandleWorkError("Review log CSV export failed", ex, "EXPORT_ERROR");
        }
    }

    private void OnExportAuditTrailClick(object sender, RoutedEventArgs e)
    {
        if (_auditRows.Count == 0)
        {
            MessageBox.Show("No audit trail rows match the current filters.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!ConfirmExport("Export filtered audit trail to CSV for QC documentation?"))
            return;

        var dialog = SaveCsvDialog("audit_trail");
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            File.WriteAllText(dialog.FileName, BuildAuditCsv(_auditRows), CsvEncoding);
            var verified = ExportVerificationService.RecordVerifiedExport("AuditTrailCsv", dialog.FileName);
            WorkflowState.Instance.AddEvent("EXPORT", $"Audit trail CSV exported: {Path.GetFileName(dialog.FileName)}");
            RefreshAfterExport($"Audit trail CSV exported: {dialog.FileName}. Verification: {verified.Verification.Status}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            HandleWorkError("Audit trail CSV export failed", ex, "EXPORT_ERROR");
        }
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
            var verified = ExportVerificationService.RecordVerifiedExport(
                "AnnotatedImageOverlays",
                dialog.FolderName,
                result.Errors.Count == 0 ? "OK" : "WARN");
            WorkflowState.Instance.AddEvent("EXPORT", $"Annotated overlays exported: {result.Count} image(s), {result.Errors.Count} issue(s).");
            LogErrors("EXPORT_ERROR", result.Errors);
            RefreshAfterExport($"Annotated overlays exported: {result.Count} image(s), {result.Errors.Count} issue(s) to {dialog.FolderName}. Verification: {verified.Verification.Status}.");
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
        var auditRows = _auditRows.ToArray();
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
                    warnings.Add("No Stage 1 validation batch run was found. validation/customer_validation_report.html and validation/validation_results.csv were generated with no validation rows.");
                else if (validationRows.Length == 0)
                    warnings.Add($"Latest Stage 1 validation run {latestRun.Id} has no persisted result rows.");

                ((IProgress<WorkProgress>)progress).Report(new WorkProgress(2, 6, "Writing validation artifacts..."));
                File.WriteAllText(Path.Combine(validationDir, "validation_results.csv"), BatchValidationService.BuildResultsCsv(validationRows), CsvEncoding);

                if (inspectionRows.Length == 0)
                    warnings.Add("No inspection history rows matched the current filters. logs/inspection_history.csv contains only headers.");
                if (reviewRows.Length == 0)
                    warnings.Add("No review/disposition rows matched the current filters. logs/review_disposition_log.csv contains only headers.");

                ((IProgress<WorkProgress>)progress).Report(new WorkProgress(3, 6, "Writing log CSV files..."));
                File.WriteAllText(Path.Combine(logsDir, "inspection_history.csv"), BuildInspectionCsv(inspectionRows), CsvEncoding);
                File.WriteAllText(Path.Combine(logsDir, "review_disposition_log.csv"), BuildReviewCsv(reviewRows), CsvEncoding);
                File.WriteAllText(Path.Combine(logsDir, "audit_trail.csv"), BuildAuditCsv(auditRows), CsvEncoding);
                if (auditRows.Length == 0)
                    warnings.Add("No audit trail rows matched the current filters. logs/audit_trail.csv contains only headers.");

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
                ((IProgress<WorkProgress>)progress).Report(new WorkProgress(4, 6, "Preparing validation report sample images..."));
                var sampleImageDir = Path.Combine(validationDir, "sample_annotated_images");
                var sampleImages = ValidationReportAssetService.ExportSampleAnnotatedImages(
                    validationRows,
                    sampleImageDir,
                    "sample_annotated_images",
                    maxCount: 8,
                    cts.Token);
                warnings.AddRange(sampleImages.Warnings);

                cts.Token.ThrowIfCancellationRequested();
                ((IProgress<WorkProgress>)progress).Report(new WorkProgress(5, 6, "Writing configuration and database summaries..."));
                var configuration = InspectionModelConfigurationService.Load();
                File.WriteAllText(Path.Combine(summariesDir, "model_engine_configuration.txt"), BuildModelConfigurationSummary(configuration, warnings), CsvEncoding);
                File.WriteAllText(Path.Combine(summariesDir, "database_health_summary.txt"), BuildDatabaseHealthSummary(inspectionRows.Length, reviewRows.Length, warnings), CsvEncoding);
                File.WriteAllText(Path.Combine(summariesDir, "recipe_revision_summary.txt"), BuildRecipeRevisionSummary(warnings), CsvEncoding);
                File.WriteAllText(Path.Combine(summariesDir, "calibration_profile_summary.txt"), BuildCalibrationProfileSummary(warnings), CsvEncoding);

                cts.Token.ThrowIfCancellationRequested();
                ((IProgress<WorkProgress>)progress).Report(new WorkProgress(6, 6, "Writing package README..."));
                var reportPath = Path.Combine(validationDir, "customer_validation_report.html");
                var reportContext = BuildCustomerValidationReportContext(latestRun, validationRows, sampleImages.Images, warnings);
                File.WriteAllText(reportPath, CustomerValidationReportService.BuildHtml(reportContext), CsvEncoding);
                File.WriteAllText(Path.Combine(validationDir, "customer_validation_report.md"), CustomerValidationReportService.BuildMarkdown(reportContext), CsvEncoding);
                File.WriteAllText(
                    Path.Combine(validationDir, "customer_validation_report_print_to_pdf.txt"),
                    CustomerValidationReportService.BuildPrintToPdfInstructions(reportPath),
                    CsvEncoding);
                File.WriteAllText(
                    Path.Combine(packageDir, "README.md"),
                    BuildStage1PackageReadme(packageDir, latestRun, validationRows.Length, overlays.Count, inspectionRows.Length, reviewRows.Length, auditRows.Length, warnings, filter),
                    CsvEncoding);
                File.WriteAllText(Path.Combine(packageDir, "warnings.txt"), BuildWarningsText(warnings), CsvEncoding);
                WritePackageManifest(packageDir, warnings);

                return new PackageOutcome(packageDir, reportPath, overlays.Count, warnings);
            }, cts.Token);

            var packageVerification = ExportVerificationService.RecordVerifiedExport(
                "Stage1CustomerPackage",
                result.PackageDir,
                result.Warnings.Count == 0 ? "OK" : "WARN");
            var reportVerification = ExportVerificationService.RecordVerifiedExport(
                "CustomerValidationHtmlReport",
                result.ReportPath,
                result.Warnings.Count == 0 ? "OK" : "WARN");
            WorkflowState.Instance.AddEvent("EXPORT", $"Stage 1 customer package exported: {Path.GetFileName(result.PackageDir)}, overlays={result.OverlayCount}, warnings={result.Warnings.Count}.");
            LogErrors("EXPORT_WARNING", result.Warnings);
            PackagePathText.Text = $"Latest customer package: {result.PackageDir}";
            RefreshAfterExport($"Stage 1 customer package exported: {result.PackageDir}. Warnings: {result.Warnings.Count}. Package verification: {packageVerification.Verification.Status}; report verification: {reportVerification.Verification.Status}.");
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

    private void OnExportFactoryReadinessPackageClick(object sender, RoutedEventArgs e)
    {
        if (!WorkflowState.Instance.TryAuthorize(RoleAuthorization.CanExportLogs, "Exporting factory readiness Go/No-Go package", out var permissionMessage))
        {
            MessageBox.Show(permissionMessage, "Permission denied", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!ConfirmExport("Export a management/customer Factory Readiness Go/No-Go package?"))
            return;

        try
        {
            var result = FactoryReadinessService.ExportGoNoGoPackage();
            WorkflowState.Instance.AddEvent("FACTORY_READINESS_EXPORT", $"Factory readiness package exported: {Path.GetFileName(result.PackageFolder)}.");
            RefreshAfterExport($"Factory readiness package exported: {result.PackageFolder}. Summary: {result.SummaryHtmlPath}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            HandleWorkError("Factory readiness package export failed", ex, "FACTORY_READINESS_EXPORT_ERROR");
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

        var verified = ExportVerificationService.RecordVerifiedExport("ImagePathVerification", reportPath, inaccessible == 0 ? "OK" : "WARN");
        WorkflowState.Instance.AddEvent("UTILITY", $"Image path verification completed. Issues={inaccessible}.");
        RefreshAfterExport($"Image path verification complete. Issues={inaccessible}. Report: {reportPath}. Verification: {verified.Verification.Status}.");
    }

    private void OnVerifySelectedExportClick(object sender, RoutedEventArgs e)
    {
        if (ExportGrid.SelectedItem is not ExportHistoryRow row)
        {
            MessageBox.Show("Select an export history row first.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var result = ExportVerificationService.Verify(row.FilePath, row.ExportType, row.Id, persist: true);
            var reportFolder = Path.Combine(EnsureExportsDir(), "export_verification");
            var report = ExportVerificationService.ExportReport(result, reportFolder);
            ExportVerificationService.RecordVerifiedExport("ExportVerificationJsonReport", report.JsonPath);
            ExportVerificationService.RecordVerifiedExport("ExportVerificationTextReport", report.TextPath);
            WorkflowState.Instance.AddEvent(
                result.Status == ExportVerificationStatus.OK ? "EXPORT_VERIFY" : "EXPORT_VERIFY_WARN",
                $"Export verification {result.Status}: {row.ExportType}; sha256={result.Sha256}; path={row.FilePath}");
            RefreshAfterExport($"Export verification {result.Status}. SHA-256: {ShortHash(result.Sha256)}. Report: {report.JsonPath}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
        {
            HandleWorkError("Export verification failed", ex, "EXPORT_VERIFY_ERROR");
        }
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
        var progress = new Progress<WorkProgress>(UpdateProgress);
        try
        {
            var result = await Task.Run(() =>
            {
                cts.Token.ThrowIfCancellationRequested();
                ((IProgress<WorkProgress>)progress).Report(new WorkProgress(1, 4, "Preparing SQLite integrity report..."));
                var exportsDir = EnsureExportsDir();
                var reportPath = Path.Combine(exportsDir, $"db_integrity_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                ((IProgress<WorkProgress>)progress).Report(new WorkProgress(2, 4, "Running SQLite integrity check..."));
                var integrity = AoiDatabase.RunIntegrityCheck();
                var status = string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase) ? "OK" : "WARN";

                cts.Token.ThrowIfCancellationRequested();
                ((IProgress<WorkProgress>)progress).Report(new WorkProgress(3, 4, "Writing SQLite integrity report..."));
                var sb = new StringBuilder();
                sb.AppendLine($"SQLiteIntegrityCheck: {integrity}");
                sb.AppendLine($"DatabasePath: {AoiDatabase.DatabasePath}");
                sb.AppendLine($"ImageVaultPath: {AoiDatabase.ImageVaultPath}");
                sb.AppendLine($"InspectionRowsVisible: {_inspectionRows.Count}");
                sb.AppendLine($"ReviewRowsVisible: {_reviewRows.Count}");
                sb.AppendLine($"AutoArchivePolicy: copy-only archive for logs older than 30 days; source rows remain queryable.");
                sb.AppendLine($"CheckedAt: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                File.WriteAllText(reportPath, sb.ToString());
                ((IProgress<WorkProgress>)progress).Report(new WorkProgress(4, 4, "SQLite integrity check complete."));
                return new IntegrityOutcome(reportPath, integrity, status);
            }, cts.Token);

            var verified = ExportVerificationService.RecordVerifiedExport("DatabaseIntegrityReport", result.ReportPath, result.Status);
            WorkflowState.Instance.AddEvent("UTILITY", $"DB integrity check result: {result.Integrity}.");
            RefreshAfterExport($"DB integrity check complete: {result.Integrity}. Report: {result.ReportPath}. Verification: {verified.Verification.Status}.");
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
            var verified = ExportVerificationService.RecordVerifiedExport("ImageIndex", result.Path, result.Errors.Count == 0 ? "OK" : "WARN");
            WorkflowState.Instance.AddEvent("UTILITY", $"Image index rebuilt with {result.Count} entries and {result.Errors.Count} issue(s).");
            LogErrors("UTILITY_ERROR", result.Errors);
            RefreshAfterExport($"Image index rebuilt with {result.Count} entries: {result.Path}. Verification: {verified.Verification.Status}.");
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

    private async void OnRunSoakTestClick(object sender, RoutedEventArgs e)
    {
        if (_workCts is not null)
        {
            MessageBox.Show("An export or utility task is already running.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!WorkflowState.Instance.TryAuthorize(RoleAuthorization.CanUseMaintenanceActions, "Running the local soak-test mode", out var permissionMessage))
        {
            MessageBox.Show(permissionMessage, "Permission denied", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var defaultEngine = InspectionModelConfigurationService.Load().SelectedEngineKey;
        var dialog = new SoakTestDialog(EnsureExportsDir(), defaultEngine)
        {
            Owner = Window.GetWindow(this),
        };

        if (dialog.ShowDialog() != true || dialog.Options is null)
            return;

        var state = WorkflowState.Instance;
        var options = dialog.Options with
        {
            OperatorId = state.OperatorWithRole,
            BoardModel = state.BoardProgram,
            LotId = "SOAK-TEST",
        };

        var cts = BeginWork("Running soak test...");
        var progress = new Progress<SoakTestProgress>(p => UpdateProgress(new WorkProgress(p.ElapsedSeconds, p.TotalSeconds, p.Message)));

        try
        {
            var result = await SoakTestService.RunAsync(options, progress, cts.Token);
            SoakTestService.Persist(result, state.OperatorWithRole);
            var reportPath = SoakTestService.WriteHtmlReport(result, options.OutputFolder);
            var jsonReportPath = SoakTestService.WriteJsonReport(result, options.OutputFolder);
            var status = result.WasCanceled
                ? "CANCELED"
                : result.Errors.Count == 0 ? "OK" : "WARN";

            var verified = ExportVerificationService.RecordVerifiedExport("SoakTestReport", reportPath, status);
            ExportVerificationService.RecordVerifiedExport("SoakTestJsonReport", jsonReportPath, status);
            WorkflowState.Instance.AddEvent("SOAK_TEST", $"Soak test {status}: cycles={result.TotalCycles}, success={result.SuccessfulCycles}, failed={result.FailedCycles}, p95={result.P95InspectionMilliseconds:F0} ms, source={result.SourceKind}, report={Path.GetFileName(reportPath)}.");
            LogErrors("SOAK_TEST_ERROR", result.Errors);
            SoakReportPathText.Text = $"Latest soak-test report: {reportPath}";
            RefreshAfterExport($"Soak test {status}. Cycles={result.TotalCycles}, failed={result.FailedCycles}, p95={result.P95InspectionMilliseconds:F0} ms, source={result.SourceKind}. HTML: {reportPath}. JSON: {jsonReportPath}. Verification: {verified.Verification.Status}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            HandleWorkError("Soak test failed", ex, "SOAK_TEST_ERROR");
        }
        finally
        {
            EndWork();
        }
    }

    private async void OnUploadMesMockClick(object sender, RoutedEventArgs e)
    {
        if (_workCts is not null)
        {
            MessageBox.Show("An export or utility task is already running.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!WorkflowState.Instance.TryAuthorize(RoleAuthorization.CanExportLogs, "Uploading a result to MES integration", out var permissionMessage))
        {
            MessageBox.Show(permissionMessage, "Permission denied", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var row = InspectionGrid.SelectedItem as InspectionLogRow ?? _inspectionRows.FirstOrDefault();
        if (row is null)
        {
            MessageBox.Show("No inspection result is available to upload. Run or save an inspection first.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var settings = MesIntegrationSettingsService.Load();
        if (settings.Mode == MesIntegrationMode.FutureProduction)
        {
            MessageBox.Show("Future Production mode is a planned Stage 4 boundary. Use Mock Local/REST or REST mode for upload testing. OPC UA remains a Stage 4 adapter TODO.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var payload = BuildTraceabilityPayload(row, settings);
        var cts = BeginWork("Uploading result to MES boundary...");
        try
        {
            var outcome = await TraceabilityUploadService.UploadAsync(payload, cts.Token);
            var exportStatus = outcome.Result.Accepted ? "OK" : "ERROR";
            var verified = ExportVerificationService.Verify(outcome.PayloadPath, "MesTraceabilityPayload");
            WorkflowState.Instance.AddEvent(
                outcome.Result.Accepted ? "MES_UPLOAD" : "MES_UPLOAD_ERROR",
                $"MES upload {exportStatus}: {outcome.Result.Message}");
            RefreshAfterExport($"MES upload {exportStatus}. Payload: {outcome.PayloadPath}. Verification: {verified.Status}.");
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "MES upload canceled.";
            WorkflowState.Instance.AddEvent("MES_UPLOAD", "MES upload canceled by user.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            HandleWorkError("MES upload failed", ex, "MES_UPLOAD_ERROR");
        }
        finally
        {
            EndWork();
        }
    }

    private async void OnRetryMesSpoolClick(object sender, RoutedEventArgs e)
    {
        if (_workCts is not null)
        {
            MessageBox.Show("An export or utility task is already running.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!WorkflowState.Instance.TryAuthorize(RoleAuthorization.CanExportLogs, "Retrying MES spool uploads", out var permissionMessage))
        {
            MessageBox.Show(permissionMessage, "Permission denied", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var pending = AoiDatabase.GetPendingMesSpoolItems();
        if (pending.Count == 0)
        {
            MessageBox.Show("No pending MES spool items are ready for retry.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            LoadLogs();
            return;
        }

        var cts = BeginWork("Retrying pending MES spool uploads...");
        try
        {
            var summary = await MesSpoolService.RetryEligibleAsync(100, cts.Token);
            var message = $"MES spool retry complete: attempted={summary.Attempted}, succeeded={summary.Succeeded}, failed={summary.Failed}.";
            WorkflowState.Instance.AddEvent("MES_SPOOL", message);
            RefreshAfterExport(message);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "MES spool retry canceled.";
            WorkflowState.Instance.AddEvent("MES_SPOOL", "MES spool retry canceled by user.");
        }
        finally
        {
            EndWork();
        }
    }

    private async void OnRetrySelectedMesSpoolClick(object sender, RoutedEventArgs e)
    {
        if (_workCts is not null)
        {
            MessageBox.Show("An export or utility task is already running.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!WorkflowState.Instance.TryAuthorize(RoleAuthorization.CanExportLogs, "Retrying selected MES queue items", out var permissionMessage))
        {
            MessageBox.Show(permissionMessage, "Permission denied", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var selected = MesSpoolGrid.SelectedItems.OfType<MesSpoolQueueRow>().ToArray();
        if (selected.Length == 0)
        {
            MessageBox.Show("Select one or more MES queue items to retry.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var retryable = selected
            .Where(row => row.Status is "Pending" or "Failed")
            .Select(row => row.Id)
            .ToArray();
        if (retryable.Length == 0)
        {
            MessageBox.Show("Selected MES queue items are not retryable. Only Pending or Failed items can be retried.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var cts = BeginWork("Retrying selected MES queue items...");
        try
        {
            var summary = await MesSpoolService.RetryItemsAsync(retryable, cts.Token);
            var message = $"Selected MES retry complete: attempted={summary.Attempted}, succeeded={summary.Succeeded}, failed={summary.Failed}.";
            WorkflowState.Instance.AddEvent("MES_SPOOL", message);
            RefreshAfterExport(message);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Selected MES retry canceled.";
            WorkflowState.Instance.AddEvent("MES_SPOOL", "Selected MES retry canceled by user.");
        }
        finally
        {
            EndWork();
        }
    }

    private void OnExportMesQueueReportClick(object sender, RoutedEventArgs e)
    {
        if (!WorkflowState.Instance.TryAuthorize(RoleAuthorization.CanExportLogs, "Exporting MES queue report", out var permissionMessage))
        {
            MessageBox.Show(permissionMessage, "Permission denied", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var report = MesSpoolService.ExportQueueReport();
            WorkflowState.Instance.AddEvent("MES_SPOOL_EXPORT", $"MES queue report exported: {Path.GetFileName(report.HtmlPath)}.");
            RefreshAfterExport($"MES queue report exported. HTML: {report.HtmlPath}. JSON: {report.JsonPath}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            HandleWorkError("MES queue report export failed", ex, "MES_SPOOL_EXPORT_ERROR");
        }
    }

    private void OnAbandonMesSpoolClick(object sender, RoutedEventArgs e)
    {
        if (!WorkflowState.Instance.TryAuthorize(RoleAuthorization.CanUseMaintenanceActions, "Abandoning MES queue items", out var permissionMessage))
        {
            MessageBox.Show(permissionMessage, "Permission denied", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var selected = MesSpoolGrid.SelectedItems.OfType<MesSpoolQueueRow>().ToArray();
        if (selected.Length == 0)
        {
            MessageBox.Show("Select one or more MES queue items to mark Abandoned.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var candidates = selected
            .Where(row => row.Status is "Pending" or "Failed")
            .ToArray();
        if (candidates.Length == 0)
        {
            MessageBox.Show("Selected MES queue items are already terminal. Only Pending or Failed items can be abandoned.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            $"Mark {candidates.Length} MES queue item(s) Abandoned? This is an Admin-only audit action and does not upload payloads.",
            "Confirm MES queue abandon",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
            return;

        try
        {
            foreach (var row in candidates)
            {
                MesSpoolService.MarkAbandoned(
                    row.Id,
                    WorkflowState.Instance.CurrentRole,
                    WorkflowState.Instance.OperatorWithRole,
                    "Abandoned from MES Queue UI.");
            }

            WorkflowState.Instance.AddEvent("MES_SPOOL_ABANDON", $"Marked {candidates.Length} MES queue item(s) Abandoned.");
            RefreshAfterExport($"Marked {candidates.Length} MES queue item(s) Abandoned.");
        }
        catch (UnauthorizedAccessException ex)
        {
            MessageBox.Show(ex.Message, "Permission denied", MessageBoxButton.OK, MessageBoxImage.Warning);
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
        sb.AppendLine("Id,TimestampUtc,BoardProgram,Operator,Result,Score,Confidence,Engine,ModelVersion,ConfidenceThreshold,ModelPath,Defect,SampleImage,GoldenImage,DecisionReason,ImageLoadMs,PreprocessingMs,InferenceMs,OverlayRenderingMs,TotalInspectionMs");
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
                row.ConfidenceThreshold.ToString("F4", CultureInfo.InvariantCulture),
                EscapeCsv(row.ModelFilePath),
                EscapeCsv(row.SuggestedDefect),
                EscapeCsv(row.SampleImagePath),
                EscapeCsv(row.GoldenImagePath),
                EscapeCsv(row.DecisionReason),
                row.ImageLoadMilliseconds.ToString("F1", CultureInfo.InvariantCulture),
                row.PreprocessingMilliseconds.ToString("F1", CultureInfo.InvariantCulture),
                row.InferenceMilliseconds.ToString("F1", CultureInfo.InvariantCulture),
                row.OverlayRenderingMilliseconds.ToString("F1", CultureInfo.InvariantCulture),
                row.TotalInspectionMilliseconds.ToString("F1", CultureInfo.InvariantCulture)));
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

    private static string BuildAuditCsv(IEnumerable<AuditLogRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Id,TimestampUtc,LocalTimestamp,UserId,UserRole,StationId,ActionCategory,ActionDetail,RelatedEntityType,RelatedEntityId,RelatedPath");
        foreach (var row in rows)
        {
            sb.AppendLine(string.Join(",",
                row.Id,
                EscapeCsv(row.TimestampUtc.ToString("O", CultureInfo.InvariantCulture)),
                EscapeCsv(row.LocalTimestamp.ToString("O", CultureInfo.InvariantCulture)),
                EscapeCsv(row.UserId),
                EscapeCsv(row.UserRole),
                EscapeCsv(row.StationId),
                EscapeCsv(row.ActionCategory),
                EscapeCsv(row.ActionDetail),
                EscapeCsv(row.RelatedEntityType),
                EscapeCsv(row.RelatedEntityId),
                EscapeCsv(row.RelatedPath)));
        }

        return sb.ToString();
    }

    private static CustomerValidationReportContext BuildCustomerValidationReportContext(
        BatchTestRunRecord? run,
        IReadOnlyCollection<BatchTestRow> rows,
        IReadOnlyList<ReportImageReference> sampleImages,
        IReadOnlyList<string> warnings)
    {
        var configuration = InspectionModelConfigurationService.Load();
        var state = WorkflowState.Instance;
        var boardModel = CustomerValidationReportService.SummarizeDistinct(rows.Select(row => row.BoardModel));
        if (string.Equals(boardModel, "Not provided", StringComparison.OrdinalIgnoreCase))
            boardModel = state.BoardProgram;

        var timestamp = run?.CreatedAtUtc ?? DateTime.Now;
        if (timestamp.Kind == DateTimeKind.Utc)
            timestamp = timestamp.ToLocalTime();

        return new CustomerValidationReportContext
        {
            StationId = state.StationId,
            UserId = state.CurrentUser.UserId,
            UserRole = state.CurrentRole.ToString(),
            RunId = run is null ? "Not available" : run.Id.ToString(CultureInfo.InvariantCulture),
            TestTimestamp = timestamp,
            BoardModel = boardModel,
            LotId = CustomerValidationReportService.SummarizeDistinct(rows.Select(row => row.LotId)),
            EngineName = run?.EngineName ?? (configuration.IsOnnxSelected ? "ONNX ML Model" : "Pixel Difference Prototype Engine"),
            ModelVersion = run?.ModelVersion ?? configuration.EffectiveModelVersion,
            ModelFileName = string.IsNullOrWhiteSpace(configuration.ModelFilePath)
                ? "Not configured"
                : Path.GetFileName(configuration.ModelFilePath),
            ConfidenceThreshold = configuration.ConfidenceThreshold,
            DatasetFolder = string.IsNullOrWhiteSpace(run?.ImageFolder) ? "Not available" : run.ImageFolder,
            GroundTruthFile = string.IsNullOrWhiteSpace(run?.GroundTruthCsvPath) ? "Not selected" : run.GroundTruthCsvPath,
            Metrics = BatchValidationService.CalculateMetrics(rows),
            PerformanceSummary = BatchValidationService.CalculatePerformanceSummary(rows),
            Rows = rows.ToArray(),
            SampleAnnotatedImages = sampleImages,
            Warnings = warnings.ToArray(),
            DatasetQualitySummary = DatasetQualityService.Analyze(rows),
            CameraAcceptanceSummary = CameraAcceptanceTestService.ToSummary(AoiDatabase.GetLatestCameraAcceptanceRun(realHardwareOnly: true)),
            RobotAcceptanceSummary = RobotAcceptanceTestService.ToSummary(AoiDatabase.GetLatestRobotAcceptanceRun()),
            MesReadinessSummary = MesSpoolService.EvaluateReadiness(),
        };
    }

    private static TraceabilityPayload BuildTraceabilityPayload(InspectionLogRow row, MesIntegrationSettings settings)
    {
        var cameraSettings = CameraSourceSettingsService.Load();
        var lotId = string.IsNullOrWhiteSpace(cameraSettings.LotId) ? "UNKNOWN" : cameraSettings.LotId;
        var timestamp = row.CreatedAtUtc == DateTime.MinValue ? DateTime.UtcNow : row.CreatedAtUtc.ToUniversalTime();
        return new TraceabilityPayload
        {
            IntegrationMode = TraceabilityUploadService.ToDisplay(settings.Mode),
            LotId = lotId,
            BoardModel = string.IsNullOrWhiteSpace(row.BoardProgram) ? cameraSettings.BoardModel : row.BoardProgram,
            SerialNumber = null,
            StationId = WorkflowState.Instance.StationId,
            OperatorId = row.OperatorId,
            Result = row.Verdict,
            TimestampUtc = timestamp,
            DefectSummary = $"{row.SuggestedDefect}; score={row.ScoreDisplay}; confidence={row.ConfidenceDisplay}",
            ImagePath = row.SampleImagePath,
            OverlayPath = string.Empty,
            InspectionEngine = row.InspectionEngine,
            ModelVersion = row.ModelVersion,
            Confidence = row.Confidence,
            Score = row.DifferenceScore,
        };
    }

    private static string BuildStage1ValidationReport(BatchTestRunRecord? run, IReadOnlyCollection<BatchTestRow> rows, IReadOnlyList<string> warnings)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Stage 1 Customer Validation Report");
        sb.AppendLine();
        sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Generated by: {WorkflowState.Instance.OperatorWithRole}");
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
            warnings.Add("Inspection engine is the deterministic Pixel Difference Prototype Engine, not a trained production ML model.");
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
        sb.AppendLine($"InputTensorName: {NullIfEmpty(configuration.InputTensorName)}");
        sb.AppendLine($"OutputTensorName: {NullIfEmpty(configuration.OutputTensorName)}");
        sb.AppendLine($"ConfidenceThreshold: {configuration.ConfidenceThreshold.ToString("F3", CultureInfo.InvariantCulture)}");
        sb.AppendLine($"LabelMapPath: {NullIfEmpty(configuration.LabelMapPath)}");
        sb.AppendLine($"LabelMapFileExists: {!string.IsNullOrWhiteSpace(configuration.LabelMapPath) && File.Exists(configuration.LabelMapPath)}");
        sb.AppendLine("OutputParser: Generic Detection [class,confidence,x,y,width,height]");
        sb.AppendLine("BuiltInLabelMap:");
        foreach (var label in configuration.BuiltInLabelMap.OrderBy(kvp => kvp.Key))
            sb.AppendLine($"  {label.Key}: {label.Value}");
        sb.AppendLine();
        sb.AppendLine("PrototypeNotice: Stage 1 is a local PoC. The default Pixel Difference Prototype Engine is deterministic evidence generation. ONNX ML Model inference is claimed only when a configured local model loads and inference succeeds.");
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

    private static string BuildRecipeRevisionSummary(ICollection<string> warnings)
    {
        var boardProgram = WorkflowState.Instance.BoardProgram;
        var revision = AoiDatabase.GetLatestRecipeRevision(boardProgram);
        if (revision is null)
            warnings.Add($"No recipe revision was found for board program '{boardProgram}'. summaries/recipe_revision_summary.txt was generated with no revision details.");

        var sb = new StringBuilder();
        sb.AppendLine("Recipe Revision Summary");
        sb.AppendLine($"GeneratedLocal: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"BoardProgram: {boardProgram}");
        sb.AppendLine($"RecipeRevisionAvailable: {revision is not null}");

        if (revision is null)
        {
            sb.AppendLine("Status: No persisted recipe revision is available for the active board program.");
            sb.AppendLine("PrototypeNotice: Stage 1 can run the operator workflow without a saved production recipe; customer packages record that condition as a warning.");
            return sb.ToString();
        }

        sb.AppendLine($"RecipeName: {revision.RecipeName}");
        sb.AppendLine($"Revision: {revision.Revision}");
        sb.AppendLine($"OperatorId: {revision.OperatorId}");
        sb.AppendLine($"DetectionPriority: {revision.DetectionPriority}");
        sb.AppendLine($"BackgroundImagePath: {NullIfEmpty(revision.BackgroundImagePath)}");
        sb.AppendLine($"CreatedUtc: {revision.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture)}");

        try
        {
            var document = System.Text.Json.JsonSerializer.Deserialize<RecipeDocument>(revision.RecipeJson);
            sb.AppendLine($"RoiCount: {document?.Rois.Count ?? 0}");
            if (document is not null)
            {
                foreach (var roi in document.Rois.Take(50))
                {
                    sb.AppendLine(string.Join(" | ",
                        $"ROI={roi.Id}",
                        $"Type={roi.RoiType}",
                        $"X={roi.X:F4}",
                        $"Y={roi.Y:F4}",
                        $"W={roi.Width:F4}",
                        $"H={roi.Height:F4}",
                        $"Threshold={roi.AiScoreThreshold:F3}",
                        $"HeightMin={roi.HeightMin:F3}",
                        $"HeightMax={roi.HeightMax:F3}",
                        $"VolumeMin={roi.VolumeMin:F3}",
                        $"VolumeMax={roi.VolumeMax:F3}"));
                }

                if (document.Rois.Count > 50)
                    sb.AppendLine($"RoiListTruncated: true; shown=50; total={document.Rois.Count}");
            }
        }
        catch (System.Text.Json.JsonException ex)
        {
            warnings.Add($"Latest recipe revision {revision.Id} could not be parsed for ROI details: {ex.Message}");
            sb.AppendLine($"RoiParseStatus: Failed - {ex.Message}");
        }

        sb.AppendLine("PrototypeNotice: Recipe data reflects the local Stage 1 recipe editor and SQLite revision history.");
        return sb.ToString();
    }

    private static string BuildCalibrationProfileSummary(ICollection<string> warnings)
    {
        var profiles = AoiDatabase.GetCalibrationProfiles();
        if (profiles.Count == 0)
            warnings.Add("No 2D calibration profile was found. summaries/calibration_profile_summary.txt was generated with no profile details.");

        var sb = new StringBuilder();
        sb.AppendLine("2D Calibration Profile Summary");
        sb.AppendLine($"GeneratedLocal: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"ProfileCount: {profiles.Count}");
        sb.AppendLine("PrototypeNotice: Calibration profiles are approximate 2D image-to-board mapping data for Stage 2 planning. They are not live camera calibration, robot calibration, or production coordinate validation.");
        sb.AppendLine();

        if (profiles.Count == 0)
        {
            sb.AppendLine("Status: No saved calibration profiles are available.");
            return sb.ToString();
        }

        foreach (var profile in profiles.Take(20))
        {
            sb.AppendLine($"ProfileId: {profile.Id}");
            sb.AppendLine($"ProfileName: {profile.ProfileName}");
            sb.AppendLine($"BoardModel: {profile.BoardModel}");
            sb.AppendLine($"ViewType: {profile.ViewType}");
            sb.AppendLine($"OperatorId: {profile.OperatorId}");
            sb.AppendLine($"CreatedUtc: {profile.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture)}");
            sb.AppendLine($"SampleImagePath: {NullIfEmpty(profile.SampleImagePath)}");
            sb.AppendLine($"PointCount: {profile.PointCount}");
            sb.AppendLine($"HasApproximateTransform: {profile.HasTransform}");
            sb.AppendLine($"Transform: {profile.TransformSummary}");

            foreach (var point in profile.Points.Take(25))
            {
                sb.AppendLine(string.Join(" | ",
                    $"PointId={point.Id}",
                    $"ImageX={point.ImageX:F3}",
                    $"ImageY={point.ImageY:F3}",
                    $"BoardXmm={point.BoardXMillimeters:F3}",
                    $"BoardYmm={point.BoardYMillimeters:F3}"));
            }

            if (profile.Points.Count > 25)
                sb.AppendLine($"PointListTruncated: true; shown=25; total={profile.Points.Count}");

            sb.AppendLine();
        }

        if (profiles.Count > 20)
            sb.AppendLine($"ProfileListTruncated: true; shown=20; total={profiles.Count}");

        return sb.ToString();
    }

    private static string BuildStage1PackageReadme(
        string packageDir,
        BatchTestRunRecord? run,
        int validationRows,
        int overlayCount,
        int inspectionRows,
        int reviewRows,
        int auditRows,
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
        sb.AppendLine("- `validation/customer_validation_report.html` - Customer-facing Stage 1 validation report with summary metrics, failed samples, sample annotated images, prototype limitations, and signature/approval section.");
        sb.AppendLine("- `validation/customer_validation_report_print_to_pdf.txt` - Instructions for creating a PDF from the HTML report with a browser print workflow.");
        sb.AppendLine("- `validation/customer_validation_report.md` - Markdown companion copy of the validation report.");
        sb.AppendLine("- `validation/sample_annotated_images/` - Sample annotated images referenced by the validation report.");
        sb.AppendLine("- `validation/validation_results.csv` - Row-level validation results from the latest persisted Stage 1 validation run.");
        sb.AppendLine("- `annotated_overlays/` - Generated PNG overlays for filtered inspection rows with accessible sample images.");
        sb.AppendLine("- `logs/inspection_history.csv` - Filtered SQLite inspection history.");
        sb.AppendLine("- `logs/review_disposition_log.csv` - Filtered review and disposition event log.");
        sb.AppendLine("- `logs/audit_trail.csv` - Filtered QC audit trail with UTC/local timestamps, user, role, station, action type, detail, and related IDs/paths.");
        sb.AppendLine("- `summaries/model_engine_configuration.txt` - Active model/engine configuration and prototype status.");
        sb.AppendLine("- `summaries/database_health_summary.txt` - SQLite health, table counts, and archive policy summary.");
        sb.AppendLine("- `summaries/recipe_revision_summary.txt` - Latest local recipe revision for the active board program, when available.");
        sb.AppendLine("- `summaries/calibration_profile_summary.txt` - Saved 2D calibration profiles and approximate image-to-board transform details for Stage 2 preparation.");
        sb.AppendLine("- `warnings.txt` - Missing optional items or non-blocking export issues.");
        sb.AppendLine();
        sb.AppendLine("## Package Summary");
        sb.AppendLine();
        sb.AppendLine($"- Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- Generated by: {WorkflowState.Instance.OperatorWithRole}");
        sb.AppendLine($"- Package path: `{packageDir}`");
        sb.AppendLine($"- Validation run: {(run is null ? "Not available" : run.Id.ToString(CultureInfo.InvariantCulture))}");
        sb.AppendLine($"- Validation rows: {validationRows}");
        sb.AppendLine($"- Annotated overlays: {overlayCount}");
        sb.AppendLine($"- Inspection history rows: {inspectionRows}");
        sb.AppendLine($"- Review/disposition rows: {reviewRows}");
        sb.AppendLine($"- Audit trail rows: {auditRows}");
        sb.AppendLine($"- Warnings: {warnings.Count}");
        sb.AppendLine();
        sb.AppendLine("## Applied Log Filters");
        sb.AppendLine();
        sb.AppendLine($"- From date: {filter.FromDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "none"}");
        sb.AppendLine($"- To date: {filter.ToDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "none"}");
        sb.AppendLine($"- Board/model: {filter.BoardProgram ?? "ALL"}");
        sb.AppendLine($"- Operator: {filter.OperatorId ?? "ALL"}");
        sb.AppendLine($"- Result: {filter.Result ?? "ALL"}");
        sb.AppendLine($"- User role: {filter.UserRole ?? "ALL"}");
        sb.AppendLine($"- Audit action type: {filter.ActionCategory ?? "ALL"}");
        sb.AppendLine();
        sb.AppendLine("## Prototype / Planned Scope");
        sb.AppendLine();
        sb.AppendLine("Implemented in Stage 1: local operator/review workflow, deterministic prototype inspection engine, SQLite persistence, image import/library support, approximate 2D calibration profile planning data, batch validation evidence, annotated overlay export, role-based local access controls, and customer package generation.");
        sb.AppendLine();
        sb.AppendLine("Planned for later stages: Stage 2 Planned Hardware Integration for live AOI camera, real 3D camera acquisition, and lighting control; Stage 3 Planned Robot Integration for PLC/robot/handler control; Stage 4 Planned MES/ERP Integration for authentication and traceability; production database integration; and trained ML inference unless separately configured and verified.");
        sb.AppendLine();
        sb.AppendLine("Missing optional inputs, such as absent validation runs or inaccessible image paths, are recorded in `warnings.txt` and do not prevent package creation.");
        return sb.ToString();
    }

    private static void WritePackageManifest(string packageDir, IReadOnlyList<string> warnings)
    {
        var manifestPath = Path.Combine(packageDir, "package_manifest.json");
        WritePackageManifestFile(packageDir, manifestPath, warnings);
        WritePackageManifestFile(packageDir, manifestPath, warnings);
    }

    private static void WritePackageManifestFile(string packageDir, string manifestPath, IReadOnlyList<string> warnings)
    {
        var manifest = new
        {
            schemaVersion = "stage1-customer-package/v1",
            packageId = Path.GetFileName(packageDir),
            generatedAtUtc = DateTime.UtcNow,
            generatedBy = WorkflowState.Instance.OperatorWithRole,
            includedFiles = Directory.EnumerateFiles(packageDir, "*", SearchOption.AllDirectories)
                .OrderBy(path => Path.GetRelativePath(packageDir, path), StringComparer.OrdinalIgnoreCase)
                .Select(path => new ValidationIncludedFile
                {
                    RelativePath = Path.GetRelativePath(packageDir, path).Replace('\\', '/'),
                    FileType = ClassifyPackageFile(packageDir, path),
                    Bytes = new FileInfo(path).Length,
                })
                .ToList(),
            warnings = warnings.ToArray(),
        };
        File.WriteAllText(
            manifestPath,
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            }),
            CsvEncoding);
    }

    private static string ClassifyPackageFile(string packageDir, string path)
    {
        var relative = Path.GetRelativePath(packageDir, path).Replace('\\', '/');
        var fileName = Path.GetFileName(path);
        if (fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            return "CSV";
        if (fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            return "Annotated image";
        if (fileName.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            return "HTML report";
        if (fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            return "Markdown";
        if (fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return "Manifest";
        if (relative.StartsWith("summaries/", StringComparison.OrdinalIgnoreCase))
            return "Summary";
        return "Package evidence";
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

    private static string ShortHash(string hash)
        => string.IsNullOrWhiteSpace(hash) || hash.Length <= 12 ? hash : hash[..12];

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

    private sealed class SoakTestDialog : Window
    {
        private readonly TextBox _imageFolderText = new();
        private readonly TextBox _durationMinutesText = new() { Text = "2" };
        private readonly TextBox _delayMillisecondsText = new() { Text = "250" };
        private readonly ComboBox _profileCombo = new();
        private readonly ComboBox _engineCombo = new();
        private readonly TextBox _outputFolderText = new();

        public SoakTestOptions? Options { get; private set; }

        public SoakTestDialog(string defaultOutputFolder, string defaultEngineKey)
        {
            Title = "Run Local Soak Test";
            Width = 640;
            Height = 430;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#11161A"));
            Foreground = Brushes.White;

            _outputFolderText.Text = defaultOutputFolder;
            ConfigureEngineOptions(defaultEngineKey);
            ConfigureProfileOptions();
            Content = BuildContent();
        }

        private Grid BuildContent()
        {
            var root = new Grid { Margin = new Thickness(16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });

            AddText(root, "Controlled local soak test using Folder Camera Simulation frames. This does not connect to real camera hardware.", 0, 0, 3, "#DCE5EB", bold: true);
            AddLabeledFolder(root, "Image folder", _imageFolderText, 1, "Select", OnSelectImageFolder);
            AddLabeledControl(root, "Test profile", _profileCombo, 2);
            AddLabeledText(root, "Duration (minutes)", _durationMinutesText, 3);
            AddLabeledText(root, "Delay between inspections (ms)", _delayMillisecondsText, 4);
            AddLabeledControl(root, "Selected engine", _engineCombo, 5);
            AddLabeledFolder(root, "Output folder", _outputFolderText, 6, "Select", OnSelectOutputFolder);
            AddText(root, "Factory PoC profile runs for 480 minutes. Folder Camera Simulation evidence is not real camera validation.", 7, 0, 3, "#9AA6AF");

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0),
            };
            var cancel = new Button { Content = "Cancel", Width = 92, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
            var run = new Button { Content = "Run", Width = 92, IsDefault = true };
            run.Click += OnRunClick;
            buttons.Children.Add(cancel);
            buttons.Children.Add(run);
            Grid.SetRow(buttons, 8);
            Grid.SetColumnSpan(buttons, 3);
            root.Children.Add(buttons);

            return root;
        }

        private void ConfigureEngineOptions(string defaultEngineKey)
        {
            _engineCombo.Items.Add(new ComboBoxItem { Content = "Pixel Difference Prototype Engine", Tag = InspectionEngineFactory.DefaultEngineKey });
            _engineCombo.Items.Add(new ComboBoxItem { Content = "ONNX ML Model (configured)", Tag = InspectionEngineFactory.OnnxEngineKey });
            var normalized = InspectionEngineFactory.NormalizeEngineKey(defaultEngineKey);
            _engineCombo.SelectedIndex = normalized == InspectionEngineFactory.OnnxEngineKey ? 1 : 0;
        }

        private void ConfigureProfileOptions()
        {
            _profileCombo.Items.Add(new ComboBoxItem { Content = "Quick smoke (5 min)", Tag = SoakTestProfile.QuickSmoke });
            _profileCombo.Items.Add(new ComboBoxItem { Content = "Short stability (30 min)", Tag = SoakTestProfile.ShortStability });
            _profileCombo.Items.Add(new ComboBoxItem { Content = "Factory PoC (8 hours)", Tag = SoakTestProfile.FactoryPoc });
            _profileCombo.Items.Add(new ComboBoxItem { Content = "Custom", Tag = SoakTestProfile.Custom });
            _profileCombo.SelectedIndex = 0;
            _durationMinutesText.Text = "5";
            _durationMinutesText.IsEnabled = false;
            _profileCombo.SelectionChanged += (_, _) =>
            {
                var profile = SelectedProfile();
                _durationMinutesText.IsEnabled = profile == SoakTestProfile.Custom;
                _durationMinutesText.Text = profile switch
                {
                    SoakTestProfile.QuickSmoke => "5",
                    SoakTestProfile.ShortStability => "30",
                    SoakTestProfile.FactoryPoc => "480",
                    _ => _durationMinutesText.Text,
                };
            };
        }

        private void OnSelectImageFolder(object sender, RoutedEventArgs e)
        {
            if (SelectFolder("Select soak-test image folder") is { } folder)
                _imageFolderText.Text = folder;
        }

        private void OnSelectOutputFolder(object sender, RoutedEventArgs e)
        {
            if (SelectFolder("Select soak-test report output folder") is { } folder)
                _outputFolderText.Text = folder;
        }

        private void OnRunClick(object sender, RoutedEventArgs e)
        {
            if (!Directory.Exists(_imageFolderText.Text))
            {
                MessageBox.Show("Select a valid image folder.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!double.TryParse(_durationMinutesText.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var durationMinutes) || durationMinutes <= 0)
            {
                MessageBox.Show("Enter a duration greater than 0 minutes.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!double.TryParse(_delayMillisecondsText.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var delayMs) || delayMs < 0)
            {
                MessageBox.Show("Enter a delay of 0 ms or greater.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(_outputFolderText.Text))
            {
                MessageBox.Show("Select an output folder.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var engineKey = ((_engineCombo.SelectedItem as ComboBoxItem)?.Tag as string)
                ?? InspectionEngineFactory.DefaultEngineKey;
            Options = new SoakTestOptions(
                _imageFolderText.Text.Trim(),
                TimeSpan.FromMinutes(durationMinutes),
                TimeSpan.FromMilliseconds(delayMs),
                engineKey,
                _outputFolderText.Text.Trim(),
                "UNKNOWN",
                "TBOX-MAIN",
                "SOAK-TEST");
            DialogResult = true;
        }

        private SoakTestProfile SelectedProfile()
            => (_profileCombo.SelectedItem as ComboBoxItem)?.Tag is SoakTestProfile profile
                ? profile
                : SoakTestProfile.Custom;

        private static string? SelectFolder(string title)
        {
            var dialog = new OpenFolderDialog
            {
                Title = title,
                Multiselect = false,
            };

            return dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName)
                ? dialog.FolderName
                : null;
        }

        private static void AddLabeledFolder(Grid root, string label, TextBox box, int row, string buttonText, RoutedEventHandler handler)
        {
            AddLabeledText(root, label, box, row);
            var button = new Button { Content = buttonText, Margin = new Thickness(6, 4, 0, 4), MinHeight = 28 };
            button.Click += handler;
            Grid.SetRow(button, row);
            Grid.SetColumn(button, 2);
            root.Children.Add(button);
        }

        private static void AddLabeledText(Grid root, string label, TextBox box, int row)
        {
            box.Margin = new Thickness(0, 4, 0, 4);
            box.MinHeight = 28;
            AddLabeledControl(root, label, box, row);
        }

        private static void AddLabeledControl(Grid root, string label, Control control, int row)
        {
            var labelBlock = new TextBlock
            {
                Text = label,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9AA6AF")),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 4, 8, 4),
            };
            control.Margin = new Thickness(0, 4, 0, 4);
            control.MinHeight = 28;
            Grid.SetRow(labelBlock, row);
            Grid.SetColumn(labelBlock, 0);
            Grid.SetRow(control, row);
            Grid.SetColumn(control, 1);
            root.Children.Add(labelBlock);
            root.Children.Add(control);
        }

        private static void AddText(Grid root, string text, int row, int column, int columnSpan, string color, bool bold = false)
        {
            var block = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)),
                FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
                Margin = new Thickness(0, 0, 0, 12),
            };
            Grid.SetRow(block, row);
            Grid.SetColumn(block, column);
            Grid.SetColumnSpan(block, columnSpan);
            root.Children.Add(block);
        }
    }

    private sealed record WorkProgress(int Completed, int Total, string Message);

    private sealed record ExportOutcome(int Count, IReadOnlyList<string> Errors);

    private sealed record PackageOutcome(string PackageDir, string ReportPath, int OverlayCount, IReadOnlyList<string> Warnings);

    private sealed record IntegrityOutcome(string ReportPath, string Integrity, string Status);

    private sealed record IndexOutcome(string Path, int Count, IReadOnlyList<string> Errors);

    public sealed class InspectionLogRow
    {
        public long Id { get; init; }
        public DateTime CreatedAtUtc { get; init; }
        public string TimestampLocal => CreatedAtUtc == DateTime.MinValue ? "--" : CreatedAtUtc.ToLocalTime().ToString("MM-dd HH:mm");
        public string BoardProgram { get; init; } = "UNKNOWN";
        public string OperatorId { get; init; } = "UNKNOWN";
        public string InspectionEngine { get; init; } = "Pixel Difference Prototype Engine";
        public string ModelVersion { get; init; } = "UNKNOWN";
        public string ModelFilePath { get; init; } = string.Empty;
        public double ConfidenceThreshold { get; init; }
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
        public double ImageLoadMilliseconds { get; init; }
        public double PreprocessingMilliseconds { get; init; }
        public double InferenceMilliseconds { get; init; }
        public double OverlayRenderingMilliseconds { get; init; }
        public double TotalInspectionMilliseconds { get; init; }
        public string TotalTimeDisplay => $"{TotalInspectionMilliseconds:F0} ms";

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
                ModelFilePath = record.ModelFilePath,
                ConfidenceThreshold = record.ConfidenceThreshold,
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
                ImageLoadMilliseconds = record.ImageLoadMilliseconds,
                PreprocessingMilliseconds = record.PreprocessingMilliseconds,
                InferenceMilliseconds = record.InferenceMilliseconds,
                OverlayRenderingMilliseconds = record.OverlayRenderingMilliseconds,
                TotalInspectionMilliseconds = record.TotalInspectionMilliseconds,
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
        public string OperatorId { get; init; } = "UNKNOWN";
        public long? AuditEventId { get; init; }
        public string AuditEventDisplay => AuditEventId is null ? "--" : AuditEventId.Value.ToString(CultureInfo.InvariantCulture);
        public string VerificationStatus { get; init; } = "--";
        public string VerificationSha256 { get; init; } = string.Empty;
        public string VerificationShaDisplay => string.IsNullOrWhiteSpace(VerificationSha256) ? "--" : ShortHash(VerificationSha256);

        public static ExportHistoryRow FromRecord(ExportHistoryRecord record, ExportVerificationRecord? verification = null)
        {
            return new ExportHistoryRow
            {
                Id = record.Id,
                CreatedAtUtc = record.CreatedAtUtc,
                ExportType = record.ExportType,
                FilePath = record.FilePath,
                Status = record.Status,
                OperatorId = record.OperatorId,
                AuditEventId = record.AuditEventId,
                VerificationStatus = verification?.Status ?? "--",
                VerificationSha256 = verification?.Sha256 ?? string.Empty,
            };
        }
    }

    public sealed class MesSpoolQueueRow
    {
        public long Id { get; init; }
        public DateTime CreatedAtUtc { get; init; }
        public string CreatedLocal => CreatedAtUtc == DateTime.MinValue ? "--" : CreatedAtUtc.ToLocalTime().ToString("MM-dd HH:mm");
        public string PayloadType { get; init; } = string.Empty;
        public string EndpointUrl { get; init; } = string.Empty;
        public int RetryCount { get; init; }
        public int MaxRetryCount { get; init; }
        public string RetryDisplay => $"{RetryCount}/{MaxRetryCount}";
        public string Status { get; init; } = string.Empty;
        public string LastError { get; init; } = string.Empty;
        public string LotId { get; init; } = string.Empty;
        public string BoardModel { get; init; } = string.Empty;
        public string Result { get; init; } = string.Empty;

        public static MesSpoolQueueRow FromRecord(MesSpoolQueueRecord record)
        {
            return new MesSpoolQueueRow
            {
                Id = record.Id,
                CreatedAtUtc = record.CreatedAtUtc,
                PayloadType = record.PayloadType,
                EndpointUrl = record.EndpointUrl,
                RetryCount = record.RetryCount,
                MaxRetryCount = record.MaxRetryCount,
                Status = record.Status,
                LastError = record.LastError,
                LotId = record.LotId,
                BoardModel = record.BoardModel,
                Result = record.Result,
            };
        }
    }

    public sealed class FactoryReadinessRow
    {
        public string Name { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string Evidence { get; init; } = string.Empty;
        public string NextAction { get; init; } = string.Empty;

        public static FactoryReadinessRow FromCategory(FactoryReadinessCategory category)
            => new()
            {
                Name = category.Name,
                Status = category.Status,
                Evidence = category.Evidence,
                NextAction = category.NextAction,
            };
    }

    public sealed class AuditLogRow
    {
        public long Id { get; init; }
        public DateTime TimestampUtc { get; init; }
        public DateTime LocalTimestamp { get; init; }
        public string TimestampUtcDisplay => TimestampUtc == DateTime.MinValue ? "--" : TimestampUtc.ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        public string LocalTimestampDisplay => LocalTimestamp == DateTime.MinValue ? "--" : LocalTimestamp.ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        public string UserId { get; init; } = "UNKNOWN";
        public string UserRole { get; init; } = "UNKNOWN";
        public string StationId { get; init; } = "UNKNOWN";
        public string ActionCategory { get; init; } = string.Empty;
        public string ActionDetail { get; init; } = string.Empty;
        public string RelatedEntityType { get; init; } = string.Empty;
        public string RelatedEntityId { get; init; } = string.Empty;
        public string RelatedPath { get; init; } = string.Empty;
        public string RelatedEntityDisplay => string.IsNullOrWhiteSpace(RelatedEntityType) && string.IsNullOrWhiteSpace(RelatedEntityId)
            ? "--"
            : $"{RelatedEntityType}:{RelatedEntityId}";

        public static AuditLogRow FromRecord(AuditEventRecord record)
        {
            return new AuditLogRow
            {
                Id = record.Id,
                TimestampUtc = record.TimestampUtc,
                LocalTimestamp = record.LocalTimestamp,
                UserId = record.UserId,
                UserRole = record.UserRole,
                StationId = record.StationId,
                ActionCategory = record.ActionCategory,
                ActionDetail = record.ActionDetail,
                RelatedEntityType = record.RelatedEntityType,
                RelatedEntityId = record.RelatedEntityId,
                RelatedPath = record.RelatedPath,
            };
        }
    }
}
