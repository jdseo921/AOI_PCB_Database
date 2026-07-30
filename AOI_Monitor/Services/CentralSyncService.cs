using System.IO;
using System.Text;
using System.Text.Json;
using AOI_Monitor.Data;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public static class CentralSyncSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static CentralSyncSettings? _cached;

    public static event Action? SettingsChanged;

    public static string SettingsPath => Path.Combine(AoiDatabase.StorageRoot, "central_sync_settings.json");

    public static CentralSyncSettings Load()
    {
        if (_cached is not null)
            return Clone(_cached);

        Directory.CreateDirectory(AoiDatabase.StorageRoot);
        if (!File.Exists(SettingsPath))
        {
            _cached = new CentralSyncSettings();
            Save(_cached, notify: false);
            return Clone(_cached);
        }

        try
        {
            _cached = JsonSerializer.Deserialize<CentralSyncSettings>(File.ReadAllText(SettingsPath)) ?? new CentralSyncSettings();
            _cached.SharedSecret = SecretProtectionService.Unprotect(_cached.SharedSecret);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"Central sync settings load fallback used: {ex.Message}");
            _cached = new CentralSyncSettings();
        }

        Normalize(_cached);
        return Clone(_cached);
    }

    public static void Save(CentralSyncSettings settings)
        => Save(settings, notify: true);

    public static void ResetForTests()
    {
        _cached = null;
    }

    public static IReadOnlyList<string> Validate(CentralSyncSettings settings)
    {
        Normalize(settings);
        var errors = new List<string>();
        if (settings.Mode is CentralSyncMode.FileDrop && string.IsNullOrWhiteSpace(settings.EndpointOrFolder))
            errors.Add("Central sync FileDrop requires a destination folder.");
        if (settings.Mode is CentralSyncMode.RestApi &&
            (!Uri.TryCreate(settings.EndpointOrFolder, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
        {
            errors.Add("Central sync REST API requires an absolute http/https endpoint.");
        }

        return errors;
    }

    public static string RedactedSummary(CentralSyncSettings settings)
    {
        Normalize(settings);
        var endpoint = settings.RedactEndpointInExports && !string.IsNullOrWhiteSpace(settings.EndpointOrFolder)
            ? "***"
            : RedactSecrets(settings.EndpointOrFolder, settings);
        return $"Mode={settings.Mode}; station={settings.StationId}; endpointOrFolder={endpoint}; interval={settings.SyncIntervalSeconds}s; includeImages={settings.IncludeImages}; redactOperator={settings.RedactOperatorId}; redactImagePaths={settings.RedactImagePaths}.";
    }

    public static string RedactSecrets(string text, CentralSyncSettings? settings = null)
    {
        var redacted = text ?? string.Empty;
        var source = settings ?? _cached ?? new CentralSyncSettings();
        return SecretProtectionService.RedactKnownSecrets(redacted, source.SharedSecret);
    }

    private static void Save(CentralSyncSettings settings, bool notify)
    {
        Normalize(settings);
        Directory.CreateDirectory(AoiDatabase.StorageRoot);
        var stored = Clone(settings);
        stored.SharedSecret = SecretProtectionService.Protect(stored.SharedSecret);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(stored, JsonOptions), Encoding.UTF8);
        _cached = Clone(settings);
        if (notify)
            SettingsChanged?.Invoke();
    }

    private static void Normalize(CentralSyncSettings settings)
    {
        if (!Enum.IsDefined(settings.Mode))
            settings.Mode = CentralSyncMode.Disabled;

        settings.EndpointOrFolder = settings.EndpointOrFolder?.Trim() ?? string.Empty;
        settings.EndpointUrl = settings.EndpointUrl?.Trim() ?? string.Empty;
        settings.FileDropFolder = settings.FileDropFolder?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(settings.EndpointOrFolder))
        {
            settings.EndpointOrFolder = settings.Mode == CentralSyncMode.FileDrop
                ? settings.FileDropFolder
                : settings.EndpointUrl;
        }
        if (settings.Mode == CentralSyncMode.FileDrop && string.IsNullOrWhiteSpace(settings.FileDropFolder))
            settings.FileDropFolder = settings.EndpointOrFolder;
        if (settings.Mode is CentralSyncMode.RestApi or CentralSyncMode.ProductionDatabaseBoundary or CentralSyncMode.PostgreSqlBoundary &&
            string.IsNullOrWhiteSpace(settings.EndpointUrl))
        {
            settings.EndpointUrl = settings.EndpointOrFolder;
        }
        settings.StationId = string.IsNullOrWhiteSpace(settings.StationId) ? Environment.MachineName : settings.StationId.Trim();
        settings.SyncIntervalSeconds = Math.Clamp(settings.SyncIntervalSeconds <= 0 ? 300 : settings.SyncIntervalSeconds, 10, 86400);
        settings.MaxRetryCount = Math.Clamp(settings.MaxRetryCount <= 0 ? 5 : settings.MaxRetryCount, 1, 100);
        settings.RetryBackoffMs = Math.Clamp(settings.RetryBackoffMs <= 0 ? 5000 : settings.RetryBackoffMs, 0, 3600000);
        settings.SharedSecret ??= string.Empty;
    }

    private static CentralSyncSettings Clone(CentralSyncSettings source)
        => new()
        {
            Mode = source.Mode,
            EndpointOrFolder = source.EndpointOrFolder,
            EndpointUrl = source.EndpointUrl,
            FileDropFolder = source.FileDropFolder,
            StationId = source.StationId,
            SyncIntervalSeconds = source.SyncIntervalSeconds,
            IncludeImages = source.IncludeImages,
            RedactOperatorId = source.RedactOperatorId,
            RedactImagePaths = source.RedactImagePaths,
            RedactEndpointInExports = source.RedactEndpointInExports,
            MaxRetryCount = source.MaxRetryCount,
            RetryBackoffMs = source.RetryBackoffMs,
            SharedSecret = source.SharedSecret,
        };
}

