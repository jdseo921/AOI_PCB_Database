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
                if (exported % 10 == 0)
                {
                    MemoryDiagnosticsService.RecordExportCheckpoint("Annotated overlay export", exported);
                    ImageCacheService.ClearOnMemoryPressure();
                }
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
        => ImageCacheService.LoadBitmap(path, cache: false);

    private static void SavePng(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private void RefreshAfterExport(string message)
    {
        _ = RefreshAsync(CancellationToken.None);
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

    private static string BuildModelConfigurationSummary(InspectionModelConfiguration configuration, ICollection<string> warnings)
    {
        var status = InspectionModelConfigurationService.GetStatusText();
        if (configuration.IsLearnedVisualModelSelected)
            warnings.Add("Inspection engine is the Learned PCB Visual Model. Evidence is image-only Stage 1 learning, not live camera validation or production acceptance.");
        else if (!configuration.IsOnnxSelected)
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
        sb.AppendLine("PrototypeNotice: Stage 1 is a local PoC. The default Pixel Difference Prototype Engine is deterministic evidence generation. Learned PCB Visual Model evidence is image-only learning. ONNX ML Model inference is claimed only when a configured local model loads and inference succeeds.");
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
        sb.AppendLine($"AutoArchivePolicy: {DescribeRetentionPolicy()}");
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
        sb.AppendLine("- `validation/customer_validation_report.pdf` - Native PDF rendering of the customer validation report.");
        sb.AppendLine("- `validation/customer_validation_report_print_to_pdf.txt` - Fallback instructions for creating a PDF from the HTML report with a browser print workflow.");
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
        if (fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            return "PDF report";
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

    private static string FindRepositoryRootForDocs()
    {
        foreach (var seed in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(seed);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "AOI_PCB_Database.slnx")) ||
                    Directory.Exists(Path.Combine(directory.FullName, "Docs")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }

        return Environment.CurrentDirectory;
    }

    private void OpenPathOrWarn(string path, string title)
    {
        if (string.IsNullOrWhiteSpace(path) || (!File.Exists(path) && !Directory.Exists(path)))
        {
            MessageBox.Show(
                $"{title} is not available yet.\n\nExpected path: {path}",
                "AOI Monitor",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            MessageBox.Show(
                $"{title} could not be opened:\n{ex.Message}",
                "AOI Monitor",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private static string? NullIfBlank(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NullIfEmpty(string value)
        => string.IsNullOrWhiteSpace(value) ? "(not configured)" : value;

    private static string ShortHash(string hash)
        => string.IsNullOrWhiteSpace(hash) || hash.Length <= 12 ? hash : hash[..12];

    private static string BuildEvidenceSummaryTextFor(BuildTestEvidenceSummary summary)
    {
        if (summary.Latest is null)
            return "Build/test evidence: not imported";

        var latest = summary.Latest;
        var commit = string.IsNullOrWhiteSpace(latest.GitCommit) ? "unknown" : ShortHash(latest.GitCommit);
        return $"Build/test evidence: {summary.Status} | {latest.Configuration} | commit {commit} | hygiene {latest.HygieneStatus}, restore {latest.RestoreStatus}, build {latest.BuildStatus}, test {latest.TestStatus}, publish {latest.PublishValidationStatus}";
    }

    private static string StandardsTraceabilitySummaryFor(StandardsTraceabilityReport report)
        => $"Standards traceability: {report.OverallStatus}; satisfied={report.SatisfiedCount}; partial={report.PartialCount}; missing={report.MissingCount}; not applicable={report.NotApplicableCount}; missing release blockers={report.ReleaseBlockerMissingCount}. Standards-aligned evidence only; not certification.";

    private static string EscapeCsv(string value)
        => $"\"{value.Replace("\"", "\"\"")}\"";

    private CancellationTokenSource BeginWork(string message)
    {
        UiPerformanceMonitorService.RecordSlowOperation(message, 0, $"Long operation started: {message}");
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
        var report = CrashReportService.WriteReport(new CrashReportRequest
        {
            Exception = ex,
            OperationName = title,
            CurrentPage = "Log & Export",
            IsFatal = false,
            IsUiThread = true,
        });
        StatusText.Text = message;
        WorkflowState.Instance.AddEvent(category, message, relatedEntityType: "CrashReport", relatedPath: report.ReportPath);
        MessageBox.Show($"{message}\n\n{report.OperatorMessage}", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
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

}
