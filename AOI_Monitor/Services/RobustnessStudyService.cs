using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AOI_Monitor.Data;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

/// <summary>
/// MSA-adapted robustness / stability study for the Stage-1 image-only pipeline.
///
/// This is the image-only analogue of a Gage R&amp;R repeatability study: the same board
/// images are re-inspected under controlled, fully deterministic synthetic perturbations
/// (lighting shift, position offset, additive pseudo-noise) and the study reports how
/// stable the verdict is under those perturbations. Every rate is reported through
/// <see cref="RateEstimate"/> with an exact Clopper-Pearson confidence interval, never as
/// a bare percentage.
///
/// Honesty limit: this is a synthetic perturbation study, not a physical Gage R&amp;R with
/// real repeated captures. It bounds sensitivity to modelled disturbances only.
/// </summary>
public static class RobustnessStudyService
{
    public const string SchemaVersion = "robustness-study.v1";

    public const string BrightnessFamily = "brightness";
    public const string OffsetFamily = "offset";
    public const string NoiseFamily = "noise";
    public const string RotationFamily = "rotation";
    public const string BlurFamily = "blur";
    public const string OriginalFamily = "original";

    private static readonly int[] DefaultBrightnessShifts = { -24, -12, 12, 24 };
    private static readonly (int OffsetX, int OffsetY)[] DefaultPixelOffsets = { (1, 0), (0, 1), (-1, -1), (2, 2) };
    private static readonly int[] DefaultNoiseAmplitudes = { 4, 8 };
    private static readonly double[] DefaultRotationDegrees = { -1.5, -0.75, 0.75, 1.5 };
    private static readonly int[] DefaultBlurRadii = { 1 };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static RobustnessStudyResult RunStudy(
        string modelId,
        IReadOnlyList<(string Path, bool IsKnownNg)> images,
        ImageOnlyPcbLearningOptions? options,
        RobustnessStudyOptions? studyOptions,
        string operatorName,
        string outputFolder)
    {
        ArgumentNullException.ThrowIfNull(images);
        if (string.IsNullOrWhiteSpace(outputFolder))
            throw new InvalidOperationException("Robustness study requires an output folder.");
        if (images.Count == 0)
            throw new InvalidOperationException("Robustness study requires at least one input image.");

        var model = AoiDatabase.GetLearnedPcbVisualModel(modelId)
            ?? throw new InvalidOperationException("Learned PCB visual model metadata does not exist.");
        var normalizedOperator = string.IsNullOrWhiteSpace(operatorName) ? "UNKNOWN" : operatorName.Trim();

        var warnings = new List<string>();
        var design = NormalizeStudyDesign(studyOptions, warnings);

        Directory.CreateDirectory(outputFolder);
        var variantFolder = Path.Combine(outputFolder, "variants");
        Directory.CreateDirectory(variantFolder);

        var imageResults = new List<RobustnessStudyImageResult>();
        var stableVariantCount = 0;
        var totalVariantTrials = 0;
        var okFlipTrials = 0;
        var okFlips = 0;
        var ngRetentionTrials = 0;
        var ngRetained = 0;
        var familyTallies = new Dictionary<string, (int Stable, int Trials)>(StringComparer.Ordinal);

        for (var imageIndex = 0; imageIndex < images.Count; imageIndex++)
        {
            var (sourcePath, isKnownNg) = images[imageIndex];
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                warnings.Add(FormattableString.Invariant($"Skipped study image #{imageIndex + 1}: no image path was provided."));
                continue;
            }

            var fileName = Path.GetFileName(sourcePath);
            try
            {
                var source = DecodeBgra32(sourcePath);
                var original = ImageOnlyPcbLearningService.InspectImagePath(modelId, sourcePath, options, normalizedOperator);
                var originalVerdict = NormalizeVerdict(original.InspectionResult.Verdict);
                var originalScore = original.InspectionResult.AnomalyScore;

                if (!isKnownNg && originalVerdict != "OK")
                    warnings.Add($"Known-OK image {fileName} was {originalVerdict} before any perturbation; its variants are excluded from the false-call flip rate.");
                if (isKnownNg && originalVerdict == "OK")
                    warnings.Add($"Known-NG image {fileName} was OK before any perturbation (baseline escape); its variants are excluded from the detection retention rate.");

                var variants = new List<RobustnessStudyVariantResult>();
                foreach (var (family, detail, pixels) in BuildVariants(source, design, imageIndex))
                {
                    var variantFileName = FormattableString.Invariant(
                        $"{imageIndex:D2}_{Path.GetFileNameWithoutExtension(fileName)}_{detail}.png");
                    var variantPath = Path.Combine(variantFolder, variantFileName);
                    EncodePng(pixels, source.Width, source.Height, variantPath);

                    var inspected = ImageOnlyPcbLearningService.InspectImagePath(modelId, variantPath, options, normalizedOperator);
                    var verdict = NormalizeVerdict(inspected.InspectionResult.Verdict);
                    var matchesOriginal = string.Equals(verdict, originalVerdict, StringComparison.Ordinal);
                    var isFalseCallFlip = !isKnownNg && originalVerdict == "OK" && verdict != "OK";
                    var isDetectionLoss = isKnownNg && originalVerdict != "OK" && verdict == "OK";

                    variants.Add(new RobustnessStudyVariantResult(
                        fileName,
                        isKnownNg,
                        family,
                        detail,
                        variantPath,
                        inspected.InspectionResult.AnomalyScore,
                        verdict,
                        originalVerdict,
                        matchesOriginal,
                        isFalseCallFlip,
                        isDetectionLoss));
                }

                // Commit aggregates only after every variant of the image succeeded, so a
                // mid-image failure cannot leave partial counts behind.
                foreach (var variant in variants)
                {
                    totalVariantTrials++;
                    if (variant.MatchesOriginalVerdict)
                        stableVariantCount++;

                    var tally = familyTallies.TryGetValue(variant.PerturbationFamily, out var existing) ? existing : (Stable: 0, Trials: 0);
                    familyTallies[variant.PerturbationFamily] =
                        (tally.Stable + (variant.MatchesOriginalVerdict ? 1 : 0), tally.Trials + 1);

                    if (!isKnownNg && originalVerdict == "OK")
                    {
                        okFlipTrials++;
                        if (variant.IsFalseCallFlip)
                            okFlips++;
                    }

                    if (isKnownNg && originalVerdict != "OK")
                    {
                        ngRetentionTrials++;
                        if (!variant.IsDetectionLoss)
                            ngRetained++;
                    }
                }

                var stableForImage = variants.Count(variant => variant.MatchesOriginalVerdict);
                imageResults.Add(new RobustnessStudyImageResult(
                    sourcePath,
                    fileName,
                    isKnownNg,
                    originalVerdict,
                    originalScore,
                    variants.Count,
                    stableForImage,
                    variants.Count > 0 ? stableForImage / (double)variants.Count : 0.0,
                    variants));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or FileFormatException or ArgumentException or System.Runtime.InteropServices.COMException)
            {
                warnings.Add($"Skipped study image {fileName}: {ex.Message}");
            }
        }

