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
                 WaiverExpiresAtUtc, DeploymentWaivedBy, DeploymentWaivedAtUtc, DeploymentWaiverRiskClassification,
                 DeployedAtUtc, RetiredReason, RetiredAtUtc)
            VALUES
                ($modelId, $displayName, $version, $createdAtUtc, $registeredAtUtc, $sourceFileName,
                 $storedModelPath, $storedLabelMapPath, $metadataPath, $sha256, $inputTensorName, $outputTensorName,
                 $inputWidth, $inputHeight, $confidenceThreshold, $labelsJson, $validationStatus, $lastValidatedAtUtc,
                 $validationMessage, $notes, $isActive, $auditEventId, $lifecycleState, $latestAcceptanceStatus,
                 $latestAcceptanceRunId, $latestReleasePackageId, $latestReleasePackagePath, $deploymentWaiverReason,
                 $waiverExpiresAtUtc, $deploymentWaivedBy, $deploymentWaivedAtUtc, $deploymentWaiverRiskClassification,
                 $deployedAtUtc, $retiredReason, $retiredAtUtc)
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
                DeploymentWaiverRiskClassification = excluded.DeploymentWaiverRiskClassification,
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
        string deploymentWaiverRiskClassification = "",
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
                DeploymentWaiverRiskClassification = CASE WHEN $replaceDeploymentWaiver = 1 THEN $deploymentWaiverRiskClassification WHEN $deploymentWaiverRiskClassification = '' THEN DeploymentWaiverRiskClassification ELSE $deploymentWaiverRiskClassification END,
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
        command.Parameters.AddWithValue("$deploymentWaiverRiskClassification", deploymentWaiverRiskClassification ?? string.Empty);
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

}
