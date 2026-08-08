using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Microsoft.Data.Sqlite;

namespace AOI_Monitor.Data;

public static partial class AoiDatabase
{
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

    private static ImageLearningProject ReadImageLearningProject(SqliteDataReader reader)
    {
        return new ImageLearningProject
        {
            Id = reader.GetInt64(0),
            ProjectId = reader.GetString(1),
            ProjectName = reader.GetString(2),
            BoardModel = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
            Description = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
            EvidenceMode = ParseImageLearningEvidenceMode(reader.GetString(5)),
            CreatedBy = reader.IsDBNull(6) ? "UNKNOWN" : reader.GetString(6),
            CreatedAtUtc = ParseDateTime(reader.GetString(7)),
            UpdatedAtUtc = ParseDateTime(reader.GetString(8)),
            IsArchived = reader.GetInt32(9) == 1,
            ArchivedBy = reader.IsDBNull(10) ? string.Empty : reader.GetString(10),
            ArchivedAtUtc = reader.IsDBNull(11) ? null : ParseDateTime(reader.GetString(11)),
            ArchiveReason = reader.IsDBNull(12) ? string.Empty : reader.GetString(12),
        };
    }

    private static ImageLearningProjectImage ReadImageLearningProjectImage(SqliteDataReader reader)
    {
        return new ImageLearningProjectImage
        {
            Id = reader.GetInt64(0),
            ProjectId = reader.GetString(1),
            Role = ParseImageLearningImageRole(reader.GetString(2)),
            OriginalPath = reader.GetString(3),
            VaultPath = reader.GetString(4),
            FileName = reader.GetString(5),
            Sha256 = reader.GetString(6),
            BoardModel = reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
            LotId = reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
            ViewType = reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
            Width = reader.GetInt32(10),
            Height = reader.GetInt32(11),
            ImportedBy = reader.IsDBNull(12) ? "UNKNOWN" : reader.GetString(12),
            ImportedAtUtc = ParseDateTime(reader.GetString(13)),
            ImageLevelTruth = reader.IsDBNull(14) ? "UNKNOWN" : reader.GetString(14),
            Notes = reader.IsDBNull(15) ? string.Empty : reader.GetString(15),
        };
    }

    private static LearnedPcbVisualModel ReadLearnedPcbVisualModel(
        SqliteDataReader reader,
        IReadOnlyList<LearnedPcbVisualModelArtifact> artifacts)
    {
        return new LearnedPcbVisualModel
        {
            Id = reader.GetInt64(0),
            ModelId = reader.GetString(1),
            ModelVersion = reader.GetString(2),
            CreatedAtUtc = ParseDateTime(reader.GetString(3)),
            ProjectId = reader.GetString(4),
            GoldenCount = reader.GetInt32(5),
            OkLearningCount = reader.GetInt32(6),
            OkValidationCount = reader.GetInt32(7),
            InputWidth = reader.GetInt32(8),
            InputHeight = reader.GetInt32(9),
            AlignmentMode = reader.IsDBNull(10) ? string.Empty : reader.GetString(10),
            BrightnessNormalizationMode = reader.IsDBNull(11) ? string.Empty : reader.GetString(11),
            LearnedThreshold = reader.GetDouble(12),
            FalseCallTarget = reader.GetDouble(13),
            FalseCallRate = reader.GetDouble(14),
            PossibleEscapeRate = reader.GetDouble(15),
            EvidenceMode = ParseImageLearningEvidenceMode(reader.GetString(16)),
            CreatedBy = reader.IsDBNull(17) ? "UNKNOWN" : reader.GetString(17),
            AuditEventId = reader.IsDBNull(18) ? null : reader.GetInt64(18),
            Artifacts = artifacts.ToList(),
        };
    }

    private static LearnedPcbVisualModelArtifact ReadLearnedPcbVisualModelArtifact(SqliteDataReader reader)
    {
        return new LearnedPcbVisualModelArtifact
        {
            Id = reader.GetInt64(0),
            ModelId = reader.GetString(1),
            ArtifactName = reader.GetString(2),
            ArtifactPath = reader.GetString(3),
            Sha256 = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
            CreatedAtUtc = ParseDateTime(reader.GetString(5)),
        };
    }

    private static ImageLearningInspectionResult ReadImageLearningInspectionResult(
        SqliteDataReader reader,
        IReadOnlyList<ImageLearningAnomalyRegion> regions)
    {
        return new ImageLearningInspectionResult
        {
            Id = reader.GetInt64(0),
            ResultId = reader.GetString(1),
            ProjectId = reader.GetString(2),
            ModelId = reader.GetString(3),
            ProjectImageId = reader.GetInt64(4),
            ImageSha256 = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
            ImagePath = reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
            CreatedAtUtc = ParseDateTime(reader.GetString(7)),
            Verdict = reader.GetString(8),
            AnomalyScore = reader.GetDouble(9),
            DecisionReason = reader.IsDBNull(10) ? string.Empty : reader.GetString(10),
            OperatorId = reader.IsDBNull(11) ? "UNKNOWN" : reader.GetString(11),
            EvidenceMode = ParseImageLearningEvidenceMode(reader.GetString(12)),
            AnomalyRegions = regions.ToList(),
        };
    }

    private static ImageLearningAnomalyRegion ReadImageLearningAnomalyRegion(SqliteDataReader reader)
    {
        return new ImageLearningAnomalyRegion
        {
            Id = reader.GetInt64(0),
            InspectionResultId = reader.GetInt64(1),
            RegionId = reader.GetString(2),
            X = reader.GetDouble(3),
            Y = reader.GetDouble(4),
            Width = reader.GetDouble(5),
            Height = reader.GetDouble(6),
            Score = reader.GetDouble(7),
            AreaPixels = reader.IsDBNull(8) ? 0 : reader.GetInt32(8),
            Confidence = reader.IsDBNull(9) ? 0 : reader.GetDouble(9),
            Severity = reader.IsDBNull(10) ? "REVIEW" : reader.GetString(10),
            RegionType = reader.IsDBNull(11) ? "Anomaly" : reader.GetString(11),
            Reason = reader.IsDBNull(12) ? string.Empty : reader.GetString(12),
            Notes = reader.IsDBNull(13) ? string.Empty : reader.GetString(13),
        };
    }

    private static ImageLearningCalibrationResult ReadImageLearningCalibrationResult(SqliteDataReader reader)
    {
        return new ImageLearningCalibrationResult
        {
            Id = reader.GetInt64(0),
            CalibrationId = reader.GetString(1),
            ProjectId = reader.GetString(2),
            ModelId = reader.GetString(3),
            CreatedAtUtc = ParseDateTime(reader.GetString(4)),
            OkValidationCount = reader.GetInt32(5),
            NgValidationCount = reader.GetInt32(6),
            LearnedThreshold = reader.GetDouble(7),
            FalseCallTarget = reader.GetDouble(8),
            FalseCallRate = reader.GetDouble(9),
            PossibleEscapeRate = reader.GetDouble(10),
            Status = reader.GetString(11),
            Summary = reader.IsDBNull(12) ? string.Empty : reader.GetString(12),
            HeldOutOkCount = reader.GetInt32(13),
            HeldOutFalseCalls = reader.GetInt32(14),
            HeldOutFalseCallRate = reader.IsDBNull(15) ? null : reader.GetDouble(15),
        };
    }