public static class CentralSyncService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static int QueueLocalChangesForSync(int limitPerType = 100)
    {
        var settings = CentralSyncSettingsService.Load();
        var queued = 0;

        queued += QueueInspections(settings, limitPerType);
        queued += QueueReviews(settings, limitPerType);
        queued += QueueAuditEvents(settings, limitPerType);
        queued += QueueValidationPackages(settings, limitPerType);
        queued += QueueExportVerifications(settings, limitPerType);
        queued += QueueLatestEvidence(settings);

        AoiDatabase.RecordAuditEvent("CENTRAL_SYNC_QUEUE", $"Central sync queued {queued} local item(s).");
        return queued;
    }

    public static IReadOnlyList<CentralSyncQueueRecord> List(int limit = 500)
        => AoiDatabase.GetCentralSyncQueue(limit);

    public static async Task<CentralSyncRetrySummary> RetryEligibleAsync(
        int limit = 100,
        CancellationToken cancellationToken = default)
        => await RetryItemsAsync(AoiDatabase.GetPendingCentralSyncItems(limit).Select(item => item.Id), cancellationToken).ConfigureAwait(false);

    public static async Task<CentralSyncRetrySummary> RetryItemsAsync(
        IEnumerable<long> itemIds,
        CancellationToken cancellationToken = default)
    {
        var ids = itemIds.Distinct().ToHashSet();
        if (ids.Count == 0)
            return new CentralSyncRetrySummary(0, 0, 0, 0);

        var settings = CentralSyncSettingsService.Load();
        // Fetch by the exact ids: intersecting with a newest-N queue snapshot silently
        // dropped the oldest pending items once the queue grew past the window, so a
        // backlog could never drain.
        var items = AoiDatabase.GetCentralSyncItemsByIds(ids)
            .Where(item => item.Status == CentralSyncStatus.Pending.ToString())
            .OrderBy(item => item.CreatedAtUtc)
            .ToArray();

        var sent = 0;
        var failed = 0;
        var skipped = 0;

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = await SyncOneAsync(item, settings, cancellationToken).ConfigureAwait(false);
            AoiDatabase.RecordCentralSyncAttempt(item.Id, settings.Mode.ToString(), RedactedEndpoint(settings), outcome.Status.ToString(), outcome.Message);

            if (outcome.Accepted)
            {
                sent++;
                AoiDatabase.MarkCentralSyncItemSent(item.Id, outcome.PayloadPath, outcome.Message);
            }
            else if (outcome.Status == IntegrationConnectionStatus.NotConnected && settings.Mode == CentralSyncMode.Disabled)
            {
                skipped++;
                AoiDatabase.MarkCentralSyncItemSkipped(item.Id, outcome.Message);
            }
            else
            {
                failed++;
                AoiDatabase.RecordCentralSyncRetryFailure(item.Id, outcome.Message, settings.RetryBackoffMs);
            }
        }

        return new CentralSyncRetrySummary(items.Length, sent, failed, skipped);
    }

    public static CentralSyncReadinessSummary EvaluateReadiness()
    {
        var settings = CentralSyncSettingsService.Load();
        var queue = AoiDatabase.GetCentralSyncQueue(1000);
        var validation = CentralSyncSettingsService.Validate(settings);
        var summary = new CentralSyncReadinessSummary
        {
            Mode = settings.Mode.ToString(),
            PendingCount = queue.Count(item => item.Status == CentralSyncStatus.Pending.ToString()),
            FailedCount = queue.Count(item => item.Status == CentralSyncStatus.Failed.ToString()),
            SentCount = queue.Count(item => item.Status == CentralSyncStatus.Sent.ToString()),
            SkippedCount = queue.Count(item => item.Status == CentralSyncStatus.Skipped.ToString()),
        };

        if (settings.Mode == CentralSyncMode.Disabled)
        {
            summary.Status = "Central Sync Disabled";
            summary.Messages.Add("Central sync is optional for Stage 1. Management aggregation across stations is not enabled.");
            return summary;
        }

        if (validation.Count > 0)
        {
            summary.Status = "Central Sync Error";
            summary.Messages.AddRange(validation);
            return summary;
        }

        if (summary.PendingCount > 0)
            summary.Messages.Add($"{summary.PendingCount} central sync item(s) are pending retry.");
        if (summary.FailedCount > 0)
            summary.Messages.Add($"{summary.FailedCount} central sync item(s) are failed.");

        summary.Status = summary.FailedCount > 0
            ? "Central Sync Error"
            : summary.PendingCount > 0
                ? "Central Sync Pending"
                : "Central Sync Ready";
        if (summary.Messages.Count == 0)
            summary.Messages.Add(summary.Status);
        return summary;
    }

    public static CentralSyncReportExport ExportQueueReport(string? outputRoot = null)
    {
        var root = string.IsNullOrWhiteSpace(outputRoot)
            ? Path.Combine(AoiDatabase.StorageRoot, "exports", "central_sync")
            : outputRoot;
        Directory.CreateDirectory(root);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var rows = AoiDatabase.GetCentralSyncQueue(1000);
        var settings = CentralSyncSettingsService.Load();
        var jsonPath = Path.Combine(root, $"central_sync_queue_{stamp}.json");
        var htmlPath = Path.Combine(root, $"central_sync_queue_{stamp}.html");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(rows.Select(row => ToReportRow(row, settings)), JsonOptions), Encoding.UTF8);
        File.WriteAllText(htmlPath, BuildHtml(rows, EvaluateReadiness(), settings), Encoding.UTF8);
        ExportVerificationService.RecordVerifiedExport("CentralSyncQueueReportJson", jsonPath);
        ExportVerificationService.RecordVerifiedExport("CentralSyncQueueReportHtml", htmlPath);
        return new CentralSyncReportExport(jsonPath, htmlPath);
    }

    private static int QueueInspections(CentralSyncSettings settings, int limit)
    {
        var count = 0;
        foreach (var row in AoiDatabase.GetInspectionHistory(new LogFilter()).Take(limit))
        {
            var itemId = row.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (AoiDatabase.CentralSyncQueueContains("InspectionResult", itemId))
                continue;

            QueuePayload(settings, BuildInspectionPayload(row, settings));
            count++;
        }

        return count;
    }

    private static int QueueReviews(CentralSyncSettings settings, int limit)
    {
        var count = 0;
        foreach (var row in AoiDatabase.GetReviewEvents(new LogFilter()).Take(limit))
        {
            var itemId = row.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (AoiDatabase.CentralSyncQueueContains("ReviewEvent", itemId))
                continue;

            QueuePayload(settings, new CentralSyncPayload
            {
                ItemType = "ReviewEvent",
                ItemId = itemId,
                StationId = settings.StationId,
                Data =
                {
                    ["eventTimeUtc"] = row.EventTimeUtc,
                    ["category"] = row.Category,
                    ["operatorId"] = settings.RedactOperatorId ? "REDACTED" : row.OperatorId,
                    ["disposition"] = row.Disposition,
                    ["message"] = row.Message,
                },
                MappingNotes = { "Maps to central review/disposition event table." },
            });
            count++;
        }

        return count;
    }

    private static int QueueValidationPackages(CentralSyncSettings settings, int limit)
    {
        var count = 0;
        foreach (var row in AoiDatabase.GetValidationPackages(limit))
        {
            var itemId = row.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (AoiDatabase.CentralSyncQueueContains("ValidationPackage", itemId))
                continue;

            QueuePayload(settings, new CentralSyncPayload
            {
                ItemType = "ValidationPackage",
                ItemId = itemId,
                StationId = settings.StationId,
                Data =
                {
                    ["createdAtUtc"] = row.CreatedAtUtc,
                    ["packageId"] = row.PackageId,
                    ["acceptanceStatus"] = row.AcceptanceStatus,
                    ["runId"] = row.RunId,
                    ["manifestPath"] = settings.RedactImagePaths ? "REDACTED" : row.ManifestPath,
                    ["packagePath"] = settings.RedactImagePaths ? "REDACTED" : row.PackagePath,
                    ["summary"] = row.Summary,
                },
                MappingNotes = { "Maps to central validation package summary table; customer images are not included." },
            });
            count++;
        }

        return count;
    }

    private static int QueueExportVerifications(CentralSyncSettings settings, int limit)
    {
        var count = 0;
        foreach (var row in AoiDatabase.GetExportVerifications(limit))
        {
            var itemId = row.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (AoiDatabase.CentralSyncQueueContains("ExportVerification", itemId))
                continue;

            QueuePayload(settings, new CentralSyncPayload
            {
                ItemType = "ExportVerification",
                ItemId = itemId,
                StationId = settings.StationId,
                Data =
                {
                    ["checkedAtUtc"] = row.CheckedAtUtc,
                    ["exportType"] = row.ExportType,
                    ["status"] = row.Status,
                    ["sha256"] = row.Sha256,
                    ["sizeBytes"] = row.SizeBytes,
                    ["exportPath"] = settings.RedactImagePaths ? "REDACTED" : row.ExportPath,
                },
                MappingNotes = { "Maps to central export verification evidence table." },
            });
            count++;
        }

        return count;
    }

    private static int QueueAuditEvents(CentralSyncSettings settings, int limit)
    {
        var count = 0;
        foreach (var row in AoiDatabase.GetAuditEvents(new LogFilter(), limit))
        {
            // Central-sync bookkeeping events must not feed back into the queue: every
            // enqueue/status change records its own CENTRAL_SYNC_* audit event, so
            // syncing them would grow the queue on every pass even on an idle line.
            if (row.ActionCategory?.StartsWith("CENTRAL_SYNC", StringComparison.OrdinalIgnoreCase) == true)
                continue;

            var itemId = row.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (AoiDatabase.CentralSyncQueueContains("AuditEvent", itemId))
                continue;

            QueuePayload(settings, new CentralSyncPayload
            {
                ItemType = "AuditEvent",
                ItemId = itemId,
                StationId = settings.StationId,
                Data =
                {
                    ["timestampUtc"] = row.TimestampUtc,
                    ["userId"] = settings.RedactOperatorId ? "REDACTED" : row.UserId,
                    ["userRole"] = row.UserRole,
                    ["stationId"] = row.StationId,
                    ["actionCategory"] = row.ActionCategory,
                    ["actionDetail"] = row.ActionDetail,
                    ["relatedEntityType"] = row.RelatedEntityType,
                    ["relatedEntityId"] = row.RelatedEntityId,
                    ["relatedPath"] = settings.RedactImagePaths ? "REDACTED" : row.RelatedPath,
                },
                MappingNotes = { "Maps to central audit/event table for management reporting." },
            });
            count++;
        }

        return count;
    }

    private static int QueueLatestEvidence(CentralSyncSettings settings)
    {
        var count = 0;
        count += QueueLatest(settings, "BuildTestEvidence", AoiDatabase.GetLatestBuildTestEvidence()?.Id, () =>
        {
            var row = AoiDatabase.GetLatestBuildTestEvidence()!;
            return new CentralSyncPayload
            {
                ItemType = "BuildTestEvidence",
                ItemId = row.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                StationId = settings.StationId,
                Data =
                {
                    ["generatedAtUtc"] = row.GeneratedAtUtc,
                    ["configuration"] = row.Configuration,
                    ["hygieneStatus"] = row.HygieneStatus,
                    ["restoreStatus"] = row.RestoreStatus,
                    ["buildStatus"] = row.BuildStatus,
                    ["testStatus"] = row.TestStatus,
                    ["publishValidationStatus"] = row.PublishValidationStatus,
                    ["commitSha"] = row.CommitSha,
                    ["operatorId"] = settings.RedactOperatorId ? "REDACTED" : row.OperatorId,
                    ["evidencePath"] = settings.RedactImagePaths ? "REDACTED" : row.EvidencePath,
                },
                MappingNotes = { "Maps to central build/test evidence table." },
            };
        });
        count += QueueLatest(settings, "CameraAcceptanceRun", AoiDatabase.GetLatestCameraAcceptanceRun(realHardwareOnly: false)?.Id, () =>
        {
            var row = AoiDatabase.GetLatestCameraAcceptanceRun(realHardwareOnly: false)!;
            return AcceptancePayload(settings, "CameraAcceptanceRun", row.Id, row.CreatedAtUtc, row.Status, new()
            {
                ["factoryReadinessStatus"] = row.FactoryReadinessStatus,
                ["adapterName"] = row.AdapterName,
                ["sourceKey"] = row.SourceKey,
                ["isRealHardware"] = row.IsRealHardware,
                ["requestedFrames"] = row.TotalRequestedFrames,
                ["receivedFrames"] = row.TotalReceivedFrames,
                ["droppedFrames"] = row.DroppedFrameCount,
            });
        });
        count += QueueLatest(settings, "LightingAcceptanceRun", AoiDatabase.GetLatestLightingAcceptanceRun()?.Id, () =>
        {
            var row = AoiDatabase.GetLatestLightingAcceptanceRun()!;
            return AcceptancePayload(settings, "LightingAcceptanceRun", row.Id, row.CreatedAtUtc, row.Status, new()
            {
                ["controllerName"] = row.ControllerName,
                ["mode"] = row.Mode,
                ["isSimulated"] = row.IsSimulated,
                ["stepCount"] = row.StepCount,
                ["passedStepCount"] = row.PassedStepCount,
            });
        });
        count += QueueLatest(settings, "RobotAcceptanceRun", AoiDatabase.GetLatestRobotAcceptanceRun()?.Id, () =>
        {
            var row = AoiDatabase.GetLatestRobotAcceptanceRun()!;
            return AcceptancePayload(settings, "RobotAcceptanceRun", row.Id, row.CreatedAtUtc, row.Status, new()
            {
                ["controllerName"] = row.ControllerName,
                ["sourceKind"] = row.SourceKind,
                ["safetyControllerName"] = row.SafetyControllerName,
                ["safetySourceKind"] = row.SafetySourceKind,
                ["emergencyStopBlocked"] = row.EmergencyStopBlocked,
                ["safetyFaultBlocked"] = row.SafetyFaultBlocked,
                ["fullCycleMs"] = row.FullCycleMs,
            });
        });
        count += QueueLatest(settings, "TraceabilityAcceptanceRun", AoiDatabase.GetLatestTraceabilityTestReport()?.Id, () =>
        {
            var row = AoiDatabase.GetLatestTraceabilityTestReport()!;
            return AcceptancePayload(settings, "TraceabilityAcceptanceRun", row.Id, row.CreatedAtUtc, row.Status, new()
            {
                ["mode"] = row.Mode,
                ["endpointUrl"] = settings.RedactEndpointInExports ? "REDACTED" : row.EndpointUrl,
                ["resultStatus"] = row.ResultStatus,
                ["imageStatus"] = row.ImageStatus,
                ["productionModeConfirmed"] = row.ProductionModeConfirmed,
                ["reportJsonPath"] = settings.RedactImagePaths ? "REDACTED" : row.ReportJsonPath,
            });
        });
        count += QueueLatest(settings, "Profile3DAcceptanceRun", AoiDatabase.GetLatestProfile3DAcceptanceRun()?.Id, () =>
        {
            var row = AoiDatabase.GetLatestProfile3DAcceptanceRun()!;
            return AcceptancePayload(settings, "Profile3DAcceptanceRun", row.Id, row.CreatedAtUtc, row.Status, new()
            {
                ["factoryReadinessStatus"] = row.FactoryReadinessStatus,
                ["sourceName"] = row.SourceName,
                ["sourceKind"] = row.SourceKind,
                ["isSimulated"] = row.IsSimulated,
                ["width"] = row.Width,
                ["height"] = row.Height,
            });
        });

        return count;
    }

    private static int QueueLatest(CentralSyncSettings settings, string itemType, long? id, Func<CentralSyncPayload> buildPayload)
    {
        if (id is null or <= 0)
            return 0;
        var itemId = id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (AoiDatabase.CentralSyncQueueContains(itemType, itemId))
            return 0;

        QueuePayload(settings, buildPayload());
        return 1;
    }

    private static CentralSyncPayload AcceptancePayload(
        CentralSyncSettings settings,
        string itemType,
        long id,
        DateTime createdAtUtc,
        string status,
        Dictionary<string, object?> fields)
    {
        var payload = new CentralSyncPayload
        {
            ItemType = itemType,
            ItemId = id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            StationId = settings.StationId,
            MappingNotes = { "Maps to central factory acceptance/readiness evidence table; raw customer images are not included." },
        };
        payload.Data["createdAtUtc"] = createdAtUtc;
        payload.Data["status"] = status;
        foreach (var pair in fields)
            payload.Data[pair.Key] = pair.Value;
        return payload;
    }

    private static void QueuePayload(CentralSyncSettings settings, CentralSyncPayload payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        AoiDatabase.EnqueueCentralSyncItem(
            payload.ItemType,
            payload.ItemId,
            json,
            string.Empty,
            RedactedEndpoint(settings),
            settings.StationId,
            settings.MaxRetryCount);
    }

    private static CentralSyncPayload BuildInspectionPayload(InspectionHistoryRecord row, CentralSyncSettings settings)
    {
        var payload = new CentralSyncPayload
        {
            ItemType = "InspectionResult",
            ItemId = row.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            StationId = settings.StationId,
            Data =
            {
                ["createdAtUtc"] = row.CreatedAtUtc,
                ["boardProgram"] = row.BoardProgram,
                ["operatorId"] = settings.RedactOperatorId ? "REDACTED" : row.OperatorId,
                ["inspectionEngine"] = row.InspectionEngine,
                ["modelVersion"] = row.ModelVersion,
                ["verdict"] = row.Verdict,
                ["differenceScore"] = row.DifferenceScore,
                ["confidence"] = row.Confidence,
                ["suggestedDefect"] = row.SuggestedDefect,
                ["decisionReason"] = row.DecisionReason,
                ["totalInspectionMilliseconds"] = row.TotalInspectionMilliseconds,
                ["sampleImagePath"] = settings.RedactImagePaths ? "REDACTED" : row.SampleImagePath,
                ["goldenImagePath"] = settings.RedactImagePaths ? "REDACTED" : row.GoldenImagePath,
            },
            MappingNotes =
            {
                "Maps to central inspection result table.",
                "Local SQLite remains the offline source of truth.",
            },
        };

        if (settings.IncludeImages && !settings.RedactImagePaths)
        {
            AddExistingFile(payload.IncludedFiles, row.SampleImagePath);
            AddExistingFile(payload.IncludedFiles, row.GoldenImagePath);
        }

        return payload;
    }

    private static async Task<CentralSyncOutcome> SyncOneAsync(
        CentralSyncQueueRecord item,
        CentralSyncSettings settings,
        CancellationToken cancellationToken)
    {
        try
        {
            return settings.Mode switch
            {
                CentralSyncMode.Disabled => new(false, IntegrationConnectionStatus.NotConnected, "Central sync disabled; item skipped.", string.Empty),
                CentralSyncMode.FileDrop => await WriteFileDropAsync(item, settings, cancellationToken).ConfigureAwait(false),
                CentralSyncMode.ProductionDatabaseBoundary or CentralSyncMode.PostgreSqlBoundary => await UploadToCentralDatabaseBoundaryAsync(item, cancellationToken).ConfigureAwait(false),
                CentralSyncMode.RestApi => new(false, IntegrationConnectionStatus.NotConnected, "Central sync REST API boundary is configured, but no production REST client is installed in this build.", string.Empty),
                _ => new(false, IntegrationConnectionStatus.Error, "Unknown central sync mode.", string.Empty),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return new(false, IntegrationConnectionStatus.Error, $"Central sync failed safely: {CentralSyncSettingsService.RedactSecrets(ex.Message, settings)}", string.Empty);
        }
    }

    private static async Task<CentralSyncOutcome> WriteFileDropAsync(
        CentralSyncQueueRecord item,
        CentralSyncSettings settings,
        CancellationToken cancellationToken)
    {
        var folder = string.IsNullOrWhiteSpace(settings.EndpointOrFolder)
            ? Path.Combine(AoiDatabase.StorageRoot, "exports", "central_sync", "file_drop")
            : settings.EndpointOrFolder;
        Directory.CreateDirectory(folder);
        var fileName = $"{SafeName(item.ItemType)}_{SafeName(item.ItemId)}_{DateTime.UtcNow:yyyyMMdd_HHmmssfff}.json";
        var path = Path.Combine(folder, fileName);
        await File.WriteAllTextAsync(path, item.PayloadJson, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        return new CentralSyncOutcome(true, IntegrationConnectionStatus.Ready, $"Central sync FileDrop wrote {fileName}.", path);
    }

    private static async Task<CentralSyncOutcome> UploadToCentralDatabaseBoundaryAsync(
        CentralSyncQueueRecord item,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<CentralSyncPayload>(item.PayloadJson, JsonOptions)
            ?? throw new InvalidOperationException("Central sync payload JSON was empty.");
        var result = await IntegrationBoundaryRegistry.CentralProductionDatabaseClient
            .UploadCentralSyncPayloadAsync(payload, cancellationToken)
            .ConfigureAwait(false);
        return new CentralSyncOutcome(result.Accepted, result.Status, result.Message, string.Empty);
    }

    private static string BuildHtml(
        IReadOnlyList<CentralSyncQueueRecord> rows,
        CentralSyncReadinessSummary readiness,
        CentralSyncSettings settings)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html><html><head><meta charset=\"utf-8\"><title>Central Sync Queue Report</title>");
        sb.AppendLine("<style>body{font-family:Segoe UI,Arial,sans-serif;margin:24px;color:#17212b}table{border-collapse:collapse;width:100%}td,th{border:1px solid #cfd8df;padding:6px;text-align:left}.notice{background:#eef6ff;border-left:5px solid #357abd;padding:12px;margin:12px 0}</style></head><body>");
        sb.AppendLine("<h1>Central Sync Queue Report</h1>");
        sb.AppendLine($"<div class=\"notice\"><strong>Readiness:</strong> {Escape(readiness.Status)}. Local SQLite remains the offline source of truth. Raw customer images are not included unless explicitly configured.</div>");
        sb.AppendLine("<table><tr><th>Created</th><th>Item Type</th><th>Item ID</th><th>Station</th><th>Endpoint/Folder</th><th>Retries</th><th>Status</th><th>Last Error</th></tr>");
        foreach (var row in rows)
        {
            sb.AppendLine($"<tr><td>{row.CreatedAtUtc:u}</td><td>{Escape(row.ItemType)}</td><td>{Escape(row.ItemId)}</td><td>{Escape(row.StationId)}</td><td>{Escape(RedactEndpoint(row.EndpointOrFolder, settings))}</td><td>{row.RetryCount}/{row.MaxRetryCount}</td><td>{Escape(row.Status)}</td><td>{Escape(CentralSyncSettingsService.RedactSecrets(row.LastError, settings))}</td></tr>");
        }

        sb.AppendLine("</table></body></html>");
        return sb.ToString();
    }

    private static CentralSyncQueueReportRow ToReportRow(CentralSyncQueueRecord row, CentralSyncSettings settings)
        => new(
            row.Id,
            row.CreatedAtUtc,
            row.LastAttemptAtUtc,
            row.NextAttemptAtUtc,
            row.ItemType,
            row.ItemId,
            row.StationId,
            RedactEndpoint(row.EndpointOrFolder, settings),
            row.RetryCount,
            row.MaxRetryCount,
            row.Status,
            CentralSyncSettingsService.RedactSecrets(row.LastError, settings));

    private static void AddExistingFile(List<string> files, string path)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            files.Add(path);
    }

    private static string RedactedEndpoint(CentralSyncSettings settings)
        => RedactEndpoint(settings.EndpointOrFolder, settings);

    private static string RedactEndpoint(string endpointOrFolder, CentralSyncSettings settings)
        => settings.RedactEndpointInExports && !string.IsNullOrWhiteSpace(endpointOrFolder)
            ? "***"
            : CentralSyncSettingsService.RedactSecrets(endpointOrFolder, settings);

    private static string SafeName(string value)
        => string.Concat((value ?? string.Empty).Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_'));

    private static string Escape(string value)
        => System.Net.WebUtility.HtmlEncode(value ?? string.Empty);
}

public sealed record CentralSyncOutcome(
    bool Accepted,
    IntegrationConnectionStatus Status,
    string Message,
    string PayloadPath);

public sealed record CentralSyncQueueReportRow(
    long Id,
    DateTime CreatedAtUtc,
    DateTime? LastAttemptAtUtc,
    DateTime? NextAttemptAtUtc,
    string ItemType,
    string ItemId,
    string StationId,
    string EndpointOrFolder,
    int RetryCount,
    int MaxRetryCount,
    string Status,
    string LastError);
