using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AOI_Monitor.Data;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public static class ModelAcceptanceService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg",
    };

    public static ModelAcceptanceRun RunAcceptance(
        string validationDatasetFolder,
        string groundTruthCsvPath,
        ModelAcceptanceCriteria? criteria = null,
        string? operatorId = null)
    {
        AoiDatabase.Initialize();
        criteria ??= new ModelAcceptanceCriteria();
        var configuration = InspectionModelConfigurationService.Load();
        var activeModel = ModelRegistryService.GetActiveModel();

        if (!configuration.IsOnnxSelected || activeModel is null)
            return PersistFailure(criteria, validationDatasetFolder, groundTruthCsvPath, operatorId, "No active ONNX model is selected. Pixel Difference is a prototype Stage 1 engine and cannot be accepted as a production candidate model.");

        if (!File.Exists(activeModel.StoredModelPath))
            return PersistFailure(criteria, validationDatasetFolder, groundTruthCsvPath, operatorId, $"Active model file is missing: {activeModel.StoredModelPath}", activeModel);

        if (activeModel.ValidationStatus != ModelConfigurationTestStatus.Ready)
            return PersistFailure(criteria, validationDatasetFolder, groundTruthCsvPath, operatorId, $"Active model runtime validation is not Ready: {activeModel.ValidationStatus}.", activeModel);

        if (!Directory.Exists(validationDatasetFolder))
            return PersistFailure(criteria, validationDatasetFolder, groundTruthCsvPath, operatorId, $"Validation dataset folder was not found: {validationDatasetFolder}", activeModel);

        try
        {
            var manifest = BatchValidationService.LoadValidationManifest(groundTruthCsvPath, validationDatasetFolder);
            var imageFiles = Directory.EnumerateFiles(validationDatasetFolder)
                .Where(path => ImageExtensions.Contains(Path.GetExtension(path)))
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var items = BatchValidationService.BuildRunItems(imageFiles, manifest);
            var engine = new OnnxInspectionEngine(ModelRegistryService.ToInspectionConfiguration(activeModel));
            var rows = new List<BatchTestRow>();
            foreach (var item in items)
            {
                try
                {
                    rows.Add(BatchValidationService.ToRow(item.ImagePath, item.Manifest, engine.Analyze(item.ImagePath, item.Manifest.GoldenPath, DetectionPriority.Balanced)));
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
                {
                    rows.Add(BatchValidationService.ToErrorRow(item.ImagePath, item.Manifest, ex.Message, engine.Name, engine.Version));
                }
            }

            var metrics = BatchValidationService.CalculateMetrics(rows);
            var performance = BatchValidationService.CalculatePerformanceSummary(rows);
            var datasetQuality = DatasetQualityService.Analyze(rows, manifest);
            var falseCallRun = FalseCallReductionService.Analyze(rows, engine.Name, engine.Version, activeModel.ModelId, activeModel.Sha256, null);
            var falseCallSummary = FalseCallReductionService.ToSummary(falseCallRun);
            var breakdown = ClassMetricsService.Calculate(rows);
            var messages = Evaluate(criteria, metrics, performance, datasetQuality, falseCallSummary, manifest, rows).ToList();
            var status = messages.Any(message => message.StartsWith("FAIL:", StringComparison.OrdinalIgnoreCase))
                ? "FAIL"
                : messages.Any(message => message.StartsWith("CONDITIONAL:", StringComparison.OrdinalIgnoreCase))
                    ? "CONDITIONAL"
                    : "PASS";

            var run = BuildRun(activeModel, criteria, validationDatasetFolder, groundTruthCsvPath, manifest.IsFormalManifest, status, operatorId);
            run.Metrics = metrics;
            run.PerformanceSummary = performance;
            run.P95InferenceMs = Percentile(rows.Select(row => row.InferenceMilliseconds).OrderBy(value => value).ToArray(), 0.95);
            run.DatasetQualitySummary = datasetQuality;
            run.FalseCallRecommendation = falseCallSummary;
            run.BreakdownSummary = breakdown;
            run.Messages = messages;
            run.Limitations = DefaultLimitations();
            run.Id = AoiDatabase.RecordModelAcceptanceRun(run);
            return run;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return PersistFailure(criteria, validationDatasetFolder, groundTruthCsvPath, operatorId, ex.Message, activeModel);
        }
    }

    public static ModelReleasePackageRecord CreateReleasePackage(long acceptanceRunId, string outputRoot, string approvedBy)
    {
        var run = AoiDatabase.GetLatestModelAcceptanceRun();
        if (run is null || run.Id != acceptanceRunId)
            throw new InvalidOperationException("Model acceptance run was not found.");

        var root = string.IsNullOrWhiteSpace(outputRoot)
            ? Path.Combine(AoiDatabase.StorageRoot, "exports", "model_releases")
            : outputRoot.Trim();
        var folder = Path.Combine(root, $"model_release_{run.ModelId}_{DateTime.UtcNow:yyyyMMdd_HHmmss}");
        Directory.CreateDirectory(folder);

        var manifestPath = Path.Combine(folder, "model_release_manifest.json");
        var reportPath = Path.Combine(folder, "model_acceptance_report.html");
        var reportPdfPath = Path.Combine(folder, "model_acceptance_report.pdf");
        var metricsPath = Path.Combine(folder, "model_acceptance_metrics.csv");
        var breakdownPath = Path.Combine(folder, "validation_breakdown.csv");
        File.WriteAllText(metricsPath, BuildMetricsCsv(run), Encoding.UTF8);
        File.WriteAllText(breakdownPath, ClassMetricsService.BuildCsv(run.BreakdownSummary), Encoding.UTF8);
        if (!string.IsNullOrWhiteSpace(run.LabelMapPath) && File.Exists(run.LabelMapPath))
            File.Copy(run.LabelMapPath, Path.Combine(folder, Path.GetFileName(run.LabelMapPath)), overwrite: true);

        var manifest = new
        {
            schemaVersion = "model-release/v1",
            generatedAtUtc = DateTime.UtcNow,
            approvedBy,
            run.Status,
            run.ModelId,
            run.ModelVersion,
            modelSha256 = run.ModelSha256,
            run.InputTensorName,
            run.OutputTensorName,
            expectedOutputShape = run.OutputShape,
            labelMapPath = string.IsNullOrWhiteSpace(run.LabelMapPath) ? "" : Path.GetFileName(run.LabelMapPath),
            labelMapSha256 = File.Exists(run.LabelMapPath) ? Sha256(run.LabelMapPath) : "",
            validationDataset = run.DatasetName,
            validationDatasetHash = DatasetHash(run.DatasetFolder, run.GroundTruthCsvPath),
            criteria = run.Criteria,
            metrics = run.Metrics,
            metricsCsv = Path.GetFileName(metricsPath),
            validationBreakdownCsv = Path.GetFileName(breakdownPath),
            datasetQuality = run.DatasetQualitySummary,
            falseCallRecommendation = run.FalseCallRecommendation,
            limitations = run.Limitations,
        };
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions), Encoding.UTF8);
        File.WriteAllText(reportPath, BuildHtml(run, approvedBy), Encoding.UTF8);
        PdfExportService.ExportHtmlFileToPdf(reportPath, reportPdfPath, "Model Acceptance Report");
        ExportVerificationService.RecordVerifiedExport("ModelAcceptanceReportPdf", reportPdfPath, run.Status, approvedBy);

        var record = new ModelReleasePackageRecord(0, DateTime.UtcNow, run.Id, run.ModelId, run.ModelVersion, run.ModelSha256, folder, manifestPath, reportPath, run.Status, approvedBy, null);
        var id = AoiDatabase.RecordModelReleasePackage(record);
        return record with { Id = id };
    }

    public static void PromoteToProductionCandidate(long acceptanceRunId, UserRole role, string approvedBy)
    {
        if (!RoleAuthorization.CanChangeThresholds(role))
            throw new UnauthorizedAccessException(RoleAuthorization.DeniedMessage(role, "promoting production candidate models"));

        var run = AoiDatabase.GetLatestModelAcceptanceRun();
        if (run is null || run.Id != acceptanceRunId)
            throw new InvalidOperationException("Model acceptance run was not found.");
        if (!string.Equals(run.Status, "PASS", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only PASS model acceptance runs can be promoted to production candidate.");

        AoiDatabase.PromoteModelAcceptanceRun(run.Id, approvedBy);
        AoiDatabase.RecordAuditEvent("MODEL_PRODUCTION_CANDIDATE", $"Promoted model acceptance run {run.Id} for model {run.ModelId} to production candidate. Validation is scoped to the acceptance dataset.", operatorWithRole: approvedBy, relatedEntityType: "ModelAcceptanceRun", relatedEntityId: run.Id.ToString(CultureInfo.InvariantCulture));
    }

    private static IEnumerable<string> Evaluate(ModelAcceptanceCriteria criteria, BatchMetrics metrics, BatchPerformanceSummary performance, DatasetQualitySummary dataset, FalseCallRecommendationSummary falseCall, ValidationManifest manifest, IReadOnlyCollection<BatchTestRow> rows)
    {
        if (criteria.RequireFormalManifest && !manifest.IsFormalManifest)
            yield return "FAIL: Formal validation manifest is required.";
        if (metrics.Accuracy < criteria.MinimumAccuracy)
            yield return $"FAIL: Accuracy {metrics.Accuracy:P1} is below {criteria.MinimumAccuracy:P1}.";
        if (metrics.Precision < criteria.MinimumPrecision)
            yield return $"FAIL: Precision {metrics.Precision:P1} is below {criteria.MinimumPrecision:P1}.";
        if (metrics.Recall < criteria.MinimumRecall)
            yield return $"FAIL: Recall {metrics.Recall:P1} is below {criteria.MinimumRecall:P1}.";
        if (metrics.FalseCallRate > criteria.MaximumFalseCallRate)
            yield return $"FAIL: False-call rate {metrics.FalseCallRate:P1} exceeds {criteria.MaximumFalseCallRate:P1}.";
        if (falseCall.PossibleEscapeRate > criteria.MaximumPossibleEscapeRate)
            yield return $"FAIL: Possible escape rate {falseCall.PossibleEscapeRate:P1} exceeds {criteria.MaximumPossibleEscapeRate:P1}.";
        var known = Math.Max(1, rows.Count(row => BatchValidationService.NormalizeBinaryLabel(row.GroundTruth) != "UNKNOWN"));
        var reviewRate = rows.Count(row => string.Equals(row.EngineResult, "REVIEW", StringComparison.OrdinalIgnoreCase)) / (double)known;
        if (reviewRate > criteria.MaximumReviewRate)
            yield return $"FAIL: Review rate {reviewRate:P1} exceeds {criteria.MaximumReviewRate:P1}.";
        var inferenceValues = rows.Select(row => row.InferenceMilliseconds).ToArray();
        var averageInference = inferenceValues.Length == 0 ? 0 : inferenceValues.Average();
        if (averageInference > criteria.MaximumAverageInferenceMs)
            yield return $"FAIL: Average inference {averageInference:F1} ms exceeds {criteria.MaximumAverageInferenceMs:F1} ms.";
        var p95 = Percentile(rows.Select(row => row.InferenceMilliseconds).OrderBy(value => value).ToArray(), 0.95);
        if (p95 > criteria.MaximumP95InferenceMs)
            yield return $"FAIL: P95 inference {p95:F1} ms exceeds {criteria.MaximumP95InferenceMs:F1} ms.";
        if (!DatasetStatusAtLeast(dataset.Status, criteria.MinimumDatasetQualityStatus))
            yield return $"FAIL: Dataset quality {dataset.Status} is below required {criteria.MinimumDatasetQualityStatus}.";
        if (criteria.RequireDefectClassCoverage && dataset.DefectClassCount < 1)
            yield return "FAIL: Defect class coverage is insufficient.";
        if (!rows.Any())
            yield return "FAIL: No validation rows were produced.";
        yield return "PASS: Acceptance evidence is scoped to this validation dataset only.";
    }

    private static ModelAcceptanceRun PersistFailure(ModelAcceptanceCriteria criteria, string datasetFolder, string csvPath, string? operatorId, string message, ModelRegistryEntry? model = null)
    {
        var run = BuildRun(model, criteria, datasetFolder, csvPath, false, "FAIL", operatorId);
        run.Messages.Add($"FAIL: {message}");
        run.Limitations = DefaultLimitations();
        run.Id = AoiDatabase.RecordModelAcceptanceRun(run);
        return run;
    }

    private static ModelAcceptanceRun BuildRun(ModelRegistryEntry? model, ModelAcceptanceCriteria criteria, string datasetFolder, string csvPath, bool isFormal, string status, string? operatorId)
        => new()
        {
            CreatedAtUtc = DateTime.UtcNow,
            ModelId = model?.ModelId ?? string.Empty,
            ModelVersion = model?.Version ?? string.Empty,
            ModelSha256 = model?.Sha256 ?? string.Empty,
            ModelPath = model?.StoredModelPath ?? string.Empty,
            LabelMapPath = model?.StoredLabelMapPath ?? string.Empty,
            InputTensorName = model?.InputTensorName ?? string.Empty,
            OutputTensorName = model?.OutputTensorName ?? string.Empty,
            OutputShape = "class, confidence, x, y, width, height rows",
            DatasetFolder = datasetFolder ?? string.Empty,
            DatasetName = string.IsNullOrWhiteSpace(datasetFolder) ? "Not provided" : Path.GetFileName(Path.GetFullPath(datasetFolder)),
            GroundTruthCsvPath = csvPath ?? string.Empty,
            IsFormalManifest = isFormal,
            Status = status,
            OperatorId = string.IsNullOrWhiteSpace(operatorId) ? "UNKNOWN" : operatorId.Trim(),
            Criteria = criteria,
        };

    private static bool DatasetStatusAtLeast(string actual, string required)
    {
        static int Rank(string status) => status.ToUpperInvariant() switch { "PASS" => 3, "CONDITIONAL" => 2, "FAIL" => 1, _ => 0 };
        return Rank(actual) >= Rank(required);
    }

    private static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        if (values.Count == 0)
            return 0;
        var index = Math.Clamp((int)Math.Ceiling(percentile * values.Count) - 1, 0, values.Count - 1);
        return values[index];
    }

    private static List<string> DefaultLimitations() =>
    [
        "Model acceptance is limited to the supplied customer validation dataset and criteria.",
        "No raw customer images are included in the model release package.",
        "Passing acceptance does not prove universal production accuracy across all boards, lighting, cameras, or factories.",
    ];

    private static string BuildHtml(ModelAcceptanceRun run, string approvedBy)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html><html><head><meta charset=\"utf-8\"><title>Model Acceptance Report</title>");
        sb.AppendLine("<style>body{font-family:Segoe UI,Arial,sans-serif;margin:24px}table{border-collapse:collapse}td,th{border:1px solid #c9d3da;padding:7px 9px}</style></head><body>");
        sb.AppendLine("<h1>Model Acceptance Report</h1>");
        sb.AppendLine($"<p><strong>Status:</strong> {Escape(run.Status)}<br><strong>Model:</strong> {Escape(run.ModelId)} / {Escape(run.ModelVersion)}<br><strong>SHA-256:</strong> {Escape(run.ModelSha256)}<br><strong>Approved by:</strong> {Escape(approvedBy)}</p>");
        sb.AppendLine($"<h2>Metrics</h2><table><tr><th>Accuracy</th><th>Precision</th><th>Recall</th><th>False Call</th><th>P95 Inference</th></tr><tr><td>{run.Metrics.Accuracy:P1}</td><td>{run.Metrics.Precision:P1}</td><td>{run.Metrics.Recall:P1}</td><td>{run.Metrics.FalseCallRate:P1}</td><td>{run.P95InferenceMs:F1} ms</td></tr></table>");
        sb.AppendLine($"<h2>Limitations</h2><ul>{string.Join("", run.Limitations.Select(item => $"<li>{Escape(item)}</li>"))}</ul>");
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    private static string BuildMetricsCsv(ModelAcceptanceRun run)
    {
        var totalImages = run.Metrics.OkCount + run.Metrics.NgCount + run.Metrics.Unknown;
        var reviewRate = totalImages == 0 ? 0 : run.Metrics.ReviewCount / (double)Math.Max(1, totalImages);
        var possibleEscapeRate = run.FalseCallRecommendation.PossibleEscapeRate;
        var averageInference = run.PerformanceSummary.AverageMilliseconds;
        var rows = new[]
        {
            ("status", run.Status),
            ("accuracy", run.Metrics.Accuracy.ToString("F6", CultureInfo.InvariantCulture)),
            ("precision", run.Metrics.Precision.ToString("F6", CultureInfo.InvariantCulture)),
            ("recall", run.Metrics.Recall.ToString("F6", CultureInfo.InvariantCulture)),
            ("false_call_rate", run.Metrics.FalseCallRate.ToString("F6", CultureInfo.InvariantCulture)),
            ("possible_escape_rate", possibleEscapeRate.ToString("F6", CultureInfo.InvariantCulture)),
            ("review_rate", reviewRate.ToString("F6", CultureInfo.InvariantCulture)),
            ("average_inference_ms", averageInference.ToString("F3", CultureInfo.InvariantCulture)),
            ("p95_inference_ms", run.P95InferenceMs.ToString("F3", CultureInfo.InvariantCulture)),
            ("dataset_quality", run.DatasetQualitySummary.Status),
        };

        var sb = new StringBuilder();
        sb.AppendLine("metric,value");
        foreach (var (name, value) in rows)
            sb.AppendLine($"\"{name}\",\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"");
        return sb.ToString();
    }

    private static string DatasetHash(string datasetFolder, string csvPath)
    {
        var text = $"{Path.GetFileName(datasetFolder)}|{(File.Exists(csvPath) ? Sha256(csvPath) : "")}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string Escape(string value) => System.Net.WebUtility.HtmlEncode(value ?? string.Empty);
}
