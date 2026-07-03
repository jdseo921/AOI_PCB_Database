using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AOI_Monitor.Data;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public static class ImageOnlyLearningReportService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static ImageOnlyLearningReportResult ExportReport(
        string projectId,
        string modelId,
        ImageOnlyLearningReportOptions? options = null,
        string operatorId = "UNKNOWN")
    {
        var project = AoiDatabase.GetImageLearningProject(projectId)
            ?? throw new InvalidOperationException("Image-only learning project does not exist.");
        var model = AoiDatabase.GetLearnedPcbVisualModel(modelId)
            ?? throw new InvalidOperationException("Learned PCB visual model metadata does not exist.");
        if (!string.Equals(project.ProjectId, model.ProjectId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The learned visual model does not belong to the selected image-learning project.");

        var normalizedOptions = options ?? new ImageOnlyLearningReportOptions();
        var normalizedOperator = string.IsNullOrWhiteSpace(operatorId) ? "UNKNOWN" : operatorId.Trim();
        var createdAtUtc = DateTime.UtcNow;
        var outputFolder = CreateOutputFolder(project.ProjectId);
        var assetsFolder = Path.Combine(outputFolder, "assets");
        Directory.CreateDirectory(assetsFolder);

        var warnings = new List<string>();
        var counts = ImageLearningProjectService.GetImageCountsByRole(project.ProjectId);
        var artifacts = CopyLearningArtifacts(model, assetsFolder, warnings);
        var comparison = RunFalseCallComparison(project, model, normalizedOptions, normalizedOperator, warnings);
        var visualEvidence = RunVisualEvidenceExport(project, model, normalizedOptions, normalizedOperator, warnings);
        var okExamples = CopyOkValidationExamples(project.ProjectId, assetsFolder, normalizedOptions, warnings);
        var anomalyExamples = CopyAnomalyExamples(visualEvidence, assetsFolder, normalizedOptions, warnings);
        var beforeAfter = CopyBeforeAfterOverlay(visualEvidence, assetsFolder, warnings);

        AddEvidenceBoundaryWarnings(project, counts, okExamples, anomalyExamples, beforeAfter, warnings);

        var htmlPath = Path.Combine(outputFolder, "visual_learning_report.html");
        var jsonPath = Path.Combine(outputFolder, "visual_learning_report.json");
        var pdfPath = normalizedOptions.GeneratePdf ? Path.Combine(outputFolder, "visual_learning_report.pdf") : string.Empty;
        var metrics = comparison?.Metrics;
        var payload = BuildJsonPayload(
            project,
            model,
            createdAtUtc,
            counts,
            artifacts,
            comparison,
            visualEvidence,
            okExamples,
            anomalyExamples,
            beforeAfter,
            warnings);

        File.WriteAllText(jsonPath, JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8);
        File.WriteAllText(
            htmlPath,
            BuildHtmlReport(
                project,
                model,
                createdAtUtc,
                counts,
                artifacts,
                metrics,
                okExamples,
                anomalyExamples,
                beforeAfter,
                warnings,
                outputFolder),
            Encoding.UTF8);

        if (normalizedOptions.GeneratePdf)
            PdfExportService.ExportHtmlFileToPdf(htmlPath, pdfPath, "Visual Learning Report");

        var verified = ExportVerificationService.RecordVerifiedExport(
            "ImageOnlyLearningVisualReport",
            outputFolder,
            warnings.Count == 0 ? "OK" : "WARN",
            normalizedOperator);
        AoiDatabase.RecordAuditEvent(
            "IMAGE_ONLY_LEARNING_REPORT_EXPORT",
            $"Image-only visual learning report exported: project={project.ProjectId}; model={model.ModelId}; warnings={warnings.Count}.",
            operatorWithRole: normalizedOperator,
            relatedEntityType: "ImageLearningProject",
            relatedEntityId: project.ProjectId,
            relatedPath: outputFolder);

        return new ImageOnlyLearningReportResult(
            project.ProjectId,
            model.ModelId,
            createdAtUtc,
            outputFolder,
            htmlPath,
            jsonPath,
            pdfPath,
            comparison?.HtmlReportPath ?? string.Empty,
            visualEvidence?.ManifestPath ?? string.Empty,
            okExamples.Select(example => example.OutputPath).ToArray(),
            anomalyExamples.Select(example => example.AnnotatedOverlay.OutputPath).Where(path => !string.IsNullOrWhiteSpace(path)).ToArray(),
            beforeAfter?.OutputPath ?? string.Empty,
            warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            verified.ExportHistoryId);
    }

    private static ImageLearningFalseCallComparisonResult? RunFalseCallComparison(
        ImageLearningProject project,
        LearnedPcbVisualModel model,
        ImageOnlyLearningReportOptions options,
        string operatorId,
        List<string> warnings)
    {
        try
        {
            return ImageLearningFalseCallComparisonService.RunComparison(
                project.ProjectId,
                model.ModelId,
                options.LearningOptions,
                operatorId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or InvalidOperationException)
        {
            warnings.Add($"Before/after false-call comparison was not available: {ex.Message}");
            return null;
        }
    }

    private static ImageLearningOverlayExportResult? RunVisualEvidenceExport(
        ImageLearningProject project,
        LearnedPcbVisualModel model,
        ImageOnlyLearningReportOptions options,
        string operatorId,
        List<string> warnings)
    {
        try
        {
            return ImageLearningOverlayExportService.ExportProjectVisualEvidence(
                project.ProjectId,
                model.ModelId,
                new ImageLearningOverlayExportOptions
                {
                    LearningOptions = options.LearningOptions,
                    MaxBatchExamples = Math.Max(10, options.AnomalyExampleCount),
                },
                operatorId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or InvalidOperationException)
        {
            warnings.Add($"Visual evidence overlays were not available: {ex.Message}");
            return null;
        }
    }

    private static LearningArtifactAssets CopyLearningArtifacts(
        LearnedPcbVisualModel model,
        string assetsFolder,
        List<string> warnings)
    {
        var reference = CopyAsset(
            FindArtifactPath(model, "learned_reference.png"),
            assetsFolder,
            "learned_reference.png",
            "Learned reference image",
            warnings);
        var tolerance = CopyAsset(
            FindArtifactPath(model, "tolerance_map.png"),
            assetsFolder,
            "tolerance_map.png",
            "Learned tolerance map",
            warnings);
        var threshold = CopyAsset(
            FindArtifactPath(model, "anomaly_threshold_map.png"),
            assetsFolder,
            "anomaly_threshold_map.png",
            "Anomaly threshold map",
            warnings);
        return new LearningArtifactAssets(reference, tolerance, threshold);
    }

    private static IReadOnlyList<ReportImageAsset> CopyOkValidationExamples(
        string projectId,
        string assetsFolder,
        ImageOnlyLearningReportOptions options,
        List<string> warnings)
    {
        var count = Math.Clamp(options.OkValidationExampleCount, 1, 12);
        return ImageLearningProjectService.GetProjectImages(projectId, ImageLearningImageRole.OkValidation)
            .OrderBy(image => image.FileName, StringComparer.OrdinalIgnoreCase)
            .Take(count)
            .Select((image, index) => CopyAsset(
                ResolveImagePath(image),
                assetsFolder,
                $"ok_validation_example_{index + 1:00}{SafeExtension(ResolveImagePath(image))}",
                $"OK Validation example: {image.FileName}",
                warnings,
                image.FileName,
                image.Role))
            .Where(asset => !string.IsNullOrWhiteSpace(asset.OutputPath))
            .ToArray();
    }

    private static IReadOnlyList<AnomalyReportExample> CopyAnomalyExamples(
        ImageLearningOverlayExportResult? visualEvidence,
        string assetsFolder,
        ImageOnlyLearningReportOptions options,
        List<string> warnings)
    {
        if (visualEvidence is null)
            return Array.Empty<AnomalyReportExample>();

        var count = Math.Clamp(options.AnomalyExampleCount, 1, 12);
        return visualEvidence.Items
            .Where(item => File.Exists(item.AnnotatedOverlayPath))
            .OrderByDescending(item => item.AnomalyScore)
            .ThenBy(item => item.FileName, StringComparer.OrdinalIgnoreCase)
            .Take(count)
            .Select((item, index) =>
            {
                var baseName = $"anomaly_example_{index + 1:00}_{SafePathPart(Path.GetFileNameWithoutExtension(item.FileName))}";
                var original = CopyAsset(
                    item.OriginalPath,
                    assetsFolder,
                    $"{baseName}_original.png",
                    $"Original inspection image: {item.FileName}",
                    warnings,
                    item.FileName,
                    item.Role);
                var heatmap = CopyAsset(
                    item.HeatmapPath,
                    assetsFolder,
                    $"{baseName}_heatmap.png",
                    $"Learned anomaly heatmap: {item.FileName}",
                    warnings,
                    item.FileName,
                    item.Role);
                var overlay = CopyAsset(
                    item.AnnotatedOverlayPath,
                    assetsFolder,
                    $"{baseName}_overlay.png",
                    $"Learned visual model overlay: {item.FileName}",
                    warnings,
                    item.FileName,
                    item.Role);
                var beforeAfter = CopyAsset(
                    item.BaselineVsLearnedPath,
                    assetsFolder,
                    $"{baseName}_before_after.png",
                    $"Baseline vs learned overlay: {item.FileName}",
                    warnings,
                    item.FileName,
                    item.Role);
                return new AnomalyReportExample(item, original, heatmap, overlay, beforeAfter);
            })
            .Where(example => !string.IsNullOrWhiteSpace(example.AnnotatedOverlay.OutputPath))
            .ToArray();
    }

    private static ReportImageAsset? CopyBeforeAfterOverlay(
        ImageLearningOverlayExportResult? visualEvidence,
        string assetsFolder,
        List<string> warnings)
    {
        if (visualEvidence is null)
            return null;

        var item = visualEvidence.Items
            .Where(row => File.Exists(row.BaselineVsLearnedPath))
            .OrderByDescending(row => row.AnomalyScore)
            .FirstOrDefault();
        if (item is null)
            return null;

        return CopyAsset(
            item.BaselineVsLearnedPath,
            assetsFolder,
            "before_after_overlay_comparison.png",
            $"Before learning vs learned model overlay: {item.FileName}",
            warnings,
            item.FileName,
            item.Role);
    }

    private static void AddEvidenceBoundaryWarnings(
        ImageLearningProject project,
        IReadOnlyDictionary<ImageLearningImageRole, int> counts,
        IReadOnlyList<ReportImageAsset> okExamples,
        IReadOnlyList<AnomalyReportExample> anomalyExamples,
        ReportImageAsset? beforeAfter,
        List<string> warnings)
    {
        if (counts.GetValueOrDefault(ImageLearningImageRole.NgValidation) == 0)
            warnings.Add("No NG Validation images were provided; missed-defect rate cannot be fully proven.");
        if (project.EvidenceMode != ImageLearningEvidenceMode.CustomerData)
            warnings.Add("Synthetic/internal demo data is not customer acceptance evidence.");
        if (okExamples.Count < 3)
            warnings.Add($"Only {okExamples.Count} OK Validation example image(s) were available for the report.");
        if (anomalyExamples.Count < 3)
            warnings.Add($"Only {anomalyExamples.Count} inspection/anomaly example overlay(s) were available for the report.");
        if (beforeAfter is null || string.IsNullOrWhiteSpace(beforeAfter.OutputPath))
            warnings.Add("No before/after overlay comparison image was available for the report.");

        warnings.Add("Stage 2 live camera validation remains separate from this image-only Stage 1 evidence.");
        warnings.Add("Formal acceptance requires a customer/evaluator dataset and reviewer signoff.");
    }

    private static object BuildJsonPayload(
        ImageLearningProject project,
        LearnedPcbVisualModel model,
        DateTime createdAtUtc,
        IReadOnlyDictionary<ImageLearningImageRole, int> counts,
        LearningArtifactAssets artifacts,
        ImageLearningFalseCallComparisonResult? comparison,
        ImageLearningOverlayExportResult? visualEvidence,
        IReadOnlyList<ReportImageAsset> okExamples,
        IReadOnlyList<AnomalyReportExample> anomalyExamples,
        ReportImageAsset? beforeAfter,
        IReadOnlyList<string> warnings)
    {
        return new
        {
            schemaVersion = "image-only-learning-visual-report.v1",
            generatedAtUtc = createdAtUtc,
            reportFiles = new
            {
                html = "visual_learning_report.html",
                json = "visual_learning_report.json",
                pdf = "visual_learning_report.pdf",
            },
            clientFriendlySummary = new[]
            {
                "The program learned normal PCB appearance.",
                "The program learned normal variation.",
                "The program ignored harmless variation.",
                "The program flagged unusual regions.",
            },
            project = new
            {
                project.ProjectId,
                project.ProjectName,
                project.BoardModel,
                project.EvidenceMode,
            },
            model = new
            {
                model.ModelId,
                model.ModelVersion,
                model.CreatedAtUtc,
                model.GoldenCount,
                model.OkLearningCount,
                model.OkValidationCount,
                model.InputWidth,
                model.InputHeight,
                model.AlignmentMode,
                model.BrightnessNormalizationMode,
                model.LearnedThreshold,
                model.FalseCallTarget,
                model.FalseCallRate,
                model.PossibleEscapeRate,
            },
            imageCounts = new
            {
                goldenReference = counts.GetValueOrDefault(ImageLearningImageRole.GoldenReference),
                okLearning = counts.GetValueOrDefault(ImageLearningImageRole.OkLearning),
                okValidation = counts.GetValueOrDefault(ImageLearningImageRole.OkValidation),
                inspection = counts.GetValueOrDefault(ImageLearningImageRole.Inspection),
                ngValidation = counts.GetValueOrDefault(ImageLearningImageRole.NgValidation),
            },
            artifacts = new
            {
                learnedReference = ToJsonAsset(artifacts.LearnedReference),
                toleranceMap = ToJsonAsset(artifacts.ToleranceMap),
                anomalyThresholdMap = ToJsonAsset(artifacts.AnomalyThresholdMap),
            },
            falseCallComparison = comparison is null
                ? null
                : new
                {
                    comparison.HtmlReportPath,
                    comparison.JsonReportPath,
                    comparison.ResultsCsvPath,
                    comparison.ThresholdSweepCsvPath,
                    comparison.Metrics,
                },
            visualEvidence = visualEvidence is null
                ? null
                : new
                {
                    visualEvidence.ManifestPath,
                    visualEvidence.OutputFolder,
                    itemCount = visualEvidence.Items.Count,
                    overlayPaths = visualEvidence.Items
                        .Select(item => item.AnnotatedOverlayPath)
                        .Where(path => !string.IsNullOrWhiteSpace(path))
                        .ToArray(),
                },
            examples = new
            {
                okValidation = okExamples.Select(ToJsonAsset).ToArray(),
                anomaly = anomalyExamples.Select(example => new
                {
                    source = new
                    {
                        example.Source.FileName,
                        example.Source.Role,
                        example.Source.Verdict,
                        example.Source.AnomalyScore,
                        example.Source.RegionCount,
                    },
                    original = ToJsonAsset(example.Original),
                    heatmap = ToJsonAsset(example.Heatmap),
                    annotatedOverlay = ToJsonAsset(example.AnnotatedOverlay),
                    beforeAfterOverlay = ToJsonAsset(example.BeforeAfterOverlay),
                }).ToArray(),
                beforeAfterOverlayComparison = beforeAfter is null ? null : ToJsonAsset(beforeAfter),
            },
            possibleEscapeStatus = counts.GetValueOrDefault(ImageLearningImageRole.NgValidation) > 0
                ? $"Possible escapes: {comparison?.Metrics.PossibleEscapes.ToString(CultureInfo.InvariantCulture) ?? "not measured"}."
                : "No NG Validation images were provided; missed-defect rate cannot be fully proven.",
            recommendedThreshold = comparison?.Metrics.RecommendedThreshold ?? model.LearnedThreshold,
            evidenceBoundary = new[]
            {
                "Image-only Stage 1 learning.",
                "Not live camera validation.",
                "Synthetic/internal demo data is not customer acceptance unless replaced with customer data.",
                "Stage 2 live camera validation remains separate.",
                "Formal acceptance requires customer/evaluator dataset.",
            },
            nextSteps = new[]
            {
                "Run the customer/evaluator dataset.",
                "Connect and validate real camera and lighting hardware during Stage 2.",
            },
            warnings = warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
        };
    }

    private static string BuildHtmlReport(
        ImageLearningProject project,
        LearnedPcbVisualModel model,
        DateTime createdAtUtc,
        IReadOnlyDictionary<ImageLearningImageRole, int> counts,
        LearningArtifactAssets artifacts,
        ImageLearningFalseCallComparisonMetrics? metrics,
        IReadOnlyList<ReportImageAsset> okExamples,
        IReadOnlyList<AnomalyReportExample> anomalyExamples,
        ReportImageAsset? beforeAfter,
        IReadOnlyList<string> warnings,
        string outputFolder)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html><html><head><meta charset=\"utf-8\"><title>Visual Learning Report</title>");
        sb.AppendLine("<style>");
        sb.AppendLine("body{font-family:Segoe UI,Arial,sans-serif;background:#0b0e10;color:#e8eef2;margin:24px;line-height:1.45}.card{border:1px solid #3e474e;background:#151a1e;padding:14px;margin:12px 0}.boundary{border-color:#8f5fd1;background:#2a1740}.warn{border-color:#987538;background:#372914}.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(260px,1fr));gap:12px}table{border-collapse:collapse;width:100%;margin-top:8px}th,td{border:1px solid #3e474e;padding:8px;text-align:left;vertical-align:top}th{background:#1b2024}img{max-width:100%;border:1px solid #3e474e;background:#050607}.metric{font-size:24px;font-weight:700}.muted{color:#a8b2b9}.caption{font-weight:700;margin:6px 0}.small{font-size:13px;color:#a8b2b9;word-break:break-all}");
        sb.AppendLine("</style></head><body>");
        sb.AppendLine("<h1>Visual Learning Report</h1>");
        sb.AppendLine($"<p class=\"muted\">Generated UTC: {Html(createdAtUtc.ToString("O", CultureInfo.InvariantCulture))}</p>");
        sb.AppendLine("<div class=\"card boundary\"><strong>Image-only Stage 1 learning.</strong> Not live camera validation. Formal acceptance requires customer/evaluator dataset.</div>");

        sb.AppendLine("<h2>Executive Summary</h2>");
        sb.AppendLine("<div class=\"card\"><p>The program learned normal PCB appearance. The program learned normal variation. The program ignored harmless variation. The program flagged unusual regions.</p>");
        sb.AppendLine($"<p>Project: <strong>{Html(project.ProjectName)}</strong> / {Html(project.ProjectId)}<br>Board model: {Html(project.BoardModel)}<br>Model: {Html(model.ModelId)} ({Html(model.ModelVersion)})</p></div>");

        sb.AppendLine("<h2>What Images Were Used</h2>");
        sb.AppendLine("<table><tr><th>Image group</th><th>Count</th><th>Purpose</th></tr>");
        ImageCountRow("Golden / Reference", counts.GetValueOrDefault(ImageLearningImageRole.GoldenReference), "Best known reference boards.");
        ImageCountRow("OK Learning", counts.GetValueOrDefault(ImageLearningImageRole.OkLearning), "Good boards used to learn normal appearance and variation.");
        ImageCountRow("OK Validation", counts.GetValueOrDefault(ImageLearningImageRole.OkValidation), "Good boards used to calibrate false calls.");
        ImageCountRow("Inspection", counts.GetValueOrDefault(ImageLearningImageRole.Inspection), "Samples inspected after learning.");
        ImageCountRow("Optional NG Validation", counts.GetValueOrDefault(ImageLearningImageRole.NgValidation), "Known-bad boards used only to estimate possible escapes.");
        sb.AppendLine("</table>");

        sb.AppendLine("<h2>Learned Visual Model</h2>");
        sb.AppendLine("<div class=\"grid\">");
        ImageCard("Learned reference image", artifacts.LearnedReference, outputFolder, sb);
        ImageCard("Learned tolerance map", artifacts.ToleranceMap, outputFolder, sb);
        ImageCard("Anomaly threshold map", artifacts.AnomalyThresholdMap, outputFolder, sb);
        sb.AppendLine("</div>");

        sb.AppendLine("<h2>Before Learning: Baseline False Calls</h2>");
        sb.AppendLine("<div class=\"grid\">");
        MetricCard("False calls before learning", metrics?.FalseCallsBeforeLearning.ToString(CultureInfo.InvariantCulture) ?? "Not measured", "Pixel Difference Prototype Engine on OK Validation images.");
        MetricCard(
            "False-call rate before",
            metrics is null
                ? "Not measured"
                : metrics.OkValidationCount >= ImageLearningFalseCallComparisonService.MinimumOkValidationForRate
                    ? metrics.FalseCallRateBeforeLearning.ToString("P1", CultureInfo.InvariantCulture)
                    : $"{metrics.FalseCallsBeforeLearning} of {metrics.OkValidationCount} (% withheld below {ImageLearningFalseCallComparisonService.MinimumOkValidationForRate})",
            "Rate on OK Validation images; percentage shown only at or above the minimum sample size.");
        MetricCard("Anomaly regions before", metrics?.AnomalyRegionsBeforeLearning.ToString(CultureInfo.InvariantCulture) ?? "Not measured", "Baseline review burden.");
        sb.AppendLine("</div>");

        sb.AppendLine("<h2>After Learning: Learned Model False Calls</h2>");
        sb.AppendLine("<div class=\"grid\">");
        MetricCard("False calls after learning", metrics?.FalseCallsAfterLearning.ToString(CultureInfo.InvariantCulture) ?? "Not measured", "Learned PCB Visual Model v1 on OK Validation images.");
        MetricCard(
            "False-call rate after",
            metrics is null
                ? "Not measured"
                : metrics.OkValidationCount >= ImageLearningFalseCallComparisonService.MinimumOkValidationForRate
                    ? metrics.FalseCallRateAfterLearning.ToString("P1", CultureInfo.InvariantCulture)
                    : $"{metrics.FalseCallsAfterLearning} of {metrics.OkValidationCount} (% withheld below {ImageLearningFalseCallComparisonService.MinimumOkValidationForRate})",
            "Calibrated false-call behavior; percentage shown only at or above the minimum sample size.");
        MetricCard("False-call reduction", metrics?.FalseCallReductionPercentage.ToString("P1", CultureInfo.InvariantCulture) ?? "Not measured", "Reduction compared with baseline.");
        sb.AppendLine("</div>");

        sb.AppendLine("<h2>False-call Reduction</h2>");
        sb.AppendLine("<div class=\"card\">");
        if (metrics is null)
        {
            sb.AppendLine("<p>Before/after false-call comparison has not been measured for this report.</p>");
        }
        else
        {
            sb.AppendLine($"<p><strong>False calls reduced from {metrics.FalseCallsBeforeLearning} to {metrics.FalseCallsAfterLearning}.</strong></p>");
            sb.AppendLine($"<p>Review burden changed from {metrics.ReviewBurdenBeforeLearning} image(s) to {metrics.ReviewBurdenAfterLearning} image(s). Recommended threshold: <strong>{FormatThreshold(metrics.RecommendedThreshold ?? model.LearnedThreshold)}</strong>.</p>");
        }

        sb.AppendLine("</div>");

        sb.AppendLine("<h2>OK Validation Examples</h2>");
        sb.AppendLine("<div class=\"grid\">");
        foreach (var example in okExamples)
            ImageCard(example.Caption, example, outputFolder, sb);
        sb.AppendLine("</div>");

        sb.AppendLine("<h2>Detected Anomaly Examples</h2>");
        sb.AppendLine("<div class=\"grid\">");
        foreach (var example in anomalyExamples)
        {
            sb.AppendLine("<div class=\"card\">");
            sb.AppendLine($"<div class=\"caption\">{Html(example.Source.FileName)} - verdict {Html(example.Source.Verdict)} / score {example.Source.AnomalyScore:F2}</div>");
            ImageTag(example.AnnotatedOverlay, outputFolder, sb);
            ImageTag(example.Heatmap, outputFolder, sb);
            sb.AppendLine($"<div class=\"small\">Annotated overlay path: {Html(example.AnnotatedOverlay.OutputPath)}</div>");
            sb.AppendLine("</div>");
        }

        sb.AppendLine("</div>");

        sb.AppendLine("<h2>Before / After Overlay Comparison</h2>");
        if (beforeAfter is not null && !string.IsNullOrWhiteSpace(beforeAfter.OutputPath))
            ImageCard("Baseline vs learned overlay comparison", beforeAfter, outputFolder, sb);
        else
            sb.AppendLine("<div class=\"card warn\">No before/after overlay comparison image was available.</div>");

        sb.AppendLine("<h2>Possible Escape Status</h2>");
        sb.AppendLine("<div class=\"card\">");
        if (counts.GetValueOrDefault(ImageLearningImageRole.NgValidation) == 0)
            sb.AppendLine("<p><strong>No NG Validation images were provided; missed-defect rate cannot be fully proven.</strong></p>");
        else
            sb.AppendLine($"<p>Possible escapes: <strong>{metrics?.PossibleEscapes.ToString(CultureInfo.InvariantCulture) ?? "not measured"}</strong>. Possible escape rate: {metrics?.PossibleEscapeRate?.ToString("P1", CultureInfo.InvariantCulture) ?? "not measured"}.</p>");
        sb.AppendLine("</div>");

        sb.AppendLine("<h2>Recommended Threshold</h2>");
        sb.AppendLine($"<div class=\"card\"><p>Recommended threshold: <strong>{FormatThreshold(metrics?.RecommendedThreshold ?? model.LearnedThreshold)}</strong>.</p><p class=\"muted\">The threshold is selected to reduce false calls while preserving NG Validation guardrails when known-bad images are available.</p></div>");

        sb.AppendLine("<h2>Evidence Boundary and Limitations</h2>");
        sb.AppendLine("<div class=\"card warn\"><ul>");
        foreach (var warning in warnings.Distinct(StringComparer.OrdinalIgnoreCase))
            sb.AppendLine($"<li>{Html(warning)}</li>");
        sb.AppendLine("</ul></div>");

        sb.AppendLine("<h2>Next Step</h2>");
        sb.AppendLine("<div class=\"card\"><p>Next step: run customer dataset / connect real camera hardware.</p><p>Stage 2 live camera validation remains separate from this image-only report.</p></div>");
        sb.AppendLine("</body></html>");
        return sb.ToString();

        void ImageCountRow(string label, int count, string purpose)
            => sb.AppendLine($"<tr><td>{Html(label)}</td><td>{count.ToString(CultureInfo.InvariantCulture)}</td><td>{Html(purpose)}</td></tr>");

        void MetricCard(string label, string value, string detail)
            => sb.AppendLine($"<div class=\"card\"><div class=\"muted\">{Html(label)}</div><div class=\"metric\">{Html(value)}</div><div>{Html(detail)}</div></div>");
    }

    private static void ImageCard(string title, ReportImageAsset asset, string outputFolder, StringBuilder sb)
    {
        sb.AppendLine("<div class=\"card\">");
        sb.AppendLine($"<div class=\"caption\">{Html(title)}</div>");
        ImageTag(asset, outputFolder, sb);
        sb.AppendLine($"<div class=\"small\">{Html(asset.OutputPath)}</div>");
        sb.AppendLine("</div>");
    }

    private static void ImageTag(ReportImageAsset asset, string outputFolder, StringBuilder sb)
    {
        if (string.IsNullOrWhiteSpace(asset.OutputPath) || !File.Exists(asset.OutputPath))
        {
            sb.AppendLine("<div class=\"warn card\">Image not available.</div>");
            return;
        }

        var relative = Path.GetRelativePath(outputFolder, asset.OutputPath).Replace('\\', '/');
        sb.AppendLine($"<img src=\"{Html(relative)}\" alt=\"{Html(asset.Caption)}\">");
    }

    private static ReportImageAsset CopyAsset(
        string? sourcePath,
        string assetsFolder,
        string fileName,
        string caption,
        List<string> warnings,
        string sourceFileName = "",
        ImageLearningImageRole? role = null)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            warnings.Add($"Report image was unavailable: {caption}.");
            return new ReportImageAsset(sourcePath ?? string.Empty, string.Empty, string.Empty, caption, sourceFileName, role);
        }

        Directory.CreateDirectory(assetsFolder);
        var safeName = SafePathPart(fileName);
        var outputPath = Path.Combine(assetsFolder, safeName);
        File.Copy(sourcePath, outputPath, overwrite: true);
        return new ReportImageAsset(
            sourcePath,
            outputPath,
            Path.Combine("assets", safeName).Replace('\\', '/'),
            caption,
            string.IsNullOrWhiteSpace(sourceFileName) ? Path.GetFileName(sourcePath) : sourceFileName,
            role);
    }

    private static string? FindArtifactPath(LearnedPcbVisualModel model, string artifactName)
        => model.Artifacts.FirstOrDefault(
            artifact => string.Equals(artifact.ArtifactName, artifactName, StringComparison.OrdinalIgnoreCase))?.ArtifactPath;

    private static string ResolveImagePath(ImageLearningProjectImage image)
        => !string.IsNullOrWhiteSpace(image.VaultPath) && File.Exists(image.VaultPath)
            ? image.VaultPath
            : image.OriginalPath;

    private static string CreateOutputFolder(string projectId)
    {
        var folder = Path.Combine(
            AoiDatabase.StorageRoot,
            "exports",
            "image_only_learning_visual_reports",
            SafePathPart(projectId),
            DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string SafeExtension(string path)
    {
        var extension = Path.GetExtension(path);
        return string.IsNullOrWhiteSpace(extension) ? ".png" : extension.ToLowerInvariant();
    }

    private static string SafePathPart(string value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "image_learning_report" : value.Trim();
        foreach (var ch in Path.GetInvalidFileNameChars().Concat(Path.GetInvalidPathChars()).Distinct())
            text = text.Replace(ch, '_');

        return text.Replace(' ', '_');
    }

    private static string FormatThreshold(double value)
        => value.ToString("F2", CultureInfo.InvariantCulture);

    private static string Html(string value)
        => WebUtility.HtmlEncode(value ?? string.Empty);

    private static object ToJsonAsset(ReportImageAsset asset)
        => new
        {
            asset.Caption,
            asset.SourceFileName,
            asset.Role,
            asset.SourcePath,
            asset.OutputPath,
            asset.RelativePath,
        };

    private sealed record LearningArtifactAssets(
        ReportImageAsset LearnedReference,
        ReportImageAsset ToleranceMap,
        ReportImageAsset AnomalyThresholdMap);

    private sealed record ReportImageAsset(
        string SourcePath,
        string OutputPath,
        string RelativePath,
        string Caption,
        string SourceFileName,
        ImageLearningImageRole? Role);

    private sealed record AnomalyReportExample(
        ImageLearningOverlayExportItem Source,
        ReportImageAsset Original,
        ReportImageAsset Heatmap,
        ReportImageAsset AnnotatedOverlay,
        ReportImageAsset BeforeAfterOverlay);
}