        if (imageResults.Count == 0)
            throw new InvalidOperationException("Robustness study could not evaluate any input image.");

        var okImageCount = imageResults.Count(image => !image.IsKnownNg);
        var ngImageCount = imageResults.Count(image => image.IsKnownNg);
        if (okImageCount == 0)
            warnings.Add("No known-OK images were evaluated; the false-call flip rate cannot be measured in this study.");
        if (ngImageCount == 0)
            warnings.Add("No known-NG images were evaluated; detection retention cannot be measured in this study.");
        if (okFlipTrials > 0 && okFlipTrials < 20)
            warnings.Add(FormattableString.Invariant($"Only {okFlipTrials} OK-image perturbation trial(s) were available; the false-call flip confidence interval is wide. Add more known-OK images or perturbation variants."));

        var overallStability = new RateEstimate(stableVariantCount, totalVariantTrials);
        var okFalseCallFlipRate = new RateEstimate(okFlips, okFlipTrials);
        var ngDetectionRetentionRate = new RateEstimate(ngRetained, ngRetentionTrials);

        var familyBreakdowns = new List<RobustnessStudyFamilyBreakdown>();
        foreach (var family in new[] { BrightnessFamily, OffsetFamily, NoiseFamily, RotationFamily, BlurFamily })
        {
            if (familyTallies.TryGetValue(family, out var tally) && tally.Trials > 0)
                familyBreakdowns.Add(new RobustnessStudyFamilyBreakdown(family, new RateEstimate(tally.Stable, tally.Trials)));
        }

        var result = new RobustnessStudyResult(
            model.ModelId,
            DateTime.UtcNow,
            imageResults.Count,
            okImageCount,
            ngImageCount,
            design.VariantsPerImage,
            totalVariantTrials,
            stableVariantCount,
            overallStability,
            okFalseCallFlipRate,
            ngDetectionRetentionRate,
            familyBreakdowns,
            imageResults,
            warnings,
            outputFolder,
            Path.Combine(outputFolder, "robustness_study.html"),
            Path.Combine(outputFolder, "robustness_study.json"),
            Path.Combine(outputFolder, "robustness_study.csv"));