    private static ImageLearningComparisonResult ReadImageLearningComparisonResult(SqliteDataReader reader)
    {
        return new ImageLearningComparisonResult
        {
            Id = reader.GetInt64(0),
            ComparisonId = reader.GetString(1),
            ProjectId = reader.GetString(2),
            ModelId = reader.GetString(3),
            ProjectImageId = reader.GetInt64(4),
            ImageSha256 = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
            CreatedAtUtc = ParseDateTime(reader.GetString(6)),
            DifferenceScore = reader.GetDouble(7),
            AnomalyScore = reader.GetDouble(8),
            Verdict = reader.GetString(9),
            Summary = reader.IsDBNull(10) ? string.Empty : reader.GetString(10),
        };
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

    private static CustomerPilotSessionRecord ReadCustomerPilotSession(SqliteDataReader reader)
    {
        return new CustomerPilotSessionRecord
        {
            Id = reader.GetInt64(reader.GetOrdinal("Id")),
            SessionId = reader.GetString(reader.GetOrdinal("SessionId")),
            DeploymentProfile = Enum.TryParse<DeploymentProfile>(reader.GetString(reader.GetOrdinal("DeploymentProfile")), out var profile)
                ? profile
                : DeploymentProfile.Stage1ImageValidation,
            Status = reader.GetString(reader.GetOrdinal("Status")),
            DatasetFolder = reader.GetString(reader.GetOrdinal("DatasetFolder")),
            ManifestPath = reader.GetString(reader.GetOrdinal("ManifestPath")),
            OperatorId = reader.GetString(reader.GetOrdinal("OperatorId")),
            CreatedAtUtc = ParseDateTime(reader.GetString(reader.GetOrdinal("CreatedAtUtc"))),
            UpdatedAtUtc = ParseDateTime(reader.GetString(reader.GetOrdinal("UpdatedAtUtc"))),
            CompletedAtUtc = reader.IsDBNull(reader.GetOrdinal("CompletedAtUtc"))
                ? null
                : ParseDateTime(reader.GetString(reader.GetOrdinal("CompletedAtUtc"))),
        };
    }

    private static CustomerPilotStepRecord ReadCustomerPilotStep(SqliteDataReader reader)
    {
        return new CustomerPilotStepRecord
        {
            Id = reader.GetInt64(reader.GetOrdinal("Id")),
            SessionId = reader.GetInt64(reader.GetOrdinal("SessionId")),
            StepKey = Enum.TryParse<CustomerPilotStepKind>(reader.GetString(reader.GetOrdinal("StepKey")), out var step)
                ? step
                : CustomerPilotStepKind.ConfirmDeploymentProfile,
            StepOrder = reader.GetInt32(reader.GetOrdinal("StepOrder")),
            Status = Enum.TryParse<CustomerPilotStepStatus>(reader.GetString(reader.GetOrdinal("Status")), out var status)
                ? status
                : CustomerPilotStepStatus.NotStarted,
            EvidencePath = reader.GetString(reader.GetOrdinal("EvidencePath")),
            Messages = DeserializeOrDefault(reader.GetString(reader.GetOrdinal("MessagesJson")), new List<string>()),
            Waived = reader.GetInt32(reader.GetOrdinal("Waived")) != 0,
            WaiverReason = reader.GetString(reader.GetOrdinal("WaiverReason")),
            WaivedBy = reader.GetString(reader.GetOrdinal("WaivedBy")),
            WaivedAtUtc = reader.IsDBNull(reader.GetOrdinal("WaivedAtUtc"))
                ? null
                : ParseDateTime(reader.GetString(reader.GetOrdinal("WaivedAtUtc"))),
            UpdatedAtUtc = ParseDateTime(reader.GetString(reader.GetOrdinal("UpdatedAtUtc"))),
        };
    }

    private static PilotIssue ReadPilotIssue(SqliteDataReader reader)
    {
        var categoryText = reader.GetString(reader.GetOrdinal("Category"));
        var statusText = reader.GetString(reader.GetOrdinal("Status"));
        return new PilotIssue
        {
            IssueId = reader.GetString(reader.GetOrdinal("IssueId")),
            CreatedAtUtc = ParseDateTime(reader.GetString(reader.GetOrdinal("CreatedAtUtc"))),
            Category = Enum.TryParse<PilotIssueCategory>(categoryText, ignoreCase: true, out var category) ? category : PilotIssueCategory.Other,
            Severity = reader.GetString(reader.GetOrdinal("Severity")),
            BoardModel = reader.GetString(reader.GetOrdinal("BoardModel")),
            LotId = reader.GetString(reader.GetOrdinal("LotId")),
            ImagePath = reader.GetString(reader.GetOrdinal("ImagePath")),
            PageName = GetStringIfColumnExists(reader, "PageName"),
            ReproductionSteps = GetStringIfColumnExists(reader, "ReproductionSteps"),
            ExpectedBehavior = GetStringIfColumnExists(reader, "ExpectedBehavior"),
            ActualBehavior = GetStringIfColumnExists(reader, "ActualBehavior"),
            ScreenshotPath = GetStringIfColumnExists(reader, "ScreenshotPath"),
            RelatedInspectionId = reader.GetString(reader.GetOrdinal("RelatedInspectionId")),
            RelatedAcceptanceRunId = reader.GetString(reader.GetOrdinal("RelatedAcceptanceRunId")),
            Status = Enum.TryParse<PilotIssueStatus>(statusText, ignoreCase: true, out var status) ? status : PilotIssueStatus.Open,
            Owner = reader.GetString(reader.GetOrdinal("Owner")),
            Notes = reader.GetString(reader.GetOrdinal("Notes")),
            Resolution = reader.GetString(reader.GetOrdinal("Resolution")),
            ClosedAtUtc = reader.IsDBNull(reader.GetOrdinal("ClosedAtUtc"))
                ? null
                : ParseDateTime(reader.GetString(reader.GetOrdinal("ClosedAtUtc"))),
        };
    }

    private static string GetStringIfColumnExists(SqliteDataReader reader, string columnName)
    {
        for (var i = 0; i < reader.FieldCount; i++)
        {
            if (string.Equals(reader.GetName(i), columnName, StringComparison.OrdinalIgnoreCase))
                return reader.IsDBNull(i) ? string.Empty : reader.GetString(i);
        }

        return string.Empty;
    }

    private static PilotIssueEvent ReadPilotIssueEvent(SqliteDataReader reader)
        => new()
        {
            Id = reader.GetInt64(reader.GetOrdinal("Id")),
            IssueId = reader.GetString(reader.GetOrdinal("IssueId")),
            CreatedAtUtc = ParseDateTime(reader.GetString(reader.GetOrdinal("CreatedAtUtc"))),
            EventType = reader.GetString(reader.GetOrdinal("EventType")),
            OperatorId = reader.GetString(reader.GetOrdinal("OperatorId")),
            Message = reader.GetString(reader.GetOrdinal("Message")),
            PreviousStatus = reader.GetString(reader.GetOrdinal("PreviousStatus")),
            NewStatus = reader.GetString(reader.GetOrdinal("NewStatus")),
        };

    private static void AddCustomerPilotSessionParameters(SqliteCommand command, CustomerPilotSessionRecord session)
    {
        command.Parameters.AddWithValue("$sessionId", session.SessionId);
        command.Parameters.AddWithValue("$deploymentProfile", session.DeploymentProfile.ToString());
        command.Parameters.AddWithValue("$status", string.IsNullOrWhiteSpace(session.Status) ? "InProgress" : session.Status);
        command.Parameters.AddWithValue("$datasetFolder", session.DatasetFolder?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$manifestPath", session.ManifestPath?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$operatorId", string.IsNullOrWhiteSpace(session.OperatorId) ? "UNKNOWN" : session.OperatorId.Trim());
        command.Parameters.AddWithValue("$createdAtUtc", session.CreatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updatedAtUtc", session.UpdatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$completedAtUtc", session.CompletedAtUtc is { } completed
            ? completed.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
            : DBNull.Value);
    }

    private static void AddCustomerPilotStepParameters(SqliteCommand command, CustomerPilotStepRecord step)
    {
        command.Parameters.AddWithValue("$sessionId", step.SessionId);
        command.Parameters.AddWithValue("$stepKey", step.StepKey.ToString());
        command.Parameters.AddWithValue("$stepOrder", step.StepOrder);
        command.Parameters.AddWithValue("$status", step.Status.ToString());
        command.Parameters.AddWithValue("$evidencePath", step.EvidencePath?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$messagesJson", JsonSerializer.Serialize(step.Messages ?? new List<string>()));
        command.Parameters.AddWithValue("$waived", step.Waived ? 1 : 0);
        command.Parameters.AddWithValue("$waiverReason", step.WaiverReason?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$waivedBy", step.WaivedBy?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$waivedAtUtc", step.WaivedAtUtc is { } waivedAt
            ? waivedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
            : DBNull.Value);
        command.Parameters.AddWithValue("$updatedAtUtc", step.UpdatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
    }

    private static void AddPilotIssueParameters(SqliteCommand command, PilotIssue issue)
    {
        command.Parameters.AddWithValue("$issueId", issue.IssueId);
        command.Parameters.AddWithValue("$createdAtUtc", issue.CreatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$category", issue.Category.ToString());
        command.Parameters.AddWithValue("$severity", string.IsNullOrWhiteSpace(issue.Severity) ? "Medium" : issue.Severity.Trim());
        command.Parameters.AddWithValue("$boardModel", issue.BoardModel?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$lotId", issue.LotId?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$imagePath", issue.ImagePath?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$pageName", issue.PageName?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$reproductionSteps", issue.ReproductionSteps?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$expectedBehavior", issue.ExpectedBehavior?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$actualBehavior", issue.ActualBehavior?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$screenshotPath", issue.ScreenshotPath?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$relatedInspectionId", issue.RelatedInspectionId?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$relatedAcceptanceRunId", issue.RelatedAcceptanceRunId?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$status", issue.Status.ToString());
        command.Parameters.AddWithValue("$owner", issue.Owner?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$notes", issue.Notes?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$resolution", issue.Resolution?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$closedAtUtc", issue.ClosedAtUtc is { } closed
            ? closed.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
            : DBNull.Value);
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
            reader.IsDBNull(32) ? string.Empty : reader.GetString(32),
            reader.IsDBNull(33) ? null : ParseDateTime(reader.GetString(33)),
            reader.IsDBNull(34) ? string.Empty : reader.GetString(34),
            reader.IsDBNull(35) ? null : ParseDateTime(reader.GetString(35)));
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

    private static DefectTaxonomyRecord ReadDefectTaxonomy(SqliteDataReader reader)
        => new()
        {
            TaxonomyId = reader.GetString(0),
            Name = reader.GetString(1),
            CustomerName = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
            IsActive = reader.GetInt32(3) != 0,
            CreatedAtUtc = ParseDateTime(reader.GetString(4)),
            UpdatedAtUtc = ParseDateTime(reader.GetString(5)),
        };

    private static IReadOnlyList<DefectTaxonomyEntry> GetDefectTaxonomyEntries(SqliteConnection connection, string taxonomyId)
    {
        var entries = new List<DefectTaxonomyEntry>();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT TaxonomyId, CanonicalClass, CustomerLabel, ModelLabelId, IsRequired, SortOrder, IsActive, Severity, DetectionMethod
            FROM DefectTaxonomyEntries
            WHERE TaxonomyId = $taxonomyId
            ORDER BY SortOrder, CanonicalClass;
            """;
        command.Parameters.AddWithValue("$taxonomyId", taxonomyId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            entries.Add(new DefectTaxonomyEntry
            {
                TaxonomyId = reader.GetString(0),
                CanonicalClass = reader.GetString(1),
                CustomerLabel = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                ModelLabelId = reader.IsDBNull(3) ? null : reader.GetInt32(3),
                IsRequired = reader.GetInt32(4) != 0,
                SortOrder = reader.GetInt32(5),
                IsActive = reader.GetInt32(6) != 0,
                Severity = reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
                DetectionMethod = reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
            });
        }

        return entries;
    }

    private static IReadOnlyList<DefectClassAliasRecord> GetDefectClassAliases(SqliteConnection connection, string taxonomyId)
    {
        var aliases = new List<DefectClassAliasRecord>();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT TaxonomyId, Alias, CanonicalClass
            FROM DefectClassAliases
            WHERE TaxonomyId = $taxonomyId
            ORDER BY Alias;
            """;
        command.Parameters.AddWithValue("$taxonomyId", taxonomyId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            aliases.Add(new DefectClassAliasRecord
            {
                TaxonomyId = reader.GetString(0),
                Alias = reader.GetString(1),
                CanonicalClass = reader.GetString(2),
            });
        }

        return aliases;
    }

    private static IReadOnlyList<MesDefectCodeMappingRecord> GetMesDefectCodeMappings(SqliteConnection connection, string taxonomyId)
    {
        var mappings = new List<MesDefectCodeMappingRecord>();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT TaxonomyId, CanonicalClass, MesCode
            FROM MesDefectCodeMappings
            WHERE TaxonomyId = $taxonomyId
            ORDER BY CanonicalClass;
            """;
        command.Parameters.AddWithValue("$taxonomyId", taxonomyId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            mappings.Add(new MesDefectCodeMappingRecord
            {
                TaxonomyId = reader.GetString(0),
                CanonicalClass = reader.GetString(1),
                MesCode = reader.GetString(2),
            });
        }

        return mappings;
    }

    private static void DeleteTaxonomyChildren(SqliteConnection connection, SqliteTransaction transaction, string taxonomyId)
    {
        foreach (var table in new[] { "DefectTaxonomyEntries", "DefectClassAliases", "MesDefectCodeMappings" })
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"DELETE FROM {table} WHERE TaxonomyId = $taxonomyId;";
            command.Parameters.AddWithValue("$taxonomyId", taxonomyId);
            command.ExecuteNonQuery();
        }
    }

    private static void InsertDefectTaxonomyEntry(SqliteConnection connection, SqliteTransaction transaction, string taxonomyId, DefectTaxonomyEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.CanonicalClass))
            return;

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO DefectTaxonomyEntries
                (TaxonomyId, CanonicalClass, CustomerLabel, ModelLabelId, IsRequired, Severity, DetectionMethod, SortOrder, IsActive)
            VALUES
                ($taxonomyId, $canonicalClass, $customerLabel, $modelLabelId, $isRequired, $severity, $detectionMethod, $sortOrder, $isActive);
            """;
        command.Parameters.AddWithValue("$taxonomyId", taxonomyId);
        command.Parameters.AddWithValue("$canonicalClass", entry.CanonicalClass.Trim());
        command.Parameters.AddWithValue("$customerLabel", string.IsNullOrWhiteSpace(entry.CustomerLabel) ? entry.CanonicalClass.Trim() : entry.CustomerLabel.Trim());
        command.Parameters.AddWithValue("$modelLabelId", entry.ModelLabelId is { } id ? (object)id : DBNull.Value);
        command.Parameters.AddWithValue("$isRequired", entry.IsRequired ? 1 : 0);
        command.Parameters.AddWithValue("$severity", (entry.Severity ?? string.Empty).Trim());
        command.Parameters.AddWithValue("$detectionMethod", (entry.DetectionMethod ?? string.Empty).Trim());
        command.Parameters.AddWithValue("$sortOrder", entry.SortOrder);
        command.Parameters.AddWithValue("$isActive", entry.IsActive ? 1 : 0);
        command.ExecuteNonQuery();
    }

    private static void InsertDefectClassAlias(SqliteConnection connection, SqliteTransaction transaction, string taxonomyId, DefectClassAliasRecord alias)
    {
        if (string.IsNullOrWhiteSpace(alias.Alias) || string.IsNullOrWhiteSpace(alias.CanonicalClass))
            return;

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT OR IGNORE INTO DefectClassAliases (TaxonomyId, Alias, CanonicalClass)
            VALUES ($taxonomyId, $alias, $canonicalClass);
            """;
        command.Parameters.AddWithValue("$taxonomyId", taxonomyId);
        command.Parameters.AddWithValue("$alias", alias.Alias.Trim());
        command.Parameters.AddWithValue("$canonicalClass", alias.CanonicalClass.Trim());
        command.ExecuteNonQuery();
    }

    private static void InsertMesDefectCodeMapping(SqliteConnection connection, SqliteTransaction transaction, string taxonomyId, MesDefectCodeMappingRecord mapping)
    {
        if (string.IsNullOrWhiteSpace(mapping.CanonicalClass) || string.IsNullOrWhiteSpace(mapping.MesCode))
            return;

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO MesDefectCodeMappings (TaxonomyId, CanonicalClass, MesCode)
            VALUES ($taxonomyId, $canonicalClass, $mesCode);
            """;
        command.Parameters.AddWithValue("$taxonomyId", taxonomyId);
        command.Parameters.AddWithValue("$canonicalClass", mapping.CanonicalClass.Trim());
        command.Parameters.AddWithValue("$mesCode", mapping.MesCode.Trim());
        command.ExecuteNonQuery();
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

    private static void BindImageLearningProject(SqliteCommand command, ImageLearningProject project)
    {
        command.Parameters.AddWithValue("$projectId", project.ProjectId);
        command.Parameters.AddWithValue("$projectName", project.ProjectName);
        command.Parameters.AddWithValue("$boardModel", project.BoardModel ?? string.Empty);
        command.Parameters.AddWithValue("$description", project.Description ?? string.Empty);
        command.Parameters.AddWithValue("$evidenceMode", project.EvidenceMode.ToString());
        command.Parameters.AddWithValue("$createdBy", project.CreatedBy ?? "UNKNOWN");
        command.Parameters.AddWithValue("$createdAtUtc", project.CreatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updatedAtUtc", project.UpdatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$isArchived", project.IsArchived ? 1 : 0);
        command.Parameters.AddWithValue("$archivedBy", project.ArchivedBy ?? string.Empty);
        command.Parameters.AddWithValue("$archivedAtUtc", project.ArchivedAtUtc is { } archivedAt ? (object)archivedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) : DBNull.Value);
        command.Parameters.AddWithValue("$archiveReason", project.ArchiveReason ?? string.Empty);
    }

    private static void BindImageLearningProjectImage(SqliteCommand command, ImageLearningProjectImage image)
    {
        command.Parameters.AddWithValue("$projectId", image.ProjectId);
        command.Parameters.AddWithValue("$role", image.Role.ToString());
        command.Parameters.AddWithValue("$originalPath", image.OriginalPath ?? string.Empty);
        command.Parameters.AddWithValue("$vaultPath", image.VaultPath ?? string.Empty);
        command.Parameters.AddWithValue("$fileName", image.FileName ?? string.Empty);
        command.Parameters.AddWithValue("$sha256", image.Sha256 ?? string.Empty);
        command.Parameters.AddWithValue("$boardModel", image.BoardModel ?? string.Empty);
        command.Parameters.AddWithValue("$lotId", image.LotId ?? string.Empty);
        command.Parameters.AddWithValue("$viewType", image.ViewType ?? string.Empty);
        command.Parameters.AddWithValue("$width", image.Width);
        command.Parameters.AddWithValue("$height", image.Height);
        command.Parameters.AddWithValue("$importedBy", image.ImportedBy ?? "UNKNOWN");
        command.Parameters.AddWithValue("$importedAtUtc", image.ImportedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$imageLevelTruth", NormalizeImageLevelTruth(image.ImageLevelTruth));
        command.Parameters.AddWithValue("$notes", image.Notes ?? string.Empty);
    }

    private static void BindLearnedPcbVisualModel(SqliteCommand command, LearnedPcbVisualModel model)
    {
        command.Parameters.AddWithValue("$modelId", model.ModelId);
        command.Parameters.AddWithValue("$modelVersion", model.ModelVersion);
        command.Parameters.AddWithValue("$createdAtUtc", model.CreatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$projectId", model.ProjectId ?? string.Empty);
        command.Parameters.AddWithValue("$goldenCount", model.GoldenCount);
        command.Parameters.AddWithValue("$okLearningCount", model.OkLearningCount);
        command.Parameters.AddWithValue("$okValidationCount", model.OkValidationCount);
        command.Parameters.AddWithValue("$inputWidth", model.InputWidth);
        command.Parameters.AddWithValue("$inputHeight", model.InputHeight);
        command.Parameters.AddWithValue("$alignmentMode", model.AlignmentMode ?? string.Empty);
        command.Parameters.AddWithValue("$brightnessNormalizationMode", model.BrightnessNormalizationMode ?? string.Empty);
        command.Parameters.AddWithValue("$learnedThreshold", model.LearnedThreshold);
        command.Parameters.AddWithValue("$falseCallTarget", model.FalseCallTarget);
        command.Parameters.AddWithValue("$falseCallRate", model.FalseCallRate);
        command.Parameters.AddWithValue("$possibleEscapeRate", model.PossibleEscapeRate);
        command.Parameters.AddWithValue("$evidenceMode", model.EvidenceMode.ToString());
        command.Parameters.AddWithValue("$createdBy", model.CreatedBy ?? "UNKNOWN");
        command.Parameters.AddWithValue("$auditEventId", model.AuditEventId is { } id ? (object)id : DBNull.Value);
    }

    private static void BindLearnedPcbVisualModelArtifact(SqliteCommand command, LearnedPcbVisualModelArtifact artifact)
    {
        command.Parameters.AddWithValue("$modelId", artifact.ModelId);
        command.Parameters.AddWithValue("$artifactName", artifact.ArtifactName ?? string.Empty);
        command.Parameters.AddWithValue("$artifactPath", artifact.ArtifactPath ?? string.Empty);
        command.Parameters.AddWithValue("$sha256", artifact.Sha256 ?? string.Empty);
        command.Parameters.AddWithValue("$createdAtUtc", artifact.CreatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
    }

    private static void BindImageLearningInspectionResult(SqliteCommand command, ImageLearningInspectionResult result)
    {
        command.Parameters.AddWithValue("$resultId", result.ResultId);
        command.Parameters.AddWithValue("$projectId", result.ProjectId ?? string.Empty);
        command.Parameters.AddWithValue("$modelId", result.ModelId ?? string.Empty);
        command.Parameters.AddWithValue("$projectImageId", result.ProjectImageId);
        command.Parameters.AddWithValue("$imageSha256", result.ImageSha256 ?? string.Empty);
        command.Parameters.AddWithValue("$imagePath", result.ImagePath ?? string.Empty);
        command.Parameters.AddWithValue("$createdAtUtc", result.CreatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$verdict", result.Verdict ?? "REVIEW");
        command.Parameters.AddWithValue("$anomalyScore", result.AnomalyScore);
        command.Parameters.AddWithValue("$decisionReason", result.DecisionReason ?? string.Empty);
        command.Parameters.AddWithValue("$operatorId", result.OperatorId ?? "UNKNOWN");
        command.Parameters.AddWithValue("$evidenceMode", result.EvidenceMode.ToString());
    }

    private static void BindImageLearningAnomalyRegion(SqliteCommand command, ImageLearningAnomalyRegion region)
    {
        command.Parameters.AddWithValue("$inspectionResultId", region.InspectionResultId);
        command.Parameters.AddWithValue("$regionId", region.RegionId ?? string.Empty);
        command.Parameters.AddWithValue("$x", region.X);
        command.Parameters.AddWithValue("$y", region.Y);
        command.Parameters.AddWithValue("$width", region.Width);
        command.Parameters.AddWithValue("$height", region.Height);
        command.Parameters.AddWithValue("$score", region.Score);
        command.Parameters.AddWithValue("$areaPixels", region.AreaPixels);
        command.Parameters.AddWithValue("$confidence", region.Confidence);
        command.Parameters.AddWithValue("$severity", region.Severity ?? "REVIEW");
        command.Parameters.AddWithValue("$regionType", region.RegionType ?? "Anomaly");
        command.Parameters.AddWithValue("$reason", region.Reason ?? string.Empty);
        command.Parameters.AddWithValue("$notes", region.Notes ?? string.Empty);
    }

    private static void BindImageLearningCalibrationResult(SqliteCommand command, ImageLearningCalibrationResult result)
    {
        command.Parameters.AddWithValue("$calibrationId", result.CalibrationId);
        command.Parameters.AddWithValue("$projectId", result.ProjectId ?? string.Empty);
        command.Parameters.AddWithValue("$modelId", result.ModelId ?? string.Empty);
        command.Parameters.AddWithValue("$createdAtUtc", result.CreatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$okValidationCount", result.OkValidationCount);
        command.Parameters.AddWithValue("$ngValidationCount", result.NgValidationCount);
        command.Parameters.AddWithValue("$learnedThreshold", result.LearnedThreshold);
        command.Parameters.AddWithValue("$falseCallTarget", result.FalseCallTarget);
        command.Parameters.AddWithValue("$falseCallRate", result.FalseCallRate);
        command.Parameters.AddWithValue("$possibleEscapeRate", result.PossibleEscapeRate);
        command.Parameters.AddWithValue("$status", result.Status ?? "REVIEW");
        command.Parameters.AddWithValue("$summary", result.Summary ?? string.Empty);
        command.Parameters.AddWithValue("$heldOutOkCount", result.HeldOutOkCount);
        command.Parameters.AddWithValue("$heldOutFalseCalls", result.HeldOutFalseCalls);
        command.Parameters.AddWithValue("$heldOutFalseCallRate", result.HeldOutFalseCallRate is { } rate ? rate : DBNull.Value);
    }

    private static void BindImageLearningComparisonResult(SqliteCommand command, ImageLearningComparisonResult result)
    {
        command.Parameters.AddWithValue("$comparisonId", result.ComparisonId);
        command.Parameters.AddWithValue("$projectId", result.ProjectId ?? string.Empty);
        command.Parameters.AddWithValue("$modelId", result.ModelId ?? string.Empty);
        command.Parameters.AddWithValue("$projectImageId", result.ProjectImageId);
        command.Parameters.AddWithValue("$imageSha256", result.ImageSha256 ?? string.Empty);
        command.Parameters.AddWithValue("$createdAtUtc", result.CreatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$differenceScore", result.DifferenceScore);
        command.Parameters.AddWithValue("$anomalyScore", result.AnomalyScore);
        command.Parameters.AddWithValue("$verdict", result.Verdict ?? "REVIEW");
        command.Parameters.AddWithValue("$summary", result.Summary ?? string.Empty);
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
        command.Parameters.AddWithValue("$deploymentWaiverRiskClassification", record.DeploymentWaiverRiskClassification ?? string.Empty);
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

    private static IReadOnlyList<LearnedPcbVisualModelArtifact> GetLearnedPcbVisualModelArtifacts(string modelId)
    {
        var artifacts = new List<LearnedPcbVisualModelArtifact>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, ModelId, ArtifactName, ArtifactPath, Sha256, CreatedAtUtc
            FROM LearnedPcbVisualModelArtifacts
            WHERE ModelId = $modelId
            ORDER BY ArtifactName ASC, Id ASC;
            """;
        command.Parameters.AddWithValue("$modelId", modelId);

        using var reader = command.ExecuteReader();
        while (reader.Read())
            artifacts.Add(ReadLearnedPcbVisualModelArtifact(reader));

        return artifacts;
    }

    private static IReadOnlyList<ImageLearningAnomalyRegion> GetImageLearningAnomalyRegions(long inspectionResultId)
    {
        var regions = new List<ImageLearningAnomalyRegion>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, InspectionResultId, RegionId, X, Y, Width, Height, Score, AreaPixels, Confidence, Severity, RegionType, Reason, Notes
            FROM ImageLearningAnomalyRegions
            WHERE InspectionResultId = $inspectionResultId
            ORDER BY Id ASC;
            """;
        command.Parameters.AddWithValue("$inspectionResultId", inspectionResultId);

        using var reader = command.ExecuteReader();
        while (reader.Read())
            regions.Add(ReadImageLearningAnomalyRegion(reader));

        return regions;
    }

    private static void ExecuteImageLearningDelete(SqliteConnection connection, SqliteTransaction transaction, string commandText, string projectId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        command.Parameters.AddWithValue("$projectId", projectId);
        command.ExecuteNonQuery();
    }

    private static ImageLearningImageRole ParseImageLearningImageRole(string value)
        => Enum.TryParse<ImageLearningImageRole>(value, ignoreCase: true, out var role)
            ? role
            : ImageLearningImageRole.Inspection;

    private static ImageLearningEvidenceMode ParseImageLearningEvidenceMode(string value)
        => Enum.TryParse<ImageLearningEvidenceMode>(value, ignoreCase: true, out var mode)
            ? mode
            : ImageLearningEvidenceMode.CustomerData;

    private static string NormalizeImageLevelTruth(string? value)
    {
        var text = value?.Trim();
        if (string.Equals(text, "OK", StringComparison.OrdinalIgnoreCase))
            return "OK";
        if (string.Equals(text, "NG", StringComparison.OrdinalIgnoreCase))
            return "NG";

        return "UNKNOWN";
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
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"String-list JSON deserialize fallback used: {ex.Message}");
            // Acceptance-run warnings/failures feed exported evidence. An empty list would
            // make a stored FAIL run look clean; keep a visible marker instead.
            return new List<string> { $"[Stored list could not be read: {ex.Message}]" };
        }
    }

    private static T DeserializeOrDefault<T>(string json, T fallback)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json) ?? fallback;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"JSON deserialize fallback used for {typeof(T).Name}: {ex.Message}");
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

    internal static void EnsureCustomerPilotTables(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS CustomerPilotSessions
            (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SessionId TEXT NOT NULL UNIQUE,
                DeploymentProfile TEXT NOT NULL,
                Status TEXT NOT NULL DEFAULT 'InProgress',
                DatasetFolder TEXT NOT NULL DEFAULT '',
                ManifestPath TEXT NOT NULL DEFAULT '',
                OperatorId TEXT NOT NULL DEFAULT 'UNKNOWN',
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL,
                CompletedAtUtc TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS CustomerPilotSteps
            (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SessionId INTEGER NOT NULL,
                StepKey TEXT NOT NULL,
                StepOrder INTEGER NOT NULL,
                Status TEXT NOT NULL,
                EvidencePath TEXT NOT NULL DEFAULT '',
                MessagesJson TEXT NOT NULL DEFAULT '[]',
                Waived INTEGER NOT NULL DEFAULT 0,
                WaiverReason TEXT NOT NULL DEFAULT '',
                WaivedBy TEXT NOT NULL DEFAULT '',
                WaivedAtUtc TEXT NULL,
                UpdatedAtUtc TEXT NOT NULL,
                UNIQUE(SessionId, StepKey),
                FOREIGN KEY (SessionId) REFERENCES CustomerPilotSessions(Id)
            );

            CREATE INDEX IF NOT EXISTS IX_CustomerPilotSessions_Status_Updated ON CustomerPilotSessions(Status, UpdatedAtUtc);
            CREATE INDEX IF NOT EXISTS IX_CustomerPilotSteps_Session_Order ON CustomerPilotSteps(SessionId, StepOrder);
            """;
        command.ExecuteNonQuery();
    }

    internal static void EnsurePilotIssueTables(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS PilotIssues
            (
                IssueId TEXT PRIMARY KEY,
                CreatedAtUtc TEXT NOT NULL,
                Category TEXT NOT NULL,
                Severity TEXT NOT NULL DEFAULT 'Medium',
                BoardModel TEXT NOT NULL DEFAULT '',
                LotId TEXT NOT NULL DEFAULT '',
                ImagePath TEXT NOT NULL DEFAULT '',
                PageName TEXT NOT NULL DEFAULT '',
                ReproductionSteps TEXT NOT NULL DEFAULT '',
                ExpectedBehavior TEXT NOT NULL DEFAULT '',
                ActualBehavior TEXT NOT NULL DEFAULT '',
                ScreenshotPath TEXT NOT NULL DEFAULT '',
                RelatedInspectionId TEXT NOT NULL DEFAULT '',
                RelatedAcceptanceRunId TEXT NOT NULL DEFAULT '',
                Status TEXT NOT NULL DEFAULT 'Open',
                Owner TEXT NOT NULL DEFAULT '',
                Notes TEXT NOT NULL DEFAULT '',
                Resolution TEXT NOT NULL DEFAULT '',
                ClosedAtUtc TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS PilotIssueEvents
            (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                IssueId TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                EventType TEXT NOT NULL,
                OperatorId TEXT NOT NULL DEFAULT 'UNKNOWN',
                Message TEXT NOT NULL DEFAULT '',
                PreviousStatus TEXT NOT NULL DEFAULT '',
                NewStatus TEXT NOT NULL DEFAULT '',
                FOREIGN KEY (IssueId) REFERENCES PilotIssues(IssueId)
            );

            CREATE INDEX IF NOT EXISTS IX_PilotIssues_Status_Severity ON PilotIssues(Status, Severity);
            CREATE INDEX IF NOT EXISTS IX_PilotIssues_Category ON PilotIssues(Category);
            CREATE INDEX IF NOT EXISTS IX_PilotIssues_Board_Lot ON PilotIssues(BoardModel, LotId);
            CREATE INDEX IF NOT EXISTS IX_PilotIssueEvents_IssueId ON PilotIssueEvents(IssueId, CreatedAtUtc);
            """;
        command.ExecuteNonQuery();
        AddColumnIfMissing(connection, transaction, "PilotIssues", "PageName", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(connection, transaction, "PilotIssues", "ReproductionSteps", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(connection, transaction, "PilotIssues", "ExpectedBehavior", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(connection, transaction, "PilotIssues", "ActualBehavior", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(connection, transaction, "PilotIssues", "ScreenshotPath", "TEXT NOT NULL DEFAULT ''");
        using var indexCommand = connection.CreateCommand();
        indexCommand.Transaction = transaction;
        indexCommand.CommandText = "CREATE INDEX IF NOT EXISTS IX_PilotIssues_PageName ON PilotIssues(PageName);";
        indexCommand.ExecuteNonQuery();
    }

    internal static void EnsureDefectTaxonomyTables(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS DefectTaxonomies
            (
                TaxonomyId TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                CustomerName TEXT NOT NULL DEFAULT '',
                IsActive INTEGER NOT NULL DEFAULT 1,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS DefectTaxonomyEntries
            (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                TaxonomyId TEXT NOT NULL,
                CanonicalClass TEXT NOT NULL,
                CustomerLabel TEXT NOT NULL DEFAULT '',
                ModelLabelId INTEGER NULL,
                IsRequired INTEGER NOT NULL DEFAULT 1,
                Severity TEXT NOT NULL DEFAULT '',
                DetectionMethod TEXT NOT NULL DEFAULT '',
                SortOrder INTEGER NOT NULL DEFAULT 0,
                IsActive INTEGER NOT NULL DEFAULT 1,
                UNIQUE(TaxonomyId, CanonicalClass),
                FOREIGN KEY (TaxonomyId) REFERENCES DefectTaxonomies(TaxonomyId)
            );

            CREATE TABLE IF NOT EXISTS DefectClassAliases
            (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                TaxonomyId TEXT NOT NULL,
                Alias TEXT NOT NULL,
                CanonicalClass TEXT NOT NULL,
                UNIQUE(TaxonomyId, Alias),
                FOREIGN KEY (TaxonomyId) REFERENCES DefectTaxonomies(TaxonomyId)
            );

            CREATE TABLE IF NOT EXISTS MesDefectCodeMappings
            (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                TaxonomyId TEXT NOT NULL,
                CanonicalClass TEXT NOT NULL,
                MesCode TEXT NOT NULL,
                UNIQUE(TaxonomyId, CanonicalClass),
                FOREIGN KEY (TaxonomyId) REFERENCES DefectTaxonomies(TaxonomyId)
            );

            CREATE INDEX IF NOT EXISTS IX_DefectTaxonomyEntries_Taxonomy ON DefectTaxonomyEntries(TaxonomyId, SortOrder);
            CREATE INDEX IF NOT EXISTS IX_DefectClassAliases_Taxonomy ON DefectClassAliases(TaxonomyId, Alias);
            CREATE INDEX IF NOT EXISTS IX_MesDefectCodeMappings_Taxonomy ON MesDefectCodeMappings(TaxonomyId, CanonicalClass);
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Brings the shipped default defect taxonomy up to the current classification catalogue in
    /// place, so an existing install gains newly catalogued classes, severities, and detection
    /// methods without an operator re-import.
    ///
    /// Scope is deliberately limited to <c>default-aoi-defect-taxonomy</c>: customer-imported
    /// taxonomies mint their own IDs and are never rewritten. Nothing happens when the default
    /// taxonomy has not been seeded yet — first-run seeding already writes the current catalogue.
    /// </summary>
    public static void UpgradeDefaultDefectTaxonomy(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        const string defaultTaxonomyId = "default-aoi-defect-taxonomy";

        using (var exists = connection.CreateCommand())
        {
            exists.Transaction = transaction;
            exists.CommandText = "SELECT COUNT(1) FROM DefectTaxonomies WHERE TaxonomyId = $id;";
            exists.Parameters.AddWithValue("$id", defaultTaxonomyId);
            if (Convert.ToInt64(exists.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture) == 0)
                return;
        }

        // The default taxonomy is system-owned and has no operator edit path, so rewriting its
        // children is deterministic rather than destructive.
        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText =
                """
                DELETE FROM DefectTaxonomyEntries WHERE TaxonomyId = $id;
                DELETE FROM DefectClassAliases WHERE TaxonomyId = $id;
                DELETE FROM MesDefectCodeMappings WHERE TaxonomyId = $id;
                """;
            delete.Parameters.AddWithValue("$id", defaultTaxonomyId);
            delete.ExecuteNonQuery();
        }

        for (var i = 0; i < DefectClassCatalog.Default.Count; i++)
        {
            var definition = DefectClassCatalog.Default[i];

            using (var entry = connection.CreateCommand())
            {
                entry.Transaction = transaction;
                entry.CommandText =
                    """
                    INSERT INTO DefectTaxonomyEntries
                        (TaxonomyId, CanonicalClass, CustomerLabel, ModelLabelId, IsRequired, Severity, DetectionMethod, SortOrder, IsActive)
                    VALUES
                        ($id, $canonicalClass, $canonicalClass, $modelLabelId, $isRequired, $severity, $detectionMethod, $sortOrder, 1);
                    """;
                entry.Parameters.AddWithValue("$id", defaultTaxonomyId);
                entry.Parameters.AddWithValue("$canonicalClass", definition.CanonicalClass);
                entry.Parameters.AddWithValue("$modelLabelId", definition.ModelLabelId);
                entry.Parameters.AddWithValue("$isRequired", definition.IsRequired ? 1 : 0);
                entry.Parameters.AddWithValue("$severity", definition.Severity);
                entry.Parameters.AddWithValue("$detectionMethod", definition.DetectionMethod);
                entry.Parameters.AddWithValue("$sortOrder", i);
                entry.ExecuteNonQuery();
            }

            using (var mes = connection.CreateCommand())
            {
                mes.Transaction = transaction;
                mes.CommandText =
                    """
                    INSERT OR IGNORE INTO MesDefectCodeMappings (TaxonomyId, CanonicalClass, MesCode)
                    VALUES ($id, $canonicalClass, $mesCode);
                    """;
                mes.Parameters.AddWithValue("$id", defaultTaxonomyId);
                mes.Parameters.AddWithValue("$canonicalClass", definition.CanonicalClass);
                mes.Parameters.AddWithValue("$mesCode", definition.MesCode);
                mes.ExecuteNonQuery();
            }

            foreach (var alias in definition.Aliases.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                using var aliasCommand = connection.CreateCommand();
                aliasCommand.Transaction = transaction;
                aliasCommand.CommandText =
                    """
                    INSERT OR IGNORE INTO DefectClassAliases (TaxonomyId, Alias, CanonicalClass)
                    VALUES ($id, $alias, $canonicalClass);
                    """;
                aliasCommand.Parameters.AddWithValue("$id", defaultTaxonomyId);
                aliasCommand.Parameters.AddWithValue("$alias", alias);
                aliasCommand.Parameters.AddWithValue("$canonicalClass", definition.CanonicalClass);
                aliasCommand.ExecuteNonQuery();
            }
        }
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
                DeploymentWaiverRiskClassification TEXT NOT NULL DEFAULT '',
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
        AddColumnIfMissing(connection, transaction, "ModelRegistry", "DeploymentWaiverRiskClassification", "TEXT NOT NULL DEFAULT ''");
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

    internal static void EnsureTrainingDatasetTables(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS TrainingSamples
            (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SourceImagePath TEXT NOT NULL,
                VaultPath TEXT NOT NULL,
                Label TEXT NOT NULL,
                Notes TEXT NULL,
                CreatedAtUtc TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_TrainingSamples_CreatedAtUtc ON TrainingSamples(CreatedAtUtc);
            CREATE INDEX IF NOT EXISTS IX_TrainingSamples_Label ON TrainingSamples(Label);
            """;
        command.ExecuteNonQuery();
    }

    internal static void EnsureImageLearningTables(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS ImageLearningProjects
            (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ProjectId TEXT NOT NULL UNIQUE,
                ProjectName TEXT NOT NULL,
                BoardModel TEXT NOT NULL DEFAULT '',
                Description TEXT NOT NULL DEFAULT '',
                EvidenceMode TEXT NOT NULL DEFAULT 'CustomerData',
                CreatedBy TEXT NOT NULL DEFAULT 'UNKNOWN',
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL,
                IsArchived INTEGER NOT NULL DEFAULT 0,
                ArchivedBy TEXT NOT NULL DEFAULT '',
                ArchivedAtUtc TEXT NULL,
                ArchiveReason TEXT NOT NULL DEFAULT ''
            );

            CREATE TABLE IF NOT EXISTS ImageLearningProjectImages
            (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ProjectId TEXT NOT NULL,
                Role TEXT NOT NULL,
                OriginalPath TEXT NOT NULL,
                VaultPath TEXT NOT NULL,
                FileName TEXT NOT NULL,
                Sha256 TEXT NOT NULL,
                BoardModel TEXT NOT NULL DEFAULT '',
                LotId TEXT NOT NULL DEFAULT '',
                ViewType TEXT NOT NULL DEFAULT '',
                Width INTEGER NOT NULL DEFAULT 0,
                Height INTEGER NOT NULL DEFAULT 0,
                ImportedBy TEXT NOT NULL DEFAULT 'UNKNOWN',
                ImportedAtUtc TEXT NOT NULL,
                ImageLevelTruth TEXT NOT NULL DEFAULT 'UNKNOWN',
                Notes TEXT NOT NULL DEFAULT '',
                FOREIGN KEY (ProjectId) REFERENCES ImageLearningProjects(ProjectId)
            );

            CREATE TABLE IF NOT EXISTS LearnedPcbVisualModels
            (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ModelId TEXT NOT NULL UNIQUE,
                ModelVersion TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                ProjectId TEXT NOT NULL,
                GoldenCount INTEGER NOT NULL DEFAULT 0,
                OkLearningCount INTEGER NOT NULL DEFAULT 0,
                OkValidationCount INTEGER NOT NULL DEFAULT 0,
                InputWidth INTEGER NOT NULL DEFAULT 0,
                InputHeight INTEGER NOT NULL DEFAULT 0,
                AlignmentMode TEXT NOT NULL DEFAULT '',
                BrightnessNormalizationMode TEXT NOT NULL DEFAULT '',
                LearnedThreshold REAL NOT NULL DEFAULT 0,
                FalseCallTarget REAL NOT NULL DEFAULT 0,
                FalseCallRate REAL NOT NULL DEFAULT 0,
                PossibleEscapeRate REAL NOT NULL DEFAULT 0,
                EvidenceMode TEXT NOT NULL DEFAULT 'CustomerData',
                CreatedBy TEXT NOT NULL DEFAULT 'UNKNOWN',
                AuditEventId INTEGER NULL,
                FOREIGN KEY (ProjectId) REFERENCES ImageLearningProjects(ProjectId)
            );

            CREATE TABLE IF NOT EXISTS LearnedPcbVisualModelArtifacts
            (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ModelId TEXT NOT NULL,
                ArtifactName TEXT NOT NULL,
                ArtifactPath TEXT NOT NULL,
                Sha256 TEXT NOT NULL DEFAULT '',
                CreatedAtUtc TEXT NOT NULL,
                FOREIGN KEY (ModelId) REFERENCES LearnedPcbVisualModels(ModelId)
            );

            CREATE TABLE IF NOT EXISTS ImageLearningInspectionResults
            (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ResultId TEXT NOT NULL UNIQUE,
                ProjectId TEXT NOT NULL,
                ModelId TEXT NOT NULL,
                ProjectImageId INTEGER NOT NULL,
                ImageSha256 TEXT NOT NULL DEFAULT '',
                ImagePath TEXT NOT NULL DEFAULT '',
                CreatedAtUtc TEXT NOT NULL,
                Verdict TEXT NOT NULL DEFAULT 'REVIEW',
                AnomalyScore REAL NOT NULL DEFAULT 0,
                DecisionReason TEXT NOT NULL DEFAULT '',
                OperatorId TEXT NOT NULL DEFAULT 'UNKNOWN',
                EvidenceMode TEXT NOT NULL DEFAULT 'CustomerData',
                FOREIGN KEY (ProjectId) REFERENCES ImageLearningProjects(ProjectId),
                FOREIGN KEY (ModelId) REFERENCES LearnedPcbVisualModels(ModelId),
                FOREIGN KEY (ProjectImageId) REFERENCES ImageLearningProjectImages(Id)
            );

            CREATE TABLE IF NOT EXISTS ImageLearningAnomalyRegions
            (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                InspectionResultId INTEGER NOT NULL,
                RegionId TEXT NOT NULL DEFAULT '',
                X REAL NOT NULL DEFAULT 0,
                Y REAL NOT NULL DEFAULT 0,
                Width REAL NOT NULL DEFAULT 0,
                Height REAL NOT NULL DEFAULT 0,
                Score REAL NOT NULL DEFAULT 0,
                AreaPixels INTEGER NOT NULL DEFAULT 0,
                Confidence REAL NOT NULL DEFAULT 0,
                Severity TEXT NOT NULL DEFAULT 'REVIEW',
                RegionType TEXT NOT NULL DEFAULT 'Anomaly',
                Reason TEXT NOT NULL DEFAULT '',
                Notes TEXT NOT NULL DEFAULT '',
                FOREIGN KEY (InspectionResultId) REFERENCES ImageLearningInspectionResults(Id)
            );

            CREATE TABLE IF NOT EXISTS ImageLearningCalibrationResults
            (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                CalibrationId TEXT NOT NULL UNIQUE,
                ProjectId TEXT NOT NULL,
                ModelId TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                OkValidationCount INTEGER NOT NULL DEFAULT 0,
                NgValidationCount INTEGER NOT NULL DEFAULT 0,
                LearnedThreshold REAL NOT NULL DEFAULT 0,
                FalseCallTarget REAL NOT NULL DEFAULT 0,
                FalseCallRate REAL NOT NULL DEFAULT 0,
                PossibleEscapeRate REAL NOT NULL DEFAULT 0,
                Status TEXT NOT NULL DEFAULT 'REVIEW',
                Summary TEXT NOT NULL DEFAULT '',
                HeldOutOkCount INTEGER NOT NULL DEFAULT 0,
                HeldOutFalseCalls INTEGER NOT NULL DEFAULT 0,
                HeldOutFalseCallRate REAL NULL,
                FOREIGN KEY (ProjectId) REFERENCES ImageLearningProjects(ProjectId),
                FOREIGN KEY (ModelId) REFERENCES LearnedPcbVisualModels(ModelId)
            );

            CREATE TABLE IF NOT EXISTS ImageLearningComparisonResults
            (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ComparisonId TEXT NOT NULL UNIQUE,
                ProjectId TEXT NOT NULL,
                ModelId TEXT NOT NULL,
                ProjectImageId INTEGER NOT NULL,
                ImageSha256 TEXT NOT NULL DEFAULT '',
                CreatedAtUtc TEXT NOT NULL,
                DifferenceScore REAL NOT NULL DEFAULT 0,
                AnomalyScore REAL NOT NULL DEFAULT 0,
                Verdict TEXT NOT NULL DEFAULT 'REVIEW',
                Summary TEXT NOT NULL DEFAULT '',
                FOREIGN KEY (ProjectId) REFERENCES ImageLearningProjects(ProjectId),
                FOREIGN KEY (ModelId) REFERENCES LearnedPcbVisualModels(ModelId),
                FOREIGN KEY (ProjectImageId) REFERENCES ImageLearningProjectImages(Id)
            );

            CREATE INDEX IF NOT EXISTS IX_ImageLearningProjects_ProjectId ON ImageLearningProjects(ProjectId);
            CREATE INDEX IF NOT EXISTS IX_ImageLearningProjects_Archived ON ImageLearningProjects(IsArchived, UpdatedAtUtc);
            CREATE INDEX IF NOT EXISTS IX_ImageLearningProjectImages_ProjectRole ON ImageLearningProjectImages(ProjectId, Role);
            CREATE INDEX IF NOT EXISTS IX_ImageLearningProjectImages_Sha256 ON ImageLearningProjectImages(Sha256);
            CREATE UNIQUE INDEX IF NOT EXISTS UX_ImageLearningProjectImages_ProjectHash ON ImageLearningProjectImages(ProjectId, Sha256);
            CREATE INDEX IF NOT EXISTS IX_LearnedPcbVisualModels_ProjectId ON LearnedPcbVisualModels(ProjectId, CreatedAtUtc);
            CREATE INDEX IF NOT EXISTS IX_LearnedPcbVisualModelArtifacts_ModelId ON LearnedPcbVisualModelArtifacts(ModelId);
            CREATE INDEX IF NOT EXISTS IX_ImageLearningInspectionResults_ProjectModel ON ImageLearningInspectionResults(ProjectId, ModelId, CreatedAtUtc);
            CREATE INDEX IF NOT EXISTS IX_ImageLearningAnomalyRegions_ResultId ON ImageLearningAnomalyRegions(InspectionResultId);
            CREATE INDEX IF NOT EXISTS IX_ImageLearningCalibrationResults_ProjectModel ON ImageLearningCalibrationResults(ProjectId, ModelId, CreatedAtUtc);
            CREATE INDEX IF NOT EXISTS IX_ImageLearningComparisonResults_ProjectModel ON ImageLearningComparisonResults(ProjectId, ModelId, CreatedAtUtc);
            """;
        command.ExecuteNonQuery();

        AddColumnIfMissing(connection, transaction, "ImageLearningAnomalyRegions", "AreaPixels", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(connection, transaction, "ImageLearningAnomalyRegions", "Confidence", "REAL NOT NULL DEFAULT 0");
        AddColumnIfMissing(connection, transaction, "ImageLearningAnomalyRegions", "Reason", "TEXT NOT NULL DEFAULT ''");
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

    /// <summary>
    /// Archive-then-purge log retention. Each qualifying row is first copied into LogArchive with a
    /// full JSON payload (so it stays recoverable), then removed from its live table. Child rows are
    /// archived and removed before their parents. Runs as a single transaction on a maintenance
    /// connection with foreign-key enforcement disabled so the bulk purge cannot fail on an
    /// unattended startup; the shared cutoff means related rows are removed together.
    /// </summary>
    public static LogRetentionResult RunLogRetention(LogRetentionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!policy.Enabled || policy.RetentionDays <= 0)
            return new LogRetentionResult(0, 0);

        var cutoff = DateTime.UtcNow.AddDays(-policy.RetentionDays).ToString("O", CultureInfo.InvariantCulture);
        var archivedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        using var connection = OpenConnection();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = OFF;";
            pragma.ExecuteNonQuery();
        }

        using var transaction = connection.BeginTransaction();
        var archived = 0;
        var purged = 0;

        // Children first (removed based on their parent's age so a parent and its detail stay together).
        var defectChild = "InspectionResultId IN (SELECT Id FROM InspectionResults WHERE datetime(CreatedAtUtc) < datetime($cutoff))";
        var exportChild = "ExportHistoryId IN (SELECT Id FROM ExportHistory WHERE datetime(CreatedAtUtc) < datetime($cutoff))";
        archived += ArchiveRowsToLogArchive(connection, transaction, "Defects", defectChild, cutoff, null, archivedAt, "Log retention archive: detail of a purged inspection result.");
        purged += DeleteRows(connection, transaction, "Defects", defectChild, cutoff);
        archived += ArchiveRowsToLogArchive(connection, transaction, "ExportVerification", exportChild, cutoff, null, archivedAt, "Log retention archive: verification of a purged export.");
        purged += DeleteRows(connection, transaction, "ExportVerification", exportChild, cutoff);

        // Parents.
        foreach (var (table, dateColumn) in new[]
        {
            ("InspectionResults", "CreatedAtUtc"),
            ("ExportHistory", "CreatedAtUtc"),
            ("ReviewEvents", "EventTimeUtc"),
            ("AuditEvents", "TimestampUtc"),
        })
        {
            var where = $"datetime({dateColumn}) < datetime($cutoff)";
            archived += ArchiveRowsToLogArchive(connection, transaction, table, where, cutoff, dateColumn, archivedAt, $"Log retention archive from {table}.");
            purged += DeleteRows(connection, transaction, table, where, cutoff);
        }

        transaction.Commit();
        return new LogRetentionResult(archived, purged);
    }

    /// <summary>
    /// Counts live log rows that will be archived-and-purged within the next <paramref name="leadDays"/>
    /// under the given retention window, used to warn operators before data leaves the live tables.
    /// </summary>
    public static int CountRowsNearingPurge(int retentionDays, int leadDays)
    {
        if (retentionDays <= 0)
            return 0;

        var purgeCutoff = DateTime.UtcNow.AddDays(-retentionDays).ToString("O", CultureInfo.InvariantCulture);
        var warnCutoff = DateTime.UtcNow.AddDays(-Math.Max(0, retentionDays - Math.Max(0, leadDays))).ToString("O", CultureInfo.InvariantCulture);

        using var connection = OpenConnection();
        var total = 0;
        foreach (var (table, dateColumn) in new[]
        {
            ("InspectionResults", "CreatedAtUtc"),
            ("ExportHistory", "CreatedAtUtc"),
            ("ReviewEvents", "EventTimeUtc"),
            ("AuditEvents", "TimestampUtc"),
        })
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                $"SELECT COUNT(*) FROM {table} WHERE datetime({dateColumn}) < datetime($warn) AND datetime({dateColumn}) >= datetime($purge);";
            command.Parameters.AddWithValue("$warn", warnCutoff);
            command.Parameters.AddWithValue("$purge", purgeCutoff);
            total += Convert.ToInt32(command.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
        }

        return total;
    }

    private static int ArchiveRowsToLogArchive(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourceTable,
        string whereClause,
        string cutoffUtc,
        string? timestampColumn,
        string archivedAtUtc,
        string notes)
    {
        var rows = new List<(long Id, string Timestamp, string Payload)>();
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = $"SELECT * FROM {sourceTable} WHERE {whereClause};";
            select.Parameters.AddWithValue("$cutoff", cutoffUtc);
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                long id = 0;
                var timestamp = archivedAtUtc;
                var payload = new System.Text.StringBuilder("{");
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    var name = reader.GetName(i);
                    if (i > 0)
                        payload.Append(',');
                    payload.Append(JsonSerializer.Serialize(name)).Append(':');
                    if (reader.IsDBNull(i))
                    {
                        payload.Append("null");
                    }
                    else
                    {
                        var value = Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? string.Empty;
                        payload.Append(JsonSerializer.Serialize(value));
                        if (string.Equals(name, "Id", StringComparison.OrdinalIgnoreCase))
                            id = Convert.ToInt64(reader.GetValue(i), CultureInfo.InvariantCulture);
                        if (timestampColumn is not null && string.Equals(name, timestampColumn, StringComparison.OrdinalIgnoreCase))
                            timestamp = value;
                    }
                }

                payload.Append('}');
                rows.Add((id, timestamp, payload.ToString()));
            }
        }

        foreach (var row in rows)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT OR IGNORE INTO LogArchive (SourceTable, SourceId, SourceTimestampUtc, ArchivedAtUtc, Notes, PayloadJson)
                VALUES ($table, $id, $timestamp, $archivedAt, $notes, $payload);
                """;
            insert.Parameters.AddWithValue("$table", sourceTable);
            insert.Parameters.AddWithValue("$id", row.Id);
            insert.Parameters.AddWithValue("$timestamp", row.Timestamp);
            insert.Parameters.AddWithValue("$archivedAt", archivedAtUtc);
            insert.Parameters.AddWithValue("$notes", notes);
            insert.Parameters.AddWithValue("$payload", row.Payload);
            insert.ExecuteNonQuery();
        }

        return rows.Count;
    }

    private static int DeleteRows(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string whereClause,
        string cutoffUtc)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"DELETE FROM {table} WHERE {whereClause};";
        command.Parameters.AddWithValue("$cutoff", cutoffUtc);
        return command.ExecuteNonQuery();
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

        var start = operatorWithRole.LastIndexOf('[');
        var end = operatorWithRole.LastIndexOf(']');
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
            DeploymentWaiverRiskClassification TEXT NOT NULL DEFAULT '',
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

        CREATE TABLE IF NOT EXISTS CustomerPilotSessions
        (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            SessionId TEXT NOT NULL UNIQUE,
            DeploymentProfile TEXT NOT NULL,
            Status TEXT NOT NULL DEFAULT 'InProgress',
            DatasetFolder TEXT NOT NULL DEFAULT '',
            ManifestPath TEXT NOT NULL DEFAULT '',
            OperatorId TEXT NOT NULL DEFAULT 'UNKNOWN',
            CreatedAtUtc TEXT NOT NULL,
            UpdatedAtUtc TEXT NOT NULL,
            CompletedAtUtc TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS CustomerPilotSteps
        (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            SessionId INTEGER NOT NULL,
            StepKey TEXT NOT NULL,
            StepOrder INTEGER NOT NULL,
            Status TEXT NOT NULL,
            EvidencePath TEXT NOT NULL DEFAULT '',
            MessagesJson TEXT NOT NULL DEFAULT '[]',
            Waived INTEGER NOT NULL DEFAULT 0,
            WaiverReason TEXT NOT NULL DEFAULT '',
            WaivedBy TEXT NOT NULL DEFAULT '',
            WaivedAtUtc TEXT NULL,
            UpdatedAtUtc TEXT NOT NULL,
            UNIQUE(SessionId, StepKey),
            FOREIGN KEY (SessionId) REFERENCES CustomerPilotSessions(Id)
        );

        CREATE TABLE IF NOT EXISTS LogArchive
        (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            SourceTable TEXT NOT NULL,
            SourceId INTEGER NOT NULL,
            SourceTimestampUtc TEXT NOT NULL,
            ArchivedAtUtc TEXT NOT NULL,
            Notes TEXT NOT NULL,
            PayloadJson TEXT NOT NULL DEFAULT '',
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
        CREATE INDEX IF NOT EXISTS IX_CustomerPilotSessions_Status_Updated ON CustomerPilotSessions(Status, UpdatedAtUtc);
        CREATE INDEX IF NOT EXISTS IX_CustomerPilotSteps_Session_Order ON CustomerPilotSteps(SessionId, StepOrder);
        """;
}
