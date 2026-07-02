using System.Windows.Media;
using System.Windows.Media.Imaging;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using AOI_Monitor.Tools;
using Xunit;

namespace AOI_Monitor.Tests;

public sealed class ImageLearningFolderImportServiceTests : IDisposable
{
    private readonly string _root;

    public ImageLearningFolderImportServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AOI_Monitor_ImageLearningFolderImport_Tests", Guid.NewGuid().ToString("N"));
        AoiDatabase.ConfigureStorageRoot(_root);
        AoiDatabase.AuditOperatorProvider = null;
        AoiDatabase.AuditUserIdProvider = null;
        AoiDatabase.AuditUserRoleProvider = null;
        AoiDatabase.AuditStationProvider = null;
        AoiDatabase.Initialize();
        AiTrainingSetupService.ResetForTests();
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
            System.Diagnostics.Trace.WriteLine("Image-learning folder import test cleanup skipped because the temporary folder was still in use.");
        }
        catch (UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.WriteLine("Image-learning folder import test cleanup skipped because the temporary folder was not accessible.");
        }
    }

    [Fact]
    public void ImageLearningFolderImportConventionCreatesProjectAndImportsRoleCounts()
    {
        var projectFolder = CreateConventionFolder("CustomerBoard");
        WritePng(Path.Combine(projectFolder, "golden"), "golden.png", 10);
        WritePng(Path.Combine(projectFolder, "ok_learning"), "ok-1.png", 20);
        WritePng(Path.Combine(projectFolder, "ok_learning"), "ok-2.png", 30);
        WritePng(Path.Combine(projectFolder, "ok_validation"), "ok-val.png", 40);
        WritePng(Path.Combine(projectFolder, "inspection"), "inspect.jpg", 50);

        var result = ImageLearningFolderImportService.ImportProjectFolder(
            projectFolder,
            "Engineer01 [Engineer]",
            ImageLearningEvidenceMode.CustomerData);

        Assert.StartsWith("ILP-", result.Project.ProjectId, StringComparison.Ordinal);
        Assert.Equal(1, result.CountsByRole[ImageLearningImageRole.GoldenReference]);
        Assert.Equal(2, result.CountsByRole[ImageLearningImageRole.OkLearning]);
        Assert.Equal(1, result.CountsByRole[ImageLearningImageRole.OkValidation]);
        Assert.Equal(1, result.CountsByRole[ImageLearningImageRole.Inspection]);
        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("ng_validation", StringComparison.OrdinalIgnoreCase));
        Assert.All(AoiDatabase.GetImageLearningProjectImages(result.Project.ProjectId), image => Assert.True(File.Exists(image.VaultPath)));
    }

    [Fact]
    public void ImageLearningFolderImportGuidedSelectionImportsSelectedImagesIntoRole()
    {
        var project = ImageLearningProjectService.CreateProject("Guided import", "BOARD-GUIDED");
        var folder = Path.Combine(_root, "guided");
        WritePng(folder, "guided-1.png", 60);
        WritePng(folder, "guided-2.jpeg", 70);

        var result = ImageLearningFolderImportService.ImportGuidedSelection(
            project.ProjectId,
            ImageLearningImageRole.OkLearning,
            new[] { folder },
            importedBy: "Engineer01 [Engineer]");

        var summary = Assert.Single(result.RoleSummaries);
        Assert.Equal(ImageLearningImageRole.OkLearning, summary.Role);
        Assert.Equal(2, summary.ImportedCount);
        Assert.Equal(2, result.CountsByRole[ImageLearningImageRole.OkLearning]);
        Assert.All(summary.Results, item => Assert.True(item.Imported));
    }

    [Fact]
    public void ImageLearningFolderImportDuplicateHandlingWarnsDuplicateAlreadyExistsInRole()
    {
        var project = ImageLearningProjectService.CreateProject("Duplicate guided import", "BOARD-DUP");
        var folder = Path.Combine(_root, "duplicates");
        var image = WritePng(folder, "same.png", 80);

        ImageLearningFolderImportService.ImportGuidedSelection(project.ProjectId, ImageLearningImageRole.OkLearning, new[] { image });
        var duplicate = ImageLearningFolderImportService.ImportGuidedSelection(project.ProjectId, ImageLearningImageRole.OkLearning, new[] { image });

        var summary = Assert.Single(duplicate.RoleSummaries);
        Assert.Equal(1, summary.DuplicateCount);
        Assert.Contains(duplicate.Warnings, warning => warning.Contains("Duplicate ignored", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(duplicate.Warnings, warning => warning.Contains("OK Learning", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, duplicate.CountsByRole[ImageLearningImageRole.OkLearning]);
    }

    [Fact]
    public void ImageLearningFolderImportUnsupportedFileHandlingSkipsWithWarning()
    {
        var project = ImageLearningProjectService.CreateProject("Unsupported guided import", "BOARD-UNSUPPORTED");
        var folder = Path.Combine(_root, "unsupported");
        Directory.CreateDirectory(folder);
        var textFile = Path.Combine(folder, "readme.txt");
        File.WriteAllText(textFile, "not an image");

        var result = ImageLearningFolderImportService.ImportGuidedSelection(
            project.ProjectId,
            ImageLearningImageRole.OkLearning,
            new[] { folder });

        var summary = Assert.Single(result.RoleSummaries);
        Assert.Equal(0, summary.ImportedCount);
        Assert.Equal(1, summary.UnsupportedCount);
        Assert.Contains(result.Warnings, warning => warning.Contains("Unsupported image skipped", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Warnings, warning => warning.Contains("PNG, JPG, and JPEG", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ImageLearningFolderImportOptionalTruthCsvParsesValidationTruthAndNotes()
    {
        var projectFolder = CreateConventionFolder("TruthBoard");
        WritePng(Path.Combine(projectFolder, "ok_validation"), "review-me.png", 90);
        WritePng(Path.Combine(projectFolder, "ng_validation"), "known-bad.png", 100);
        File.WriteAllText(
            Path.Combine(projectFolder, "image_truth.csv"),
            "image,truth,notes" + Environment.NewLine +
            "ok_validation/review-me.png,UNKNOWN,lighting edge case" + Environment.NewLine +
            "ng_validation/known-bad.png,NG,known bridge sample");

        var result = ImageLearningFolderImportService.ImportProjectFolder(
            projectFolder,
            "Engineer01 [Engineer]",
            ImageLearningEvidenceMode.CustomerData);
        var images = AoiDatabase.GetImageLearningProjectImages(result.Project.ProjectId);

        var review = Assert.Single(images, image => image.FileName == "review-me.png");
        var knownBad = Assert.Single(images, image => image.FileName == "known-bad.png");
        Assert.Equal("UNKNOWN", review.ImageLevelTruth);
        Assert.Equal("lighting edge case", review.Notes);
        Assert.Equal("NG", knownBad.ImageLevelTruth);
        Assert.Equal("known bridge sample", knownBad.Notes);
    }

    [Fact]
    public void ImageLearningFolderImportCommandOutputsProjectIdCountsWarningsAndNextCommand()
    {
        var projectFolder = CreateConventionFolder("CliBoard");
        WritePng(Path.Combine(projectFolder, "golden"), "golden.png", 110);
        WritePng(Path.Combine(projectFolder, "ok_learning"), "ok.png", 120);
        File.WriteAllText(Path.Combine(projectFolder, "ok_validation", "notes.doc"), "unsupported");

        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = ImageLearningProjectImportCommand.Execute(
            [
                "import-image-learning-project",
                "--project-folder",
                projectFolder,
                "--operator",
                "Engineer01 [Engineer]",
                "--evidence-mode",
                "InternalDemo",
            ],
            output,
            error);
        var text = output.ToString();

        Assert.Equal(0, exitCode);
        Assert.Contains("projectId:", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("counts by role:", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Unsupported image skipped", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("next suggested command:", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(string.Empty, error.ToString());
        Assert.NotNull(AiTrainingSetupService.GetSnapshot(UserRole.Engineer).Project);
    }

    private string CreateConventionFolder(string name)
    {
        var projectFolder = Path.Combine(_root, name);
        foreach (var folder in ImageLearningFolderImportService.ConventionFolders.Values)
            Directory.CreateDirectory(Path.Combine(projectFolder, folder));

        return projectFolder;
    }

    private static string WritePng(string folder, string fileName, int value)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, fileName);
        const int width = 4;
        const int height = 4;
        var pixels = new byte[width * height * 4];
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = (byte)Math.Clamp(value, 0, 255);
            pixels[offset + 1] = (byte)Math.Clamp(value + 11, 0, 255);
            pixels[offset + 2] = (byte)Math.Clamp(value + 23, 0, 255);
            pixels[offset + 3] = 255;
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
}
