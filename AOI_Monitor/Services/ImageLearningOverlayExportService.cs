using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AOI_Monitor.Data;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public static class ImageLearningOverlayExportService
{
    private const int MaxOverlaySidePixels = 900;
    private const int MinOverlaySidePixels = 320;
    private const int SideBySideGapPixels = 18;
    private const int LabelBandHeightPixels = 34;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly Typeface LabelTypeface = new("Segoe UI");

    public static ImageLearningOverlayExportResult ExportProjectVisualEvidence(
        string projectId,
        string modelId,
        ImageLearningOverlayExportOptions? options = null,
        string operatorId = "UNKNOWN")
    {
        var project = AoiDatabase.GetImageLearningProject(projectId)
            ?? throw new InvalidOperationException("Image-only learning project does not exist.");
        var model = AoiDatabase.GetLearnedPcbVisualModel(modelId)
            ?? throw new InvalidOperationException("Learned PCB visual model metadata does not exist.");
        if (!string.Equals(project.ProjectId, model.ProjectId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The learned visual model does not belong to the selected image-learning project.");

        var normalizedOptions = options ?? new ImageLearningOverlayExportOptions();
        var learningOptions = normalizedOptions.LearningOptions ?? new ImageOnlyPcbLearningOptions
        {
            InputWidth = model.InputWidth,
            InputHeight = model.InputHeight,
            FalseCallTarget = model.FalseCallTarget,
        };
        var maxExamples = Math.Clamp(normalizedOptions.MaxBatchExamples, 1, 50);
        var normalizedOperator = string.IsNullOrWhiteSpace(operatorId) ? "UNKNOWN" : operatorId.Trim();
        var outputFolder = CreateOutputFolder(project.ProjectId);
        var warnings = new List<string>();
        var items = new List<ImageLearningOverlayExportItem>();
        var projectImages = ImageLearningProjectService.GetProjectImages(project.ProjectId)
            .ToDictionary(image => image.Id);
        var reference = SelectReferenceImage(project.ProjectId, model, warnings);

        var persistedResults = AoiDatabase.GetImageLearningInspectionResults(project.ProjectId, model.ModelId, limit: 10000);
        if (persistedResults.Count == 0)
            warnings.Add("No saved image-only inspection results were available for visual evidence export.");

        var index = 1;
        foreach (var result in persistedResults.OrderBy(row => row.CreatedAtUtc).ThenBy(row => row.Id))
        {
            projectImages.TryGetValue(result.ProjectImageId, out var image);
            items.Add(ExportVisualItem(
                outputFolder,
                "inspection_results",
                index++,
                result,
                image,
                reference,
                warnings));
        }

        foreach (var result in persistedResults.OrderByDescending(row => row.AnomalyScore).ThenBy(row => row.CreatedAtUtc).Take(maxExamples))
        {
            projectImages.TryGetValue(result.ProjectImageId, out var image);
            items.Add(ExportVisualItem(
                outputFolder,
                "top_anomaly_examples",
                index++,
                result,
                image,
                reference,
                warnings));
        }

        var comparisonCandidates = BuildComparisonCandidates(project.ProjectId, model.ModelId, reference, learningOptions, normalizedOperator, warnings);
        foreach (var candidate in comparisonCandidates
                     .Where(candidate => candidate.IsOkValidationFalseCallBeforeLearning)
                     .OrderByDescending(candidate => candidate.Baseline.DifferenceScore)
                     .Take(maxExamples))
        {
            items.Add(ExportVisualItem(
                outputFolder,
                "ok_false_calls_before_learning",
                index++,
                candidate.Learned.InspectionResult,
                candidate.Image,
                reference,
                warnings,
                candidate.Baseline));
        }

        foreach (var candidate in comparisonCandidates
                     .Where(candidate => candidate.IsResolvedByLearning)
                     .OrderByDescending(candidate => candidate.Baseline.DifferenceScore)
                     .Take(maxExamples))
        {
            items.Add(ExportVisualItem(
                outputFolder,
                "resolved_by_learning",
                index++,
                candidate.Learned.InspectionResult,
                candidate.Image,
                reference,
                warnings,
                candidate.Baseline));
        }

        foreach (var candidate in comparisonCandidates
                     .Where(candidate => candidate.IsPossibleEscapeAfterLearning)
                     .OrderBy(candidate => candidate.Learned.InspectionResult.AnomalyScore)
                     .Take(maxExamples))
        {
            items.Add(ExportVisualItem(
                outputFolder,
                "possible_escape_warnings",
                index++,
                candidate.Learned.InspectionResult,
                candidate.Image,
                reference,
                warnings,
                candidate.Baseline));
        }

        var manifestPath = Path.Combine(outputFolder, "visual_evidence_manifest.json");
        var createdAtUtc = DateTime.UtcNow;
        WriteManifest(manifestPath, project, model, createdAtUtc, items, warnings);
        var verified = ExportVerificationService.RecordVerifiedExport(
            "ImageLearningVisualEvidence",
            outputFolder,
            warnings.Count == 0 ? "OK" : "WARN",
            normalizedOperator);
        AoiDatabase.RecordAuditEvent(
            "IMAGE_LEARNING_VISUAL_EVIDENCE_EXPORT",
            $"Image-only visual evidence exported: project={project.ProjectId}; model={model.ModelId}; items={items.Count}; warnings={warnings.Count}.",
            operatorWithRole: normalizedOperator,
            relatedEntityType: "ImageLearningProject",
            relatedEntityId: project.ProjectId,
            relatedPath: outputFolder);

        return new ImageLearningOverlayExportResult(
            project.ProjectId,
            model.ModelId,
            createdAtUtc,
            outputFolder,
            manifestPath,
            items,
            warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            verified.ExportHistoryId);
    }

    private static ImageLearningOverlayExportItem ExportVisualItem(
        string outputFolder,
        string category,
        int index,
        ImageLearningInspectionResult result,
        ImageLearningProjectImage? image,
        ReferenceImage? reference,
        List<string> exportWarnings,
        AnalysisResult? baseline = null)
    {
        var itemWarnings = new List<string>();
        var sourcePath = ResolveImagePath(result, image);
        var role = image?.Role ?? ImageLearningImageRole.Inspection;
        var fileName = image?.FileName ?? Path.GetFileName(sourcePath);
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = result.ResultId;

        var baseFolder = Path.Combine(outputFolder, category);
        Directory.CreateDirectory(baseFolder);
        var safeBaseName = $"{index:000}_{SafePathPart(Path.GetFileNameWithoutExtension(fileName))}";
        var originalPath = Path.Combine(baseFolder, $"{safeBaseName}_original.png");
        var heatmapPath = Path.Combine(baseFolder, $"{safeBaseName}_learned_heatmap.png");
        var overlayPath = Path.Combine(baseFolder, $"{safeBaseName}_learned_overlay.png");
        var referenceVsInspectedPath = Path.Combine(baseFolder, $"{safeBaseName}_reference_vs_inspected.png");
        var baselineVsLearnedPath = Path.Combine(baseFolder, $"{safeBaseName}_baseline_vs_learned.png");

        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            var warning = $"Missing source image for visual evidence: result={result.ResultId}; image={fileName}.";
            itemWarnings.Add(warning);
            exportWarnings.Add(warning);
            return BuildItem(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
        }

        try
        {
            var inspected = LoadBitmap(sourcePath);
            SavePng(inspected, originalPath);
            RenderLearnedHeatmap(inspected, result, heatmapPath);
            RenderLearnedOverlay(inspected, result, overlayPath);

            if (reference is not null && File.Exists(reference.Path))
            {
                var referenceBitmap = LoadBitmap(reference.Path);
                RenderReferenceVsInspected(referenceBitmap, inspected, reference.Label, fileName, referenceVsInspectedPath);

                baseline ??= TryAnalyzeBaseline(sourcePath, reference.Path, fileName, itemWarnings);
                RenderBaselineVsLearned(inspected, baseline, result, baselineVsLearnedPath);
            }
            else
            {
                var warning = $"Reference image was unavailable; side-by-side reference and baseline overlay were not generated for {fileName}.";
                itemWarnings.Add(warning);
                exportWarnings.Add(warning);
                referenceVsInspectedPath = string.Empty;
                baselineVsLearnedPath = string.Empty;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            var warning = $"Visual evidence export skipped image {fileName}: {ex.Message}";
            itemWarnings.Add(warning);
            exportWarnings.Add(warning);
            return BuildItem(
                File.Exists(originalPath) ? originalPath : string.Empty,
                File.Exists(heatmapPath) ? heatmapPath : string.Empty,
                File.Exists(overlayPath) ? overlayPath : string.Empty,
                File.Exists(referenceVsInspectedPath) ? referenceVsInspectedPath : string.Empty,
                File.Exists(baselineVsLearnedPath) ? baselineVsLearnedPath : string.Empty);
        }

        foreach (var warning in itemWarnings)
            exportWarnings.Add(warning);

        return BuildItem(originalPath, heatmapPath, overlayPath, referenceVsInspectedPath, baselineVsLearnedPath);

        ImageLearningOverlayExportItem BuildItem(
            string original,
            string heatmap,
            string overlay,
            string referenceVsInspected,
            string baselineVsLearned)
            => new(
                category,
                result.Id > 0 ? result.Id : null,
                image?.Id ?? result.ProjectImageId,
                fileName,
                role,
                NormalizeVerdict(result.Verdict),
                result.AnomalyScore,
                result.AnomalyRegions.Count,
                original,
                heatmap,
                overlay,
                referenceVsInspected,
                baselineVsLearned,
                itemWarnings.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static IReadOnlyList<ComparisonCandidate> BuildComparisonCandidates(
        string projectId,
        string modelId,
        ReferenceImage? reference,
        ImageOnlyPcbLearningOptions learningOptions,
        string operatorId,
        List<string> warnings)
    {
        if (reference is null || !File.Exists(reference.Path))
            return Array.Empty<ComparisonCandidate>();

        var candidates = new List<ComparisonCandidate>();
        var engine = new PixelDifferenceInspectionEngine();
        var targets = ImageLearningProjectService.GetProjectImages(projectId, ImageLearningImageRole.OkValidation)
            .Concat(ImageLearningProjectService.GetProjectImages(projectId, ImageLearningImageRole.NgValidation))
            .ToArray();
        foreach (var image in targets)
        {
            var path = ResolveImagePath(image);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                warnings.Add($"Skipped batch visual evidence comparison for missing image: {image.FileName}.");
                continue;
            }

            try
            {
                var baseline = engine.Analyze(path, reference.Path, DetectionPriority.MaximizeDefectRecall);
                var learned = ImageOnlyPcbLearningService.InspectImagePath(
                    modelId,
                    path,
                    learningOptions,
                    operatorId,
                    image,
                    persist: false);
                var baselineVerdict = NormalizeVerdict(baseline.Verdict);
                var learnedVerdict = NormalizeVerdict(learned.InspectionResult.Verdict);
                var isOkFalseCall = image.Role == ImageLearningImageRole.OkValidation && baselineVerdict != "OK";
                candidates.Add(new ComparisonCandidate(
                    image,
                    baseline,
                    learned,
                    isOkFalseCall,
                    isOkFalseCall && learnedVerdict == "OK",
                    image.Role == ImageLearningImageRole.NgValidation && learnedVerdict == "OK"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or InvalidOperationException)
            {
                warnings.Add($"Skipped batch visual evidence comparison for {image.FileName}: {ex.Message}");
            }
        }

        return candidates
            .OrderByDescending(candidate => candidate.Baseline.DifferenceScore)
            .ToArray();
    }

    private static AnalysisResult? TryAnalyzeBaseline(
        string imagePath,
        string referencePath,
        string fileName,
        List<string> warnings)
    {
        try
        {
            return new PixelDifferenceInspectionEngine().Analyze(imagePath, referencePath, DetectionPriority.MaximizeDefectRecall);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or InvalidOperationException)
        {
            warnings.Add($"Baseline visual overlay could not be generated for {fileName}: {ex.Message}");
            return null;
        }
    }

    private static ReferenceImage? SelectReferenceImage(
        string projectId,
        LearnedPcbVisualModel model,
        List<string> warnings)
    {
        var referenceImage = ImageLearningProjectService.GetProjectImages(projectId, ImageLearningImageRole.GoldenReference).FirstOrDefault()
            ?? ImageLearningProjectService.GetProjectImages(projectId, ImageLearningImageRole.OkLearning).FirstOrDefault();
        if (referenceImage is not null)
        {
            var path = ResolveImagePath(referenceImage);
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                return new ReferenceImage(path, referenceImage.FileName);
        }

        var learnedReference = model.Artifacts.FirstOrDefault(
            artifact => string.Equals(artifact.ArtifactName, "learned_reference.png", StringComparison.OrdinalIgnoreCase))?.ArtifactPath;
        if (!string.IsNullOrWhiteSpace(learnedReference) && File.Exists(learnedReference))
            return new ReferenceImage(learnedReference, "learned_reference.png");

        warnings.Add("No Golden / Reference, OK Learning, or learned reference artifact was available for side-by-side visual evidence.");
        return null;
    }

    private static void RenderLearnedHeatmap(
        BitmapSource inspected,
        ImageLearningInspectionResult result,
        string outputPath)
    {
        var size = ComputeOverlaySize(inspected.PixelWidth, inspected.PixelHeight);
        var imageRect = new Rect(0, 0, size.Width, size.Height);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Black, null, imageRect);
            dc.PushOpacity(0.42);
            dc.DrawImage(inspected, imageRect);
            dc.Pop();
            foreach (var region in result.AnomalyRegions)
            {
                var rect = ToDisplayRect(region.X, region.Y, region.Width, region.Height, imageRect);
                var opacity = Math.Clamp(0.28 + region.Confidence * 0.55, 0.35, 0.86);
                var fill = new SolidColorBrush(Color.FromArgb((byte)(opacity * 255), 255, 72, 0));
                fill.Freeze();
                dc.DrawRectangle(fill, null, rect);
            }

            DrawLabel(dc, $"{ImageOnlyPcbLearningService.EngineName} anomaly heatmap", new Point(8, 8), Brushes.White, HeatmapLabelBrush(), size.Width - 16);
            DrawLabel(dc, BuildLearnedSummaryLabel(result), new Point(8, size.Height - 34), Brushes.White, DarkLabelBrush(), size.Width - 16);
        }

        SaveRenderedVisual(visual, (int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height), outputPath);
    }

    private static void RenderLearnedOverlay(
        BitmapSource inspected,
        ImageLearningInspectionResult result,
        string outputPath)
    {
        var size = ComputeOverlaySize(inspected.PixelWidth, inspected.PixelHeight);
        var imageRect = new Rect(0, 0, size.Width, size.Height);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(inspected, imageRect);
            DrawLearnedBoxes(dc, result, imageRect);
            DrawLabel(dc, ImageOnlyPcbLearningService.EngineName, new Point(8, 8), Brushes.White, LearnedLabelBrush(), size.Width - 16);
            DrawLabel(dc, BuildLearnedSummaryLabel(result), new Point(8, 38), Brushes.White, DarkLabelBrush(), size.Width - 16);
        }

        SaveRenderedVisual(visual, (int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height), outputPath);
    }

    private static void RenderReferenceVsInspected(
        BitmapSource reference,
        BitmapSource inspected,
        string referenceLabel,
        string inspectedLabel,
        string outputPath)
    {
        var panelSize = ComputePairPanelSize(reference, inspected);
        var width = (int)(panelSize.Width * 2 + SideBySideGapPixels);
        var height = (int)(panelSize.Height + LabelBandHeightPixels);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(DarkSurfaceBrush(), null, new Rect(0, 0, width, height));
            var leftPanel = new Rect(0, LabelBandHeightPixels, panelSize.Width, panelSize.Height);
            var rightPanel = new Rect(panelSize.Width + SideBySideGapPixels, LabelBandHeightPixels, panelSize.Width, panelSize.Height);
            DrawImagePanel(dc, reference, leftPanel);
            DrawImagePanel(dc, inspected, rightPanel);
            DrawLabel(dc, $"Reference: {referenceLabel}", new Point(8, 5), Brushes.White, DarkLabelBrush(), panelSize.Width - 16);
            DrawLabel(dc, $"Inspected: {inspectedLabel}", new Point(panelSize.Width + SideBySideGapPixels + 8, 5), Brushes.White, DarkLabelBrush(), panelSize.Width - 16);
        }

        SaveRenderedVisual(visual, width, height, outputPath);
    }

    private static void RenderBaselineVsLearned(
        BitmapSource inspected,
        AnalysisResult? baseline,
        ImageLearningInspectionResult learned,
        string outputPath)
    {
        var panelSize = ComputeOverlaySize(inspected.PixelWidth, inspected.PixelHeight);
        var width = (int)(panelSize.Width * 2 + SideBySideGapPixels);
        var height = (int)(panelSize.Height + LabelBandHeightPixels);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(DarkSurfaceBrush(), null, new Rect(0, 0, width, height));
            var left = new Rect(0, LabelBandHeightPixels, panelSize.Width, panelSize.Height);
            var right = new Rect(panelSize.Width + SideBySideGapPixels, LabelBandHeightPixels, panelSize.Width, panelSize.Height);
            DrawImagePanel(dc, inspected, left);
            DrawImagePanel(dc, inspected, right);
            if (baseline is not null)
                DrawBaselineBoxes(dc, baseline, left);
            DrawLearnedBoxes(dc, learned, right);
            DrawLabel(dc, $"Before learning: {NormalizeVerdict(baseline?.Verdict ?? "REVIEW")}", new Point(8, 5), Brushes.White, BaselineLabelBrush(), panelSize.Width - 16);
            DrawLabel(dc, $"{ImageOnlyPcbLearningService.EngineName}: {NormalizeVerdict(learned.Verdict)} / Anomaly score {learned.AnomalyScore:F2}", new Point(panelSize.Width + SideBySideGapPixels + 8, 5), Brushes.White, LearnedLabelBrush(), panelSize.Width - 16);
        }

        SaveRenderedVisual(visual, width, height, outputPath);
    }

    private static void DrawImagePanel(DrawingContext dc, BitmapSource image, Rect panel)
    {
        dc.DrawRectangle(Brushes.Black, BorderPen(), panel);
        var fit = FitRect(image.PixelWidth, image.PixelHeight, panel);
        dc.DrawImage(image, fit);
    }

    private static void DrawLearnedBoxes(DrawingContext dc, ImageLearningInspectionResult result, Rect imageRect)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(242, 119, 119)), Math.Max(2.0, imageRect.Width / 180.0));
        pen.Freeze();
        var reviewPen = new Pen(new SolidColorBrush(Color.FromRgb(225, 163, 52)), Math.Max(2.0, imageRect.Width / 180.0));
        reviewPen.Freeze();
        foreach (var region in result.AnomalyRegions.OrderByDescending(region => region.Score).Take(30))
        {
            var rect = ToDisplayRect(region.X, region.Y, region.Width, region.Height, imageRect);
            dc.DrawRectangle(null, string.Equals(region.Severity, "NG", StringComparison.OrdinalIgnoreCase) ? pen : reviewPen, rect);
            var label = $"Anomaly score {region.Score:F2} / Confidence {region.Confidence:P0}";
            DrawLabel(dc, label, new Point(rect.X, Math.Max(imageRect.Y + 2, rect.Y - 28)), Brushes.White, DarkLabelBrush(), Math.Max(110, imageRect.Right - rect.X - 4));
        }
    }

    private static string BuildLearnedSummaryLabel(ImageLearningInspectionResult result)
    {
        var confidence = result.AnomalyRegions.Count == 0
            ? 0
            : result.AnomalyRegions.Max(region => region.Confidence);
        return $"Verdict {NormalizeVerdict(result.Verdict)} / Anomaly score {result.AnomalyScore:F2} / Confidence {confidence:P0}";
    }

    private static void DrawBaselineBoxes(DrawingContext dc, AnalysisResult baseline, Rect imageRect)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(225, 163, 52)), Math.Max(2.0, imageRect.Width / 180.0));
        pen.Freeze();
        var defects = baseline.Defects
            .Where(defect => !string.Equals(defect.JudgmentStatus, "OK", StringComparison.OrdinalIgnoreCase))
            .Take(30)
            .ToArray();
        if (defects.Length == 0 && NormalizeVerdict(baseline.Verdict) != "OK")
        {
            var hotspot = baseline.Hotspot == Rect.Empty ? new Rect(0.42, 0.42, 0.16, 0.16) : baseline.Hotspot;
            dc.DrawRectangle(null, pen, ToDisplayRect(hotspot.X, hotspot.Y, hotspot.Width, hotspot.Height, imageRect));
            return;
        }

        foreach (var defect in defects)
        {
            var box = defect.BoundingBox == Rect.Empty ? baseline.Hotspot : defect.BoundingBox;
            var rect = ToDisplayRect(box.X, box.Y, box.Width, box.Height, imageRect);
            dc.DrawRectangle(null, pen, rect);
            DrawLabel(dc, $"{NormalizeVerdict(defect.JudgmentStatus)} {defect.Confidence:P0}", new Point(rect.X, Math.Max(imageRect.Y + 2, rect.Y - 28)), Brushes.White, BaselineLabelBrush(), Math.Max(110, imageRect.Right - rect.X - 4));
        }
    }

    private static void DrawLabel(
        DrawingContext dc,
        string text,
        Point origin,
        Brush foreground,
        Brush background,
        double maxWidth)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            LabelTypeface,
            14,
            foreground,
            1.0)
        {
            MaxTextWidth = Math.Max(80, maxWidth),
            MaxLineCount = 2,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        var bounds = new Rect(origin.X, origin.Y, Math.Min(maxWidth, formatted.Width + 8), formatted.Height + 6);
        dc.DrawRectangle(background, null, bounds);
        dc.DrawText(formatted, new Point(origin.X + 4, origin.Y + 3));
    }

    private static BitmapSource LoadBitmap(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0)
            throw new InvalidDataException("Image decoder found no frames.");

        BitmapSource source = decoder.Frames[0];
        if (source.Format != PixelFormats.Bgra32 && source.Format != PixelFormats.Pbgra32)
            source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        source.Freeze();
        return source;
    }

    private static void SavePng(BitmapSource bitmap, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void SaveRenderedVisual(DrawingVisual visual, int width, int height, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        SavePng(bitmap, path);
    }

    private static Size ComputeOverlaySize(int imageWidth, int imageHeight)
    {
        var width = Math.Max(1, imageWidth);
        var height = Math.Max(1, imageHeight);
        var scale = Math.Min(MaxOverlaySidePixels / (double)width, MaxOverlaySidePixels / (double)height);
        if (scale > 1.0)
            scale = Math.Max(MinOverlaySidePixels / (double)width, MinOverlaySidePixels / (double)height);

        scale = Math.Clamp(scale, 0.05, 16.0);
        return new Size(Math.Max(1, Math.Round(width * scale)), Math.Max(1, Math.Round(height * scale)));
    }

    private static Size ComputePairPanelSize(BitmapSource first, BitmapSource second)
    {
        var firstSize = ComputeOverlaySize(first.PixelWidth, first.PixelHeight);
        var secondSize = ComputeOverlaySize(second.PixelWidth, second.PixelHeight);
        return new Size(Math.Max(firstSize.Width, secondSize.Width), Math.Max(firstSize.Height, secondSize.Height));
    }

    private static Rect FitRect(double sourceWidth, double sourceHeight, Rect target)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
            return target;

        var scale = Math.Min(target.Width / sourceWidth, target.Height / sourceHeight);
        var width = sourceWidth * scale;
        var height = sourceHeight * scale;
        return new Rect(target.X + (target.Width - width) / 2.0, target.Y + (target.Height - height) / 2.0, width, height);
    }

    private static Rect ToDisplayRect(double x, double y, double width, double height, Rect imageRect)
    {
        var left = imageRect.X + Math.Clamp(x, 0.0, 1.0) * imageRect.Width;
        var top = imageRect.Y + Math.Clamp(y, 0.0, 1.0) * imageRect.Height;
        var boxWidth = Math.Clamp(width, 0.001, 1.0) * imageRect.Width;
        var boxHeight = Math.Clamp(height, 0.001, 1.0) * imageRect.Height;
        if (left + boxWidth > imageRect.Right)
            boxWidth = Math.Max(2, imageRect.Right - left);
        if (top + boxHeight > imageRect.Bottom)
            boxHeight = Math.Max(2, imageRect.Bottom - top);

        return new Rect(left, top, Math.Max(2, boxWidth), Math.Max(2, boxHeight));
    }

    private static void WriteManifest(
        string manifestPath,
        ImageLearningProject project,
        LearnedPcbVisualModel model,
        DateTime createdAtUtc,
        IReadOnlyList<ImageLearningOverlayExportItem> items,
        IReadOnlyList<string> warnings)
    {
        var manifest = new
        {
            schemaVersion = "image-learning-visual-evidence.v1",
            generatedAtUtc = createdAtUtc,
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
                model.LearnedThreshold,
                model.FalseCallTarget,
            },
            truthfulness = new[]
            {
                "Image-only Stage 1 learning",
                "Not live camera validation",
                "Formal acceptance requires customer/evaluator dataset",
            },
            warnings = warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            categories = items.GroupBy(item => item.Category)
                .Select(group => new { category = group.Key, count = group.Count() })
                .OrderBy(group => group.category, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            items,
        };

        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath) ?? ".");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions));
    }

    private static string ResolveImagePath(ImageLearningInspectionResult result, ImageLearningProjectImage? image)
    {
        if (!string.IsNullOrWhiteSpace(result.ImagePath) && File.Exists(result.ImagePath))
            return result.ImagePath;
        if (image is not null)
            return ResolveImagePath(image);

        return result.ImagePath;
    }

    private static string ResolveImagePath(ImageLearningProjectImage image)
    {
        if (!string.IsNullOrWhiteSpace(image.VaultPath) && File.Exists(image.VaultPath))
            return image.VaultPath;
        return image.OriginalPath;
    }

    private static string CreateOutputFolder(string projectId)
    {
        var folder = Path.Combine(
            AoiDatabase.StorageRoot,
            "exports",
            "image_learning_visual_evidence",
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

        return text.Replace(' ', '_');
    }

    private static string NormalizeVerdict(string value)
        => string.Equals(value, "OK", StringComparison.OrdinalIgnoreCase) ? "OK"
            : string.Equals(value, "NG", StringComparison.OrdinalIgnoreCase) ? "NG"
            : "REVIEW";

    private static Brush DarkSurfaceBrush()
        => FrozenBrush(Color.FromRgb(11, 14, 16));

    private static Brush DarkLabelBrush()
        => FrozenBrush(Color.FromArgb(220, 18, 22, 25));

    private static Brush LearnedLabelBrush()
        => FrozenBrush(Color.FromArgb(230, 42, 23, 64));

    private static Brush BaselineLabelBrush()
        => FrozenBrush(Color.FromArgb(230, 63, 44, 19));

    private static Brush HeatmapLabelBrush()
        => FrozenBrush(Color.FromArgb(230, 74, 23, 26));

    private static Pen BorderPen()
    {
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(62, 71, 78)), 1.0);
        pen.Freeze();
        return pen;
    }

    private static SolidColorBrush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private sealed record ReferenceImage(string Path, string Label);

    private sealed record ComparisonCandidate(
        ImageLearningProjectImage Image,
        AnalysisResult Baseline,
        ImageOnlyPcbInspectionResult Learned,
        bool IsOkValidationFalseCallBeforeLearning,
        bool IsResolvedByLearning,
        bool IsPossibleEscapeAfterLearning);
}
