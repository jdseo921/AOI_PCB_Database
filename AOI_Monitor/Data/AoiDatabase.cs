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
        using var command = connection.CreateCommand();
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
                 SuggestedDefect, PolicyName, ModelVersion, ModelFilePath, ConfidenceThreshold, DecisionReason, HotspotX, HotspotY, HotspotWidth,
                 HotspotHeight, ImageLoadMs, PreprocessingMs, InferenceMs, OverlayRenderingMs, TotalInspectionMs, CreatedAtUtc)
            VALUES
                ($sampleImagePath, $goldenImagePath, $boardProgram, $operatorId, $inspectionEngine, $differenceScore, $meanBrightness, $verdict, $confidence,
                 $suggestedDefect, $policyName, $modelVersion, $modelFilePath, $confidenceThreshold, $decisionReason, $hotspotX, $hotspotY, $hotspotWidth,
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

    public static long RecordBatchTestRun(
        string imageFolder,
        string? groundTruthCsvPath,
        string engineName,
        string modelVersion,
        double accuracy,
        double precision,
        double recall,
        double falseCallRate,
        IReadOnlyList<BatchTestResultRecord> results)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO BatchTestRuns
                (ImageFolder, GroundTruthCsvPath, EngineName, ModelVersion, Accuracy, Precision, Recall,
                 FalseCallRate, TotalImages, FailedCount, CreatedAtUtc)
            VALUES
                ($imageFolder, $groundTruthCsvPath, $engineName, $modelVersion, $accuracy, $precision, $recall,
                 $falseCallRate, $totalImages, $failedCount, $createdAtUtc);
            SELECT last_insert_rowid();
            """;

        command.Parameters.AddWithValue("$imageFolder", imageFolder);
        command.Parameters.AddWithValue("$groundTruthCsvPath", (object?)groundTruthCsvPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$engineName", engineName);
        command.Parameters.AddWithValue("$modelVersion", modelVersion);
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
                     DefectType, RoiX, RoiY, RoiWidth, RoiHeight, Side, RefDes, LotId, BoardModel, Notes,
                     ImageLoadMs, PreprocessingMs, InferenceMs, OverlayRenderingMs, TotalInspectionMs, CreatedAtUtc)
                VALUES
                    ($runId, $imagePath, $imageName, $groundTruth, $engineResult, $inspectionEngine, $modelVersion, $score, $passFail,
                     $defectType, $roiX, $roiY, $roiWidth, $roiHeight, $side, $refDes, $lotId, $boardModel, $notes,
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

        transaction.Commit();
        return runId;
    }

    public static BatchTestRunRecord? GetLatestBatchTestRun()
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, ImageFolder, GroundTruthCsvPath, EngineName, ModelVersion, CreatedAtUtc, Accuracy, Precision,
                   Recall, FalseCallRate, TotalImages, FailedCount
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
                   ValidationMessage, Notes, IsActive, AuditEventId
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
                   ValidationMessage, Notes, IsActive, AuditEventId
            FROM ModelRegistry
            WHERE IsActive = 1
            ORDER BY datetime(RegisteredAtUtc) DESC, Id DESC
            LIMIT 1;
            """;

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadModelRegistryRecord(reader) : null;
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
            CountTable(connection, "ModelRegistry", "OK"),
            CountTable(connection, "ExportHistory", "OK"),
            CountTable(connection, "ValidationPackages", "OK"),
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
                 ValidationMessage, Notes, IsActive, AuditEventId)
            VALUES
                ($modelId, $displayName, $version, $createdAtUtc, $registeredAtUtc, $sourceFileName,
                 $storedModelPath, $storedLabelMapPath, $metadataPath, $sha256, $inputTensorName, $outputTensorName,
                 $inputWidth, $inputHeight, $confidenceThreshold, $labelsJson, $validationStatus, $lastValidatedAtUtc,
                 $validationMessage, $notes, $isActive, $auditEventId)
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
                AuditEventId = excluded.AuditEventId;
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
                ValidationMessage = $validationMessage
            WHERE ModelId = $modelId;
            """;
        command.Parameters.AddWithValue("$validationStatus", status.ToString());
        command.Parameters.AddWithValue("$lastValidatedAtUtc", timestampUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$validationMessage", message);
        command.Parameters.AddWithValue("$modelId", modelId);
        command.ExecuteNonQuery();
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
        command.Parameters.AddWithValue("$lastError", lastError);
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
            $"MES REST payload spooled: id={id}; type={payloadType}; result={result}; message={lastError}.",
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

    public static void DeleteMesSpoolItem(long id, string message)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM MesSpoolQueue WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();

        RecordAuditEvent(
            "MES_SPOOL",
            $"MES spool item {id} completed and was removed. {message}",
            relatedEntityType: "MesSpoolQueue",
            relatedEntityId: id.ToString(CultureInfo.InvariantCulture));
    }

    public static void RecordMesSpoolRetryFailure(long id, string message, int retryBackoffMs)
    {
        EnsureInitialized();

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
        command.Parameters.AddWithValue("$lastError", message);
        command.ExecuteNonQuery();

        RecordAuditEvent(
            "MES_SPOOL",
            $"MES spool item {id} retry failed: {message}",
            relatedEntityType: "MesSpoolQueue",
            relatedEntityId: id.ToString(CultureInfo.InvariantCulture));
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
            reader.GetInt32(11));
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
            reader.IsDBNull(23) ? 0 : reader.GetDouble(23),
            reader.IsDBNull(24) ? 0 : reader.GetDouble(24),
            ParseDateTime(reader.GetString(25)));
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
            reader.IsDBNull(22) ? null : reader.GetInt64(22));
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
    }

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
                AuditEventId INTEGER NULL
            );

            CREATE INDEX IF NOT EXISTS IX_ModelRegistry_ModelId ON ModelRegistry(ModelId);
            CREATE INDEX IF NOT EXISTS IX_ModelRegistry_IsActive ON ModelRegistry(IsActive);
            CREATE INDEX IF NOT EXISTS IX_ModelRegistry_RegisteredAtUtc ON ModelRegistry(RegisteredAtUtc);
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
            AuditEventId INTEGER NULL
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
        CREATE INDEX IF NOT EXISTS IX_ReviewEvents_EventTimeUtc ON ReviewEvents(EventTimeUtc);
        CREATE INDEX IF NOT EXISTS IX_AuditEvents_TimestampUtc ON AuditEvents(TimestampUtc);
        CREATE INDEX IF NOT EXISTS IX_AuditEvents_UserRole ON AuditEvents(UserId, UserRole);
        CREATE INDEX IF NOT EXISTS IX_AuditEvents_ActionCategory ON AuditEvents(ActionCategory);
        CREATE INDEX IF NOT EXISTS IX_RecipeRevisions_BoardProgram_CreatedAtUtc ON RecipeRevisions(BoardProgram, CreatedAtUtc);
        CREATE INDEX IF NOT EXISTS IX_CalibrationProfiles_BoardModel_CreatedAtUtc ON CalibrationProfiles(BoardModel, CreatedAtUtc);
        CREATE INDEX IF NOT EXISTS IX_CalibrationPoints_ProfileId ON CalibrationPoints(ProfileId);
        CREATE INDEX IF NOT EXISTS IX_BatchTestResults_RunId ON BatchTestResults(RunId);
        CREATE INDEX IF NOT EXISTS IX_ModelRegistry_ModelId ON ModelRegistry(ModelId);
        CREATE INDEX IF NOT EXISTS IX_ModelRegistry_IsActive ON ModelRegistry(IsActive);
        CREATE INDEX IF NOT EXISTS IX_ModelRegistry_RegisteredAtUtc ON ModelRegistry(RegisteredAtUtc);
        CREATE INDEX IF NOT EXISTS IX_ExportHistory_CreatedAtUtc ON ExportHistory(CreatedAtUtc);
        CREATE INDEX IF NOT EXISTS IX_ExportVerification_ExportHistoryId ON ExportVerification(ExportHistoryId);
        CREATE INDEX IF NOT EXISTS IX_ExportVerification_CheckedAtUtc ON ExportVerification(CheckedAtUtc);
        CREATE INDEX IF NOT EXISTS IX_ExportVerification_Status ON ExportVerification(Status);
        CREATE INDEX IF NOT EXISTS IX_ValidationPackages_CreatedAtUtc ON ValidationPackages(CreatedAtUtc);
        CREATE INDEX IF NOT EXISTS IX_ValidationPackages_PackageId ON ValidationPackages(PackageId);
        CREATE INDEX IF NOT EXISTS IX_MesUploadAttempts_CreatedAtUtc ON MesUploadAttempts(CreatedAtUtc);
        CREATE INDEX IF NOT EXISTS IX_MesSpoolQueue_Status_NextAttempt ON MesSpoolQueue(Status, NextAttemptAtUtc);
        CREATE INDEX IF NOT EXISTS IX_MesSpoolQueue_CreatedAtUtc ON MesSpoolQueue(CreatedAtUtc);
        """;
}
