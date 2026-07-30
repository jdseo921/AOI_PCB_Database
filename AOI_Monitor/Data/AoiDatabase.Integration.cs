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

    public static DefectTaxonomySnapshot? GetActiveDefectTaxonomy()
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        EnsureDefectTaxonomyTables(connection);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT TaxonomyId, Name, CustomerName, IsActive, CreatedAtUtc, UpdatedAtUtc
            FROM DefectTaxonomies
            WHERE IsActive = 1
            ORDER BY datetime(UpdatedAtUtc) DESC
            LIMIT 1;
            """;
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        var taxonomy = ReadDefectTaxonomy(reader);
        return new DefectTaxonomySnapshot
        {
            Taxonomy = taxonomy,
            Entries = GetDefectTaxonomyEntries(connection, taxonomy.TaxonomyId).ToList(),
            Aliases = GetDefectClassAliases(connection, taxonomy.TaxonomyId).ToList(),
            MesMappings = GetMesDefectCodeMappings(connection, taxonomy.TaxonomyId).ToList(),
        };
    }

    public static IReadOnlyList<DefectTaxonomyEntry> GetActiveDefectTaxonomyEntries()
        => GetActiveDefectTaxonomy()?.Entries ?? (IReadOnlyList<DefectTaxonomyEntry>)Array.Empty<DefectTaxonomyEntry>();

    public static void SaveDefectTaxonomySnapshot(DefectTaxonomySnapshot snapshot, string operatorWithRole = "SYSTEM")
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(snapshot);
        if (string.IsNullOrWhiteSpace(snapshot.Taxonomy.TaxonomyId))
            throw new ArgumentException("Taxonomy ID is required.", nameof(snapshot));

        var now = DateTime.UtcNow;
        snapshot.Taxonomy.CreatedAtUtc = snapshot.Taxonomy.CreatedAtUtc == default ? now : snapshot.Taxonomy.CreatedAtUtc;
        snapshot.Taxonomy.UpdatedAtUtc = now;

        using var connection = OpenConnection();
        EnsureDefectTaxonomyTables(connection);
        using var transaction = connection.BeginTransaction();
        if (snapshot.Taxonomy.IsActive)
        {
            using var deactivate = connection.CreateCommand();
            deactivate.Transaction = transaction;
            deactivate.CommandText = "UPDATE DefectTaxonomies SET IsActive = 0;";
            deactivate.ExecuteNonQuery();
        }

        using var taxonomy = connection.CreateCommand();
        taxonomy.Transaction = transaction;
        taxonomy.CommandText =
            """
            INSERT INTO DefectTaxonomies (TaxonomyId, Name, CustomerName, IsActive, CreatedAtUtc, UpdatedAtUtc)
            VALUES ($id, $name, $customerName, $isActive, $createdAtUtc, $updatedAtUtc)
            ON CONFLICT(TaxonomyId) DO UPDATE SET
                Name = excluded.Name,
                CustomerName = excluded.CustomerName,
                IsActive = excluded.IsActive,
                UpdatedAtUtc = excluded.UpdatedAtUtc;
            """;
        taxonomy.Parameters.AddWithValue("$id", snapshot.Taxonomy.TaxonomyId);
        taxonomy.Parameters.AddWithValue("$name", snapshot.Taxonomy.Name);
        taxonomy.Parameters.AddWithValue("$customerName", snapshot.Taxonomy.CustomerName);
        taxonomy.Parameters.AddWithValue("$isActive", snapshot.Taxonomy.IsActive ? 1 : 0);
        taxonomy.Parameters.AddWithValue("$createdAtUtc", snapshot.Taxonomy.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        taxonomy.Parameters.AddWithValue("$updatedAtUtc", snapshot.Taxonomy.UpdatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        taxonomy.ExecuteNonQuery();

        DeleteTaxonomyChildren(connection, transaction, snapshot.Taxonomy.TaxonomyId);
        foreach (var entry in snapshot.Entries)
            InsertDefectTaxonomyEntry(connection, transaction, snapshot.Taxonomy.TaxonomyId, entry);
        foreach (var alias in snapshot.Aliases)
            InsertDefectClassAlias(connection, transaction, snapshot.Taxonomy.TaxonomyId, alias);
        foreach (var mapping in snapshot.MesMappings)
            InsertMesDefectCodeMapping(connection, transaction, snapshot.Taxonomy.TaxonomyId, mapping);

        transaction.Commit();
        RecordAuditEvent(
            "DEFECT_TAXONOMY",
            $"Defect taxonomy saved: {snapshot.Taxonomy.Name}; entries={snapshot.Entries.Count}; aliases={snapshot.Aliases.Count}; MES mappings={snapshot.MesMappings.Count}.",
            operatorWithRole: operatorWithRole,
            relatedEntityType: "DefectTaxonomy",
            relatedEntityId: snapshot.Taxonomy.TaxonomyId);
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

    /// <summary>Fetches queue rows by exact id (chunked parameterized IN), regardless of queue depth.</summary>
    public static IReadOnlyList<CentralSyncQueueRecord> GetCentralSyncItemsByIds(IReadOnlyCollection<long> ids)
    {
        EnsureInitialized();

        var records = new List<CentralSyncQueueRecord>();
        if (ids.Count == 0)
            return records;

        using var connection = OpenConnection();
        foreach (var chunk in ids.Chunk(100))
        {
            using var command = connection.CreateCommand();
            var parameterNames = new List<string>(chunk.Length);
            for (var i = 0; i < chunk.Length; i++)
            {
                var name = $"$id{i}";
                parameterNames.Add(name);
                command.Parameters.AddWithValue(name, chunk[i]);
            }

            command.CommandText =
                $"""
                SELECT Id, CreatedAtUtc, LastAttemptAtUtc, NextAttemptAtUtc, ItemType, ItemId, PayloadJson,
                       PayloadPath, EndpointOrFolder, StationId, RetryCount, MaxRetryCount, Status, LastError
                FROM CentralSyncQueue
                WHERE Id IN ({string.Join(", ", parameterNames)});
                """;

            using var reader = command.ExecuteReader();
            while (reader.Read())
                records.Add(ReadCentralSyncQueueRecord(reader));
        }

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

}
