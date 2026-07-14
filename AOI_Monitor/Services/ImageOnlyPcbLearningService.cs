using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AOI_Monitor.Data;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public static class ImageOnlyPcbLearningService
{
    public const string EngineName = "Learned PCB Visual Model v1";
    public const string EngineVersion = "LPCB_VISUAL_1.0";

    // Legacy Gray8 tolerance artifacts (models trained before Gray16 persistence) used this coarse scale.
    private const double ToleranceArtifactScale = 16.0;
    private const double ToleranceArtifactScaleGray16 = 256.0;
    private static readonly JsonSerializerOptions SummaryJsonOptions = new()
    {
        WriteIndented = true,
    };

    public static ImageOnlyPcbLearningResult TrainProject(
        string projectId,
        ImageOnlyPcbLearningOptions? options = null,
        string createdBy = "UNKNOWN")
    {
        var normalizedOptions = NormalizeOptions(options);
        var project = AoiDatabase.GetImageLearningProject(projectId)
            ?? throw new InvalidOperationException("Image-only learning project does not exist.");
        var validation = ImageLearningProjectService.ValidateMinimumProjectRequirements(projectId);
        if (!validation.CanTrain)
            throw new InvalidOperationException(string.Join(" ", validation.BlockingIssues));

        var goldenImages = ImageLearningProjectService.GetProjectImages(projectId, ImageLearningImageRole.GoldenReference);
        var okLearningImages = ImageLearningProjectService.GetProjectImages(projectId, ImageLearningImageRole.OkLearning);
        var okValidationImages = ImageLearningProjectService.GetProjectImages(projectId, ImageLearningImageRole.OkValidation);
        var ngValidationImages = ImageLearningProjectService.GetProjectImages(projectId, ImageLearningImageRole.NgValidation);
        var skippedImages = new List<ImageLearningSkippedImage>();

        var trainingImages = LoadImages(
            goldenImages.Concat(okLearningImages),
            normalizedOptions,
            skippedImages);
        if (trainingImages.Count == 0)
            throw new InvalidOperationException("Training requires at least one readable Golden / Reference or OK Learning image.");

        var referenceSeed = trainingImages.FirstOrDefault(image => image.Source.Role == ImageLearningImageRole.GoldenReference)
            ?? trainingImages[0];
        var alignedTraining = new List<ImageMatrix>();
        var alignmentRecords = new List<ImageLearningAlignmentRecord>();

        foreach (var image in trainingImages)
        {
            var alignment = ReferenceEquals(image, referenceSeed)
                ? new AlignmentOffset(0, 0, 0)
                : FindAlignment(referenceSeed.Matrix, image.Matrix, normalizedOptions.AlignmentSearchRadiusPixels, normalizedOptions.RotationSearchDegrees);
            var aligned = ApplyRigidTransform(image.Matrix, referenceSeed.Matrix, alignment.OffsetX, alignment.OffsetY, alignment.AngleDegrees);
            alignedTraining.Add(aligned);
            alignmentRecords.Add(new ImageLearningAlignmentRecord(
                image.Source.Id,
                image.Source.FileName,
                image.Source.Role,
                alignment.OffsetX,
                alignment.OffsetY,
                alignment.Error,
                "OK",
                "Aligned to the image-only learning reference.",
                alignment.AngleDegrees));
        }

        var reference = BuildMeanImage(alignedTraining);
        var tolerance = BuildToleranceImage(alignedTraining, reference, normalizedOptions.MinimumTolerance, normalizedOptions.ToleranceShrinkagePseudoCount);
        var package = new LearnedVisualModelPackage(
            new LearnedPcbVisualModel
            {
                ModelId = NewModelId(),
                ModelVersion = EngineName,
                CreatedAtUtc = DateTime.UtcNow,
                ProjectId = project.ProjectId,
                GoldenCount = trainingImages.Count(image => image.Source.Role == ImageLearningImageRole.GoldenReference),
                OkLearningCount = trainingImages.Count(image => image.Source.Role == ImageLearningImageRole.OkLearning),
                InputWidth = normalizedOptions.InputWidth,
                InputHeight = normalizedOptions.InputHeight,
                AlignmentMode = normalizedOptions.RotationSearchDegrees > 0
                    ? $"DownscaledRigidSearch(+/-{normalizedOptions.AlignmentSearchRadiusPixels}px, +/-{normalizedOptions.RotationSearchDegrees:F1}deg)"
                    : $"DownscaledTranslationSearch(+/-{normalizedOptions.AlignmentSearchRadiusPixels}px)",
                BrightnessNormalizationMode = BuildPreprocessingMode(normalizedOptions),
                FalseCallTarget = normalizedOptions.FalseCallTarget,
                EvidenceMode = project.EvidenceMode,
                CreatedBy = string.IsNullOrWhiteSpace(createdBy) ? "UNKNOWN" : createdBy.Trim(),
            },
            reference,
            tolerance,
            normalizedOptions,
            string.Empty);

        var artifactFolder = CreateArtifactFolder(project.ProjectId, package.Model.ModelId);
        package = package with { ArtifactFolder = artifactFolder };
        WriteLearnedImageArtifacts(package);

        // Calibrate against the artifacts reloaded from disk so the measured false-call and
        // possible-escape rates hold at inspection time, which always evaluates the persisted model.
        var deployedPackage = package with
        {
            Reference = LoadImage(Path.Combine(artifactFolder, "learned_reference.png"), normalizedOptions, normalizeBrightness: false).Matrix,
            Tolerance = ReadToleranceArtifact(Path.Combine(artifactFolder, "tolerance_map.png"), normalizedOptions),
        };
        var okValidation = LoadImages(okValidationImages, normalizedOptions, skippedImages);
        var ngValidation = LoadImages(ngValidationImages, normalizedOptions, skippedImages);
        var calibration = Calibrate(project.ProjectId, deployedPackage, okValidation, ngValidation, normalizedOptions);

        package.Model.OkValidationCount = okValidation.Count;
        package.Model.LearnedThreshold = calibration.Result.LearnedThreshold;
        package.Model.FalseCallRate = calibration.Result.FalseCallRate;
        package.Model.PossibleEscapeRate = calibration.Result.PossibleEscapeRate;

        WriteCalibrationArtifacts(package, calibration, alignmentRecords, skippedImages);
        package.Model.Artifacts = CreateArtifactRecords(package.Model.ModelId, artifactFolder).ToList();

        AoiDatabase.RecordLearnedPcbVisualModel(package.Model);
        AoiDatabase.RecordImageLearningCalibrationResult(calibration.Result);

        var savedModel = AoiDatabase.GetLearnedPcbVisualModel(package.Model.ModelId) ?? package.Model;
        var savedCalibration = AoiDatabase.GetImageLearningCalibrationResult(calibration.Result.CalibrationId) ?? calibration.Result;
        return new ImageOnlyPcbLearningResult(
            savedModel,
            savedCalibration,
            artifactFolder,
            alignmentRecords,
            skippedImages,
            calibration.Sweep,
            BuildLearningWarnings(skippedImages, calibration.Result));
    }

    public static ImageOnlyPcbInspectionResult InspectProjectImage(
        string modelId,
        ImageLearningProjectImage image,
        ImageOnlyPcbLearningOptions? options = null,
        string operatorId = "UNKNOWN")
    {
        ArgumentNullException.ThrowIfNull(image);
        return InspectImagePath(modelId, image.VaultPath, options, operatorId, image, persist: true);
    }

    public static ImageOnlyPcbInspectionResult InspectImagePath(
        string modelId,
        string imagePath,
        ImageOnlyPcbLearningOptions? options = null,
        string operatorId = "UNKNOWN",
        ImageLearningProjectImage? projectImage = null,
        bool persist = false)
    {
        var package = LoadModelPackage(modelId, options);
        var normalizedOptions = NormalizeOptions(options, package.Model.InputWidth, package.Model.InputHeight);
        ApplyModelPreprocessingMode(normalizedOptions, package.Model);
        var image = LoadImage(imagePath, normalizedOptions);
        var evaluation = Evaluate(package, image.Matrix, normalizedOptions);
        var projectId = projectImage?.ProjectId ?? package.Model.ProjectId;
        var imageSha = projectImage?.Sha256 ?? ComputeSha256IfReadable(imagePath);
        var inspectionResult = new ImageLearningInspectionResult
        {
            ResultId = NewResultId(),
            ProjectId = projectId,
            ModelId = package.Model.ModelId,
            ProjectImageId = projectImage?.Id ?? 0,
            ImageSha256 = imageSha,
            ImagePath = imagePath,
            Verdict = evaluation.Verdict,
            AnomalyScore = evaluation.Score,
            DecisionReason = evaluation.DecisionReason,
            OperatorId = string.IsNullOrWhiteSpace(operatorId) ? "UNKNOWN" : operatorId.Trim(),
            EvidenceMode = package.Model.EvidenceMode,
            AnomalyRegions = evaluation.Regions,
        };
        var comparison = new ImageLearningComparisonResult
        {
            ComparisonId = NewComparisonId(),
            ProjectId = projectId,
            ModelId = package.Model.ModelId,
            ProjectImageId = projectImage?.Id ?? 0,
            ImageSha256 = imageSha,
            DifferenceScore = evaluation.RawDifferencePercent,
            AnomalyScore = evaluation.Score,
            Verdict = evaluation.Verdict,
            Summary = $"Image-only anomaly score {evaluation.Score:F2}; learned threshold {package.Model.LearnedThreshold:F2}.",
        };

        if (persist)
        {
            if (projectImage is null)
                throw new InvalidOperationException("Persisted image-only inspections require project image metadata.");

            var resultId = AoiDatabase.RecordImageLearningInspectionResult(inspectionResult);
            AoiDatabase.RecordImageLearningComparisonResult(comparison);
            inspectionResult = AoiDatabase.GetImageLearningInspectionResult(resultId) ?? inspectionResult;
        }

        return new ImageOnlyPcbInspectionResult(
            inspectionResult,
            comparison,
            inspectionResult.AnomalyRegions,
            evaluation.Alignment.OffsetX,
            evaluation.Alignment.OffsetY,
            evaluation.Alignment.AngleDegrees);
    }

    public static double CalculateRawDifferencePercentForTest(string firstImagePath, string secondImagePath, ImageOnlyPcbLearningOptions? options = null)
    {
        var normalizedOptions = NormalizeOptions(options);
        var first = LoadImage(firstImagePath, normalizedOptions);
        var second = LoadImage(secondImagePath, normalizedOptions);
        return CalculateRawDifferencePercent(first.Matrix, second.Matrix);
    }

    /// <summary>
    /// Minimum OK Validation images before the calibration/held-out split activates. Below this,
    /// both halves would be too small to be meaningful, so the pre-split behavior is retained and
    /// the reported rate is explicitly labeled in-sample.
    /// </summary>
    public const int MinimumOkValidationForHoldOut = 10;

    private static CalibrationComputation Calibrate(
        string projectId,
        LearnedVisualModelPackage package,
        IReadOnlyList<LoadedProjectImage> okValidationImages,
        IReadOnlyList<LoadedProjectImage> ngValidationImages,
        ImageOnlyPcbLearningOptions options)
    {
        var okScores = okValidationImages
            .Select(image => Evaluate(package, image.Matrix, options).Score)
            .ToArray();
        var ngScores = ngValidationImages
            .Select(image => Evaluate(package, image.Matrix, options).Score)
            .ToArray();

        // Held-out split: selecting the threshold on the same images the false-call rate is
        // reported on biases the headline number optimistic. With enough OK Validation images,
        // calibrate on the even-index half (deterministic; image order is the stable DB order)
        // and measure the reported rate on the untouched odd-index half. NG images stay whole:
        // they drive the possible-escape guardrail, not an unbiased rate estimate.
        var useHoldOut = okScores.Length >= MinimumOkValidationForHoldOut;
        var calibrationScores = useHoldOut ? okScores.Where((_, index) => index % 2 == 0).ToArray() : okScores;
        var heldOutScores = useHoldOut ? okScores.Where((_, index) => index % 2 == 1).ToArray() : Array.Empty<double>();
        var sweep = BuildThresholdSweep(calibrationScores, ngScores, options);

        if (okScores.Length == 0)
        {
            // Without OK Validation scores every sweep row trivially meets the false-call target,
            // so an aggressive minimum threshold must never be selected for deployment.
            var retainedThreshold = options.DefaultLearnedThreshold;
            var retainedEscapes = ngScores.Count(score => score < retainedThreshold);
            var retainedEscapeRate = ngScores.Length == 0 ? 0 : retainedEscapes / (double)ngScores.Length;
            return new CalibrationComputation(
                new ImageLearningCalibrationResult
                {
                    CalibrationId = NewCalibrationId(),
                    ProjectId = projectId,
                    ModelId = package.Model.ModelId,
                    OkValidationCount = 0,
                    NgValidationCount = ngScores.Length,
                    LearnedThreshold = retainedThreshold,
                    FalseCallTarget = options.FalseCallTarget,
                    FalseCallRate = 0,
                    PossibleEscapeRate = retainedEscapeRate,
                    Status = "REVIEW",
                    Summary = $"No readable OK Validation images were available; the default learned threshold {retainedThreshold:F2} was retained. False-call calibration is pending until OK Validation images are added and the model is rebuilt.",
                },
                sweep);
        }

        var selected = SelectThreshold(sweep, options);
        var heldOutFalseCalls = heldOutScores.Count(score => score >= selected.Threshold);
        double? heldOutRate = useHoldOut ? heldOutFalseCalls / (double)heldOutScores.Length : null;
        var heldOutMeetsTarget = heldOutRate is not { } rate || rate <= options.FalseCallTarget;
        var status = selected.MeetsFalseCallTarget && selected.MeetsPossibleEscapeLimit && heldOutMeetsTarget
            ? "OK"
            : "REVIEW";
        var summary = useHoldOut
            ? $"Selected threshold {selected.Threshold:F2} on {calibrationScores.Length} calibration image(s); held-out false-call rate {heldOutRate:P1} ({heldOutFalseCalls} of {heldOutScores.Length} held-out image(s)); possible escape rate {selected.PossibleEscapeRate:P1}."
            : $"Selected threshold {selected.Threshold:F2}; false-call rate {selected.FalseCallRate:P1} (in-sample; add OK Validation images to reach {MinimumOkValidationForHoldOut} for a held-out estimate); possible escape rate {selected.PossibleEscapeRate:P1}.";

        return new CalibrationComputation(
            new ImageLearningCalibrationResult
            {
                CalibrationId = NewCalibrationId(),
                ProjectId = projectId,
                ModelId = package.Model.ModelId,
                OkValidationCount = okScores.Length,
                NgValidationCount = ngScores.Length,
                LearnedThreshold = selected.Threshold,
                FalseCallTarget = options.FalseCallTarget,
                // The headline rate is the held-out estimate when available: it is the number
                // free of threshold-selection bias. Small sets fall back to the in-sample rate.
                FalseCallRate = heldOutRate ?? selected.FalseCallRate,
                PossibleEscapeRate = selected.PossibleEscapeRate,
                Status = status,
                Summary = summary,
                HeldOutOkCount = heldOutScores.Length,
                HeldOutFalseCalls = heldOutFalseCalls,
                HeldOutFalseCallRate = heldOutRate,
            },
            sweep);
    }

    private static IReadOnlyList<ImageLearningThresholdSweepRow> BuildThresholdSweep(
        IReadOnlyList<double> okScores,
        IReadOnlyList<double> ngScores,
        ImageOnlyPcbLearningOptions options)
    {
        var candidates = new SortedSet<double>();
        var maxObserved = Math.Max(
            okScores.Count == 0 ? options.DefaultLearnedThreshold : okScores.Max(),
            ngScores.Count == 0 ? options.DefaultLearnedThreshold : ngScores.Max());
        var maxCandidate = Math.Max(20.0, maxObserved + 2.0);
        for (var threshold = 0.5; threshold <= maxCandidate; threshold += 0.25)
            candidates.Add(Math.Round(threshold, 4));
        candidates.Add(Math.Round(options.DefaultLearnedThreshold, 4));
        foreach (var score in okScores.Concat(ngScores))
        {
            candidates.Add(Math.Round(Math.Max(0.1, score), 4));
            candidates.Add(Math.Round(Math.Max(0.1, score + 0.001), 4));
        }

        return candidates
            .Select(threshold =>
            {
                var okFalseCalls = okScores.Count(score => score >= threshold);
                var falseCallRate = okScores.Count == 0 ? 0 : okFalseCalls / (double)okScores.Count;
                var possibleEscapes = ngScores.Count(score => score < threshold);
                var possibleEscapeRate = ngScores.Count == 0 ? 0 : possibleEscapes / (double)ngScores.Count;
                return new ImageLearningThresholdSweepRow(
                    threshold,
                    okFalseCalls,
                    okScores.Count,
                    falseCallRate,
                    possibleEscapes,
                    ngScores.Count,
                    possibleEscapeRate,
                    falseCallRate <= options.FalseCallTarget,
                    possibleEscapeRate <= options.MaxAllowedPossibleEscapeRate);
            })
            .ToArray();
    }

    private static ImageLearningThresholdSweepRow SelectThreshold(
        IReadOnlyList<ImageLearningThresholdSweepRow> sweep,
        ImageOnlyPcbLearningOptions options)
    {
        return sweep
            .Where(row => row.MeetsFalseCallTarget && row.MeetsPossibleEscapeLimit)
            .OrderBy(row => row.Threshold)
            .FirstOrDefault()
            ?? sweep
                .Where(row => row.MeetsPossibleEscapeLimit)
                .OrderBy(row => row.FalseCallRate)
                .ThenBy(row => row.Threshold)
                .FirstOrDefault()
            ?? sweep
                .OrderBy(row => row.PossibleEscapeRate)
                .ThenBy(row => row.FalseCallRate)
                .ThenBy(row => Math.Abs(row.Threshold - options.DefaultLearnedThreshold))
                .First();
    }

    private static EvaluationResult Evaluate(
        LearnedVisualModelPackage package,
        ImageMatrix testImage,
        ImageOnlyPcbLearningOptions options)
    {
        var alignment = FindAlignment(package.Reference, testImage, options.AlignmentSearchRadiusPixels, options.RotationSearchDegrees);
        var aligned = ApplyRigidTransform(testImage, package.Reference, alignment.OffsetX, alignment.OffsetY, alignment.AngleDegrees);
        var scoreMap = new double[aligned.Pixels.Length];
        var rawDifference = 0.0;

        for (var i = 0; i < aligned.Pixels.Length; i++)
        {
            var diff = Math.Abs(aligned.Pixels[i] - package.Reference.Pixels[i]);
            rawDifference += diff;
            scoreMap[i] = diff / Math.Max(options.MinimumTolerance, package.Tolerance.Pixels[i]);
        }

        var smoothed = Smooth(scoreMap, aligned.Width, aligned.Height);
        // The image score must not depend on the deployed threshold, otherwise calibration
        // (measured before the threshold exists) and runtime inspection would score the same
        // image differently and the calibrated false-call rate would not hold.
        var score = ComputeAnomalyScore(smoothed, options.MinimumAnomalyAreaPixels);
        var threshold = package.Model.LearnedThreshold > 0
            ? package.Model.LearnedThreshold
            : options.DefaultLearnedThreshold;
        var regions = FindAnomalyRegions(smoothed, aligned.Width, aligned.Height, threshold, options.MinimumAnomalyAreaPixels);
        var verdict = ToVerdict(score, threshold);
        var reason = verdict switch
        {
            "OK" => "Image-only anomaly score is below the learned threshold.",
            "NG" => "Image-only anomaly score exceeds the learned NG band.",
            _ => "Image-only anomaly score is close to the learned threshold; review is required.",
        };

        return new EvaluationResult(
            score,
            rawDifference / aligned.Pixels.Length / 255.0 * 100.0,
            verdict,
            reason,
            regions,
            alignment);
    }

    private static double ComputeAnomalyScore(double[] smoothed, int minimumAreaPixels)
    {
        if (smoothed.Length == 0)
            return 0;

        // Score = the k-th largest smoothed normalized difference, where k is the minimum
        // anomaly area. This is the strongest level at which at least `minimumAreaPixels`
        // pixels are anomalous, and it is independent of any decision threshold, so calibration
        // (run before the threshold exists) and runtime inspection score an image identically.
        var k = Math.Clamp(minimumAreaPixels, 1, smoothed.Length);
        return KthLargest(smoothed, k);
    }

    private static double KthLargest(double[] values, int k)
    {
        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        return sorted[sorted.Length - k];
    }

    private static List<ImageLearningAnomalyRegion> FindAnomalyRegions(
        double[] scoreMap,
        int width,
        int height,
        double threshold,
        int minimumAreaPixels)
    {
        var visited = new bool[scoreMap.Length];
        var regions = new List<ImageLearningAnomalyRegion>();
        var queue = new Queue<int>();
        var regionNumber = 1;

        for (var index = 0; index < scoreMap.Length; index++)
        {
            if (visited[index] || scoreMap[index] < threshold)
                continue;

            visited[index] = true;
            queue.Enqueue(index);
            var minX = width;
            var minY = height;
            var maxX = 0;
            var maxY = 0;
            var area = 0;
            var maxScore = 0.0;
            var sumScore = 0.0;

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                var x = current % width;
                var y = current / width;
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
                area++;
                maxScore = Math.Max(maxScore, scoreMap[current]);
                sumScore += scoreMap[current];

                EnqueueIfAnomaly(x - 1, y);
                EnqueueIfAnomaly(x + 1, y);
                EnqueueIfAnomaly(x, y - 1);
                EnqueueIfAnomaly(x, y + 1);
            }

            if (area < minimumAreaPixels)
                continue;

            var score = Math.Max(maxScore, sumScore / area);
            regions.Add(new ImageLearningAnomalyRegion
            {
                RegionId = $"ILA-{regionNumber++:000}",
                X = minX / (double)width,
                Y = minY / (double)height,
                Width = (maxX - minX + 1) / (double)width,
                Height = (maxY - minY + 1) / (double)height,
                Score = score,
                AreaPixels = area,
                Confidence = Math.Clamp(0.55 + (score - threshold) / Math.Max(threshold * 2.0, 1.0), 0.55, 0.99),
                Severity = score >= threshold * 1.25 ? "NG" : "REVIEW",
                RegionType = "VisualAnomaly",
                Reason = "Localized image-only difference exceeded the learned tolerance.",
                Notes = "Generated without manual defect-class variables or training bounding boxes.",
            });
        }

        return regions
            .OrderByDescending(region => region.Score)
            .ThenByDescending(region => region.AreaPixels)
            .Take(50)
            .ToList();

        void EnqueueIfAnomaly(int x, int y)
        {
            if (x < 0 || y < 0 || x >= width || y >= height)
                return;

            var next = y * width + x;
            if (visited[next] || scoreMap[next] < threshold)
                return;

            visited[next] = true;
            queue.Enqueue(next);
        }
    }

    private static double[] Smooth(double[] source, int width, int height)
    {
        var smoothed = new double[source.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var sum = 0.0;
                var count = 0;
                for (var yy = Math.Max(0, y - 1); yy <= Math.Min(height - 1, y + 1); yy++)
                {
                    for (var xx = Math.Max(0, x - 1); xx <= Math.Min(width - 1, x + 1); xx++)
                    {
                        sum += source[yy * width + xx];
                        count++;
                    }
                }

                smoothed[y * width + x] = sum / count;
            }
        }

        return smoothed;
    }

    private static string ToVerdict(double score, double threshold)
    {
        if (score < threshold)
            return "OK";
        if (score < threshold * 1.25)
            return "REVIEW";
        return "NG";
    }

    private static IReadOnlyList<LoadedProjectImage> LoadImages(
        IEnumerable<ImageLearningProjectImage> images,
        ImageOnlyPcbLearningOptions options,
        List<ImageLearningSkippedImage> skippedImages)
    {
        var loaded = new List<LoadedProjectImage>();
        foreach (var image in images)
        {
            var path = string.IsNullOrWhiteSpace(image.VaultPath) ? image.OriginalPath : image.VaultPath;
            try
            {
                loaded.Add(LoadImage(path, options, image));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or FileFormatException or ArgumentException or System.Runtime.InteropServices.COMException)
            {
                skippedImages.Add(new ImageLearningSkippedImage(image.Id, image.FileName, image.Role, path, ex.Message));
            }
        }

        return loaded;
    }

    private static LoadedProjectImage LoadImage(
        string path,
        ImageOnlyPcbLearningOptions options,
        ImageLearningProjectImage? source = null,
        bool normalizeBrightness = true)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new InvalidDataException("Image file does not exist.");

        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0)
            throw new InvalidDataException("Image decoder found no frames.");

        var frame = decoder.Frames[0];
        BitmapSource converted = frame.Format == PixelFormats.Bgra32
            ? frame
            : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        converted.Freeze();

        var stride = converted.PixelWidth * 4;
        var count = stride * converted.PixelHeight;
        var pool = ArrayPool<byte>.Shared;
        var pixels = pool.Rent(count);
        try
        {
            converted.CopyPixels(pixels, stride, 0);
            var matrix = ResizeToGrayscale(pixels, converted.PixelWidth, converted.PixelHeight, options.InputWidth, options.InputHeight, options.UseBoxAverageResampling);
            // normalizeBrightness == false is the artifact-reload path (learned_reference.png is
            // already fully preprocessed), so illumination flattening is gated on the same flag.
            if (normalizeBrightness)
            {
                if (options.FlattenIllumination)
                    FlattenIllumination(matrix);
                NormalizeBrightness(matrix, options.TargetMeanBrightness);
            }
            return new LoadedProjectImage(
                source ?? new ImageLearningProjectImage
                {
                    FileName = Path.GetFileName(path),
                    VaultPath = path,
                    OriginalPath = path,
                },
                matrix);
        }
        finally
        {
            pool.Return(pixels);
        }
    }

    private static ImageMatrix ResizeToGrayscale(byte[] bgra, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight, bool useBoxAverage)
    {
        var output = new double[targetWidth * targetHeight];
        var scaleX = sourceWidth / (double)targetWidth;
        var scaleY = sourceHeight / (double)targetHeight;
        var downscaling = sourceWidth > targetWidth || sourceHeight > targetHeight;

        if (useBoxAverage && downscaling)
        {
            // Area-average decimation: each target pixel is the mean luma of its source cell.
            // Point sampling would flip every target pixel's source under half-pixel physical
            // motion, injecting frame-to-frame sampling noise into the tolerance map.
            for (var y = 0; y < targetHeight; y++)
            {
                var y0 = Math.Clamp((int)(y * scaleY), 0, sourceHeight - 1);
                var y1 = Math.Clamp((int)Math.Ceiling((y + 1) * scaleY), y0 + 1, sourceHeight);
                for (var x = 0; x < targetWidth; x++)
                {
                    var x0 = Math.Clamp((int)(x * scaleX), 0, sourceWidth - 1);
                    var x1 = Math.Clamp((int)Math.Ceiling((x + 1) * scaleX), x0 + 1, sourceWidth);
                    var sum = 0.0;
                    for (var sy = y0; sy < y1; sy++)
                    {
                        var row = sy * sourceWidth;
                        for (var sx = x0; sx < x1; sx++)
                        {
                            var offset = (row + sx) * 4;
                            sum += (0.114 * bgra[offset]) + (0.587 * bgra[offset + 1]) + (0.299 * bgra[offset + 2]);
                        }
                    }

                    output[(y * targetWidth) + x] = sum / ((y1 - y0) * (x1 - x0));
                }
            }

            return new ImageMatrix(targetWidth, targetHeight, output);
        }

        for (var y = 0; y < targetHeight; y++)
        {
            var sourceY = Math.Clamp((int)((y + 0.5) * scaleY), 0, sourceHeight - 1);
            for (var x = 0; x < targetWidth; x++)
            {
                var sourceX = Math.Clamp((int)((x + 0.5) * scaleX), 0, sourceWidth - 1);
                var offset = (sourceY * sourceWidth + sourceX) * 4;
                output[y * targetWidth + x] = 0.114 * bgra[offset] + 0.587 * bgra[offset + 1] + 0.299 * bgra[offset + 2];
            }
        }

        return new ImageMatrix(targetWidth, targetHeight, output);
    }

    /// <summary>
    /// Kernel for local illumination estimation: much larger than the minimum anomaly area so
    /// real defects barely influence their own background estimate, always odd.
    /// </summary>
    private static int FlattenKernelSize(int width, int height)
    {
        var kernel = Math.Max(17, Math.Min(width, height) / 8);
        return kernel % 2 == 0 ? kernel + 1 : kernel;
    }

    /// <summary>
    /// Removes low-frequency illumination (lighting gradients, vignetting) by subtracting a
    /// large-kernel local mean and re-adding the global mean. Separable box blur, O(n).
    /// Defect-scale detail passes through nearly unchanged because the kernel is far larger
    /// than the minimum anomaly area.
    /// </summary>
    private static void FlattenIllumination(ImageMatrix matrix)
    {
        var width = matrix.Width;
        var height = matrix.Height;
        if (width < 3 || height < 3)
            return;

        var kernel = FlattenKernelSize(width, height);
        var half = kernel / 2;
        var globalMean = matrix.Pixels.Average();

        // Horizontal running-sum pass.
        var horizontal = new double[matrix.Pixels.Length];
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            var sum = 0.0;
            var count = 0;
            for (var x = 0; x < Math.Min(half + 1, width); x++)
            {
                sum += matrix.Pixels[row + x];
                count++;
            }

            for (var x = 0; x < width; x++)
            {
                horizontal[row + x] = sum / count;
                var addIndex = x + half + 1;
                if (addIndex < width)
                {
                    sum += matrix.Pixels[row + addIndex];
                    count++;
                }

                var removeIndex = x - half;
                if (removeIndex >= 0)
                {
                    sum -= matrix.Pixels[row + removeIndex];
                    count--;
                }
            }
        }

        // Vertical running-sum pass over the horizontal means, then flatten in place.
        for (var x = 0; x < width; x++)
        {
            var sum = 0.0;
            var count = 0;
            for (var y = 0; y < Math.Min(half + 1, height); y++)
            {
                sum += horizontal[(y * width) + x];
                count++;
            }

            for (var y = 0; y < height; y++)
            {
                var background = sum / count;
                var index = (y * width) + x;
                matrix.Pixels[index] = Math.Clamp(matrix.Pixels[index] - background + globalMean, 0.0, 255.0);

                var addIndex = y + half + 1;
                if (addIndex < height)
                {
                    sum += horizontal[(addIndex * width) + x];
                    count++;
                }

                var removeIndex = y - half;
                if (removeIndex >= 0)
                {
                    sum -= horizontal[(removeIndex * width) + x];
                    count--;
                }
            }
        }
    }

    private static void NormalizeBrightness(ImageMatrix matrix, double targetMeanBrightness)
    {
        var mean = matrix.Pixels.Length == 0 ? 0 : matrix.Pixels.Average();
        if (mean < 1.0)
            return;

        var scale = targetMeanBrightness / mean;
        for (var i = 0; i < matrix.Pixels.Length; i++)
            matrix.Pixels[i] = Math.Clamp(matrix.Pixels[i] * scale, 0.0, 255.0);
    }

    private static ImageMatrix BuildMeanImage(IReadOnlyList<ImageMatrix> images)
    {
        var width = images[0].Width;
        var height = images[0].Height;
        var reference = new double[width * height];
        foreach (var image in images)
        {
            for (var i = 0; i < reference.Length; i++)
                reference[i] += image.Pixels[i];
        }

        for (var i = 0; i < reference.Length; i++)
            reference[i] /= images.Count;

        return new ImageMatrix(width, height, reference);
    }

    private static ImageMatrix BuildToleranceImage(IReadOnlyList<ImageMatrix> images, ImageMatrix reference, double minimumTolerance, double shrinkagePseudoCount = 0)
    {
        var tolerance = new double[reference.Pixels.Length];
        if (images.Count <= 1)
        {
            Array.Fill(tolerance, minimumTolerance);
            return new ImageMatrix(reference.Width, reference.Height, tolerance);
        }

        foreach (var image in images)
        {
            for (var i = 0; i < tolerance.Length; i++)
            {
                var delta = image.Pixels[i] - reference.Pixels[i];
                tolerance[i] += delta * delta;
            }
        }

        var degreesOfFreedom = Math.Max(1, images.Count - 1);
        for (var i = 0; i < tolerance.Length; i++)
            tolerance[i] /= degreesOfFreedom;

        // Variance shrinkage: a per-pixel variance estimated from a handful of OK images is
        // itself noisy (5 samples -> roughly +/-50% on the stddev), so part of the tolerance map
        // is estimation noise rather than real process variation. Pool each pixel's variance
        // toward the global mean variance with `shrinkagePseudoCount` pseudo-observations
        // (James-Stein-style), stabilizing small-N maps; large sets are barely affected.
        if (shrinkagePseudoCount > 0)
        {
            var globalVariance = tolerance.Average();
            for (var i = 0; i < tolerance.Length; i++)
                tolerance[i] = ((degreesOfFreedom * tolerance[i]) + (shrinkagePseudoCount * globalVariance)) / (degreesOfFreedom + shrinkagePseudoCount);
        }

        for (var i = 0; i < tolerance.Length; i++)
            tolerance[i] = Math.Max(minimumTolerance, Math.Sqrt(tolerance[i]));

        // 1-px max dilation: alignment is integer-only, so a genuine sub-pixel offset leaves
        // razor-thin high-difference lines along high-contrast edges. Judging each pixel against
        // its neighborhood's tolerance (classic AOI edge relief) removes that false-call class;
        // escape exposure is limited to defects within 1 px of a high-variance edge.
        tolerance = DilateMax3x3(tolerance, reference.Width, reference.Height);

        return new ImageMatrix(reference.Width, reference.Height, tolerance);
    }

    private static double[] DilateMax3x3(double[] values, int width, int height)
    {
        var dilated = new double[values.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var best = double.MinValue;
                for (var dy = -1; dy <= 1; dy++)
                {
                    var ny = y + dy;
                    if (ny < 0 || ny >= height)
                        continue;

                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var nx = x + dx;
                        if (nx < 0 || nx >= width)
                            continue;

                        var value = values[ny * width + nx];
                        if (value > best)
                            best = value;
                    }
                }

                dilated[y * width + x] = best;
            }
        }

        return dilated;
    }

    private static AlignmentOffset FindAlignment(
        ImageMatrix reference,
        ImageMatrix candidate,
        int searchRadiusPixels,
        double rotationSearchDegrees = 0)
    {
        var radius = Math.Min(searchRadiusPixels, Math.Min(reference.Width, reference.Height) / 4);
        var sampleStep = Math.Max(1, Math.Max(reference.Width, reference.Height) / 96);
        var best = new AlignmentOffset(0, 0, double.PositiveInfinity);

        for (var yOffset = -radius; yOffset <= radius; yOffset++)
        {
            for (var xOffset = -radius; xOffset <= radius; xOffset++)
            {
                var error = AlignmentError(reference, candidate, xOffset, yOffset, sampleStep);
                if (error < best.Error - 0.000001 ||
                    (Math.Abs(error - best.Error) <= 0.000001 &&
                     Math.Abs(xOffset) + Math.Abs(yOffset) < Math.Abs(best.OffsetX) + Math.Abs(best.OffsetY)))
                {
                    best = new AlignmentOffset(xOffset, yOffset, error);
                }
            }
        }

        // Small-angle rotation search: fixtures and hand placement rotate boards slightly, and
        // translation-only alignment leaves that rotation as edge false-calls. Rotation and
        // translation are near-orthogonal for small angles (rotation is about the image center),
        // so each candidate angle only needs a narrow translation window around the 0-degree
        // winner rather than the full radius. Skipped for tiny images (rotation displacement is
        // sub-pixel there) so existing small-image behavior and tests stay bit-identical.
        var rotationEnabled = rotationSearchDegrees > 0 && Math.Min(reference.Width, reference.Height) >= 96;
        if (rotationEnabled)
        {
            for (var magnitude = 0.5; magnitude <= rotationSearchDegrees + 0.000001; magnitude += 0.5)
            {
                foreach (var angle in new[] { magnitude, -magnitude })
                {
                    var minYOffset = Math.Max(-radius, best.OffsetY - 4);
                    var maxYOffset = Math.Min(radius, best.OffsetY + 4);
                    var minXOffset = Math.Max(-radius, best.OffsetX - 4);
                    var maxXOffset = Math.Min(radius, best.OffsetX + 4);
                    for (var yOffset = minYOffset; yOffset <= maxYOffset; yOffset++)
                    {
                        for (var xOffset = minXOffset; xOffset <= maxXOffset; xOffset++)
                        {
                            var error = RigidAlignmentError(reference, candidate, xOffset, yOffset, angle, sampleStep);
                            // Strictly-better comparison: on ties the earlier (smaller-angle,
                            // ultimately 0-degree) candidate wins, keeping rotation honest.
                            if (error < best.Error - 0.000001)
                                best = new AlignmentOffset(xOffset, yOffset, error, angle);
                        }
                    }
                }
            }
        }

        // Coarse-to-fine refinement: the passes above score offsets on a sparse sampleStep pixel
        // grid, which can lock onto a slightly wrong offset when the sampled pixels land on
        // locally dominant texture. Re-score the +/-2 offset neighborhood (and, when rotation is
        // active, the +/-0.25-degree half-steps) of the winner at full resolution and return the
        // refined transform with its full-resolution error. Small images already ran at full
        // resolution, so refinement is skipped there and their behavior is unchanged.
        if (sampleStep > 1)
        {
            var refined = new AlignmentOffset(0, 0, double.PositiveInfinity);
            var angleCandidates = rotationEnabled && best.AngleDegrees != 0
                ? new[] { best.AngleDegrees, best.AngleDegrees - 0.25, best.AngleDegrees + 0.25 }
                : new[] { best.AngleDegrees };
            var minRefineY = Math.Max(-radius, best.OffsetY - 2);
            var maxRefineY = Math.Min(radius, best.OffsetY + 2);
            var minRefineX = Math.Max(-radius, best.OffsetX - 2);
            var maxRefineX = Math.Min(radius, best.OffsetX + 2);
            foreach (var angle in angleCandidates)
            {
                for (var yOffset = minRefineY; yOffset <= maxRefineY; yOffset++)
                {
                    for (var xOffset = minRefineX; xOffset <= maxRefineX; xOffset++)
                    {
                        var error = RigidAlignmentError(reference, candidate, xOffset, yOffset, angle, sampleStep: 1);
                        if (error < refined.Error - 0.000001 ||
                            (Math.Abs(error - refined.Error) <= 0.000001 &&
                             Math.Abs(xOffset) + Math.Abs(yOffset) < Math.Abs(refined.OffsetX) + Math.Abs(refined.OffsetY)))
                        {
                            refined = new AlignmentOffset(xOffset, yOffset, error, angle);
                        }
                    }
                }
            }

            best = refined;
        }

        return best;
    }

    /// <summary>
    /// Rigid (rotation + translation) alignment error. Zero rotation delegates to the exact
    /// integer-translation path so translation-only behavior stays bit-identical; non-zero
    /// rotation inverse-maps each sampled reference pixel through a rotation about the image
    /// center with bilinear sampling, matching <see cref="ApplyRigidTransform"/>.
    /// </summary>
    private static double RigidAlignmentError(
        ImageMatrix reference,
        ImageMatrix candidate,
        int offsetX,
        int offsetY,
        double angleDegrees,
        int sampleStep)
    {
        if (angleDegrees == 0)
            return AlignmentError(reference, candidate, offsetX, offsetY, sampleStep);

        var radians = angleDegrees * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        var centerX = (reference.Width - 1) / 2.0;
        var centerY = (reference.Height - 1) / 2.0;
        var sum = 0.0;
        var count = 0;

        for (var y = 0; y < reference.Height; y += sampleStep)
        {
            var relY = y - centerY;
            for (var x = 0; x < reference.Width; x += sampleStep)
            {
                var relX = x - centerX;
                var sourceX = (cos * relX) + (sin * relY) + centerX - offsetX;
                var sourceY = (-sin * relX) + (cos * relY) + centerY - offsetY;
                if (!TryBilinearSample(candidate, sourceX, sourceY, out var value))
                    continue;

                var delta = reference.Pixels[(y * reference.Width) + x] - value;
                sum += delta * delta;
                count++;
            }
        }

        return count == 0 ? double.PositiveInfinity : sum / count;
    }

    private static bool TryBilinearSample(ImageMatrix image, double x, double y, out double value)
    {
        value = 0;
        if (x < 0 || y < 0 || x > image.Width - 1 || y > image.Height - 1)
            return false;

        var x0 = (int)Math.Floor(x);
        var y0 = (int)Math.Floor(y);
        var x1 = Math.Min(x0 + 1, image.Width - 1);
        var y1 = Math.Min(y0 + 1, image.Height - 1);
        var fx = x - x0;
        var fy = y - y0;

        var topLeft = image.Pixels[(y0 * image.Width) + x0];
        var topRight = image.Pixels[(y0 * image.Width) + x1];
        var bottomLeft = image.Pixels[(y1 * image.Width) + x0];
        var bottomRight = image.Pixels[(y1 * image.Width) + x1];
        var top = topLeft + ((topRight - topLeft) * fx);
        var bottom = bottomLeft + ((bottomRight - bottomLeft) * fx);
        value = top + ((bottom - top) * fy);
        return true;
    }

    private static double AlignmentError(ImageMatrix reference, ImageMatrix candidate, int offsetX, int offsetY, int sampleStep)
    {
        var sum = 0.0;
        var count = 0;
        for (var y = 0; y < reference.Height; y += sampleStep)
        {
            var sourceY = y - offsetY;
            if (sourceY < 0 || sourceY >= candidate.Height)
                continue;

            for (var x = 0; x < reference.Width; x += sampleStep)
            {
                var sourceX = x - offsetX;
                if (sourceX < 0 || sourceX >= candidate.Width)
                    continue;

                var delta = reference.Pixels[y * reference.Width + x] - candidate.Pixels[sourceY * candidate.Width + sourceX];
                sum += delta * delta;
                count++;
            }
        }

        return count == 0 ? double.PositiveInfinity : sum / count;
    }

    /// <summary>
    /// Applies the recovered rigid transform (rotation about the image center + integer
    /// translation). Zero rotation delegates to the exact integer-translation path so
    /// translation-only alignment stays bit-identical with earlier models; non-zero rotation
    /// inverse-maps with bilinear sampling and falls back to reference pixels outside the frame.
    /// </summary>
    private static ImageMatrix ApplyRigidTransform(ImageMatrix source, ImageMatrix fallback, int offsetX, int offsetY, double angleDegrees)
    {
        if (angleDegrees == 0)
            return ApplyTranslation(source, fallback, offsetX, offsetY);

        var radians = angleDegrees * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        var centerX = (fallback.Width - 1) / 2.0;
        var centerY = (fallback.Height - 1) / 2.0;
        var output = new double[fallback.Pixels.Length];

        for (var y = 0; y < fallback.Height; y++)
        {
            var relY = y - centerY;
            for (var x = 0; x < fallback.Width; x++)
            {
                var relX = x - centerX;
                var sourceX = (cos * relX) + (sin * relY) + centerX - offsetX;
                var sourceY = (-sin * relX) + (cos * relY) + centerY - offsetY;
                output[(y * fallback.Width) + x] = TryBilinearSample(source, sourceX, sourceY, out var value)
                    ? value
                    : fallback.Pixels[(y * fallback.Width) + x];
            }
        }

        return new ImageMatrix(fallback.Width, fallback.Height, output);
    }

    private static ImageMatrix ApplyTranslation(ImageMatrix source, ImageMatrix fallback, int offsetX, int offsetY)
    {
        var output = new double[fallback.Pixels.Length];
        for (var y = 0; y < fallback.Height; y++)
        {
            var sourceY = y - offsetY;
            for (var x = 0; x < fallback.Width; x++)
            {
                var sourceX = x - offsetX;
                output[y * fallback.Width + x] =
                    sourceX >= 0 && sourceX < source.Width && sourceY >= 0 && sourceY < source.Height
                        ? source.Pixels[sourceY * source.Width + sourceX]
                        : fallback.Pixels[y * fallback.Width + x];
            }
        }

        return new ImageMatrix(fallback.Width, fallback.Height, output);
    }

    private static void WriteLearnedImageArtifacts(LearnedVisualModelPackage package)
    {
        Directory.CreateDirectory(package.ArtifactFolder);
        WriteGray8Png(Path.Combine(package.ArtifactFolder, "learned_reference.png"), package.Reference.Pixels, package.Reference.Width, package.Reference.Height);
        // Tolerance is persisted as 16-bit grayscale so the standard-deviation map survives the
        // disk round-trip losslessly (1/256 gray-level precision, no saturation), keeping runtime
        // anomaly scores aligned with the calibrated values.
        WriteGray16Png(
            Path.Combine(package.ArtifactFolder, "tolerance_map.png"),
            package.Tolerance.Pixels,
            package.Tolerance.Width,
            package.Tolerance.Height,
            ToleranceArtifactScaleGray16);
    }

    private static void WriteCalibrationArtifacts(
        LearnedVisualModelPackage package,
        CalibrationComputation calibration,
        IReadOnlyList<ImageLearningAlignmentRecord> alignments,
        IReadOnlyList<ImageLearningSkippedImage> skippedImages)
    {
        Directory.CreateDirectory(package.ArtifactFolder);
        WriteGray8Png(
            Path.Combine(package.ArtifactFolder, "anomaly_threshold_map.png"),
            BuildThresholdMap(package.Reference, package.Tolerance, calibration.Result.LearnedThreshold),
            package.Reference.Width,
            package.Reference.Height);
        File.WriteAllText(
            Path.Combine(package.ArtifactFolder, "learning_summary.json"),
            JsonSerializer.Serialize(BuildLearningSummary(package, calibration, skippedImages), SummaryJsonOptions),
            Encoding.UTF8);
        File.WriteAllText(Path.Combine(package.ArtifactFolder, "alignment_summary.csv"), BuildAlignmentCsv(alignments), Encoding.UTF8);
        File.WriteAllText(Path.Combine(package.ArtifactFolder, "threshold_sweep.csv"), BuildThresholdSweepCsv(calibration.Sweep), Encoding.UTF8);
    }

    private static object BuildLearningSummary(
        LearnedVisualModelPackage package,
        CalibrationComputation calibration,
        IReadOnlyList<ImageLearningSkippedImage> skippedImages)
        => new
        {
            engine = EngineName,
            engineVersion = EngineVersion,
            package.Model.ModelId,
            package.Model.ModelVersion,
            package.Model.ProjectId,
            package.Model.CreatedAtUtc,
            package.Model.InputWidth,
            package.Model.InputHeight,
            package.Model.AlignmentMode,
            package.Model.BrightnessNormalizationMode,
            package.Model.GoldenCount,
            package.Model.OkLearningCount,
            package.Model.OkValidationCount,
            calibration.Result.NgValidationCount,
            calibration.Result.LearnedThreshold,
            calibration.Result.FalseCallTarget,
            calibration.Result.FalseCallRate,
            calibration.Result.PossibleEscapeRate,
            calibration.Result.HeldOutOkCount,
            calibration.Result.HeldOutFalseCalls,
            calibration.Result.HeldOutFalseCallRate,
            calibration.Result.Status,
            toleranceArtifactScale = ToleranceArtifactScaleGray16,
            toleranceArtifactBitDepth = 16,
            toleranceDilation = "Max3x3",
            resamplingMode = package.Options.UseBoxAverageResampling ? "BoxAverage" : "NearestNeighbor",
            illuminationFlattening = package.Options.FlattenIllumination ? $"BoxBlur({FlattenKernelSize(package.Options.InputWidth, package.Options.InputHeight)})" : "Off",
            toleranceShrinkagePseudoCount = package.Options.ToleranceShrinkagePseudoCount,
            skippedImages,
            note = "Image-only learning uses OK images and optional image-level validation truth; no defect labels or training boxes are required.",
        };

    private static string BuildAlignmentCsv(IReadOnlyList<ImageLearningAlignmentRecord> alignments)
    {
        var builder = new StringBuilder();
        builder.AppendLine("projectImageId,fileName,role,offsetX,offsetY,rotationDegrees,alignmentError,status,message");
        foreach (var row in alignments)
        {
            builder.Append(row.ProjectImageId.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(EscapeCsv(row.FileName)).Append(',')
                .Append(row.Role).Append(',')
                .Append(row.OffsetX.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(row.OffsetY.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(row.RotationDegrees.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
                .Append(row.AlignmentError.ToString("G17", CultureInfo.InvariantCulture)).Append(',')
                .Append(EscapeCsv(row.Status)).Append(',')
                .Append(EscapeCsv(row.Message)).AppendLine();
        }

        return builder.ToString();
    }

    private static string BuildThresholdSweepCsv(IReadOnlyList<ImageLearningThresholdSweepRow> rows)
    {
        var builder = new StringBuilder();
        builder.AppendLine("threshold,okFalseCalls,okValidationCount,falseCallRate,ngPossibleEscapes,ngValidationCount,possibleEscapeRate,meetsFalseCallTarget,meetsPossibleEscapeLimit");
        foreach (var row in rows)
        {
            builder.Append(row.Threshold.ToString("G17", CultureInfo.InvariantCulture)).Append(',')
                .Append(row.OkFalseCalls.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(row.OkValidationCount.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(row.FalseCallRate.ToString("G17", CultureInfo.InvariantCulture)).Append(',')
                .Append(row.NgPossibleEscapes.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(row.NgValidationCount.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(row.PossibleEscapeRate.ToString("G17", CultureInfo.InvariantCulture)).Append(',')
                .Append(row.MeetsFalseCallTarget ? "true" : "false").Append(',')
                .Append(row.MeetsPossibleEscapeLimit ? "true" : "false").AppendLine();
        }

        return builder.ToString();
    }

    private static double[] BuildThresholdMap(ImageMatrix reference, ImageMatrix tolerance, double threshold)
    {
        var pixels = new double[reference.Pixels.Length];
        for (var i = 0; i < pixels.Length; i++)
            pixels[i] = Math.Clamp(reference.Pixels[i] + tolerance.Pixels[i] * threshold, 0.0, 255.0);

        return pixels;
    }

    private static void WriteGray8Png(string path, IReadOnlyList<double> pixels, int width, int height)
    {
        var bytes = new byte[pixels.Count];
        for (var i = 0; i < pixels.Count; i++)
            bytes[i] = (byte)Math.Clamp((int)Math.Round(pixels[i], MidpointRounding.AwayFromZero), 0, 255);

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, bytes, width);
        bitmap.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void WriteGray16Png(string path, IReadOnlyList<double> values, int width, int height, double scale)
    {
        var bytes = new byte[values.Count * 2];
        for (var i = 0; i < values.Count; i++)
        {
            var sample = (int)Math.Clamp(Math.Round(values[i] * scale, MidpointRounding.AwayFromZero), 0, 65535);
            bytes[i * 2] = (byte)(sample & 0xFF);
            bytes[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray16, null, bytes, width * 2);
        bitmap.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static ImageMatrix ReadToleranceArtifact(string path, ImageOnlyPcbLearningOptions options)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0)
            throw new InvalidDataException("Tolerance map decoder found no frames.");

        var frame = decoder.Frames[0];
        var width = frame.PixelWidth;
        var height = frame.PixelHeight;
        var values = new double[width * height];

        if (frame.Format == PixelFormats.Gray16)
        {
            var stride = width * 2;
            var raw = new byte[stride * height];
            frame.CopyPixels(raw, stride, 0);
            for (var i = 0; i < values.Length; i++)
            {
                var sample = raw[i * 2] | (raw[i * 2 + 1] << 8);
                values[i] = Math.Max(options.MinimumTolerance, sample / ToleranceArtifactScaleGray16);
            }
        }
        else
        {
            // Legacy Gray8 tolerance artifact from models trained before 16-bit persistence.
            BitmapSource gray8 = frame.Format == PixelFormats.Gray8
                ? frame
                : new FormatConvertedBitmap(frame, PixelFormats.Gray8, null, 0);
            var stride = width;
            var raw = new byte[stride * height];
            gray8.CopyPixels(raw, stride, 0);
            for (var i = 0; i < values.Length; i++)
                values[i] = Math.Max(options.MinimumTolerance, raw[i] / ToleranceArtifactScale);
        }

        return new ImageMatrix(width, height, values);
    }

    /// <summary>
    /// Records the preprocessing pipeline on the model so inspection always reproduces the
    /// exact transform the model was trained with.
    /// </summary>
    private static string BuildPreprocessingMode(ImageOnlyPcbLearningOptions options)
    {
        var parts = new List<string>();
        if (options.UseBoxAverageResampling)
            parts.Add("BoxAverage");
        if (options.FlattenIllumination)
            parts.Add($"FlattenLocal({FlattenKernelSize(options.InputWidth, options.InputHeight)})");
        parts.Add($"MeanBrightnessTo{options.TargetMeanBrightness:F0}");
        return string.Join("+", parts);
    }

    /// <summary>
    /// Inspection must preprocess exactly the way the model was trained, regardless of caller
    /// options — legacy models (mode string without the newer tokens) keep nearest-neighbor
    /// resampling and no flattening, so their persisted artifacts stay score-compatible.
    /// </summary>
    private static void ApplyModelPreprocessingMode(ImageOnlyPcbLearningOptions options, LearnedPcbVisualModel model)
    {
        var mode = model.BrightnessNormalizationMode ?? string.Empty;
        options.UseBoxAverageResampling = mode.Contains("BoxAverage", StringComparison.Ordinal);
        options.FlattenIllumination = mode.Contains("FlattenLocal", StringComparison.Ordinal);

        // Restore the brightness target the model was built with, exactly as the two flags above
        // are restored. Without this, an inspection that supplied a non-default target would
        // normalize the test image to a different mean than the persisted reference and bias every
        // score. Legacy models with no MeanBrightnessTo token keep the current option value.
        var brightnessToken = ExtractMeanBrightnessTarget(mode);
        if (brightnessToken is { } target)
            options.TargetMeanBrightness = target;
    }

    private static double? ExtractMeanBrightnessTarget(string mode)
    {
        const string marker = "MeanBrightnessTo";
        var start = mode.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
            return null;

        var valueStart = start + marker.Length;
        var valueEnd = valueStart;
        while (valueEnd < mode.Length && (char.IsDigit(mode[valueEnd]) || mode[valueEnd] == '.'))
            valueEnd++;

        return double.TryParse(
            mode.AsSpan(valueStart, valueEnd - valueStart),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed)
            ? Math.Clamp(parsed, 16.0, 240.0)
            : null;
    }

    private static LearnedVisualModelPackage LoadModelPackage(string modelId, ImageOnlyPcbLearningOptions? options)
    {
        var model = AoiDatabase.GetLearnedPcbVisualModel(modelId)
            ?? throw new InvalidOperationException("Learned PCB visual model metadata does not exist.");
        var normalizedOptions = NormalizeOptions(options, model.InputWidth, model.InputHeight);
        ApplyModelPreprocessingMode(normalizedOptions, model);
        var referencePath = FindArtifactPath(model, "learned_reference.png");
        var tolerancePath = FindArtifactPath(model, "tolerance_map.png");
        var reference = LoadImage(referencePath, normalizedOptions, normalizeBrightness: false).Matrix;
        var toleranceMap = ReadToleranceArtifact(tolerancePath, normalizedOptions);

        return new LearnedVisualModelPackage(
            model,
            reference,
            toleranceMap,
            normalizedOptions,
            Path.GetDirectoryName(referencePath) ?? string.Empty);
    }

    private static string FindArtifactPath(LearnedPcbVisualModel model, string artifactName)
    {
        var path = model.Artifacts.FirstOrDefault(
            artifact => string.Equals(artifact.ArtifactName, artifactName, StringComparison.OrdinalIgnoreCase))?.ArtifactPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new InvalidOperationException($"Required image-only learning artifact is missing: {artifactName}.");

        return path;
    }

    private static IReadOnlyList<LearnedPcbVisualModelArtifact> CreateArtifactRecords(string modelId, string artifactFolder)
        => ImageLearningProjectService.ExpectedArtifactFileNames
            .Select(name =>
            {
                var path = Path.Combine(artifactFolder, name);
                return new LearnedPcbVisualModelArtifact
                {
                    ModelId = modelId,
                    ArtifactName = name,
                    ArtifactPath = path,
                    Sha256 = File.Exists(path) ? ComputeSha256IfReadable(path) : string.Empty,
                    CreatedAtUtc = DateTime.UtcNow,
                };
            })
            .ToArray();

    private static ImageOnlyPcbLearningOptions NormalizeOptions(
        ImageOnlyPcbLearningOptions? options,
        int? modelWidth = null,
        int? modelHeight = null)
    {
        var source = options ?? new ImageOnlyPcbLearningOptions();
        var width = modelWidth.GetValueOrDefault(source.InputWidth <= 0 ? 768 : source.InputWidth);
        var height = modelHeight.GetValueOrDefault(source.InputHeight <= 0 ? 768 : source.InputHeight);
        if (width * (long)height > 768L * 768L)
        {
            width = Math.Clamp(source.SafeFallbackWidth, 64, 768);
            height = Math.Clamp(source.SafeFallbackHeight, 64, 768);
        }

        return new ImageOnlyPcbLearningOptions
        {
            InputWidth = Math.Clamp(width, 16, 768),
            InputHeight = Math.Clamp(height, 16, 768),
            SafeFallbackWidth = Math.Clamp(source.SafeFallbackWidth, 64, 768),
            SafeFallbackHeight = Math.Clamp(source.SafeFallbackHeight, 64, 768),
            AlignmentSearchRadiusPixels = Math.Clamp(source.AlignmentSearchRadiusPixels, 0, 20),
            TargetMeanBrightness = Math.Clamp(source.TargetMeanBrightness, 16.0, 240.0),
            MinimumTolerance = Math.Clamp(source.MinimumTolerance, 1.0, 64.0),
            DefaultLearnedThreshold = Math.Clamp(source.DefaultLearnedThreshold, 0.5, 64.0),
            FalseCallTarget = Math.Clamp(source.FalseCallTarget, 0.0, 1.0),
            MaxAllowedPossibleEscapeRate = Math.Clamp(source.MaxAllowedPossibleEscapeRate, 0.0, 1.0),
            MinimumAnomalyAreaPixels = Math.Max(1, source.MinimumAnomalyAreaPixels),
            RotationSearchDegrees = Math.Clamp(source.RotationSearchDegrees, 0.0, 5.0),
            UseBoxAverageResampling = source.UseBoxAverageResampling,
            FlattenIllumination = source.FlattenIllumination,
            ToleranceShrinkagePseudoCount = Math.Clamp(source.ToleranceShrinkagePseudoCount, 0.0, 50.0),
        };
    }

    private static string CreateArtifactFolder(string projectId, string modelId)
    {
        var folder = Path.Combine(
            AoiDatabase.ImageVaultPath,
            "image_learning",
            SafePathPart(projectId),
            "models",
            SafePathPart(modelId));
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

    private static string EscapeCsv(string value)
    {
        var text = value ?? string.Empty;
        if (!text.Contains('"') && !text.Contains(',') && !text.Contains('\n') && !text.Contains('\r'))
            return text;

        return $"\"{text.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private static double CalculateRawDifferencePercent(ImageMatrix first, ImageMatrix second)
    {
        var count = Math.Min(first.Pixels.Length, second.Pixels.Length);
        if (count == 0)
            return 0;

        var sum = 0.0;
        for (var i = 0; i < count; i++)
            sum += Math.Abs(first.Pixels[i] - second.Pixels[i]);

        return sum / count / 255.0 * 100.0;
    }

    private static IReadOnlyList<string> BuildLearningWarnings(
        IReadOnlyList<ImageLearningSkippedImage> skippedImages,
        ImageLearningCalibrationResult calibration)
    {
        var warnings = new List<string>();
        if (skippedImages.Count > 0)
            warnings.Add($"{skippedImages.Count} image(s) were skipped because they could not be read.");
        if (!string.Equals(calibration.Status, "OK", StringComparison.OrdinalIgnoreCase))
            warnings.Add(calibration.Summary);
        return warnings;
    }

    private static string ComputeSha256IfReadable(string path)
    {
        try
        {
            return HashUtil.ComputeSha256(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"Image-only learning SHA-256 could not be read: {ex.Message}");
            return string.Empty;
        }
    }

    private static string NewModelId()
        => $"ILM-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid().ToString("N")[..6]}";

    private static string NewCalibrationId()
        => $"ILC-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid().ToString("N")[..6]}";

    private static string NewResultId()
        => $"ILR-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid().ToString("N")[..6]}";

    private static string NewComparisonId()
        => $"ILCMP-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid().ToString("N")[..6]}";

    private sealed record LoadedProjectImage(ImageLearningProjectImage Source, ImageMatrix Matrix);

    private sealed record LearnedVisualModelPackage(
        LearnedPcbVisualModel Model,
        ImageMatrix Reference,
        ImageMatrix Tolerance,
        ImageOnlyPcbLearningOptions Options,
        string ArtifactFolder);

    private sealed record CalibrationComputation(
        ImageLearningCalibrationResult Result,
        IReadOnlyList<ImageLearningThresholdSweepRow> Sweep);

    private sealed record EvaluationResult(
        double Score,
        double RawDifferencePercent,
        string Verdict,
        string DecisionReason,
        List<ImageLearningAnomalyRegion> Regions,
        AlignmentOffset Alignment);

    private sealed record AlignmentOffset(int OffsetX, int OffsetY, double Error, double AngleDegrees = 0);

    private sealed record ImageMatrix(int Width, int Height, double[] Pixels);
}