        File.WriteAllText(result.JsonReportPath, BuildJsonReport(result, model, design), Encoding.UTF8);
        File.WriteAllText(result.CsvReportPath, BuildCsvReport(result), Encoding.UTF8);
        File.WriteAllText(result.HtmlReportPath, BuildHtmlReport(result, model, design), Encoding.UTF8);

        AoiDatabase.RecordAuditEvent(
            "IMAGE_LEARNING_ROBUSTNESS_STUDY",
            FormattableString.Invariant($"Image-only robustness study exported: model={model.ModelId}; images={imageResults.Count}; variantTrials={totalVariantTrials}; stableTrials={stableVariantCount}; okFlips={okFlips}; ngRetained={ngRetained}."),
            operatorWithRole: normalizedOperator,
            relatedEntityType: "LearnedPcbVisualModel",
            relatedEntityId: model.ModelId,
            relatedPath: outputFolder);

        return result;
    }

    private static StudyDesign NormalizeStudyDesign(RobustnessStudyOptions? studyOptions, List<string> warnings)
    {
        var provided = studyOptions ?? new RobustnessStudyOptions();
        var brightnessShifts = (provided.BrightnessShifts ?? DefaultBrightnessShifts).ToArray();
        var pixelOffsets = (provided.PixelOffsets ?? DefaultPixelOffsets).ToArray();
        var requestedAmplitudes = (provided.NoiseAmplitudes ?? DefaultNoiseAmplitudes).ToArray();
        var noiseAmplitudes = requestedAmplitudes.Where(amplitude => amplitude > 0).ToArray();
        if (noiseAmplitudes.Length < requestedAmplitudes.Length)
            warnings.Add("Non-positive noise amplitudes were ignored; a noise amplitude must be at least 1 gray level.");

        var rotationDegrees = (provided.RotationDegreesVariants ?? DefaultRotationDegrees)
            .Where(degrees => degrees != 0 && double.IsFinite(degrees))
            .ToArray();
        var blurRadii = (provided.BlurRadii ?? DefaultBlurRadii)
            .Where(radius => radius > 0)
            .ToArray();

        var design = new StudyDesign(brightnessShifts, pixelOffsets, noiseAmplitudes, provided.NoiseSeed, rotationDegrees, blurRadii);
        if (design.VariantsPerImage == 0)
            throw new InvalidOperationException("Robustness study requires at least one perturbation variant (brightness shift, pixel offset, noise amplitude, rotation, or blur).");

        return design;
    }

    private static IEnumerable<(string Family, string Detail, byte[] Pixels)> BuildVariants(
        DecodedImage source,
        StudyDesign design,
        int imageIndex)
    {
        foreach (var shift in design.BrightnessShifts)
        {
            yield return (
                BrightnessFamily,
                FormattableString.Invariant($"brightness{shift:+0;-0}"),
                ApplyBrightnessShift(source.Pixels, shift));
        }

        foreach (var (offsetX, offsetY) in design.PixelOffsets)
        {
            yield return (
                OffsetFamily,
                FormattableString.Invariant($"offset({offsetX},{offsetY})"),
                ApplyPixelOffset(source.Pixels, source.Width, source.Height, offsetX, offsetY));
        }

        foreach (var amplitude in design.NoiseAmplitudes)
        {
            yield return (
                NoiseFamily,
                FormattableString.Invariant($"noise-amp{amplitude}"),
                ApplyDeterministicNoise(source.Pixels, amplitude, DeriveNoiseSeed(design.NoiseSeed, imageIndex, amplitude)));
        }

        foreach (var degrees in design.RotationDegrees)
        {
            yield return (
                RotationFamily,
                FormattableString.Invariant($"rotation{degrees:+0.##;-0.##}deg"),
                ApplyRotation(source.Pixels, source.Width, source.Height, degrees));
        }

        foreach (var radius in design.BlurRadii)
        {
            yield return (
                BlurFamily,
                FormattableString.Invariant($"blur-r{radius}"),
                ApplyBoxBlur(source.Pixels, source.Width, source.Height, radius));
        }
    }

    // Rotates the image about its center (bilinear sampling, edge-replicated fill) —
    // the fixture/hand-placement perturbation the alignment search is expected to absorb.
    private static byte[] ApplyRotation(byte[] source, int width, int height, double degrees)
    {
        var output = new byte[source.Length];
        var radians = degrees * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        var centerX = (width - 1) / 2.0;
        var centerY = (height - 1) / 2.0;

        for (var y = 0; y < height; y++)
        {
            var relY = y - centerY;
            for (var x = 0; x < width; x++)
            {
                var relX = x - centerX;
                var sourceX = Math.Clamp((cos * relX) + (sin * relY) + centerX, 0, width - 1);
                var sourceY = Math.Clamp((-sin * relX) + (cos * relY) + centerY, 0, height - 1);
                var x0 = (int)Math.Floor(sourceX);
                var y0 = (int)Math.Floor(sourceY);
                var x1 = Math.Min(x0 + 1, width - 1);
                var y1 = Math.Min(y0 + 1, height - 1);
                var fx = sourceX - x0;
                var fy = sourceY - y0;
                var targetOffset = ((y * width) + x) * 4;
                for (var channel = 0; channel < 3; channel++)
                {
                    var topLeft = source[(((y0 * width) + x0) * 4) + channel];
                    var topRight = source[(((y0 * width) + x1) * 4) + channel];
                    var bottomLeft = source[(((y1 * width) + x0) * 4) + channel];
                    var bottomRight = source[(((y1 * width) + x1) * 4) + channel];
                    var top = topLeft + ((topRight - topLeft) * fx);
                    var bottom = bottomLeft + ((bottomRight - bottomLeft) * fx);
                    output[targetOffset + channel] = ClampToByte((int)Math.Round(top + ((bottom - top) * fy)));
                }

                output[targetOffset + 3] = source[targetOffset + 3];
            }
        }

        return output;
    }

    // Box blur with the given radius — simulates focus softness / motion smear at capture.
    private static byte[] ApplyBoxBlur(byte[] source, int width, int height, int radius)
    {
        var output = new byte[source.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var sums = new int[3];
                var count = 0;
                for (var dy = -radius; dy <= radius; dy++)
                {
                    var sy = y + dy;
                    if (sy < 0 || sy >= height)
                        continue;

                    for (var dx = -radius; dx <= radius; dx++)
                    {
                        var sx = x + dx;
                        if (sx < 0 || sx >= width)
                            continue;

                        var offset = ((sy * width) + sx) * 4;
                        sums[0] += source[offset];
                        sums[1] += source[offset + 1];
                        sums[2] += source[offset + 2];
                        count++;
                    }
                }

                var targetOffset = ((y * width) + x) * 4;
                output[targetOffset] = ClampToByte(sums[0] / count);
                output[targetOffset + 1] = ClampToByte(sums[1] / count);
                output[targetOffset + 2] = ClampToByte(sums[2] / count);
                output[targetOffset + 3] = source[targetOffset + 3];
            }
        }

        return output;
    }

    private static byte[] ApplyBrightnessShift(byte[] source, int shift)
    {
        var output = new byte[source.Length];
        for (var i = 0; i < source.Length; i += 4)
        {
            output[i] = ClampToByte(source[i] + shift);
            output[i + 1] = ClampToByte(source[i + 1] + shift);
            output[i + 2] = ClampToByte(source[i + 2] + shift);
            output[i + 3] = source[i + 3];
        }

        return output;
    }

    // Shifts the image content by (offsetX, offsetY); uncovered border pixels are padded by
    // replicating the nearest edge pixel of the original image.
    private static byte[] ApplyPixelOffset(byte[] source, int width, int height, int offsetX, int offsetY)
    {
        var output = new byte[source.Length];
        for (var y = 0; y < height; y++)
        {
            var sourceY = Math.Clamp(y - offsetY, 0, height - 1);
            for (var x = 0; x < width; x++)
            {
                var sourceX = Math.Clamp(x - offsetX, 0, width - 1);
                Array.Copy(source, (sourceY * width + sourceX) * 4, output, (y * width + x) * 4, 4);
            }
        }

        return output;
    }

    // Additive gray noise in [-amplitude, +amplitude] from a fixed linear congruential
    // generator (Numerical Recipes constants). No System.Random: the sequence depends only
    // on the derived seed, so repeated runs produce byte-identical variants.
    private static byte[] ApplyDeterministicNoise(byte[] source, int amplitude, uint seed)
    {
        var output = new byte[source.Length];
        var state = seed;
        var span = (uint)(2 * amplitude + 1);
        for (var i = 0; i < source.Length; i += 4)
        {
            state = unchecked(state * 1664525u + 1013904223u);
            var delta = (int)((state >> 16) % span) - amplitude;
            output[i] = ClampToByte(source[i] + delta);
            output[i + 1] = ClampToByte(source[i + 1] + delta);
            output[i + 2] = ClampToByte(source[i + 2] + delta);
            output[i + 3] = source[i + 3];
        }

        return output;
    }

    // Pure function of (configured seed, image index, amplitude): reproducible across runs,
    // but different images and amplitudes get different noise patterns.
    private static uint DeriveNoiseSeed(int noiseSeed, int imageIndex, int amplitude)
    {
        unchecked
        {
            var seed = (uint)noiseSeed;
            seed = seed * 2654435761u + (uint)imageIndex;
            seed = seed * 2654435761u + (uint)amplitude;
            return seed == 0 ? 0x9E3779B9u : seed;
        }
    }

    private static DecodedImage DecodeBgra32(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new InvalidDataException("Robustness study image file does not exist.");

        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0)
            throw new InvalidDataException("Robustness study image decoder found no frames.");

        var frame = decoder.Frames[0];
        BitmapSource converted = frame.Format == PixelFormats.Bgra32
            ? frame
            : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        converted.Freeze();

        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        var pixels = new byte[width * 4 * height];
        converted.CopyPixels(pixels, width * 4, 0);
        return new DecodedImage(pixels, width, height);
    }

    private static void EncodePng(byte[] pixels, int width, int height, string path)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static string BuildJsonReport(RobustnessStudyResult result, LearnedPcbVisualModel model, StudyDesign design)
    {
        var payload = new
        {
            schemaVersion = SchemaVersion,
            generatedAtUtc = result.CreatedAtUtc,
            honestyNote = "Synthetic perturbation study on Stage-1 image-only pipeline. Not a substitute for a physical Gage R&R with real repeated captures.",
            engine = ImageOnlyPcbLearningService.EngineName,
            model = new
            {
                model.ModelId,
                model.ModelVersion,
                model.ProjectId,
                model.LearnedThreshold,
            },
            studyDesign = new
            {
                brightnessShifts = design.BrightnessShifts,
                pixelOffsets = design.PixelOffsets.Select(offset => FormatOffset(offset.OffsetX, offset.OffsetY)).ToArray(),
                noiseAmplitudes = design.NoiseAmplitudes,
                noiseSeed = design.NoiseSeed,
                rotationDegrees = design.RotationDegrees,
                blurRadii = design.BlurRadii,
                variantsPerImage = design.VariantsPerImage,
            },
            counts = new
            {
                imageCount = result.ImageCount,
                okImageCount = result.OkImageCount,
                ngImageCount = result.NgImageCount,
                totalVariantTrials = result.TotalVariantTrials,
                stableVariantCount = result.StableVariantCount,
            },
            overallStability = DescribeEstimate(result.OverallStability),
            okFalseCallFlipRate = DescribeEstimate(result.OkFalseCallFlipRate),
            ngDetectionRetentionRate = DescribeEstimate(result.NgDetectionRetentionRate),
            familyBreakdowns = result.FamilyBreakdowns
                .Select(breakdown => new { family = breakdown.Family, stability = DescribeEstimate(breakdown.Stability) })
                .ToArray(),
            warnings = result.Warnings,
            imageResults = result.ImageResults.Select(image => new
            {
                fileName = image.FileName,
                knownTruth = image.IsKnownNg ? "NG" : "OK",
                originalVerdict = image.OriginalVerdict,
                originalScore = image.OriginalScore,
                variantCount = image.VariantCount,
                stableVariantCount = image.StableVariantCount,
                stabilityFraction = image.StabilityFraction,
                variants = image.Variants.Select(variant => new
                {
                    family = variant.PerturbationFamily,
                    detail = variant.PerturbationDetail,
                    variantFileName = Path.GetFileName(variant.VariantPath),
                    verdict = variant.Verdict,
                    score = variant.Score,
                    matchesOriginalVerdict = variant.MatchesOriginalVerdict,
                    isFalseCallFlip = variant.IsFalseCallFlip,
                    isDetectionLoss = variant.IsDetectionLoss,
                }).ToArray(),
            }).ToArray(),
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    // RateEstimate.Point is NaN when there are no trials; JSON cannot carry NaN, so the
    // point estimate is projected to null and the honest "not measurable" text is kept.
    private static object DescribeEstimate(RateEstimate estimate) => new
    {
        successes = estimate.Successes,
        trials = estimate.Trials,
        point = estimate.IsMeasurable ? estimate.Point : (double?)null,
        lower = estimate.Lower,
        upper = estimate.Upper,
        confidence = estimate.Confidence,
        description = estimate.DescribeRate(),
    };

    private static string BuildCsvReport(RobustnessStudyResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("sourceFileName,knownTruth,perturbationFamily,perturbationDetail,variantFileName,originalVerdict,originalScore,verdict,score,matchesOriginalVerdict,isFalseCallFlip,isDetectionLoss");
        foreach (var image in result.ImageResults)
        {
            var knownTruth = image.IsKnownNg ? "NG" : "OK";
            AppendCsvRow(
                sb,
                image.FileName,
                knownTruth,
                OriginalFamily,
                OriginalFamily,
                image.FileName,
                image.OriginalVerdict,
                image.OriginalScore,
                image.OriginalVerdict,
                image.OriginalScore,
                matchesOriginalVerdict: true,
                isFalseCallFlip: false,
                isDetectionLoss: false);
            foreach (var variant in image.Variants)
            {
                AppendCsvRow(
                    sb,
                    image.FileName,
                    knownTruth,
                    variant.PerturbationFamily,
                    variant.PerturbationDetail,
                    Path.GetFileName(variant.VariantPath),
                    image.OriginalVerdict,
                    image.OriginalScore,
                    variant.Verdict,
                    variant.Score,
                    variant.MatchesOriginalVerdict,
                    variant.IsFalseCallFlip,
                    variant.IsDetectionLoss);
            }
        }

        return sb.ToString();
    }

    private static void AppendCsvRow(
        StringBuilder sb,
        string sourceFileName,
        string knownTruth,
        string family,
        string detail,
        string variantFileName,
        string originalVerdict,
        double originalScore,
        string verdict,
        double score,
        bool matchesOriginalVerdict,
        bool isFalseCallFlip,
        bool isDetectionLoss)
    {
        sb.Append(Csv(sourceFileName)).Append(',')
            .Append(Csv(knownTruth)).Append(',')
            .Append(Csv(family)).Append(',')
            .Append(Csv(detail)).Append(',')
            .Append(Csv(variantFileName)).Append(',')
            .Append(Csv(originalVerdict)).Append(',')
            .Append(originalScore.ToString("G17", CultureInfo.InvariantCulture)).Append(',')
            .Append(Csv(verdict)).Append(',')
            .Append(score.ToString("G17", CultureInfo.InvariantCulture)).Append(',')
            .Append(matchesOriginalVerdict ? "true" : "false").Append(',')
            .Append(isFalseCallFlip ? "true" : "false").Append(',')
            .Append(isDetectionLoss ? "true" : "false").AppendLine();
    }

    private static string BuildHtmlReport(RobustnessStudyResult result, LearnedPcbVisualModel model, StudyDesign design)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html><html><head><meta charset=\"utf-8\"><title>Image-only Robustness / Stability Study</title>");
        sb.AppendLine("<style>body{font-family:Segoe UI,Arial,sans-serif;background:#0b0e10;color:#e8eef2;margin:24px;}table{border-collapse:collapse;width:100%;margin-top:12px;}th,td{border:1px solid #3e474e;padding:8px;text-align:left;}th{background:#1b2024}.card{border:1px solid #3e474e;background:#151a1e;padding:12px;margin:8px 0}.warn{border-color:#987538;background:#372914}.sim{border-color:#8f5fd1;background:#2a1740}.ok{color:#50f56e}.ng{color:#f27777}</style>");
        sb.AppendLine("</head><body>");
        sb.AppendLine("<h1>Image-only Robustness / Stability Study (MSA-adapted)</h1>");
        sb.AppendLine("<div class=\"card sim\"><strong>Synthetic perturbation study on Stage-1 image-only pipeline.</strong> Not a substitute for a physical Gage R&amp;R with real repeated captures.</div>");
        sb.AppendLine($"<p>Model: {Html(model.ModelId)} ({Html(model.ModelVersion)})<br>Project: {Html(model.ProjectId)}<br>Engine: {Html(ImageOnlyPcbLearningService.EngineName)}<br>Generated (UTC): {result.CreatedAtUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}</p>");

        sb.AppendLine("<h2>Study Design</h2><div class=\"card\">");
        sb.AppendLine($"Brightness shifts (gray levels): {Html(string.Join(", ", design.BrightnessShifts.Select(shift => shift.ToString("+0;-0", CultureInfo.InvariantCulture))))}<br>");
        sb.AppendLine($"Pixel offsets (x,y): {Html(string.Join(", ", design.PixelOffsets.Select(offset => FormatOffset(offset.OffsetX, offset.OffsetY))))}<br>");
        sb.AppendLine($"Noise amplitudes (gray levels): {Html(string.Join(", ", design.NoiseAmplitudes.Select(amplitude => amplitude.ToString(CultureInfo.InvariantCulture))))} (deterministic LCG, seed {design.NoiseSeed.ToString(CultureInfo.InvariantCulture)})<br>");
        sb.AppendLine($"Variants per image: {design.VariantsPerImage.ToString(CultureInfo.InvariantCulture)}</div>");

        var ngLoss = new RateEstimate(
            result.NgDetectionRetentionRate.Trials - result.NgDetectionRetentionRate.Successes,
            result.NgDetectionRetentionRate.Trials);
        sb.AppendLine("<h2>Stability Summary</h2><table><tr><th>Metric</th><th>Value</th></tr>");
        Row("Images studied", FormattableString.Invariant($"{result.ImageCount} ({result.OkImageCount} known-OK, {result.NgImageCount} known-NG)"));
        Row("Perturbation trials", result.TotalVariantTrials.ToString(CultureInfo.InvariantCulture));
        Row("Overall verdict stability (exact 95% CI)", result.OverallStability.DescribeRate());
        Row("OK-image false-call flips (exact 95% CI)", result.OkFalseCallFlipRate.DescribeRate());
        Row("OK-image false-call flips (PPM)", result.OkFalseCallFlipRate.DescribePpm());
        Row("NG-image detection retention (exact 95% CI)", result.NgDetectionRetentionRate.DescribeRate());
        Row("NG-image detection losses (upper bound)", ngLoss.DescribeUpperBound());
        sb.AppendLine("</table>");

        sb.AppendLine("<h2>Per-perturbation-family Stability</h2><table><tr><th>Family</th><th>Stable</th><th>Trials</th><th>Stability (exact 95% CI)</th></tr>");
        foreach (var breakdown in result.FamilyBreakdowns)
        {
            sb.Append("<tr>")
                .Append(Cell(breakdown.Family))
                .Append(Cell(breakdown.Stability.Successes.ToString(CultureInfo.InvariantCulture)))
                .Append(Cell(breakdown.Stability.Trials.ToString(CultureInfo.InvariantCulture)))
                .Append(Cell(breakdown.Stability.DescribeRate()))
                .AppendLine("</tr>");
        }
        sb.AppendLine("</table>");

        if (result.Warnings.Count > 0)
        {
            sb.AppendLine("<h2>Warnings / Limits</h2><div class=\"card warn\"><ul>");
            foreach (var warning in result.Warnings)
                sb.AppendLine($"<li>{Html(warning)}</li>");
            sb.AppendLine("</ul></div>");
        }

        sb.AppendLine("<h2>Per-image Stability</h2><table><tr><th>Image</th><th>Known truth</th><th>Original verdict</th><th>Original score</th><th>Stable variants</th><th>Stability</th></tr>");
        foreach (var image in result.ImageResults)
        {
            sb.Append("<tr>")
                .Append(Cell(image.FileName))
                .Append(Cell(image.IsKnownNg ? "NG" : "OK"))
                .Append(Cell(image.OriginalVerdict))
                .Append(Cell(image.OriginalScore.ToString("F2", CultureInfo.InvariantCulture)))
                .Append(Cell(FormattableString.Invariant($"{image.StableVariantCount} of {image.VariantCount}")))
                .Append(Cell(image.StabilityFraction.ToString("P1", CultureInfo.InvariantCulture)))
                .AppendLine("</tr>");
        }
        sb.AppendLine("</table>");

        sb.AppendLine("<h2>Variant Results</h2><table><tr><th>Image</th><th>Family</th><th>Perturbation</th><th>Verdict</th><th>Score</th><th>Matches original</th><th>False-call flip</th><th>Detection loss</th></tr>");
        foreach (var image in result.ImageResults)
        {
            foreach (var variant in image.Variants)
            {
                sb.Append("<tr>")
                    .Append(Cell(image.FileName))
                    .Append(Cell(variant.PerturbationFamily))
                    .Append(Cell(variant.PerturbationDetail))
                    .Append(Cell(variant.Verdict))
                    .Append(Cell(variant.Score.ToString("F2", CultureInfo.InvariantCulture)))
                    .Append(Cell(variant.MatchesOriginalVerdict ? "YES" : "no"))
                    .Append(Cell(variant.IsFalseCallFlip ? "FLIP" : string.Empty))
                    .Append(Cell(variant.IsDetectionLoss ? "LOSS" : string.Empty))
                    .AppendLine("</tr>");
            }
        }

        sb.AppendLine("</table></body></html>");
        return sb.ToString();

        void Row(string label, string value)
            => sb.AppendLine($"<tr><td>{Html(label)}</td><td>{Html(value)}</td></tr>");
    }

    private static string FormatOffset(int offsetX, int offsetY)
        => FormattableString.Invariant($"({offsetX},{offsetY})");

    private static string NormalizeVerdict(string value)
        => string.Equals(value, "OK", StringComparison.OrdinalIgnoreCase) ? "OK"
            : string.Equals(value, "NG", StringComparison.OrdinalIgnoreCase) ? "NG"
            : "REVIEW";

    private static byte ClampToByte(int value)
        => (byte)Math.Clamp(value, 0, 255);

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

    private sealed record DecodedImage(byte[] Pixels, int Width, int Height);

    private sealed record StudyDesign(
        int[] BrightnessShifts,
        (int OffsetX, int OffsetY)[] PixelOffsets,
        int[] NoiseAmplitudes,
        int NoiseSeed,
        double[] RotationDegrees,
        int[] BlurRadii)
    {
        public int VariantsPerImage => BrightnessShifts.Length + PixelOffsets.Length + NoiseAmplitudes.Length + RotationDegrees.Length + BlurRadii.Length;
    }
}

