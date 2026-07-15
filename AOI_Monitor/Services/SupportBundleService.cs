using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AOI_Monitor.Data;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public sealed class SupportBundleOptions
{
    public string OutputRoot { get; set; } = string.Empty;
    public bool IncludeModelFiles { get; set; }
    public bool RedactCustomerImagePaths { get; set; } = true;
    public bool RedactStorageRoot { get; set; } = true;
}

public sealed class SupportBundleResult
{
    public string ZipPath { get; set; } = string.Empty;
    public string StagingFolder { get; set; } = string.Empty;
    public string ManifestPath { get; set; } = string.Empty;
    public string SummaryHtmlPath { get; set; } = string.Empty;
    public SupportBundleManifest Manifest { get; set; } = new();
}

public sealed class SupportBundleManifest
{
    public string SchemaVersion { get; set; } = "support-bundle/v1";
    public string BundleId { get; set; } = string.Empty;
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
    public string AppVersion { get; set; } = string.Empty;
    public bool IncludeModelFiles { get; set; }
    public bool RedactCustomerImagePaths { get; set; } = true;
    public bool RedactStorageRoot { get; set; } = true;
    public List<string> ExcludedData { get; set; } = new();
    public List<ValidationIncludedFile> IncludedFiles { get; set; } = new();
}

