using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public sealed class PixelDifferenceInspectionEngine : IInspectionEngine
{
    public string Name => "Pixel Difference Prototype Engine";
    public string Version => "PIXEL_DIFF_0.1";

    public AnalysisResult Analyze(string samplePath, string? goldenPath, DetectionPriority priority)
    {
        var timing = new InspectionTiming();
        var timestamp = DateTime.Now;
        var result = CreateBaseResult(samplePath, goldenPath, priority, timing, timestamp);

        var loadWatch = Stopwatch.StartNew();
        var sample = LoadBgra32(samplePath);
        loadWatch.Stop();
        timing.ImageLoadMilliseconds += loadWatch.Elapsed.TotalMilliseconds;
        if (sample is null)
        {
            result.ErrorCode = "SAMPLE_IMAGE_LOAD_FAILED";
            result.ErrorMessage = $"Unable to load sample image: {samplePath}";
            result.DecisionReason = result.ErrorMessage;
            result.Evidence = new List<string>
            {
                "Sample image could not be loaded; decision remains REVIEW by policy.",
                result.ErrorMessage,
            };
            result.Defects.Add(CreateDefectResult(result, "Sample Image Load Failed", "ROI-SAMPLE-ERROR", 1, 0, 0));
            result.Timing.RecalculateTotal();
            return result;
        }

        var preprocessingWatch = Stopwatch.StartNew();
        var meanBrightness = CalculateBrightness(sample);
        preprocessingWatch.Stop();
        timing.PreprocessingMilliseconds += preprocessingWatch.Elapsed.TotalMilliseconds;
        result.MeanBrightness = meanBrightness;

        if (string.IsNullOrWhiteSpace(goldenPath))
        {
            result.Defects.Add(CreateDefectResult(result, "Reference Missing", "ROI-REFERENCE", 1, sample.PixelWidth, sample.PixelHeight));
            result.Timing.RecalculateTotal();
            return result;
        }

        loadWatch.Restart();
        var golden = LoadBgra32(goldenPath);
        loadWatch.Stop();
        timing.ImageLoadMilliseconds += loadWatch.Elapsed.TotalMilliseconds;
        if (golden is null)
        {
            result.ErrorCode = "GOLDEN_IMAGE_LOAD_FAILED";
            result.ErrorMessage = $"Unable to load golden image: {goldenPath}";
            result.SuggestedDefect = "Golden Reference Load Failed";
            result.DecisionReason = result.ErrorMessage;
            result.Evidence = new List<string>
            {
                "Golden reference could not be loaded; decision remains REVIEW by policy.",
                result.ErrorMessage,
            };
            result.Defects.Add(CreateDefectResult(result, "Golden Reference Load Failed", "ROI-GOLDEN-ERROR", 1, sample.PixelWidth, sample.PixelHeight));
            result.Timing.RecalculateTotal();
            return result;
        }

        preprocessingWatch.Restart();
        var sampleNorm = Resize(sample, 384, 384);
        var goldenNorm = Resize(golden, 384, 384);
        preprocessingWatch.Stop();
        timing.PreprocessingMilliseconds += preprocessingWatch.Elapsed.TotalMilliseconds;

        var recipeLoad = RecipeService.LoadLatestRecipe(result.BoardProgram);
        if (recipeLoad.HasEnabledRois)
        {
            var recipeWatch = Stopwatch.StartNew();
            ApplyRecipeRoiAnalysis(result, sampleNorm, goldenNorm, priority, recipeLoad);
            recipeWatch.Stop();
            timing.InferenceMilliseconds = recipeWatch.Elapsed.TotalMilliseconds;
            result.Timing.RecalculateTotal();
            return result;
        }

        var inferenceWatch = Stopwatch.StartNew();
        var diff = Compare(sampleNorm, goldenNorm, out var hotspot);
        inferenceWatch.Stop();
        timing.InferenceMilliseconds = inferenceWatch.Elapsed.TotalMilliseconds;
        result.DifferenceScore = diff;
        result.Hotspot = hotspot;

        var (ngThreshold, reviewThreshold) = GetThresholds(priority);
        result.NgThreshold = ngThreshold;
        result.ReviewThreshold = reviewThreshold;

        if (diff >= ngThreshold)
        {
            result.Verdict = "NG";
            result.SuggestedDefect = "Possible Solder Bridge";
            result.DecisionMargin = diff - ngThreshold;
            result.DecisionReason = "Difference score exceeds NG threshold under current policy.";
        }
        else if (diff >= reviewThreshold)
        {
            result.Verdict = "REVIEW";
            result.SuggestedDefect = "Alignment / Reflection Difference";
            result.DecisionMargin = Math.Min(diff - reviewThreshold, ngThreshold - diff);
            result.DecisionReason = "Difference score is in the review band; human confirmation required.";
        }
        else
        {
            result.Verdict = "OK";
            result.SuggestedDefect = "No Significant Difference";
            result.DecisionMargin = reviewThreshold - diff;
            result.DecisionReason = "Difference score is below review threshold for this policy.";
        }

        result.Confidence = ComputeConfidence(result.Verdict, diff, reviewThreshold, ngThreshold);
        result.Evidence = BuildEvidence(result, priority);
        AppendRecipeWarnings(result, recipeLoad);
        result.Defects.Add(CreateDefectResult(result, result.SuggestedDefect, "ROI-HOTSPOT-001", 1, sampleNorm.PixelWidth, sampleNorm.PixelHeight));
        result.Timing.RecalculateTotal();

        return result;
    }

    private static void ApplyRecipeRoiAnalysis(
        AnalysisResult result,
        BitmapSource sample,
        BitmapSource golden,
        DetectionPriority priority,
        RecipeLoadResult recipeLoad)
    {
        var recipe = recipeLoad.Recipe!;
        var rois = recipe.Rois.Where(roi => roi.Enabled).ToArray();
        var (defaultNgThreshold, defaultReviewThreshold) = GetThresholds(priority);
        result.RecipeName = recipe.RecipeName;
        result.RecipeRevision = recipe.Revision;
        result.NgThreshold = defaultNgThreshold;
        result.ReviewThreshold = defaultReviewThreshold;
        result.Defects.Clear();
        result.Evidence.Clear();
        AppendRecipeWarnings(result, recipeLoad);

        var checkedCount = 0;
        double maxScore = 0;
        RecipeRoi? maxRoi = null;
        var sequence = 1;

        foreach (var roi in rois)
        {
            checkedCount++;
            var score = CompareRegion(sample, golden, new Rect(roi.X, roi.Y, roi.Width, roi.Height));
            if (score > maxScore)
            {
                maxScore = score;
                maxRoi = roi;
            }

            var threshold = ResolveRoiThreshold(result, recipe, priority, roi);
            var roiNgThreshold = threshold.NgThreshold;
            var roiReviewThreshold = threshold.ReviewThreshold;
            if (score < roiReviewThreshold)
                continue;

            var judgment = score >= roiNgThreshold ? "NG" : "REVIEW";
            var confidence = ComputeConfidence(judgment, score, roiReviewThreshold, roiNgThreshold);
            var defect = CreateDefectResult(
                result,
                $"{roi.RoiType} ROI Difference",
                roi.RoiId,
                sequence++,
                sample.PixelWidth,
                sample.PixelHeight,
                new Rect(roi.X, roi.Y, roi.Width, roi.Height),
                confidence,
                judgment);
            defect.RoiName = roi.DisplayName;
            defect.RoiType = roi.RoiType;
            defect.SourceRoiId = roi.RoiId;
            result.Defects.Add(defect);
        }

        result.DifferenceScore = maxScore;
        if (maxRoi is not null)
            result.Hotspot = new Rect(maxRoi.X, maxRoi.Y, maxRoi.Width, maxRoi.Height);

        result.Evidence.Add($"Recipe '{recipe.RecipeName}' revision {recipe.Revision} applied.");
        result.Evidence.Add($"Checked {checkedCount} enabled recipe ROI(s).");
        result.Evidence.Add($"Maximum ROI difference score: {maxScore:F1}%.");

        if (result.Defects.Count == 0)
        {
            result.Verdict = "OK";
            result.SuggestedDefect = "No Recipe ROI Difference Above Threshold";
            result.Confidence = ComputeConfidence("OK", maxScore, result.ReviewThreshold, result.NgThreshold);
            result.DecisionMargin = result.ReviewThreshold - maxScore;
            result.DecisionReason = $"All {checkedCount} enabled recipe ROI(s) were below review threshold.";
            return;
        }

        var topDefect = result.Defects.OrderByDescending(defect => defect.Confidence).First();
        result.Verdict = result.Defects.Any(defect => defect.JudgmentStatus == "NG") ? "NG" : "REVIEW";
        result.SuggestedDefect = topDefect.DefectType;
        result.Confidence = result.Defects.Max(defect => defect.Confidence);
        result.DecisionMargin = result.Defects.Count;
        result.DecisionReason = $"{result.Defects.Count} recipe ROI(s) crossed review/NG threshold.";
        result.Evidence.Add($"Recipe ROI findings: {string.Join("; ", result.Defects.Select(d => $"{d.RoiId}/{d.RoiType}={d.JudgmentStatus} {d.Confidence:P0}"))}.");
    }

    private AnalysisResult CreateBaseResult(
        string samplePath,
        string? goldenPath,
        DetectionPriority priority,
        InspectionTiming timing,
        DateTime timestamp)
    {
        var result = new AnalysisResult
        {
            SchemaVersion = AnalysisResult.CurrentSchemaVersion,
            InspectionId = InspectionContractIds.NewInspectionId(timestamp.ToUniversalTime()),
            SamplePath = samplePath,
            GoldenPath = goldenPath,
            InspectionEngine = Name,
            ModelVersion = Version,
            MeanBrightness = 0,
            Timestamp = timestamp,
            SuggestedDefect = "Solder Bridge",
            Verdict = "REVIEW",
            DifferenceScore = 0,
            ReviewThreshold = 0,
            NgThreshold = 0,
            Confidence = 0.55,
            DecisionMargin = 0,
            DecisionReason = "Golden reference is required for differential judgement.",
            PolicyName = ToPolicyDisplay(priority),
            Hotspot = new Rect(0.45, 0.4, 0.14, 0.12),
            SourceKind = "File",
            SourceFrameId = Path.GetFileName(samplePath),
            IsSimulatedSource = false,
            Evidence = new List<string>
            {
                "No golden image was supplied; decision remains REVIEW by policy.",
                "Run comparison against a verified golden image for actionable classification.",
            },
            Timing = timing,
        };

        ApplyWorkflowContext(result);
        return result;
    }

    private static void ApplyWorkflowContext(AnalysisResult result)
    {
        try
        {
            var state = WorkflowState.Instance;
            result.StationId = state.StationId;
            result.BoardProgram = state.BoardProgram;
            result.BoardId = state.BoardProgram;
            result.OperatorId = state.OperatorWithRole;
        }
        catch
        {
        }
    }

    private static DefectResult CreateDefectResult(
        AnalysisResult result,
        string defectType,
        string roiId,
        int sequence,
        double imageWidthPixels,
        double imageHeightPixels)
        => CreateDefectResult(
            result,
            defectType,
            roiId,
            sequence,
            imageWidthPixels,
            imageHeightPixels,
            result.Hotspot,
            result.Confidence,
            result.Verdict);

    private static DefectResult CreateDefectResult(
        AnalysisResult result,
        string defectType,
        string roiId,
        int sequence,
        double imageWidthPixels,
        double imageHeightPixels,
        Rect boundingBox,
        double confidence,
        string judgmentStatus)
    {
        var box = boundingBox;
        var widthPixels = imageWidthPixels > 0 ? box.Width * imageWidthPixels : 0;
        var heightPixels = imageHeightPixels > 0 ? box.Height * imageHeightPixels : 0;
        return new DefectResult
        {
            DefectId = InspectionContractIds.NewDefectId(result.InspectionId, sequence),
            DefectType = defectType,
            Confidence = confidence,
            BoundingBox = box,
            XPosition = box.X + box.Width / 2.0,
            YPosition = box.Y + box.Height / 2.0,
            WidthPixels = widthPixels,
            HeightPixels = heightPixels,
            AreaPixels = widthPixels * heightPixels,
            Severity = ToDefectSeverity(judgmentStatus),
            SourceRoiId = roiId,
            SideOrViewType = result.ViewType,
            RoiId = roiId,
            JudgmentStatus = judgmentStatus,
        };
    }

    private static string ToDefectSeverity(string judgmentStatus)
        => judgmentStatus.ToUpperInvariant() switch
        {
            "NG" => "Major",
            "OK" => "Info",
            _ => "Review",
        };

    private static string ToPolicyDisplay(DetectionPriority priority) => priority switch
    {
        DetectionPriority.MinimizeFalsePositives => "Minimize False Positives",
        DetectionPriority.Balanced => "Balanced",
        DetectionPriority.MaximizeDefectRecall => "Maximize Defect Recall",
        _ => "Balanced",
    };

    private static double ComputeConfidence(string verdict, double diff, double reviewThreshold, double ngThreshold)
    {
        if (verdict == "NG")
        {
            var normalized = Math.Clamp((diff - ngThreshold) / Math.Max(2.0, ngThreshold * 0.5), 0, 1);
            return 0.72 + normalized * 0.27;
        }

        if (verdict == "OK")
        {
            var normalized = Math.Clamp((reviewThreshold - diff) / Math.Max(2.0, reviewThreshold * 0.6), 0, 1);
            return 0.68 + normalized * 0.3;
        }

        var mid = (reviewThreshold + ngThreshold) / 2.0;
        var halfBand = Math.Max(1.0, (ngThreshold - reviewThreshold) / 2.0);
        var centered = 1.0 - Math.Clamp(Math.Abs(diff - mid) / halfBand, 0, 1);
        return 0.52 + centered * 0.22;
    }

    private static List<string> BuildEvidence(AnalysisResult result, DetectionPriority priority)
    {
        return new List<string>
        {
            $"Difference score: {result.DifferenceScore:F1}% (Review >= {result.ReviewThreshold:F1}%, NG >= {result.NgThreshold:F1}%).",
            $"Threshold source: {result.ThresholdSource}.",
            $"Policy: {ToPolicyDisplay(priority)}.",
            $"Hotspot: x={result.Hotspot.X:P0}, y={result.Hotspot.Y:P0}, w={result.Hotspot.Width:P0}, h={result.Hotspot.Height:P0}.",
            $"Mean brightness (sample): {result.MeanBrightness:F1}.",
            $"Decision margin: {result.DecisionMargin:F2}.",
        };
    }

    private static (double ngThreshold, double reviewThreshold) GetThresholds(DetectionPriority priority)
    {
        return priority switch
        {
            DetectionPriority.MinimizeFalsePositives => (24, 12),
            DetectionPriority.Balanced => (18, 8),
            DetectionPriority.MaximizeDefectRecall => (14, 5),
            _ => (18, 8),
        };
    }

    private static (double ngThreshold, double reviewThreshold) GetRoiThresholds(DetectionPriority priority, RecipeRoi roi)
    {
        var (defaultNgThreshold, defaultReviewThreshold) = GetThresholds(priority);
        if (roi.Thresholds.AiScoreThreshold <= 0)
            return (defaultNgThreshold, defaultReviewThreshold);

        var roiReviewThreshold = Math.Clamp(roi.Thresholds.AiScoreThreshold * 100.0, 0.1, 100.0);
        roiReviewThreshold = Math.Min(defaultReviewThreshold, roiReviewThreshold);
        var roiNgThreshold = Math.Min(defaultNgThreshold, Math.Max(roiReviewThreshold, roiReviewThreshold * 2.0));
        return (roiNgThreshold, roiReviewThreshold);
    }

    private static EffectiveThresholdRule ResolveRoiThreshold(
        AnalysisResult result,
        RecipeDefinition recipe,
        DetectionPriority priority,
        RecipeRoi roi)
    {
        var profileRule = ThresholdProfileService.GetEffectiveThreshold(
            result.BoardId,
            recipe.BoardProgram,
            recipe.RecipeName,
            result.ViewType,
            roi.RoiType,
            roi.RoiType);
        if (profileRule is not null)
        {
            ApplyThresholdProfile(result, profileRule);
            result.Evidence.Add($"Threshold source for ROI {roi.RoiId}: Active threshold profile {profileRule.ProfileId}/{profileRule.Revision} (Review >= {profileRule.ReviewThreshold:F1}%, NG >= {profileRule.NgThreshold:F1}%).");
            return profileRule;
        }

        var (ngThreshold, reviewThreshold) = GetRoiThresholds(priority, roi);
        var source = roi.Thresholds.AiScoreThreshold > 0 ? "Recipe ROI threshold" : "Built-in policy default";
        result.ThresholdSource = source;
        result.Evidence.Add($"Threshold source for ROI {roi.RoiId}: {source} (Review >= {reviewThreshold:F1}%, NG >= {ngThreshold:F1}%).");
        return new EffectiveThresholdRule
        {
            Source = source,
            ReviewThreshold = reviewThreshold,
            NgThreshold = ngThreshold,
            ConfidenceThreshold = Math.Clamp(reviewThreshold / 100.0, 0.0, 1.0),
        };
    }

    private static void ApplyThresholdProfile(AnalysisResult result, EffectiveThresholdRule threshold)
    {
        result.ThresholdSource = threshold.Source;
        result.ThresholdProfileId = threshold.ProfileId;
        result.ThresholdProfileRevision = threshold.Revision;
        result.ReviewThreshold = threshold.ReviewThreshold;
        result.NgThreshold = threshold.NgThreshold;
        result.ConfidenceThreshold = threshold.ConfidenceThreshold;
    }

    private static void AppendRecipeWarnings(AnalysisResult result, RecipeLoadResult recipeLoad)
    {
        foreach (var warning in recipeLoad.Warnings)
            result.Evidence.Add($"Recipe warning: {warning}");
    }

    private static BitmapSource? LoadBgra32(string path)
    {
        if (!File.Exists(path)) return null;

        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.UriSource = new Uri(path, UriKind.Absolute);
        bmp.EndInit();
        bmp.Freeze();

        if (bmp.Format == PixelFormats.Bgra32)
            return bmp;

        var converted = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        return converted;
    }

    private static BitmapSource Resize(BitmapSource source, int maxW, int maxH)
    {
        var scale = Math.Min(maxW / (double)source.PixelWidth, maxH / (double)source.PixelHeight);
        scale = Math.Min(1.0, scale);

        var transform = new ScaleTransform(scale, scale);
        var resized = new TransformedBitmap(source, transform);
        resized.Freeze();

        if (resized.Format == PixelFormats.Bgra32)
            return resized;

        var converted = new FormatConvertedBitmap(resized, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        return converted;
    }

    private static double CalculateBrightness(BitmapSource src)
    {
        var stride = src.PixelWidth * 4;
        var count = stride * src.PixelHeight;
        var pool = ArrayPool<byte>.Shared;
        var pixels = pool.Rent(count);

        try
        {
            src.CopyPixels(pixels, stride, 0);

            double sum = 0;
            for (int i = 0; i < count; i += 4)
            {
                sum += 0.114 * pixels[i] + 0.587 * pixels[i + 1] + 0.299 * pixels[i + 2];
            }

            var n = count / 4.0;
            return n == 0 ? 0 : sum / n;
        }
        finally
        {
            pool.Return(pixels);
        }
    }

    private static double Compare(BitmapSource a, BitmapSource b, out Rect hotspot)
    {
        var w = Math.Min(a.PixelWidth, b.PixelWidth);
        var h = Math.Min(a.PixelHeight, b.PixelHeight);

        var stride = w * 4;
        var count = stride * h;
        var pool = ArrayPool<byte>.Shared;
        var pa = pool.Rent(count);
        var pb = pool.Rent(count);

        try
        {
            var ra = new CroppedBitmap(a, new Int32Rect(0, 0, w, h));
            var rb = new CroppedBitmap(b, new Int32Rect(0, 0, w, h));
            ra.CopyPixels(pa, stride, 0);
            rb.CopyPixels(pb, stride, 0);

            double total = 0;
            const int gridX = 8;
            const int gridY = 8;
            var bins = new double[gridX * gridY];
            int cw = Math.Max(1, w / gridX);
            int ch = Math.Max(1, h / gridY);

            for (int y = 0; y < h; y++)
            {
                int gy = Math.Min(gridY - 1, y / ch);
                int row = y * stride;
                for (int x = 0; x < w; x++)
                {
                    int gx = Math.Min(gridX - 1, x / cw);
                    int i = row + x * 4;

                    double dr = Math.Abs(pa[i + 2] - pb[i + 2]);
                    double dg = Math.Abs(pa[i + 1] - pb[i + 1]);
                    double db = Math.Abs(pa[i] - pb[i]);
                    double d = (dr + dg + db) / 3.0;

                    total += d;
                    bins[gy * gridX + gx] += d;
                }
            }

            int idx = 0;
            double best = double.MinValue;
            for (int i = 0; i < bins.Length; i++)
            {
                if (bins[i] > best)
                {
                    best = bins[i];
                    idx = i;
                }
            }

            int bx = idx % gridX;
            int by = idx / gridX;
            hotspot = new Rect(
                bx / (double)gridX,
                by / (double)gridY,
                1.0 / gridX,
                1.0 / gridY);

            var mad = total / (w * h);
            return Math.Min(100.0, mad / 255.0 * 100.0);
        }
        finally
        {
            pool.Return(pa);
            pool.Return(pb);
        }
    }

    private static double CompareRegion(BitmapSource a, BitmapSource b, Rect normalizedRegion)
    {
        var w = Math.Min(a.PixelWidth, b.PixelWidth);
        var h = Math.Min(a.PixelHeight, b.PixelHeight);
        if (w <= 0 || h <= 0)
            return 0;

        var left = Math.Clamp((int)Math.Floor(normalizedRegion.X * w), 0, w - 1);
        var top = Math.Clamp((int)Math.Floor(normalizedRegion.Y * h), 0, h - 1);
        var right = Math.Clamp((int)Math.Ceiling((normalizedRegion.X + normalizedRegion.Width) * w), left + 1, w);
        var bottom = Math.Clamp((int)Math.Ceiling((normalizedRegion.Y + normalizedRegion.Height) * h), top + 1, h);
        var regionWidth = right - left;
        var regionHeight = bottom - top;
        var fullStride = w * 4;
        var count = fullStride * h;
        var pool = ArrayPool<byte>.Shared;
        var pa = pool.Rent(count);
        var pb = pool.Rent(count);

        try
        {
            var ra = new CroppedBitmap(a, new Int32Rect(0, 0, w, h));
            var rb = new CroppedBitmap(b, new Int32Rect(0, 0, w, h));
            ra.CopyPixels(pa, fullStride, 0);
            rb.CopyPixels(pb, fullStride, 0);

            double total = 0;
            for (var y = top; y < bottom; y++)
            {
                var row = y * fullStride;
                for (var x = left; x < right; x++)
                {
                    var i = row + x * 4;
                    var dr = Math.Abs(pa[i + 2] - pb[i + 2]);
                    var dg = Math.Abs(pa[i + 1] - pb[i + 1]);
                    var db = Math.Abs(pa[i] - pb[i]);
                    total += (dr + dg + db) / 3.0;
                }
            }

            var mad = total / (regionWidth * regionHeight);
            return Math.Min(100.0, mad / 255.0 * 100.0);
        }
        finally
        {
            pool.Return(pa);
            pool.Return(pb);
        }
    }
}