/// <summary>
/// Perturbation design for the robustness study. All defaults are deterministic; the noise
/// uses a fixed-seed linear congruential generator so repeated runs are byte-identical.
/// </summary>
public sealed class RobustnessStudyOptions
{
    /// <summary>Simulated lighting shifts in gray levels, applied to every channel and clamped to 0..255.</summary>
    public IReadOnlyList<int> BrightnessShifts { get; set; } = new[] { -24, -12, 12, 24 };

    /// <summary>Simulated fixture/position offsets in pixels; uncovered borders are edge-padded.</summary>
    public IReadOnlyList<(int OffsetX, int OffsetY)> PixelOffsets { get; set; } = new (int OffsetX, int OffsetY)[] { (1, 0), (0, 1), (-1, -1), (2, 2) };

    /// <summary>Additive pseudo-noise amplitudes in gray levels (uniform in [-amplitude, +amplitude]).</summary>
    public IReadOnlyList<int> NoiseAmplitudes { get; set; } = new[] { 4, 8 };

    /// <summary>Base seed for the deterministic noise generator.</summary>
    public int NoiseSeed { get; set; } = 12345;

    /// <summary>Simulated fixture rotations in degrees (bilinear about the image center, edge-padded). Zero values are ignored.</summary>
    public IReadOnlyList<double> RotationDegreesVariants { get; set; } = new[] { -1.5, -0.75, 0.75, 1.5 };