public static class SupportBundleService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static SupportBundleResult Export(SupportBundleOptions? options = null, string operatorId = "UNKNOWN")
    {
        options ??= new SupportBundleOptions();
        AoiDatabase.Initialize();

        var root = string.IsNullOrWhiteSpace(options.OutputRoot)
            ? Path.Combine(AoiDatabase.StorageRoot, "exports", "support_bundle")
            : options.OutputRoot.Trim();
        Directory.CreateDirectory(root);
        var bundleId = $"support_bundle_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
        var staging = EnsureUniqueFolder(Path.Combine(root, bundleId));
        Directory.CreateDirectory(staging);

        var context = BuildContext(options);
        WriteJson(staging, "support_context.json", context, options);
        WriteJson(staging, "readiness_summary.json", context.Readiness, options);
        WriteJson(staging, "build_test_evidence_summary.json", context.BuildTestEvidence, options);
        WriteJson(staging, "acceptance_summaries.json", context.AcceptanceSummaries, options);
        WriteJson(staging, "audit_summary.json", context.AuditSummary, options);
        WriteJson(staging, "configuration_summary.json", context.ConfigurationSummary, options);
        WriteJson(staging, "error_summary.json", context.ErrorSummary, options);
        WriteModelMetadata(staging, options);

        var summaryPath = Path.Combine(staging, "support_summary.html");
        File.WriteAllText(summaryPath, BuildHtml(context), Encoding.UTF8);

        var manifest = BuildManifest(staging, bundleId, options);
        var manifestPath = Path.Combine(staging, "support_bundle_manifest.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions), Encoding.UTF8);
        manifest = BuildManifest(staging, bundleId, options);
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions), Encoding.UTF8);

        var zipPath = Path.Combine(root, "support_bundle.zip");
        if (File.Exists(zipPath))
            File.Delete(zipPath);
        ZipFile.CreateFromDirectory(staging, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);

        ExportVerificationService.RecordVerifiedExport("SupportBundleZip", zipPath, "OK", operatorId);
        AoiDatabase.RecordAuditEvent(
            "SUPPORT_BUNDLE_EXPORT",
            $"Support bundle exported: {Path.GetFileName(zipPath)}; includeModelFiles={options.IncludeModelFiles}; redactImagePaths={options.RedactCustomerImagePaths}.",
            operatorWithRole: operatorId,
            relatedEntityType: "SupportBundle",
            relatedPath: zipPath);

        return new SupportBundleResult
        {
            ZipPath = zipPath,
            StagingFolder = staging,
            ManifestPath = manifestPath,
            SummaryHtmlPath = summaryPath,
            Manifest = manifest,
        };
    }

    private static SupportBundleContext BuildContext(SupportBundleOptions options)
    {
        var diagnostics = SystemDiagnosticService.RunDiagnostics();
        var readiness = FactoryReadinessService.Evaluate(FactoryReadinessService.CriteriaForProfile(DeploymentProfileSettingsService.Load()));
        var audits = AoiDatabase.GetAuditEvents(new LogFilter(), 100);
        var exportVerifications = AoiDatabase.GetExportVerifications(50);
        var mesSettings = MesIntegrationSettingsService.Load();
        var centralSettings = CentralSyncSettingsService.Load();
        var memoryDiagnostics = MemoryDiagnosticsService.Capture();

        return new SupportBundleContext
        {
            AppVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0",
            OsInfo = Environment.OSVersion.VersionString,
            MachineName = Environment.MachineName,
            StorageRootSummary = new
            {
                storageRoot = options.RedactStorageRoot ? RedactedPath(AoiDatabase.StorageRoot) : AoiDatabase.StorageRoot,
                exists = Directory.Exists(AoiDatabase.StorageRoot),
                databasePath = options.RedactStorageRoot ? RedactedPath(AoiDatabase.DatabasePath) : AoiDatabase.DatabasePath,
                imageVaultExcluded = true,
            },
            SchemaVersion = AoiDatabase.GetSchemaVersion(),
            DatabaseIntegrity = AoiDatabase.RunIntegrityCheck(),
            DatabaseHealth = AoiDatabase.GetDatabaseHealthRows(),
            Diagnostics = diagnostics,
            MemoryDiagnostics = memoryDiagnostics,
            Readiness = readiness,
            BuildTestEvidence = BuildTestEvidenceService.GetSummary(),
            ModelAcceptanceSummary = SummarizeModelAcceptance(AoiDatabase.GetLatestModelAcceptanceRun()),
            AcceptanceSummaries = new
            {
                camera = CameraAcceptanceTestService.ToSummary(AoiDatabase.GetLatestCameraAcceptanceRun(realHardwareOnly: false)),
                robot = RobotAcceptanceTestService.ToSummary(AoiDatabase.GetLatestRobotAcceptanceRun()),
                mes = MesSpoolService.EvaluateReadiness(),
                traceability = AoiDatabase.GetLatestTraceabilityTestReport(),
                lighting = AoiDatabase.GetLatestLightingAcceptanceRun(),
                profile3D = AoiDatabase.GetLatestProfile3DAcceptanceRun(),
            },
            AuditSummary = new
            {
                totalIncluded = audits.Count,
                byCategory = audits.GroupBy(audit => audit.ActionCategory).OrderByDescending(group => group.Count()).Take(20).ToDictionary(group => group.Key, group => group.Count()),
                recent = audits.Take(25).Select(audit => new
                {
                    audit.Id,
                    audit.TimestampUtc,
                    audit.UserId,
                    audit.UserRole,
                    audit.StationId,
                    audit.ActionCategory,
                    actionDetail = RedactText(audit.ActionDetail, options),
                    relatedEntityType = audit.RelatedEntityType,
                    relatedEntityId = audit.RelatedEntityId,
                    relatedPath = RedactPathText(audit.RelatedPath, options),
                }).ToArray(),
            },
            ConfigurationSummary = new
            {
                inspectionEngine = SummarizeInspectionConfiguration(InspectionModelConfigurationService.Load(), options),
                camera = SummarizeCamera(CameraSourceSettingsService.Load(), options),
                lighting = LightingSettingsService.Load(),
                mes = MesIntegrationSettingsService.RedactedSummary(mesSettings),
                centralSync = CentralSyncSettingsService.RedactedSummary(centralSettings),
                deploymentProfile = DeploymentProfileSettingsService.Load().ToString(),
                authenticationMode = AuthenticationSettingsService.CurrentMode.ToString(),
                taxonomy = DefectTaxonomyService.GetActiveTaxonomy().Taxonomy,
            },
            ErrorSummary = new
            {
                diagnosticsErrors = diagnostics.Checks.Where(check => check.Status == DiagnosticStatus.Error).ToArray(),
                readinessBlocking = readiness.BlockingIssues,
                exportErrors = exportVerifications.Where(item => item.Status.Equals("ERROR", StringComparison.OrdinalIgnoreCase)).Take(20).ToArray(),
                recentAuditErrors = audits.Where(audit =>
                    audit.ActionCategory.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ||
                    audit.ActionDetail.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                    audit.ActionDetail.Contains("exception", StringComparison.OrdinalIgnoreCase))
                    .Take(20)
                    .Select(audit => new
                    {
                        audit.TimestampUtc,
                        audit.ActionCategory,
                        actionDetail = RedactText(audit.ActionDetail, options),
                    })
                    .ToArray(),
            },
        };
    }

    private static object SummarizeInspectionConfiguration(InspectionModelConfiguration configuration, SupportBundleOptions options)
        => new
        {
            configuration.SelectedEngineKey,
            configuration.ActiveModelId,
            configuration.ActiveModelSha256,
            configuration.ActiveModelValidationStatus,
            modelFilePath = options.IncludeModelFiles ? RedactPathText(configuration.ModelFilePath, options) : RedactedPath(configuration.ModelFilePath),
            configuration.ModelVersion,
            configuration.InputImageWidth,
            configuration.InputImageHeight,
            configuration.InputTensorName,
            configuration.OutputTensorName,
            configuration.ConfidenceThreshold,
            labelMapPath = RedactPathText(configuration.LabelMapPath, options),
            configuration.LastModelCheckTimestampUtc,
            configuration.LastModelCheckResult,
            lastModelCheckMessage = RedactText(configuration.LastModelCheckMessage, options),
        };

    private static object SummarizeCamera(CameraSourceSettings settings, SupportBundleOptions options)
        => new
        {
            settings.SourceKey,
            topFolder = RedactPathText(settings.TopFolder, options),
            sideFolder = RedactPathText(settings.SideFolder, options),
            bottomFolder = RedactPathText(settings.BottomFolder, options),
            settings.TopDeviceId,
            settings.SideDeviceId,
            settings.BottomDeviceId,
            adapterFolder = RedactPathText(settings.AdapterFolder, options),
            settings.AcquisitionMode,
            settings.ExposureMs,
            settings.Gain,
            settings.TriggerTimeoutMs,
            settings.FrameTimeoutMs,
            settings.BoardModel,
            settings.LotId,
        };

    private static object SummarizeModelAcceptance(ModelAcceptanceRun? run)
    {
        if (run is null)
            return new { status = "NOT RUN" };

        return new
        {
            run.Id,
            run.CreatedAtUtc,
            run.ModelId,
            run.ModelVersion,
            run.ModelSha256,
            run.DatasetName,
            run.Status,
            run.IsProductionCandidate,
            run.Metrics,
            run.DatasetQualitySummary,
            run.FalseCallRecommendation,
            run.PerformanceSummary,
            run.P95InferenceMs,
            Messages = run.Messages.Take(10).ToArray(),
        };
    }

    private static void WriteModelMetadata(string staging, SupportBundleOptions options)
    {
        var folder = Path.Combine(staging, "model_metadata");
        Directory.CreateDirectory(folder);
        var models = ModelRegistryService.GetModels()
            .Select(model => new
            {
                model.ModelId,
                model.DisplayName,
                model.Version,
                model.Sha256,
                model.ValidationStatus,
                model.LifecycleState,
                model.LatestAcceptanceStatus,
                model.LatestAcceptanceRunId,
                model.LatestReleasePackageId,
                model.IsActive,
                storedModelPath = options.IncludeModelFiles ? RedactPathText(model.StoredModelPath, options) : RedactedPath(model.StoredModelPath),
                storedLabelMapPath = RedactPathText(model.StoredLabelMapPath, options),
            })
            .ToArray();
        File.WriteAllText(Path.Combine(folder, "model_registry_metadata.json"), RedactText(JsonSerializer.Serialize(models, JsonOptions), options), Encoding.UTF8);

        if (!options.IncludeModelFiles)
            return;

        var filesFolder = Path.Combine(folder, "model_files");
        Directory.CreateDirectory(filesFolder);
        foreach (var model in ModelRegistryService.GetModels())
        {
            if (!string.IsNullOrWhiteSpace(model.StoredModelPath) && File.Exists(model.StoredModelPath))
                File.Copy(model.StoredModelPath, Path.Combine(filesFolder, $"{model.ModelId}_{Path.GetFileName(model.StoredModelPath)}"), overwrite: true);
        }
    }

    private static void WriteJson(string folder, string fileName, object value, SupportBundleOptions options)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        File.WriteAllText(Path.Combine(folder, fileName), RedactText(json, options), Encoding.UTF8);
    }

    private static SupportBundleManifest BuildManifest(string staging, string bundleId, SupportBundleOptions options)
    {
        var manifestPath = Path.Combine(staging, "support_bundle_manifest.json");
        var files = Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories)
            .Where(path => !path.Equals(manifestPath, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => Path.GetRelativePath(staging, path), StringComparer.OrdinalIgnoreCase)
            .Select(path => new ValidationIncludedFile
            {
                RelativePath = Path.GetRelativePath(staging, path).Replace('\\', '/'),
                FileType = Classify(path),
                Bytes = new FileInfo(path).Length,
                Sha256 = HashUtil.ComputeSha256(path),
            })
            .ToList();

        return new SupportBundleManifest
        {
            BundleId = bundleId,
            GeneratedAtUtc = DateTime.UtcNow,
            AppVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0",
            IncludeModelFiles = options.IncludeModelFiles,
            RedactCustomerImagePaths = options.RedactCustomerImagePaths,
            RedactStorageRoot = options.RedactStorageRoot,
            IncludedFiles = files,
            ExcludedData =
            {
                "raw customer images",
                "image_vault/",
                "SQLite WAL/SHM runtime files",
                "API keys, bearer tokens, and passwords",
                options.IncludeModelFiles ? "model files explicitly included by operator selection" : "model files",
            },
        };
    }

    private static string BuildHtml(SupportBundleContext context)
    {
        var readiness = context.Readiness;
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html><html><head><meta charset=\"utf-8\"><title>AOI Support Bundle</title>");
        sb.AppendLine("<style>body{font-family:Segoe UI,Arial,sans-serif;margin:24px;color:#17212b}table{border-collapse:collapse;width:100%}td,th{border:1px solid #cfd8df;padding:7px;text-align:left;vertical-align:top}th{background:#edf2f5}.notice{background:#fff8e8;border-left:5px solid #d9951b;padding:10px}</style></head><body>");
        sb.AppendLine("<h1>AOI Monitor Support Bundle</h1>");
        sb.AppendLine("<div class=\"notice\">Raw customer images, image vault contents, model files by default, and secrets are excluded or redacted.</div>");
        sb.AppendLine($"<p><strong>App:</strong> {Html(context.AppVersion)}<br><strong>OS:</strong> {Html(context.OsInfo)}<br><strong>Schema:</strong> {context.SchemaVersion}<br><strong>DB integrity:</strong> {Html(context.DatabaseIntegrity)}</p>");
        sb.AppendLine($"<h2>Memory</h2><p><strong>Working set:</strong> {context.MemoryDiagnostics.WorkingSetBytes / 1024d / 1024d:F0} MB<br><strong>Managed GC:</strong> {context.MemoryDiagnostics.ManagedBytes / 1024d / 1024d:F0} MB<br><strong>Image cache:</strong> {context.MemoryDiagnostics.ImageCacheCount} full / {context.MemoryDiagnostics.ThumbnailCacheCount} thumbnails; {context.MemoryDiagnostics.ImageCacheBytes / 1024d / 1024d:F0} MB; large bitmaps={context.MemoryDiagnostics.LargeBitmapCount}; active pages={context.MemoryDiagnostics.ActivePageCount}<br><strong>Latest export peak:</strong> {context.MemoryDiagnostics.LatestExportOperation}: working set {context.MemoryDiagnostics.LatestExportWorkingSetPeakBytes / 1024d / 1024d:F0} MB; managed {context.MemoryDiagnostics.LatestExportManagedPeakBytes / 1024d / 1024d:F0} MB; processed={context.MemoryDiagnostics.LatestExportProcessedCount}</p>");
        if (context.MemoryDiagnostics.ExportMemoryWarnings.Count > 0)
            sb.AppendLine($"<p><strong>Export memory warnings:</strong> {Html(string.Join(" | ", context.MemoryDiagnostics.ExportMemoryWarnings.TakeLast(10)))}</p>");
        sb.AppendLine($"<h2>Readiness</h2><p><strong>{Html(readiness.OverallStatus)}</strong> for {Html(readiness.DeploymentProfile)}. Blocking={readiness.BlockingIssues.Count}; warnings={readiness.Warnings.Count}.</p>");
        sb.AppendLine("<table><tr><th>Category</th><th>Status</th><th>Evidence</th></tr>");
        foreach (var category in readiness.Categories.Take(16))
            sb.AppendLine($"<tr><td>{Html(category.Name)}</td><td>{Html(category.Status)}</td><td>{Html(category.Evidence)}</td></tr>");
        sb.AppendLine("</table>");
        sb.AppendLine("<h2>Support Files</h2><p>See <code>support_bundle_manifest.json</code> for checksums and file list.</p>");
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    private static string RedactText(string text, SupportBundleOptions options)
    {
        var redacted = text ?? string.Empty;
        redacted = MesIntegrationSettingsService.RedactSecrets(redacted, MesIntegrationSettingsService.Load());
        redacted = CentralSyncSettingsService.RedactSecrets(redacted, CentralSyncSettingsService.Load());
        redacted = SecretProtectionService.RedactKnownSecrets(redacted);
        redacted = redacted.Replace(SecretProtectionService.ProtectedPrefix, "PROTECTED:", StringComparison.Ordinal);
        if (options.RedactStorageRoot)
            redacted = RedactPathValue(redacted, AoiDatabase.StorageRoot, "[STORAGE_ROOT]");
        if (options.RedactCustomerImagePaths)
        {
            redacted = RedactPathValue(redacted, AoiDatabase.ImageVaultPath, "[IMAGE_VAULT]");
            foreach (var extension in new[] { ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff" })
                redacted = RedactFilePathsByExtension(redacted, extension);
        }

        return redacted;
    }

    private static string RedactPathText(string value, SupportBundleOptions options)
        => RedactText(value ?? string.Empty, options);

    private static string RedactedPath(string value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : $"[REDACTED_PATH]/{Path.GetFileName(value)}";

    private static string RedactPathValue(string text, string path, string marker)
    {
        if (string.IsNullOrWhiteSpace(path))
            return text;
        return text.Replace(path, marker, StringComparison.OrdinalIgnoreCase)
            .Replace(path.Replace("\\", "\\\\", StringComparison.Ordinal), marker, StringComparison.OrdinalIgnoreCase);
    }

    private static string RedactFilePathsByExtension(string text, string extension)
    {
        var marker = $"[REDACTED_IMAGE_PATH]{extension}";
        var parts = text.Split('"');
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Contains(extension, StringComparison.OrdinalIgnoreCase) &&
                (parts[i].Contains("\\", StringComparison.Ordinal) || parts[i].Contains("/", StringComparison.Ordinal)))
            {
                parts[i] = marker;
            }
        }

        return string.Join("\"", parts);
    }

    private static string Classify(string path)
    {
        var fileName = Path.GetFileName(path);
        if (fileName.Contains("manifest", StringComparison.OrdinalIgnoreCase))
            return "Support bundle manifest";
        if (fileName.Contains("summary", StringComparison.OrdinalIgnoreCase))
            return "Support summary";
        if (fileName.Contains("readiness", StringComparison.OrdinalIgnoreCase))
            return "Readiness summary";
        if (fileName.Contains("configuration", StringComparison.OrdinalIgnoreCase))
            return "Configuration summary";
        if (fileName.Contains("audit", StringComparison.OrdinalIgnoreCase))
            return "Audit summary";
        if (fileName.Contains("error", StringComparison.OrdinalIgnoreCase))
            return "Error summary";
        if (fileName.Contains("model", StringComparison.OrdinalIgnoreCase))
            return "Model metadata";
        return "Support evidence";
    }

    private static string EnsureUniqueFolder(string folder)
    {
        if (!Directory.Exists(folder))
            return folder;
        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{folder}_{i:D2}";
            if (!Directory.Exists(candidate))
                return candidate;
        }

        return $"{folder}_{Guid.NewGuid():N}";
    }

    private static string Html(string value)
        => System.Net.WebUtility.HtmlEncode(value ?? string.Empty);

    private sealed class SupportBundleContext
    {
        public string AppVersion { get; set; } = string.Empty;
        public string OsInfo { get; set; } = string.Empty;
        public string MachineName { get; set; } = string.Empty;
        public object StorageRootSummary { get; set; } = new();
        public int SchemaVersion { get; set; }
        public string DatabaseIntegrity { get; set; } = string.Empty;
        public IReadOnlyList<DbHealthRow> DatabaseHealth { get; set; } = Array.Empty<DbHealthRow>();
        public SystemDiagnosticReport Diagnostics { get; set; } = new();
        public MemoryDiagnosticsSnapshot MemoryDiagnostics { get; set; } = new();
        public FactoryReadinessReport Readiness { get; set; } = new();
        public BuildTestEvidenceSummary BuildTestEvidence { get; set; } = new();
        public object ModelAcceptanceSummary { get; set; } = new();
        public object AcceptanceSummaries { get; set; } = new();
        public object AuditSummary { get; set; } = new();
        public object ConfigurationSummary { get; set; } = new();
        public object ErrorSummary { get; set; } = new();
    }
}
