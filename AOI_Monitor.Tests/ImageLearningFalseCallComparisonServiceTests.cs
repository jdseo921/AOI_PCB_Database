using System.Windows.Media;
using System.Windows.Media.Imaging;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Xunit;

namespace AOI_Monitor.Tests;

public sealed class ImageLearningFalseCallComparisonServiceTests : IDisposable
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

    public ImageLearningFalseCallComparisonServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AOI_Monitor_ImageLearningFalseCall_Tests", Guid.NewGuid().ToString("N"));
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
            System.Diagnostics.Trace.WriteLine("Image-learning false-call test cleanup skipped because the temporary folder was still in use.");
        }
        catch (UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.WriteLine("Image-learning false-call test cleanup skipped because the temporary folder was not accessible.");
        }
    }

    [Fact]
    public void ImageLearningFalseCallLearnedModelReducesFalseCallsOnOkLightingAndPositionVariation()
    {
        var project = CreateLearningProject("False-call reduction");
        ImportStandardTrainingSet(project);
        ImportImages(
            project,
            ImageLearningImageRole.OkValidation,
            WriteBoardPng("ok-validation-shifted-bright.png", brightnessShift: 30, offsetX: 1, offsetY: -1, variant: 20));

        var learning = ImageOnlyPcbLearningService.TrainProject(project.ProjectId, _options, "Engineer01 [Engineer]");
        var comparison = ImageLearningFalseCallComparisonService.RunComparison(project.ProjectId, learning.Model.ModelId, _options, "Engineer01 [Engineer]");

        Assert.Equal(1, comparison.Metrics.OkValidationCount);
        Assert.True(comparison.Metrics.FalseCallsBeforeLearning > comparison.Metrics.FalseCallsAfterLearning);
        Assert.Equal(1, comparison.Metrics.FalseCallsBeforeLearning);
        Assert.Equal(0, comparison.Metrics.FalseCallsAfterLearning);
        Assert.True(comparison.Metrics.FalseCallReductionPercentage > 0);
    }

    [Fact]
    public void ImageLearningFalseCallObviousAnomalyRemainsDetectedAfterLearning()
    {
        var project = CreateLearningProject("Anomaly remains detected");
        ImportStandardTrainingSet(project);
        ImportImages(project, ImageLearningImageRole.OkValidation, WriteBoardPng("ok-validation.png", brightnessShift: 12, variant: 21));
        ImportImages(project, ImageLearningImageRole.Inspection, WriteBoardPng("inspection-ng.png", anomaly: true, variant: 31));

        var learning = ImageOnlyPcbLearningService.TrainProject(project.ProjectId, _options, "Engineer01 [Engineer]");
        var comparison = ImageLearningFalseCallComparisonService.RunComparison(project.ProjectId, learning.Model.ModelId, _options, "Engineer01 [Engineer]");
        var inspection = Assert.Single(comparison.ImageResults, row => row.Role == ImageLearningImageRole.Inspection);

        Assert.NotEqual("OK", inspection.LearnedVerdict);
        Assert.True(inspection.LearnedAnomalyRegions > 0);
        Assert.Contains(comparison.ImageResults, row => row.Role == ImageLearningImageRole.OkValidation);
    }

    [Fact]
    public void ImageLearningFalseCallUnsafeThresholdRejectedWhenNgValidationExists()
    {
        var project = CreateLearningProject("NG guardrail");
        ImportStandardTrainingSet(project);
        ImportImages(project, ImageLearningImageRole.OkValidation, WriteBoardPng("ok-validation.png", brightnessShift: 8, variant: 22));
        ImportImages(project, ImageLearningImageRole.NgValidation, WriteBoardPng("ng-validation.png", anomaly: true, variant: 32));

        var learning = ImageOnlyPcbLearningService.TrainProject(project.ProjectId, _options, "Engineer01 [Engineer]");
        var comparison = ImageLearningFalseCallComparisonService.RunComparison(project.ProjectId, learning.Model.ModelId, _options, "Engineer01 [Engineer]");
        var recommended = Assert.Single(comparison.ThresholdSweep, row => row.IsRecommended);

        Assert.Contains(comparison.ThresholdSweep, row => row.PossibleEscapeRate > _options.MaxAllowedPossibleEscapeRate);
        Assert.True(recommended.PossibleEscapeRate <= _options.MaxAllowedPossibleEscapeRate);
        Assert.DoesNotContain(comparison.ThresholdSweep, row => row.IsRecommended && row.PossibleEscapeRate > _options.MaxAllowedPossibleEscapeRate);
    }

    [Fact]
    public void ImageLearningFalseCallReportIncludesBeforeAfterMetrics()
    {
        var project = CreateLearningProject("Report metrics");
        ImportStandardTrainingSet(project);
        ImportImages(project, ImageLearningImageRole.OkValidation, WriteBoardPng("ok-validation-bright.png", brightnessShift: 30, variant: 23));

        var learning = ImageOnlyPcbLearningService.TrainProject(project.ProjectId, _options, "Engineer01 [Engineer]");
        var comparison = ImageLearningFalseCallComparisonService.RunComparison(project.ProjectId, learning.Model.ModelId, _options, "Engineer01 [Engineer]");

        Assert.True(File.Exists(comparison.HtmlReportPath));
        Assert.True(File.Exists(comparison.JsonReportPath));
        Assert.True(File.Exists(comparison.ResultsCsvPath));
        Assert.True(File.Exists(comparison.ThresholdSweepCsvPath));
        Assert.Contains("False calls reduced from", File.ReadAllText(comparison.HtmlReportPath), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Missed-defect rate cannot yet be proven", File.ReadAllText(comparison.HtmlReportPath), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("falseCallsBeforeLearning", File.ReadAllText(comparison.JsonReportPath), StringComparison.Ordinal);
        Assert.Contains("baselineScore", File.ReadLines(comparison.ResultsCsvPath).First(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("threshold,falseCalls", File.ReadLines(comparison.ThresholdSweepCsvPath).First(), StringComparison.OrdinalIgnoreCase);
        Assert.Single(AoiDatabase.GetAuditEvents(new LogFilter { ActionCategory = "IMAGE_LEARNING_FALSE_CALL_COMPARISON" }));
    }

    [Fact]
    public void ImageLearningFalseCallReportWithholdsRateBelowMinimumSampleSize()
    {
        var project = CreateLearningProject("Small-sample rate gate");
        ImportStandardTrainingSet(project);
        ImportImages(project, ImageLearningImageRole.OkValidation, WriteBoardPng("ok-validation-small.png", brightnessShift: 10, variant: 24));

        var learning = ImageOnlyPcbLearningService.TrainProject(project.ProjectId, _options, "Engineer01 [Engineer]");
        var comparison = ImageLearningFalseCallComparisonService.RunComparison(project.ProjectId, learning.Model.ModelId, _options, "Engineer01 [Engineer]");

        // The tiny synthetic set is below the minimum, so the percentage must be withheld.
        Assert.True(comparison.Metrics.OkValidationCount < ImageLearningFalseCallComparisonService.MinimumOkValidationForRate);

        var html = File.ReadAllText(comparison.HtmlReportPath);
        Assert.Contains("not statistically meaningful", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Percentage withheld", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("False-call rate before:", html, StringComparison.OrdinalIgnoreCase);
    }

    private ImageLearningProject CreateLearningProject(string name)
        => ImageLearningProjectService.CreateProject(name, "BOARD-FALSE-CALL", ImageLearningEvidenceMode.SyntheticDemo, "Engineer01 [Engineer]");

    private void ImportStandardTrainingSet(ImageLearningProject project)
    {
        ImportImages(project, ImageLearningImageRole.GoldenReference, WriteBoardPng("golden.png"));
        ImportImages(project, ImageLearningImageRole.OkLearning, WriteBoardPng("ok-shift-left.png", offsetX: -1, variant: 1));
        ImportImages(project, ImageLearningImageRole.OkLearning, WriteBoardPng("ok-shift-right.png", offsetX: 1, variant: 2));
        ImportImages(project, ImageLearningImageRole.OkLearning, WriteBoardPng("ok-bright-a.png", brightnessShift: 4, variant: 3));
        ImportImages(project, ImageLearningImageRole.OkLearning, WriteBoardPng("ok-bright-b.png", brightnessShift: -4, variant: 4));
        ImportImages(project, ImageLearningImageRole.OkLearning, WriteBoardPng("ok-normal.png", variant: 5));
    }

    private IReadOnlyList<ImageLearningImportResult> ImportImages(
        ImageLearningProject project,
        ImageLearningImageRole role,
        params string[] imagePaths)
    {
        var results = ImageLearningProjectService.ImportImageFiles(
            project.ProjectId,
            role,
            imagePaths,
            viewType: "Top",
            importedBy: "Engineer01 [Engineer]");
        Assert.All(results, result => Assert.True(result.Imported, result.Message));
        return results;
    }

    private string WriteBoardPng(
        string fileName,
        int brightnessShift = 0,
        int offsetX = 0,
        int offsetY = 0,
        int variant = 0,
        bool anomaly = false)
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