    /// <summary>Box-blur radii in pixels simulating focus softness at capture. Non-positive values are ignored.</summary>
    public IReadOnlyList<int> BlurRadii { get; set; } = new[] { 1 };
}

public sealed record RobustnessStudyVariantResult(
    string SourceFileName,
    bool IsKnownNg,
    string PerturbationFamily,
    string PerturbationDetail,
    string VariantPath,
    double Score,
    string Verdict,
    string OriginalVerdict,
    bool MatchesOriginalVerdict,
    bool IsFalseCallFlip,
    bool IsDetectionLoss);

public sealed record RobustnessStudyImageResult(
    string ImagePath,
    string FileName,
    bool IsKnownNg,
    string OriginalVerdict,
    double OriginalScore,
    int VariantCount,
    int StableVariantCount,
    double StabilityFraction,
    IReadOnlyList<RobustnessStudyVariantResult> Variants);

public sealed record RobustnessStudyFamilyBreakdown(
    string Family,
    RateEstimate Stability);

public sealed record RobustnessStudyResult(
    string ModelId,
    DateTime CreatedAtUtc,
    int ImageCount,
    int OkImageCount,
    int NgImageCount,
    int VariantsPerImage,
    int TotalVariantTrials,
    int StableVariantCount,
    RateEstimate OverallStability,
    RateEstimate OkFalseCallFlipRate,
    RateEstimate NgDetectionRetentionRate,
    IReadOnlyList<RobustnessStudyFamilyBreakdown> FamilyBreakdowns,
    IReadOnlyList<RobustnessStudyImageResult> ImageResults,
    IReadOnlyList<string> Warnings,
    string OutputFolder,
    string HtmlReportPath,
    string JsonReportPath,
    string CsvReportPath);
