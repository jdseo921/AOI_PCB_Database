using System.Windows.Media;
using System.Windows.Media.Imaging;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Xunit;

namespace AOI_Monitor.Tests;

public sealed class ImageLearningOverlayExportServiceTests : IDisposable
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

    public ImageLearningOverlayExportServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AOI_Monitor_ImageLearningOverlayExport_Tests", Guid.NewGuid().ToString("N"));
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
            System.Diagnostics.Trace.WriteLine("Image-learning overlay export test cleanup skipped because the temporary folder was still in use.");
        }
        catch (UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.WriteLine("Image-learning overlay export test cleanup skipped because the temporary folder was not accessible.");
        }
    }

    [Fact]
    public void ImageLearningOverlayExportOverlayGeneratedForAnomalyResult()
    {
        var setup = CreateInspectedProject("Overlay anomaly");

        var export = ImageLearningOverlayExportService.ExportProjectVisualEvidence(
            setup.Project.ProjectId,
            setup.Model.ModelId,
            new ImageLearningOverlayExportOptions { LearningOptions = _options },
            "Engineer01 [Engineer]");

        var item = Assert.Single(export.Items, row => row.Category == "inspection_results");
        Assert.True(File.Exists(item.AnnotatedOverlayPath));
        Assert.True(File.Exists(item.OriginalPath));
        Assert.Contains("Learned", File.ReadAllText(export.ManifestPath), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ImageLearningOverlayExportHeatmapGeneratedForAnomalyResult()
    {
        var setup = CreateInspectedProject("Heatmap anomaly");

        var export = ImageLearningOverlayExportService.ExportProjectVisualEvidence(
            setup.Project.ProjectId,
            setup.Model.ModelId,
            new ImageLearningOverlayExportOptions { LearningOptions = _options },
            "Engineer01 [Engineer]");

        var item = Assert.Single(export.Items, row => row.Category == "inspection_results");
        Assert.True(File.Exists(item.HeatmapPath));
        Assert.EndsWith(".png", item.HeatmapPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ImageLearningOverlayExportOutputFolderContainsManifestAndAuditTrail()
    {
        var setup = CreateInspectedProject("Manifest audit");

        var export = ImageLearningOverlayExportService.ExportProjectVisualEvidence(
            setup.Project.ProjectId,
            setup.Model.ModelId,
            new ImageLearningOverlayExportOptions { LearningOptions = _options },
            "Engineer01 [Engineer]");

        Assert.True(File.Exists(export.ManifestPath));
        Assert.Equal(export.OutputFolder, Path.GetDirectoryName(export.ManifestPath));
        Assert.Contains("image-learning-visual-evidence.v1", File.ReadAllText(export.ManifestPath), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(AoiDatabase.GetExportHistory(), row => row.Id == export.ExportHistoryId && row.ExportType == "ImageLearningVisualEvidence");
        Assert.Single(AoiDatabase.GetAuditEvents(new LogFilter { ActionCategory = "IMAGE_LEARNING_VISUAL_EVIDENCE_EXPORT" }));
    }

    [Fact]
    public void ImageLearningOverlayExportMissingSourceImageReportsWarningNotCrash()
    {
        var setup = CreateInspectedProject("Missing source");
        var image = setup.InspectionImage;
        TryDelete(image.VaultPath);
        TryDelete(image.OriginalPath);

        var export = ImageLearningOverlayExportService.ExportProjectVisualEvidence(
            setup.Project.ProjectId,
            setup.Model.ModelId,
            new ImageLearningOverlayExportOptions { LearningOptions = _options },
            "Engineer01 [Engineer]");

        Assert.True(File.Exists(export.ManifestPath));
        Assert.Contains(export.Warnings, warning => warning.Contains("Missing source image", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(export.Items, item => item.Warnings.Any(warning => warning.Contains("Missing source image", StringComparison.OrdinalIgnoreCase)));
    }

    private TestSetup CreateInspectedProject(string name)
    {
        var project = ImageLearningProjectService.CreateProject(name, "BOARD-OVERLAY", ImageLearningEvidenceMode.SyntheticDemo, "Engineer01 [Engineer]");
        ImportStandardTrainingSet(project);
        ImportImages(project, ImageLearningImageRole.OkValidation, WriteBoardPng("ok-validation.png", brightnessShift: 8, variant: 21));
        var learning = ImageOnlyPcbLearningService.TrainProject(project.ProjectId, _options, "Engineer01 [Engineer]");
        var inspectionImport = ImportImages(
            project,
            ImageLearningImageRole.Inspection,
            WriteBoardPng("inspection-ng.png", anomaly: true, variant: 31)).Single();
        var inspected = ImageOnlyPcbLearningService.InspectProjectImage(
            learning.Model.ModelId,
            inspectionImport.Image!,
            _options,
            "Engineer01 [Engineer]");

        Assert.NotEmpty(inspected.Regions);
        return new TestSetup(project, learning.Model, inspectionImport.Image!);
    }

    private void ImportStandardTrainingSet(ImageLearningProject project)
    {
        ImportImages(project, ImageLearningImageRole.GoldenReference, WriteBoardPng("golden.png"));
        for (var i = 0; i < 5; i++)
            ImportImages(project, ImageLearningImageRole.OkLearning, WriteBoardPng($"ok-{i}.png", variant: i + 1));
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

    private static void TryDelete(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            System.Diagnostics.Trace.WriteLine("Image-learning overlay export test source image cleanup skipped because the file was still in use.");
        }
        catch (UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.WriteLine("Image-learning overlay export test source image cleanup skipped because the file was not accessible.");
        }
    }

    private sealed record TestSetup(
        ImageLearningProject Project,
        LearnedPcbVisualModel Model,
        ImageLearningProjectImage InspectionImage);
}
