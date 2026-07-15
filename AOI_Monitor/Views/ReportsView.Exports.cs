using System.Collections.ObjectModel;
using System.Diagnostics;
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

public partial class ReportsView
{
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

        var gateReport = ClientDemoReadinessGateService.Evaluate();
        if (!EnsureClientDemoGateAllowsExport(gateReport, allowMissingStage1PackageForStage1Export: true))
            return;
        var gateWarnings = gateReport.Checks
            .Where(check => check.Status != ClientDemoGateStatus.Pass)
            .Select(check => $"Client demo readiness gate {check.Status}: {check.Name}: {check.Evidence}")
            .ToArray();

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
                warnings.AddRange(gateWarnings);
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
                var reportPdfPath = Path.Combine(validationDir, "customer_validation_report.pdf");
                var reportContext = BuildCustomerValidationReportContext(latestRun, validationRows, sampleImages.Images, warnings);
                File.WriteAllText(reportPath, CustomerValidationReportService.BuildHtml(reportContext), CsvEncoding);
                PdfExportService.ExportHtmlFileToPdf(reportPath, reportPdfPath, "Customer Validation Report");
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
            var gateReport = ClientDemoReadinessGateService.Evaluate();
            if (!EnsureClientDemoGateAllowsExport(gateReport, allowMissingStage1PackageForStage1Export: false))
                return;
            var result = FactoryReadinessService.ExportGoNoGoPackage();
            WorkflowState.Instance.AddEvent("FACTORY_READINESS_EXPORT", $"Factory readiness package exported: {Path.GetFileName(result.PackageFolder)}.");
            RefreshAfterExport($"Factory readiness package exported: {result.PackageFolder}. Summary: {result.SummaryHtmlPath}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            HandleWorkError("Factory readiness package export failed", ex, "FACTORY_READINESS_EXPORT_ERROR");
        }
    }

    private bool EnsureClientDemoGateAllowsExport(ClientDemoReadinessGateReport report, bool allowMissingStage1PackageForStage1Export)
    {
        UpdateClientDemoGateText(report);
        var blocking = report.Checks
            .Where(check => check.Status == ClientDemoGateStatus.Blocked)
            .Where(check => !(allowMissingStage1PackageForStage1Export && check.Name == "Stage 1 package"))
            .ToArray();
        if (blocking.Length > 0)
        {
            MessageBox.Show(
                $"Client demo readiness is BLOCKED.\n\n{string.Join("\n", blocking.Select(check => $"- {check.Name}: {check.Evidence}"))}",
                "Client Demo Readiness Gate",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            WorkflowState.Instance.AddEvent("CLIENT_DEMO_GATE_BLOCKED", $"Client-facing export blocked: {string.Join("; ", blocking.Select(check => check.Name))}.");
            return false;
        }

        if (report.OverallStatus == ClientDemoGateStatus.Pass)
            return true;

        var warningText = string.Join("\n", report.Checks
            .Where(check => check.Status != ClientDemoGateStatus.Pass)
            .Select(check => $"- {check.Name}: {check.Status}; {check.Evidence}"));
        var proceed = MessageBox.Show(
            $"Client demo readiness has warnings.\n\n{warningText}\n\nProceed and include these warnings in the client-facing package?",
            "Client Demo Readiness Gate",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (proceed != MessageBoxResult.Yes)
            return false;

        WorkflowState.Instance.AddEvent("CLIENT_DEMO_GATE_WARNING", $"Client-facing export proceeded with gate status {report.OverallStatus}.");
        return true;
    }

    private void OnExportFactoryAcceptanceChecklistClick(object sender, RoutedEventArgs e)
    {
        if (!WorkflowState.Instance.TryAuthorize(RoleAuthorization.CanExportLogs, "Exporting factory acceptance checklist", out var permissionMessage))
        {
            MessageBox.Show(permissionMessage, "Permission denied", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!ConfirmExport("Export a client-facing Factory Acceptance Checklist package?"))
            return;

        try
        {
            var profile = SelectedFactoryAcceptanceProfile();
            var result = FactoryAcceptanceChecklistService.Export(profile, EnsureExportsDir());
            var checklist = FactoryAcceptanceChecklistService.Generate(profile);
            ReplaceRows(_factoryAcceptanceRows, checklist.Items);
            WorkflowState.Instance.AddEvent("FACTORY_ACCEPTANCE_EXPORT", $"Factory acceptance checklist exported: {Path.GetFileName(result.Folder)}.");
            RefreshAfterExport($"Factory acceptance checklist exported. HTML: {result.HtmlPath}. JSON: {result.JsonPath}. CSV: {result.CsvPath}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            HandleWorkError("Factory acceptance checklist export failed", ex, "FACTORY_ACCEPTANCE_EXPORT_ERROR");
        }
    }

    private void OnImportBuildTestEvidenceClick(object sender, RoutedEventArgs e)
    {
        if (!WorkflowState.Instance.TryAuthorize(RoleAuthorization.CanExportLogs, "Importing build/test evidence", out var permissionMessage))
        {
            MessageBox.Show(permissionMessage, "Permission denied", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Select build/test evidence JSON",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var evidence = BuildTestEvidenceService.ImportEvidence(dialog.FileName, WorkflowState.Instance.OperatorWithRole);
            WorkflowState.Instance.AddEvent("BUILD_TEST_EVIDENCE", $"Build/test evidence imported: {Path.GetFileName(evidence.EvidencePath)}.");
            _ = RefreshAsync(CancellationToken.None);
            StatusText.Text = $"Build/test evidence imported: {evidence.EvidencePath}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
        {
            HandleWorkError("Build/test evidence import failed", ex, "BUILD_TEST_EVIDENCE_ERROR");
        }
    }

    private void OnOpenBuildEvidenceFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(BuildTestEvidenceService.EvidenceFolder);
            Process.Start(new ProcessStartInfo
            {
                FileName = BuildTestEvidenceService.EvidenceFolder,
                UseShellExecute = true,
            });
            StatusText.Text = $"Opened build/test evidence folder: {BuildTestEvidenceService.EvidenceFolder}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            HandleWorkError("Open build/test evidence folder failed", ex, "BUILD_TEST_EVIDENCE_ERROR");
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

}
