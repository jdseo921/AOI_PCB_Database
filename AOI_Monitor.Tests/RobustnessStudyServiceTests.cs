using System.Windows.Media;
using System.Windows.Media.Imaging;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Xunit;

namespace AOI_Monitor.Tests;

public sealed class RobustnessStudyServiceTests : IDisposable
{
    private readonly string _root;
    private readonly ImageOnlyPcbLearningOptions _options = new()
    {
        InputWidth = 32,
        InputHeight = 32,
        AlignmentSearchRadiusPixels = 6,
        MinimumAnomalyAreaPixels = 6,
        DefaultLearnedThreshold = 4.0,
        FalseCallTarget = 0.05,
        MaxAllowedPossibleEscapeRate = 0.0,
    };

    public RobustnessStudyServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AOI_Monitor_RobustnessStudy_Tests", Guid.NewGuid().ToString("N"));
        AoiDatabase.ConfigureStorageRoot(_root);
        AoiDatabase.AuditOperatorProvider = null;
        AoiDatabase.AuditUserIdProvider = null;
        AoiDatabase.AuditUserRoleProvider = null;
        AoiDatabase.AuditStationProvider = null;
        AoiDatabase.Initialize();
    }

    public void Dispose()
    {
        AoiDatabase.AuditOperatorProvider = null;
        AoiDatabase.AuditUserIdProvider = null;
        AoiDatabase.AuditUserRoleProvider = null;
        AoiDatabase.AuditStationProvider = null;
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            System.Diagnostics.Trace.WriteLine("Robustness study test cleanup skipped because the temporary folder was still in use.");
        }
        catch (UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.WriteLine("Robustness study test cleanup skipped because the temporary folder was not accessible.");
        }
    }

    [Fact]
    public void RobustnessStudyWritesJsonCsvAndHtmlReportFiles()
    {
        var (modelId, images) = TrainModelAndCreateStudySet();

        var result = RunStudy(modelId, images, "study-reports");

        Assert.True(File.Exists(result.JsonReportPath));
        Assert.True(File.Exists(result.CsvReportPath));
        Assert.True(File.Exists(result.HtmlReportPath));
        Assert.Equal(Path.Combine(result.OutputFolder, "robustness_study.json"), result.JsonReportPath);
        Assert.Equal(Path.Combine(result.OutputFolder, "robustness_study.csv"), result.CsvReportPath);
        Assert.Equal(Path.Combine(result.OutputFolder, "robustness_study.html"), result.HtmlReportPath);
    }

    [Fact]
    public void RobustnessStudyJsonContainsSchemaVersionAndAggregateRates()
    {
        var (modelId, images) = TrainModelAndCreateStudySet();

        var result = RunStudy(modelId, images, "study-json");
        var json = File.ReadAllText(result.JsonReportPath);

        Assert.Contains("robustness-study.v1", json, StringComparison.Ordinal);
        Assert.Contains("overallStability", json, StringComparison.Ordinal);
        Assert.Contains("okFalseCallFlipRate", json, StringComparison.Ordinal);
        Assert.Contains("ngDetectionRetentionRate", json, StringComparison.Ordinal);
        Assert.Contains("familyBreakdowns", json, StringComparison.Ordinal);
    }

    [Fact]
    public void RobustnessStudyHtmlContainsHonestyBannerAndExactConfidenceIntervals()
    {
        var (modelId, images) = TrainModelAndCreateStudySet();

        var result = RunStudy(modelId, images, "study-html");
        var html = File.ReadAllText(result.HtmlReportPath);

        Assert.Contains("Synthetic perturbation study on Stage-1 image-only pipeline.", html, StringComparison.Ordinal);
        // "&" is HTML-encoded in the report body, so the banner reads "Gage R&amp;R" on disk.
        Assert.Contains("Not a substitute for a physical Gage R&amp;R with real repeated captures.", html, StringComparison.Ordinal);
        Assert.Contains("95% CI", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RobustnessStudyIsDeterministicAcrossRepeatedRuns()
    {
        var (modelId, images) = TrainModelAndCreateStudySet();

        var first = RunStudy(modelId, images, "study-run-1");
        var second = RunStudy(modelId, images, "study-run-2");

        Assert.Equal(first.OverallStability.Successes, second.OverallStability.Successes);
        Assert.Equal(first.OverallStability.Trials, second.OverallStability.Trials);
        Assert.Equal(first.OkFalseCallFlipRate.Successes, second.OkFalseCallFlipRate.Successes);
        Assert.Equal(first.OkFalseCallFlipRate.Trials, second.OkFalseCallFlipRate.Trials);
        Assert.Equal(first.NgDetectionRetentionRate.Successes, second.NgDetectionRetentionRate.Successes);
        Assert.Equal(first.NgDetectionRetentionRate.Trials, second.NgDetectionRetentionRate.Trials);
        Assert.Equal(VerdictFingerprint(first), VerdictFingerprint(second));
    }

    [Fact]
    public void RobustnessStudyOverallStabilityCoversEveryVariantTrial()
    {
        var (modelId, images) = TrainModelAndCreateStudySet();

        var result = RunStudy(modelId, images, "study-trials");

        // Default design: 4 brightness shifts + 4 pixel offsets + 2 noise amplitudes.
        Assert.Equal(10, result.VariantsPerImage);
        Assert.Equal(3, result.ImageCount);
        Assert.Equal(result.VariantsPerImage * result.ImageCount, result.TotalVariantTrials);
        Assert.Equal(result.TotalVariantTrials, result.OverallStability.Trials);
        Assert.True(result.OverallStability.IsMeasurable);
        Assert.All(
            result.ImageResults.Where(image => !image.IsKnownNg),
            image => Assert.Equal("OK", image.OriginalVerdict));
        Assert.Equal(result.VariantsPerImage * 2, result.OkFalseCallFlipRate.Trials);
        Assert.True(result.OkFalseCallFlipRate.IsMeasurable);
    }

    [Fact]
    public void RobustnessStudyNgImageRetainsDetectionAcrossAllVariants()
    {
        var (modelId, images) = TrainModelAndCreateStudySet();

        var result = RunStudy(modelId, images, "study-ng-retention");
        var ngImage = Assert.Single(result.ImageResults, image => image.IsKnownNg);

        Assert.NotEqual("OK", ngImage.OriginalVerdict);
        Assert.Equal(result.VariantsPerImage, result.NgDetectionRetentionRate.Trials);
        Assert.Equal(result.NgDetectionRetentionRate.Trials, result.NgDetectionRetentionRate.Successes);
        Assert.All(ngImage.Variants, variant => Assert.NotEqual("OK", variant.Verdict));
        Assert.All(ngImage.Variants, variant => Assert.False(variant.IsDetectionLoss));
    }

    [Fact]
    public void RobustnessStudyFamilyBreakdownCoversBrightnessOffsetAndNoise()
    {
        var (modelId, images) = TrainModelAndCreateStudySet();

        var result = RunStudy(modelId, images, "study-families");

        Assert.Equal(3, result.FamilyBreakdowns.Count);
        var brightness = Assert.Single(result.FamilyBreakdowns, breakdown => breakdown.Family == RobustnessStudyService.BrightnessFamily);
        var offset = Assert.Single(result.FamilyBreakdowns, breakdown => breakdown.Family == RobustnessStudyService.OffsetFamily);
        var noise = Assert.Single(result.FamilyBreakdowns, breakdown => breakdown.Family == RobustnessStudyService.NoiseFamily);
        Assert.Equal(4 * result.ImageCount, brightness.Stability.Trials);
        Assert.Equal(4 * result.ImageCount, offset.Stability.Trials);
        Assert.Equal(2 * result.ImageCount, noise.Stability.Trials);
    }

    [Fact]
    public void RobustnessStudyCsvContainsOneRowPerImageAndVariant()
    {
        var (modelId, images) = TrainModelAndCreateStudySet();

        var result = RunStudy(modelId, images, "study-csv");
        var lines = File.ReadAllLines(result.CsvReportPath).Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();

        // Header + per image: 1 original row + 10 variant rows.
        Assert.Equal(1 + result.ImageCount * (1 + result.VariantsPerImage), lines.Length);
        Assert.Contains("brightness+12", string.Join("\n", lines), StringComparison.Ordinal);
        Assert.Contains("offset(2,2)", string.Join("\n", lines), StringComparison.Ordinal);
        Assert.Contains("noise-amp8", string.Join("\n", lines), StringComparison.Ordinal);
    }

    [Fact]
    public void RobustnessStudyRequiresAtLeastOneInputImage()
    {
        var (modelId, _) = TrainModelAndCreateStudySet();

        var ex = Assert.Throws<InvalidOperationException>(() => RunStudy(
            modelId,
            Array.Empty<(string Path, bool IsKnownNg)>(),
            "study-empty"));

        Assert.Contains("at least one input image", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private (string ModelId, IReadOnlyList<(string Path, bool IsKnownNg)> Images) TrainModelAndCreateStudySet()
    {
        var project = CreateLearningProject("Robustness study");
        ImportStandardTrainingSet(project);
        ImportImages(project, ImageLearningImageRole.OkValidation, WriteBoardPng("ok-validation-a.png", brightnessShift: 8, variant: 11));
        ImportImages(project, ImageLearningImageRole.OkValidation, WriteBoardPng("ok-validation-b.png", brightnessShift: 12, variant: 12));

        var training = ImageOnlyPcbLearningService.TrainProject(project.ProjectId, _options, "Engineer01 [Engineer]");

        var images = new List<(string Path, bool IsKnownNg)>
        {
            (WriteBoardPng("study-ok-a.png", variant: 20), false),
            (WriteBoardPng("study-ok-b.png", variant: 21), false),
            (WriteBoardPng("study-ng.png", anomaly: true, variant: 31), true),
        };
        return (training.Model.ModelId, images);
    }

    private RobustnessStudyResult RunStudy(
        string modelId,
        IReadOnlyList<(string Path, bool IsKnownNg)> images,
        string outputSubFolder)
        => RobustnessStudyService.RunStudy(
            modelId,
            images,
            _options,
            new RobustnessStudyOptions(),
            "Engineer01 [Engineer]",
            Path.Combine(_root, outputSubFolder));

    private static string VerdictFingerprint(RobustnessStudyResult result)
        => string.Join(
            "|",
            result.ImageResults.SelectMany(image => image.Variants.Select(
                variant => $"{image.FileName}:{variant.PerturbationDetail}={variant.Verdict}")));

    private ImageLearningProject CreateLearningProject(string name)
        => ImageLearningProjectService.CreateProject(name, "BOARD-IMAGE-ONLY", ImageLearningEvidenceMode.SyntheticDemo, "Engineer01 [Engineer]");

    private void ImportStandardTrainingSet(ImageLearningProject project)
    {
        ImportImages(project, ImageLearningImageRole.GoldenReference, WriteBoardPng("golden.png"));
        for (var i = 0; i < 5; i++)
            ImportImages(project, ImageLearningImageRole.OkLearning, WriteBoardPng($"ok-{i}.png", variant: i + 1));
    }

    private IReadOnlyList<ImageLearningImportResult> ImportImages(
        ImageLearningProject project,
        ImageLearningImageRole role,
        params string[] imagePaths)
        => ImageLearningProjectService.ImportImageFiles(
            project.ProjectId,
            role,
            imagePaths,
            viewType: "Top",
            importedBy: "Engineer01 [Engineer]");

    private string WriteBoardPng(
        string fileName,
        int brightnessShift = 0,
        int offsetX = 0,
        int offsetY = 0,
        int variant = 0,
        bool anomaly = false,
        bool tinyMarker = false)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, fileName);
        const int width = 32;
        const int height = 32;
        var pixels = new byte[width * height * 4];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var patternX = x - offsetX;
                var patternY = y - offsetY;
                var value = Pattern(patternX, patternY, brightnessShift, variant);
                if (anomaly && x is >= 21 and <= 27 && y is >= 5 and <= 11)
                    value = 245;
                if (tinyMarker && x == 0 && y == 0)
                    value = ClampByte(value + 1);

                var index = (y * width + x) * 4;
                pixels[index] = value;
                pixels[index + 1] = value;
                pixels[index + 2] = value;
                pixels[index + 3] = 255;
            }
        }

        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    private static byte Pattern(int x, int y, int brightnessShift, int variant)
    {
        var value = 64 + brightnessShift;
        if (x < 0 || y < 0 || x >= 32 || y >= 32)
            return ClampByte(value);
        if (x is >= 3 and <= 28 && y is >= 14 and <= 17)
            value = 178 + brightnessShift + variant % 3;
        if (y is >= 3 and <= 28 && x is >= 14 and <= 17)
            value = 168 + brightnessShift - variant % 2;
        if (x is >= 6 and <= 11 && y is >= 6 and <= 11)
            value = 112 + brightnessShift + variant % 5;
        if (x is >= 20 and <= 26 && y is >= 20 and <= 25)
            value = 132 + brightnessShift - variant % 4;

        return ClampByte(value);
    }

    private static byte ClampByte(int value)
        => (byte)Math.Clamp(value, 0, 255);
}
