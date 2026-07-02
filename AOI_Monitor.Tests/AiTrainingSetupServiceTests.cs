using System.Windows.Media;
using System.Windows.Media.Imaging;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Xunit;

namespace AOI_Monitor.Tests;

public sealed class AiTrainingSetupServiceTests : IDisposable
{
    private readonly string _root;
    private readonly ImageOnlyPcbLearningOptions _options = new()
    {
        InputWidth = 24,
        InputHeight = 24,
        AlignmentSearchRadiusPixels = 4,
        MinimumAnomalyAreaPixels = 4,
        DefaultLearnedThreshold = 4.0,
    };

    public AiTrainingSetupServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AOI_Monitor_AiTrainingSetup_Tests", Guid.NewGuid().ToString("N"));
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
            System.Diagnostics.Trace.WriteLine("AI Training Setup test cleanup skipped because the temporary folder was still in use.");
        }
        catch (UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.WriteLine("AI Training Setup test cleanup skipped because the temporary folder was not accessible.");
        }
    }

    [Fact]
    public void AiTrainingSetupEmptyProjectShowsMissingImageGuidance()
    {
        AiTrainingSetupService.CreateTrainingProject("Empty setup", "BOARD-SETUP", UserRole.Engineer, "Engineer01 [Engineer]");

        var snapshot = AiTrainingSetupService.GetSnapshot(UserRole.Engineer);

        Assert.False(snapshot.CanRunLearning);
        Assert.Contains("Golden / Reference", snapshot.Guidance, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("5 OK Learning", snapshot.Guidance, StringComparison.OrdinalIgnoreCase);
        Assert.All(snapshot.RoleCards, card => Assert.Equal(0, card.ImageCount));
    }

    [Fact]
    public void AiTrainingSetupEnoughOkImagesEnablesLearnButton()
    {
        AiTrainingSetupService.CreateTrainingProject("Enough OK", "BOARD-SETUP", UserRole.Engineer, "Engineer01 [Engineer]");
        ImportOkLearningImages();

        var snapshot = AiTrainingSetupService.GetSnapshot(UserRole.Engineer);

        Assert.True(snapshot.CanRunLearning);
        Assert.Contains(snapshot.RoleCards, card => card.Role == ImageLearningImageRole.OkLearning && card.ImageCount == 5);
    }

    [Fact]
    public void AiTrainingSetupCalibrationDisabledUntilOkValidationImagesExist()
    {
        AiTrainingSetupService.CreateTrainingProject("Calibration gate", "BOARD-SETUP", UserRole.Engineer, "Engineer01 [Engineer]");
        ImportOkLearningImages();

        var beforeValidation = AiTrainingSetupService.GetSnapshot(UserRole.Engineer);
        AiTrainingSetupService.ImportImages(
            ImageLearningImageRole.OkValidation,
            new[] { WritePng("ok-validation.png", 90) },
            UserRole.Engineer,
            "Engineer01 [Engineer]");
        var afterValidation = AiTrainingSetupService.GetSnapshot(UserRole.Engineer);

        Assert.False(beforeValidation.CanRunCalibration);
        Assert.True(afterValidation.CanRunCalibration);
    }

    [Fact]
    public void AiTrainingSetupRoleFolderImportShowsOkValidationMetric()
    {
        AiTrainingSetupService.CreateTrainingProject("Role folder import", "BOARD-SETUP", UserRole.Engineer, "Engineer01 [Engineer]");
        var okValidationFolder = Path.Combine(_root, "ok-validation-folder");
        WritePng(Path.Combine(okValidationFolder, "ok-validation-1.png"), 92);
        WritePng(Path.Combine(okValidationFolder, "ok-validation-2.jpg"), 94);

        var results = AiTrainingSetupService.ImportImages(
            ImageLearningImageRole.OkValidation,
            new[] { okValidationFolder },
            UserRole.Engineer,
            "Engineer01 [Engineer]");
        var snapshot = AiTrainingSetupService.GetSnapshot(UserRole.Engineer);

        Assert.Equal(2, results.Count(result => result.Imported));
        Assert.Contains(snapshot.RoleCards, card =>
            card.Role == ImageLearningImageRole.OkValidation &&
            card.ImageCount == 2 &&
            string.Equals(card.LastFolder, okValidationFolder, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(snapshot.MetricCards, card =>
            card.Label == "OK validation images" &&
            card.Value == "2");
    }

    [Fact]
    public void AiTrainingSetupFalseCallReductionMetricUsesRelativeReduction()
    {
        AiTrainingSetupService.CreateTrainingProject("False-call metric", "BOARD-SETUP", UserRole.Engineer, "Engineer01 [Engineer]");
        var state = AiTrainingSetupService.LoadState();
        state.FalseCallsBeforeLearning = 2;
        state.FalseCallsAfterLearning = 1;
        state.FalseCallRateBeforeLearning = 0.5;
        state.FalseCallRateAfterLearning = 0.25;
        AiTrainingSetupService.SaveState(state);

        var snapshot = AiTrainingSetupService.GetSnapshot(UserRole.Engineer);

        Assert.Contains(snapshot.MetricCards, card =>
            card.Label == "False-call reduction" &&
            card.Value == "50.0 %");
    }

    [Fact]
    public void AiTrainingSetupOperatorCannotRunLearning()
    {
        AiTrainingSetupService.CreateTrainingProject("Operator denied", "BOARD-SETUP", UserRole.Engineer, "Engineer01 [Engineer]");
        ImportOkLearningImages();

        var ex = Assert.Throws<UnauthorizedAccessException>(
            () => AiTrainingSetupService.RunLearning(UserRole.Operator, _options, "Operator01 [Operator]"));

        Assert.Contains("requires a higher local role", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AiTrainingSetupEngineerCanRunLearning()
    {
        AiTrainingSetupService.CreateTrainingProject("Engineer learns", "BOARD-SETUP", UserRole.Engineer, "Engineer01 [Engineer]");
        ImportOkLearningImages();
        AiTrainingSetupService.ImportImages(
            ImageLearningImageRole.OkValidation,
            new[] { WritePng("ok-validation.png", 101) },
            UserRole.Engineer,
            "Engineer01 [Engineer]");

        var outcome = AiTrainingSetupService.RunLearning(UserRole.Engineer, _options, "Engineer01 [Engineer]");
        var snapshot = AiTrainingSetupService.GetSnapshot(UserRole.Engineer);

        Assert.NotNull(outcome.LearningResult.Model);
        Assert.False(string.IsNullOrWhiteSpace(outcome.LearningResult.Model.ModelId));
        Assert.NotNull(snapshot.Model);
        Assert.True(snapshot.State.FalseCallRateAfterLearning.HasValue);
        Assert.Contains(snapshot.Timeline, item => item.Label == "Model learned" && item.IsComplete);
    }

    [Fact]
    public void AiTrainingSetupAdminCanRunLearning()
    {
        AiTrainingSetupService.CreateTrainingProject("Admin learns", "BOARD-SETUP", UserRole.Admin, "Admin01 [Admin]");
        ImportOkLearningImages();

        var outcome = AiTrainingSetupService.RunLearning(UserRole.Admin, _options, "Admin01 [Admin]");

        Assert.NotNull(outcome.LearningResult.Model);
        Assert.False(string.IsNullOrWhiteSpace(outcome.LearningResult.Model.ModelId));
    }

    private void ImportOkLearningImages()
    {
        for (var i = 0; i < 5; i++)
        {
            AiTrainingSetupService.ImportImages(
                ImageLearningImageRole.OkLearning,
                new[] { WritePng($"ok-learning-{i}.png", 80 + i) },
                UserRole.Engineer,
                "Engineer01 [Engineer]");
        }
    }

    private string WritePng(string fileName, int value)
    {
        var path = Path.IsPathRooted(fileName)
            ? fileName
            : Path.Combine(_root, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        const int width = 8;
        const int height = 8;
        var pixels = new byte[width * height * 4];
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = (byte)Math.Clamp(value, 0, 255);
            pixels[offset + 1] = (byte)Math.Clamp(value + 8, 0, 255);
            pixels[offset + 2] = (byte)Math.Clamp(value + 16, 0, 255);
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
