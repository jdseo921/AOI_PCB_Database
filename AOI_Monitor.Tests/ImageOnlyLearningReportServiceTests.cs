using System.Windows.Media;
using System.Windows.Media.Imaging;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Xunit;

namespace AOI_Monitor.Tests;

public sealed class ImageOnlyLearningReportServiceTests : IDisposable
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

    public ImageOnlyLearningReportServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AOI_Monitor_ImageOnlyLearningReport_Tests", Guid.NewGuid().ToString("N"));
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
            System.Diagnostics.Trace.WriteLine("Image-only learning report test cleanup skipped because the temporary folder was still in use.");
        }
        catch (UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.WriteLine("Image-only learning report test cleanup skipped because the temporary folder was not accessible.");
        }
    }

    [Fact]
    public void ImageOnlyLearningReportContainsImageCounts()
    {
        var report = CreateReport();
        var html = File.ReadAllText(report.HtmlPath);
        var json = File.ReadAllText(report.JsonPath);

        Assert.Contains("Golden / Reference", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OK Learning", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OK Validation", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"okValidation\": 3", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"inspection\": 3", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ImageOnlyLearningReportContainsFalseCallBeforeAfter()
    {
        var report = CreateReport();
        var html = File.ReadAllText(report.HtmlPath);
        var json = File.ReadAllText(report.JsonPath);

        Assert.Contains("False calls before learning", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("False calls after learning", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("False calls reduced from", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("falseCallsBeforeLearning", json, StringComparison.Ordinal);
        Assert.Contains("falseCallsAfterLearning", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ImageOnlyLearningReportContainsRecommendedThreshold()
    {
        var report = CreateReport();
        var html = File.ReadAllText(report.HtmlPath);
        var json = File.ReadAllText(report.JsonPath);

        Assert.Contains("Recommended threshold", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("recommendedThreshold", json, StringComparison.Ordinal);
        Assert.True(File.Exists(report.PdfPath));
    }

    [Fact]
    public void ImageOnlyLearningReportContainsEvidenceBoundary()
    {
        var report = CreateReport();
        var html = File.ReadAllText(report.HtmlPath);
        var json = File.ReadAllText(report.JsonPath);

        Assert.Contains("The program learned normal PCB appearance.", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("The program learned normal variation.", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("The program ignored harmless variation.", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("The program flagged unusual regions.", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("missed-defect rate cannot be fully proven", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Synthetic/internal demo data is not customer acceptance", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Stage 2 live camera validation remains separate", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ImageOnlyLearningReportContainsExampleOverlayPathsAndAudit()
    {
        var report = CreateReport();
        var json = File.ReadAllText(report.JsonPath);

        Assert.True(File.Exists(report.HtmlPath));
        Assert.True(File.Exists(report.JsonPath));
        Assert.True(File.Exists(report.PdfPath));
        Assert.True(report.AnomalyExampleOverlayPaths.Count >= 3);
        Assert.All(report.AnomalyExampleOverlayPaths, path => Assert.True(File.Exists(path), path));
        Assert.False(string.IsNullOrWhiteSpace(report.BeforeAfterOverlayPath));
        Assert.True(File.Exists(report.BeforeAfterOverlayPath));
        Assert.Contains("annotatedOverlay", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("beforeAfterOverlayComparison", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(AoiDatabase.GetExportHistory(), row => row.Id == report.ExportHistoryId && row.ExportType == "ImageOnlyLearningVisualReport");
        Assert.Single(AoiDatabase.GetAuditEvents(new LogFilter { ActionCategory = "IMAGE_ONLY_LEARNING_REPORT_EXPORT" }));
    }

    private ImageOnlyLearningReportResult CreateReport()
    {
        var project = ImageLearningProjectService.CreateProject(
            "Visual learning report",
            "BOARD-REPORT",
            ImageLearningEvidenceMode.SyntheticDemo,
            "Engineer01 [Engineer]");
        ImportStandardTrainingSet(project);

        for (var i = 0; i < 3; i++)
        {
            ImportImages(
                project,
                ImageLearningImageRole.OkValidation,
                WriteBoardPng($"ok-validation-{i}.png", brightnessShift: 24 + i, offsetX: i % 2, variant: 20 + i));
        }

        var learning = ImageOnlyPcbLearningService.TrainProject(project.ProjectId, _options, "Engineer01 [Engineer]");
        for (var i = 0; i < 3; i++)
        {
            var imported = ImportImages(
                project,
                ImageLearningImageRole.Inspection,
                WriteBoardPng($"inspection-anomaly-{i}.png", anomaly: true, variant: 40 + i)).Single();
            var inspection = ImageOnlyPcbLearningService.InspectProjectImage(
                learning.Model.ModelId,
                imported.Image!,
                _options,
                "Engineer01 [Engineer]");
            Assert.NotEmpty(inspection.Regions);
        }

        return ImageOnlyLearningReportService.ExportReport(
            project.ProjectId,
            learning.Model.ModelId,
            new ImageOnlyLearningReportOptions { LearningOptions = _options },
            "Engineer01 [Engineer]");
    }

    private void ImportStandardTrainingSet(ImageLearningProject project)
    {
        ImportImages(project, ImageLearningImageRole.GoldenReference, WriteBoardPng("golden.png"));
        for (var i = 0; i < 5; i++)
            ImportImages(project, ImageLearningImageRole.OkLearning, WriteBoardPng($"ok-learning-{i}.png", variant: i + 1));
    }

    private static IReadOnlyList<ImageLearningImportResult> ImportImages(
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
