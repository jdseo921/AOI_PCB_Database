using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Xunit;

namespace AOI_Monitor.Tests;

public sealed class TrainedDataManagementServiceTests : IDisposable
{
    private readonly string _root;

    public TrainedDataManagementServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AOI_Monitor_TrainedDataManagement_Tests", Guid.NewGuid().ToString("N"));
        AoiDatabase.ConfigureStorageRoot(_root);
        AoiDatabase.AuditOperatorProvider = null;
        AoiDatabase.AuditUserIdProvider = null;
        AoiDatabase.AuditUserRoleProvider = null;
        AoiDatabase.AuditStationProvider = null;
        AoiDatabase.Initialize();
        InspectionModelConfigurationService.Save(new InspectionModelConfiguration());
        AiTrainingSetupService.ResetForTests();
    }

    public void Dispose()
    {
        InspectionModelConfigurationService.Save(new InspectionModelConfiguration());
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
            System.Diagnostics.Trace.WriteLine("Trained data management test cleanup skipped because the temporary folder was still in use.");
        }
        catch (UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.WriteLine("Trained data management test cleanup skipped because the temporary folder was not accessible.");
        }
    }

    [Fact]
    public void TrainedDataManagementModelVersionListShowsModelHistory()
    {
        var project = CreateProject("Version history");
        var first = CreateModel(project, "MODEL-V1", DateTime.UtcNow.AddMinutes(-2), falseCallRate: 0.30, possibleEscapeRate: 0.0);
        var second = CreateModel(project, "MODEL-V2", DateTime.UtcNow.AddMinutes(-1), falseCallRate: 0.10, possibleEscapeRate: 0.0);

        var snapshot = ImageLearningTrainedDataManagementService.GetSnapshot(UserRole.Engineer);
        var rows = snapshot.ModelVersions.Where(row => row.ProjectId == project.ProjectId).ToArray();

        Assert.Equal(2, rows.Length);
        Assert.Contains(rows, row => row.ModelId == first.ModelId && row.VersionDisplay.StartsWith("Version 1", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(rows, row => row.ModelId == second.ModelId && row.VersionDisplay.StartsWith("Version 2", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(rows, row => row.ModelId == second.ModelId && row.MetricsSummary.Contains("false calls", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(snapshot.ModelComparisons, row => row.ModelId == second.ModelId && row.ComparisonSummary.Contains("possible escapes", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TrainedDataManagementSetActiveModelUpdatesActiveHistory()
    {
        var project = CreateProject("Set active");
        var model = CreateModel(project, "MODEL-ACTIVE", DateTime.UtcNow, falseCallRate: 0.05, possibleEscapeRate: 0.0);

        var active = ImageLearningTrainedDataManagementService.SetModelAsActive(
            model.ModelId,
            UserRole.Engineer,
            "Engineer01 [Engineer]");
        var snapshot = ImageLearningTrainedDataManagementService.GetSnapshot(UserRole.Engineer);

        Assert.Equal(model.ModelId, active.ModelId);
        Assert.Contains(model.ModelId, snapshot.ActiveModelDisplay, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(snapshot.ModelVersions, row => row.ModelId == model.ModelId && row.IsActive);
    }

    [Fact]
    public void TrainedDataManagementArchiveProjectMarksProjectArchived()
    {
        var project = CreateProject("Archive me");

        var archived = ImageLearningTrainedDataManagementService.ArchiveProject(
            project.ProjectId,
            UserRole.Admin,
            "Admin01 [Admin]",
            "Unit test archive.");
        var snapshot = ImageLearningTrainedDataManagementService.GetSnapshot(UserRole.Admin);

        Assert.True(archived.IsArchived);
        Assert.Contains(snapshot.Projects, row => row.ProjectId == project.ProjectId && row.IsArchived && row.Status == "Archived");
    }

    [Fact]
    public void TrainedDataManagementDeleteGeneratedArtifactsRequiresAdmin()
    {
        var project = CreateProject("Delete role gate");
        var model = CreateModel(project, "MODEL-DELETE-GATE", DateTime.UtcNow, falseCallRate: 0.05, possibleEscapeRate: 0.0);

        var ex = Assert.Throws<UnauthorizedAccessException>(() =>
            ImageLearningTrainedDataManagementService.DeleteGeneratedArtifacts(
                model.ModelId,
                UserRole.Engineer,
                explicitConfirmation: true,
                "Engineer01 [Engineer]"));
        var result = ImageLearningTrainedDataManagementService.DeleteGeneratedArtifacts(
            model.ModelId,
            UserRole.Admin,
            explicitConfirmation: true,
            "Admin01 [Admin]");

        Assert.Contains("requires a higher local role", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ImageLearningProjectService.ExpectedArtifactFileNames.Count, result.FilesDeleted);
        Assert.Empty(AoiDatabase.GetLearnedPcbVisualModel(model.ModelId)!.Artifacts);
    }

    [Fact]
    public void TrainedDataManagementArtifactCleanupDoesNotDeleteOriginalImagePaths()
    {
        var project = CreateProject("Preserve images");
        var originalPath = WritePng("customer-original.png", 90);
        ImageLearningProjectService.ImportImageFiles(
            project.ProjectId,
            ImageLearningImageRole.OkLearning,
            new[] { originalPath },
            importedBy: "Engineer01 [Engineer]");
        var imported = AoiDatabase.GetImageLearningProjectImages(project.ProjectId, ImageLearningImageRole.OkLearning).Single();
        var model = CreateModel(project, "MODEL-PRESERVE-IMAGES", DateTime.UtcNow, falseCallRate: 0.05, possibleEscapeRate: 0.0);

        var result = ImageLearningTrainedDataManagementService.DeleteGeneratedArtifacts(
            model.ModelId,
            UserRole.Admin,
            explicitConfirmation: true,
            "Admin01 [Admin]");

        Assert.True(File.Exists(originalPath));
        Assert.True(File.Exists(imported.OriginalPath));
        Assert.True(File.Exists(imported.VaultPath));
        Assert.Equal(ImageLearningProjectService.ExpectedArtifactFileNames.Count, result.FilesDeleted);
    }

    private ImageLearningProject CreateProject(string name)
        => ImageLearningProjectService.CreateProject(
            name,
            "BOARD-TRAINED-DATA",
            ImageLearningEvidenceMode.SyntheticDemo,
            "Engineer01 [Engineer]");

    private LearnedPcbVisualModel CreateModel(
        ImageLearningProject project,
        string modelId,
        DateTime createdAtUtc,
        double falseCallRate,
        double possibleEscapeRate)
    {
        var artifactFolder = Path.Combine(_root, "artifacts", modelId);
        Directory.CreateDirectory(artifactFolder);
        var artifacts = ImageLearningProjectService.ExpectedArtifactFileNames
            .Select(name =>
            {
                var path = Path.Combine(artifactFolder, name);
                WriteArtifact(path, name);
                return new LearnedPcbVisualModelArtifact
                {
                    ModelId = modelId,
                    ArtifactName = name,
                    ArtifactPath = path,
                    Sha256 = ComputeSha256(path),
                    CreatedAtUtc = createdAtUtc,
                };
            })
            .ToList();

        var model = new LearnedPcbVisualModel
        {
            ModelId = modelId,
            ModelVersion = ImageOnlyPcbLearningService.EngineName,
            CreatedAtUtc = createdAtUtc,
            ProjectId = project.ProjectId,
            GoldenCount = 1,
            OkLearningCount = 5,
            OkValidationCount = 3,
            InputWidth = 32,
            InputHeight = 32,
            AlignmentMode = "TranslationSearch",
            BrightnessNormalizationMode = "MeanShift",
            LearnedThreshold = 4.25,
            FalseCallTarget = 0.05,
            FalseCallRate = falseCallRate,
            PossibleEscapeRate = possibleEscapeRate,
            EvidenceMode = project.EvidenceMode,
            CreatedBy = "Engineer01 [Engineer]",
            Artifacts = artifacts,
        };
        ImageLearningProjectService.SaveModelMetadata(model);
        AoiDatabase.RecordImageLearningCalibrationResult(new ImageLearningCalibrationResult
        {
            ProjectId = project.ProjectId,
            ModelId = model.ModelId,
            CreatedAtUtc = createdAtUtc,
            OkValidationCount = model.OkValidationCount,
            NgValidationCount = 1,
            LearnedThreshold = model.LearnedThreshold,
            FalseCallTarget = model.FalseCallTarget,
            FalseCallRate = falseCallRate,
            PossibleEscapeRate = possibleEscapeRate,
            Status = "OK",
            Summary = "Unit-test calibration run.",
        });
        return AoiDatabase.GetLearnedPcbVisualModel(model.ModelId)!;
    }

    private void WriteArtifact(string path, string artifactName)
    {
        if (artifactName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            WritePng(path, 120);
            return;
        }

        if (artifactName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            File.WriteAllText(path, """{"schemaVersion":"1.0","status":"OK"}""");
            return;
        }

        File.WriteAllText(path, "image,score,status\nsample.png,0.1,OK\n");
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

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
