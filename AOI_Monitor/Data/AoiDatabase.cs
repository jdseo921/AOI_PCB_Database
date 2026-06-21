using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media.Imaging;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Microsoft.Data.Sqlite;

namespace AOI_Monitor.Data;

public static class AoiDatabase
{
    private const string AppFolderName = "AOI_Monitor";
    private static readonly HashSet<string> SupportedImportExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg",
    };

    private static bool _initialized;
    private static string _storageRoot = ResolveStorageRoot();

    public static Func<string>? AuditOperatorProvider { get; set; }
    public static Func<string>? AuditUserIdProvider { get; set; }
    public static Func<string>? AuditUserRoleProvider { get; set; }
    public static Func<string>? AuditStationProvider { get; set; }

    public static string StorageRoot => _storageRoot;
    public static string DefaultStorageRoot => ResolveStorageRoot();
    public static string DatabasePath => Path.Combine(StorageRoot, "aoi_monitor.sqlite");
    public static string ImageVaultPath => Path.Combine(StorageRoot, "image_vault");
    public static string TrainingVaultPath => Path.Combine(ImageVaultPath, "training");
    public static int LatestSchemaVersion => AoiDatabaseMigrations.LatestVersion;

    public static void ConfigureStorageRoot(string storageRoot)
    {
        if (string.IsNullOrWhiteSpace(storageRoot))
            throw new ArgumentException("Storage root is required.", nameof(storageRoot));

        _storageRoot = Path.GetFullPath(storageRoot);
        _initialized = false;
    }

    public static void Initialize()
    {
        Directory.CreateDirectory(StorageRoot);
        Directory.CreateDirectory(ImageVaultPath);
        Directory.CreateDirectory(TrainingVaultPath);

        using var connection = OpenConnection();
        var hasExistingSchema = HasExistingUserSchema(connection);
        if (hasExistingSchema)
        {
            EnsureSchemaCompatibility(connection);
            ExecuteSchemaSql(connection);
        }
        else
        {
            ExecuteSchemaSql(connection);
            EnsureSchemaCompatibility(connection);
        }

        AutoArchiveOldLogs(connection);

        _initialized = true;
    }

    public static ImportedImage ImportImage(
        string sourcePath,
        string boardModel,
        string lotId,
        string viewType)
    {
        EnsureInitialized();

        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Image file was not found.", sourcePath);

        var importedAt = DateTime.UtcNow;
        var hash = ComputeSha256(sourcePath);
        var originalName = Path.GetFileName(sourcePath);
        var safeName = MakeVaultFileName(importedAt, viewType, originalName);
        var vaultPath = Path.Combine(ImageVaultPath, safeName);
        File.Copy(sourcePath, vaultPath, overwrite: true);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO Images
                (OriginalPath, VaultPath, FileName, BoardModel, LotId, ViewType, ImportedAtUtc, FileHash)
            VALUES
                ($originalPath, $vaultPath, $fileName, $boardModel, $lotId, $viewType, $importedAtUtc, $fileHash);
            SELECT last_insert_rowid();
            """;

        command.Parameters.AddWithValue("$originalPath", sourcePath);
        command.Parameters.AddWithValue("$vaultPath", vaultPath);
        command.Parameters.AddWithValue("$fileName", originalName);
        command.Parameters.AddWithValue("$boardModel", boardModel);
        command.Parameters.AddWithValue("$lotId", lotId);
        command.Parameters.AddWithValue("$viewType", viewType);
        command.Parameters.AddWithValue("$importedAtUtc", importedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$fileHash", hash);

        var id = (long)(command.ExecuteScalar() ?? 0L);
        var imported = new ImportedImage(id, sourcePath, vaultPath, originalName, boardModel, lotId, viewType, importedAt, hash);
        RecordAuditEvent(
            "IMAGE_IMPORT",
            $"Image imported to vault: {originalName}; board={boardModel}; lot={lotId}; view={viewType}.",
            relatedEntityType: "Image",
            relatedEntityId: id.ToString(CultureInfo.InvariantCulture),
            relatedPath: vaultPath);
        return imported;
    }

    public static ImageImportResult TryImportImage(
        string sourcePath,
        string boardModel,
        string lotId,
        string viewType)
    {
        EnsureInitialized();

        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            return new ImageImportResult(null, false, "Missing", "File does not exist.");

        var extension = Path.GetExtension(sourcePath);
        if (!SupportedImportExtensions.Contains(extension))
            return new ImageImportResult(null, false, "Unsupported", "Only PNG, JPG, and JPEG images are supported.");

        try
        {
            using var stream = File.Open(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length == 0)
                return new ImageImportResult(null, false, "Unreadable", "File is empty.");
        }
        catch (Exception ex)
        {
            return new ImageImportResult(null, false, "Unreadable", ex.Message);
        }

        try
        {
            using var stream = File.Open(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);

            if (decoder.Frames.Count == 0)
                return new ImageImportResult(null, false, "Invalid", "Image decoder found no frames.");
        }
        catch (Exception ex)
        {
            return new ImageImportResult(null, false, "Invalid", ex.Message);
        }

        try
        {
            var hash = ComputeSha256(sourcePath);
            if (TryGetImageByHash(hash) is { } existing)
            {
                RecordAuditEvent(
                    "IMAGE_IMPORT_DUPLICATE",
                    $"Duplicate image skipped: {Path.GetFileName(sourcePath)}.",
                    relatedEntityType: "Image",
                    relatedEntityId: existing.Id.ToString(CultureInfo.InvariantCulture),
                    relatedPath: existing.VaultPath);
                return new ImageImportResult(existing, false, "Duplicate", "Image already exists in the vault.");
            }

            var imported = ImportImage(sourcePath, boardModel, lotId, viewType);
            return new ImageImportResult(imported, true, "Imported", "Image copied into the vault.");
        }
        catch (Exception ex)
        {
            return new ImageImportResult(null, false, "Invalid", ex.Message);
        }
    }

    public static IReadOnlyList<ImportedImage> GetImportedImages()
    {
        EnsureInitialized();

        var images = new List<ImportedImage>();
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT Id, OriginalPath, VaultPath, FileName, BoardModel, LotId, ViewType, ImportedAtUtc, FileHash
            FROM Images
            ORDER BY datetime(ImportedAtUtc) DESC, Id DESC;
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            images.Add(ReadImportedImage(reader));
        }

        return images;
    }

    public static long RecordInspectionResult(AnalysisResult result)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO InspectionResults
                (SampleImagePath, GoldenImagePath, BoardProgram, OperatorId, InspectionEngine, DifferenceScore, MeanBrightness, Verdict, Confidence,
                 SuggestedDefect, PolicyName, ModelVersion, ModelFilePath, ConfidenceThreshold, ThresholdProfileId, ThresholdProfileRevision,
                 ThresholdSource, DecisionReason, HotspotX, HotspotY, HotspotWidth,
                 HotspotHeight, ImageLoadMs, PreprocessingMs, InferenceMs, OverlayRenderingMs, TotalInspectionMs, CreatedAtUtc)
            VALUES
                ($sampleImagePath, $goldenImagePath, $boardProgram, $operatorId, $inspectionEngine, $differenceScore, $meanBrightness, $verdict, $confidence,
                 $suggestedDefect, $policyName, $modelVersion, $modelFilePath, $confidenceThreshold, $thresholdProfileId, $thresholdProfileRevision,
                 $thresholdSource, $decisionReason, $hotspotX, $hotspotY, $hotspotWidth,
                 $hotspotHeight, $imageLoadMs, $preprocessingMs, $inferenceMs, $overlayRenderingMs, $totalInspectionMs, $createdAtUtc);
            SELECT last_insert_rowid();
            """;

        command.Parameters.AddWithValue("$sampleImagePath", result.SamplePath);
        command.Parameters.AddWithValue("$goldenImagePath", (object?)result.GoldenPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$boardProgram", result.BoardProgram);
        command.Parameters.AddWithValue("$operatorId", result.OperatorId);
        command.Parameters.AddWithValue("$inspectionEngine", result.InspectionEngine);
        command.Parameters.AddWithValue("$differenceScore", result.DifferenceScore);
        command.Parameters.AddWithValue("$meanBrightness", result.MeanBrightness);
        command.Parameters.AddWithValue("$verdict", result.Verdict);
        command.Parameters.AddWithValue("$confidence", result.Confidence);
        command.Parameters.AddWithValue("$suggestedDefect", result.SuggestedDefect);
        command.Parameters.AddWithValue("$policyName", result.PolicyName);
        command.Parameters.AddWithValue("$modelVersion", result.ModelVersion);
        command.Parameters.AddWithValue("$modelFilePath", result.ModelFilePath);
        command.Parameters.AddWithValue("$confidenceThreshold", result.ConfidenceThreshold);
        command.Parameters.AddWithValue("$thresholdProfileId", result.ThresholdProfileId);
        command.Parameters.AddWithValue("$thresholdProfileRevision", result.ThresholdProfileRevision);
        command.Parameters.AddWithValue("$thresholdSource", result.ThresholdSource);
        command.Parameters.AddWithValue("$decisionReason", result.DecisionReason);
        command.Parameters.AddWithValue("$hotspotX", result.Hotspot.X);
        command.Parameters.AddWithValue("$hotspotY", result.Hotspot.Y);
        command.Parameters.AddWithValue("$hotspotWidth", result.Hotspot.Width);
        command.Parameters.AddWithValue("$hotspotHeight", result.Hotspot.Height);
        command.Parameters.AddWithValue("$imageLoadMs", result.Timing.ImageLoadMilliseconds);
        command.Parameters.AddWithValue("$preprocessingMs", result.Timing.PreprocessingMilliseconds);
        command.Parameters.AddWithValue("$inferenceMs", result.Timing.InferenceMilliseconds);
        command.Parameters.AddWithValue("$overlayRenderingMs", result.Timing.OverlayRenderingMilliseconds);
        command.Parameters.AddWithValue("$totalInspectionMs", result.Timing.TotalInspectionMilliseconds);
        command.Parameters.AddWithValue("$createdAtUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        var inspectionResultId = (long)(command.ExecuteScalar() ?? 0L);

        foreach (var defect in result.Defects)
        {
            using var defectCommand = connection.CreateCommand();
            defectCommand.Transaction = transaction;
            defectCommand.CommandText =
                """
                INSERT INTO Defects
                    (InspectionResultId, ImageId, RefDes, DefectType, Severity, Confidence,
                     RoiX, RoiY, RoiWidth, RoiHeight, XPosition, YPosition, SideOrViewType,
                     RoiId, JudgmentStatus, CreatedAtUtc)
                VALUES
                    ($inspectionResultId, NULL, NULL, $defectType, $severity, $confidence,
                     $roiX, $roiY, $roiWidth, $roiHeight, $xPosition, $yPosition, $sideOrViewType,
                     $roiId, $judgmentStatus, $createdAtUtc);
                """;

            defectCommand.Parameters.AddWithValue("$inspectionResultId", inspectionResultId);
            defectCommand.Parameters.AddWithValue("$defectType", defect.DefectType);
            defectCommand.Parameters.AddWithValue("$severity", ToDefectSeverity(defect.JudgmentStatus));
            defectCommand.Parameters.AddWithValue("$confidence", defect.Confidence);
            defectCommand.Parameters.AddWithValue("$roiX", defect.BoundingBox.X);
            defectCommand.Parameters.AddWithValue("$roiY", defect.BoundingBox.Y);
            defectCommand.Parameters.AddWithValue("$roiWidth", defect.BoundingBox.Width);
            defectCommand.Parameters.AddWithValue("$roiHeight", defect.BoundingBox.Height);
            defectCommand.Parameters.AddWithValue("$xPosition", defect.XPosition);
            defectCommand.Parameters.AddWithValue("$yPosition", defect.YPosition);
            defectCommand.Parameters.AddWithValue("$sideOrViewType", defect.SideOrViewType);
            defectCommand.Parameters.AddWithValue("$roiId", defect.RoiId);
            defectCommand.Parameters.AddWithValue("$judgmentStatus", defect.JudgmentStatus);
            defectCommand.Parameters.AddWithValue("$createdAtUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            defectCommand.ExecuteNonQuery();
        }

        transaction.Commit();
        RecordAuditEvent(
            "INSPECTION_RESULT",
            $"Inspection result persisted: {result.Verdict}, engine {result.InspectionEngine}, score {result.DifferenceScore:F1}%.",
            operatorWithRole: result.OperatorId,
            relatedEntityType: "InspectionResult",
            relatedEntityId: inspectionResultId.ToString(CultureInfo.InvariantCulture),
            relatedPath: result.SamplePath);
        return inspectionResultId;
    }

    public static long RecordInspectionLatencyTrace(InspectionLatencyTrace trace)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO InspectionLatencyTraces
                (TraceId, CreatedAtUtc, FrameCapturedAtUtc, FrameReceivedAtUtc,
                 PreprocessingStartUtc, PreprocessingEndUtc, InferenceStartUtc, InferenceEndUtc,
                 PostprocessStartUtc, PostprocessEndUtc, OverlayRenderStartUtc, OverlayRenderEndUtc,
                 ResultPersistStartUtc, ResultPersistEndUtc, TotalFrameToOverlayMs, TotalFrameToSavedResultMs,
                 SourceKind, Engine, ModelId, ImageWidth, ImageHeight, Verdict, WarningsJson)
            VALUES
                ($traceId, $createdAtUtc, $frameCapturedAtUtc, $frameReceivedAtUtc,
                 $preprocessingStartUtc, $preprocessingEndUtc, $inferenceStartUtc, $inferenceEndUtc,
                 $postprocessStartUtc, $postprocessEndUtc, $overlayRenderStartUtc, $overlayRenderEndUtc,
                 $resultPersistStartUtc, $resultPersistEndUtc, $totalFrameToOverlayMs, $totalFrameToSavedResultMs,
                 $sourceKind, $engine, $modelId, $imageWidth, $imageHeight, $verdict, $warningsJson);
            SELECT last_insert_rowid();
            """;
        BindInspectionLatencyTrace(command, trace);
        var id = (long)(command.ExecuteScalar() ?? 0L);
        trace.Id = id;
        return id;
    }

    public static IReadOnlyList<InspectionLatencyTrace> GetInspectionLatencyTraces(int limit = 500)
    {
        EnsureInitialized();

        var traces = new List<InspectionLatencyTrace>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, TraceId, CreatedAtUtc, FrameCapturedAtUtc, FrameReceivedAtUtc,
                   PreprocessingStartUtc, PreprocessingEndUtc, InferenceStartUtc, InferenceEndUtc,
                   PostprocessStartUtc, PostprocessEndUtc, OverlayRenderStartUtc, OverlayRenderEndUtc,
                   ResultPersistStartUtc, ResultPersistEndUtc, TotalFrameToOverlayMs, TotalFrameToSavedResultMs,
                   SourceKind, Engine, ModelId, ImageWidth, ImageHeight, Verdict, WarningsJson
            FROM InspectionLatencyTraces
            ORDER BY datetime(CreatedAtUtc) DESC, Id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 10000));
        using var reader = command.ExecuteReader();
        while (reader.Read())
            traces.Add(ReadInspectionLatencyTrace(reader));

        return traces;
    }

    public static IReadOnlyList<InspectionLatencyTrace> GetInspectionLatencyTraces(DateTime fromUtc, DateTime toUtc, int limit = 10000)
    {
        EnsureInitialized();

        var from = fromUtc.ToUniversalTime();
        var to = toUtc.ToUniversalTime();
        if (to < from)
            (from, to) = (to, from);

        var traces = new List<InspectionLatencyTrace>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, TraceId, CreatedAtUtc, FrameCapturedAtUtc, FrameReceivedAtUtc,
                   PreprocessingStartUtc, PreprocessingEndUtc, InferenceStartUtc, InferenceEndUtc,
                   PostprocessStartUtc, PostprocessEndUtc, OverlayRenderStartUtc, OverlayRenderEndUtc,
                   ResultPersistStartUtc, ResultPersistEndUtc, TotalFrameToOverlayMs, TotalFrameToSavedResultMs,
                   SourceKind, Engine, ModelId, ImageWidth, ImageHeight, Verdict, WarningsJson
            FROM InspectionLatencyTraces
            WHERE CreatedAtUtc >= $fromUtc
              AND CreatedAtUtc <= $toUtc
            ORDER BY datetime(CreatedAtUtc) DESC, Id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$fromUtc", from.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$toUtc", to.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100000));
        using var reader = command.ExecuteReader();
        while (reader.Read())
            traces.Add(ReadInspectionLatencyTrace(reader));

        return traces;
    }

    public static long RecordBatchTestRun(
        string imageFolder,
        string? groundTruthCsvPath,
        string engineName,
        string modelVersion,
        double accuracy,
        double precision,
        double recall,
        double falseCallRate,
        IReadOnlyList<BatchTestResultRecord> results,
        string thresholdProfileId = "",
        string thresholdProfileRevision = "")
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO BatchTestRuns
                (ImageFolder, GroundTruthCsvPath, EngineName, ModelVersion, ThresholdProfileId, ThresholdProfileRevision, Accuracy, Precision, Recall,
                 FalseCallRate, TotalImages, FailedCount, CreatedAtUtc)
            VALUES
                ($imageFolder, $groundTruthCsvPath, $engineName, $modelVersion, $thresholdProfileId, $thresholdProfileRevision, $accuracy, $precision, $recall,
                 $falseCallRate, $totalImages, $failedCount, $createdAtUtc);
            SELECT last_insert_rowid();
            """;

        command.Parameters.AddWithValue("$imageFolder", imageFolder);
        command.Parameters.AddWithValue("$groundTruthCsvPath", (object?)groundTruthCsvPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$engineName", engineName);
        command.Parameters.AddWithValue("$modelVersion", modelVersion);
        command.Parameters.AddWithValue("$thresholdProfileId", thresholdProfileId);
        command.Parameters.AddWithValue("$thresholdProfileRevision", thresholdProfileRevision);
        command.Parameters.AddWithValue("$accuracy", accuracy);
        command.Parameters.AddWithValue("$precision", precision);
        command.Parameters.AddWithValue("$recall", recall);
        command.Parameters.AddWithValue("$falseCallRate", falseCallRate);
        command.Parameters.AddWithValue("$totalImages", results.Count);
        command.Parameters.AddWithValue("$failedCount", results.Count(r => r.PassFail == "FAIL"));
        command.Parameters.AddWithValue("$createdAtUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));

        var runId = (long)(command.ExecuteScalar() ?? 0L);

        foreach (var result in results)
        {
            using var resultCommand = connection.CreateCommand();
            resultCommand.Transaction = transaction;
            resultCommand.CommandText =
                """
                INSERT INTO BatchTestResults
                    (RunId, ImagePath, ImageName, GroundTruth, EngineResult, InspectionEngine, ModelVersion, Score, PassFail,
                     DefectType, NormalizedDefectClass, NormalizedSide, RoiId, RoiType, FailureCategory,
                     RoiX, RoiY, RoiWidth, RoiHeight, Side, RefDes, LotId, BoardModel, Notes,
                     ImageLoadMs, PreprocessingMs, InferenceMs, OverlayRenderingMs, TotalInspectionMs, CreatedAtUtc)
                VALUES
                    ($runId, $imagePath, $imageName, $groundTruth, $engineResult, $inspectionEngine, $modelVersion, $score, $passFail,
                     $defectType, $normalizedDefectClass, $normalizedSide, $roiId, $roiType, $failureCategory,
                     $roiX, $roiY, $roiWidth, $roiHeight, $side, $refDes, $lotId, $boardModel, $notes,
                     $imageLoadMs, $preprocessingMs, $inferenceMs, $overlayRenderingMs, $totalInspectionMs, $createdAtUtc);
                """;

            resultCommand.Parameters.AddWithValue("$runId", runId);
            resultCommand.Parameters.AddWithValue("$imagePath", result.ImagePath);
            resultCommand.Parameters.AddWithValue("$imageName", result.ImageName);
            resultCommand.Parameters.AddWithValue("$groundTruth", result.GroundTruth);
            resultCommand.Parameters.AddWithValue("$engineResult", result.EngineResult);
            resultCommand.Parameters.AddWithValue("$inspectionEngine", result.InspectionEngine);
            resultCommand.Parameters.AddWithValue("$modelVersion", result.ModelVersion);
            resultCommand.Parameters.AddWithValue("$score", result.Score);
            resultCommand.Parameters.AddWithValue("$passFail", result.PassFail);
            resultCommand.Parameters.AddWithValue("$defectType", result.DefectType);
            resultCommand.Parameters.AddWithValue("$normalizedDefectClass", result.NormalizedDefectClass);
            resultCommand.Parameters.AddWithValue("$normalizedSide", result.NormalizedSide);
            resultCommand.Parameters.AddWithValue("$roiId", result.RoiId);
            resultCommand.Parameters.AddWithValue("$roiType", result.RoiType);
            resultCommand.Parameters.AddWithValue("$failureCategory", result.FailureCategory);
            resultCommand.Parameters.AddWithValue("$roiX", result.RoiX);
            resultCommand.Parameters.AddWithValue("$roiY", result.RoiY);
            resultCommand.Parameters.AddWithValue("$roiWidth", result.RoiWidth);
            resultCommand.Parameters.AddWithValue("$roiHeight", result.RoiHeight);
            resultCommand.Parameters.AddWithValue("$side", result.Side);
            resultCommand.Parameters.AddWithValue("$refDes", result.RefDes);
            resultCommand.Parameters.AddWithValue("$lotId", result.LotId);
            resultCommand.Parameters.AddWithValue("$boardModel", result.BoardModel);
            resultCommand.Parameters.AddWithValue("$notes", result.Notes);
            resultCommand.Parameters.AddWithValue("$imageLoadMs", result.ImageLoadMilliseconds);
            resultCommand.Parameters.AddWithValue("$preprocessingMs", result.PreprocessingMilliseconds);
            resultCommand.Parameters.AddWithValue("$inferenceMs", result.InferenceMilliseconds);
            resultCommand.Parameters.AddWithValue("$overlayRenderingMs", result.OverlayRenderingMilliseconds);
            resultCommand.Parameters.AddWithValue("$totalInspectionMs", result.TotalInspectionMilliseconds);
            resultCommand.Parameters.AddWithValue("$createdAtUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            resultCommand.ExecuteNonQuery();
        }

        foreach (var metric in ClassMetricsService.Flatten(ClassMetricsService.Calculate(results.Select(BatchTestRow.FromRecord).ToArray())))
        {
            using var metricCommand = connection.CreateCommand();
            metricCommand.Transaction = transaction;
            metricCommand.CommandText =
                """
                INSERT INTO ValidationBreakdownMetrics
                    (RunId, BreakdownType, Key, DisplayName, Total, TruePositive, TrueNegative,
                     FalsePositive, FalseNegative, WrongDefectClass, WrongSide, UnknownGroundTruth,
                     Precision, Recall, FalseCallRate, CreatedAtUtc)
                VALUES
                    ($runId, $breakdownType, $key, $displayName, $total, $truePositive, $trueNegative,
                     $falsePositive, $falseNegative, $wrongDefectClass, $wrongSide, $unknownGroundTruth,
                     $precision, $recall, $falseCallRate, $createdAtUtc);
                """;
            BindValidationBreakdownMetric(metricCommand, runId, metric);
            metricCommand.ExecuteNonQuery();
        }

        transaction.Commit();
        return runId;
    }

    public static BatchTestRunRecord? GetLatestBatchTestRun()
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT Id, ImageFolder, GroundTruthCsvPath, EngineName, ModelVersion, CreatedAtUtc, Accuracy, Precision,
                   Recall, FalseCallRate, TotalImages, FailedCount, ThresholdProfileId, ThresholdProfileRevision
            FROM BatchTestRuns
            ORDER BY datetime(CreatedAtUtc) DESC, Id DESC
            LIMIT 1;
            """;

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadBatchTestRun(reader) : null;
    }

    public static IReadOnlyList<BatchTestResultRecord> GetBatchTestResults(long runId)
    {
        EnsureInitialized();

        var results = new List<BatchTestResultRecord>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, RunId, ImagePath, ImageName, GroundTruth, EngineResult, Score, PassFail,
                   DefectType, RoiX, RoiY, RoiWidth, RoiHeight, Side, RefDes, LotId, BoardModel,
                   InspectionEngine, ModelVersion, Notes, ImageLoadMs, PreprocessingMs, InferenceMs,
                   NormalizedDefectClass, NormalizedSide, RoiId, RoiType, FailureCategory,
                   OverlayRenderingMs, TotalInspectionMs, CreatedAtUtc
            FROM BatchTestResults
            WHERE RunId = $runId
            ORDER BY Id ASC;
            """;
        command.Parameters.AddWithValue("$runId", runId);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(ReadBatchTestResult(reader));
        }

        return results;
    }

    public static IReadOnlyList<InspectionHistoryRecord> GetInspectionHistory(LogFilter filter)
    {
        EnsureInitialized();

        var records = new List<InspectionHistoryRecord>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        var where = BuildInspectionWhere(filter, command);
        command.CommandText =
            $"""
            SELECT Id, CreatedAtUtc, BoardProgram, OperatorId, InspectionEngine, ModelVersion, ModelFilePath, ConfidenceThreshold,
                   SampleImagePath, GoldenImagePath, Verdict, DifferenceScore, Confidence,
                   SuggestedDefect, DecisionReason, HotspotX, HotspotY, HotspotWidth, HotspotHeight,
                   ImageLoadMs, PreprocessingMs, InferenceMs, OverlayRenderingMs, TotalInspectionMs
            FROM InspectionResults
            {where}
            ORDER BY datetime(CreatedAtUtc) DESC, Id DESC;
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            records.Add(ReadInspectionHistory(reader));
        }

        return records;
    }

    public static IReadOnlyList<ReviewEventRecord> GetReviewEvents(LogFilter filter)
    {
        EnsureInitialized();

        var records = new List<ReviewEventRecord>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        var where = BuildReviewWhere(filter, command);
        command.CommandText =
            $"""
            SELECT Id, EventTimeUtc, Category, OperatorId, Disposition, Message
            FROM ReviewEvents
            {where}
            ORDER BY datetime(EventTimeUtc) DESC, Id DESC;
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            records.Add(ReadReviewEvent(reader));
        }

        return records;
    }

    public static IReadOnlyList<ExportHistoryRecord> GetExportHistory(int limit = 100)
    {
        EnsureInitialized();

        var records = new List<ExportHistoryRecord>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, CreatedAtUtc, ExportType, FilePath, Status, OperatorId, AuditEventId
            FROM ExportHistory
            ORDER BY datetime(CreatedAtUtc) DESC, Id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            records.Add(ReadExportHistory(reader));
        }

        return records;
    }

    public static ExportHistoryRecord? GetExportHistoryRecord(long id)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, CreatedAtUtc, ExportType, FilePath, Status, OperatorId, AuditEventId
            FROM ExportHistory
            WHERE Id = $id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", id);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadExportHistory(reader) : null;
    }

    public static ExportVerificationRecord? GetLatestExportVerification(long exportHistoryId)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, ExportHistoryId, CheckedAtUtc, ExportType, ExportPath, Status,
                   Sha256, SizeBytes, MessagesJson, ArtifactChecksumsJson
            FROM ExportVerification
            WHERE ExportHistoryId = $exportHistoryId
            ORDER BY datetime(CheckedAtUtc) DESC, Id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$exportHistoryId", exportHistoryId);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadExportVerification(reader) : null;
    }

    public static IReadOnlyList<ExportVerificationRecord> GetExportVerifications(int limit = 100)
    {
        EnsureInitialized();

        var records = new List<ExportVerificationRecord>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, ExportHistoryId, CheckedAtUtc, ExportType, ExportPath, Status,
                   Sha256, SizeBytes, MessagesJson, ArtifactChecksumsJson
            FROM ExportVerification
            ORDER BY datetime(CheckedAtUtc) DESC, Id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        using var reader = command.ExecuteReader();
        while (reader.Read())
            records.Add(ReadExportVerification(reader));

        return records;
    }

    public static BuildTestEvidenceRecord? GetLatestBuildTestEvidence()
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, GeneratedAtUtc, CommitSha, Configuration, HygieneStatus, RestoreStatus,
                   BuildStatus, TestStatus, PublishValidationStatus, EvidencePath, OperatorId,
                   CreatedAtUtc, TestResultPath, MachineName
            FROM BuildTestEvidence
            ORDER BY datetime(GeneratedAtUtc) DESC, Id DESC
            LIMIT 1;
            """;

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadBuildTestEvidence(reader) : null;
    }

    public static IReadOnlyList<ValidationPackageRecord> GetValidationPackages(int limit = 100)
    {
        EnsureInitialized();

        var records = new List<ValidationPackageRecord>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, CreatedAtUtc, PackageId, PackagePath, ManifestPath, AcceptanceStatus,
                   Summary, RunId, OperatorId, AuditEventId
            FROM ValidationPackages
            ORDER BY datetime(CreatedAtUtc) DESC, Id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        using var reader = command.ExecuteReader();
        while (reader.Read())
            records.Add(ReadValidationPackage(reader));

        return records;
    }

    public static IReadOnlyList<ValidationBreakdownMetric> GetValidationBreakdownMetrics(long runId)
    {
        EnsureInitialized();

        var metrics = new List<ValidationBreakdownMetric>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, RunId, BreakdownType, Key, DisplayName, Total, TruePositive, TrueNegative,
                   FalsePositive, FalseNegative, WrongDefectClass, WrongSide, UnknownGroundTruth,
                   Precision, Recall, FalseCallRate
            FROM ValidationBreakdownMetrics
            WHERE RunId = $runId
            ORDER BY BreakdownType ASC, (FalsePositive + FalseNegative + WrongDefectClass + WrongSide) DESC, Key ASC;
            """;
        command.Parameters.AddWithValue("$runId", runId);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            metrics.Add(new ValidationBreakdownMetric
            {
                Id = reader.GetInt64(0),
                RunId = reader.GetInt64(1),
                BreakdownType = reader.GetString(2),
                Key = reader.GetString(3),
                DisplayName = reader.GetString(4),
                Total = reader.GetInt32(5),
                TruePositive = reader.GetInt32(6),
                TrueNegative = reader.GetInt32(7),
                FalsePositive = reader.GetInt32(8),
                FalseNegative = reader.GetInt32(9),
                WrongDefectClass = reader.GetInt32(10),
                WrongSide = reader.GetInt32(11),
                UnknownGroundTruth = reader.GetInt32(12),
                Precision = reader.GetDouble(13),
                Recall = reader.GetDouble(14),
                FalseCallRate = reader.GetDouble(15),
            });
        }

        return metrics;
    }

    public static void SaveThresholdProfile(ThresholdProfile profile)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO ThresholdProfiles
                (ProfileId, Revision, BoardModel, BoardProgram, RecipeName, RecipeRevision, Status,
                 SourceValidationRunId, SourceFalseCallReductionRunId, CreatedBy, CreatedAtUtc, ApprovedBy, ApprovedAtUtc)
            VALUES
                ($profileId, $revision, $boardModel, $boardProgram, $recipeName, $recipeRevision, $status,
                 $sourceValidationRunId, $sourceFalseCallReductionRunId, $createdBy, $createdAtUtc, $approvedBy, $approvedAtUtc)
            ON CONFLICT(ProfileId, Revision) DO UPDATE SET
                BoardModel = excluded.BoardModel,
                BoardProgram = excluded.BoardProgram,
                RecipeName = excluded.RecipeName,
                RecipeRevision = excluded.RecipeRevision,
                Status = excluded.Status,
                SourceValidationRunId = excluded.SourceValidationRunId,
                SourceFalseCallReductionRunId = excluded.SourceFalseCallReductionRunId,
                CreatedBy = excluded.CreatedBy,
                CreatedAtUtc = excluded.CreatedAtUtc,
                ApprovedBy = excluded.ApprovedBy,
                ApprovedAtUtc = excluded.ApprovedAtUtc;
            """;
        BindThresholdProfile(command, profile);
        command.ExecuteNonQuery();

        using var deleteRules = connection.CreateCommand();
        deleteRules.Transaction = transaction;
        deleteRules.CommandText = "DELETE FROM ThresholdProfileRules WHERE ProfileId = $profileId AND Revision = $revision;";
        deleteRules.Parameters.AddWithValue("$profileId", profile.ProfileId);
        deleteRules.Parameters.AddWithValue("$revision", profile.Revision);
        deleteRules.ExecuteNonQuery();

        foreach (var rule in profile.Rules)
        {
            using var ruleCommand = connection.CreateCommand();
            ruleCommand.Transaction = transaction;
            ruleCommand.CommandText =
                """
                INSERT INTO ThresholdProfileRules
                    (ProfileId, Revision, ViewType, RoiType, DefectClass, ReviewThreshold, NgThreshold,
                     ConfidenceThreshold, MinimumAreaPixels, MaxAllowedFalseCallRate)
                VALUES
                    ($profileId, $revision, $viewType, $roiType, $defectClass, $reviewThreshold, $ngThreshold,
                     $confidenceThreshold, $minimumAreaPixels, $maxAllowedFalseCallRate);
                """;
            BindThresholdProfileRule(ruleCommand, profile, rule);
            ruleCommand.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public static ThresholdProfile? GetThresholdProfile(string profileId, string revision)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT ProfileId, Revision, BoardModel, BoardProgram, RecipeName, RecipeRevision, Status,
                   SourceValidationRunId, SourceFalseCallReductionRunId, CreatedBy, CreatedAtUtc, ApprovedBy, ApprovedAtUtc
            FROM ThresholdProfiles
            WHERE ProfileId = $profileId AND Revision = $revision
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$profileId", profileId);
        command.Parameters.AddWithValue("$revision", revision);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        var profile = ReadThresholdProfile(reader);
        profile.Rules = GetThresholdProfileRules(profile.ProfileId, profile.Revision).ToList();
        return profile;
    }

    public static IReadOnlyList<ThresholdProfile> GetThresholdProfiles()
    {
        EnsureInitialized();

        var profiles = new List<ThresholdProfile>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT ProfileId, Revision, BoardModel, BoardProgram, RecipeName, RecipeRevision, Status,
                   SourceValidationRunId, SourceFalseCallReductionRunId, CreatedBy, CreatedAtUtc, ApprovedBy, ApprovedAtUtc
            FROM ThresholdProfiles
            ORDER BY datetime(CreatedAtUtc) DESC, ProfileId ASC, Revision DESC;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var profile = ReadThresholdProfile(reader);
            profile.Rules = GetThresholdProfileRules(profile.ProfileId, profile.Revision).ToList();
            profiles.Add(profile);
        }

        return profiles;
    }

    public static ThresholdProfile? GetActiveThresholdProfile(string boardModel, string boardProgram, string recipeName)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT p.ProfileId, p.Revision, p.BoardModel, p.BoardProgram, p.RecipeName, p.RecipeRevision, p.Status,
                   p.SourceValidationRunId, p.SourceFalseCallReductionRunId, p.CreatedBy, p.CreatedAtUtc, p.ApprovedBy, p.ApprovedAtUtc
            FROM ThresholdProfileDeployments d
            INNER JOIN ThresholdProfiles p ON p.ProfileId = d.ProfileId AND p.Revision = d.Revision
            WHERE d.IsActive = 1
              AND (d.BoardModel = $boardModel OR d.BoardModel = 'ANY')
              AND (d.BoardProgram = $boardProgram OR d.BoardProgram = 'ANY')
              AND (d.RecipeName = $recipeName OR d.RecipeName = 'ANY')
            ORDER BY
              CASE WHEN d.BoardModel = $boardModel THEN 1 ELSE 0 END +
              CASE WHEN d.BoardProgram = $boardProgram THEN 1 ELSE 0 END +
              CASE WHEN d.RecipeName = $recipeName THEN 1 ELSE 0 END DESC,
              datetime(d.DeployedAtUtc) DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$boardModel", NormalizeProfileScope(boardModel));
        command.Parameters.AddWithValue("$boardProgram", NormalizeProfileScope(boardProgram));
        command.Parameters.AddWithValue("$recipeName", NormalizeProfileScope(recipeName));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        var profile = ReadThresholdProfile(reader);
        profile.Rules = GetThresholdProfileRules(profile.ProfileId, profile.Revision).ToList();
        return profile;
    }

    public static void UpdateThresholdProfileStatus(string profileId, string revision, string status, string? approvedBy = null, DateTime? approvedAtUtc = null)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE ThresholdProfiles
            SET Status = $status,
                ApprovedBy = COALESCE($approvedBy, ApprovedBy),
                ApprovedAtUtc = COALESCE($approvedAtUtc, ApprovedAtUtc)
            WHERE ProfileId = $profileId AND Revision = $revision;
            """;
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$approvedBy", string.IsNullOrWhiteSpace(approvedBy) ? DBNull.Value : approvedBy);
        command.Parameters.AddWithValue("$approvedAtUtc", approvedAtUtc is { } at ? at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) : DBNull.Value);
        command.Parameters.AddWithValue("$profileId", profileId);
        command.Parameters.AddWithValue("$revision", revision);
        command.ExecuteNonQuery();
    }

    public static void DeployThresholdProfile(ThresholdProfile profile, string deployedBy)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var deactivate = connection.CreateCommand();
        deactivate.Transaction = transaction;
        deactivate.CommandText =
            """
            UPDATE ThresholdProfileDeployments
            SET IsActive = 0
            WHERE BoardModel = $boardModel AND BoardProgram = $boardProgram AND RecipeName = $recipeName;
            """;
        deactivate.Parameters.AddWithValue("$boardModel", NormalizeProfileScope(profile.BoardModel));
        deactivate.Parameters.AddWithValue("$boardProgram", NormalizeProfileScope(profile.BoardProgram));
        deactivate.Parameters.AddWithValue("$recipeName", NormalizeProfileScope(profile.RecipeName));
        deactivate.ExecuteNonQuery();

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            """
            INSERT INTO ThresholdProfileDeployments
                (ProfileId, Revision, BoardModel, BoardProgram, RecipeName, DeployedAtUtc, DeployedBy, IsActive)
            VALUES
                ($profileId, $revision, $boardModel, $boardProgram, $recipeName, $deployedAtUtc, $deployedBy, 1);
            """;
        insert.Parameters.AddWithValue("$profileId", profile.ProfileId);
        insert.Parameters.AddWithValue("$revision", profile.Revision);
        insert.Parameters.AddWithValue("$boardModel", NormalizeProfileScope(profile.BoardModel));
        insert.Parameters.AddWithValue("$boardProgram", NormalizeProfileScope(profile.BoardProgram));
        insert.Parameters.AddWithValue("$recipeName", NormalizeProfileScope(profile.RecipeName));
        insert.Parameters.AddWithValue("$deployedAtUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        insert.Parameters.AddWithValue("$deployedBy", deployedBy);
        insert.ExecuteNonQuery();

        using var updateProfile = connection.CreateCommand();
        updateProfile.Transaction = transaction;
        updateProfile.CommandText = "UPDATE ThresholdProfiles SET Status = 'Deployed' WHERE ProfileId = $profileId AND Revision = $revision;";
        updateProfile.Parameters.AddWithValue("$profileId", profile.ProfileId);
        updateProfile.Parameters.AddWithValue("$revision", profile.Revision);
        updateProfile.ExecuteNonQuery();

        transaction.Commit();
    }

    public static long RecordFalseCallReductionRun(FalseCallReductionRun run, string? operatorId = null)
    {
        EnsureInitialized();

        var effectiveOperator = string.IsNullOrWhiteSpace(operatorId) ? AuditOperatorProvider?.Invoke() ?? "UNKNOWN" : operatorId;
        var selected = run.Recommendation.Point;
        var auditEventId = RecordAuditEvent(
            "FALSE_CALL_RECOMMENDATION",
            selected is null
                ? $"False-call reduction recommendation generated: status={run.Recommendation.Status}; mode={run.Recommendation.Mode}."
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"False-call reduction recommendation generated: status={run.Recommendation.Status}; mode={run.Recommendation.Mode}; threshold={selected.ConfidenceThreshold:F3}; falseCallRate={selected.FalseCallRate:P1}; possibleEscapes={selected.FalseNegative}."),
            operatorWithRole: effectiveOperator,
            relatedEntityType: "FalseCallReductionRun",
            relatedEntityId: run.BatchRunId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO FalseCallReductionRuns
                (BatchRunId, CreatedAtUtc, EngineName, ModelVersion, ModelId, ModelSha256,
                 CriteriaJson, RecommendationStatus, RecommendationMode, SelectedThreshold,
                 SelectedFalseCallRate, SelectedPossibleEscapeRate, SelectedReviewRate,
                 SelectedManualReviewMinutes, SelectedPossibleEscapeCount, RecommendationMessagesJson,
                 OperatorId, AuditEventId)
            VALUES
                ($batchRunId, $createdAtUtc, $engineName, $modelVersion, $modelId, $modelSha256,
                 $criteriaJson, $recommendationStatus, $recommendationMode, $selectedThreshold,
                 $selectedFalseCallRate, $selectedPossibleEscapeRate, $selectedReviewRate,
                 $selectedManualReviewMinutes, $selectedPossibleEscapeCount, $recommendationMessagesJson,
                 $operatorId, $auditEventId);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$batchRunId", run.BatchRunId is { } batchRunId ? (object)batchRunId : DBNull.Value);
        command.Parameters.AddWithValue("$createdAtUtc", run.CreatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$engineName", run.EngineName);
        command.Parameters.AddWithValue("$modelVersion", run.ModelVersion);
        command.Parameters.AddWithValue("$modelId", run.ModelId);
        command.Parameters.AddWithValue("$modelSha256", run.ModelSha256);
        command.Parameters.AddWithValue("$criteriaJson", JsonSerializer.Serialize(run.Criteria));
        command.Parameters.AddWithValue("$recommendationStatus", run.Recommendation.Status);
        command.Parameters.AddWithValue("$recommendationMode", run.Recommendation.Mode);
        command.Parameters.AddWithValue("$selectedThreshold", selected is null ? DBNull.Value : selected.ConfidenceThreshold);
        command.Parameters.AddWithValue("$selectedFalseCallRate", selected is null ? DBNull.Value : selected.FalseCallRate);
        command.Parameters.AddWithValue("$selectedPossibleEscapeRate", selected is null ? DBNull.Value : selected.PossibleEscapeRate);
        command.Parameters.AddWithValue("$selectedReviewRate", selected is null ? DBNull.Value : selected.ReviewRate);
        command.Parameters.AddWithValue("$selectedManualReviewMinutes", selected is null ? DBNull.Value : selected.EstimatedManualReviewMinutes);
        command.Parameters.AddWithValue("$selectedPossibleEscapeCount", selected is null ? DBNull.Value : selected.FalseNegative);
        command.Parameters.AddWithValue("$recommendationMessagesJson", JsonSerializer.Serialize(run.Recommendation.Messages));
        command.Parameters.AddWithValue("$operatorId", effectiveOperator);
        command.Parameters.AddWithValue("$auditEventId", auditEventId);

        var runId = (long)(command.ExecuteScalar() ?? 0L);
        foreach (var point in run.Points)
        {
            using var pointCommand = connection.CreateCommand();
            pointCommand.Transaction = transaction;
            pointCommand.CommandText =
                """
                INSERT INTO FalseCallReductionPoints
                    (RunId, ConfidenceThreshold, DifferenceThreshold, TruePositive, TrueNegative,
                     FalsePositive, FalseNegative, Precision, Recall, FalseCallRate, PossibleEscapeRate,
                     ReviewRate, NgRate, ReviewCount, NgCount, EstimatedManualReviewMinutes,
                     MeetsConstraints, Status)
                VALUES
                    ($runId, $confidenceThreshold, $differenceThreshold, $truePositive, $trueNegative,
                     $falsePositive, $falseNegative, $precision, $recall, $falseCallRate, $possibleEscapeRate,
                     $reviewRate, $ngRate, $reviewCount, $ngCount, $estimatedManualReviewMinutes,
                     $meetsConstraints, $status);
                """;
            pointCommand.Parameters.AddWithValue("$runId", runId);
            BindFalseCallReductionPoint(pointCommand, point);
            pointCommand.ExecuteNonQuery();
        }

        transaction.Commit();
        return runId;
    }

    public static FalseCallReductionRun? GetLatestFalseCallReductionRun(long? batchRunId = null)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = batchRunId is null
            ? """
              SELECT Id, BatchRunId, CreatedAtUtc, EngineName, ModelVersion, ModelId, ModelSha256,
                     CriteriaJson, RecommendationStatus, RecommendationMode, SelectedThreshold,
                     RecommendationMessagesJson
              FROM FalseCallReductionRuns
              ORDER BY datetime(CreatedAtUtc) DESC, Id DESC
              LIMIT 1;
              """
            : """
              SELECT Id, BatchRunId, CreatedAtUtc, EngineName, ModelVersion, ModelId, ModelSha256,
                     CriteriaJson, RecommendationStatus, RecommendationMode, SelectedThreshold,
                     RecommendationMessagesJson
              FROM FalseCallReductionRuns
              WHERE BatchRunId = $batchRunId
              ORDER BY datetime(CreatedAtUtc) DESC, Id DESC
              LIMIT 1;
              """;
        if (batchRunId is { } id)
            command.Parameters.AddWithValue("$batchRunId", id);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        var run = ReadFalseCallReductionRun(reader);
        run.Points = GetFalseCallReductionPoints(run.Id);
        run.Recommendation.Point = run.Points
            .OrderBy(point => Math.Abs(point.ConfidenceThreshold - (run.Recommendation.Point?.ConfidenceThreshold ?? point.ConfidenceThreshold)))
            .FirstOrDefault(point => string.Equals(point.Status, run.Recommendation.Status, StringComparison.OrdinalIgnoreCase)) ?? run.Points.FirstOrDefault();
        return run;
    }

    public static long RecordCameraAcceptanceRun(CameraAcceptanceRun run, string? operatorId = null)
    {
        EnsureInitialized();

        var effectiveOperator = string.IsNullOrWhiteSpace(operatorId) ? AuditOperatorProvider?.Invoke() ?? "UNKNOWN" : operatorId;
        var auditEventId = RecordAuditEvent(
            "CAMERA_ACCEPTANCE_TEST",
            $"Camera acceptance test completed: status={run.Status}; readiness={run.FactoryReadinessStatus}; adapter={run.AdapterName}; realHardware={run.IsRealHardware}.",
            operatorWithRole: effectiveOperator,
            relatedEntityType: "CameraAcceptanceRun");

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO CameraAcceptanceRuns
                (CreatedAtUtc, AdapterName, SourceKey, SettingsSummary, CriteriaJson,
                 Status, FactoryReadinessStatus, IsRealHardware, TotalRequestedFrames,
                 TotalReceivedFrames, DroppedFrameCount, TriggerFailureCount, TimeoutCount,
                 MaxConnectMs, MaxFirstFrameMs, AverageFrameIntervalMs, WarningsJson,
                 FailuresJson, ViewMetricsJson, OperatorId, AuditEventId)
            VALUES
                ($createdAtUtc, $adapterName, $sourceKey, $settingsSummary, $criteriaJson,
                 $status, $factoryReadinessStatus, $isRealHardware, $totalRequestedFrames,
                 $totalReceivedFrames, $droppedFrameCount, $triggerFailureCount, $timeoutCount,
                 $maxConnectMs, $maxFirstFrameMs, $averageFrameIntervalMs, $warningsJson,
                 $failuresJson, $viewMetricsJson, $operatorId, $auditEventId);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$createdAtUtc", run.CreatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$adapterName", run.AdapterName);
        command.Parameters.AddWithValue("$sourceKey", run.SourceKey);
        command.Parameters.AddWithValue("$settingsSummary", run.SettingsSummary);
        command.Parameters.AddWithValue("$criteriaJson", JsonSerializer.Serialize(run.Criteria));
        command.Parameters.AddWithValue("$status", run.Status);
        command.Parameters.AddWithValue("$factoryReadinessStatus", run.FactoryReadinessStatus);
        command.Parameters.AddWithValue("$isRealHardware", run.IsRealHardware ? 1 : 0);
        command.Parameters.AddWithValue("$totalRequestedFrames", run.TotalRequestedFrames);
        command.Parameters.AddWithValue("$totalReceivedFrames", run.TotalReceivedFrames);
        command.Parameters.AddWithValue("$droppedFrameCount", run.DroppedFrameCount);
        command.Parameters.AddWithValue("$triggerFailureCount", run.TriggerFailureCount);
        command.Parameters.AddWithValue("$timeoutCount", run.TimeoutCount);
        command.Parameters.AddWithValue("$maxConnectMs", run.MaxConnectMs);
        command.Parameters.AddWithValue("$maxFirstFrameMs", run.MaxFirstFrameMs);
        command.Parameters.AddWithValue("$averageFrameIntervalMs", run.AverageFrameIntervalMs);
        command.Parameters.AddWithValue("$warningsJson", JsonSerializer.Serialize(run.Warnings));
        command.Parameters.AddWithValue("$failuresJson", JsonSerializer.Serialize(run.Failures));
        command.Parameters.AddWithValue("$viewMetricsJson", JsonSerializer.Serialize(run.ViewMetrics));
        command.Parameters.AddWithValue("$operatorId", effectiveOperator);
        command.Parameters.AddWithValue("$auditEventId", auditEventId);
        var runId = (long)(command.ExecuteScalar() ?? 0L);

        foreach (var frame in run.Frames)
        {
            using var frameCommand = connection.CreateCommand();
            frameCommand.Transaction = transaction;
            frameCommand.CommandText =
                """
                INSERT INTO CameraAcceptanceFrames
                    (RunId, ViewType, Sequence, FrameId, CameraId, CapturedAtUtc,
                     Width, Height, PixelFormat, SourceKind, IsSimulated, LatencyMs,
                     IntervalMs, MetadataValid, Message)
                VALUES
                    ($runId, $viewType, $sequence, $frameId, $cameraId, $capturedAtUtc,
                     $width, $height, $pixelFormat, $sourceKind, $isSimulated, $latencyMs,
                     $intervalMs, $metadataValid, $message);
                """;
            frameCommand.Parameters.AddWithValue("$runId", runId);
            frameCommand.Parameters.AddWithValue("$viewType", frame.ViewType);
            frameCommand.Parameters.AddWithValue("$sequence", frame.Sequence);
            frameCommand.Parameters.AddWithValue("$frameId", frame.FrameId);
            frameCommand.Parameters.AddWithValue("$cameraId", frame.CameraId);
            frameCommand.Parameters.AddWithValue("$capturedAtUtc", frame.CapturedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            frameCommand.Parameters.AddWithValue("$width", frame.Width);
            frameCommand.Parameters.AddWithValue("$height", frame.Height);
            frameCommand.Parameters.AddWithValue("$pixelFormat", frame.PixelFormat);
            frameCommand.Parameters.AddWithValue("$sourceKind", frame.SourceKind);
            frameCommand.Parameters.AddWithValue("$isSimulated", frame.IsSimulated ? 1 : 0);
            frameCommand.Parameters.AddWithValue("$latencyMs", frame.LatencyMs);
            frameCommand.Parameters.AddWithValue("$intervalMs", frame.IntervalMs);
            frameCommand.Parameters.AddWithValue("$metadataValid", frame.MetadataValid ? 1 : 0);
            frameCommand.Parameters.AddWithValue("$message", frame.Message);
            frameCommand.ExecuteNonQuery();
        }

        transaction.Commit();
        return runId;
    }

    public static CameraAcceptanceRun? GetLatestCameraAcceptanceRun(bool realHardwareOnly = false)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, CreatedAtUtc, AdapterName, SourceKey, SettingsSummary, CriteriaJson,
                   Status, FactoryReadinessStatus, IsRealHardware, TotalRequestedFrames,
                   TotalReceivedFrames, DroppedFrameCount, TriggerFailureCount, TimeoutCount,
                   MaxConnectMs, MaxFirstFrameMs, AverageFrameIntervalMs, WarningsJson,
                   FailuresJson, ViewMetricsJson
            FROM CameraAcceptanceRuns
            WHERE ($realHardwareOnly = 0 OR IsRealHardware = 1)
            ORDER BY datetime(CreatedAtUtc) DESC, Id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$realHardwareOnly", realHardwareOnly ? 1 : 0);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        var run = ReadCameraAcceptanceRun(reader);
        run.Frames = GetCameraAcceptanceFrames(run.Id).ToList();
        return run;
    }

    public static long RecordLightingAcceptanceRun(LightingAcceptanceRun run, string? operatorId = null)
    {
        EnsureInitialized();

        var effectiveOperator = string.IsNullOrWhiteSpace(operatorId) ? AuditOperatorProvider?.Invoke() ?? "UNKNOWN" : operatorId;
        var auditEventId = RecordAuditEvent(
            "LIGHTING_ACCEPTANCE_TEST",
            $"Lighting sync acceptance completed: status={run.Status}; mode={run.Mode}; simulated={run.IsSimulated}; steps={run.PassedStepCount}/{run.StepCount}.",
            operatorWithRole: effectiveOperator,
            relatedEntityType: "LightingAcceptanceRun");

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO LightingAcceptanceRuns
                (CreatedAtUtc, ControllerName, Mode, SettingsSummary, CriteriaJson, Status,
                 IsSimulated, StepCount, PassedStepCount, FailedStepCount, MaxCommandLatencyMs,
                 MaxTriggerToFrameLatencyMs, WarningsJson, FailuresJson, OperatorId, AuditEventId)
            VALUES
                ($createdAtUtc, $controllerName, $mode, $settingsSummary, $criteriaJson, $status,
                 $isSimulated, $stepCount, $passedStepCount, $failedStepCount, $maxCommandLatencyMs,
                 $maxTriggerToFrameLatencyMs, $warningsJson, $failuresJson, $operatorId, $auditEventId);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$createdAtUtc", run.CreatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$controllerName", run.ControllerName);
        command.Parameters.AddWithValue("$mode", run.Mode);
        command.Parameters.AddWithValue("$settingsSummary", run.SettingsSummary);
        command.Parameters.AddWithValue("$criteriaJson", JsonSerializer.Serialize(run.Criteria));
        command.Parameters.AddWithValue("$status", run.Status);
        command.Parameters.AddWithValue("$isSimulated", run.IsSimulated ? 1 : 0);
        command.Parameters.AddWithValue("$stepCount", run.StepCount);
        command.Parameters.AddWithValue("$passedStepCount", run.PassedStepCount);
        command.Parameters.AddWithValue("$failedStepCount", run.FailedStepCount);
        command.Parameters.AddWithValue("$maxCommandLatencyMs", run.MaxCommandLatencyMs);
        command.Parameters.AddWithValue("$maxTriggerToFrameLatencyMs", run.MaxTriggerToFrameLatencyMs);
        command.Parameters.AddWithValue("$warningsJson", JsonSerializer.Serialize(run.Warnings));
        command.Parameters.AddWithValue("$failuresJson", JsonSerializer.Serialize(run.Failures));
        command.Parameters.AddWithValue("$operatorId", effectiveOperator);
        command.Parameters.AddWithValue("$auditEventId", auditEventId);
        var runId = (long)(command.ExecuteScalar() ?? 0L);

        foreach (var step in run.Steps)
        {
            using var stepCommand = connection.CreateCommand();
            stepCommand.Transaction = transaction;
            stepCommand.CommandText =
                """
                INSERT INTO LightingAcceptanceSteps
                    (RunId, ViewType, ProgramName, CommandText, CommandLatencyMs,
                     TriggerToFrameLatencyMs, CommandAccepted, FrameReceived, FrameId,
                     CameraId, Status, Message)
                VALUES
                    ($runId, $viewType, $programName, $commandText, $commandLatencyMs,
                     $triggerToFrameLatencyMs, $commandAccepted, $frameReceived, $frameId,
                     $cameraId, $status, $message);
                """;
            stepCommand.Parameters.AddWithValue("$runId", runId);
            stepCommand.Parameters.AddWithValue("$viewType", step.ViewType);
            stepCommand.Parameters.AddWithValue("$programName", step.ProgramName);
            stepCommand.Parameters.AddWithValue("$commandText", step.CommandText);
            stepCommand.Parameters.AddWithValue("$commandLatencyMs", step.CommandLatencyMs);
            stepCommand.Parameters.AddWithValue("$triggerToFrameLatencyMs", step.TriggerToFrameLatencyMs);
            stepCommand.Parameters.AddWithValue("$commandAccepted", step.CommandAccepted ? 1 : 0);
            stepCommand.Parameters.AddWithValue("$frameReceived", step.FrameReceived ? 1 : 0);
            stepCommand.Parameters.AddWithValue("$frameId", step.FrameId);
            stepCommand.Parameters.AddWithValue("$cameraId", step.CameraId);
            stepCommand.Parameters.AddWithValue("$status", step.Status);
            stepCommand.Parameters.AddWithValue("$message", step.Message);
            stepCommand.ExecuteNonQuery();
        }

        transaction.Commit();
        return runId;
    }

    public static LightingAcceptanceRun? GetLatestLightingAcceptanceRun()
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, CreatedAtUtc, ControllerName, Mode, SettingsSummary, CriteriaJson,
                   Status, IsSimulated, StepCount, PassedStepCount, FailedStepCount,
                   MaxCommandLatencyMs, MaxTriggerToFrameLatencyMs, WarningsJson, FailuresJson
            FROM LightingAcceptanceRuns
            ORDER BY datetime(CreatedAtUtc) DESC, Id DESC
            LIMIT 1;
            """;

        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        var run = ReadLightingAcceptanceRun(reader);
        run.Steps = GetLightingAcceptanceSteps(run.Id).ToList();
        return run;
    }

    public static long RecordProfile3DAcceptanceRun(Profile3DAcceptanceRun run, string? operatorId = null)
    {
        EnsureInitialized();

        var effectiveOperator = string.IsNullOrWhiteSpace(operatorId) ? AuditOperatorProvider?.Invoke() ?? "UNKNOWN" : operatorId;
        var auditEventId = RecordAuditEvent(
            "PROFILE_3D_ACCEPTANCE_TEST",
            $"3D profile acceptance completed: status={run.Status}; readiness={run.FactoryReadinessStatus}; source={run.SourceName}; simulated={run.IsSimulated}.",
            operatorWithRole: effectiveOperator,
            relatedEntityType: "Profile3DAcceptanceRun");

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO Profile3DAcceptanceRuns
                (CreatedAtUtc, SourceName, SourceKind, IsSimulated, Status, FactoryReadinessStatus,
                 AcquisitionMs, Width, Height, Unit, XPitchMicrons, YPitchMicrons, MissingHeightCount,
                 NaNHeightCount, FrameId, CriteriaJson, DiagnosticsJson, WarningsJson, FailuresJson,
                 OperatorId, AuditEventId)
            VALUES
                ($createdAtUtc, $sourceName, $sourceKind, $isSimulated, $status, $factoryReadinessStatus,
                 $acquisitionMs, $width, $height, $unit, $xPitchMicrons, $yPitchMicrons, $missingHeightCount,
                 $nanHeightCount, $frameId, $criteriaJson, $diagnosticsJson, $warningsJson, $failuresJson,
                 $operatorId, $auditEventId);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$createdAtUtc", run.CreatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$sourceName", run.SourceName);
        command.Parameters.AddWithValue("$sourceKind", run.SourceKind);
        command.Parameters.AddWithValue("$isSimulated", run.IsSimulated ? 1 : 0);
        command.Parameters.AddWithValue("$status", run.Status);
        command.Parameters.AddWithValue("$factoryReadinessStatus", run.FactoryReadinessStatus);
        command.Parameters.AddWithValue("$acquisitionMs", run.AcquisitionMs);
        command.Parameters.AddWithValue("$width", run.Width);
        command.Parameters.AddWithValue("$height", run.Height);
        command.Parameters.AddWithValue("$unit", run.Unit);
        command.Parameters.AddWithValue("$xPitchMicrons", run.XPitchMicrons);
        command.Parameters.AddWithValue("$yPitchMicrons", run.YPitchMicrons);
        command.Parameters.AddWithValue("$missingHeightCount", run.MissingHeightCount);
        command.Parameters.AddWithValue("$nanHeightCount", run.NaNHeightCount);
        command.Parameters.AddWithValue("$frameId", run.FrameId);
        command.Parameters.AddWithValue("$criteriaJson", JsonSerializer.Serialize(run.Criteria));
        command.Parameters.AddWithValue("$diagnosticsJson", JsonSerializer.Serialize(run.Diagnostics));
        command.Parameters.AddWithValue("$warningsJson", JsonSerializer.Serialize(run.Warnings));
        command.Parameters.AddWithValue("$failuresJson", JsonSerializer.Serialize(run.Failures));
        command.Parameters.AddWithValue("$operatorId", effectiveOperator);
        command.Parameters.AddWithValue("$auditEventId", auditEventId);
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    public static Profile3DAcceptanceRun? GetLatestProfile3DAcceptanceRun()
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, CreatedAtUtc, SourceName, SourceKind, IsSimulated, Status,
                   FactoryReadinessStatus, AcquisitionMs, Width, Height, Unit,
                   XPitchMicrons, YPitchMicrons, MissingHeightCount, NaNHeightCount,
                   FrameId, CriteriaJson, DiagnosticsJson, WarningsJson, FailuresJson
            FROM Profile3DAcceptanceRuns
            ORDER BY datetime(CreatedAtUtc) DESC, Id DESC
            LIMIT 1;
            """;
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadProfile3DAcceptanceRun(reader) : null;
    }

    public static long RecordRobotAcceptanceRun(RobotAcceptanceRun run, string? operatorId = null)
    {
        EnsureInitialized();

        var effectiveOperator = string.IsNullOrWhiteSpace(operatorId) ? AuditOperatorProvider?.Invoke() ?? "UNKNOWN" : operatorId;
        var auditEventId = RecordAuditEvent(
            "ROBOT_ACCEPTANCE_TEST",
            $"Robot acceptance completed: status={run.Status}; source={run.SourceKind}; controller={run.ControllerName}; cycleMs={run.FullCycleMs:F1}.",
            operatorWithRole: effectiveOperator,
            relatedEntityType: "RobotAcceptanceRun");

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO RobotAcceptanceRuns
                (CreatedAtUtc, ControllerName, EmergencyStopName, SafetyControllerName, SafetySourceKind, SourceKind, CriteriaJson,
                 Status, FinalState, LoadMs, MoveToInspectMs, InspectionMs, UnloadMs,
                 FullCycleMs, InvalidTransitionRejected, EmergencyStopBlocked, SafetyFaultBlocked, ResetReturnedIdle,
                 AuditEventCount, WarningsJson, FailuresJson, OperatorId, AuditEventId)
            VALUES
                ($createdAtUtc, $controllerName, $emergencyStopName, $safetyControllerName, $safetySourceKind, $sourceKind, $criteriaJson,
                 $status, $finalState, $loadMs, $moveToInspectMs, $inspectionMs, $unloadMs,
                 $fullCycleMs, $invalidTransitionRejected, $emergencyStopBlocked, $safetyFaultBlocked, $resetReturnedIdle,
                 $auditEventCount, $warningsJson, $failuresJson, $operatorId, $auditEventId);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$createdAtUtc", run.CreatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$controllerName", run.ControllerName);
        command.Parameters.AddWithValue("$emergencyStopName", run.EmergencyStopName);
        command.Parameters.AddWithValue("$safetyControllerName", run.SafetyControllerName);
        command.Parameters.AddWithValue("$safetySourceKind", run.SafetySourceKind);
        command.Parameters.AddWithValue("$sourceKind", run.SourceKind);
        command.Parameters.AddWithValue("$criteriaJson", JsonSerializer.Serialize(run.Criteria));
        command.Parameters.AddWithValue("$status", run.Status);
        command.Parameters.AddWithValue("$finalState", run.FinalState);
        command.Parameters.AddWithValue("$loadMs", run.LoadMs);
        command.Parameters.AddWithValue("$moveToInspectMs", run.MoveToInspectMs);
        command.Parameters.AddWithValue("$inspectionMs", run.InspectionMs);
        command.Parameters.AddWithValue("$unloadMs", run.UnloadMs);
        command.Parameters.AddWithValue("$fullCycleMs", run.FullCycleMs);
        command.Parameters.AddWithValue("$invalidTransitionRejected", run.InvalidTransitionRejected ? 1 : 0);
        command.Parameters.AddWithValue("$emergencyStopBlocked", run.EmergencyStopBlocked ? 1 : 0);
        command.Parameters.AddWithValue("$safetyFaultBlocked", run.SafetyFaultBlocked ? 1 : 0);
        command.Parameters.AddWithValue("$resetReturnedIdle", run.ResetReturnedIdle ? 1 : 0);
        command.Parameters.AddWithValue("$auditEventCount", run.AuditEventCount);
        command.Parameters.AddWithValue("$warningsJson", JsonSerializer.Serialize(run.Warnings));
        command.Parameters.AddWithValue("$failuresJson", JsonSerializer.Serialize(run.Failures));
        command.Parameters.AddWithValue("$operatorId", effectiveOperator);
        command.Parameters.AddWithValue("$auditEventId", auditEventId);
        var runId = (long)(command.ExecuteScalar() ?? 0L);

        foreach (var step in run.Steps)
        {
            using var stepCommand = connection.CreateCommand();
            stepCommand.Transaction = transaction;
            stepCommand.CommandText =
                """
                INSERT INTO RobotAcceptanceSteps
                    (RunId, StepName, FromState, ToState, ElapsedMs, Accepted, Status, Message)
                VALUES
                    ($runId, $stepName, $fromState, $toState, $elapsedMs, $accepted, $status, $message);
                """;
            stepCommand.Parameters.AddWithValue("$runId", runId);
            stepCommand.Parameters.AddWithValue("$stepName", step.StepName);
            stepCommand.Parameters.AddWithValue("$fromState", step.FromState);
            stepCommand.Parameters.AddWithValue("$toState", step.ToState);
            stepCommand.Parameters.AddWithValue("$elapsedMs", step.ElapsedMs);
            stepCommand.Parameters.AddWithValue("$accepted", step.Accepted ? 1 : 0);
            stepCommand.Parameters.AddWithValue("$status", step.Status);
            stepCommand.Parameters.AddWithValue("$message", step.Message);
            stepCommand.ExecuteNonQuery();
        }

        transaction.Commit();
        return runId;
    }

    public static RobotAcceptanceRun? GetLatestRobotAcceptanceRun()
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, CreatedAtUtc, ControllerName, EmergencyStopName, SafetyControllerName, SafetySourceKind, SourceKind, CriteriaJson,
                   Status, FinalState, LoadMs, MoveToInspectMs, InspectionMs, UnloadMs,
                   FullCycleMs, InvalidTransitionRejected, EmergencyStopBlocked, SafetyFaultBlocked, ResetReturnedIdle,
                   AuditEventCount, WarningsJson, FailuresJson
            FROM RobotAcceptanceRuns
            ORDER BY datetime(CreatedAtUtc) DESC, Id DESC
            LIMIT 1;
            """;
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        var run = ReadRobotAcceptanceRun(reader);
        run.Steps = GetRobotAcceptanceSteps(run.Id).ToList();
        return run;
    }

    public static long RecordSoakTestRun(SoakTestResult run, string? operatorId = null)
    {
        EnsureInitialized();

        var effectiveOperator = string.IsNullOrWhiteSpace(operatorId) ? AuditOperatorProvider?.Invoke() ?? run.OperatorId : operatorId;
        var auditEventId = RecordAuditEvent(
            "SOAK_TEST",
            $"Soak test persisted: run={run.RunId}; cycles={run.TotalCycles}; failures={run.FailedCycles}; canceled={run.WasCanceled}; factoryEvidence={run.IsCompletedFactoryEvidence}.",
            operatorWithRole: effectiveOperator,
            relatedEntityType: "SoakTestRun",
            relatedEntityId: run.RunId);

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO SoakTestRuns
                (RunId, StartedAtUtc, EndedAtUtc, ImageFolder, OutputFolder, EngineKey, EngineName,
                 EngineVersion, SourceKind, IsRealCameraSource, ProfileName, RequestedDurationSeconds,
                 ActualDurationSeconds, DelayBetweenInspectionsMs, OperatorId, BoardModel, LotId,
                 WasCanceled, TotalCycles, SuccessfulCycles, FailedCycles, AverageInspectionMs,
                 MinInspectionMs, MaxInspectionMs, P95InspectionMs, CountOverOneSecond,
                 StartManagedMemoryMb, EndManagedMemoryMb, StartWorkingSetMb, EndWorkingSetMb,
                 PeakWorkingSetMb, IsCompletedFactoryEvidence, ErrorsJson, AuditEventId,
                 AverageTotalCycleMs, MaxTotalCycleMs, P95TotalCycleMs, CancellationReason,
                 FirstCriticalError, MemoryWarningsJson)
            VALUES
                ($runId, $startedAtUtc, $endedAtUtc, $imageFolder, $outputFolder, $engineKey, $engineName,
                 $engineVersion, $sourceKind, $isRealCameraSource, $profileName, $requestedDurationSeconds,
                 $actualDurationSeconds, $delayBetweenInspectionsMs, $operatorId, $boardModel, $lotId,
                 $wasCanceled, $totalCycles, $successfulCycles, $failedCycles, $averageInspectionMs,
                 $minInspectionMs, $maxInspectionMs, $p95InspectionMs, $countOverOneSecond,
                 $startManagedMemoryMb, $endManagedMemoryMb, $startWorkingSetMb, $endWorkingSetMb,
                 $peakWorkingSetMb, $isCompletedFactoryEvidence, $errorsJson, $auditEventId,
                 $averageTotalCycleMs, $maxTotalCycleMs, $p95TotalCycleMs, $cancellationReason,
                 $firstCriticalError, $memoryWarningsJson);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$runId", run.RunId);
        command.Parameters.AddWithValue("$startedAtUtc", run.StartTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$endedAtUtc", run.EndTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$imageFolder", run.ImageFolder);
        command.Parameters.AddWithValue("$outputFolder", run.OutputFolder);
        command.Parameters.AddWithValue("$engineKey", run.EngineKey);
        command.Parameters.AddWithValue("$engineName", run.EngineName);
        command.Parameters.AddWithValue("$engineVersion", run.EngineVersion);
        command.Parameters.AddWithValue("$sourceKind", run.SourceKind);
        command.Parameters.AddWithValue("$isRealCameraSource", run.IsRealCameraSource ? 1 : 0);
        command.Parameters.AddWithValue("$profileName", run.ProfileName);
        command.Parameters.AddWithValue("$requestedDurationSeconds", run.RequestedDuration.TotalSeconds);
        command.Parameters.AddWithValue("$actualDurationSeconds", Math.Max(0, run.ActualDuration.TotalSeconds));
        command.Parameters.AddWithValue("$delayBetweenInspectionsMs", run.DelayBetweenInspections.TotalMilliseconds);
        command.Parameters.AddWithValue("$operatorId", effectiveOperator);
        command.Parameters.AddWithValue("$boardModel", run.BoardModel);
        command.Parameters.AddWithValue("$lotId", run.LotId);
        command.Parameters.AddWithValue("$wasCanceled", run.WasCanceled ? 1 : 0);
        command.Parameters.AddWithValue("$totalCycles", run.TotalCycles);
        command.Parameters.AddWithValue("$successfulCycles", run.SuccessfulCycles);
        command.Parameters.AddWithValue("$failedCycles", run.FailedCycles);
        command.Parameters.AddWithValue("$averageInspectionMs", run.AverageInspectionMilliseconds);
        command.Parameters.AddWithValue("$minInspectionMs", run.MinInspectionMilliseconds);
        command.Parameters.AddWithValue("$maxInspectionMs", run.MaxInspectionMilliseconds);
        command.Parameters.AddWithValue("$p95InspectionMs", run.P95InspectionMilliseconds);
        command.Parameters.AddWithValue("$countOverOneSecond", run.CountOverOneSecond);
        command.Parameters.AddWithValue("$startManagedMemoryMb", run.StartManagedMemoryMegabytes);
        command.Parameters.AddWithValue("$endManagedMemoryMb", run.EndManagedMemoryMegabytes);
        command.Parameters.AddWithValue("$startWorkingSetMb", run.StartWorkingSetMegabytes);
        command.Parameters.AddWithValue("$endWorkingSetMb", run.EndWorkingSetMegabytes);
        command.Parameters.AddWithValue("$peakWorkingSetMb", run.PeakWorkingSetMegabytes);
        command.Parameters.AddWithValue("$isCompletedFactoryEvidence", run.IsCompletedFactoryEvidence ? 1 : 0);
        command.Parameters.AddWithValue("$errorsJson", JsonSerializer.Serialize(run.Errors));
        command.Parameters.AddWithValue("$auditEventId", auditEventId);
        command.Parameters.AddWithValue("$averageTotalCycleMs", run.AverageTotalCycleMilliseconds);
        command.Parameters.AddWithValue("$maxTotalCycleMs", run.MaxTotalCycleMilliseconds);
        command.Parameters.AddWithValue("$p95TotalCycleMs", run.P95TotalCycleMilliseconds);
        command.Parameters.AddWithValue("$cancellationReason", run.CancellationReason);
        command.Parameters.AddWithValue("$firstCriticalError", run.FirstCriticalError);
        command.Parameters.AddWithValue("$memoryWarningsJson", JsonSerializer.Serialize(run.MemoryWarnings));

        var runRowId = (long)(command.ExecuteScalar() ?? 0L);
        run.Id = runRowId;

        using var iterationCommand = connection.CreateCommand();
        iterationCommand.Transaction = transaction;
        iterationCommand.CommandText =
            """
            INSERT INTO SoakTestIterations
                (RunId, CycleNumber, TimestampUtc, FrameId, ImagePath, EngineName, Verdict,
                 TotalInspectionMs, WorkingSetMb, Success, Message, Error, TotalCycleMs, ExceptionCategory)
            VALUES
                ($runId, $cycleNumber, $timestampUtc, $frameId, $imagePath, $engineName, $verdict,
                 $totalInspectionMs, $workingSetMb, $success, $message, $error, $totalCycleMs, $exceptionCategory);
            """;
        foreach (var cycle in run.Cycles)
        {
            iterationCommand.Parameters.Clear();
            iterationCommand.Parameters.AddWithValue("$runId", runRowId);
            iterationCommand.Parameters.AddWithValue("$cycleNumber", cycle.CycleNumber);
            iterationCommand.Parameters.AddWithValue("$timestampUtc", (cycle.TimestampUtc ?? run.StartTime.ToUniversalTime()).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            iterationCommand.Parameters.AddWithValue("$frameId", cycle.FrameId);
            iterationCommand.Parameters.AddWithValue("$imagePath", cycle.ImagePath);
            iterationCommand.Parameters.AddWithValue("$engineName", cycle.EngineName);
            iterationCommand.Parameters.AddWithValue("$verdict", cycle.Verdict);
            iterationCommand.Parameters.AddWithValue("$totalInspectionMs", cycle.TotalMilliseconds);
            iterationCommand.Parameters.AddWithValue("$workingSetMb", cycle.WorkingSetMegabytes);
            iterationCommand.Parameters.AddWithValue("$success", cycle.Success ? 1 : 0);
            iterationCommand.Parameters.AddWithValue("$message", cycle.Message);
            iterationCommand.Parameters.AddWithValue("$error", cycle.Error);
            iterationCommand.Parameters.AddWithValue("$totalCycleMs", cycle.TotalCycleMilliseconds);
            iterationCommand.Parameters.AddWithValue("$exceptionCategory", cycle.ExceptionCategory);
            iterationCommand.ExecuteNonQuery();
        }

        transaction.Commit();
        return runRowId;
    }

    public static SoakTestResult? GetLatestSoakTestRun()
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, RunId, StartedAtUtc, EndedAtUtc, ImageFolder, OutputFolder, EngineKey, EngineName,
                   EngineVersion, SourceKind, IsRealCameraSource, ProfileName, RequestedDurationSeconds,
                   ActualDurationSeconds, DelayBetweenInspectionsMs, OperatorId, BoardModel, LotId,
                   WasCanceled, TotalCycles, SuccessfulCycles, FailedCycles, AverageInspectionMs,
                   MinInspectionMs, MaxInspectionMs, P95InspectionMs, CountOverOneSecond,
                   StartManagedMemoryMb, EndManagedMemoryMb, StartWorkingSetMb, EndWorkingSetMb,
                   PeakWorkingSetMb, IsCompletedFactoryEvidence, ErrorsJson, AverageTotalCycleMs,
                   MaxTotalCycleMs, P95TotalCycleMs, CancellationReason, FirstCriticalError,
                   MemoryWarningsJson
            FROM SoakTestRuns
            ORDER BY datetime(StartedAtUtc) DESC, Id DESC
            LIMIT 1;
            """;

        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        var run = ReadSoakTestRun(reader);
        run.Cycles.AddRange(GetSoakTestIterations(run.Id));
        return run;
    }

    public static IReadOnlyList<ModelRegistryRecord> GetModelRegistryRecords()
    {
        EnsureInitialized();

        var records = new List<ModelRegistryRecord>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, ModelId, DisplayName, Version, CreatedAtUtc, RegisteredAtUtc, SourceFileName,
                   StoredModelPath, StoredLabelMapPath, MetadataPath, Sha256, InputTensorName, OutputTensorName,
                   InputWidth, InputHeight, ConfidenceThreshold, LabelsJson, ValidationStatus, LastValidatedAtUtc,
                   ValidationMessage, Notes, IsActive, AuditEventId, LifecycleState, LatestAcceptanceStatus,
                   LatestAcceptanceRunId, LatestReleasePackageId, LatestReleasePackagePath, DeploymentWaiverReason,
                   WaiverExpiresAtUtc, DeploymentWaivedBy, DeploymentWaivedAtUtc, DeployedAtUtc, RetiredReason, RetiredAtUtc
            FROM ModelRegistry
            ORDER BY IsActive DESC, datetime(RegisteredAtUtc) DESC, Id DESC;
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
            records.Add(ReadModelRegistryRecord(reader));

        return records;
    }

    public static ModelRegistryRecord? GetActiveModelRegistryRecord()
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, ModelId, DisplayName, Version, CreatedAtUtc, RegisteredAtUtc, SourceFileName,
                   StoredModelPath, StoredLabelMapPath, MetadataPath, Sha256, InputTensorName, OutputTensorName,
                   InputWidth, InputHeight, ConfidenceThreshold, LabelsJson, ValidationStatus, LastValidatedAtUtc,
                   ValidationMessage, Notes, IsActive, AuditEventId, LifecycleState, LatestAcceptanceStatus,
                   LatestAcceptanceRunId, LatestReleasePackageId, LatestReleasePackagePath, DeploymentWaiverReason,
                   WaiverExpiresAtUtc, DeploymentWaivedBy, DeploymentWaivedAtUtc, DeployedAtUtc, RetiredReason, RetiredAtUtc
            FROM ModelRegistry
            WHERE IsActive = 1
            ORDER BY datetime(RegisteredAtUtc) DESC, Id DESC
            LIMIT 1;
            """;

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadModelRegistryRecord(reader) : null;
    }

    public static ModelAcceptanceRun? GetLatestModelAcceptanceRun(string? modelId = null)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        var filter = string.IsNullOrWhiteSpace(modelId) ? string.Empty : "WHERE ModelId = $modelId";
        command.CommandText =
            $"""
            SELECT Id, CreatedAtUtc, ModelId, ModelVersion, ModelSha256, ModelPath, LabelMapPath,
                   InputTensorName, OutputTensorName, OutputShape, DatasetFolder, DatasetName,
                   GroundTruthCsvPath, IsFormalManifest, Status, OperatorId, ApprovedBy, ApprovedAtUtc,
                   IsProductionCandidate, CriteriaJson, MetricsJson, DatasetQualityJson,
                   FalseCallRecommendationJson, BreakdownJson, PerformanceJson, P95InferenceMs,
                   MessagesJson, LimitationsJson
            FROM ModelAcceptanceRuns
            {filter}
            ORDER BY datetime(CreatedAtUtc) DESC, Id DESC
            LIMIT 1;
            """;
        if (!string.IsNullOrWhiteSpace(modelId))
            command.Parameters.AddWithValue("$modelId", modelId);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadModelAcceptanceRun(reader) : null;
    }

    public static ModelAcceptanceRun? GetLatestPassingProductionModelAcceptance(string? modelId = null)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        var filter = string.IsNullOrWhiteSpace(modelId) ? string.Empty : "AND ModelId = $modelId";
        command.CommandText =
            $"""
            SELECT Id, CreatedAtUtc, ModelId, ModelVersion, ModelSha256, ModelPath, LabelMapPath,
                   InputTensorName, OutputTensorName, OutputShape, DatasetFolder, DatasetName,
                   GroundTruthCsvPath, IsFormalManifest, Status, OperatorId, ApprovedBy, ApprovedAtUtc,
                   IsProductionCandidate, CriteriaJson, MetricsJson, DatasetQualityJson,
                   FalseCallRecommendationJson, BreakdownJson, PerformanceJson, P95InferenceMs,
                   MessagesJson, LimitationsJson
            FROM ModelAcceptanceRuns
            WHERE Status = 'PASS'
              AND IsProductionCandidate = 1
              {filter}
            ORDER BY datetime(CreatedAtUtc) DESC, Id DESC
            LIMIT 1;
            """;
        if (!string.IsNullOrWhiteSpace(modelId))
            command.Parameters.AddWithValue("$modelId", modelId);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadModelAcceptanceRun(reader) : null;
    }

    public static IReadOnlyList<AuditEventRecord> GetAuditEvents(LogFilter filter, int limit = 500)
    {
        EnsureInitialized();

        var records = new List<AuditEventRecord>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        var where = BuildAuditWhere(filter, command);
        command.CommandText =
            $"""
            SELECT Id, TimestampUtc, LocalTimestamp, UserId, UserRole, StationId,
                   ActionCategory, ActionDetail, RelatedEntityType, RelatedEntityId, RelatedPath
            FROM AuditEvents
            {where}
            ORDER BY datetime(TimestampUtc) DESC, Id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        using var reader = command.ExecuteReader();
        while (reader.Read())
            records.Add(ReadAuditEvent(reader));

        return records;
    }

    public static RecipeRevisionRecord? GetLatestRecipeRevision(string boardProgram)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, RecipeName, Revision, BoardProgram, OperatorId, DetectionPriority,
                   BackgroundImagePath, RecipeJson, CreatedAtUtc
            FROM RecipeRevisions
            WHERE BoardProgram = $boardProgram
            ORDER BY datetime(CreatedAtUtc) DESC, Id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$boardProgram", boardProgram);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRecipeRevision(reader) : null;
    }

    public static IReadOnlyList<RecipeRevisionRecord> GetRecipeRevisions()
    {
        EnsureInitialized();

        var revisions = new List<RecipeRevisionRecord>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, RecipeName, Revision, BoardProgram, OperatorId, DetectionPriority,
                   BackgroundImagePath, RecipeJson, CreatedAtUtc
            FROM RecipeRevisions
            ORDER BY datetime(CreatedAtUtc) DESC, Id DESC;
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
            revisions.Add(ReadRecipeRevision(reader));

        return revisions;
    }

    public static long SaveRecipeRevision(
        string recipeName,
        string boardProgram,
        string operatorId,
        string detectionPriority,
        string backgroundImagePath,
        string recipeJson)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        var revision = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        command.CommandText =
            """
            INSERT INTO RecipeRevisions
                (RecipeName, Revision, BoardProgram, OperatorId, DetectionPriority,
                 BackgroundImagePath, RecipeJson, Notes, CreatedAtUtc)
            VALUES
                ($recipeName, $revision, $boardProgram, $operatorId, $detectionPriority,
                 $backgroundImagePath, $recipeJson, $notes, $createdAtUtc);
            SELECT last_insert_rowid();
            """;

        command.Parameters.AddWithValue("$recipeName", recipeName);
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$boardProgram", boardProgram);
        command.Parameters.AddWithValue("$operatorId", operatorId);
        command.Parameters.AddWithValue("$detectionPriority", detectionPriority);
        command.Parameters.AddWithValue("$backgroundImagePath", backgroundImagePath);
        command.Parameters.AddWithValue("$recipeJson", recipeJson);
        command.Parameters.AddWithValue("$notes", "Recipe editor revision");
        command.Parameters.AddWithValue("$createdAtUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        var id = (long)(command.ExecuteScalar() ?? 0L);
        RecordAuditEvent(
            "RECIPE_SAVE",
            $"Recipe revision saved: {recipeName}; board={boardProgram}; priority={detectionPriority}.",
            operatorWithRole: operatorId,
            relatedEntityType: "RecipeRevision",
            relatedEntityId: id.ToString(CultureInfo.InvariantCulture),
            relatedPath: backgroundImagePath);
        return id;
    }

    public static long SaveCalibrationProfile(
        string profileName,
        string boardModel,
        string viewType,
        string sampleImagePath,
        string operatorId,
        IReadOnlyList<CalibrationPointInput> points)
    {
        EnsureInitialized();

        if (string.IsNullOrWhiteSpace(profileName))
            throw new ArgumentException("Calibration profile name is required.", nameof(profileName));
        if (points.Count == 0)
            throw new ArgumentException("At least one calibration point is required.", nameof(points));

        var transform = CalibrationTransformService.Calculate(points);
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO CalibrationProfiles
                (ProfileName, BoardModel, ViewType, SampleImagePath, OperatorId, PointCount,
                 ScaleX, OffsetX, ScaleY, OffsetY, TransformSummary, CreatedAtUtc)
            VALUES
                ($profileName, $boardModel, $viewType, $sampleImagePath, $operatorId, $pointCount,
                 $scaleX, $offsetX, $scaleY, $offsetY, $transformSummary, $createdAtUtc);
            SELECT last_insert_rowid();
            """;

        command.Parameters.AddWithValue("$profileName", profileName.Trim());
        command.Parameters.AddWithValue("$boardModel", string.IsNullOrWhiteSpace(boardModel) ? "UNKNOWN" : boardModel.Trim());
        command.Parameters.AddWithValue("$viewType", string.IsNullOrWhiteSpace(viewType) ? "Top" : viewType.Trim());
        command.Parameters.AddWithValue("$sampleImagePath", string.IsNullOrWhiteSpace(sampleImagePath) ? string.Empty : sampleImagePath.Trim());
        command.Parameters.AddWithValue("$operatorId", string.IsNullOrWhiteSpace(operatorId) ? AuditOperatorProvider?.Invoke() ?? "UNKNOWN" : operatorId.Trim());
        command.Parameters.AddWithValue("$pointCount", points.Count);
        command.Parameters.AddWithValue("$scaleX", transform.ScaleX);
        command.Parameters.AddWithValue("$offsetX", transform.OffsetX);
        command.Parameters.AddWithValue("$scaleY", transform.ScaleY);
        command.Parameters.AddWithValue("$offsetY", transform.OffsetY);
        command.Parameters.AddWithValue("$transformSummary", transform.Summary);
        command.Parameters.AddWithValue("$createdAtUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));

        var profileId = (long)(command.ExecuteScalar() ?? 0L);
        foreach (var point in points)
        {
            using var pointCommand = connection.CreateCommand();
            pointCommand.Transaction = transaction;
            pointCommand.CommandText =
                """
                INSERT INTO CalibrationPoints
                    (ProfileId, ImageX, ImageY, BoardXMillimeters, BoardYMillimeters, CreatedAtUtc)
                VALUES
                    ($profileId, $imageX, $imageY, $boardXMillimeters, $boardYMillimeters, $createdAtUtc);
                """;
            pointCommand.Parameters.AddWithValue("$profileId", profileId);
            pointCommand.Parameters.AddWithValue("$imageX", point.ImageX);
            pointCommand.Parameters.AddWithValue("$imageY", point.ImageY);
            pointCommand.Parameters.AddWithValue("$boardXMillimeters", point.BoardXMillimeters);
            pointCommand.Parameters.AddWithValue("$boardYMillimeters", point.BoardYMillimeters);
            pointCommand.Parameters.AddWithValue("$createdAtUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            pointCommand.ExecuteNonQuery();
        }

        transaction.Commit();
        RecordAuditEvent(
            "CALIBRATION_SAVE",
            $"2D calibration profile saved: {profileName}; board={boardModel}; view={viewType}; points={points.Count}.",
            operatorWithRole: operatorId,
            relatedEntityType: "CalibrationProfile",
            relatedEntityId: profileId.ToString(CultureInfo.InvariantCulture),
            relatedPath: sampleImagePath);
        return profileId;
    }

    public static IReadOnlyList<CalibrationProfileRecord> GetCalibrationProfiles()
    {
        EnsureInitialized();

        var profiles = new List<CalibrationProfileRecord>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, ProfileName, BoardModel, ViewType, SampleImagePath, OperatorId, PointCount,
                   ScaleX, OffsetX, ScaleY, OffsetY, TransformSummary, CreatedAtUtc
            FROM CalibrationProfiles
            ORDER BY datetime(CreatedAtUtc) DESC, Id DESC;
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var profileId = reader.GetInt64(0);
            profiles.Add(ReadCalibrationProfile(reader, GetCalibrationPoints(profileId)));
        }

        return profiles;
    }

    public static CalibrationProfileRecord? GetCalibrationProfile(long profileId)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, ProfileName, BoardModel, ViewType, SampleImagePath, OperatorId, PointCount,
                   ScaleX, OffsetX, ScaleY, OffsetY, TransformSummary, CreatedAtUtc
            FROM CalibrationProfiles
            WHERE Id = $profileId;
            """;
        command.Parameters.AddWithValue("$profileId", profileId);

        using var reader = command.ExecuteReader();
        return reader.Read()
            ? ReadCalibrationProfile(reader, GetCalibrationPoints(profileId))
            : null;
    }

    public static IReadOnlyList<DbHealthRow> GetDatabaseHealthRows()
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        return new[]
        {
            CountTable(connection, "Images", "OK"),
            CountTable(connection, "InspectionResults", "OK"),
            CountTable(connection, "Defects", "OK"),
            CountTable(connection, "ReviewEvents", "OK"),
            CountTable(connection, "AuditEvents", "OK"),
            CountTable(connection, "RecipeRevisions", "OK"),
            CountTable(connection, "CalibrationProfiles", "OK"),
            CountTable(connection, "CalibrationPoints", "OK"),
            CountTable(connection, "BatchTestRuns", "OK"),
            CountTable(connection, "ValidationBreakdownMetrics", "OK"),
            CountTable(connection, "FalseCallReductionRuns", "OK"),
            CountTable(connection, "ThresholdProfiles", "OK"),
            CountTable(connection, "ModelRegistry", "OK"),
            CountTable(connection, "ExportHistory", "OK"),
            CountTable(connection, "ValidationPackages", "OK"),
            CountTable(connection, "SoakTestRuns", "OK"),
            CountTable(connection, "SoakTestIterations", "OK"),
            CountTable(connection, "MesUploadAttempts", "OK"),
            CountTable(connection, "LogArchive", "OK"),
        };
    }

    public static void RecordWorkflowEvent(string category, string message, DateTime timestamp, string? operatorId = null)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO ReviewEvents (Category, Message, OperatorId, EventTimeUtc)
            VALUES ($category, $message, $operatorId, $eventTimeUtc);
            """;
        command.Parameters.AddWithValue("$category", category);
        command.Parameters.AddWithValue("$message", message);
        command.Parameters.AddWithValue("$operatorId", string.IsNullOrWhiteSpace(operatorId) ? DBNull.Value : operatorId);
        command.Parameters.AddWithValue("$eventTimeUtc", timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    public static long RecordAuditEvent(
        string actionCategory,
        string actionDetail,
        DateTime? timestampLocal = null,
        string? operatorWithRole = null,
        string? userId = null,
        string? userRole = null,
        string? stationId = null,
        string relatedEntityType = "",
        string relatedEntityId = "",
        string relatedPath = "")
    {
        EnsureInitialized();

        var localTimestamp = timestampLocal ?? DateTime.Now;
        var timestampUtc = localTimestamp.ToUniversalTime();
        var (parsedUserId, parsedRole) = SplitOperatorWithRole(operatorWithRole);
        var effectiveUserId = NullIfWhiteSpace(userId) ?? parsedUserId ?? AuditUserIdProvider?.Invoke() ?? "UNKNOWN";
        var effectiveRole = NullIfWhiteSpace(userRole) ?? parsedRole ?? AuditUserRoleProvider?.Invoke() ?? ExtractRole(AuditOperatorProvider?.Invoke()) ?? "UNKNOWN";
        var effectiveStation = NullIfWhiteSpace(stationId) ?? AuditStationProvider?.Invoke() ?? "UNKNOWN";

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO AuditEvents
                (TimestampUtc, LocalTimestamp, UserId, UserRole, StationId,
                 ActionCategory, ActionDetail, RelatedEntityType, RelatedEntityId, RelatedPath)
            VALUES
                ($timestampUtc, $localTimestamp, $userId, $userRole, $stationId,
                 $actionCategory, $actionDetail, $relatedEntityType, $relatedEntityId, $relatedPath);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$timestampUtc", timestampUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$localTimestamp", localTimestamp.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$userId", effectiveUserId);
        command.Parameters.AddWithValue("$userRole", effectiveRole);
        command.Parameters.AddWithValue("$stationId", effectiveStation);
        command.Parameters.AddWithValue("$actionCategory", string.IsNullOrWhiteSpace(actionCategory) ? "UNKNOWN" : actionCategory);
        command.Parameters.AddWithValue("$actionDetail", string.IsNullOrWhiteSpace(actionDetail) ? string.Empty : actionDetail);
        command.Parameters.AddWithValue("$relatedEntityType", relatedEntityType);
        command.Parameters.AddWithValue("$relatedEntityId", relatedEntityId);
        command.Parameters.AddWithValue("$relatedPath", relatedPath);
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    public static void UpsertLocalUserMetadata(string userId, string role, bool isDisabled, DateTime createdAtUtc, string createdBy, DateTime? updatedAtUtc = null)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO LocalUsers (UserId, Role, IsDisabled, CreatedAtUtc, CreatedBy, UpdatedAtUtc)
            VALUES ($userId, $role, $isDisabled, $createdAtUtc, $createdBy, $updatedAtUtc)
            ON CONFLICT(UserId) DO UPDATE SET
                Role = excluded.Role,
                IsDisabled = excluded.IsDisabled,
                CreatedAtUtc = excluded.CreatedAtUtc,
                CreatedBy = excluded.CreatedBy,
                UpdatedAtUtc = excluded.UpdatedAtUtc;
            """;
        command.Parameters.AddWithValue("$userId", userId);
        command.Parameters.AddWithValue("$role", role);
        command.Parameters.AddWithValue("$isDisabled", isDisabled ? 1 : 0);
        command.Parameters.AddWithValue("$createdAtUtc", createdAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$createdBy", createdBy);
        command.Parameters.AddWithValue("$updatedAtUtc", (updatedAtUtc ?? DateTime.UtcNow).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    public static void MarkLocalUserDeleted(string userId, string operatorWithRole)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE LocalUsers
            SET IsDeleted = 1,
                IsDisabled = 1,
                UpdatedAtUtc = $updatedAtUtc
            WHERE UserId = $userId;
            """;
        command.Parameters.AddWithValue("$userId", userId);
        command.Parameters.AddWithValue("$updatedAtUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
        RecordAuditEvent("LOCAL_USER_DELETE", $"Local user deleted: {userId}.", operatorWithRole: operatorWithRole, relatedEntityType: "LocalUser", relatedEntityId: userId);
    }

    public static long RecordLocalUserSession(string userId, string role, string authenticationMode, bool success, string message)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO LocalUserSessions
                (UserId, UserRole, AuthenticationMode, LoginAtUtc, Success, Message)
            VALUES
                ($userId, $userRole, $authenticationMode, $loginAtUtc, $success, $message);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$userId", userId);
        command.Parameters.AddWithValue("$userRole", role);
        command.Parameters.AddWithValue("$authenticationMode", authenticationMode);
        command.Parameters.AddWithValue("$loginAtUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$success", success ? 1 : 0);
        command.Parameters.AddWithValue("$message", message);
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    public static void RecordTrainingSample(string sourcePath, string label, string notes)
    {
        EnsureInitialized();

        var targetPath = sourcePath;
        if (File.Exists(sourcePath) && !IsUnderDirectory(sourcePath, TrainingVaultPath))
        {
            var targetName = MakeVaultFileName(DateTime.UtcNow, "training", Path.GetFileName(sourcePath));
            targetPath = Path.Combine(TrainingVaultPath, targetName);
            File.Copy(sourcePath, targetPath, overwrite: true);
        }

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO TrainingSamples (SourceImagePath, VaultPath, Label, Notes, CreatedAtUtc)
            VALUES ($sourceImagePath, $vaultPath, $label, $notes, $createdAtUtc);
            """;
        command.Parameters.AddWithValue("$sourceImagePath", sourcePath);
        command.Parameters.AddWithValue("$vaultPath", targetPath);
        command.Parameters.AddWithValue("$label", label);
        command.Parameters.AddWithValue("$notes", notes);
        command.Parameters.AddWithValue("$createdAtUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    public static long RecordValidationPackage(
        string packageId,
        string packagePath,
        string manifestPath,
        string acceptanceStatus,
        string summary,
        long? runId = null,
        string? operatorId = null)
    {
        EnsureInitialized();

        var effectiveOperator = string.IsNullOrWhiteSpace(operatorId) ? AuditOperatorProvider?.Invoke() ?? "UNKNOWN" : operatorId;
        var auditEventId = RecordAuditEvent(
            "EXPORT",
            $"Stage 1 validation package recorded: {packageId}; status={acceptanceStatus}; manifest={manifestPath}.",
            operatorWithRole: effectiveOperator,
            relatedEntityType: "ValidationPackage",
            relatedEntityId: packageId,
            relatedPath: manifestPath);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO ValidationPackages
                (PackageId, PackagePath, ManifestPath, AcceptanceStatus, Summary, RunId, OperatorId, AuditEventId, CreatedAtUtc)
            VALUES
                ($packageId, $packagePath, $manifestPath, $acceptanceStatus, $summary, $runId, $operatorId, $auditEventId, $createdAtUtc);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$packageId", packageId);
        command.Parameters.AddWithValue("$packagePath", packagePath);
        command.Parameters.AddWithValue("$manifestPath", manifestPath);
        command.Parameters.AddWithValue("$acceptanceStatus", acceptanceStatus);
        command.Parameters.AddWithValue("$summary", summary);
        command.Parameters.AddWithValue("$runId", runId is { } id ? (object)id : DBNull.Value);
        command.Parameters.AddWithValue("$operatorId", effectiveOperator);
        command.Parameters.AddWithValue("$auditEventId", auditEventId);
        command.Parameters.AddWithValue("$createdAtUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    public static void UpsertModelRegistryRecord(ModelRegistryRecord record)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO ModelRegistry
                (ModelId, DisplayName, Version, CreatedAtUtc, RegisteredAtUtc, SourceFileName,
                 StoredModelPath, StoredLabelMapPath, MetadataPath, Sha256, InputTensorName, OutputTensorName,
                 InputWidth, InputHeight, ConfidenceThreshold, LabelsJson, ValidationStatus, LastValidatedAtUtc,
                 ValidationMessage, Notes, IsActive, AuditEventId, LifecycleState, LatestAcceptanceStatus,
                 LatestAcceptanceRunId, LatestReleasePackageId, LatestReleasePackagePath, DeploymentWaiverReason,
                 WaiverExpiresAtUtc, DeploymentWaivedBy, DeploymentWaivedAtUtc, DeployedAtUtc, RetiredReason, RetiredAtUtc)
            VALUES
                ($modelId, $displayName, $version, $createdAtUtc, $registeredAtUtc, $sourceFileName,
                 $storedModelPath, $storedLabelMapPath, $metadataPath, $sha256, $inputTensorName, $outputTensorName,
                 $inputWidth, $inputHeight, $confidenceThreshold, $labelsJson, $validationStatus, $lastValidatedAtUtc,
                 $validationMessage, $notes, $isActive, $auditEventId, $lifecycleState, $latestAcceptanceStatus,
                 $latestAcceptanceRunId, $latestReleasePackageId, $latestReleasePackagePath, $deploymentWaiverReason,
                 $waiverExpiresAtUtc, $deploymentWaivedBy, $deploymentWaivedAtUtc, $deployedAtUtc, $retiredReason, $retiredAtUtc)
            ON CONFLICT(ModelId) DO UPDATE SET
                DisplayName = excluded.DisplayName,
                Version = excluded.Version,
                CreatedAtUtc = excluded.CreatedAtUtc,
                RegisteredAtUtc = excluded.RegisteredAtUtc,
                SourceFileName = excluded.SourceFileName,
                StoredModelPath = excluded.StoredModelPath,
                StoredLabelMapPath = excluded.StoredLabelMapPath,
                MetadataPath = excluded.MetadataPath,
                Sha256 = excluded.Sha256,
                InputTensorName = excluded.InputTensorName,
                OutputTensorName = excluded.OutputTensorName,
                InputWidth = excluded.InputWidth,
                InputHeight = excluded.InputHeight,
                ConfidenceThreshold = excluded.ConfidenceThreshold,
                LabelsJson = excluded.LabelsJson,
                ValidationStatus = excluded.ValidationStatus,
                LastValidatedAtUtc = excluded.LastValidatedAtUtc,
                ValidationMessage = excluded.ValidationMessage,
                Notes = excluded.Notes,
                IsActive = excluded.IsActive,
                AuditEventId = excluded.AuditEventId,
                LifecycleState = excluded.LifecycleState,
                LatestAcceptanceStatus = excluded.LatestAcceptanceStatus,
                LatestAcceptanceRunId = excluded.LatestAcceptanceRunId,
                LatestReleasePackageId = excluded.LatestReleasePackageId,
                LatestReleasePackagePath = excluded.LatestReleasePackagePath,
                DeploymentWaiverReason = excluded.DeploymentWaiverReason,
                WaiverExpiresAtUtc = excluded.WaiverExpiresAtUtc,
                DeploymentWaivedBy = excluded.DeploymentWaivedBy,
                DeploymentWaivedAtUtc = excluded.DeploymentWaivedAtUtc,
                DeployedAtUtc = excluded.DeployedAtUtc,
                RetiredReason = excluded.RetiredReason,
                RetiredAtUtc = excluded.RetiredAtUtc;
            """;
        BindModelRegistryRecord(command, record);
        command.ExecuteNonQuery();
    }

    public static void SetActiveModelRegistryRecord(string modelId)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "UPDATE ModelRegistry SET IsActive = 0;";
            clear.ExecuteNonQuery();
        }

        using (var set = connection.CreateCommand())
        {
            set.Transaction = transaction;
            set.CommandText = "UPDATE ModelRegistry SET IsActive = 1 WHERE ModelId = $modelId;";
            set.Parameters.AddWithValue("$modelId", modelId);
            set.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public static void UpdateModelRegistryValidation(
        string modelId,
        ModelConfigurationTestStatus status,
        DateTime timestampUtc,
        string message)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE ModelRegistry
            SET ValidationStatus = $validationStatus,
                LastValidatedAtUtc = $lastValidatedAtUtc,
                ValidationMessage = $validationMessage,
                LifecycleState = CASE WHEN $validationStatus = 'Ready' THEN 'RuntimeValidated' ELSE LifecycleState END
            WHERE ModelId = $modelId;
            """;
        command.Parameters.AddWithValue("$validationStatus", status.ToString());
        command.Parameters.AddWithValue("$lastValidatedAtUtc", timestampUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$validationMessage", message);
        command.Parameters.AddWithValue("$modelId", modelId);
        command.ExecuteNonQuery();
    }

    public static void UpdateModelLifecycle(
        string modelId,
        ModelLifecycleState lifecycleState,
        string latestAcceptanceStatus = "",
        long? latestAcceptanceRunId = null,
        long? latestReleasePackageId = null,
        string latestReleasePackagePath = "",
        string deploymentWaiverReason = "",
        DateTime? waiverExpiresAtUtc = null,
        string deploymentWaivedBy = "",
        DateTime? deploymentWaivedAtUtc = null,
        DateTime? deployedAtUtc = null,
        string retiredReason = "",
        DateTime? retiredAtUtc = null,
        bool? isActive = null,
        bool replaceDeploymentWaiver = false)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE ModelRegistry
            SET LifecycleState = $lifecycleState,
                LatestAcceptanceStatus = CASE WHEN $latestAcceptanceStatus = '' THEN LatestAcceptanceStatus ELSE $latestAcceptanceStatus END,
                LatestAcceptanceRunId = CASE WHEN $latestAcceptanceRunId IS NULL THEN LatestAcceptanceRunId ELSE $latestAcceptanceRunId END,
                LatestReleasePackageId = CASE WHEN $latestReleasePackageId IS NULL THEN LatestReleasePackageId ELSE $latestReleasePackageId END,
                LatestReleasePackagePath = CASE WHEN $latestReleasePackagePath = '' THEN LatestReleasePackagePath ELSE $latestReleasePackagePath END,
                DeploymentWaiverReason = CASE WHEN $replaceDeploymentWaiver = 1 THEN $deploymentWaiverReason WHEN $deploymentWaiverReason = '' THEN DeploymentWaiverReason ELSE $deploymentWaiverReason END,
                WaiverExpiresAtUtc = CASE WHEN $replaceDeploymentWaiver = 1 THEN $waiverExpiresAtUtc WHEN $waiverExpiresAtUtc IS NULL THEN WaiverExpiresAtUtc ELSE $waiverExpiresAtUtc END,
                DeploymentWaivedBy = CASE WHEN $replaceDeploymentWaiver = 1 THEN $deploymentWaivedBy WHEN $deploymentWaivedBy = '' THEN DeploymentWaivedBy ELSE $deploymentWaivedBy END,
                DeploymentWaivedAtUtc = CASE WHEN $replaceDeploymentWaiver = 1 THEN $deploymentWaivedAtUtc WHEN $deploymentWaivedAtUtc IS NULL THEN DeploymentWaivedAtUtc ELSE $deploymentWaivedAtUtc END,
                DeployedAtUtc = CASE WHEN $deployedAtUtc IS NULL THEN DeployedAtUtc ELSE $deployedAtUtc END,
                RetiredReason = CASE WHEN $retiredReason = '' THEN RetiredReason ELSE $retiredReason END,
                RetiredAtUtc = CASE WHEN $retiredAtUtc IS NULL THEN RetiredAtUtc ELSE $retiredAtUtc END,
                IsActive = CASE WHEN $isActive IS NULL THEN IsActive ELSE $isActive END
            WHERE ModelId = $modelId;
            """;
        command.Parameters.AddWithValue("$modelId", modelId);
        command.Parameters.AddWithValue("$lifecycleState", lifecycleState.ToString());
        command.Parameters.AddWithValue("$latestAcceptanceStatus", latestAcceptanceStatus ?? string.Empty);
        command.Parameters.AddWithValue("$latestAcceptanceRunId", latestAcceptanceRunId is { } acceptanceId ? (object)acceptanceId : DBNull.Value);
        command.Parameters.AddWithValue("$latestReleasePackageId", latestReleasePackageId is { } releaseId ? (object)releaseId : DBNull.Value);
        command.Parameters.AddWithValue("$latestReleasePackagePath", latestReleasePackagePath ?? string.Empty);
        command.Parameters.AddWithValue("$deploymentWaiverReason", deploymentWaiverReason ?? string.Empty);
        command.Parameters.AddWithValue("$waiverExpiresAtUtc", waiverExpiresAtUtc is { } expiresAt ? (object)expiresAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) : DBNull.Value);
        command.Parameters.AddWithValue("$deploymentWaivedBy", deploymentWaivedBy ?? string.Empty);
        command.Parameters.AddWithValue("$deploymentWaivedAtUtc", deploymentWaivedAtUtc is { } waivedAt ? (object)waivedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) : DBNull.Value);
        command.Parameters.AddWithValue("$deployedAtUtc", deployedAtUtc is { } deployedAt ? (object)deployedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) : DBNull.Value);
        command.Parameters.AddWithValue("$retiredReason", retiredReason ?? string.Empty);
        command.Parameters.AddWithValue("$retiredAtUtc", retiredAtUtc is { } retiredAt ? (object)retiredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) : DBNull.Value);
        command.Parameters.AddWithValue("$isActive", isActive is { } active ? (object)(active ? 1 : 0) : DBNull.Value);
        command.Parameters.AddWithValue("$replaceDeploymentWaiver", replaceDeploymentWaiver ? 1 : 0);
        command.ExecuteNonQuery();
    }

    public static long RecordModelAcceptanceRun(ModelAcceptanceRun run)
    {
        EnsureInitialized();

        var effectiveOperator = string.IsNullOrWhiteSpace(run.OperatorId) ? AuditOperatorProvider?.Invoke() ?? "UNKNOWN" : run.OperatorId;
        var auditEventId = RecordAuditEvent(
            "MODEL_ACCEPTANCE",
            $"Model acceptance run recorded: model={run.ModelId}; version={run.ModelVersion}; status={run.Status}; dataset={run.DatasetName}.",
            operatorWithRole: effectiveOperator,
            relatedEntityType: "ModelAcceptanceRun",
            relatedEntityId: run.ModelId,
            relatedPath: run.DatasetFolder);

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO ModelAcceptanceRuns
                (CreatedAtUtc, ModelId, ModelVersion, ModelSha256, ModelPath, LabelMapPath,
                 InputTensorName, OutputTensorName, OutputShape, DatasetFolder, DatasetName,
                 GroundTruthCsvPath, IsFormalManifest, Status, OperatorId, ApprovedBy, ApprovedAtUtc,
                 IsProductionCandidate, CriteriaJson, MetricsJson, DatasetQualityJson,
                 FalseCallRecommendationJson, BreakdownJson, PerformanceJson, P95InferenceMs,
                 MessagesJson, LimitationsJson, AuditEventId)
            VALUES
                ($createdAtUtc, $modelId, $modelVersion, $modelSha256, $modelPath, $labelMapPath,
                 $inputTensorName, $outputTensorName, $outputShape, $datasetFolder, $datasetName,
                 $groundTruthCsvPath, $isFormalManifest, $status, $operatorId, $approvedBy, $approvedAtUtc,
                 $isProductionCandidate, $criteriaJson, $metricsJson, $datasetQualityJson,
                 $falseCallRecommendationJson, $breakdownJson, $performanceJson, $p95InferenceMs,
                 $messagesJson, $limitationsJson, $auditEventId);
            SELECT last_insert_rowid();
            """;
        BindModelAcceptanceRun(command, run, effectiveOperator, auditEventId);
        var id = (long)(command.ExecuteScalar() ?? 0L);
        RecordModelAcceptanceMetrics(connection, transaction, id, run);
        transaction.Commit();
        return id;
    }

    private static void RecordModelAcceptanceMetrics(SqliteConnection connection, SqliteTransaction transaction, long runId, ModelAcceptanceRun run)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO ModelAcceptanceMetrics
                (RunId, MetricName, MetricValue, MetricText)
            VALUES
                ($runId, $metricName, $metricValue, $metricText);
            """;

        foreach (var metric in ModelAcceptanceMetricRows(run))
        {
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$runId", runId);
            command.Parameters.AddWithValue("$metricName", metric.Name);
            command.Parameters.AddWithValue("$metricValue", metric.Value);
            command.Parameters.AddWithValue("$metricText", metric.Text);
            command.ExecuteNonQuery();
        }
    }

    private static IEnumerable<(string Name, double Value, string Text)> ModelAcceptanceMetricRows(ModelAcceptanceRun run)
    {
        yield return ("accuracy", run.Metrics.Accuracy, run.Metrics.Accuracy.ToString("P3", CultureInfo.InvariantCulture));
        yield return ("precision", run.Metrics.Precision, run.Metrics.Precision.ToString("P3", CultureInfo.InvariantCulture));
        yield return ("recall", run.Metrics.Recall, run.Metrics.Recall.ToString("P3", CultureInfo.InvariantCulture));
        yield return ("false_call_rate", run.Metrics.FalseCallRate, run.Metrics.FalseCallRate.ToString("P3", CultureInfo.InvariantCulture));
        yield return ("possible_escape_rate", run.FalseCallRecommendation.PossibleEscapeRate, run.FalseCallRecommendation.PossibleEscapeRate.ToString("P3", CultureInfo.InvariantCulture));
        var totalImages = run.Metrics.OkCount + run.Metrics.NgCount + run.Metrics.Unknown;
        yield return ("review_rate", totalImages == 0 ? 0 : run.Metrics.ReviewCount / (double)Math.Max(1, totalImages), string.Empty);
        yield return ("average_inference_ms", run.PerformanceSummary.AverageMilliseconds, run.PerformanceSummary.AverageMilliseconds.ToString("F3", CultureInfo.InvariantCulture));
        yield return ("p95_inference_ms", run.P95InferenceMs, run.P95InferenceMs.ToString("F3", CultureInfo.InvariantCulture));
    }

    public static void PromoteModelAcceptanceRun(long id, string approvedBy)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE ModelAcceptanceRuns
            SET IsProductionCandidate = 1,
                ApprovedBy = $approvedBy,
                ApprovedAtUtc = $approvedAtUtc
            WHERE Id = $id
              AND Status = 'PASS';
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$approvedBy", string.IsNullOrWhiteSpace(approvedBy) ? "UNKNOWN" : approvedBy);
        command.Parameters.AddWithValue("$approvedAtUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    public static long RecordModelReleasePackage(ModelReleasePackageRecord record)
    {
        EnsureInitialized();

        var auditEventId = RecordAuditEvent(
            "MODEL_RELEASE_PACKAGE",
            $"Model release package recorded: model={record.ModelId}; version={record.ModelVersion}; status={record.Status}.",
            operatorWithRole: record.ApprovedBy,
            relatedEntityType: "ModelReleasePackage",
            relatedEntityId: record.ModelId,
            relatedPath: record.PackagePath);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO ModelReleasePackages
                (CreatedAtUtc, AcceptanceRunId, ModelId, ModelVersion, ModelSha256,
                 PackagePath, ManifestPath, ReportPath, Status, ApprovedBy, AuditEventId)
            VALUES
                ($createdAtUtc, $acceptanceRunId, $modelId, $modelVersion, $modelSha256,
                 $packagePath, $manifestPath, $reportPath, $status, $approvedBy, $auditEventId);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$createdAtUtc", record.CreatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$acceptanceRunId", record.AcceptanceRunId);
        command.Parameters.AddWithValue("$modelId", record.ModelId);
        command.Parameters.AddWithValue("$modelVersion", record.ModelVersion);
        command.Parameters.AddWithValue("$modelSha256", record.ModelSha256);
        command.Parameters.AddWithValue("$packagePath", record.PackagePath);
        command.Parameters.AddWithValue("$manifestPath", record.ManifestPath);
        command.Parameters.AddWithValue("$reportPath", record.ReportPath);
        command.Parameters.AddWithValue("$status", record.Status);
        command.Parameters.AddWithValue("$approvedBy", record.ApprovedBy);
        command.Parameters.AddWithValue("$auditEventId", auditEventId);
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    public static ModelReleasePackageRecord? GetLatestModelReleasePackage(string? modelId = null)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        var filter = string.IsNullOrWhiteSpace(modelId) ? string.Empty : "WHERE ModelId = $modelId";
        command.CommandText =
            $"""
            SELECT Id, CreatedAtUtc, AcceptanceRunId, ModelId, ModelVersion, ModelSha256,
                   PackagePath, ManifestPath, ReportPath, Status, ApprovedBy, AuditEventId
            FROM ModelReleasePackages
            {filter}
            ORDER BY datetime(CreatedAtUtc) DESC, Id DESC
            LIMIT 1;
            """;
        if (!string.IsNullOrWhiteSpace(modelId))
            command.Parameters.AddWithValue("$modelId", modelId);

        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new ModelReleasePackageRecord(
                reader.GetInt64(0),
                ParseDateTime(reader.GetString(1)),
                reader.GetInt64(2),
                reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
                reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
                reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
                reader.IsDBNull(10) ? string.Empty : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetInt64(11))
            : null;
    }

    public static long RecordExport(string exportType, string filePath, string status = "OK", string? operatorId = null)
    {
        EnsureInitialized();

        var effectiveOperator = string.IsNullOrWhiteSpace(operatorId) ? AuditOperatorProvider?.Invoke() ?? "UNKNOWN" : operatorId;
        var auditEventId = RecordAuditEvent(
            "EXPORT",
            $"Export recorded: {exportType}; status={status}; path={filePath}.",
            operatorWithRole: effectiveOperator,
            relatedEntityType: "ExportHistory",
            relatedPath: filePath);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO ExportHistory (ExportType, FilePath, Status, OperatorId, AuditEventId, CreatedAtUtc)
            VALUES ($exportType, $filePath, $status, $operatorId, $auditEventId, $createdAtUtc);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$exportType", exportType);
        command.Parameters.AddWithValue("$filePath", filePath);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$operatorId", effectiveOperator);
        command.Parameters.AddWithValue("$auditEventId", auditEventId);
        command.Parameters.AddWithValue("$createdAtUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    public static long RecordExportVerification(ExportVerificationResult result, long? exportHistoryId = null)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO ExportVerification
                (ExportHistoryId, CheckedAtUtc, ExportType, ExportPath, Status, Sha256, SizeBytes, MessagesJson, ArtifactChecksumsJson)
            VALUES
                ($exportHistoryId, $checkedAtUtc, $exportType, $exportPath, $status, $sha256, $sizeBytes, $messagesJson, $artifactChecksumsJson);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$exportHistoryId", exportHistoryId is null ? DBNull.Value : exportHistoryId.Value);
        command.Parameters.AddWithValue("$checkedAtUtc", result.CheckedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$exportType", result.ExportType);
        command.Parameters.AddWithValue("$exportPath", result.ExportPath);
        command.Parameters.AddWithValue("$status", result.Status.ToString());
        command.Parameters.AddWithValue("$sha256", result.Sha256);
        command.Parameters.AddWithValue("$sizeBytes", result.SizeBytes);
        command.Parameters.AddWithValue("$messagesJson", JsonSerializer.Serialize(result.Messages));
        command.Parameters.AddWithValue("$artifactChecksumsJson", JsonSerializer.Serialize(result.ArtifactChecksums));
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    public static long RecordBuildTestEvidence(BuildTestEvidenceRecord evidence)
    {
        EnsureInitialized();

        var effectiveOperator = string.IsNullOrWhiteSpace(evidence.OperatorId)
            ? AuditOperatorProvider?.Invoke() ?? "UNKNOWN"
            : evidence.OperatorId.Trim();
        var auditEventId = RecordAuditEvent(
            "BUILD_TEST_EVIDENCE",
            $"Build/test evidence recorded: configuration={evidence.Configuration}; hygiene={evidence.HygieneStatus}; build={evidence.BuildStatus}; test={evidence.TestStatus}; publishValidation={evidence.PublishValidationStatus}.",
            operatorWithRole: effectiveOperator,
            relatedEntityType: "BuildTestEvidence",
            relatedPath: evidence.EvidencePath);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO BuildTestEvidence
                (GeneratedAtUtc, CommitSha, Configuration, HygieneStatus, RestoreStatus,
                 BuildStatus, TestStatus, PublishValidationStatus, EvidencePath, OperatorId,
                 CreatedAtUtc, TestResultPath, MachineName, AuditEventId)
            VALUES
                ($generatedAtUtc, $commitSha, $configuration, $hygieneStatus, $restoreStatus,
                 $buildStatus, $testStatus, $publishValidationStatus, $evidencePath, $operatorId,
                 $createdAtUtc, $testResultPath, $machineName, $auditEventId);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$generatedAtUtc", evidence.GeneratedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$commitSha", evidence.GitCommit ?? string.Empty);
        command.Parameters.AddWithValue("$configuration", evidence.Configuration ?? "Release");
        command.Parameters.AddWithValue("$hygieneStatus", evidence.HygieneStatus ?? "UNKNOWN");
        command.Parameters.AddWithValue("$restoreStatus", evidence.RestoreStatus ?? "UNKNOWN");
        command.Parameters.AddWithValue("$buildStatus", evidence.BuildStatus ?? "UNKNOWN");
        command.Parameters.AddWithValue("$testStatus", evidence.TestStatus ?? "UNKNOWN");
        command.Parameters.AddWithValue("$publishValidationStatus", evidence.PublishValidationStatus ?? "UNKNOWN");
        command.Parameters.AddWithValue("$evidencePath", evidence.EvidencePath ?? string.Empty);
        command.Parameters.AddWithValue("$operatorId", effectiveOperator);
        command.Parameters.AddWithValue("$createdAtUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$testResultPath", evidence.TestResultPath ?? string.Empty);
        command.Parameters.AddWithValue("$machineName", evidence.MachineName ?? string.Empty);
        command.Parameters.AddWithValue("$auditEventId", auditEventId);
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    public static void RecordMesUploadAttempt(
        string mode,
        string endpointUrl,
        string payloadPath,
        string status,
        string message,
        string operatorId,
        string lotId,
        string boardModel,
        string result)
    {
        EnsureInitialized();

        var effectiveOperator = string.IsNullOrWhiteSpace(operatorId) ? AuditOperatorProvider?.Invoke() ?? "UNKNOWN" : operatorId;
        RecordAuditEvent(
            "MES_MOCK_UPLOAD",
            $"Mock MES upload attempt: mode={mode}; status={status}; result={result}; message={message}.",
            operatorWithRole: effectiveOperator,
            relatedEntityType: "MesUploadAttempt",
            relatedPath: payloadPath);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO MesUploadAttempts
                (Mode, EndpointUrl, PayloadPath, Status, Message, OperatorId, LotId, BoardModel, Result, CreatedAtUtc)
            VALUES
                ($mode, $endpointUrl, $payloadPath, $status, $message, $operatorId, $lotId, $boardModel, $result, $createdAtUtc);
            """;
        command.Parameters.AddWithValue("$mode", mode);
        command.Parameters.AddWithValue("$endpointUrl", endpointUrl);
        command.Parameters.AddWithValue("$payloadPath", payloadPath);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$message", message);
        command.Parameters.AddWithValue("$operatorId", effectiveOperator);
        command.Parameters.AddWithValue("$lotId", lotId);
        command.Parameters.AddWithValue("$boardModel", boardModel);
        command.Parameters.AddWithValue("$result", result);
        command.Parameters.AddWithValue("$createdAtUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    public static IReadOnlyList<MesUploadAttemptRecord> GetMesUploadAttempts(int limit = 100)
    {
        EnsureInitialized();

        var records = new List<MesUploadAttemptRecord>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, CreatedAtUtc, Mode, EndpointUrl, PayloadPath, Status, Message, OperatorId, LotId, BoardModel, Result
            FROM MesUploadAttempts
            ORDER BY datetime(CreatedAtUtc) DESC, Id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        using var reader = command.ExecuteReader();
        while (reader.Read())
            records.Add(ReadMesUploadAttempt(reader));

        return records;
    }

    public static long EnqueueMesSpoolItem(
        string payloadType,
        string payloadJson,
        string payloadPath,
        string endpointUrl,
        int maxRetryCount,
        string lastError,
        string operatorId,
        string lotId,
        string boardModel,
        string result)
    {
        EnsureInitialized();

        var now = DateTime.UtcNow;
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO MesSpoolQueue
                (CreatedAtUtc, LastAttemptAtUtc, NextAttemptAtUtc, PayloadType, PayloadJson, PayloadPath, EndpointUrl, RetryCount, MaxRetryCount, Status, LastError, OperatorId, LotId, BoardModel, Result)
            VALUES
                ($createdAtUtc, $lastAttemptAtUtc, $nextAttemptAtUtc, $payloadType, $payloadJson, $payloadPath, $endpointUrl, 0, $maxRetryCount, 'Pending', $lastError, $operatorId, $lotId, $boardModel, $result);
            """;
        command.Parameters.AddWithValue("$createdAtUtc", now.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$lastAttemptAtUtc", now.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$nextAttemptAtUtc", now.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$payloadType", payloadType);
        command.Parameters.AddWithValue("$payloadJson", payloadJson);
        command.Parameters.AddWithValue("$payloadPath", payloadPath);
        command.Parameters.AddWithValue("$endpointUrl", endpointUrl);
        command.Parameters.AddWithValue("$maxRetryCount", maxRetryCount);
        var safeLastError = MesIntegrationSettingsService.RedactSecrets(lastError);
        command.Parameters.AddWithValue("$lastError", safeLastError);
        command.Parameters.AddWithValue("$operatorId", string.IsNullOrWhiteSpace(operatorId) ? "UNKNOWN" : operatorId);
        command.Parameters.AddWithValue("$lotId", lotId);
        command.Parameters.AddWithValue("$boardModel", boardModel);
        command.Parameters.AddWithValue("$result", result);

        command.ExecuteNonQuery();
        command.Parameters.Clear();
        command.CommandText = "SELECT last_insert_rowid();";
        var id = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        RecordAuditEvent(
            "MES_SPOOL",
            $"MES REST payload spooled: id={id}; type={payloadType}; result={result}; message={safeLastError}.",
            operatorWithRole: operatorId,
            relatedEntityType: "MesSpoolQueue",
            relatedEntityId: id.ToString(CultureInfo.InvariantCulture),
            relatedPath: payloadPath);
        return id;
    }

    public static IReadOnlyList<MesSpoolQueueRecord> GetPendingMesSpoolItems(int limit = 100)
    {
        EnsureInitialized();

        var records = new List<MesSpoolQueueRecord>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, CreatedAtUtc, LastAttemptAtUtc, NextAttemptAtUtc, PayloadType, PayloadJson, PayloadPath, EndpointUrl,
                   RetryCount, MaxRetryCount, Status, LastError, OperatorId, LotId, BoardModel, Result
            FROM MesSpoolQueue
            WHERE Status = 'Pending' AND (NextAttemptAtUtc IS NULL OR datetime(NextAttemptAtUtc) <= datetime($now))
            ORDER BY datetime(CreatedAtUtc), Id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$limit", limit);

        using var reader = command.ExecuteReader();
        while (reader.Read())
            records.Add(ReadMesSpoolQueueRecord(reader));

        return records;
    }

    public static IReadOnlyList<MesSpoolQueueRecord> GetMesSpoolQueue(int limit = 100)
    {
        EnsureInitialized();

        var records = new List<MesSpoolQueueRecord>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, CreatedAtUtc, LastAttemptAtUtc, NextAttemptAtUtc, PayloadType, PayloadJson, PayloadPath, EndpointUrl,
                   RetryCount, MaxRetryCount, Status, LastError, OperatorId, LotId, BoardModel, Result
            FROM MesSpoolQueue
            ORDER BY datetime(CreatedAtUtc) DESC, Id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        using var reader = command.ExecuteReader();
        while (reader.Read())
            records.Add(ReadMesSpoolQueueRecord(reader));

        return records;
    }

    public static void MarkMesSpoolItemSent(long id, string message)
    {
        EnsureInitialized();
        var safeMessage = MesIntegrationSettingsService.RedactSecrets(message);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE MesSpoolQueue
            SET Status = 'Sent',
                LastAttemptAtUtc = $lastAttemptAtUtc,
                NextAttemptAtUtc = NULL,
                LastError = $message
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$lastAttemptAtUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$message", safeMessage);
        command.ExecuteNonQuery();

        RecordAuditEvent(
            "MES_SPOOL",
            $"MES spool item {id} marked Sent. {safeMessage}",
            relatedEntityType: "MesSpoolQueue",
            relatedEntityId: id.ToString(CultureInfo.InvariantCulture));
    }

    public static void DeleteMesSpoolItem(long id, string message)
        => MarkMesSpoolItemSent(id, message);

    public static void RecordMesSpoolRetryFailure(long id, string message, int retryBackoffMs)
    {
        EnsureInitialized();
        var safeMessage = MesIntegrationSettingsService.RedactSecrets(message);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE MesSpoolQueue
            SET RetryCount = RetryCount + 1,
                LastAttemptAtUtc = $lastAttemptAtUtc,
                NextAttemptAtUtc = $nextAttemptAtUtc,
                Status = CASE WHEN RetryCount + 1 >= MaxRetryCount THEN 'Failed' ELSE 'Pending' END,
                LastError = $lastError
            WHERE Id = $id;
            """;
        var now = DateTime.UtcNow;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$lastAttemptAtUtc", now.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$nextAttemptAtUtc", now.AddMilliseconds(Math.Max(0, retryBackoffMs)).ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$lastError", safeMessage);
        command.ExecuteNonQuery();

        RecordAuditEvent(
            "MES_SPOOL",
            $"MES spool item {id} retry failed: {safeMessage}",
            relatedEntityType: "MesSpoolQueue",
            relatedEntityId: id.ToString(CultureInfo.InvariantCulture));
    }

    public static void MarkMesSpoolItemAbandoned(long id, string message, string operatorId)
    {
        EnsureInitialized();
        var safeMessage = MesIntegrationSettingsService.RedactSecrets(message);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE MesSpoolQueue
            SET Status = 'Abandoned',
                LastAttemptAtUtc = $lastAttemptAtUtc,
                NextAttemptAtUtc = NULL,
                LastError = $message
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$lastAttemptAtUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$message", safeMessage);
        command.ExecuteNonQuery();

        RecordAuditEvent(
            "MES_SPOOL_ABANDON",
            $"MES spool item {id} marked Abandoned. {safeMessage}",
            operatorWithRole: operatorId,
            relatedEntityType: "MesSpoolQueue",
            relatedEntityId: id.ToString(CultureInfo.InvariantCulture));
    }

    public static long EnqueueCentralSyncItem(
        string itemType,
        string itemId,
        string payloadJson,
        string payloadPath,
        string endpointOrFolder,
        string stationId,
        int maxRetryCount,
        string status = "Pending",
        string lastError = "")
    {
        EnsureInitialized();

        var now = DateTime.UtcNow;
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO CentralSyncQueue
                (CreatedAtUtc, LastAttemptAtUtc, NextAttemptAtUtc, ItemType, ItemId, PayloadJson, PayloadPath,
                 EndpointOrFolder, StationId, RetryCount, MaxRetryCount, Status, LastError)
            VALUES
                ($createdAtUtc, NULL, $nextAttemptAtUtc, $itemType, $itemId, $payloadJson, $payloadPath,
                 $endpointOrFolder, $stationId, 0, $maxRetryCount, $status, $lastError);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$createdAtUtc", now.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$nextAttemptAtUtc", now.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$itemType", itemType);
        command.Parameters.AddWithValue("$itemId", itemId);
        command.Parameters.AddWithValue("$payloadJson", payloadJson);
        command.Parameters.AddWithValue("$payloadPath", payloadPath);
        command.Parameters.AddWithValue("$endpointOrFolder", endpointOrFolder);
        command.Parameters.AddWithValue("$stationId", string.IsNullOrWhiteSpace(stationId) ? Environment.MachineName : stationId);
        command.Parameters.AddWithValue("$maxRetryCount", Math.Max(0, maxRetryCount));
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$lastError", lastError);

        var id = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        RecordAuditEvent(
            "CENTRAL_SYNC_QUEUE",
            $"Central sync payload queued: id={id}; type={itemType}; item={itemId}; status={status}.",
            relatedEntityType: "CentralSyncQueue",
            relatedEntityId: id.ToString(CultureInfo.InvariantCulture),
            relatedPath: payloadPath);
        return id;
    }

    public static bool CentralSyncQueueContains(string itemType, string itemId)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT 1
            FROM CentralSyncQueue
            WHERE ItemType = $itemType AND ItemId = $itemId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$itemType", itemType);
        command.Parameters.AddWithValue("$itemId", itemId);
        return command.ExecuteScalar() is not null;
    }

    public static IReadOnlyList<CentralSyncQueueRecord> GetPendingCentralSyncItems(int limit = 100)
    {
        EnsureInitialized();

        var records = new List<CentralSyncQueueRecord>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, CreatedAtUtc, LastAttemptAtUtc, NextAttemptAtUtc, ItemType, ItemId, PayloadJson,
                   PayloadPath, EndpointOrFolder, StationId, RetryCount, MaxRetryCount, Status, LastError
            FROM CentralSyncQueue
            WHERE Status = 'Pending' AND (NextAttemptAtUtc IS NULL OR datetime(NextAttemptAtUtc) <= datetime($now))
            ORDER BY datetime(CreatedAtUtc), Id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$limit", limit);

        using var reader = command.ExecuteReader();
        while (reader.Read())
            records.Add(ReadCentralSyncQueueRecord(reader));

        return records;
    }

    public static IReadOnlyList<CentralSyncQueueRecord> GetCentralSyncQueue(int limit = 500)
    {
        EnsureInitialized();

        var records = new List<CentralSyncQueueRecord>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, CreatedAtUtc, LastAttemptAtUtc, NextAttemptAtUtc, ItemType, ItemId, PayloadJson,
                   PayloadPath, EndpointOrFolder, StationId, RetryCount, MaxRetryCount, Status, LastError
            FROM CentralSyncQueue
            ORDER BY datetime(CreatedAtUtc) DESC, Id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        using var reader = command.ExecuteReader();
        while (reader.Read())
            records.Add(ReadCentralSyncQueueRecord(reader));

        return records;
    }

    public static void MarkCentralSyncItemSent(long id, string payloadPath, string message)
        => UpdateCentralSyncItem(id, "Sent", payloadPath, message, retryBackoffMs: 0, incrementRetry: false);

    public static void MarkCentralSyncItemSkipped(long id, string message)
        => UpdateCentralSyncItem(id, "Skipped", payloadPath: string.Empty, message, retryBackoffMs: 0, incrementRetry: false);

    public static void RecordCentralSyncRetryFailure(long id, string message, int retryBackoffMs)
        => UpdateCentralSyncItem(id, "Pending", payloadPath: string.Empty, message, retryBackoffMs, incrementRetry: true);

    public static void RecordCentralSyncAttempt(
        long queueId,
        string mode,
        string endpointOrFolder,
        string status,
        string message)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO CentralSyncAttempts
                (QueueId, AttemptedAtUtc, Mode, EndpointOrFolder, Status, Message)
            VALUES
                ($queueId, $attemptedAtUtc, $mode, $endpointOrFolder, $status, $message);
            """;
        command.Parameters.AddWithValue("$queueId", queueId);
        command.Parameters.AddWithValue("$attemptedAtUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$mode", mode);
        command.Parameters.AddWithValue("$endpointOrFolder", endpointOrFolder);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$message", message);
        command.ExecuteNonQuery();
    }

    private static void UpdateCentralSyncItem(
        long id,
        string status,
        string payloadPath,
        string message,
        int retryBackoffMs,
        bool incrementRetry)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            UPDATE CentralSyncQueue
            SET Status = $status,
                LastAttemptAtUtc = $lastAttemptAtUtc,
                NextAttemptAtUtc = {(status == "Pending" ? "$nextAttemptAtUtc" : "NULL")},
                RetryCount = RetryCount + {(incrementRetry ? "1" : "0")},
                PayloadPath = CASE WHEN $payloadPath <> '' THEN $payloadPath ELSE PayloadPath END,
                LastError = $message
            WHERE Id = $id;
            """;
        var now = DateTime.UtcNow;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$lastAttemptAtUtc", now.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$nextAttemptAtUtc", now.AddMilliseconds(Math.Max(0, retryBackoffMs)).ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$payloadPath", payloadPath);
        command.Parameters.AddWithValue("$message", message);
        command.ExecuteNonQuery();

        RecordAuditEvent(
            "CENTRAL_SYNC_QUEUE",
            $"Central sync item {id} status={status}. {message}",
            relatedEntityType: "CentralSyncQueue",
            relatedEntityId: id.ToString(CultureInfo.InvariantCulture),
            relatedPath: payloadPath);
    }

    public static long RecordTraceabilityTestReport(TraceabilityTestReport report)
    {
        EnsureInitialized();

        var auditEventId = RecordAuditEvent(
            "MES_TRACEABILITY_TEST",
            $"Traceability test {report.Status}; mode={report.Mode}; result={report.ResultStatus}; image={report.ImageStatus}; endpoint={MesIntegrationSettingsService.RedactSecrets(report.EndpointUrl)}.",
            operatorWithRole: report.OperatorId,
            relatedEntityType: "TraceabilityTestReport");

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO TraceabilityTestReports
                (CreatedAtUtc, Status, Mode, EndpointUrl, ResultStatus, ImageStatus,
                 PayloadPath, ReportJsonPath, ReportHtmlPath, Message, ProductionModeConfirmed,
                 OperatorId, AuditEventId)
            VALUES
                ($createdAtUtc, $status, $mode, $endpointUrl, $resultStatus, $imageStatus,
                 $payloadPath, $reportJsonPath, $reportHtmlPath, $message, $productionModeConfirmed,
                 $operatorId, $auditEventId);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$createdAtUtc", report.CreatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$status", report.Status);
        command.Parameters.AddWithValue("$mode", report.Mode);
        command.Parameters.AddWithValue("$endpointUrl", MesIntegrationSettingsService.RedactSecrets(report.EndpointUrl));
        command.Parameters.AddWithValue("$resultStatus", report.ResultStatus);
        command.Parameters.AddWithValue("$imageStatus", report.ImageStatus);
        command.Parameters.AddWithValue("$payloadPath", report.PayloadPath);
        command.Parameters.AddWithValue("$reportJsonPath", report.ReportJsonPath);
        command.Parameters.AddWithValue("$reportHtmlPath", report.ReportHtmlPath);
        command.Parameters.AddWithValue("$message", MesIntegrationSettingsService.RedactSecrets(report.Message));
        command.Parameters.AddWithValue("$productionModeConfirmed", report.ProductionModeConfirmed ? 1 : 0);
        command.Parameters.AddWithValue("$operatorId", report.OperatorId);
        command.Parameters.AddWithValue("$auditEventId", auditEventId);
        var id = (long)(command.ExecuteScalar() ?? 0L);
        report.Id = id;
        return id;
    }

    public static TraceabilityTestReport? GetLatestTraceabilityTestReport()
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, CreatedAtUtc, Status, Mode, EndpointUrl, ResultStatus, ImageStatus,
                   PayloadPath, ReportJsonPath, ReportHtmlPath, Message, ProductionModeConfirmed,
                   OperatorId
            FROM TraceabilityTestReports
            ORDER BY datetime(CreatedAtUtc) DESC, Id DESC
            LIMIT 1;
            """;
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new TraceabilityTestReport
            {
                Id = reader.GetInt64(0),
                CreatedAtUtc = ParseDateTime(reader.GetString(1)),
                Status = reader.GetString(2),
                Mode = reader.GetString(3),
                EndpointUrl = reader.GetString(4),
                ResultStatus = reader.GetString(5),
                ImageStatus = reader.GetString(6),
                PayloadPath = reader.GetString(7),
                ReportJsonPath = reader.GetString(8),
                ReportHtmlPath = reader.GetString(9),
                Message = reader.GetString(10),
                ProductionModeConfirmed = reader.GetInt32(11) != 0,
                OperatorId = reader.GetString(12),
            }
            : null;
    }

    public static string RunIntegrityCheck()
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "unknown";
    }

    private static void EnsureInitialized()
    {
        if (!_initialized)
            Initialize();
    }

    private static SqliteConnection OpenConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        };

        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        return connection;
    }

    private static void ExecuteSchemaSql(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = SchemaSql;
        command.ExecuteNonQuery();
    }

    private static bool HasExistingUserSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type IN ('table', 'view')
              AND name NOT LIKE 'sqlite_%';
            """;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    public static bool TableExists(SqliteConnection connection, string tableName)
        => TableExists(connection, null, tableName);

    internal static bool TableExists(SqliteConnection connection, SqliteTransaction? transaction, string tableName)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'table'
              AND name = $tableName;
            """;
        command.Parameters.AddWithValue("$tableName", tableName);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    public static bool ColumnExists(SqliteConnection connection, string tableName, string columnName)
        => ColumnExists(connection, null, tableName, columnName);

    internal static bool ColumnExists(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string tableName,
        string columnName)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info({QuoteIdentifier(tableName)});";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static bool IndexExists(SqliteConnection connection, string indexName)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'index'
              AND name = $indexName;
            """;
        command.Parameters.AddWithValue("$indexName", indexName);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    public static int GetSchemaVersion()
    {
        using var connection = OpenConnection();
        return GetSchemaVersion(connection);
    }

    public static int GetSchemaVersion(SqliteConnection connection)
    {
        if (!TableExists(connection, "SchemaInfo"))
            return 0;

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Value
            FROM SchemaInfo
            WHERE Key = 'SchemaVersion'
            LIMIT 1;
            """;
        var value = Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var version)
            ? version
            : 0;
    }

    public static void SetSchemaVersion(SqliteConnection connection, int version)
        => SetSchemaVersion(connection, null, version);

    internal static void SetSchemaVersion(SqliteConnection connection, SqliteTransaction? transaction, int version)
    {
        EnsureSchemaInfoTable(connection, transaction);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO SchemaInfo (Key, Value)
            VALUES ('SchemaVersion', $version)
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
            """;
        command.Parameters.AddWithValue("$version", version.ToString(CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    internal static void EnsureSchemaInfoTable(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS SchemaInfo
            (
                Key TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    internal static void ExecuteNonQuery(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string commandText)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }

    private static string QuoteIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            throw new ArgumentException("SQLite identifier is required.", nameof(identifier));

        return "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static string ResolveStorageRoot()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
            return Path.Combine(localAppData, AppFolderName);

        return Path.Combine(AppContext.BaseDirectory, "data");
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static ImportedImage? TryGetImageByHash(string hash)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, OriginalPath, VaultPath, FileName, BoardModel, LotId, ViewType, ImportedAtUtc, FileHash
            FROM Images
            WHERE FileHash = $fileHash
            ORDER BY Id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$fileHash", hash);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadImportedImage(reader) : null;
    }

    private static ImportedImage ReadImportedImage(SqliteDataReader reader)
    {
        var importedAtText = reader.GetString(7);
        var importedAt = DateTime.TryParse(
            importedAtText,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed
            : DateTime.MinValue;

        return new ImportedImage(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            importedAt,
            reader.GetString(8));
    }

    private static BatchTestRunRecord ReadBatchTestRun(SqliteDataReader reader)
    {
        return new BatchTestRunRecord(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? "UNKNOWN" : reader.GetString(4),
            ParseDateTime(reader.GetString(5)),
            reader.GetDouble(6),
            reader.GetDouble(7),
            reader.GetDouble(8),
            reader.GetDouble(9),
            reader.GetInt32(10),
            reader.GetInt32(11),
            reader.IsDBNull(12) ? string.Empty : reader.GetString(12),
            reader.IsDBNull(13) ? string.Empty : reader.GetString(13));
    }

    private static BatchTestResultRecord ReadBatchTestResult(SqliteDataReader reader)
    {
        return new BatchTestResultRecord(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.IsDBNull(17) ? "Pixel Difference Prototype Engine" : reader.GetString(17),
            reader.IsDBNull(18) ? "PIXEL_DIFF_0.1" : reader.GetString(18),
            reader.GetDouble(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.IsDBNull(23) ? "UNASSIGNED" : reader.GetString(23),
            reader.IsDBNull(24) ? "UNASSIGNED" : reader.GetString(24),
            reader.IsDBNull(25) ? "UNASSIGNED" : reader.GetString(25),
            reader.IsDBNull(26) ? "UNASSIGNED" : reader.GetString(26),
            reader.IsDBNull(27) ? "UNKNOWN_GT" : reader.GetString(27),
            reader.GetDouble(9),
            reader.GetDouble(10),
            reader.GetDouble(11),
            reader.GetDouble(12),
            reader.IsDBNull(13) ? string.Empty : reader.GetString(13),
            reader.IsDBNull(14) ? string.Empty : reader.GetString(14),
            reader.IsDBNull(15) ? string.Empty : reader.GetString(15),
            reader.IsDBNull(16) ? string.Empty : reader.GetString(16),
            reader.IsDBNull(19) ? string.Empty : reader.GetString(19),
            reader.IsDBNull(20) ? 0 : reader.GetDouble(20),
            reader.IsDBNull(21) ? 0 : reader.GetDouble(21),
            reader.IsDBNull(22) ? 0 : reader.GetDouble(22),
            reader.IsDBNull(28) ? 0 : reader.GetDouble(28),
            reader.IsDBNull(29) ? 0 : reader.GetDouble(29),
            ParseDateTime(reader.GetString(30)));
    }

    private static InspectionHistoryRecord ReadInspectionHistory(SqliteDataReader reader)
    {
        return new InspectionHistoryRecord(
            reader.GetInt64(0),
            ParseDateTime(reader.GetString(1)),
            reader.IsDBNull(2) ? "UNKNOWN" : reader.GetString(2),
            reader.IsDBNull(3) ? "UNKNOWN" : reader.GetString(3),
            reader.IsDBNull(4) ? "Pixel Difference Prototype Engine" : reader.GetString(4),
            reader.IsDBNull(5) ? "UNKNOWN" : reader.GetString(5),
            reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
            reader.IsDBNull(7) ? 0 : reader.GetDouble(7),
            reader.GetString(8),
            reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
            reader.GetString(10),
            reader.GetDouble(11),
            reader.GetDouble(12),
            reader.GetString(13),
            reader.GetString(14),
            reader.GetDouble(15),
            reader.GetDouble(16),
            reader.GetDouble(17),
            reader.GetDouble(18),
            reader.IsDBNull(19) ? 0 : reader.GetDouble(19),
            reader.IsDBNull(20) ? 0 : reader.GetDouble(20),
            reader.IsDBNull(21) ? 0 : reader.GetDouble(21),
            reader.IsDBNull(22) ? 0 : reader.GetDouble(22),
            reader.IsDBNull(23) ? 0 : reader.GetDouble(23));
    }

    private static ReviewEventRecord ReadReviewEvent(SqliteDataReader reader)
    {
        return new ReviewEventRecord(
            reader.GetInt64(0),
            ParseDateTime(reader.GetString(1)),
            reader.GetString(2),
            reader.IsDBNull(3) ? "UNKNOWN" : reader.GetString(3),
            reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
            reader.GetString(5));
    }

    private static ExportHistoryRecord ReadExportHistory(SqliteDataReader reader)
    {
        return new ExportHistoryRecord(
            reader.GetInt64(0),
            ParseDateTime(reader.GetString(1)),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? "UNKNOWN" : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetInt64(6));
    }

    private static ExportVerificationRecord ReadExportVerification(SqliteDataReader reader)
    {
        return new ExportVerificationRecord(
            reader.GetInt64(0),
            reader.IsDBNull(1) ? null : reader.GetInt64(1),
            ParseDateTime(reader.GetString(2)),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
            reader.IsDBNull(7) ? 0 : reader.GetInt64(7),
            reader.IsDBNull(8) ? "[]" : reader.GetString(8),
            reader.IsDBNull(9) ? "{}" : reader.GetString(9));
    }

    private static BuildTestEvidenceRecord ReadBuildTestEvidence(SqliteDataReader reader)
    {
        return new BuildTestEvidenceRecord
        {
            Id = reader.GetInt64(0),
            GeneratedAtUtc = ParseDateTime(reader.GetString(1)),
            GitCommit = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
            Configuration = reader.IsDBNull(3) ? "Release" : reader.GetString(3),
            HygieneStatus = reader.IsDBNull(4) ? "UNKNOWN" : reader.GetString(4),
            RestoreStatus = reader.IsDBNull(5) ? "UNKNOWN" : reader.GetString(5),
            BuildStatus = reader.IsDBNull(6) ? "UNKNOWN" : reader.GetString(6),
            TestStatus = reader.IsDBNull(7) ? "UNKNOWN" : reader.GetString(7),
            PublishValidationStatus = reader.IsDBNull(8) ? "UNKNOWN" : reader.GetString(8),
            EvidencePath = reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
            OperatorId = reader.IsDBNull(10) ? "UNKNOWN" : reader.GetString(10),
            CreatedAtUtc = ParseDateTime(reader.GetString(11)),
            TestResultPath = reader.IsDBNull(12) ? string.Empty : reader.GetString(12),
            MachineName = reader.IsDBNull(13) ? string.Empty : reader.GetString(13),
        };
    }

    private static ValidationPackageRecord ReadValidationPackage(SqliteDataReader reader)
    {
        return new ValidationPackageRecord(
            reader.GetInt64(0),
            ParseDateTime(reader.GetString(1)),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetInt64(7),
            reader.IsDBNull(8) ? "UNKNOWN" : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetInt64(9));
    }

    private static FalseCallReductionRun ReadFalseCallReductionRun(SqliteDataReader reader)
    {
        var criteria = DeserializeOrDefault(reader.IsDBNull(7) ? "{}" : reader.GetString(7), new FalseCallReductionCriteria());
        var selectedThreshold = reader.IsDBNull(10) ? (double?)null : reader.GetDouble(10);
        return new FalseCallReductionRun
        {
            Id = reader.GetInt64(0),
            BatchRunId = reader.IsDBNull(1) ? null : reader.GetInt64(1),
            CreatedAtUtc = ParseDateTime(reader.GetString(2)),
            EngineName = reader.GetString(3),
            ModelVersion = reader.GetString(4),
            ModelId = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
            ModelSha256 = reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
            Criteria = criteria,
            Recommendation = new OperatingPointRecommendation
            {
                Status = reader.IsDBNull(8) ? "INVALID" : reader.GetString(8),
                Mode = reader.IsDBNull(9) ? criteria.Mode.ToString() : reader.GetString(9),
                Point = selectedThreshold is null ? null : new ThresholdSweepPoint { ConfidenceThreshold = selectedThreshold.Value },
                Messages = DeserializeStringList(reader.IsDBNull(11) ? "[]" : reader.GetString(11)).ToList(),
            },
        };
    }

    private static CameraAcceptanceRun ReadCameraAcceptanceRun(SqliteDataReader reader)
        => new()
        {
            Id = reader.GetInt64(0),
            CreatedAtUtc = ParseDateTime(reader.GetString(1)),
            AdapterName = reader.GetString(2),
            SourceKey = reader.GetString(3),
            SettingsSummary = reader.GetString(4),
            Criteria = DeserializeOrDefault(reader.IsDBNull(5) ? "{}" : reader.GetString(5), new CameraAcceptanceCriteria()),
            Status = reader.GetString(6),
            FactoryReadinessStatus = reader.GetString(7),
            IsRealHardware = reader.GetInt32(8) != 0,
            TotalRequestedFrames = reader.GetInt32(9),
            TotalReceivedFrames = reader.GetInt32(10),
            DroppedFrameCount = reader.GetInt32(11),
            TriggerFailureCount = reader.GetInt32(12),
            TimeoutCount = reader.GetInt32(13),
            MaxConnectMs = reader.GetDouble(14),
            MaxFirstFrameMs = reader.GetDouble(15),
            AverageFrameIntervalMs = reader.GetDouble(16),
            Warnings = DeserializeOrDefault(reader.IsDBNull(17) ? "[]" : reader.GetString(17), new List<string>()),
            Failures = DeserializeOrDefault(reader.IsDBNull(18) ? "[]" : reader.GetString(18), new List<string>()),
            ViewMetrics = DeserializeOrDefault(reader.IsDBNull(19) ? "[]" : reader.GetString(19), new List<CameraAcceptanceViewMetrics>()),
        };

    private static IReadOnlyList<CameraAcceptanceFrameRecord> GetCameraAcceptanceFrames(long runId)
    {
        var frames = new List<CameraAcceptanceFrameRecord>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT ViewType, Sequence, FrameId, CameraId, CapturedAtUtc, Width, Height,
                   PixelFormat, SourceKind, IsSimulated, LatencyMs, IntervalMs,
                   MetadataValid, Message
            FROM CameraAcceptanceFrames
            WHERE RunId = $runId
            ORDER BY Id;
            """;
        command.Parameters.AddWithValue("$runId", runId);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            frames.Add(new CameraAcceptanceFrameRecord
            {
                ViewType = reader.GetString(0),
                Sequence = reader.GetInt32(1),
                FrameId = reader.GetString(2),
                CameraId = reader.GetString(3),
                CapturedAtUtc = ParseDateTime(reader.GetString(4)),
                Width = reader.GetInt32(5),
                Height = reader.GetInt32(6),
                PixelFormat = reader.GetString(7),
                SourceKind = reader.GetString(8),
                IsSimulated = reader.GetInt32(9) != 0,
                LatencyMs = reader.GetDouble(10),
                IntervalMs = reader.GetDouble(11),
                MetadataValid = reader.GetInt32(12) != 0,
                Message = reader.GetString(13),
            });
        }

        return frames;
    }

    private static LightingAcceptanceRun ReadLightingAcceptanceRun(SqliteDataReader reader)
        => new()
        {
            Id = reader.GetInt64(0),
            CreatedAtUtc = ParseDateTime(reader.GetString(1)),
            ControllerName = reader.GetString(2),
            Mode = reader.GetString(3),
            SettingsSummary = reader.GetString(4),
            Criteria = DeserializeOrDefault(reader.IsDBNull(5) ? "{}" : reader.GetString(5), new LightingAcceptanceCriteria()),
            Status = reader.GetString(6),
            IsSimulated = reader.GetInt32(7) != 0,
            StepCount = reader.GetInt32(8),
            PassedStepCount = reader.GetInt32(9),
            FailedStepCount = reader.GetInt32(10),
            MaxCommandLatencyMs = reader.GetDouble(11),
            MaxTriggerToFrameLatencyMs = reader.GetDouble(12),
            Warnings = DeserializeOrDefault(reader.IsDBNull(13) ? "[]" : reader.GetString(13), new List<string>()),
            Failures = DeserializeOrDefault(reader.IsDBNull(14) ? "[]" : reader.GetString(14), new List<string>()),
        };

    private static Profile3DAcceptanceRun ReadProfile3DAcceptanceRun(SqliteDataReader reader)
        => new()
        {
            Id = reader.GetInt64(0),
            CreatedAtUtc = ParseDateTime(reader.GetString(1)),
            SourceName = reader.GetString(2),
            SourceKind = reader.GetString(3),
            IsSimulated = reader.GetInt32(4) != 0,
            Status = reader.GetString(5),
            FactoryReadinessStatus = reader.GetString(6),
            AcquisitionMs = reader.GetDouble(7),
            Width = reader.GetInt32(8),
            Height = reader.GetInt32(9),
            Unit = reader.GetString(10),
            XPitchMicrons = reader.GetDouble(11),
            YPitchMicrons = reader.GetDouble(12),
            MissingHeightCount = reader.GetInt32(13),
            NaNHeightCount = reader.GetInt32(14),
            FrameId = reader.GetString(15),
            Criteria = DeserializeOrDefault(reader.IsDBNull(16) ? "{}" : reader.GetString(16), new Profile3DAcceptanceCriteria()),
            Diagnostics = DeserializeOrDefault(reader.IsDBNull(17) ? "{}" : reader.GetString(17), new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
            Warnings = DeserializeOrDefault(reader.IsDBNull(18) ? "[]" : reader.GetString(18), new List<string>()),
            Failures = DeserializeOrDefault(reader.IsDBNull(19) ? "[]" : reader.GetString(19), new List<string>()),
        };

    private static IReadOnlyList<LightingAcceptanceStep> GetLightingAcceptanceSteps(long runId)
    {
        var steps = new List<LightingAcceptanceStep>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT ViewType, ProgramName, CommandText, CommandLatencyMs,
                   TriggerToFrameLatencyMs, CommandAccepted, FrameReceived, FrameId,
                   CameraId, Status, Message
            FROM LightingAcceptanceSteps
            WHERE RunId = $runId
            ORDER BY Id;
            """;
        command.Parameters.AddWithValue("$runId", runId);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            steps.Add(new LightingAcceptanceStep
            {
                ViewType = reader.GetString(0),
                ProgramName = reader.GetString(1),
                CommandText = reader.GetString(2),
                CommandLatencyMs = reader.GetDouble(3),
                TriggerToFrameLatencyMs = reader.GetDouble(4),
                CommandAccepted = reader.GetInt32(5) != 0,
                FrameReceived = reader.GetInt32(6) != 0,
                FrameId = reader.GetString(7),
                CameraId = reader.GetString(8),
                Status = reader.GetString(9),
                Message = reader.GetString(10),
            });
        }

        return steps;
    }

    private static RobotAcceptanceRun ReadRobotAcceptanceRun(SqliteDataReader reader)
        => new()
        {
            Id = reader.GetInt64(0),
            CreatedAtUtc = ParseDateTime(reader.GetString(1)),
            ControllerName = reader.GetString(2),
            EmergencyStopName = reader.GetString(3),
            SafetyControllerName = reader.GetString(4),
            SafetySourceKind = reader.GetString(5),
            SourceKind = reader.GetString(6),
            Criteria = DeserializeOrDefault(reader.IsDBNull(7) ? "{}" : reader.GetString(7), new RobotAcceptanceCriteria()),
            Status = reader.GetString(8),
            FinalState = reader.GetString(9),
            LoadMs = reader.GetDouble(10),
            MoveToInspectMs = reader.GetDouble(11),
            InspectionMs = reader.GetDouble(12),
            UnloadMs = reader.GetDouble(13),
            FullCycleMs = reader.GetDouble(14),
            InvalidTransitionRejected = reader.GetInt32(15) != 0,
            EmergencyStopBlocked = reader.GetInt32(16) != 0,
            SafetyFaultBlocked = reader.GetInt32(17) != 0,
            ResetReturnedIdle = reader.GetInt32(18) != 0,
            AuditEventCount = reader.GetInt32(19),
            Warnings = DeserializeOrDefault(reader.IsDBNull(20) ? "[]" : reader.GetString(20), new List<string>()),
            Failures = DeserializeOrDefault(reader.IsDBNull(21) ? "[]" : reader.GetString(21), new List<string>()),
        };

    private static IReadOnlyList<RobotAcceptanceStep> GetRobotAcceptanceSteps(long runId)
    {
        var steps = new List<RobotAcceptanceStep>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT StepName, FromState, ToState, ElapsedMs, Accepted, Status, Message
            FROM RobotAcceptanceSteps
            WHERE RunId = $runId
            ORDER BY Id;
            """;
        command.Parameters.AddWithValue("$runId", runId);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            steps.Add(new RobotAcceptanceStep
            {
                StepName = reader.GetString(0),
                FromState = reader.GetString(1),
                ToState = reader.GetString(2),
                ElapsedMs = reader.GetDouble(3),
                Accepted = reader.GetInt32(4) != 0,
                Status = reader.GetString(5),
                Message = reader.GetString(6),
            });
        }

        return steps;
    }

    private static SoakTestResult ReadSoakTestRun(SqliteDataReader reader)
    {
        var run = new SoakTestResult
        {
            Id = reader.GetInt64(0),
            RunId = reader.GetString(1),
            StartTime = ParseDateTime(reader.GetString(2)).ToLocalTime(),
            EndTime = ParseDateTime(reader.GetString(3)).ToLocalTime(),
            ImageFolder = reader.GetString(4),
            OutputFolder = reader.GetString(5),
            EngineKey = reader.GetString(6),
            EngineName = reader.GetString(7),
            EngineVersion = reader.GetString(8),
            SourceKind = reader.GetString(9),
            IsRealCameraSource = reader.GetInt32(10) != 0,
            ProfileName = reader.GetString(11),
            RequestedDuration = TimeSpan.FromSeconds(reader.GetDouble(12)),
            DelayBetweenInspections = TimeSpan.FromMilliseconds(reader.GetDouble(14)),
            OperatorId = reader.GetString(15),
            BoardModel = reader.GetString(16),
            LotId = reader.GetString(17),
            WasCanceled = reader.GetInt32(18) != 0,
            TotalCycles = reader.GetInt32(19),
            SuccessfulCycles = reader.GetInt32(20),
            FailedCycles = reader.GetInt32(21),
            AverageInspectionMilliseconds = reader.GetDouble(22),
            MinInspectionMilliseconds = reader.GetDouble(23),
            MaxInspectionMilliseconds = reader.GetDouble(24),
            P95InspectionMilliseconds = reader.GetDouble(25),
            CountOverOneSecond = reader.GetInt32(26),
            StartManagedMemoryMegabytes = reader.GetDouble(27),
            EndManagedMemoryMegabytes = reader.GetDouble(28),
            StartWorkingSetMegabytes = reader.GetDouble(29),
            EndWorkingSetMegabytes = reader.GetDouble(30),
            PeakWorkingSetMegabytes = reader.GetDouble(31),
            AverageTotalCycleMilliseconds = reader.IsDBNull(34) ? 0 : reader.GetDouble(34),
            MaxTotalCycleMilliseconds = reader.IsDBNull(35) ? 0 : reader.GetDouble(35),
            P95TotalCycleMilliseconds = reader.IsDBNull(36) ? 0 : reader.GetDouble(36),
            CancellationReason = reader.IsDBNull(37) ? string.Empty : reader.GetString(37),
            FirstCriticalError = reader.IsDBNull(38) ? string.Empty : reader.GetString(38),
        };
        run.Errors.AddRange(DeserializeOrDefault(reader.IsDBNull(33) ? "[]" : reader.GetString(33), new List<string>()));
        run.MemoryWarnings.AddRange(DeserializeOrDefault(reader.IsDBNull(39) ? "[]" : reader.GetString(39), new List<string>()));
        return run;
    }

    private static IReadOnlyList<SoakTestCycleRecord> GetSoakTestIterations(long runId)
    {
        var cycles = new List<SoakTestCycleRecord>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT CycleNumber, TimestampUtc, FrameId, ImagePath, EngineName, Verdict,
                   TotalInspectionMs, WorkingSetMb, Success, Message, Error, TotalCycleMs, ExceptionCategory
            FROM SoakTestIterations
            WHERE RunId = $runId
            ORDER BY CycleNumber ASC, Id ASC;
            """;
        command.Parameters.AddWithValue("$runId", runId);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cycles.Add(new SoakTestCycleRecord(
                reader.GetInt32(0),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(5),
                reader.GetDouble(6),
                reader.GetInt32(8) != 0,
                reader.GetString(9),
                ParseDateTime(reader.GetString(1)),
                reader.GetString(4),
                reader.GetDouble(7),
                reader.GetString(10),
                reader.IsDBNull(11) ? 0 : reader.GetDouble(11),
                reader.IsDBNull(12) ? string.Empty : reader.GetString(12)));
        }

        return cycles;
    }

    private static ThresholdProfile ReadThresholdProfile(SqliteDataReader reader)
    {
        return new ThresholdProfile
        {
            ProfileId = reader.GetString(0),
            Revision = reader.GetString(1),
            BoardModel = reader.GetString(2),
            BoardProgram = reader.GetString(3),
            RecipeName = reader.GetString(4),
            RecipeRevision = reader.GetString(5),
            Status = reader.GetString(6),
            SourceValidationRunId = reader.IsDBNull(7) ? null : reader.GetInt64(7),
            SourceFalseCallReductionRunId = reader.IsDBNull(8) ? null : reader.GetInt64(8),
            CreatedBy = reader.GetString(9),
            CreatedAtUtc = ParseDateTime(reader.GetString(10)),
            ApprovedBy = reader.IsDBNull(11) ? string.Empty : reader.GetString(11),
            ApprovedAtUtc = reader.IsDBNull(12) ? null : ParseDateTime(reader.GetString(12)),
        };
    }

    private static IReadOnlyList<ThresholdProfileRule> GetThresholdProfileRules(string profileId, string revision)
    {
        var rules = new List<ThresholdProfileRule>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, ProfileId, Revision, ViewType, RoiType, DefectClass, ReviewThreshold, NgThreshold,
                   ConfidenceThreshold, MinimumAreaPixels, MaxAllowedFalseCallRate
            FROM ThresholdProfileRules
            WHERE ProfileId = $profileId AND Revision = $revision
            ORDER BY Id ASC;
            """;
        command.Parameters.AddWithValue("$profileId", profileId);
        command.Parameters.AddWithValue("$revision", revision);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rules.Add(new ThresholdProfileRule
            {
                Id = reader.GetInt64(0),
                ProfileId = reader.GetString(1),
                Revision = reader.GetString(2),
                ViewType = reader.GetString(3),
                RoiType = reader.GetString(4),
                DefectClass = reader.GetString(5),
                ReviewThreshold = reader.GetDouble(6),
                NgThreshold = reader.GetDouble(7),
                ConfidenceThreshold = reader.GetDouble(8),
                MinimumAreaPixels = reader.GetDouble(9),
                MaxAllowedFalseCallRate = reader.GetDouble(10),
            });
        }

        return rules;
    }

    private static IReadOnlyList<ThresholdSweepPoint> GetFalseCallReductionPoints(long runId)
    {
        var points = new List<ThresholdSweepPoint>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT ConfidenceThreshold, DifferenceThreshold, TruePositive, TrueNegative,
                   FalsePositive, FalseNegative, Precision, Recall, FalseCallRate, PossibleEscapeRate,
                   ReviewRate, NgRate, ReviewCount, NgCount, EstimatedManualReviewMinutes,
                   MeetsConstraints, Status
            FROM FalseCallReductionPoints
            WHERE RunId = $runId
            ORDER BY ConfidenceThreshold ASC, Id ASC;
            """;
        command.Parameters.AddWithValue("$runId", runId);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            points.Add(new ThresholdSweepPoint
            {
                ConfidenceThreshold = reader.GetDouble(0),
                DifferenceThreshold = reader.GetDouble(1),
                TruePositive = reader.GetInt32(2),
                TrueNegative = reader.GetInt32(3),
                FalsePositive = reader.GetInt32(4),
                FalseNegative = reader.GetInt32(5),
                Precision = reader.GetDouble(6),
                Recall = reader.GetDouble(7),
                FalseCallRate = reader.GetDouble(8),
                PossibleEscapeRate = reader.GetDouble(9),
                ReviewRate = reader.GetDouble(10),
                NgRate = reader.GetDouble(11),
                ReviewCount = reader.GetInt32(12),
                NgCount = reader.GetInt32(13),
                EstimatedManualReviewMinutes = reader.GetDouble(14),
                MeetsConstraints = reader.GetInt32(15) != 0,
                Status = reader.GetString(16),
            });
        }

        return points;
    }

    private static ModelRegistryRecord ReadModelRegistryRecord(SqliteDataReader reader)
    {
        var statusText = reader.IsDBNull(17) ? string.Empty : reader.GetString(17);
        var status = Enum.TryParse<ModelConfigurationTestStatus>(statusText, ignoreCase: true, out var parsedStatus)
            ? parsedStatus
            : ModelConfigurationTestStatus.NotTested;

        return new ModelRegistryRecord(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            ParseDateTime(reader.GetString(4)),
            ParseDateTime(reader.GetString(5)),
            reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
            reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
            reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
            reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
            reader.IsDBNull(10) ? string.Empty : reader.GetString(10),
            reader.IsDBNull(11) ? string.Empty : reader.GetString(11),
            reader.IsDBNull(12) ? string.Empty : reader.GetString(12),
            reader.IsDBNull(13) ? 640 : reader.GetInt32(13),
            reader.IsDBNull(14) ? 640 : reader.GetInt32(14),
            reader.IsDBNull(15) ? 0.65 : reader.GetDouble(15),
            DeserializeStringList(reader.IsDBNull(16) ? "[]" : reader.GetString(16)),
            status,
            reader.IsDBNull(18) ? null : ParseDateTime(reader.GetString(18)),
            reader.IsDBNull(19) ? string.Empty : reader.GetString(19),
            reader.IsDBNull(20) ? string.Empty : reader.GetString(20),
            !reader.IsDBNull(21) && reader.GetInt32(21) != 0,
            reader.IsDBNull(22) ? null : reader.GetInt64(22),
            reader.IsDBNull(23) || !Enum.TryParse<ModelLifecycleState>(reader.GetString(23), ignoreCase: true, out var lifecycleState)
                ? ModelLifecycleState.Registered
                : lifecycleState,
            reader.IsDBNull(24) ? string.Empty : reader.GetString(24),
            reader.IsDBNull(25) ? null : reader.GetInt64(25),
            reader.IsDBNull(26) ? null : reader.GetInt64(26),
            reader.IsDBNull(27) ? string.Empty : reader.GetString(27),
            reader.IsDBNull(28) ? string.Empty : reader.GetString(28),
            reader.IsDBNull(29) ? null : ParseDateTime(reader.GetString(29)),
            reader.IsDBNull(30) ? string.Empty : reader.GetString(30),
            reader.IsDBNull(31) ? null : ParseDateTime(reader.GetString(31)),
            reader.IsDBNull(32) ? null : ParseDateTime(reader.GetString(32)),
            reader.IsDBNull(33) ? string.Empty : reader.GetString(33),
            reader.IsDBNull(34) ? null : ParseDateTime(reader.GetString(34)));
    }

    private static InspectionLatencyTrace ReadInspectionLatencyTrace(SqliteDataReader reader)
        => new()
        {
            Id = reader.GetInt64(0),
            TraceId = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
            CreatedAtUtc = ParseDateTime(reader.GetString(2)),
            FrameCapturedAtUtc = reader.IsDBNull(3) ? null : ParseDateTime(reader.GetString(3)),
            FrameReceivedAtUtc = reader.IsDBNull(4) ? null : ParseDateTime(reader.GetString(4)),
            PreprocessingStartUtc = reader.IsDBNull(5) ? null : ParseDateTime(reader.GetString(5)),
            PreprocessingEndUtc = reader.IsDBNull(6) ? null : ParseDateTime(reader.GetString(6)),
            InferenceStartUtc = reader.IsDBNull(7) ? null : ParseDateTime(reader.GetString(7)),
            InferenceEndUtc = reader.IsDBNull(8) ? null : ParseDateTime(reader.GetString(8)),
            PostprocessStartUtc = reader.IsDBNull(9) ? null : ParseDateTime(reader.GetString(9)),
            PostprocessEndUtc = reader.IsDBNull(10) ? null : ParseDateTime(reader.GetString(10)),
            OverlayRenderStartUtc = reader.IsDBNull(11) ? null : ParseDateTime(reader.GetString(11)),
            OverlayRenderEndUtc = reader.IsDBNull(12) ? null : ParseDateTime(reader.GetString(12)),
            ResultPersistStartUtc = reader.IsDBNull(13) ? null : ParseDateTime(reader.GetString(13)),
            ResultPersistEndUtc = reader.IsDBNull(14) ? null : ParseDateTime(reader.GetString(14)),
            TotalFrameToOverlayMs = reader.IsDBNull(15) ? 0 : reader.GetDouble(15),
            TotalFrameToSavedResultMs = reader.IsDBNull(16) ? 0 : reader.GetDouble(16),
            SourceKind = reader.IsDBNull(17) ? string.Empty : reader.GetString(17),
            Engine = reader.IsDBNull(18) ? string.Empty : reader.GetString(18),
            ModelId = reader.IsDBNull(19) ? string.Empty : reader.GetString(19),
            ImageWidth = reader.IsDBNull(20) ? 0 : reader.GetInt32(20),
            ImageHeight = reader.IsDBNull(21) ? 0 : reader.GetInt32(21),
            Verdict = reader.IsDBNull(22) ? string.Empty : reader.GetString(22),
            Warnings = DeserializeStringList(reader.IsDBNull(23) ? "[]" : reader.GetString(23)).ToList(),
        };

    private static ModelAcceptanceRun ReadModelAcceptanceRun(SqliteDataReader reader)
    {
        return new ModelAcceptanceRun
        {
            Id = reader.GetInt64(0),
            CreatedAtUtc = ParseDateTime(reader.GetString(1)),
            ModelId = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
            ModelVersion = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
            ModelSha256 = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
            ModelPath = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
            LabelMapPath = reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
            InputTensorName = reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
            OutputTensorName = reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
            OutputShape = reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
            DatasetFolder = reader.IsDBNull(10) ? string.Empty : reader.GetString(10),
            DatasetName = reader.IsDBNull(11) ? string.Empty : reader.GetString(11),
            GroundTruthCsvPath = reader.IsDBNull(12) ? string.Empty : reader.GetString(12),
            IsFormalManifest = !reader.IsDBNull(13) && reader.GetInt32(13) != 0,
            Status = reader.IsDBNull(14) ? "FAIL" : reader.GetString(14),
            OperatorId = reader.IsDBNull(15) ? "UNKNOWN" : reader.GetString(15),
            ApprovedBy = reader.IsDBNull(16) ? string.Empty : reader.GetString(16),
            ApprovedAtUtc = reader.IsDBNull(17) ? null : ParseDateTime(reader.GetString(17)),
            IsProductionCandidate = !reader.IsDBNull(18) && reader.GetInt32(18) != 0,
            Criteria = DeserializeOrDefault(reader.IsDBNull(19) ? "{}" : reader.GetString(19), new ModelAcceptanceCriteria()),
            Metrics = DeserializeOrDefault(reader.IsDBNull(20) ? "{}" : reader.GetString(20), new BatchMetrics(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)),
            DatasetQualitySummary = DeserializeOrDefault(reader.IsDBNull(21) ? "{}" : reader.GetString(21), new DatasetQualitySummary()),
            FalseCallRecommendation = DeserializeOrDefault(reader.IsDBNull(22) ? "{}" : reader.GetString(22), new FalseCallRecommendationSummary()),
            BreakdownSummary = DeserializeOrDefault(reader.IsDBNull(23) ? "{}" : reader.GetString(23), new ValidationBreakdownSummary()),
            PerformanceSummary = DeserializeOrDefault(reader.IsDBNull(24) ? "{}" : reader.GetString(24), new BatchPerformanceSummary(0, 0, 0, 0, 0)),
            P95InferenceMs = reader.IsDBNull(25) ? 0 : reader.GetDouble(25),
            Messages = DeserializeStringList(reader.IsDBNull(26) ? "[]" : reader.GetString(26)).ToList(),
            Limitations = DeserializeStringList(reader.IsDBNull(27) ? "[]" : reader.GetString(27)).ToList(),
        };
    }

    private static AuditEventRecord ReadAuditEvent(SqliteDataReader reader)
    {
        return new AuditEventRecord(
            reader.GetInt64(0),
            ParseDateTime(reader.GetString(1)),
            ParseDateTime(reader.GetString(2)),
            reader.IsDBNull(3) ? "UNKNOWN" : reader.GetString(3),
            reader.IsDBNull(4) ? "UNKNOWN" : reader.GetString(4),
            reader.IsDBNull(5) ? "UNKNOWN" : reader.GetString(5),
            reader.IsDBNull(6) ? "UNKNOWN" : reader.GetString(6),
            reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
            reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
            reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
            reader.IsDBNull(10) ? string.Empty : reader.GetString(10));
    }

    private static MesUploadAttemptRecord ReadMesUploadAttempt(SqliteDataReader reader)
    {
        return new MesUploadAttemptRecord(
            reader.GetInt64(0),
            ParseDateTime(reader.GetString(1)),
            reader.GetString(2),
            reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.IsDBNull(7) ? "UNKNOWN" : reader.GetString(7),
            reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
            reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
            reader.IsDBNull(10) ? string.Empty : reader.GetString(10));
    }

    private static MesSpoolQueueRecord ReadMesSpoolQueueRecord(SqliteDataReader reader)
    {
        return new MesSpoolQueueRecord(
            reader.GetInt64(0),
            ParseDateTime(reader.GetString(1)),
            reader.IsDBNull(2) ? null : ParseDateTime(reader.GetString(2)),
            reader.IsDBNull(3) ? null : ParseDateTime(reader.GetString(3)),
            reader.GetString(4),
            reader.GetString(5),
            reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
            reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
            reader.GetInt32(8),
            reader.GetInt32(9),
            reader.GetString(10),
            reader.IsDBNull(11) ? string.Empty : reader.GetString(11),
            reader.IsDBNull(12) ? "UNKNOWN" : reader.GetString(12),
            reader.IsDBNull(13) ? string.Empty : reader.GetString(13),
            reader.IsDBNull(14) ? string.Empty : reader.GetString(14),
            reader.IsDBNull(15) ? string.Empty : reader.GetString(15));
    }

    private static CentralSyncQueueRecord ReadCentralSyncQueueRecord(SqliteDataReader reader)
    {
        return new CentralSyncQueueRecord(
            reader.GetInt64(0),
            ParseDateTime(reader.GetString(1)),
            reader.IsDBNull(2) ? null : ParseDateTime(reader.GetString(2)),
            reader.IsDBNull(3) ? null : ParseDateTime(reader.GetString(3)),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
            reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
            reader.IsDBNull(9) ? Environment.MachineName : reader.GetString(9),
            reader.GetInt32(10),
            reader.GetInt32(11),
            reader.GetString(12),
            reader.IsDBNull(13) ? string.Empty : reader.GetString(13));
    }

    private static RecipeRevisionRecord ReadRecipeRevision(SqliteDataReader reader)
    {
        return new RecipeRevisionRecord(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? "UNKNOWN" : reader.GetString(3),
            reader.IsDBNull(4) ? "UNKNOWN" : reader.GetString(4),
            reader.GetString(5),
            reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
            reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
            ParseDateTime(reader.GetString(8)));
    }

    private static CalibrationProfileRecord ReadCalibrationProfile(
        SqliteDataReader reader,
        IReadOnlyList<CalibrationPointRecord> points)
    {
        return new CalibrationProfileRecord(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? "UNKNOWN" : reader.GetString(2),
            reader.IsDBNull(3) ? "Top" : reader.GetString(3),
            reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
            reader.IsDBNull(5) ? "UNKNOWN" : reader.GetString(5),
            reader.IsDBNull(6) ? points.Count : reader.GetInt32(6),
            reader.IsDBNull(7) ? 0 : reader.GetDouble(7),
            reader.IsDBNull(8) ? 0 : reader.GetDouble(8),
            reader.IsDBNull(9) ? 0 : reader.GetDouble(9),
            reader.IsDBNull(10) ? 0 : reader.GetDouble(10),
            reader.IsDBNull(11) ? string.Empty : reader.GetString(11),
            ParseDateTime(reader.GetString(12)),
            points);
    }

    private static void BindModelRegistryRecord(SqliteCommand command, ModelRegistryRecord record)
    {
        command.Parameters.AddWithValue("$modelId", record.ModelId);
        command.Parameters.AddWithValue("$displayName", record.DisplayName);
        command.Parameters.AddWithValue("$version", record.Version);
        command.Parameters.AddWithValue("$createdAtUtc", record.CreatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$registeredAtUtc", record.RegisteredAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$sourceFileName", record.SourceFileName);
        command.Parameters.AddWithValue("$storedModelPath", record.StoredModelPath);
        command.Parameters.AddWithValue("$storedLabelMapPath", record.StoredLabelMapPath);
        command.Parameters.AddWithValue("$metadataPath", record.MetadataPath);
        command.Parameters.AddWithValue("$sha256", record.Sha256);
        command.Parameters.AddWithValue("$inputTensorName", record.InputTensorName);
        command.Parameters.AddWithValue("$outputTensorName", record.OutputTensorName);
        command.Parameters.AddWithValue("$inputWidth", record.InputWidth);
        command.Parameters.AddWithValue("$inputHeight", record.InputHeight);
        command.Parameters.AddWithValue("$confidenceThreshold", record.ConfidenceThreshold);
        command.Parameters.AddWithValue("$labelsJson", JsonSerializer.Serialize(record.Labels));
        command.Parameters.AddWithValue("$validationStatus", record.ValidationStatus.ToString());
        command.Parameters.AddWithValue("$lastValidatedAtUtc", record.LastValidatedAtUtc is { } timestamp ? (object)timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) : DBNull.Value);
        command.Parameters.AddWithValue("$validationMessage", record.ValidationMessage);
        command.Parameters.AddWithValue("$notes", record.Notes);
        command.Parameters.AddWithValue("$isActive", record.IsActive ? 1 : 0);
        command.Parameters.AddWithValue("$auditEventId", record.AuditEventId is { } id ? (object)id : DBNull.Value);
        command.Parameters.AddWithValue("$lifecycleState", record.LifecycleState.ToString());
        command.Parameters.AddWithValue("$latestAcceptanceStatus", record.LatestAcceptanceStatus ?? string.Empty);
        command.Parameters.AddWithValue("$latestAcceptanceRunId", record.LatestAcceptanceRunId is { } acceptanceId ? (object)acceptanceId : DBNull.Value);
        command.Parameters.AddWithValue("$latestReleasePackageId", record.LatestReleasePackageId is { } releaseId ? (object)releaseId : DBNull.Value);
        command.Parameters.AddWithValue("$latestReleasePackagePath", record.LatestReleasePackagePath ?? string.Empty);
        command.Parameters.AddWithValue("$deploymentWaiverReason", record.DeploymentWaiverReason ?? string.Empty);
        command.Parameters.AddWithValue("$waiverExpiresAtUtc", record.WaiverExpiresAtUtc is { } expiresAt ? (object)expiresAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) : DBNull.Value);
        command.Parameters.AddWithValue("$deploymentWaivedBy", record.DeploymentWaivedBy ?? string.Empty);
        command.Parameters.AddWithValue("$deploymentWaivedAtUtc", record.DeploymentWaivedAtUtc is { } waivedAt ? (object)waivedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) : DBNull.Value);
        command.Parameters.AddWithValue("$deployedAtUtc", record.DeployedAtUtc is { } deployedAt ? (object)deployedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) : DBNull.Value);
        command.Parameters.AddWithValue("$retiredReason", record.RetiredReason ?? string.Empty);
        command.Parameters.AddWithValue("$retiredAtUtc", record.RetiredAtUtc is { } retiredAt ? (object)retiredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) : DBNull.Value);
    }

    private static void BindInspectionLatencyTrace(SqliteCommand command, InspectionLatencyTrace trace)
    {
        command.Parameters.AddWithValue("$traceId", trace.TraceId);
        command.Parameters.AddWithValue("$createdAtUtc", trace.CreatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$frameCapturedAtUtc", ToDbDate(trace.FrameCapturedAtUtc));
        command.Parameters.AddWithValue("$frameReceivedAtUtc", ToDbDate(trace.FrameReceivedAtUtc));
        command.Parameters.AddWithValue("$preprocessingStartUtc", ToDbDate(trace.PreprocessingStartUtc));
        command.Parameters.AddWithValue("$preprocessingEndUtc", ToDbDate(trace.PreprocessingEndUtc));
        command.Parameters.AddWithValue("$inferenceStartUtc", ToDbDate(trace.InferenceStartUtc));
        command.Parameters.AddWithValue("$inferenceEndUtc", ToDbDate(trace.InferenceEndUtc));
        command.Parameters.AddWithValue("$postprocessStartUtc", ToDbDate(trace.PostprocessStartUtc));
        command.Parameters.AddWithValue("$postprocessEndUtc", ToDbDate(trace.PostprocessEndUtc));
        command.Parameters.AddWithValue("$overlayRenderStartUtc", ToDbDate(trace.OverlayRenderStartUtc));
        command.Parameters.AddWithValue("$overlayRenderEndUtc", ToDbDate(trace.OverlayRenderEndUtc));
        command.Parameters.AddWithValue("$resultPersistStartUtc", ToDbDate(trace.ResultPersistStartUtc));
        command.Parameters.AddWithValue("$resultPersistEndUtc", ToDbDate(trace.ResultPersistEndUtc));
        command.Parameters.AddWithValue("$totalFrameToOverlayMs", trace.TotalFrameToOverlayMs);
        command.Parameters.AddWithValue("$totalFrameToSavedResultMs", trace.TotalFrameToSavedResultMs);
        command.Parameters.AddWithValue("$sourceKind", trace.SourceKind ?? string.Empty);
        command.Parameters.AddWithValue("$engine", trace.Engine ?? string.Empty);
        command.Parameters.AddWithValue("$modelId", trace.ModelId ?? string.Empty);
        command.Parameters.AddWithValue("$imageWidth", trace.ImageWidth);
        command.Parameters.AddWithValue("$imageHeight", trace.ImageHeight);
        command.Parameters.AddWithValue("$verdict", trace.Verdict ?? string.Empty);
        command.Parameters.AddWithValue("$warningsJson", JsonSerializer.Serialize(trace.Warnings));
    }

    private static object ToDbDate(DateTime? value)
        => value is { } timestamp
            ? timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
            : DBNull.Value;

    private static void BindModelAcceptanceRun(SqliteCommand command, ModelAcceptanceRun run, string operatorId, long auditEventId)
    {
        command.Parameters.AddWithValue("$createdAtUtc", run.CreatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$modelId", run.ModelId ?? string.Empty);
        command.Parameters.AddWithValue("$modelVersion", run.ModelVersion ?? string.Empty);
        command.Parameters.AddWithValue("$modelSha256", run.ModelSha256 ?? string.Empty);
        command.Parameters.AddWithValue("$modelPath", run.ModelPath ?? string.Empty);
        command.Parameters.AddWithValue("$labelMapPath", run.LabelMapPath ?? string.Empty);
        command.Parameters.AddWithValue("$inputTensorName", run.InputTensorName ?? string.Empty);
        command.Parameters.AddWithValue("$outputTensorName", run.OutputTensorName ?? string.Empty);
        command.Parameters.AddWithValue("$outputShape", run.OutputShape ?? string.Empty);
        command.Parameters.AddWithValue("$datasetFolder", run.DatasetFolder ?? string.Empty);
        command.Parameters.AddWithValue("$datasetName", run.DatasetName ?? string.Empty);
        command.Parameters.AddWithValue("$groundTruthCsvPath", run.GroundTruthCsvPath ?? string.Empty);
        command.Parameters.AddWithValue("$isFormalManifest", run.IsFormalManifest ? 1 : 0);
        command.Parameters.AddWithValue("$status", run.Status ?? "FAIL");
        command.Parameters.AddWithValue("$operatorId", operatorId);
        command.Parameters.AddWithValue("$approvedBy", run.ApprovedBy ?? string.Empty);
        command.Parameters.AddWithValue("$approvedAtUtc", run.ApprovedAtUtc is { } approvedAt ? (object)approvedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) : DBNull.Value);
        command.Parameters.AddWithValue("$isProductionCandidate", run.IsProductionCandidate ? 1 : 0);
        command.Parameters.AddWithValue("$criteriaJson", JsonSerializer.Serialize(run.Criteria));
        command.Parameters.AddWithValue("$metricsJson", JsonSerializer.Serialize(run.Metrics));
        command.Parameters.AddWithValue("$datasetQualityJson", JsonSerializer.Serialize(run.DatasetQualitySummary));
        command.Parameters.AddWithValue("$falseCallRecommendationJson", JsonSerializer.Serialize(run.FalseCallRecommendation));
        command.Parameters.AddWithValue("$breakdownJson", JsonSerializer.Serialize(run.BreakdownSummary));
        command.Parameters.AddWithValue("$performanceJson", JsonSerializer.Serialize(run.PerformanceSummary));
        command.Parameters.AddWithValue("$p95InferenceMs", run.P95InferenceMs);
        command.Parameters.AddWithValue("$messagesJson", JsonSerializer.Serialize(run.Messages));
        command.Parameters.AddWithValue("$limitationsJson", JsonSerializer.Serialize(run.Limitations));
        command.Parameters.AddWithValue("$auditEventId", auditEventId);
    }

    private static void BindFalseCallReductionPoint(SqliteCommand command, ThresholdSweepPoint point)
    {
        command.Parameters.AddWithValue("$confidenceThreshold", point.ConfidenceThreshold);
        command.Parameters.AddWithValue("$differenceThreshold", point.DifferenceThreshold);
        command.Parameters.AddWithValue("$truePositive", point.TruePositive);
        command.Parameters.AddWithValue("$trueNegative", point.TrueNegative);
        command.Parameters.AddWithValue("$falsePositive", point.FalsePositive);
        command.Parameters.AddWithValue("$falseNegative", point.FalseNegative);
        command.Parameters.AddWithValue("$precision", point.Precision);
        command.Parameters.AddWithValue("$recall", point.Recall);
        command.Parameters.AddWithValue("$falseCallRate", point.FalseCallRate);
        command.Parameters.AddWithValue("$possibleEscapeRate", point.PossibleEscapeRate);
        command.Parameters.AddWithValue("$reviewRate", point.ReviewRate);
        command.Parameters.AddWithValue("$ngRate", point.NgRate);
        command.Parameters.AddWithValue("$reviewCount", point.ReviewCount);
        command.Parameters.AddWithValue("$ngCount", point.NgCount);
        command.Parameters.AddWithValue("$estimatedManualReviewMinutes", point.EstimatedManualReviewMinutes);
        command.Parameters.AddWithValue("$meetsConstraints", point.MeetsConstraints ? 1 : 0);
        command.Parameters.AddWithValue("$status", point.Status);
    }

    private static void BindValidationBreakdownMetric(SqliteCommand command, long runId, ValidationBreakdownMetric metric)
    {
        command.Parameters.AddWithValue("$runId", runId);
        command.Parameters.AddWithValue("$breakdownType", metric.BreakdownType);
        command.Parameters.AddWithValue("$key", metric.Key);
        command.Parameters.AddWithValue("$displayName", metric.DisplayName);
        command.Parameters.AddWithValue("$total", metric.Total);
        command.Parameters.AddWithValue("$truePositive", metric.TruePositive);
        command.Parameters.AddWithValue("$trueNegative", metric.TrueNegative);
        command.Parameters.AddWithValue("$falsePositive", metric.FalsePositive);
        command.Parameters.AddWithValue("$falseNegative", metric.FalseNegative);
        command.Parameters.AddWithValue("$wrongDefectClass", metric.WrongDefectClass);
        command.Parameters.AddWithValue("$wrongSide", metric.WrongSide);
        command.Parameters.AddWithValue("$unknownGroundTruth", metric.UnknownGroundTruth);
        command.Parameters.AddWithValue("$precision", metric.Precision);
        command.Parameters.AddWithValue("$recall", metric.Recall);
        command.Parameters.AddWithValue("$falseCallRate", metric.FalseCallRate);
        command.Parameters.AddWithValue("$createdAtUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
    }

    private static void BindThresholdProfile(SqliteCommand command, ThresholdProfile profile)
    {
        command.Parameters.AddWithValue("$profileId", profile.ProfileId);
        command.Parameters.AddWithValue("$revision", profile.Revision);
        command.Parameters.AddWithValue("$boardModel", NormalizeProfileScope(profile.BoardModel));
        command.Parameters.AddWithValue("$boardProgram", NormalizeProfileScope(profile.BoardProgram));
        command.Parameters.AddWithValue("$recipeName", NormalizeProfileScope(profile.RecipeName));
        command.Parameters.AddWithValue("$recipeRevision", NormalizeProfileScope(profile.RecipeRevision));
        command.Parameters.AddWithValue("$status", profile.Status);
        command.Parameters.AddWithValue("$sourceValidationRunId", profile.SourceValidationRunId is { } validationRunId ? (object)validationRunId : DBNull.Value);
        command.Parameters.AddWithValue("$sourceFalseCallReductionRunId", profile.SourceFalseCallReductionRunId is { } falseCallRunId ? (object)falseCallRunId : DBNull.Value);
        command.Parameters.AddWithValue("$createdBy", profile.CreatedBy);
        command.Parameters.AddWithValue("$createdAtUtc", profile.CreatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$approvedBy", string.IsNullOrWhiteSpace(profile.ApprovedBy) ? DBNull.Value : profile.ApprovedBy);
        command.Parameters.AddWithValue("$approvedAtUtc", profile.ApprovedAtUtc is { } approvedAt ? approvedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) : DBNull.Value);
    }

    private static void BindThresholdProfileRule(SqliteCommand command, ThresholdProfile profile, ThresholdProfileRule rule)
    {
        command.Parameters.AddWithValue("$profileId", profile.ProfileId);
        command.Parameters.AddWithValue("$revision", profile.Revision);
        command.Parameters.AddWithValue("$viewType", NormalizeProfileScope(rule.ViewType));
        command.Parameters.AddWithValue("$roiType", NormalizeProfileScope(rule.RoiType));
        command.Parameters.AddWithValue("$defectClass", NormalizeProfileScope(rule.DefectClass));
        command.Parameters.AddWithValue("$reviewThreshold", rule.ReviewThreshold);
        command.Parameters.AddWithValue("$ngThreshold", rule.NgThreshold);
        command.Parameters.AddWithValue("$confidenceThreshold", rule.ConfidenceThreshold);
        command.Parameters.AddWithValue("$minimumAreaPixels", rule.MinimumAreaPixels);
        command.Parameters.AddWithValue("$maxAllowedFalseCallRate", rule.MaxAllowedFalseCallRate);
    }

    private static string NormalizeProfileScope(string? value)
        => string.IsNullOrWhiteSpace(value) ? "ANY" : value.Trim();

    private static IReadOnlyList<CalibrationPointRecord> GetCalibrationPoints(long profileId)
    {
        var points = new List<CalibrationPointRecord>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, ProfileId, ImageX, ImageY, BoardXMillimeters, BoardYMillimeters
            FROM CalibrationPoints
            WHERE ProfileId = $profileId
            ORDER BY Id ASC;
            """;
        command.Parameters.AddWithValue("$profileId", profileId);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            points.Add(new CalibrationPointRecord(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetDouble(2),
                reader.GetDouble(3),
                reader.GetDouble(4),
                reader.GetDouble(5)));
        }

        return points;
    }

    private static string BuildInspectionWhere(LogFilter filter, SqliteCommand command)
    {
        var clauses = new List<string>();
        AddDateRangeClauses("CreatedAtUtc", filter, clauses, command);

        if (!string.IsNullOrWhiteSpace(filter.BoardProgram))
        {
            clauses.Add("BoardProgram LIKE $boardProgram");
            command.Parameters.AddWithValue("$boardProgram", $"%{filter.BoardProgram}%");
        }

        if (!string.IsNullOrWhiteSpace(filter.OperatorId))
        {
            clauses.Add("OperatorId LIKE $operatorId");
            command.Parameters.AddWithValue("$operatorId", $"%{filter.OperatorId}%");
        }

        if (!string.IsNullOrWhiteSpace(filter.Result) && filter.Result != "ALL")
        {
            clauses.Add("Verdict = $result");
            command.Parameters.AddWithValue("$result", filter.Result);
        }

        return clauses.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", clauses);
    }

    private static string BuildReviewWhere(LogFilter filter, SqliteCommand command)
    {
        var clauses = new List<string>();
        AddDateRangeClauses("EventTimeUtc", filter, clauses, command);

        if (!string.IsNullOrWhiteSpace(filter.OperatorId))
        {
            clauses.Add("OperatorId LIKE $operatorId");
            command.Parameters.AddWithValue("$operatorId", $"%{filter.OperatorId}%");
        }

        if (!string.IsNullOrWhiteSpace(filter.BoardProgram))
        {
            clauses.Add("(Category LIKE $reviewBoardProgram OR Message LIKE $reviewBoardProgram OR Disposition LIKE $reviewBoardProgram)");
            command.Parameters.AddWithValue("$reviewBoardProgram", $"%{filter.BoardProgram}%");
        }

        if (!string.IsNullOrWhiteSpace(filter.Result) && filter.Result != "ALL")
        {
            clauses.Add("(Message LIKE $reviewResult OR Disposition LIKE $reviewResult)");
            command.Parameters.AddWithValue("$reviewResult", $"%{filter.Result}%");
        }

        return clauses.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", clauses);
    }

    private static string BuildAuditWhere(LogFilter filter, SqliteCommand command)
    {
        var clauses = new List<string>();
        AddDateRangeClauses("TimestampUtc", filter, clauses, command);

        if (!string.IsNullOrWhiteSpace(filter.OperatorId))
        {
            clauses.Add("UserId LIKE $auditUserId");
            command.Parameters.AddWithValue("$auditUserId", $"%{filter.OperatorId}%");
        }

        if (!string.IsNullOrWhiteSpace(filter.UserRole) && filter.UserRole != "ALL")
        {
            clauses.Add("UserRole = $auditUserRole");
            command.Parameters.AddWithValue("$auditUserRole", filter.UserRole);
        }

        if (!string.IsNullOrWhiteSpace(filter.ActionCategory) && filter.ActionCategory != "ALL")
        {
            clauses.Add("ActionCategory LIKE $auditActionCategory");
            command.Parameters.AddWithValue("$auditActionCategory", $"%{filter.ActionCategory}%");
        }

        if (!string.IsNullOrWhiteSpace(filter.BoardProgram))
        {
            clauses.Add("(ActionDetail LIKE $auditBoard OR RelatedPath LIKE $auditBoard OR RelatedEntityId LIKE $auditBoard)");
            command.Parameters.AddWithValue("$auditBoard", $"%{filter.BoardProgram}%");
        }

        return clauses.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", clauses);
    }

    private static void AddDateRangeClauses(string columnName, LogFilter filter, List<string> clauses, SqliteCommand command)
    {
        if (filter.FromDate is { } fromDate)
        {
            clauses.Add($"datetime({columnName}) >= datetime($fromDate)");
            command.Parameters.AddWithValue("$fromDate", fromDate.Date.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        }

        if (filter.ToDate is { } toDate)
        {
            clauses.Add($"datetime({columnName}) < datetime($toDate)");
            command.Parameters.AddWithValue("$toDate", toDate.Date.AddDays(1).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        }
    }

    private static DbHealthRow CountTable(SqliteConnection connection, string tableName, string status)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {tableName};";
        var count = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        return new DbHealthRow(tableName, count.ToString("N0", CultureInfo.InvariantCulture), status);
    }

    private static DateTime ParseDateTime(string text)
    {
        return DateTime.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed
            : DateTime.MinValue;
    }

    private static IReadOnlyList<string> DeserializeStringList(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static T DeserializeOrDefault<T>(string json, T fallback)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json) ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static void EnsureSchemaCompatibility(SqliteConnection connection)
    {
        AoiDatabaseMigrations.ApplyPending(connection);
    }

    internal static void EnsureMesSpoolQueueTable(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS MesSpoolQueue
            (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                CreatedAtUtc TEXT NOT NULL,
                LastAttemptAtUtc TEXT NULL,
                NextAttemptAtUtc TEXT NULL,
                PayloadType TEXT NOT NULL,
                PayloadJson TEXT NOT NULL,
                PayloadPath TEXT NOT NULL DEFAULT '',
                EndpointUrl TEXT NOT NULL DEFAULT '',
                RetryCount INTEGER NOT NULL DEFAULT 0,
                MaxRetryCount INTEGER NOT NULL DEFAULT 3,
                Status TEXT NOT NULL DEFAULT 'Pending',
                LastError TEXT NOT NULL DEFAULT '',
                OperatorId TEXT NOT NULL DEFAULT 'UNKNOWN',
                LotId TEXT NOT NULL DEFAULT '',
                BoardModel TEXT NOT NULL DEFAULT '',
                Result TEXT NOT NULL DEFAULT ''
            );

            CREATE INDEX IF NOT EXISTS IX_MesSpoolQueue_Status_NextAttempt ON MesSpoolQueue(Status, NextAttemptAtUtc);
            CREATE INDEX IF NOT EXISTS IX_MesSpoolQueue_CreatedAtUtc ON MesSpoolQueue(CreatedAtUtc);
            """;
        command.ExecuteNonQuery();
    }

    internal static void EnsureCentralSyncTables(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS CentralSyncQueue
            (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                CreatedAtUtc TEXT NOT NULL,
                LastAttemptAtUtc TEXT NULL,
                NextAttemptAtUtc TEXT NULL,
                ItemType TEXT NOT NULL,
                ItemId TEXT NOT NULL,
                PayloadJson TEXT NOT NULL,
                PayloadPath TEXT NOT NULL DEFAULT '',
                EndpointOrFolder TEXT NOT NULL DEFAULT '',
                StationId TEXT NOT NULL DEFAULT '',
                RetryCount INTEGER NOT NULL DEFAULT 0,
                MaxRetryCount INTEGER NOT NULL DEFAULT 5,
                Status TEXT NOT NULL DEFAULT 'Pending',
                LastError TEXT NOT NULL DEFAULT ''
            );

            CREATE TABLE IF NOT EXISTS CentralSyncAttempts
            (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                QueueId INTEGER NOT NULL,
                AttemptedAtUtc TEXT NOT NULL,
                Mode TEXT NOT NULL,
                EndpointOrFolder TEXT NOT NULL DEFAULT '',
                Status TEXT NOT NULL,
                Message TEXT NOT NULL DEFAULT '',
                FOREIGN KEY (QueueId) REFERENCES CentralSyncQueue(Id)
            );

            CREATE INDEX IF NOT EXISTS IX_CentralSyncQueue_Status_NextAttempt ON CentralSyncQueue(Status, NextAttemptAtUtc);
            CREATE INDEX IF NOT EXISTS IX_CentralSyncQueue_Item ON CentralSyncQueue(ItemType, ItemId);
            CREATE INDEX IF NOT EXISTS IX_CentralSyncAttempts_QueueId ON CentralSyncAttempts(QueueId);
            """;
        command.ExecuteNonQuery();
    }

    internal static void EnsureValidationPackagesTable(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS ValidationPackages
            (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                PackageId TEXT NOT NULL,
                PackagePath TEXT NOT NULL DEFAULT '',
                ManifestPath TEXT NOT NULL,
                AcceptanceStatus TEXT NOT NULL,
                Summary TEXT NOT NULL DEFAULT '',
                RunId INTEGER NULL,
                OperatorId TEXT NOT NULL DEFAULT 'UNKNOWN',
                AuditEventId INTEGER NULL,
                CreatedAtUtc TEXT NOT NULL,
                FOREIGN KEY (RunId) REFERENCES BatchTestRuns(Id)
            );

            CREATE INDEX IF NOT EXISTS IX_ValidationPackages_CreatedAtUtc ON ValidationPackages(CreatedAtUtc);
            CREATE INDEX IF NOT EXISTS IX_ValidationPackages_PackageId ON ValidationPackages(PackageId);
            """;
        command.ExecuteNonQuery();
    }

    internal static void EnsureTraceabilityTestReportsTable(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS TraceabilityTestReports
            (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                CreatedAtUtc TEXT NOT NULL,
                Status TEXT NOT NULL DEFAULT 'FAIL',
                Mode TEXT NOT NULL DEFAULT 'Not Connected',
                EndpointUrl TEXT NOT NULL DEFAULT '',
                ResultStatus TEXT NOT NULL DEFAULT 'FAIL',
                ImageStatus TEXT NOT NULL DEFAULT 'NOT SENT',
                PayloadPath TEXT NOT NULL DEFAULT '',
                ReportJsonPath TEXT NOT NULL DEFAULT '',
                ReportHtmlPath TEXT NOT NULL DEFAULT '',
                Message TEXT NOT NULL DEFAULT '',
                ProductionModeConfirmed INTEGER NOT NULL DEFAULT 0,
                OperatorId TEXT NOT NULL DEFAULT 'UNKNOWN',
                AuditEventId INTEGER NULL
            );

            CREATE INDEX IF NOT EXISTS IX_TraceabilityTestReports_CreatedAtUtc ON TraceabilityTestReports(CreatedAtUtc);
            """;
        command.ExecuteNonQuery();
    }

    internal static void EnsureBuildTestEvidenceTable(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS BuildTestEvidence
            (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                GeneratedAtUtc TEXT NOT NULL,
                CommitSha TEXT NOT NULL DEFAULT '',
                Configuration TEXT NOT NULL DEFAULT 'Release',
                HygieneStatus TEXT NOT NULL DEFAULT 'UNKNOWN',
                RestoreStatus TEXT NOT NULL DEFAULT 'UNKNOWN',
                BuildStatus TEXT NOT NULL DEFAULT 'UNKNOWN',
                TestStatus TEXT NOT NULL DEFAULT 'UNKNOWN',
                PublishValidationStatus TEXT NOT NULL DEFAULT 'UNKNOWN',
                EvidencePath TEXT NOT NULL DEFAULT '',
                OperatorId TEXT NOT NULL DEFAULT 'UNKNOWN',
                CreatedAtUtc TEXT NOT NULL,
                TestResultPath TEXT NOT NULL DEFAULT '',
                MachineName TEXT NOT NULL DEFAULT '',
                AuditEventId INTEGER NULL
            );

            CREATE INDEX IF NOT EXISTS IX_BuildTestEvidence_GeneratedAtUtc ON BuildTestEvidence(GeneratedAtUtc);
            """;
        command.ExecuteNonQuery();
    }

    internal static void EnsureLocalAuthenticationTables(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS LocalUsers
            (
                UserId TEXT PRIMARY KEY,
                Role TEXT NOT NULL,
                IsDisabled INTEGER NOT NULL DEFAULT 0,
                IsDeleted INTEGER NOT NULL DEFAULT 0,
                CreatedAtUtc TEXT NOT NULL,
                CreatedBy TEXT NOT NULL DEFAULT 'UNKNOWN',
                UpdatedAtUtc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS LocalUserSessions
            (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                UserId TEXT NOT NULL,
                UserRole TEXT NOT NULL,
                AuthenticationMode TEXT NOT NULL,
                LoginAtUtc TEXT NOT NULL,
                LogoutAtUtc TEXT NULL,
                Success INTEGER NOT NULL DEFAULT 0,
                Message TEXT NOT NULL DEFAULT ''
            );

            CREATE INDEX IF NOT EXISTS IX_LocalUserSessions_UserId ON LocalUserSessions(UserId);
            CREATE INDEX IF NOT EXISTS IX_LocalUserSessions_LoginAtUtc ON LocalUserSessions(LoginAtUtc);
            """;
        command.ExecuteNonQuery();
    }

    internal static void EnsureInspectionLatencyTraceTable(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS InspectionLatencyTraces
            (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                TraceId TEXT NOT NULL UNIQUE,
                CreatedAtUtc TEXT NOT NULL,
                FrameCapturedAtUtc TEXT NULL,
                FrameReceivedAtUtc TEXT NULL,
                PreprocessingStartUtc TEXT NULL,
                PreprocessingEndUtc TEXT NULL,
                InferenceStartUtc TEXT NULL,
                InferenceEndUtc TEXT NULL,
                PostprocessStartUtc TEXT NULL,
                PostprocessEndUtc TEXT NULL,
                OverlayRenderStartUtc TEXT NULL,
                OverlayRenderEndUtc TEXT NULL,
                ResultPersistStartUtc TEXT NULL,
                ResultPersistEndUtc TEXT NULL,
                TotalFrameToOverlayMs REAL NOT NULL DEFAULT 0,
                TotalFrameToSavedResultMs REAL NOT NULL DEFAULT 0,
                SourceKind TEXT NOT NULL DEFAULT '',
                Engine TEXT NOT NULL DEFAULT '',
                ModelId TEXT NOT NULL DEFAULT '',
                ImageWidth INTEGER NOT NULL DEFAULT 0,
                ImageHeight INTEGER NOT NULL DEFAULT 0,
                Verdict TEXT NOT NULL DEFAULT '',
                WarningsJson TEXT NOT NULL DEFAULT '[]'
            );

            CREATE INDEX IF NOT EXISTS IX_InspectionLatencyTraces_CreatedAtUtc ON InspectionLatencyTraces(CreatedAtUtc);
            CREATE INDEX IF NOT EXISTS IX_InspectionLatencyTraces_SourceKind ON InspectionLatencyTraces(SourceKind);
            """;
        command.ExecuteNonQuery();
        AddColumnIfMissing(connection, transaction, "InspectionLatencyTraces", "Verdict", "TEXT NOT NULL DEFAULT ''");
    }

    internal static void EnsureProfile3DAcceptanceTable(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS Profile3DAcceptanceRuns
            (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                CreatedAtUtc TEXT NOT NULL,
                SourceName TEXT NOT NULL,
                SourceKind TEXT NOT NULL DEFAULT 'None',
                IsSimulated INTEGER NOT NULL DEFAULT 1,
                Status TEXT NOT NULL DEFAULT 'FAIL',
                FactoryReadinessStatus TEXT NOT NULL DEFAULT 'NOT VALIDATED',
                AcquisitionMs REAL NOT NULL DEFAULT 0,
                Width INTEGER NOT NULL DEFAULT 0,
                Height INTEGER NOT NULL DEFAULT 0,
                Unit TEXT NOT NULL DEFAULT '',
                XPitchMicrons REAL NOT NULL DEFAULT 0,
                YPitchMicrons REAL NOT NULL DEFAULT 0,
                MissingHeightCount INTEGER NOT NULL DEFAULT 0,
                NaNHeightCount INTEGER NOT NULL DEFAULT 0,
                FrameId TEXT NOT NULL DEFAULT '',
                CriteriaJson TEXT NOT NULL DEFAULT '{}',
                DiagnosticsJson TEXT NOT NULL DEFAULT '{}',
                WarningsJson TEXT NOT NULL DEFAULT '[]',
                FailuresJson TEXT NOT NULL DEFAULT '[]',
                OperatorId TEXT NOT NULL DEFAULT 'UNKNOWN',
                AuditEventId INTEGER NULL
            );

            CREATE INDEX IF NOT EXISTS IX_Profile3DAcceptanceRuns_CreatedAtUtc ON Profile3DAcceptanceRuns(CreatedAtUtc);
            """;
        command.ExecuteNonQuery();
    }

    internal static void EnsureFalseCallReductionTables(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS FalseCallReductionRuns
            (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                BatchRunId INTEGER NULL,
                CreatedAtUtc TEXT NOT NULL,
                EngineName TEXT NOT NULL,
                ModelVersion TEXT NOT NULL DEFAULT 'UNKNOWN',
                ModelId TEXT NOT NULL DEFAULT '',
                ModelSha256 TEXT NOT NULL DEFAULT '',
                CriteriaJson TEXT NOT NULL DEFAULT '{}',
                RecommendationStatus TEXT NOT NULL DEFAULT 'INVALID',
                RecommendationMode TEXT NOT NULL DEFAULT 'Balanced',
                SelectedThreshold REAL NULL,
                SelectedFalseCallRate REAL NULL,
                SelectedPossibleEscapeRate REAL NULL,
                SelectedReviewRate REAL NULL,
                SelectedManualReviewMinutes REAL NULL,
                SelectedPossibleEscapeCount INTEGER NULL,
                RecommendationMessagesJson TEXT NOT NULL DEFAULT '[]',
                OperatorId TEXT NOT NULL DEFAULT 'UNKNOWN',
                AuditEventId INTEGER NULL,
                FOREIGN KEY (BatchRunId) REFERENCES BatchTestRuns(Id)
            );

            CREATE TABLE IF NOT EXISTS FalseCallReductionPoints
            (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                RunId INTEGER NOT NULL,
                ConfidenceThreshold REAL NOT NULL,
                DifferenceThreshold REAL NOT NULL,
                TruePositive INTEGER NOT NULL,
                TrueNegative INTEGER NOT NULL,
                FalsePositive INTEGER NOT NULL,
                FalseNegative INTEGER NOT NULL,
                Precision REAL NOT NULL,
                Recall REAL NOT NULL,
                FalseCallRate REAL NOT NULL,
                PossibleEscapeRate REAL NOT NULL,
                ReviewRate REAL NOT NULL,
                NgRate REAL NOT NULL,
                ReviewCount INTEGER NOT NULL,
                NgCount INTEGER NOT NULL,
                EstimatedManualReviewMinutes REAL NOT NULL,
                MeetsConstraints INTEGER NOT NULL DEFAULT 0,
                Status TEXT NOT NULL DEFAULT 'CONDITIONAL',
                FOREIGN KEY (RunId) REFERENCES FalseCallReductionRuns(Id)
            );

            CREATE INDEX IF NOT EXISTS IX_FalseCallReductionRuns_CreatedAtUtc ON FalseCallReductionRuns(CreatedAtUtc);
            CREATE INDEX IF NOT EXISTS IX_FalseCallReductionRuns_BatchRunId ON FalseCallReductionRuns(BatchRunId);
            CREATE INDEX IF NOT EXISTS IX_FalseCallReductionPoints_RunId ON FalseCallReductionPoints(RunId);
            """;
        command.ExecuteNonQuery();
    }

    internal static void EnsureValidationBreakdownMetricsTable(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS ValidationBreakdownMetrics
            (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                RunId INTEGER NOT NULL,
                BreakdownType TEXT NOT NULL,
                Key TEXT NOT NULL,
                DisplayName TEXT NOT NULL DEFAULT '',
                Total INTEGER NOT NULL DEFAULT 0,
                TruePositive INTEGER NOT NULL DEFAULT 0,
                TrueNegative INTEGER NOT NULL DEFAULT 0,
                FalsePositive INTEGER NOT NULL DEFAULT 0,
                FalseNegative INTEGER NOT NULL DEFAULT 0,
                WrongDefectClass INTEGER NOT NULL DEFAULT 0,
                WrongSide INTEGER NOT NULL DEFAULT 0,
                UnknownGroundTruth INTEGER NOT NULL DEFAULT 0,
                Precision REAL NOT NULL DEFAULT 0,
                Recall REAL NOT NULL DEFAULT 0,
                FalseCallRate REAL NOT NULL DEFAULT 0,
                CreatedAtUtc TEXT NOT NULL,
                FOREIGN KEY (RunId) REFERENCES BatchTestRuns(Id)
            );

            CREATE INDEX IF NOT EXISTS IX_ValidationBreakdownMetrics_RunId ON ValidationBreakdownMetrics(RunId);
            CREATE INDEX IF NOT EXISTS IX_ValidationBreakdownMetrics_Type ON ValidationBreakdownMetrics(BreakdownType, Key);
            """;
        command.ExecuteNonQuery();
    }

    internal static void EnsureThresholdProfileTables(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS ThresholdProfiles
            (
                ProfileId TEXT NOT NULL,
                Revision TEXT NOT NULL,
                BoardModel TEXT NOT NULL DEFAULT 'ANY',
                BoardProgram TEXT NOT NULL DEFAULT 'ANY',
                RecipeName TEXT NOT NULL DEFAULT 'ANY',
                RecipeRevision TEXT NOT NULL DEFAULT 'ANY',
                Status TEXT NOT NULL DEFAULT 'Draft',
                SourceValidationRunId INTEGER NULL,
                SourceFalseCallReductionRunId INTEGER NULL,
                CreatedBy TEXT NOT NULL DEFAULT 'UNKNOWN',
                CreatedAtUtc TEXT NOT NULL,
                ApprovedBy TEXT NULL,
                ApprovedAtUtc TEXT NULL,
                PRIMARY KEY (ProfileId, Revision)
            );

            CREATE TABLE IF NOT EXISTS ThresholdProfileRules
            (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ProfileId TEXT NOT NULL,
                Revision TEXT NOT NULL,
                ViewType TEXT NOT NULL DEFAULT 'Any',
                RoiType TEXT NOT NULL DEFAULT 'Any',
                DefectClass TEXT NOT NULL DEFAULT 'Any',
                ReviewThreshold REAL NOT NULL,
                NgThreshold REAL NOT NULL,
                ConfidenceThreshold REAL NOT NULL DEFAULT 0.65,
                MinimumAreaPixels REAL NOT NULL DEFAULT 0,
                MaxAllowedFalseCallRate REAL NOT NULL DEFAULT 1,
                FOREIGN KEY (ProfileId, Revision) REFERENCES ThresholdProfiles(ProfileId, Revision)
            );

            CREATE TABLE IF NOT EXISTS ThresholdProfileDeployments
            (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ProfileId TEXT NOT NULL,
                Revision TEXT NOT NULL,
                BoardModel TEXT NOT NULL DEFAULT 'ANY',
                BoardProgram TEXT NOT NULL DEFAULT 'ANY',
                RecipeName TEXT NOT NULL DEFAULT 'ANY',
                DeployedAtUtc TEXT NOT NULL,
                DeployedBy TEXT NOT NULL DEFAULT 'UNKNOWN',
                IsActive INTEGER NOT NULL DEFAULT 1,
                FOREIGN KEY (ProfileId, Revision) REFERENCES ThresholdProfiles(ProfileId, Revision)
            );

            CREATE INDEX IF NOT EXISTS IX_ThresholdProfiles_Status ON ThresholdProfiles(Status);
            CREATE INDEX IF NOT EXISTS IX_ThresholdProfileRules_Profile ON ThresholdProfileRules(ProfileId, Revision);
            CREATE INDEX IF NOT EXISTS IX_ThresholdProfileDeployments_Active ON ThresholdProfileDeployments(BoardModel, BoardProgram, RecipeName, IsActive);
            """;
        command.ExecuteNonQuery();
    }

    internal static void EnsureCameraAcceptanceTables(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS CameraAcceptanceRuns
            (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                CreatedAtUtc TEXT NOT NULL,
                AdapterName TEXT NOT NULL,
                SourceKey TEXT NOT NULL,
                SettingsSummary TEXT NOT NULL,
                CriteriaJson TEXT NOT NULL,
                Status TEXT NOT NULL,
                FactoryReadinessStatus TEXT NOT NULL,
                IsRealHardware INTEGER NOT NULL DEFAULT 0,
                TotalRequestedFrames INTEGER NOT NULL DEFAULT 0,
                TotalReceivedFrames INTEGER NOT NULL DEFAULT 0,
                DroppedFrameCount INTEGER NOT NULL DEFAULT 0,
                TriggerFailureCount INTEGER NOT NULL DEFAULT 0,
                TimeoutCount INTEGER NOT NULL DEFAULT 0,
                MaxConnectMs REAL NOT NULL DEFAULT 0,
                MaxFirstFrameMs REAL NOT NULL DEFAULT 0,
                AverageFrameIntervalMs REAL NOT NULL DEFAULT 0,
                WarningsJson TEXT NOT NULL DEFAULT '[]',
                FailuresJson TEXT NOT NULL DEFAULT '[]',
                ViewMetricsJson TEXT NOT NULL DEFAULT '[]',
                OperatorId TEXT NOT NULL DEFAULT 'UNKNOWN',
                AuditEventId INTEGER NULL
            );

            CREATE TABLE IF NOT EXISTS CameraAcceptanceFrames
            (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                RunId INTEGER NOT NULL,
                ViewType TEXT NOT NULL,
                Sequence INTEGER NOT NULL,
                FrameId TEXT NOT NULL,
                CameraId TEXT NOT NULL,
                CapturedAtUtc TEXT NOT NULL,
                Width INTEGER NOT NULL DEFAULT 0,
                Height INTEGER NOT NULL DEFAULT 0,
                PixelFormat TEXT NOT NULL DEFAULT '',
                SourceKind TEXT NOT NULL DEFAULT '',
                IsSimulated INTEGER NOT NULL DEFAULT 0,
                LatencyMs REAL NOT NULL DEFAULT 0,
                IntervalMs REAL NOT NULL DEFAULT 0,
                MetadataValid INTEGER NOT NULL DEFAULT 0,
                Message TEXT NOT NULL DEFAULT '',
                FOREIGN KEY (RunId) REFERENCES CameraAcceptanceRuns(Id)
            );

            CREATE INDEX IF NOT EXISTS IX_CameraAcceptanceRuns_CreatedAtUtc ON CameraAcceptanceRuns(CreatedAtUtc);
            CREATE INDEX IF NOT EXISTS IX_CameraAcceptanceRuns_RealHardware ON CameraAcceptanceRuns(IsRealHardware, CreatedAtUtc);
            CREATE INDEX IF NOT EXISTS IX_CameraAcceptanceFrames_RunId ON CameraAcceptanceFrames(RunId);
            """;
        command.ExecuteNonQuery();
    }

    internal static void EnsureLightingAcceptanceTables(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS LightingAcceptanceRuns
            (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                CreatedAtUtc TEXT NOT NULL,
                ControllerName TEXT NOT NULL,
                Mode TEXT NOT NULL,
                SettingsSummary TEXT NOT NULL,
                CriteriaJson TEXT NOT NULL,
                Status TEXT NOT NULL,
                IsSimulated INTEGER NOT NULL DEFAULT 0,
                StepCount INTEGER NOT NULL DEFAULT 0,
                PassedStepCount INTEGER NOT NULL DEFAULT 0,
                FailedStepCount INTEGER NOT NULL DEFAULT 0,
                MaxCommandLatencyMs REAL NOT NULL DEFAULT 0,
                MaxTriggerToFrameLatencyMs REAL NOT NULL DEFAULT 0,
                WarningsJson TEXT NOT NULL DEFAULT '[]',
                FailuresJson TEXT NOT NULL DEFAULT '[]',
                OperatorId TEXT NOT NULL DEFAULT 'UNKNOWN',
                AuditEventId INTEGER NULL
            );

            CREATE TABLE IF NOT EXISTS LightingAcceptanceSteps
            (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                RunId INTEGER NOT NULL,
                ViewType TEXT NOT NULL,
                ProgramName TEXT NOT NULL,
                CommandText TEXT NOT NULL,
                CommandLatencyMs REAL NOT NULL DEFAULT 0,
                TriggerToFrameLatencyMs REAL NOT NULL DEFAULT 0,
                CommandAccepted INTEGER NOT NULL DEFAULT 0,
                FrameReceived INTEGER NOT NULL DEFAULT 0,
                FrameId TEXT NOT NULL DEFAULT '',
                CameraId TEXT NOT NULL DEFAULT '',
                Status TEXT NOT NULL,
                Message TEXT NOT NULL DEFAULT '',
                FOREIGN KEY (RunId) REFERENCES LightingAcceptanceRuns(Id)
            );

            CREATE INDEX IF NOT EXISTS IX_LightingAcceptanceRuns_CreatedAtUtc ON LightingAcceptanceRuns(CreatedAtUtc);
            CREATE INDEX IF NOT EXISTS IX_LightingAcceptanceSteps_RunId ON LightingAcceptanceSteps(RunId);
            """;
        command.ExecuteNonQuery();
    }

    internal static void EnsureRobotAcceptanceTables(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS RobotAcceptanceRuns
            (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                CreatedAtUtc TEXT NOT NULL,
                ControllerName TEXT NOT NULL,
                EmergencyStopName TEXT NOT NULL,
                SafetyControllerName TEXT NOT NULL DEFAULT '',
                SafetySourceKind TEXT NOT NULL DEFAULT 'NotConnected',
                SourceKind TEXT NOT NULL,
                CriteriaJson TEXT NOT NULL,
                Status TEXT NOT NULL,
                FinalState TEXT NOT NULL,
                LoadMs REAL NOT NULL DEFAULT 0,
                MoveToInspectMs REAL NOT NULL DEFAULT 0,
                InspectionMs REAL NOT NULL DEFAULT 0,
                UnloadMs REAL NOT NULL DEFAULT 0,
                FullCycleMs REAL NOT NULL DEFAULT 0,
                InvalidTransitionRejected INTEGER NOT NULL DEFAULT 0,
                EmergencyStopBlocked INTEGER NOT NULL DEFAULT 0,
                SafetyFaultBlocked INTEGER NOT NULL DEFAULT 0,
                ResetReturnedIdle INTEGER NOT NULL DEFAULT 0,
                AuditEventCount INTEGER NOT NULL DEFAULT 0,
                WarningsJson TEXT NOT NULL DEFAULT '[]',
                FailuresJson TEXT NOT NULL DEFAULT '[]',
                OperatorId TEXT NOT NULL DEFAULT 'UNKNOWN',
                AuditEventId INTEGER NULL
            );

            CREATE TABLE IF NOT EXISTS RobotAcceptanceSteps
            (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                RunId INTEGER NOT NULL,
                StepName TEXT NOT NULL,
                FromState TEXT NOT NULL,
                ToState TEXT NOT NULL,
                ElapsedMs REAL NOT NULL DEFAULT 0,
                Accepted INTEGER NOT NULL DEFAULT 0,
                Status TEXT NOT NULL,
                Message TEXT NOT NULL DEFAULT '',
                FOREIGN KEY (RunId) REFERENCES RobotAcceptanceRuns(Id)
            );

            CREATE INDEX IF NOT EXISTS IX_RobotAcceptanceRuns_CreatedAtUtc ON RobotAcceptanceRuns(CreatedAtUtc);
            CREATE INDEX IF NOT EXISTS IX_RobotAcceptanceSteps_RunId ON RobotAcceptanceSteps(RunId);
            """;
        command.ExecuteNonQuery();
    }

    internal static void EnsureSoakTestTables(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS SoakTestRuns
            (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                RunId TEXT NOT NULL,
                StartedAtUtc TEXT NOT NULL,
                EndedAtUtc TEXT NOT NULL,
                ImageFolder TEXT NOT NULL,
                OutputFolder TEXT NOT NULL,
                EngineKey TEXT NOT NULL,
                EngineName TEXT NOT NULL,
                EngineVersion TEXT NOT NULL,
                SourceKind TEXT NOT NULL DEFAULT 'Simulated source',
                IsRealCameraSource INTEGER NOT NULL DEFAULT 0,
                ProfileName TEXT NOT NULL DEFAULT 'Custom',
                RequestedDurationSeconds REAL NOT NULL DEFAULT 0,
                ActualDurationSeconds REAL NOT NULL DEFAULT 0,
                DelayBetweenInspectionsMs REAL NOT NULL DEFAULT 0,
                OperatorId TEXT NOT NULL DEFAULT 'UNKNOWN',
                BoardModel TEXT NOT NULL DEFAULT 'UNKNOWN',
                LotId TEXT NOT NULL DEFAULT 'SOAK-TEST',
                WasCanceled INTEGER NOT NULL DEFAULT 0,
                TotalCycles INTEGER NOT NULL DEFAULT 0,
                SuccessfulCycles INTEGER NOT NULL DEFAULT 0,
                FailedCycles INTEGER NOT NULL DEFAULT 0,
                AverageInspectionMs REAL NOT NULL DEFAULT 0,
                MinInspectionMs REAL NOT NULL DEFAULT 0,
                MaxInspectionMs REAL NOT NULL DEFAULT 0,
                P95InspectionMs REAL NOT NULL DEFAULT 0,
                CountOverOneSecond INTEGER NOT NULL DEFAULT 0,
                StartManagedMemoryMb REAL NOT NULL DEFAULT 0,
                EndManagedMemoryMb REAL NOT NULL DEFAULT 0,
                StartWorkingSetMb REAL NOT NULL DEFAULT 0,
                EndWorkingSetMb REAL NOT NULL DEFAULT 0,
                PeakWorkingSetMb REAL NOT NULL DEFAULT 0,
                IsCompletedFactoryEvidence INTEGER NOT NULL DEFAULT 0,
                ErrorsJson TEXT NOT NULL DEFAULT '[]',
                AverageTotalCycleMs REAL NOT NULL DEFAULT 0,
                MaxTotalCycleMs REAL NOT NULL DEFAULT 0,
                P95TotalCycleMs REAL NOT NULL DEFAULT 0,
                CancellationReason TEXT NOT NULL DEFAULT '',
                FirstCriticalError TEXT NOT NULL DEFAULT '',
                MemoryWarningsJson TEXT NOT NULL DEFAULT '[]',
                AuditEventId INTEGER NULL
            );

            CREATE TABLE IF NOT EXISTS SoakTestIterations
            (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                RunId INTEGER NOT NULL,
                CycleNumber INTEGER NOT NULL,
                TimestampUtc TEXT NOT NULL,
                FrameId TEXT NOT NULL,
                ImagePath TEXT NOT NULL,
                EngineName TEXT NOT NULL,
                Verdict TEXT NOT NULL,
                TotalInspectionMs REAL NOT NULL DEFAULT 0,
                WorkingSetMb REAL NOT NULL DEFAULT 0,
                Success INTEGER NOT NULL DEFAULT 0,
                Message TEXT NOT NULL DEFAULT '',
                Error TEXT NOT NULL DEFAULT '',
                TotalCycleMs REAL NOT NULL DEFAULT 0,
                ExceptionCategory TEXT NOT NULL DEFAULT '',
                FOREIGN KEY (RunId) REFERENCES SoakTestRuns(Id)
            );

            CREATE INDEX IF NOT EXISTS IX_SoakTestRuns_StartedAtUtc ON SoakTestRuns(StartedAtUtc);
            CREATE INDEX IF NOT EXISTS IX_SoakTestRuns_FactoryEvidence ON SoakTestRuns(IsCompletedFactoryEvidence, StartedAtUtc);
            CREATE INDEX IF NOT EXISTS IX_SoakTestIterations_RunId ON SoakTestIterations(RunId);
            """;
        command.ExecuteNonQuery();
    }

    internal static void EnsureModelRegistryTable(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS ModelRegistry
            (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ModelId TEXT NOT NULL UNIQUE,
                DisplayName TEXT NOT NULL,
                Version TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                RegisteredAtUtc TEXT NOT NULL,
                SourceFileName TEXT NOT NULL,
                StoredModelPath TEXT NOT NULL,
                StoredLabelMapPath TEXT NOT NULL DEFAULT '',
                MetadataPath TEXT NOT NULL DEFAULT '',
                Sha256 TEXT NOT NULL,
                InputTensorName TEXT NOT NULL DEFAULT '',
                OutputTensorName TEXT NOT NULL DEFAULT '',
                InputWidth INTEGER NOT NULL DEFAULT 640,
                InputHeight INTEGER NOT NULL DEFAULT 640,
                ConfidenceThreshold REAL NOT NULL DEFAULT 0.65,
                LabelsJson TEXT NOT NULL DEFAULT '[]',
                ValidationStatus TEXT NOT NULL DEFAULT 'NotTested',
                LastValidatedAtUtc TEXT NULL,
                ValidationMessage TEXT NOT NULL DEFAULT '',
                Notes TEXT NOT NULL DEFAULT '',
                IsActive INTEGER NOT NULL DEFAULT 0,
                AuditEventId INTEGER NULL,
                LifecycleState TEXT NOT NULL DEFAULT 'Registered',
                LatestAcceptanceStatus TEXT NOT NULL DEFAULT '',
                LatestAcceptanceRunId INTEGER NULL,
                LatestReleasePackageId INTEGER NULL,
                LatestReleasePackagePath TEXT NOT NULL DEFAULT '',
                DeploymentWaiverReason TEXT NOT NULL DEFAULT '',
                WaiverExpiresAtUtc TEXT NULL,
                DeploymentWaivedBy TEXT NOT NULL DEFAULT '',
                DeploymentWaivedAtUtc TEXT NULL,
                DeployedAtUtc TEXT NULL,
                RetiredReason TEXT NOT NULL DEFAULT '',
                RetiredAtUtc TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_ModelRegistry_ModelId ON ModelRegistry(ModelId);
            CREATE INDEX IF NOT EXISTS IX_ModelRegistry_IsActive ON ModelRegistry(IsActive);
            CREATE INDEX IF NOT EXISTS IX_ModelRegistry_RegisteredAtUtc ON ModelRegistry(RegisteredAtUtc);
            """;
        command.ExecuteNonQuery();
        AddModelLifecycleColumns(connection, transaction);
    }

    private static void AddModelLifecycleColumns(SqliteConnection connection, SqliteTransaction? transaction)
    {
        AddColumnIfMissing(connection, transaction, "ModelRegistry", "LifecycleState", "TEXT NOT NULL DEFAULT 'Registered'");
        AddColumnIfMissing(connection, transaction, "ModelRegistry", "LatestAcceptanceStatus", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(connection, transaction, "ModelRegistry", "LatestAcceptanceRunId", "INTEGER NULL");
        AddColumnIfMissing(connection, transaction, "ModelRegistry", "LatestReleasePackageId", "INTEGER NULL");
        AddColumnIfMissing(connection, transaction, "ModelRegistry", "LatestReleasePackagePath", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(connection, transaction, "ModelRegistry", "DeploymentWaiverReason", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(connection, transaction, "ModelRegistry", "WaiverExpiresAtUtc", "TEXT NULL");
        AddColumnIfMissing(connection, transaction, "ModelRegistry", "DeploymentWaivedBy", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(connection, transaction, "ModelRegistry", "DeploymentWaivedAtUtc", "TEXT NULL");
        AddColumnIfMissing(connection, transaction, "ModelRegistry", "DeployedAtUtc", "TEXT NULL");
        AddColumnIfMissing(connection, transaction, "ModelRegistry", "RetiredReason", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(connection, transaction, "ModelRegistry", "RetiredAtUtc", "TEXT NULL");
    }

    internal static void EnsureModelAcceptanceTables(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS ModelAcceptanceRuns
            (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                CreatedAtUtc TEXT NOT NULL,
                ModelId TEXT NOT NULL DEFAULT '',
                ModelVersion TEXT NOT NULL DEFAULT '',
                ModelSha256 TEXT NOT NULL DEFAULT '',
                ModelPath TEXT NOT NULL DEFAULT '',
                LabelMapPath TEXT NOT NULL DEFAULT '',
                InputTensorName TEXT NOT NULL DEFAULT '',
                OutputTensorName TEXT NOT NULL DEFAULT '',
                OutputShape TEXT NOT NULL DEFAULT '',
                DatasetFolder TEXT NOT NULL DEFAULT '',
                DatasetName TEXT NOT NULL DEFAULT '',
                GroundTruthCsvPath TEXT NOT NULL DEFAULT '',
                IsFormalManifest INTEGER NOT NULL DEFAULT 0,
                Status TEXT NOT NULL DEFAULT 'FAIL',
                OperatorId TEXT NOT NULL DEFAULT 'UNKNOWN',
                ApprovedBy TEXT NOT NULL DEFAULT '',
                ApprovedAtUtc TEXT NULL,
                IsProductionCandidate INTEGER NOT NULL DEFAULT 0,
                CriteriaJson TEXT NOT NULL DEFAULT '{}',
                MetricsJson TEXT NOT NULL DEFAULT '{}',
                DatasetQualityJson TEXT NOT NULL DEFAULT '{}',
                FalseCallRecommendationJson TEXT NOT NULL DEFAULT '{}',
                BreakdownJson TEXT NOT NULL DEFAULT '{}',
                PerformanceJson TEXT NOT NULL DEFAULT '{}',
                P95InferenceMs REAL NOT NULL DEFAULT 0,
                MessagesJson TEXT NOT NULL DEFAULT '[]',
                LimitationsJson TEXT NOT NULL DEFAULT '[]',
                AuditEventId INTEGER NULL
            );

            CREATE TABLE IF NOT EXISTS ModelAcceptanceMetrics
            (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                RunId INTEGER NOT NULL,
                MetricName TEXT NOT NULL,
                MetricValue REAL NOT NULL DEFAULT 0,
                MetricText TEXT NOT NULL DEFAULT '',
                FOREIGN KEY (RunId) REFERENCES ModelAcceptanceRuns(Id)
            );

            CREATE TABLE IF NOT EXISTS ModelReleasePackages
            (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                CreatedAtUtc TEXT NOT NULL,
                AcceptanceRunId INTEGER NOT NULL,
                ModelId TEXT NOT NULL,
                ModelVersion TEXT NOT NULL,
                ModelSha256 TEXT NOT NULL,
                PackagePath TEXT NOT NULL,
                ManifestPath TEXT NOT NULL,
                ReportPath TEXT NOT NULL,
                Status TEXT NOT NULL,
                ApprovedBy TEXT NOT NULL DEFAULT '',
                AuditEventId INTEGER NULL,
                FOREIGN KEY (AcceptanceRunId) REFERENCES ModelAcceptanceRuns(Id)
            );

            CREATE INDEX IF NOT EXISTS IX_ModelAcceptanceRuns_Model_Status ON ModelAcceptanceRuns(ModelId, Status, IsProductionCandidate);
            CREATE INDEX IF NOT EXISTS IX_ModelAcceptanceRuns_CreatedAtUtc ON ModelAcceptanceRuns(CreatedAtUtc);
            CREATE INDEX IF NOT EXISTS IX_ModelAcceptanceMetrics_RunId ON ModelAcceptanceMetrics(RunId);
            CREATE INDEX IF NOT EXISTS IX_ModelReleasePackages_Model ON ModelReleasePackages(ModelId, CreatedAtUtc);
            """;
        command.ExecuteNonQuery();
    }

    internal static void EnsureExportVerificationTable(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS ExportVerification
            (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ExportHistoryId INTEGER NULL,
                CheckedAtUtc TEXT NOT NULL,
                ExportType TEXT NOT NULL,
                ExportPath TEXT NOT NULL,
                Status TEXT NOT NULL,
                Sha256 TEXT NOT NULL DEFAULT '',
                SizeBytes INTEGER NOT NULL DEFAULT 0,
                MessagesJson TEXT NOT NULL DEFAULT '[]',
                ArtifactChecksumsJson TEXT NOT NULL DEFAULT '{}',
                FOREIGN KEY (ExportHistoryId) REFERENCES ExportHistory(Id)
            );

            CREATE INDEX IF NOT EXISTS IX_ExportVerification_ExportHistoryId ON ExportVerification(ExportHistoryId);
            CREATE INDEX IF NOT EXISTS IX_ExportVerification_CheckedAtUtc ON ExportVerification(CheckedAtUtc);
            CREATE INDEX IF NOT EXISTS IX_ExportVerification_Status ON ExportVerification(Status);
            """;
        command.ExecuteNonQuery();
    }

    private static void AutoArchiveOldLogs(SqliteConnection connection)
    {
        var cutoff = DateTime.UtcNow.AddDays(-30).ToString("O", CultureInfo.InvariantCulture);
        var archivedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        CopyArchiveRows(
            connection,
            "InspectionResults",
            "CreatedAtUtc",
            cutoff,
            archivedAt,
            "Auto archive copy-only: source inspection result remains in InspectionResults.");

        CopyArchiveRows(
            connection,
            "ReviewEvents",
            "EventTimeUtc",
            cutoff,
            archivedAt,
            "Auto archive copy-only: source review event remains in ReviewEvents.");

        CopyArchiveRows(
            connection,
            "AuditEvents",
            "TimestampUtc",
            cutoff,
            archivedAt,
            "Auto archive copy-only: source audit event remains in AuditEvents.");

        CopyArchiveRows(
            connection,
            "ExportHistory",
            "CreatedAtUtc",
            cutoff,
            archivedAt,
            "Auto archive copy-only: source export row remains in ExportHistory.");
    }

    private static void CopyArchiveRows(
        SqliteConnection connection,
        string sourceTable,
        string dateColumn,
        string cutoffUtc,
        string archivedAtUtc,
        string notes)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            INSERT OR IGNORE INTO LogArchive (SourceTable, SourceId, SourceTimestampUtc, ArchivedAtUtc, Notes)
            SELECT '{sourceTable}', Id, {dateColumn}, $archivedAtUtc, $notes
            FROM {sourceTable}
            WHERE datetime({dateColumn}) < datetime($cutoffUtc);
            """;
        command.Parameters.AddWithValue("$archivedAtUtc", archivedAtUtc);
        command.Parameters.AddWithValue("$notes", notes);
        command.Parameters.AddWithValue("$cutoffUtc", cutoffUtc);
        command.ExecuteNonQuery();
    }

    public static void AddColumnIfMissing(SqliteConnection connection, string tableName, string columnName, string columnDefinition)
        => AddColumnIfMissing(connection, null, tableName, columnName, columnDefinition);

    internal static void AddColumnIfMissing(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string tableName,
        string columnName,
        string columnDefinition)
    {
        if (!TableExists(connection, transaction, tableName) || ColumnExists(connection, transaction, tableName, columnName))
            return;

        using var alterCommand = connection.CreateCommand();
        alterCommand.Transaction = transaction;
        alterCommand.CommandText =
            $"ALTER TABLE {QuoteIdentifier(tableName)} ADD COLUMN {QuoteIdentifier(columnName)} {columnDefinition};";
        alterCommand.ExecuteNonQuery();
    }

    private static string? NullIfWhiteSpace(string? text)
        => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static (string? UserId, string? Role) SplitOperatorWithRole(string? operatorWithRole)
    {
        if (string.IsNullOrWhiteSpace(operatorWithRole))
            return (null, null);

        var trimmed = operatorWithRole.Trim();
        var role = ExtractRole(trimmed);
        var bracketIndex = trimmed.IndexOf('[', StringComparison.Ordinal);
        var userId = bracketIndex > 0 ? trimmed[..bracketIndex].Trim() : trimmed;
        return (string.IsNullOrWhiteSpace(userId) ? null : userId, role);
    }

    private static string? ExtractRole(string? operatorWithRole)
    {
        if (string.IsNullOrWhiteSpace(operatorWithRole))
            return null;

        var start = operatorWithRole.LastIndexOf("[", StringComparison.Ordinal);
        var end = operatorWithRole.LastIndexOf("]", StringComparison.Ordinal);
        if (start < 0 || end <= start)
            return null;

        var role = operatorWithRole.Substring(start + 1, end - start - 1).Trim();
        return string.IsNullOrWhiteSpace(role) ? null : role;
    }

    private static string ToDefectSeverity(string judgmentStatus)
    {
        return judgmentStatus.ToUpperInvariant() switch
        {
            "NG" => "Major",
            "OK" => "Info",
            _ => "Review",
        };
    }

    private static string MakeVaultFileName(DateTime timestampUtc, string viewType, string originalName)
    {
        var extension = Path.GetExtension(originalName);
        var stem = Path.GetFileNameWithoutExtension(originalName);
        var safeStem = string.Join("_", stem.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        var safeViewType = string.Join("_", viewType.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        return $"{timestampUtc:yyyyMMdd_HHmmssfff}_{safeViewType}_{safeStem}{extension}";
    }

    private static bool IsUnderDirectory(string path, string directory)
    {
        var fullPath = Path.GetFullPath(path);
        var fullDirectory = Path.GetFullPath(directory);
        return fullPath.StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private const string SchemaSql =
        """
        PRAGMA journal_mode = WAL;
        PRAGMA foreign_keys = ON;

        CREATE TABLE IF NOT EXISTS SchemaInfo
        (
            Key TEXT PRIMARY KEY,
            Value TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS Images
        (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            OriginalPath TEXT NOT NULL,
            VaultPath TEXT NOT NULL,
            FileName TEXT NOT NULL,
            BoardModel TEXT NOT NULL,
            LotId TEXT NOT NULL,
            ViewType TEXT NOT NULL,
            ImportedAtUtc TEXT NOT NULL,
            FileHash TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS InspectionResults
        (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            SampleImagePath TEXT NOT NULL,
            GoldenImagePath TEXT NULL,
            BoardProgram TEXT NOT NULL DEFAULT 'UNKNOWN',
            OperatorId TEXT NOT NULL DEFAULT 'UNKNOWN',
            InspectionEngine TEXT NOT NULL DEFAULT 'Pixel Difference Prototype Engine',
            DifferenceScore REAL NOT NULL,
            MeanBrightness REAL NOT NULL,
            Verdict TEXT NOT NULL,
            Confidence REAL NOT NULL,
            SuggestedDefect TEXT NOT NULL,
            PolicyName TEXT NOT NULL,
            ModelVersion TEXT NOT NULL,
            ModelFilePath TEXT NOT NULL DEFAULT '',
            ConfidenceThreshold REAL NOT NULL DEFAULT 0,
            ThresholdProfileId TEXT NOT NULL DEFAULT '',
            ThresholdProfileRevision TEXT NOT NULL DEFAULT '',
            ThresholdSource TEXT NOT NULL DEFAULT 'Built-in policy default',
            DecisionReason TEXT NOT NULL,
            HotspotX REAL NOT NULL,
            HotspotY REAL NOT NULL,
            HotspotWidth REAL NOT NULL,
            HotspotHeight REAL NOT NULL,
            ImageLoadMs REAL NOT NULL DEFAULT 0,
            PreprocessingMs REAL NOT NULL DEFAULT 0,
            InferenceMs REAL NOT NULL DEFAULT 0,
            OverlayRenderingMs REAL NOT NULL DEFAULT 0,
            TotalInspectionMs REAL NOT NULL DEFAULT 0,
            CreatedAtUtc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS InspectionLatencyTraces
        (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            TraceId TEXT NOT NULL UNIQUE,
            CreatedAtUtc TEXT NOT NULL,
            FrameCapturedAtUtc TEXT NULL,
            FrameReceivedAtUtc TEXT NULL,
            PreprocessingStartUtc TEXT NULL,
            PreprocessingEndUtc TEXT NULL,
            InferenceStartUtc TEXT NULL,
            InferenceEndUtc TEXT NULL,
            PostprocessStartUtc TEXT NULL,
            PostprocessEndUtc TEXT NULL,
            OverlayRenderStartUtc TEXT NULL,
            OverlayRenderEndUtc TEXT NULL,
            ResultPersistStartUtc TEXT NULL,
            ResultPersistEndUtc TEXT NULL,
            TotalFrameToOverlayMs REAL NOT NULL DEFAULT 0,
            TotalFrameToSavedResultMs REAL NOT NULL DEFAULT 0,
            SourceKind TEXT NOT NULL DEFAULT '',
            Engine TEXT NOT NULL DEFAULT '',
            ModelId TEXT NOT NULL DEFAULT '',
            ImageWidth INTEGER NOT NULL DEFAULT 0,
            ImageHeight INTEGER NOT NULL DEFAULT 0,
            Verdict TEXT NOT NULL DEFAULT '',
            WarningsJson TEXT NOT NULL DEFAULT '[]'
        );

        CREATE TABLE IF NOT EXISTS Defects
        (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            InspectionResultId INTEGER NULL,
            ImageId INTEGER NULL,
            RefDes TEXT NULL,
            DefectType TEXT NOT NULL,
            Severity TEXT NOT NULL,
            Confidence REAL NOT NULL DEFAULT 0,
            RoiX REAL NULL,
            RoiY REAL NULL,
            RoiWidth REAL NULL,
            RoiHeight REAL NULL,
            XPosition REAL NULL,
            YPosition REAL NULL,
            SideOrViewType TEXT NOT NULL DEFAULT 'sample',
            RoiId TEXT NOT NULL DEFAULT 'ROI-UNASSIGNED',
            JudgmentStatus TEXT NOT NULL DEFAULT 'REVIEW',
            CreatedAtUtc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
            FOREIGN KEY (InspectionResultId) REFERENCES InspectionResults(Id),
            FOREIGN KEY (ImageId) REFERENCES Images(Id)
        );

        CREATE TABLE IF NOT EXISTS ReviewEvents
        (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Category TEXT NOT NULL,
            Message TEXT NOT NULL,
            Disposition TEXT NULL,
            OperatorId TEXT NULL,
            EventTimeUtc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS AuditEvents
        (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            TimestampUtc TEXT NOT NULL,
            LocalTimestamp TEXT NOT NULL,
            UserId TEXT NOT NULL DEFAULT 'UNKNOWN',
            UserRole TEXT NOT NULL DEFAULT 'UNKNOWN',
            StationId TEXT NOT NULL DEFAULT 'UNKNOWN',
            ActionCategory TEXT NOT NULL,
            ActionDetail TEXT NOT NULL,
            RelatedEntityType TEXT NOT NULL DEFAULT '',
            RelatedEntityId TEXT NOT NULL DEFAULT '',
            RelatedPath TEXT NOT NULL DEFAULT ''
        );

        CREATE TABLE IF NOT EXISTS LocalUsers
        (
            UserId TEXT PRIMARY KEY,
            Role TEXT NOT NULL,
            IsDisabled INTEGER NOT NULL DEFAULT 0,
            IsDeleted INTEGER NOT NULL DEFAULT 0,
            CreatedAtUtc TEXT NOT NULL,
            CreatedBy TEXT NOT NULL DEFAULT 'UNKNOWN',
            UpdatedAtUtc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS LocalUserSessions
        (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            UserId TEXT NOT NULL,
            UserRole TEXT NOT NULL,
            AuthenticationMode TEXT NOT NULL,
            LoginAtUtc TEXT NOT NULL,
            LogoutAtUtc TEXT NULL,
            Success INTEGER NOT NULL DEFAULT 0,
            Message TEXT NOT NULL DEFAULT ''
        );

        CREATE TABLE IF NOT EXISTS RecipeRevisions
        (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            RecipeName TEXT NOT NULL,
            Revision TEXT NOT NULL,
            BoardProgram TEXT NOT NULL DEFAULT 'UNKNOWN',
            OperatorId TEXT NOT NULL DEFAULT 'UNKNOWN',
            DetectionPriority TEXT NOT NULL,
            BackgroundImagePath TEXT NOT NULL DEFAULT '',
            RecipeJson TEXT NOT NULL DEFAULT '',
            Notes TEXT NULL,
            CreatedAtUtc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
        );

        CREATE TABLE IF NOT EXISTS TrainingSamples
        (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            SourceImagePath TEXT NOT NULL,
            VaultPath TEXT NOT NULL,
            Label TEXT NOT NULL,
            Notes TEXT NULL,
            CreatedAtUtc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS CalibrationProfiles
        (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            ProfileName TEXT NOT NULL,
            BoardModel TEXT NOT NULL DEFAULT 'UNKNOWN',
            ViewType TEXT NOT NULL DEFAULT 'Top',
            SampleImagePath TEXT NOT NULL DEFAULT '',
            OperatorId TEXT NOT NULL DEFAULT 'UNKNOWN',
            PointCount INTEGER NOT NULL DEFAULT 0,
            ScaleX REAL NOT NULL DEFAULT 0,
            OffsetX REAL NOT NULL DEFAULT 0,
            ScaleY REAL NOT NULL DEFAULT 0,
            OffsetY REAL NOT NULL DEFAULT 0,
            TransformSummary TEXT NOT NULL DEFAULT '',
            CreatedAtUtc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
        );

        CREATE TABLE IF NOT EXISTS CalibrationPoints
        (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            ProfileId INTEGER NOT NULL,
            ImageX REAL NOT NULL,
            ImageY REAL NOT NULL,
            BoardXMillimeters REAL NOT NULL,
            BoardYMillimeters REAL NOT NULL,
            CreatedAtUtc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
            FOREIGN KEY (ProfileId) REFERENCES CalibrationProfiles(Id)
        );

        CREATE TABLE IF NOT EXISTS BatchTestRuns
        (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            ImageFolder TEXT NOT NULL,
            GroundTruthCsvPath TEXT NULL,
            EngineName TEXT NOT NULL,
            ModelVersion TEXT NOT NULL DEFAULT 'UNKNOWN',
            ThresholdProfileId TEXT NOT NULL DEFAULT '',
            ThresholdProfileRevision TEXT NOT NULL DEFAULT '',
            Accuracy REAL NOT NULL,
            Precision REAL NOT NULL,
            Recall REAL NOT NULL,
            FalseCallRate REAL NOT NULL,
            TotalImages INTEGER NOT NULL,
            FailedCount INTEGER NOT NULL,
            CreatedAtUtc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS BatchTestResults
        (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            RunId INTEGER NOT NULL,
            ImagePath TEXT NOT NULL,
            ImageName TEXT NOT NULL,
            GroundTruth TEXT NOT NULL,
            EngineResult TEXT NOT NULL,
            InspectionEngine TEXT NOT NULL DEFAULT 'Pixel Difference Prototype Engine',
            ModelVersion TEXT NOT NULL DEFAULT 'PIXEL_DIFF_0.1',
            Score REAL NOT NULL,
            PassFail TEXT NOT NULL,
            DefectType TEXT NOT NULL,
            NormalizedDefectClass TEXT NOT NULL DEFAULT 'UNASSIGNED',
            NormalizedSide TEXT NOT NULL DEFAULT 'UNASSIGNED',
            RoiId TEXT NOT NULL DEFAULT 'UNASSIGNED',
            RoiType TEXT NOT NULL DEFAULT 'UNASSIGNED',
            FailureCategory TEXT NOT NULL DEFAULT 'UNKNOWN_GT',
            RoiX REAL NOT NULL,
            RoiY REAL NOT NULL,
            RoiWidth REAL NOT NULL,
            RoiHeight REAL NOT NULL,
            Side TEXT NOT NULL DEFAULT '',
            RefDes TEXT NOT NULL DEFAULT '',
            LotId TEXT NOT NULL DEFAULT '',
            BoardModel TEXT NOT NULL DEFAULT '',
            Notes TEXT NOT NULL DEFAULT '',
            ImageLoadMs REAL NOT NULL DEFAULT 0,
            PreprocessingMs REAL NOT NULL DEFAULT 0,
            InferenceMs REAL NOT NULL DEFAULT 0,
            OverlayRenderingMs REAL NOT NULL DEFAULT 0,
            TotalInspectionMs REAL NOT NULL DEFAULT 0,
            CreatedAtUtc TEXT NOT NULL,
            FOREIGN KEY (RunId) REFERENCES BatchTestRuns(Id)
        );

        CREATE TABLE IF NOT EXISTS ValidationBreakdownMetrics
        (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            RunId INTEGER NOT NULL,
            BreakdownType TEXT NOT NULL,
            Key TEXT NOT NULL,
            DisplayName TEXT NOT NULL DEFAULT '',
            Total INTEGER NOT NULL DEFAULT 0,
            TruePositive INTEGER NOT NULL DEFAULT 0,
            TrueNegative INTEGER NOT NULL DEFAULT 0,
            FalsePositive INTEGER NOT NULL DEFAULT 0,
            FalseNegative INTEGER NOT NULL DEFAULT 0,
            WrongDefectClass INTEGER NOT NULL DEFAULT 0,
            WrongSide INTEGER NOT NULL DEFAULT 0,
            UnknownGroundTruth INTEGER NOT NULL DEFAULT 0,
            Precision REAL NOT NULL DEFAULT 0,
            Recall REAL NOT NULL DEFAULT 0,
            FalseCallRate REAL NOT NULL DEFAULT 0,
            CreatedAtUtc TEXT NOT NULL,
            FOREIGN KEY (RunId) REFERENCES BatchTestRuns(Id)
        );

        CREATE TABLE IF NOT EXISTS ThresholdProfiles
        (
            ProfileId TEXT NOT NULL,
            Revision TEXT NOT NULL,
            BoardModel TEXT NOT NULL DEFAULT 'ANY',
            BoardProgram TEXT NOT NULL DEFAULT 'ANY',
            RecipeName TEXT NOT NULL DEFAULT 'ANY',
            RecipeRevision TEXT NOT NULL DEFAULT 'ANY',
            Status TEXT NOT NULL DEFAULT 'Draft',
            SourceValidationRunId INTEGER NULL,
            SourceFalseCallReductionRunId INTEGER NULL,
            CreatedBy TEXT NOT NULL DEFAULT 'UNKNOWN',
            CreatedAtUtc TEXT NOT NULL,
            ApprovedBy TEXT NULL,
            ApprovedAtUtc TEXT NULL,
            PRIMARY KEY (ProfileId, Revision)
        );

        CREATE TABLE IF NOT EXISTS ThresholdProfileRules
        (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            ProfileId TEXT NOT NULL,
            Revision TEXT NOT NULL,
            ViewType TEXT NOT NULL DEFAULT 'Any',
            RoiType TEXT NOT NULL DEFAULT 'Any',
            DefectClass TEXT NOT NULL DEFAULT 'Any',
            ReviewThreshold REAL NOT NULL,
            NgThreshold REAL NOT NULL,
            ConfidenceThreshold REAL NOT NULL DEFAULT 0.65,
            MinimumAreaPixels REAL NOT NULL DEFAULT 0,
            MaxAllowedFalseCallRate REAL NOT NULL DEFAULT 1,
            FOREIGN KEY (ProfileId, Revision) REFERENCES ThresholdProfiles(ProfileId, Revision)
        );

        CREATE TABLE IF NOT EXISTS ThresholdProfileDeployments
        (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            ProfileId TEXT NOT NULL,
            Revision TEXT NOT NULL,
            BoardModel TEXT NOT NULL DEFAULT 'ANY',
            BoardProgram TEXT NOT NULL DEFAULT 'ANY',
            RecipeName TEXT NOT NULL DEFAULT 'ANY',
            DeployedAtUtc TEXT NOT NULL,
            DeployedBy TEXT NOT NULL DEFAULT 'UNKNOWN',
            IsActive INTEGER NOT NULL DEFAULT 1,
            FOREIGN KEY (ProfileId, Revision) REFERENCES ThresholdProfiles(ProfileId, Revision)
        );

        CREATE TABLE IF NOT EXISTS ModelRegistry
        (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            ModelId TEXT NOT NULL UNIQUE,
            DisplayName TEXT NOT NULL,
            Version TEXT NOT NULL,
            CreatedAtUtc TEXT NOT NULL,
            RegisteredAtUtc TEXT NOT NULL,
            SourceFileName TEXT NOT NULL,
            StoredModelPath TEXT NOT NULL,
            StoredLabelMapPath TEXT NOT NULL DEFAULT '',
            MetadataPath TEXT NOT NULL DEFAULT '',
            Sha256 TEXT NOT NULL,
            InputTensorName TEXT NOT NULL DEFAULT '',
            OutputTensorName TEXT NOT NULL DEFAULT '',
            InputWidth INTEGER NOT NULL DEFAULT 640,
            InputHeight INTEGER NOT NULL DEFAULT 640,
            ConfidenceThreshold REAL NOT NULL DEFAULT 0.65,
            LabelsJson TEXT NOT NULL DEFAULT '[]',
            ValidationStatus TEXT NOT NULL DEFAULT 'NotTested',
            LastValidatedAtUtc TEXT NULL,
            ValidationMessage TEXT NOT NULL DEFAULT '',
            Notes TEXT NOT NULL DEFAULT '',
            IsActive INTEGER NOT NULL DEFAULT 0,
            AuditEventId INTEGER NULL,
            LifecycleState TEXT NOT NULL DEFAULT 'Registered',
            LatestAcceptanceStatus TEXT NOT NULL DEFAULT '',
            LatestAcceptanceRunId INTEGER NULL,
            LatestReleasePackageId INTEGER NULL,
            LatestReleasePackagePath TEXT NOT NULL DEFAULT '',
            DeploymentWaiverReason TEXT NOT NULL DEFAULT '',
            WaiverExpiresAtUtc TEXT NULL,
            DeploymentWaivedBy TEXT NOT NULL DEFAULT '',
            DeploymentWaivedAtUtc TEXT NULL,
            DeployedAtUtc TEXT NULL,
            RetiredReason TEXT NOT NULL DEFAULT '',
            RetiredAtUtc TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS ExportHistory
        (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            ExportType TEXT NOT NULL,
            FilePath TEXT NOT NULL,
            Status TEXT NOT NULL,
            OperatorId TEXT NOT NULL DEFAULT 'UNKNOWN',
            AuditEventId INTEGER NULL,
            CreatedAtUtc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS ExportVerification
        (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            ExportHistoryId INTEGER NULL,
            CheckedAtUtc TEXT NOT NULL,
            ExportType TEXT NOT NULL,
            ExportPath TEXT NOT NULL,
            Status TEXT NOT NULL,
            Sha256 TEXT NOT NULL DEFAULT '',
            SizeBytes INTEGER NOT NULL DEFAULT 0,
            MessagesJson TEXT NOT NULL DEFAULT '[]',
            ArtifactChecksumsJson TEXT NOT NULL DEFAULT '{}',
            FOREIGN KEY (ExportHistoryId) REFERENCES ExportHistory(Id)
        );

        CREATE TABLE IF NOT EXISTS BuildTestEvidence
        (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            GeneratedAtUtc TEXT NOT NULL,
            CommitSha TEXT NOT NULL DEFAULT '',
            Configuration TEXT NOT NULL DEFAULT 'Release',
            HygieneStatus TEXT NOT NULL DEFAULT 'UNKNOWN',
            RestoreStatus TEXT NOT NULL DEFAULT 'UNKNOWN',
            BuildStatus TEXT NOT NULL DEFAULT 'UNKNOWN',
            TestStatus TEXT NOT NULL DEFAULT 'UNKNOWN',
            PublishValidationStatus TEXT NOT NULL DEFAULT 'UNKNOWN',
            EvidencePath TEXT NOT NULL DEFAULT '',
            OperatorId TEXT NOT NULL DEFAULT 'UNKNOWN',
            CreatedAtUtc TEXT NOT NULL,
            TestResultPath TEXT NOT NULL DEFAULT '',
            MachineName TEXT NOT NULL DEFAULT '',
            AuditEventId INTEGER NULL
        );

        CREATE TABLE IF NOT EXISTS ValidationPackages
        (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            PackageId TEXT NOT NULL,
            PackagePath TEXT NOT NULL DEFAULT '',
            ManifestPath TEXT NOT NULL,
            AcceptanceStatus TEXT NOT NULL,
            Summary TEXT NOT NULL DEFAULT '',
            RunId INTEGER NULL,
            OperatorId TEXT NOT NULL DEFAULT 'UNKNOWN',
            AuditEventId INTEGER NULL,
            CreatedAtUtc TEXT NOT NULL,
            FOREIGN KEY (RunId) REFERENCES BatchTestRuns(Id)
        );

        CREATE TABLE IF NOT EXISTS FalseCallReductionRuns
        (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            BatchRunId INTEGER NULL,
            CreatedAtUtc TEXT NOT NULL,
            EngineName TEXT NOT NULL,
            ModelVersion TEXT NOT NULL DEFAULT 'UNKNOWN',
            ModelId TEXT NOT NULL DEFAULT '',
            ModelSha256 TEXT NOT NULL DEFAULT '',
            CriteriaJson TEXT NOT NULL DEFAULT '{}',
            RecommendationStatus TEXT NOT NULL DEFAULT 'INVALID',
            RecommendationMode TEXT NOT NULL DEFAULT 'Balanced',
            SelectedThreshold REAL NULL,
            SelectedFalseCallRate REAL NULL,
            SelectedPossibleEscapeRate REAL NULL,
            SelectedReviewRate REAL NULL,
            SelectedManualReviewMinutes REAL NULL,
            SelectedPossibleEscapeCount INTEGER NULL,
            RecommendationMessagesJson TEXT NOT NULL DEFAULT '[]',
            OperatorId TEXT NOT NULL DEFAULT 'UNKNOWN',
            AuditEventId INTEGER NULL,
            FOREIGN KEY (BatchRunId) REFERENCES BatchTestRuns(Id)
        );

        CREATE TABLE IF NOT EXISTS FalseCallReductionPoints
        (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            RunId INTEGER NOT NULL,
            ConfidenceThreshold REAL NOT NULL,
            DifferenceThreshold REAL NOT NULL,
            TruePositive INTEGER NOT NULL,
            TrueNegative INTEGER NOT NULL,
            FalsePositive INTEGER NOT NULL,
            FalseNegative INTEGER NOT NULL,
            Precision REAL NOT NULL,
            Recall REAL NOT NULL,
            FalseCallRate REAL NOT NULL,
            PossibleEscapeRate REAL NOT NULL,
            ReviewRate REAL NOT NULL,
            NgRate REAL NOT NULL,
            ReviewCount INTEGER NOT NULL,
            NgCount INTEGER NOT NULL,
            EstimatedManualReviewMinutes REAL NOT NULL,
            MeetsConstraints INTEGER NOT NULL DEFAULT 0,
            Status TEXT NOT NULL DEFAULT 'CONDITIONAL',
            FOREIGN KEY (RunId) REFERENCES FalseCallReductionRuns(Id)
        );

        CREATE TABLE IF NOT EXISTS CameraAcceptanceRuns
        (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            CreatedAtUtc TEXT NOT NULL,
            AdapterName TEXT NOT NULL,
            SourceKey TEXT NOT NULL,
            SettingsSummary TEXT NOT NULL,
            CriteriaJson TEXT NOT NULL,
            Status TEXT NOT NULL,
            FactoryReadinessStatus TEXT NOT NULL,
            IsRealHardware INTEGER NOT NULL DEFAULT 0,
            TotalRequestedFrames INTEGER NOT NULL DEFAULT 0,
            TotalReceivedFrames INTEGER NOT NULL DEFAULT 0,
            DroppedFrameCount INTEGER NOT NULL DEFAULT 0,
            TriggerFailureCount INTEGER NOT NULL DEFAULT 0,
            TimeoutCount INTEGER NOT NULL DEFAULT 0,
            MaxConnectMs REAL NOT NULL DEFAULT 0,
            MaxFirstFrameMs REAL NOT NULL DEFAULT 0,
            AverageFrameIntervalMs REAL NOT NULL DEFAULT 0,
            WarningsJson TEXT NOT NULL DEFAULT '[]',
            FailuresJson TEXT NOT NULL DEFAULT '[]',
            ViewMetricsJson TEXT NOT NULL DEFAULT '[]',
            OperatorId TEXT NOT NULL DEFAULT 'UNKNOWN',
            AuditEventId INTEGER NULL
        );

        CREATE TABLE IF NOT EXISTS CameraAcceptanceFrames
        (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            RunId INTEGER NOT NULL,
            ViewType TEXT NOT NULL,
            Sequence INTEGER NOT NULL,
            FrameId TEXT NOT NULL,
            CameraId TEXT NOT NULL,
            CapturedAtUtc TEXT NOT NULL,
            Width INTEGER NOT NULL DEFAULT 0,
            Height INTEGER NOT NULL DEFAULT 0,
            PixelFormat TEXT NOT NULL DEFAULT '',
            SourceKind TEXT NOT NULL DEFAULT '',
            IsSimulated INTEGER NOT NULL DEFAULT 0,
            LatencyMs REAL NOT NULL DEFAULT 0,
            IntervalMs REAL NOT NULL DEFAULT 0,
            MetadataValid INTEGER NOT NULL DEFAULT 0,
            Message TEXT NOT NULL DEFAULT '',
            FOREIGN KEY (RunId) REFERENCES CameraAcceptanceRuns(Id)
        );

        CREATE TABLE IF NOT EXISTS LightingAcceptanceRuns
        (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            CreatedAtUtc TEXT NOT NULL,
            ControllerName TEXT NOT NULL,
            Mode TEXT NOT NULL,
            SettingsSummary TEXT NOT NULL,
            CriteriaJson TEXT NOT NULL,
            Status TEXT NOT NULL,
            IsSimulated INTEGER NOT NULL DEFAULT 0,
            StepCount INTEGER NOT NULL DEFAULT 0,
            PassedStepCount INTEGER NOT NULL DEFAULT 0,
            FailedStepCount INTEGER NOT NULL DEFAULT 0,
            MaxCommandLatencyMs REAL NOT NULL DEFAULT 0,
            MaxTriggerToFrameLatencyMs REAL NOT NULL DEFAULT 0,
            WarningsJson TEXT NOT NULL DEFAULT '[]',
            FailuresJson TEXT NOT NULL DEFAULT '[]',
            OperatorId TEXT NOT NULL DEFAULT 'UNKNOWN',
            AuditEventId INTEGER NULL
        );

        CREATE TABLE IF NOT EXISTS LightingAcceptanceSteps
        (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            RunId INTEGER NOT NULL,
            ViewType TEXT NOT NULL,
            ProgramName TEXT NOT NULL,
            CommandText TEXT NOT NULL,
            CommandLatencyMs REAL NOT NULL DEFAULT 0,
            TriggerToFrameLatencyMs REAL NOT NULL DEFAULT 0,
            CommandAccepted INTEGER NOT NULL DEFAULT 0,
            FrameReceived INTEGER NOT NULL DEFAULT 0,
            FrameId TEXT NOT NULL DEFAULT '',
            CameraId TEXT NOT NULL DEFAULT '',
            Status TEXT NOT NULL,
            Message TEXT NOT NULL DEFAULT '',
            FOREIGN KEY (RunId) REFERENCES LightingAcceptanceRuns(Id)
        );

        CREATE TABLE IF NOT EXISTS Profile3DAcceptanceRuns
        (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            CreatedAtUtc TEXT NOT NULL,
            SourceName TEXT NOT NULL,
            SourceKind TEXT NOT NULL DEFAULT 'None',
            IsSimulated INTEGER NOT NULL DEFAULT 1,
            Status TEXT NOT NULL DEFAULT 'FAIL',
            FactoryReadinessStatus TEXT NOT NULL DEFAULT 'NOT VALIDATED',
            AcquisitionMs REAL NOT NULL DEFAULT 0,
            Width INTEGER NOT NULL DEFAULT 0,
            Height INTEGER NOT NULL DEFAULT 0,
            Unit TEXT NOT NULL DEFAULT '',
            XPitchMicrons REAL NOT NULL DEFAULT 0,
            YPitchMicrons REAL NOT NULL DEFAULT 0,
            MissingHeightCount INTEGER NOT NULL DEFAULT 0,
            NaNHeightCount INTEGER NOT NULL DEFAULT 0,
            FrameId TEXT NOT NULL DEFAULT '',
            CriteriaJson TEXT NOT NULL DEFAULT '{}',
            DiagnosticsJson TEXT NOT NULL DEFAULT '{}',
            WarningsJson TEXT NOT NULL DEFAULT '[]',
            FailuresJson TEXT NOT NULL DEFAULT '[]',
            OperatorId TEXT NOT NULL DEFAULT 'UNKNOWN',
            AuditEventId INTEGER NULL
        );

        CREATE TABLE IF NOT EXISTS RobotAcceptanceRuns
        (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            CreatedAtUtc TEXT NOT NULL,
            ControllerName TEXT NOT NULL,
            EmergencyStopName TEXT NOT NULL,
            SafetyControllerName TEXT NOT NULL DEFAULT '',
            SafetySourceKind TEXT NOT NULL DEFAULT 'NotConnected',
            SourceKind TEXT NOT NULL,
            CriteriaJson TEXT NOT NULL,
            Status TEXT NOT NULL,
            FinalState TEXT NOT NULL,
            LoadMs REAL NOT NULL DEFAULT 0,
            MoveToInspectMs REAL NOT NULL DEFAULT 0,
            InspectionMs REAL NOT NULL DEFAULT 0,
            UnloadMs REAL NOT NULL DEFAULT 0,
            FullCycleMs REAL NOT NULL DEFAULT 0,
            InvalidTransitionRejected INTEGER NOT NULL DEFAULT 0,
            EmergencyStopBlocked INTEGER NOT NULL DEFAULT 0,
            SafetyFaultBlocked INTEGER NOT NULL DEFAULT 0,
            ResetReturnedIdle INTEGER NOT NULL DEFAULT 0,
            AuditEventCount INTEGER NOT NULL DEFAULT 0,
            WarningsJson TEXT NOT NULL DEFAULT '[]',
            FailuresJson TEXT NOT NULL DEFAULT '[]',
            OperatorId TEXT NOT NULL DEFAULT 'UNKNOWN',
            AuditEventId INTEGER NULL
        );

        CREATE TABLE IF NOT EXISTS RobotAcceptanceSteps
        (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            RunId INTEGER NOT NULL,
            StepName TEXT NOT NULL,
            FromState TEXT NOT NULL,
            ToState TEXT NOT NULL,
            ElapsedMs REAL NOT NULL DEFAULT 0,
            Accepted INTEGER NOT NULL DEFAULT 0,
            Status TEXT NOT NULL,
            Message TEXT NOT NULL DEFAULT '',
            FOREIGN KEY (RunId) REFERENCES RobotAcceptanceRuns(Id)
        );

        CREATE TABLE IF NOT EXISTS SoakTestRuns
        (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            RunId TEXT NOT NULL,
            StartedAtUtc TEXT NOT NULL,
            EndedAtUtc TEXT NOT NULL,
            ImageFolder TEXT NOT NULL,
            OutputFolder TEXT NOT NULL,
            EngineKey TEXT NOT NULL,
            EngineName TEXT NOT NULL,
            EngineVersion TEXT NOT NULL,
            SourceKind TEXT NOT NULL DEFAULT 'Simulated source',
            IsRealCameraSource INTEGER NOT NULL DEFAULT 0,
            ProfileName TEXT NOT NULL DEFAULT 'Custom',
            RequestedDurationSeconds REAL NOT NULL DEFAULT 0,
            ActualDurationSeconds REAL NOT NULL DEFAULT 0,
            DelayBetweenInspectionsMs REAL NOT NULL DEFAULT 0,
            OperatorId TEXT NOT NULL DEFAULT 'UNKNOWN',
            BoardModel TEXT NOT NULL DEFAULT 'UNKNOWN',
            LotId TEXT NOT NULL DEFAULT 'SOAK-TEST',
            WasCanceled INTEGER NOT NULL DEFAULT 0,
            TotalCycles INTEGER NOT NULL DEFAULT 0,
            SuccessfulCycles INTEGER NOT NULL DEFAULT 0,
            FailedCycles INTEGER NOT NULL DEFAULT 0,
            AverageInspectionMs REAL NOT NULL DEFAULT 0,
            MinInspectionMs REAL NOT NULL DEFAULT 0,
            MaxInspectionMs REAL NOT NULL DEFAULT 0,
            P95InspectionMs REAL NOT NULL DEFAULT 0,
            CountOverOneSecond INTEGER NOT NULL DEFAULT 0,
            StartManagedMemoryMb REAL NOT NULL DEFAULT 0,
            EndManagedMemoryMb REAL NOT NULL DEFAULT 0,
            StartWorkingSetMb REAL NOT NULL DEFAULT 0,
            EndWorkingSetMb REAL NOT NULL DEFAULT 0,
            PeakWorkingSetMb REAL NOT NULL DEFAULT 0,
            IsCompletedFactoryEvidence INTEGER NOT NULL DEFAULT 0,
            ErrorsJson TEXT NOT NULL DEFAULT '[]',
            AverageTotalCycleMs REAL NOT NULL DEFAULT 0,
            MaxTotalCycleMs REAL NOT NULL DEFAULT 0,
            P95TotalCycleMs REAL NOT NULL DEFAULT 0,
            CancellationReason TEXT NOT NULL DEFAULT '',
            FirstCriticalError TEXT NOT NULL DEFAULT '',
            MemoryWarningsJson TEXT NOT NULL DEFAULT '[]',
            AuditEventId INTEGER NULL
        );

        CREATE TABLE IF NOT EXISTS SoakTestIterations
        (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            RunId INTEGER NOT NULL,
            CycleNumber INTEGER NOT NULL,
            TimestampUtc TEXT NOT NULL,
            FrameId TEXT NOT NULL,
            ImagePath TEXT NOT NULL,
            EngineName TEXT NOT NULL,
            Verdict TEXT NOT NULL,
            TotalInspectionMs REAL NOT NULL DEFAULT 0,
            WorkingSetMb REAL NOT NULL DEFAULT 0,
            Success INTEGER NOT NULL DEFAULT 0,
            Message TEXT NOT NULL DEFAULT '',
            Error TEXT NOT NULL DEFAULT '',
            TotalCycleMs REAL NOT NULL DEFAULT 0,
            ExceptionCategory TEXT NOT NULL DEFAULT '',
            FOREIGN KEY (RunId) REFERENCES SoakTestRuns(Id)
        );

        CREATE TABLE IF NOT EXISTS MesUploadAttempts
        (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Mode TEXT NOT NULL,
            EndpointUrl TEXT NOT NULL DEFAULT '',
            PayloadPath TEXT NOT NULL,
            Status TEXT NOT NULL,
            Message TEXT NOT NULL,
            OperatorId TEXT NOT NULL DEFAULT 'UNKNOWN',
            LotId TEXT NOT NULL DEFAULT '',
            BoardModel TEXT NOT NULL DEFAULT '',
            Result TEXT NOT NULL DEFAULT '',
            CreatedAtUtc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS MesSpoolQueue
        (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            CreatedAtUtc TEXT NOT NULL,
            LastAttemptAtUtc TEXT NULL,
            NextAttemptAtUtc TEXT NULL,
            PayloadType TEXT NOT NULL,
            PayloadJson TEXT NOT NULL,
            PayloadPath TEXT NOT NULL DEFAULT '',
            EndpointUrl TEXT NOT NULL DEFAULT '',
            RetryCount INTEGER NOT NULL DEFAULT 0,
            MaxRetryCount INTEGER NOT NULL DEFAULT 3,
            Status TEXT NOT NULL DEFAULT 'Pending',
            LastError TEXT NOT NULL DEFAULT '',
            OperatorId TEXT NOT NULL DEFAULT 'UNKNOWN',
            LotId TEXT NOT NULL DEFAULT '',
            BoardModel TEXT NOT NULL DEFAULT '',
            Result TEXT NOT NULL DEFAULT ''
        );

        CREATE TABLE IF NOT EXISTS CentralSyncQueue
        (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            CreatedAtUtc TEXT NOT NULL,
            LastAttemptAtUtc TEXT NULL,
            NextAttemptAtUtc TEXT NULL,
            ItemType TEXT NOT NULL,
            ItemId TEXT NOT NULL,
            PayloadJson TEXT NOT NULL,
            PayloadPath TEXT NOT NULL DEFAULT '',
            EndpointOrFolder TEXT NOT NULL DEFAULT '',
            StationId TEXT NOT NULL DEFAULT '',
            RetryCount INTEGER NOT NULL DEFAULT 0,
            MaxRetryCount INTEGER NOT NULL DEFAULT 5,
            Status TEXT NOT NULL DEFAULT 'Pending',
            LastError TEXT NOT NULL DEFAULT ''
        );

        CREATE TABLE IF NOT EXISTS CentralSyncAttempts
        (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            QueueId INTEGER NOT NULL,
            AttemptedAtUtc TEXT NOT NULL,
            Mode TEXT NOT NULL,
            EndpointOrFolder TEXT NOT NULL DEFAULT '',
            Status TEXT NOT NULL,
            Message TEXT NOT NULL DEFAULT '',
            FOREIGN KEY (QueueId) REFERENCES CentralSyncQueue(Id)
        );

        CREATE TABLE IF NOT EXISTS TraceabilityTestReports
        (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            CreatedAtUtc TEXT NOT NULL,
            Status TEXT NOT NULL DEFAULT 'FAIL',
            Mode TEXT NOT NULL DEFAULT 'Not Connected',
            EndpointUrl TEXT NOT NULL DEFAULT '',
            ResultStatus TEXT NOT NULL DEFAULT 'FAIL',
            ImageStatus TEXT NOT NULL DEFAULT 'NOT SENT',
            PayloadPath TEXT NOT NULL DEFAULT '',
            ReportJsonPath TEXT NOT NULL DEFAULT '',
            ReportHtmlPath TEXT NOT NULL DEFAULT '',
            Message TEXT NOT NULL DEFAULT '',
            ProductionModeConfirmed INTEGER NOT NULL DEFAULT 0,
            OperatorId TEXT NOT NULL DEFAULT 'UNKNOWN',
            AuditEventId INTEGER NULL
        );

        CREATE TABLE IF NOT EXISTS LogArchive
        (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            SourceTable TEXT NOT NULL,
            SourceId INTEGER NOT NULL,
            SourceTimestampUtc TEXT NOT NULL,
            ArchivedAtUtc TEXT NOT NULL,
            Notes TEXT NOT NULL,
            UNIQUE(SourceTable, SourceId)
        );

        CREATE INDEX IF NOT EXISTS IX_Images_FileHash ON Images(FileHash);
        CREATE INDEX IF NOT EXISTS IX_Images_BoardModel_LotId ON Images(BoardModel, LotId);
        CREATE INDEX IF NOT EXISTS IX_InspectionResults_CreatedAtUtc ON InspectionResults(CreatedAtUtc);
        CREATE INDEX IF NOT EXISTS IX_InspectionResults_BoardProgram ON InspectionResults(BoardProgram);
        CREATE INDEX IF NOT EXISTS IX_InspectionResults_OperatorId ON InspectionResults(OperatorId);
        CREATE INDEX IF NOT EXISTS IX_InspectionResults_Verdict ON InspectionResults(Verdict);
        CREATE INDEX IF NOT EXISTS IX_LocalUserSessions_UserId ON LocalUserSessions(UserId);
        CREATE INDEX IF NOT EXISTS IX_LocalUserSessions_LoginAtUtc ON LocalUserSessions(LoginAtUtc);
        CREATE INDEX IF NOT EXISTS IX_InspectionLatencyTraces_CreatedAtUtc ON InspectionLatencyTraces(CreatedAtUtc);
        CREATE INDEX IF NOT EXISTS IX_InspectionLatencyTraces_SourceKind ON InspectionLatencyTraces(SourceKind);
        CREATE INDEX IF NOT EXISTS IX_ReviewEvents_EventTimeUtc ON ReviewEvents(EventTimeUtc);
        CREATE INDEX IF NOT EXISTS IX_AuditEvents_TimestampUtc ON AuditEvents(TimestampUtc);
        CREATE INDEX IF NOT EXISTS IX_AuditEvents_UserRole ON AuditEvents(UserId, UserRole);
        CREATE INDEX IF NOT EXISTS IX_AuditEvents_ActionCategory ON AuditEvents(ActionCategory);
        CREATE INDEX IF NOT EXISTS IX_RecipeRevisions_BoardProgram_CreatedAtUtc ON RecipeRevisions(BoardProgram, CreatedAtUtc);
        CREATE INDEX IF NOT EXISTS IX_CalibrationProfiles_BoardModel_CreatedAtUtc ON CalibrationProfiles(BoardModel, CreatedAtUtc);
        CREATE INDEX IF NOT EXISTS IX_CalibrationPoints_ProfileId ON CalibrationPoints(ProfileId);
        CREATE INDEX IF NOT EXISTS IX_BatchTestResults_RunId ON BatchTestResults(RunId);
        CREATE INDEX IF NOT EXISTS IX_ValidationBreakdownMetrics_RunId ON ValidationBreakdownMetrics(RunId);
        CREATE INDEX IF NOT EXISTS IX_ValidationBreakdownMetrics_Type ON ValidationBreakdownMetrics(BreakdownType, Key);
        CREATE INDEX IF NOT EXISTS IX_ThresholdProfiles_Status ON ThresholdProfiles(Status);
        CREATE INDEX IF NOT EXISTS IX_ThresholdProfileRules_Profile ON ThresholdProfileRules(ProfileId, Revision);
        CREATE INDEX IF NOT EXISTS IX_ThresholdProfileDeployments_Active ON ThresholdProfileDeployments(BoardModel, BoardProgram, RecipeName, IsActive);
        CREATE INDEX IF NOT EXISTS IX_ModelRegistry_ModelId ON ModelRegistry(ModelId);
        CREATE INDEX IF NOT EXISTS IX_ModelRegistry_IsActive ON ModelRegistry(IsActive);
        CREATE INDEX IF NOT EXISTS IX_ModelRegistry_RegisteredAtUtc ON ModelRegistry(RegisteredAtUtc);
        CREATE INDEX IF NOT EXISTS IX_ExportHistory_CreatedAtUtc ON ExportHistory(CreatedAtUtc);
        CREATE INDEX IF NOT EXISTS IX_ExportVerification_ExportHistoryId ON ExportVerification(ExportHistoryId);
        CREATE INDEX IF NOT EXISTS IX_ExportVerification_CheckedAtUtc ON ExportVerification(CheckedAtUtc);
        CREATE INDEX IF NOT EXISTS IX_ExportVerification_Status ON ExportVerification(Status);
        CREATE INDEX IF NOT EXISTS IX_BuildTestEvidence_GeneratedAtUtc ON BuildTestEvidence(GeneratedAtUtc);
        CREATE INDEX IF NOT EXISTS IX_ValidationPackages_CreatedAtUtc ON ValidationPackages(CreatedAtUtc);
        CREATE INDEX IF NOT EXISTS IX_ValidationPackages_PackageId ON ValidationPackages(PackageId);
        CREATE INDEX IF NOT EXISTS IX_FalseCallReductionRuns_CreatedAtUtc ON FalseCallReductionRuns(CreatedAtUtc);
        CREATE INDEX IF NOT EXISTS IX_FalseCallReductionRuns_BatchRunId ON FalseCallReductionRuns(BatchRunId);
        CREATE INDEX IF NOT EXISTS IX_FalseCallReductionPoints_RunId ON FalseCallReductionPoints(RunId);
        CREATE INDEX IF NOT EXISTS IX_CameraAcceptanceRuns_CreatedAtUtc ON CameraAcceptanceRuns(CreatedAtUtc);
        CREATE INDEX IF NOT EXISTS IX_CameraAcceptanceRuns_RealHardware ON CameraAcceptanceRuns(IsRealHardware, CreatedAtUtc);
        CREATE INDEX IF NOT EXISTS IX_CameraAcceptanceFrames_RunId ON CameraAcceptanceFrames(RunId);
        CREATE INDEX IF NOT EXISTS IX_LightingAcceptanceRuns_CreatedAtUtc ON LightingAcceptanceRuns(CreatedAtUtc);
        CREATE INDEX IF NOT EXISTS IX_LightingAcceptanceSteps_RunId ON LightingAcceptanceSteps(RunId);
        CREATE INDEX IF NOT EXISTS IX_Profile3DAcceptanceRuns_CreatedAtUtc ON Profile3DAcceptanceRuns(CreatedAtUtc);
        CREATE INDEX IF NOT EXISTS IX_RobotAcceptanceRuns_CreatedAtUtc ON RobotAcceptanceRuns(CreatedAtUtc);
        CREATE INDEX IF NOT EXISTS IX_RobotAcceptanceSteps_RunId ON RobotAcceptanceSteps(RunId);
        CREATE INDEX IF NOT EXISTS IX_SoakTestRuns_StartedAtUtc ON SoakTestRuns(StartedAtUtc);
        CREATE INDEX IF NOT EXISTS IX_SoakTestRuns_FactoryEvidence ON SoakTestRuns(IsCompletedFactoryEvidence, StartedAtUtc);
        CREATE INDEX IF NOT EXISTS IX_SoakTestIterations_RunId ON SoakTestIterations(RunId);
        CREATE INDEX IF NOT EXISTS IX_MesUploadAttempts_CreatedAtUtc ON MesUploadAttempts(CreatedAtUtc);
        CREATE INDEX IF NOT EXISTS IX_MesSpoolQueue_Status_NextAttempt ON MesSpoolQueue(Status, NextAttemptAtUtc);
        CREATE INDEX IF NOT EXISTS IX_MesSpoolQueue_CreatedAtUtc ON MesSpoolQueue(CreatedAtUtc);
        CREATE INDEX IF NOT EXISTS IX_CentralSyncQueue_Status_NextAttempt ON CentralSyncQueue(Status, NextAttemptAtUtc);
        CREATE INDEX IF NOT EXISTS IX_CentralSyncQueue_Item ON CentralSyncQueue(ItemType, ItemId);
        CREATE INDEX IF NOT EXISTS IX_CentralSyncAttempts_QueueId ON CentralSyncAttempts(QueueId);
        CREATE INDEX IF NOT EXISTS IX_TraceabilityTestReports_CreatedAtUtc ON TraceabilityTestReports(CreatedAtUtc);
        """;
}
