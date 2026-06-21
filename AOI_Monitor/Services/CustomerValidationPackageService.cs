using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AOI_Monitor.Data;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public sealed class CustomerValidationPackageRequest
{
    public string? OutputRoot { get; init; }
    public long? RunId { get; init; }
    public DateTime? RunCreatedAtUtc { get; init; }
    public string StationId { get; init; } = "AOI-LIB-01";
    public string OperatorId { get; init; } = "UNKNOWN";
    public string OperatorRole { get; init; } = "UNKNOWN";
    public string BoardModel { get; init; } = "Not provided";
    public string LotId { get; init; } = "Not provided";
    public string ModelId { get; init; } = "Not selected";
    public string ModelSha256 { get; init; } = "Not available";
    public string ModelValidationStatus { get; init; } = "Not Tested";
    public string EngineName { get; init; } = "Pixel Difference Prototype Engine";
    public string ModelVersion { get; init; } = "PIXEL_DIFF_0.1";
    public string ModelFileName { get; init; } = "Not configured";
    public double ConfidenceThreshold { get; init; }
    public string DatasetFolder { get; init; } = string.Empty;
    public string GroundTruthCsvPath { get; init; } = string.Empty;
    public bool IsFormalManifest { get; init; }
    public BatchMetrics Metrics { get; init; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    public BatchPerformanceSummary PerformanceSummary { get; init; } = new(0, 0, 0, 0, 0);
    public IReadOnlyCollection<BatchTestRow> Rows { get; init; } = Array.Empty<BatchTestRow>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public ValidationAcceptanceCriteria Criteria { get; init; } = new();
    public ValidationDatasetQualityCriteria DatasetQualityCriteria { get; init; } = new();
    public DatasetQualitySummary? DatasetQualitySummary { get; init; }
    public FalseCallReductionRun? FalseCallReductionRun { get; init; }
}

public sealed class CustomerValidationPackageResult
{
    public CustomerValidationPackageResult(
        string packageId,
        string packageFolder,
        string manifestPath,
        string reportPath,
        string csvPath,
        string annotatedImagesFolder,
        ValidationPackageManifest manifest,
        ValidationAcceptanceSummary acceptance,
        IReadOnlyList<string> warnings)
    {
        PackageId = packageId;
        PackageFolder = packageFolder;
        ManifestPath = manifestPath;
        ReportPath = reportPath;
        CsvPath = csvPath;
        AnnotatedImagesFolder = annotatedImagesFolder;
        Manifest = manifest;
        Acceptance = acceptance;
        Warnings = warnings;
    }

    public string PackageId { get; }
    public string PackageFolder { get; }
    public string ManifestPath { get; }
    public string ReportPath { get; }
    public string CsvPath { get; }
    public string AnnotatedImagesFolder { get; }
    public ValidationPackageManifest Manifest { get; }
    public ValidationAcceptanceSummary Acceptance { get; }
    public IReadOnlyList<string> Warnings { get; }
}

public static class CustomerValidationPackageService
{
    private const string ManifestSchemaVersion = "stage1-validation-package/v1";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static ValidationAcceptanceSummary EvaluateAcceptance(
        BatchMetrics metrics,
        BatchPerformanceSummary performance,
        int totalRows,
        bool isFormalManifest,
        ValidationAcceptanceCriteria? criteria = null,
        DatasetQualitySummary? datasetQuality = null)
    {
        criteria ??= new ValidationAcceptanceCriteria();
        var messages = new List<string>();
        var failures = new List<string>();
        var knownRows = Math.Max(0, totalRows - metrics.Unknown);
        var accuracyComputable = knownRows > 0;
        var precisionComputable = metrics.TruePositive + metrics.FalsePositive > 0;
        var recallComputable = metrics.TruePositive + metrics.FalseNegative > 0;
        var falseCallRateComputable = metrics.FalsePositive + metrics.TrueNegative > 0;
        var metricsComputed = accuracyComputable && precisionComputable && recallComputable && falseCallRateComputable;

        if (!accuracyComputable)
            messages.Add("Ground-truth labels are missing or unknown, so accuracy cannot be computed.");
        if (!precisionComputable)
            messages.Add("Precision cannot be computed because the run has no predicted NG samples.");
        if (!recallComputable)
            messages.Add("Recall cannot be computed because the run has no known NG ground-truth samples.");
        if (!falseCallRateComputable)
            messages.Add("False call rate cannot be computed because the run has no known OK ground-truth samples.");

        if (accuracyComputable && metrics.Accuracy < criteria.MinimumAccuracy)
            failures.Add($"Accuracy {FormatPercent(metrics.Accuracy)} is below the {FormatPercent(criteria.MinimumAccuracy)} gate.");
        if (precisionComputable && metrics.Precision < criteria.MinimumPrecision)
            failures.Add($"Precision {FormatPercent(metrics.Precision)} is below the {FormatPercent(criteria.MinimumPrecision)} gate.");
        if (recallComputable && metrics.Recall < criteria.MinimumRecall)
            failures.Add($"Recall {FormatPercent(metrics.Recall)} is below the {FormatPercent(criteria.MinimumRecall)} gate.");
        if (falseCallRateComputable && metrics.FalseCallRate > criteria.MaximumFalseCallRate)
            failures.Add($"False call rate {FormatPercent(metrics.FalseCallRate)} is above the {FormatPercent(criteria.MaximumFalseCallRate)} gate.");
        if (performance.CountOverOneSecond > criteria.MaximumImagesOverOneSecond)
            failures.Add($"{performance.CountOverOneSecond} image(s) exceeded the one-second target; maximum allowed is {criteria.MaximumImagesOverOneSecond}.");
        if (!isFormalManifest)
            messages.Add("Formal validation manifest was not present; acceptance is conditional until manifest evidence is supplied.");
        if (criteria.RequireFormalManifest && !isFormalManifest)
            failures.Add("Formal validation manifest is required by the active criteria.");
        if (datasetQuality is not null)
        {
            if (string.Equals(datasetQuality.Status, "FAIL", StringComparison.OrdinalIgnoreCase))
            {
                failures.Add("Dataset quality gate failed; validation package cannot be accepted as PASS.");
                messages.AddRange(datasetQuality.BlockingFailures);
            }
            else if (string.Equals(datasetQuality.Status, "CONDITIONAL", StringComparison.OrdinalIgnoreCase))
            {
                messages.Add("Dataset quality gate is conditional; validation package cannot be accepted as PASS.");
                messages.AddRange(datasetQuality.Warnings);
            }
        }

        var status = failures.Count > 0
            ? "FAIL"
            : messages.Count > 0
                ? "CONDITIONAL"
                : "PASS";

        var allMessages = failures.Concat(messages).ToList();
        if (allMessages.Count == 0)
            allMessages.Add("All configured numeric gates passed and formal manifest evidence was present.");

        return new ValidationAcceptanceSummary
        {
            Status = status,
            MetricsComputed = metricsComputed,
            FormalManifestPresent = isFormalManifest,
            NumericGatesPassed = failures.Count == 0,
            DatasetQualityStatus = datasetQuality?.Status ?? "CONDITIONAL",
            Messages = allMessages,
        };
    }

    public static CustomerValidationPackageResult CreatePackage(
        CustomerValidationPackageRequest request,
        CancellationToken cancellationToken = default)
    {
        var rows = request.Rows.ToArray();
        var criteria = request.Criteria ?? new ValidationAcceptanceCriteria();
        var generatedAtUtc = DateTime.UtcNow;
        var packageId = BuildPackageId(generatedAtUtc);
        var outputRoot = string.IsNullOrWhiteSpace(request.OutputRoot)
            ? Path.Combine(AoiDatabase.StorageRoot, "exports", "packages")
            : request.OutputRoot;
        var packageFolder = Path.Combine(outputRoot, $"stage1_validation_{generatedAtUtc:yyyyMMdd_HHmmss}");
        packageFolder = EnsureUniqueFolder(packageFolder);
        Directory.CreateDirectory(packageFolder);

        var csvPath = Path.Combine(packageFolder, "validation_results.csv");
        var breakdownCsvPath = Path.Combine(packageFolder, "validation_breakdown.csv");
        var reportPath = Path.Combine(packageFolder, "customer_validation_report.html");
        var instructionsPath = Path.Combine(packageFolder, "print_to_pdf_instructions.txt");
        var readmePath = Path.Combine(packageFolder, "README.txt");
        var manifestPath = Path.Combine(packageFolder, "validation_manifest.json");
        var annotatedFolder = Path.Combine(packageFolder, "annotated_images");

        File.WriteAllText(csvPath, BatchValidationService.BuildResultsCsv(rows), Encoding.UTF8);
        var breakdownSummary = ClassMetricsService.Calculate(rows);
        var datasetQuality = request.DatasetQualitySummary ?? DatasetQualityService.Analyze(rows, null, request.DatasetQualityCriteria);
        var cameraAcceptance = CameraAcceptanceTestService.ToSummary(AoiDatabase.GetLatestCameraAcceptanceRun(realHardwareOnly: true));
        var robotAcceptance = RobotAcceptanceTestService.ToSummary(AoiDatabase.GetLatestRobotAcceptanceRun());
        var mesReadiness = MesSpoolService.EvaluateReadiness();
        var thresholdProfileEvidence = ThresholdProfileService.GetActiveEvidenceSummary(request.BoardModel, request.BoardModel, "ANY");
        File.WriteAllText(breakdownCsvPath, ClassMetricsService.BuildCsv(breakdownSummary), Encoding.UTF8);
        cancellationToken.ThrowIfCancellationRequested();

        var assetResult = ValidationReportAssetService.ExportSampleAnnotatedImages(
            rows,
            annotatedFolder,
            "annotated_images",
            maxCount: 25,
            cancellationToken);
        var warnings = request.Warnings
            .Concat(assetResult.Warnings)
            .Where(warning => !string.IsNullOrWhiteSpace(warning))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var acceptance = EvaluateAcceptance(
            request.Metrics,
            request.PerformanceSummary,
            rows.Length,
            request.IsFormalManifest,
            criteria,
            datasetQuality);

        var reportContext = new CustomerValidationReportContext
        {
            StationId = request.StationId,
            UserId = request.OperatorId,
            UserRole = request.OperatorRole,
            RunId = request.RunId?.ToString(CultureInfo.InvariantCulture) ?? "Not available",
            TestTimestamp = ToReportTimestamp(request.RunCreatedAtUtc, generatedAtUtc),
            BoardModel = request.BoardModel,
            LotId = request.LotId,
            ModelId = request.ModelId,
            ModelSha256 = request.ModelSha256,
            ModelValidationStatus = request.ModelValidationStatus,
            EngineName = request.EngineName,
            ModelVersion = request.ModelVersion,
            ModelFileName = request.ModelFileName,
            ConfidenceThreshold = request.ConfidenceThreshold,
            DatasetFolder = string.IsNullOrWhiteSpace(request.DatasetFolder) ? "Not available" : request.DatasetFolder,
            GroundTruthFile = string.IsNullOrWhiteSpace(request.GroundTruthCsvPath) ? "Not selected" : request.GroundTruthCsvPath,
            Metrics = request.Metrics,
            PerformanceSummary = request.PerformanceSummary,
            AcceptanceStatus = acceptance.Status,
            AcceptanceMessages = acceptance.Messages,
            AcceptanceCriteria = criteria,
            Rows = rows,
            SampleAnnotatedImages = assetResult.Images,
            Warnings = warnings,
            FalseCallRecommendation = FalseCallReductionService.ToSummary(request.FalseCallReductionRun),
            BreakdownSummary = breakdownSummary,
            DatasetQualitySummary = datasetQuality,
            CameraAcceptanceSummary = cameraAcceptance,
            RobotAcceptanceSummary = robotAcceptance,
            MesReadinessSummary = mesReadiness,
            ThresholdProfileEvidence = thresholdProfileEvidence,
        };

        File.WriteAllText(reportPath, CustomerValidationReportService.BuildHtml(reportContext), Encoding.UTF8);
        File.WriteAllText(instructionsPath, CustomerValidationReportService.BuildPrintToPdfInstructions(reportPath), Encoding.UTF8);
        File.WriteAllText(readmePath, BuildReadme(packageId, acceptance, warnings), Encoding.UTF8);

        var manifest = BuildManifest(request, criteria, acceptance, warnings, generatedAtUtc, packageId, breakdownSummary, datasetQuality, cameraAcceptance, robotAcceptance, mesReadiness, thresholdProfileEvidence);
        manifest.IncludedFiles = EnumerateIncludedFiles(packageFolder).ToList();
        WriteManifest(manifestPath, manifest);
        manifest.IncludedFiles = EnumerateIncludedFiles(packageFolder).ToList();
        WriteManifest(manifestPath, manifest);

        var summary = BuildDatabaseSummary(request.Metrics, request.PerformanceSummary, acceptance);
        AoiDatabase.RecordValidationPackage(
            packageId,
            packageFolder,
            manifestPath,
            acceptance.Status,
            summary,
            request.RunId,
            request.OperatorId);
        ExportVerificationService.RecordVerifiedExport("Stage1ValidationPackage", packageFolder, acceptance.Status, request.OperatorId);

        return new CustomerValidationPackageResult(
            packageId,
            packageFolder,
            manifestPath,
            reportPath,
            csvPath,
            annotatedFolder,
            manifest,
            acceptance,
            warnings);
    }

    private static ValidationPackageManifest BuildManifest(
        CustomerValidationPackageRequest request,
        ValidationAcceptanceCriteria criteria,
        ValidationAcceptanceSummary acceptance,
        IReadOnlyList<string> warnings,
        DateTime generatedAtUtc,
        string packageId,
        ValidationBreakdownSummary breakdownSummary,
        DatasetQualitySummary datasetQuality,
        CameraAcceptanceSummary cameraAcceptance,
        RobotAcceptanceSummary robotAcceptance,
        MesReadinessSummary mesReadiness,
        ThresholdProfileEvidenceSummary thresholdProfileEvidence)
    {
        return new ValidationPackageManifest
        {
            SchemaVersion = ManifestSchemaVersion,
            PackageId = packageId,
            GeneratedAtUtc = generatedAtUtc,
            AppVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0",
            DeploymentProfile = DeploymentProfileSettingsService.Load().ToString(),
            StationId = request.StationId,
            OperatorId = request.OperatorId,
            BoardModel = request.BoardModel,
            LotId = request.LotId,
            ModelId = request.ModelId,
            ModelSha256 = request.ModelSha256,
            ModelValidationStatus = request.ModelValidationStatus,
            ModelVersion = request.ModelVersion,
            EngineName = request.EngineName,
            ActiveConfidenceThreshold = request.ConfidenceThreshold,
            DatasetFolderHashOrName = DatasetName(request.DatasetFolder),
            GroundTruthCsvName = string.IsNullOrWhiteSpace(request.GroundTruthCsvPath) ? "Not selected" : Path.GetFileName(request.GroundTruthCsvPath),
            RunId = request.RunId?.ToString(CultureInfo.InvariantCulture) ?? "Not available",
            MetricSummary = new ValidationMetricSummary
            {
                TotalImages = request.Rows.Count,
                KnownGroundTruthImages = Math.Max(0, request.Rows.Count - request.Metrics.Unknown),
                UnknownGroundTruthImages = request.Metrics.Unknown,
                Accuracy = request.Metrics.Accuracy,
                Precision = request.Metrics.Precision,
                Recall = request.Metrics.Recall,
                FalseCallRate = request.Metrics.FalseCallRate,
                TruePositive = request.Metrics.TruePositive,
                TrueNegative = request.Metrics.TrueNegative,
                FalsePositive = request.Metrics.FalsePositive,
                FalseNegative = request.Metrics.FalseNegative,
                FalseCall = request.Metrics.FalseCall,
                PossibleEscape = request.Metrics.PossibleEscape,
                VerifiedNg = request.Metrics.VerifiedNg,
                OkCount = request.Metrics.OkCount,
                NgCount = request.Metrics.NgCount,
                ReviewCount = request.Metrics.ReviewCount,
            },
            PerformanceSummary = new ValidationPackagePerformanceSummary
            {
                AverageMilliseconds = request.PerformanceSummary.AverageMilliseconds,
                MaxMilliseconds = request.PerformanceSummary.MaxMilliseconds,
                MinMilliseconds = request.PerformanceSummary.MinMilliseconds,
                CountOverOneSecond = request.PerformanceSummary.CountOverOneSecond,
                TimedImageCount = request.PerformanceSummary.TimedImageCount,
            },
            BreakdownSummary = breakdownSummary,
            DatasetQualitySummary = datasetQuality,
            CameraAcceptanceSummary = cameraAcceptance,
            RobotAcceptanceSummary = robotAcceptance,
            MesReadinessSummary = mesReadiness,
            AcceptanceStatus = acceptance.Status,
            Criteria = criteria,
            FalseCallRecommendation = FalseCallReductionService.ToSummary(request.FalseCallReductionRun),
            ThresholdProfileEvidence = thresholdProfileEvidence,
            Warnings = warnings.Concat(acceptance.Messages).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Limitations = CustomerValidationReportContext.DefaultPrototypeLimitations.ToList(),
        };
    }

    private static IEnumerable<ValidationIncludedFile> EnumerateIncludedFiles(string packageFolder)
    {
        return Directory.EnumerateFiles(packageFolder, "*", SearchOption.AllDirectories)
            .OrderBy(path => Path.GetRelativePath(packageFolder, path), StringComparer.OrdinalIgnoreCase)
            .Select(path => new ValidationIncludedFile
            {
                RelativePath = Path.GetRelativePath(packageFolder, path).Replace('\\', '/'),
                FileType = ClassifyFile(packageFolder, path),
                Bytes = new FileInfo(path).Length,
            });
    }

    private static string ClassifyFile(string packageFolder, string path)
    {
        var relative = Path.GetRelativePath(packageFolder, path).Replace('\\', '/');
        var fileName = Path.GetFileName(path);
        if (string.Equals(fileName, "validation_manifest.json", StringComparison.OrdinalIgnoreCase))
            return "Manifest";
        if (string.Equals(fileName, "validation_results.csv", StringComparison.OrdinalIgnoreCase))
            return "CSV results";
        if (string.Equals(fileName, "validation_breakdown.csv", StringComparison.OrdinalIgnoreCase))
            return "CSV validation breakdown";
        if (string.Equals(fileName, "customer_validation_report.html", StringComparison.OrdinalIgnoreCase))
            return "HTML report";
        if (string.Equals(fileName, "print_to_pdf_instructions.txt", StringComparison.OrdinalIgnoreCase))
            return "Print-to-PDF instructions";
        if (string.Equals(fileName, "README.txt", StringComparison.OrdinalIgnoreCase))
            return "README";
        if (relative.StartsWith("annotated_images/", StringComparison.OrdinalIgnoreCase))
            return "Annotated image";
        return "Package evidence";
    }

    private static void WriteManifest(string manifestPath, ValidationPackageManifest manifest)
    {
        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        File.WriteAllText(manifestPath, json, Encoding.UTF8);
    }

    private static string BuildReadme(
        string packageId,
        ValidationAcceptanceSummary acceptance,
        IReadOnlyList<string> warnings)
    {
        var warningText = warnings.Count == 0
            ? "No export warnings were recorded."
            : string.Join(Environment.NewLine, warnings.Select(warning => $"- {warning}"));

        return $"""
        AOI Monitor Stage 1 Customer Validation Package

        Package ID: {packageId}
        Acceptance status: {acceptance.Status}

        Contents:
        - validation_manifest.json: versioned manifest and acceptance summary.
        - validation_results.csv: per-image validation results exported from the batch run.
        - validation_breakdown.csv: per-class, per-side, and per-ROI validation breakdown.
        - customer_validation_report.html: browser-readable customer validation report.
        - print_to_pdf_instructions.txt: browser print-to-PDF workflow.
        - annotated_images/: generated overlays only. Raw source customer datasets are not copied into this package.

        How to inspect:
        1. Open validation_manifest.json and confirm packageId, runId, acceptanceStatus, criteria, and includedFiles.
        2. Open customer_validation_report.html to review metrics, acceptance notes, limitations, and sample overlays.
        3. Open validation_results.csv to audit row-level results against the ground-truth manifest.

        Prototype limitations:
        {string.Join(Environment.NewLine, CustomerValidationReportContext.DefaultPrototypeLimitations.Select(item => $"- {item}"))}

        Warnings:
        {warningText}

        """;
    }

    private static string BuildDatabaseSummary(
        BatchMetrics metrics,
        BatchPerformanceSummary performance,
        ValidationAcceptanceSummary acceptance)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"status={acceptance.Status}; accuracy={metrics.Accuracy:P1}; precision={metrics.Precision:P1}; recall={metrics.Recall:P1}; falseCallRate={metrics.FalseCallRate:P1}; overOneSecond={performance.CountOverOneSecond}");
    }

    private static DateTime ToReportTimestamp(DateTime? runCreatedAtUtc, DateTime generatedAtUtc)
    {
        var timestamp = runCreatedAtUtc ?? generatedAtUtc;
        return timestamp.Kind == DateTimeKind.Utc ? timestamp.ToLocalTime() : timestamp;
    }

    private static string BuildPackageId(DateTime generatedAtUtc)
        => $"STAGE1-{generatedAtUtc:yyyyMMddHHmmssfff}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}";

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

    private static string DatasetName(string datasetFolder)
    {
        if (string.IsNullOrWhiteSpace(datasetFolder))
            return "Not provided";

        try
        {
            var trimmed = datasetFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var name = Path.GetFileName(trimmed);
            return string.IsNullOrWhiteSpace(name) ? trimmed : name;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "Unusable dataset path";
        }
    }

    private static string FormatPercent(double value)
        => value.ToString("P1", CultureInfo.InvariantCulture);
}
