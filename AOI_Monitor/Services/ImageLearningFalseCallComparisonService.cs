using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AOI_Monitor.Data;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public static class ImageLearningFalseCallComparisonService
{
    private const double BaselineReviewThreshold = 5.0;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static ImageLearningFalseCallComparisonResult RunComparison(
        string projectId,
        string modelId,
        ImageOnlyPcbLearningOptions? options = null,
        string operatorId = "UNKNOWN")
    {
        var project = AoiDatabase.GetImageLearningProject(projectId)
            ?? throw new InvalidOperationException("Image-only learning project does not exist.");
        var model = AoiDatabase.GetLearnedPcbVisualModel(modelId)
            ?? throw new InvalidOperationException("Learned PCB visual model metadata does not exist.");
        if (!string.Equals(project.ProjectId, model.ProjectId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The learned visual model does not belong to the selected image-learning project.");

        var normalizedOperator = string.IsNullOrWhiteSpace(operatorId) ? "UNKNOWN" : operatorId.Trim();
        var normalizedOptions = options ?? new ImageOnlyPcbLearningOptions
        {
            InputWidth = model.InputWidth,
            InputHeight = model.InputHeight,
            FalseCallTarget = model.FalseCallTarget,
        };
        normalizedOptions.FalseCallTarget = model.FalseCallTarget > 0 ? model.FalseCallTarget : normalizedOptions.FalseCallTarget;

        var warnings = new List<string>();
        var referenceImage = SelectReferenceImage(project.ProjectId)
            ?? throw new InvalidOperationException("False-call comparison requires one Golden / Reference image or one OK Learning image as the baseline reference.");
        var referencePath = ResolveImagePath(referenceImage);
        if (string.IsNullOrWhiteSpace(referencePath) || !File.Exists(referencePath))
            throw new InvalidOperationException("False-call comparison baseline reference image is missing from the managed vault.");

        var targetImages = GetTargetImages(project.ProjectId);
        if (targetImages.Count == 0)
            warnings.Add("No OK Validation, Inspection, or NG Validation images were available for before/after comparison.");
        if (targetImages.All(image => image.Role != ImageLearningImageRole.OkValidation))
            warnings.Add("No OK Validation images were available; false-call reduction cannot be measured yet.");
        var okValidationImageCount = targetImages.Count(image => image.Role == ImageLearningImageRole.OkValidation);
        if (okValidationImageCount > 0 && okValidationImageCount < MinimumOkValidationForRate)
            warnings.Add($"Only {okValidationImageCount} OK Validation image(s) were available (minimum {MinimumOkValidationForRate} recommended). The false-call percentage is not statistically meaningful; rely on the measured false-call count instead of the rate.");
        if (targetImages.All(image => image.Role != ImageLearningImageRole.NgValidation))
            warnings.Add("No NG Validation images were provided; missed-defect rate cannot yet be proven.");

        var baselineEngine = new PixelDifferenceInspectionEngine();
        var imageResults = new List<ImageLearningFalseCallImageResult>();
        foreach (var image in targetImages)
        {
            var imagePath = ResolveImagePath(image);
            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            {
                warnings.Add($"Skipped missing comparison image: {image.FileName}.");
                continue;
            }

            try
            {
                var baseline = baselineEngine.Analyze(imagePath, referencePath, DetectionPriority.MaximizeDefectRecall);
                var learned = ImageOnlyPcbLearningService.InspectImagePath(
                    model.ModelId,
                    imagePath,
                    normalizedOptions,
                    normalizedOperator,
                    image,
                    persist: false);
                imageResults.Add(BuildImageResult(image, imagePath, baseline, learned));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or InvalidOperationException)
            {
                warnings.Add($"Skipped comparison image {image.FileName}: {ex.Message}");
            }
        }

        var sweep = BuildThresholdSweep(imageResults, normalizedOptions, model);
        var recommended = SelectRecommendedThreshold(sweep, normalizedOptions, model, imageResults.Any(row => row.Role == ImageLearningImageRole.NgValidation));
        if (recommended is null && imageResults.Any(row => row.Role == ImageLearningImageRole.NgValidation))
            warnings.Add("No threshold was recommended because every candidate exceeded the allowed possible-escape limit on NG Validation images.");

        var sweepWithRecommendation = sweep
            .Select(row => row with { IsRecommended = recommended is not null && Math.Abs(row.Threshold - recommended.Threshold) < 0.000001 })
            .ToArray();
        var metrics = BuildMetrics(imageResults, sweepWithRecommendation, recommended, normalizedOptions);
        var outputFolder = CreateOutputFolder(project.ProjectId);
        var paths = WriteReports(project, model, metrics, imageResults, sweepWithRecommendation, warnings, outputFolder, referenceImage);

        AoiDatabase.RecordAuditEvent(
            "IMAGE_LEARNING_FALSE_CALL_COMPARISON",
            $"Image-only false-call comparison exported: project={project.ProjectId}; model={model.ModelId}; before={metrics.FalseCallsBeforeLearning}; after={metrics.FalseCallsAfterLearning}; possibleEscapes={metrics.PossibleEscapes}.",
            operatorWithRole: normalizedOperator,
            relatedEntityType: "ImageLearningProject",
            relatedEntityId: project.ProjectId,
            relatedPath: outputFolder);
        // Auditors read this status: a comparison with skipped images or no safe threshold
        // is recorded as WARN, matching the overlay exporter's convention.
        ImageLearningProjectService.RecordReportExport(project.ProjectId, paths.HtmlReportPath, warnings.Count == 0 ? "OK" : "WARN", normalizedOperator);

        return new ImageLearningFalseCallComparisonResult(
            project.ProjectId,
            model.ModelId,
            DateTime.UtcNow,
            metrics,
            imageResults,
            sweepWithRecommendation,
            warnings,
            outputFolder,
            paths.HtmlReportPath,
            paths.JsonReportPath,
            paths.ResultsCsvPath,
            paths.ThresholdSweepCsvPath);
    }

    private static ImageLearningProjectImage? SelectReferenceImage(string projectId)
    {
        var golden = ImageLearningProjectService.GetProjectImages(projectId, ImageLearningImageRole.GoldenReference);
        if (golden.Count > 0)
            return golden[0];

        var okLearning = ImageLearningProjectService.GetProjectImages(projectId, ImageLearningImageRole.OkLearning);
        return okLearning.Count > 0 ? okLearning[0] : null;
    }

    private static IReadOnlyList<ImageLearningProjectImage> GetTargetImages(string projectId)
        => ImageLearningProjectService.GetProjectImages(projectId, ImageLearningImageRole.OkValidation)
            .Concat(ImageLearningProjectService.GetProjectImages(projectId, ImageLearningImageRole.Inspection))
            .Concat(ImageLearningProjectService.GetProjectImages(projectId, ImageLearningImageRole.NgValidation))
            .OrderBy(image => image.Role)
            .ThenBy(image => image.FileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static ImageLearningFalseCallImageResult BuildImageResult(
        ImageLearningProjectImage image,
        string imagePath,
        AnalysisResult baseline,
        ImageOnlyPcbInspectionResult learned)
    {
        var baselineVerdict = NormalizeVerdict(baseline.Verdict);
        var learnedVerdict = NormalizeVerdict(learned.InspectionResult.Verdict);
        var baselineRegions = baselineVerdict == "OK"
            ? 0
            : Math.Max(1, baseline.Defects.Count(defect => !string.Equals(defect.JudgmentStatus, "OK", StringComparison.OrdinalIgnoreCase)));
        var learnedRegions = learned.Regions.Count;
        var isOkValidation = image.Role == ImageLearningImageRole.OkValidation;
        var isNgValidation = image.Role == ImageLearningImageRole.NgValidation;

        return new ImageLearningFalseCallImageResult(
            image.Id,
            image.FileName,
            image.Role,
            string.IsNullOrWhiteSpace(image.ImageLevelTruth) ? DefaultTruth(image.Role) : image.ImageLevelTruth,
            imagePath,
            baseline.DifferenceScore,
            baselineVerdict,
            baselineRegions,
            learned.InspectionResult.AnomalyScore,
            learnedVerdict,
            learnedRegions,
            isOkValidation && baselineVerdict != "OK",
            isOkValidation && learnedVerdict != "OK",
            isNgValidation && learnedVerdict == "OK",
            $"Baseline={baseline.InspectionEngine}; learned={ImageOnlyPcbLearningService.EngineName}.");
    }

    private static IReadOnlyList<ImageLearningFalseCallThresholdSweepRow> BuildThresholdSweep(
        IReadOnlyList<ImageLearningFalseCallImageResult> imageResults,
        ImageOnlyPcbLearningOptions options,
        LearnedPcbVisualModel model)
    {
        var okScores = imageResults
            .Where(row => row.Role == ImageLearningImageRole.OkValidation)
            .Select(row => row.LearnedScore)
            .ToArray();
        var ngScores = imageResults
            .Where(row => row.Role == ImageLearningImageRole.NgValidation)
            .Select(row => row.LearnedScore)
            .ToArray();
        var allScores = imageResults.Select(row => row.LearnedScore).ToArray();

        var candidates = new SortedSet<double>();
        var maxObserved = allScores.Length == 0 ? Math.Max(model.LearnedThreshold, options.DefaultLearnedThreshold) : allScores.Max();
        var maxCandidate = Math.Max(Math.Max(20.0, model.LearnedThreshold + 2.0), maxObserved + 2.0);
        for (var threshold = 0.5; threshold <= maxCandidate + 0.000001; threshold += 0.25)
            candidates.Add(Math.Round(threshold, 4));
        candidates.Add(Math.Round(model.LearnedThreshold > 0 ? model.LearnedThreshold : options.DefaultLearnedThreshold, 4));
        foreach (var score in allScores)
        {
            candidates.Add(Math.Round(Math.Max(0.1, score), 4));
            candidates.Add(Math.Round(Math.Max(0.1, score + 0.001), 4));
        }

        return candidates.Select(threshold =>
        {
            var falseCalls = okScores.Count(score => score >= threshold);
            var possibleEscapes = ngScores.Count(score => score < threshold);
            var reviewCount = allScores.Count(score => score >= threshold);
            var falseCallRate = Divide(falseCalls, okScores.Length);
            var possibleEscapeRate = Divide(possibleEscapes, ngScores.Length);
            return new ImageLearningFalseCallThresholdSweepRow(
                threshold,
                falseCalls,
                okScores.Length,
                falseCallRate,
                possibleEscapes,
                ngScores.Length,
                possibleEscapeRate,
                reviewCount,
                allScores.Length,
                Divide(reviewCount, allScores.Length),
                falseCallRate <= options.FalseCallTarget,
                possibleEscapeRate <= options.MaxAllowedPossibleEscapeRate,
                IsRecommended: false);
        }).ToArray();
    }

    private static ImageLearningFalseCallThresholdSweepRow? SelectRecommendedThreshold(
        IReadOnlyList<ImageLearningFalseCallThresholdSweepRow> sweep,
        ImageOnlyPcbLearningOptions options,
        LearnedPcbVisualModel model,
        bool hasNgValidation)
    {
        if (sweep.Count == 0)
            return null;

        var modelThreshold = model.LearnedThreshold > 0 ? model.LearnedThreshold : options.DefaultLearnedThreshold;
        var modelRow = sweep.OrderBy(row => Math.Abs(row.Threshold - modelThreshold)).FirstOrDefault();
        if (modelRow is not null &&
            modelRow.MeetsFalseCallTarget &&
            (!hasNgValidation || modelRow.MeetsPossibleEscapeLimit))
            return modelRow;

        var safeRows = hasNgValidation
            ? sweep.Where(row => row.MeetsPossibleEscapeLimit).ToArray()
            : sweep.ToArray();
        if (safeRows.Length == 0)
            return null;

        return safeRows
            .Where(row => row.MeetsFalseCallTarget)
            .OrderBy(row => row.FalseCallRate)
            .ThenBy(row => row.ReviewRate)
            .ThenBy(row => Math.Abs(row.Threshold - modelThreshold))
            .ThenBy(row => row.Threshold)
            .FirstOrDefault()
            ?? safeRows
                .OrderBy(row => row.FalseCallRate)
                .ThenBy(row => row.ReviewRate)
                .ThenBy(row => Math.Abs(row.Threshold - modelThreshold))
                .ThenBy(row => row.Threshold)
                .First();
    }

    // Minimum OK Validation sample size before a false-call percentage is reported.
    // Below this, the measured count is shown instead of a statistically hollow rate.
    public const int MinimumOkValidationForRate = 20;

    private static ImageLearningFalseCallComparisonMetrics BuildMetrics(
        IReadOnlyList<ImageLearningFalseCallImageResult> imageResults,
        IReadOnlyList<ImageLearningFalseCallThresholdSweepRow> sweep,
        ImageLearningFalseCallThresholdSweepRow? recommended,
        ImageOnlyPcbLearningOptions options)
    {
        var okValidationCount = imageResults.Count(row => row.Role == ImageLearningImageRole.OkValidation);
        var inspectionCount = imageResults.Count(row => row.Role == ImageLearningImageRole.Inspection);
        var ngValidationCount = imageResults.Count(row => row.Role == ImageLearningImageRole.NgValidation);
        var falseCallsBefore = imageResults.Count(row => row.IsFalseCallBeforeLearning);
        var falseCallsAfter = imageResults.Count(row => row.IsFalseCallAfterLearning);
        var falseCallRateBefore = Divide(falseCallsBefore, okValidationCount);
        var falseCallRateAfter = Divide(falseCallsAfter, okValidationCount);
        var reduction = falseCallRateBefore <= 0
            ? 0
            : Math.Max(0.0, (falseCallRateBefore - falseCallRateAfter) / falseCallRateBefore);
        var reviewBefore = imageResults.Count(row => row.BaselineVerdict != "OK");
        var reviewAfter = imageResults.Count(row => row.LearnedVerdict != "OK");
        var possibleEscapes = imageResults.Count(row => row.IsPossibleEscapeAfterLearning);
        var hasNgValidation = ngValidationCount > 0;
        var recommendationStatus = recommended is null
            ? hasNgValidation ? "NO_SAFE_THRESHOLD" : "NOT_AVAILABLE"
            : recommended.MeetsFalseCallTarget && recommended.MeetsPossibleEscapeLimit ? "VALID" : "REVIEW";
        var missedDefectNote = hasNgValidation
            ? $"Possible escapes measured on {ngValidationCount} NG Validation image(s)."
            : "Missed-defect rate cannot yet be proven because no NG Validation images were provided.";

        return new ImageLearningFalseCallComparisonMetrics(
            okValidationCount,
            inspectionCount,
            ngValidationCount,
            falseCallsBefore,
            falseCallsAfter,
            falseCallRateBefore,
            falseCallRateAfter,
            reduction,
            imageResults.Sum(row => row.BaselineAnomalyRegions),
            imageResults.Sum(row => row.LearnedAnomalyRegions),
            possibleEscapes,
            hasNgValidation ? Divide(possibleEscapes, ngValidationCount) : null,
            reviewBefore,
            reviewAfter,
            Divide(reviewBefore, imageResults.Count),
            Divide(reviewAfter, imageResults.Count),
            BaselineReviewThreshold,
            recommended?.Threshold,
            recommendationStatus,
            missedDefectNote);
    }

    private static ReportPaths WriteReports(
        ImageLearningProject project,
        LearnedPcbVisualModel model,
        ImageLearningFalseCallComparisonMetrics metrics,
        IReadOnlyList<ImageLearningFalseCallImageResult> imageResults,
        IReadOnlyList<ImageLearningFalseCallThresholdSweepRow> sweep,
        IReadOnlyList<string> warnings,
        string outputFolder,
        ImageLearningProjectImage referenceImage)
    {
        Directory.CreateDirectory(outputFolder);
        var htmlPath = Path.Combine(outputFolder, "before_after_false_call_report.html");
        var jsonPath = Path.Combine(outputFolder, "before_after_false_call_report.json");
        var resultsCsvPath = Path.Combine(outputFolder, "before_after_results.csv");
        var sweepCsvPath = Path.Combine(outputFolder, "threshold_sweep.csv");

        var jsonPayload = new
        {
            schemaVersion = "image-learning-false-call-comparison.v1",
            generatedAtUtc = DateTime.UtcNow,
            project,
            model = new
            {
                model.ModelId,
                model.ModelVersion,
                model.ProjectId,
                model.LearnedThreshold,
                model.FalseCallTarget,
                model.EvidenceMode,
            },
            baseline = new
            {
                engine = "Pixel Difference Prototype Engine",
                reviewThreshold = BaselineReviewThreshold,
                referenceImage = referenceImage.FileName,
            },
            learned = new
            {
                engine = ImageOnlyPcbLearningService.EngineName,
                model.ModelId,
            },
            metrics,
            warnings,
            imageResults,
            thresholdSweep = sweep,
        };

        File.WriteAllText(jsonPath, JsonSerializer.Serialize(jsonPayload, JsonOptions), Encoding.UTF8);
        File.WriteAllText(resultsCsvPath, BuildResultsCsv(imageResults), Encoding.UTF8);
        File.WriteAllText(sweepCsvPath, BuildSweepCsv(sweep), Encoding.UTF8);
        File.WriteAllText(htmlPath, BuildHtmlReport(project, model, metrics, imageResults, sweep, warnings, referenceImage), Encoding.UTF8);
        return new ReportPaths(htmlPath, jsonPath, resultsCsvPath, sweepCsvPath);
    }

    private static string BuildHtmlReport(
        ImageLearningProject project,
        LearnedPcbVisualModel model,
        ImageLearningFalseCallComparisonMetrics metrics,
        IReadOnlyList<ImageLearningFalseCallImageResult> imageResults,
        IReadOnlyList<ImageLearningFalseCallThresholdSweepRow> sweep,
        IReadOnlyList<string> warnings,
        ImageLearningProjectImage referenceImage)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html><html><head><meta charset=\"utf-8\"><title>Image-only False-call Comparison</title>");
        sb.AppendLine("<style>body{font-family:Segoe UI,Arial,sans-serif;background:#0b0e10;color:#e8eef2;margin:24px;}table{border-collapse:collapse;width:100%;margin-top:12px;}th,td{border:1px solid #3e474e;padding:8px;text-align:left;}th{background:#1b2024}.card{border:1px solid #3e474e;background:#151a1e;padding:12px;margin:8px 0}.warn{border-color:#987538;background:#372914}.sim{border-color:#8f5fd1;background:#2a1740}.ok{color:#50f56e}.ng{color:#f27777}</style>");
        sb.AppendLine("</head><body>");
        sb.AppendLine("<h1>Image-only False-call Comparison</h1>");
        sb.AppendLine("<div class=\"card sim\"><strong>Image-only Stage 1 learning.</strong> Not live camera validation. Formal acceptance requires customer/evaluator dataset.</div>");
        sb.AppendLine($"<p>Project: {Html(project.ProjectName)} / {Html(project.ProjectId)}<br>Board model: {Html(project.BoardModel)}<br>Model: {Html(model.ModelId)}<br>Baseline reference: {Html(referenceImage.FileName)}</p>");
        var fcBefore = new RateEstimate(metrics.FalseCallsBeforeLearning, metrics.OkValidationCount);
        var fcAfter = new RateEstimate(metrics.FalseCallsAfterLearning, metrics.OkValidationCount);
        var escape = new RateEstimate(metrics.PossibleEscapes, metrics.NgValidationCount);
        sb.AppendLine($"<h2>Before / After</h2><div class=\"card\"><strong>False calls reduced from {metrics.FalseCallsBeforeLearning} to {metrics.FalseCallsAfterLearning}</strong><br>");
        sb.AppendLine($"False-call rate before: {fcBefore.DescribeRate()}<br>");
        sb.AppendLine($"False-call rate after: {fcAfter.DescribeRate()}<br>");
        sb.AppendLine($"False calls per million OK boards (after learning): {fcAfter.DescribePpm()}<br>");
        sb.AppendLine(metrics.NgValidationCount > 0
            ? $"Possible escapes: {escape.DescribeUpperBound()}<br>"
            : $"Possible escapes: {metrics.PossibleEscapes}; missed-defect rate not provable without NG Validation images<br>");
        sb.AppendLine($"Recommended threshold: {(metrics.RecommendedThreshold.HasValue ? metrics.RecommendedThreshold.Value.ToString("F2", CultureInfo.InvariantCulture) : "No safe threshold")}<br>");
        sb.AppendLine($"{Html(metrics.MissedDefectEvidenceNote)}</div>");
        sb.AppendLine("<h2>Metric Summary</h2><table><tr><th>Metric</th><th>Value</th></tr>");
        Row("OK validation count", metrics.OkValidationCount.ToString(CultureInfo.InvariantCulture));
        Row("False calls before learning", metrics.FalseCallsBeforeLearning.ToString(CultureInfo.InvariantCulture));
        Row("False calls after learning", metrics.FalseCallsAfterLearning.ToString(CultureInfo.InvariantCulture));
        Row("False-call rate after learning (exact 95% CI)", fcAfter.DescribeRate());
        Row("False calls PPM after learning (exact 95% CI)", fcAfter.DescribePpm());
        Row("Anomaly regions before learning", metrics.AnomalyRegionsBeforeLearning.ToString(CultureInfo.InvariantCulture));
        Row("Anomaly regions after learning", metrics.AnomalyRegionsAfterLearning.ToString(CultureInfo.InvariantCulture));
        Row("Review burden before learning", $"{metrics.ReviewBurdenBeforeLearning} ({metrics.ReviewBurdenRateBeforeLearning:P1})");
        Row("Review burden after learning", $"{metrics.ReviewBurdenAfterLearning} ({metrics.ReviewBurdenRateAfterLearning:P1})");
        Row("Possible escape rate", metrics.NgValidationCount > 0 ? escape.DescribeUpperBound() : "Not proven without NG Validation images");
        Row("Recommendation status", metrics.RecommendationStatus);
        sb.AppendLine("</table>");

        if (warnings.Count > 0)
        {
            sb.AppendLine("<h2>Warnings / Limits</h2><div class=\"card warn\"><ul>");
            foreach (var warning in warnings)
                sb.AppendLine($"<li>{Html(warning)}</li>");
            sb.AppendLine("</ul></div>");
        }

        sb.AppendLine("<h2>Image Results</h2><table><tr><th>Image</th><th>Role</th><th>Baseline verdict</th><th>Baseline score</th><th>Learned verdict</th><th>Learned score</th><th>Before regions</th><th>After regions</th></tr>");
        foreach (var row in imageResults)
        {
            sb.Append("<tr>")
                .Append(Cell(row.FileName))
                .Append(Cell(row.Role.ToString()))
                .Append(Cell(row.BaselineVerdict))
                .Append(Cell(row.BaselineScore.ToString("F2", CultureInfo.InvariantCulture)))
                .Append(Cell(row.LearnedVerdict))
                .Append(Cell(row.LearnedScore.ToString("F2", CultureInfo.InvariantCulture)))
                .Append(Cell(row.BaselineAnomalyRegions.ToString(CultureInfo.InvariantCulture)))
                .Append(Cell(row.LearnedAnomalyRegions.ToString(CultureInfo.InvariantCulture)))
                .AppendLine("</tr>");
        }
        sb.AppendLine("</table>");

        sb.AppendLine("<h2>Threshold Sweep</h2><table><tr><th>Threshold</th><th>False-call rate</th><th>Possible-escape rate</th><th>Review rate</th><th>Recommended</th></tr>");
        foreach (var row in sweep)
        {
            sb.Append("<tr>")
                .Append(Cell(row.Threshold.ToString("F2", CultureInfo.InvariantCulture)))
                .Append(Cell(row.FalseCallRate.ToString("P1", CultureInfo.InvariantCulture)))
                .Append(Cell(row.NgValidationCount > 0 ? row.PossibleEscapeRate.ToString("P1", CultureInfo.InvariantCulture) : "Not proven"))
                .Append(Cell(row.ReviewRate.ToString("P1", CultureInfo.InvariantCulture)))
                .Append(Cell(row.IsRecommended ? "YES" : string.Empty))
                .AppendLine("</tr>");
        }

        sb.AppendLine("</table></body></html>");
        return sb.ToString();

        void Row(string label, string value)
            => sb.AppendLine($"<tr><td>{Html(label)}</td><td>{Html(value)}</td></tr>");
    }

    private static string BuildResultsCsv(IReadOnlyList<ImageLearningFalseCallImageResult> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("projectImageId,fileName,role,truth,baselineScore,baselineVerdict,baselineAnomalyRegions,learnedScore,learnedVerdict,learnedAnomalyRegions,isFalseCallBefore,isFalseCallAfter,isPossibleEscapeAfter,notes");
        foreach (var row in rows)
        {
            sb.Append(row.ProjectImageId.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(Csv(row.FileName)).Append(',')
                .Append(row.Role).Append(',')
                .Append(Csv(row.Truth)).Append(',')
                .Append(row.BaselineScore.ToString("G17", CultureInfo.InvariantCulture)).Append(',')
                .Append(Csv(row.BaselineVerdict)).Append(',')
                .Append(row.BaselineAnomalyRegions.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(row.LearnedScore.ToString("G17", CultureInfo.InvariantCulture)).Append(',')
                .Append(Csv(row.LearnedVerdict)).Append(',')
                .Append(row.LearnedAnomalyRegions.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(row.IsFalseCallBeforeLearning ? "true" : "false").Append(',')
                .Append(row.IsFalseCallAfterLearning ? "true" : "false").Append(',')
                .Append(row.IsPossibleEscapeAfterLearning ? "true" : "false").Append(',')
                .Append(Csv(row.Notes)).AppendLine();
        }

        return sb.ToString();
    }

    private static string BuildSweepCsv(IReadOnlyList<ImageLearningFalseCallThresholdSweepRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("threshold,falseCalls,okValidationCount,falseCallRate,possibleEscapes,ngValidationCount,possibleEscapeRate,reviewCount,evaluatedCount,reviewRate,meetsFalseCallTarget,meetsPossibleEscapeLimit,isRecommended");
        foreach (var row in rows)
        {
            sb.Append(row.Threshold.ToString("G17", CultureInfo.InvariantCulture)).Append(',')
                .Append(row.FalseCalls.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(row.OkValidationCount.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(row.FalseCallRate.ToString("G17", CultureInfo.InvariantCulture)).Append(',')
                .Append(row.PossibleEscapes.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(row.NgValidationCount.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(row.PossibleEscapeRate.ToString("G17", CultureInfo.InvariantCulture)).Append(',')
                .Append(row.ReviewCount.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(row.EvaluatedCount.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(row.ReviewRate.ToString("G17", CultureInfo.InvariantCulture)).Append(',')
                .Append(row.MeetsFalseCallTarget ? "true" : "false").Append(',')
                .Append(row.MeetsPossibleEscapeLimit ? "true" : "false").Append(',')
                .Append(row.IsRecommended ? "true" : "false").AppendLine();
        }

        return sb.ToString();
    }

    private static string ResolveImagePath(ImageLearningProjectImage image)
        => !string.IsNullOrWhiteSpace(image.VaultPath) && File.Exists(image.VaultPath)
            ? image.VaultPath
            : image.OriginalPath;

    private static string CreateOutputFolder(string projectId)
    {
        var folder = Path.Combine(
            AoiDatabase.StorageRoot,
            "exports",
            "image_learning_false_call",
            SafePathPart(projectId),
            DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string SafePathPart(string value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "image-learning" : value.Trim();
        foreach (var ch in Path.GetInvalidFileNameChars().Concat(Path.GetInvalidPathChars()).Distinct())
            text = text.Replace(ch, '_');

        return text;
    }

    private static string NormalizeVerdict(string value)
        => string.Equals(value, "OK", StringComparison.OrdinalIgnoreCase) ? "OK"
            : string.Equals(value, "NG", StringComparison.OrdinalIgnoreCase) ? "NG"
            : "REVIEW";

    private static string DefaultTruth(ImageLearningImageRole role)
        => role switch
        {
            ImageLearningImageRole.OkValidation => "OK",
            ImageLearningImageRole.NgValidation => "NG",
            _ => "UNKNOWN",
        };

    private static double Divide(int numerator, int denominator)
        => denominator <= 0 ? 0 : numerator / (double)denominator;

    private static string Html(string value)
        => WebUtility.HtmlEncode(value ?? string.Empty);

    private static string Cell(string value)
        => $"<td>{Html(value)}</td>";

    private static string Csv(string value)
    {
        var text = value ?? string.Empty;
        if (!text.Contains('"') && !text.Contains(',') && !text.Contains('\n') && !text.Contains('\r'))
            return text;

        return $"\"{text.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private sealed record ReportPaths(
        string HtmlReportPath,
        string JsonReportPath,
        string ResultsCsvPath,
        string ThresholdSweepCsvPath);
}
