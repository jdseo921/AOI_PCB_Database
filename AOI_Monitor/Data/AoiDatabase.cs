using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Media.Imaging;
using AOI_Monitor.Models;
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

    public static string StorageRoot { get; } = ResolveStorageRoot();
    public static string DatabasePath { get; } = Path.Combine(StorageRoot, "aoi_monitor.sqlite");
    public static string ImageVaultPath { get; } = Path.Combine(StorageRoot, "image_vault");
    public static string TrainingVaultPath { get; } = Path.Combine(ImageVaultPath, "training");

    public static void Initialize()
    {
        Directory.CreateDirectory(StorageRoot);
        Directory.CreateDirectory(ImageVaultPath);
        Directory.CreateDirectory(TrainingVaultPath);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = SchemaSql;
        command.ExecuteNonQuery();
        EnsureSchemaCompatibility(connection);

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
        return new ImportedImage(id, sourcePath, vaultPath, originalName, boardModel, lotId, viewType, importedAt, hash);
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
                return new ImageImportResult(existing, false, "Duplicate", "Image already exists in the vault.");

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

    public static void RecordInspectionResult(AnalysisResult result)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO InspectionResults
                (SampleImagePath, GoldenImagePath, InspectionEngine, DifferenceScore, MeanBrightness, Verdict, Confidence,
                 SuggestedDefect, PolicyName, ModelVersion, DecisionReason, HotspotX, HotspotY, HotspotWidth,
                 HotspotHeight, CreatedAtUtc)
            VALUES
                ($sampleImagePath, $goldenImagePath, $inspectionEngine, $differenceScore, $meanBrightness, $verdict, $confidence,
                 $suggestedDefect, $policyName, $modelVersion, $decisionReason, $hotspotX, $hotspotY, $hotspotWidth,
                 $hotspotHeight, $createdAtUtc);
            SELECT last_insert_rowid();
            """;

        command.Parameters.AddWithValue("$sampleImagePath", result.SamplePath);
        command.Parameters.AddWithValue("$goldenImagePath", (object?)result.GoldenPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$inspectionEngine", result.InspectionEngine);
        command.Parameters.AddWithValue("$differenceScore", result.DifferenceScore);
        command.Parameters.AddWithValue("$meanBrightness", result.MeanBrightness);
        command.Parameters.AddWithValue("$verdict", result.Verdict);
        command.Parameters.AddWithValue("$confidence", result.Confidence);
        command.Parameters.AddWithValue("$suggestedDefect", result.SuggestedDefect);
        command.Parameters.AddWithValue("$policyName", result.PolicyName);
        command.Parameters.AddWithValue("$modelVersion", result.ModelVersion);
        command.Parameters.AddWithValue("$decisionReason", result.DecisionReason);
        command.Parameters.AddWithValue("$hotspotX", result.Hotspot.X);
        command.Parameters.AddWithValue("$hotspotY", result.Hotspot.Y);
        command.Parameters.AddWithValue("$hotspotWidth", result.Hotspot.Width);
        command.Parameters.AddWithValue("$hotspotHeight", result.Hotspot.Height);
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
    }

    public static void RecordWorkflowEvent(string category, string message, DateTime timestamp)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO ReviewEvents (Category, Message, EventTimeUtc)
            VALUES ($category, $message, $eventTimeUtc);
            """;
        command.Parameters.AddWithValue("$category", category);
        command.Parameters.AddWithValue("$message", message);
        command.Parameters.AddWithValue("$eventTimeUtc", timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
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

    public static void RecordExport(string exportType, string filePath, string status = "OK")
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO ExportHistory (ExportType, FilePath, Status, CreatedAtUtc)
            VALUES ($exportType, $filePath, $status, $createdAtUtc);
            """;
        command.Parameters.AddWithValue("$exportType", exportType);
        command.Parameters.AddWithValue("$filePath", filePath);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$createdAtUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
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

    private static void EnsureSchemaCompatibility(SqliteConnection connection)
    {
        AddColumnIfMissing(connection, "InspectionResults", "InspectionEngine", "TEXT NOT NULL DEFAULT 'Pixel Difference'");
        AddColumnIfMissing(connection, "Defects", "Confidence", "REAL NOT NULL DEFAULT 0");
        AddColumnIfMissing(connection, "Defects", "XPosition", "REAL NULL");
        AddColumnIfMissing(connection, "Defects", "YPosition", "REAL NULL");
        AddColumnIfMissing(connection, "Defects", "SideOrViewType", "TEXT NOT NULL DEFAULT 'sample'");
        AddColumnIfMissing(connection, "Defects", "RoiId", "TEXT NOT NULL DEFAULT 'ROI-UNASSIGNED'");
        AddColumnIfMissing(connection, "Defects", "JudgmentStatus", "TEXT NOT NULL DEFAULT 'REVIEW'");
    }

    private static void AddColumnIfMissing(SqliteConnection connection, string tableName, string columnName, string columnDefinition)
    {
        using var readCommand = connection.CreateCommand();
        readCommand.CommandText = $"PRAGMA table_info({tableName});";

        using (var reader = readCommand.ExecuteReader())
        {
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                    return;
            }
        }

        using var alterCommand = connection.CreateCommand();
        alterCommand.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition};";
        alterCommand.ExecuteNonQuery();
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
            InspectionEngine TEXT NOT NULL DEFAULT 'Pixel Difference',
            DifferenceScore REAL NOT NULL,
            MeanBrightness REAL NOT NULL,
            Verdict TEXT NOT NULL,
            Confidence REAL NOT NULL,
            SuggestedDefect TEXT NOT NULL,
            PolicyName TEXT NOT NULL,
            ModelVersion TEXT NOT NULL,
            DecisionReason TEXT NOT NULL,
            HotspotX REAL NOT NULL,
            HotspotY REAL NOT NULL,
            HotspotWidth REAL NOT NULL,
            HotspotHeight REAL NOT NULL,
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

        CREATE TABLE IF NOT EXISTS RecipeRevisions
        (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            RecipeName TEXT NOT NULL,
            Revision TEXT NOT NULL,
            DetectionPriority TEXT NOT NULL,
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

        CREATE TABLE IF NOT EXISTS ExportHistory
        (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            ExportType TEXT NOT NULL,
            FilePath TEXT NOT NULL,
            Status TEXT NOT NULL,
            CreatedAtUtc TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS IX_Images_FileHash ON Images(FileHash);
        CREATE INDEX IF NOT EXISTS IX_Images_BoardModel_LotId ON Images(BoardModel, LotId);
        CREATE INDEX IF NOT EXISTS IX_ReviewEvents_EventTimeUtc ON ReviewEvents(EventTimeUtc);
        """;
}
