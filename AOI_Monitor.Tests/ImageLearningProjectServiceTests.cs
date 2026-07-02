using System.Windows.Media;
using System.Windows.Media.Imaging;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Xunit;

namespace AOI_Monitor.Tests;

public sealed class ImageLearningProjectServiceTests : IDisposable
{
    private readonly string _root;

    public ImageLearningProjectServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AOI_Monitor_ImageLearningProject_Tests", Guid.NewGuid().ToString("N"));
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
            System.Diagnostics.Trace.WriteLine("Image-learning project test cleanup skipped because the temporary folder was still in use.");
        }
        catch (UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.WriteLine("Image-learning project test cleanup skipped because the temporary folder was not accessible.");
        }
    }

    [Fact]
    public void CreateProjectPersistsImageLearningProjectAndAudit()
    {
        var project = ImageLearningProjectService.CreateProject(
            "Customer A image-only learning",
            "BOARD-A",
            ImageLearningEvidenceMode.CustomerData,
            "Engineer01 [Engineer]",
            "Customer image-only sample grouping.");

        var loaded = AoiDatabase.GetImageLearningProject(project.ProjectId);
        var audit = Assert.Single(AoiDatabase.GetAuditEvents(new LogFilter { ActionCategory = "IMAGE_LEARNING_PROJECT_CREATE" }));

        Assert.NotNull(loaded);
        Assert.StartsWith("ILP-", project.ProjectId, StringComparison.Ordinal);
        Assert.Equal("Customer A image-only learning", loaded!.ProjectName);
        Assert.Equal("BOARD-A", loaded.BoardModel);
        Assert.Equal(ImageLearningEvidenceMode.CustomerData, loaded.EvidenceMode);
        Assert.Equal(project.ProjectId, audit.RelatedEntityId);
    }

    [Fact]
    public void ImportImagesByRolePersistsImageOnlyMetadata()
    {
        var project = ImageLearningProjectService.CreateProject("Learning set", "BOARD-A", createdBy: "Engineer01 [Engineer]");
        var goldenPath = WritePng("golden.png", 255, 255, 255);
        var inspectionPath = WritePng("inspection.png", 32, 64, 96);

        var golden = ImageLearningProjectService.ImportImageFiles(
            project.ProjectId,
            ImageLearningImageRole.GoldenReference,
            new[] { goldenPath },
            lotId: "LOT-1",
            viewType: "Top",
            importedBy: "Engineer01 [Engineer]").Single();
        var inspection = ImageLearningProjectService.ImportImageFiles(
            project.ProjectId,
            ImageLearningImageRole.Inspection,
            new[] { inspectionPath },
            lotId: "LOT-2",
            viewType: "Bottom",
            importedBy: "Operator01 [Operator]").Single();

        var images = AoiDatabase.GetImageLearningProjectImages(project.ProjectId);

        Assert.True(golden.Imported);
        Assert.True(inspection.Imported);
        Assert.Equal(2, images.Count);
        Assert.Contains(images, image => image.Role == ImageLearningImageRole.GoldenReference && image.ImageLevelTruth == "OK");
        Assert.Contains(images, image => image.Role == ImageLearningImageRole.Inspection && image.ImageLevelTruth == "UNKNOWN");
        Assert.All(images, image =>
        {
            Assert.True(File.Exists(image.OriginalPath));
            Assert.True(File.Exists(image.VaultPath));
            Assert.Equal(2, image.Width);
            Assert.Equal(2, image.Height);
            Assert.False(string.IsNullOrWhiteSpace(image.Sha256));
        });
    }

    [Fact]
    public void DuplicateHashDetectionSkipsSecondImportInSameProject()
    {
        var project = ImageLearningProjectService.CreateProject("Duplicate check", "BOARD-DUP", createdBy: "Engineer01 [Engineer]");
        var source = WritePng("same.png", 10, 20, 30);

        var first = ImageLearningProjectService.ImportImageFiles(project.ProjectId, ImageLearningImageRole.OkLearning, new[] { source }).Single();
        var second = ImageLearningProjectService.ImportImageFiles(project.ProjectId, ImageLearningImageRole.OkLearning, new[] { source }).Single();
        var counts = ImageLearningProjectService.GetImageCountsByRole(project.ProjectId);

        Assert.True(first.Imported);
        Assert.False(second.Imported);
        Assert.True(second.IsDuplicate);
        Assert.Equal("Duplicate", second.Status);
        Assert.Equal(1, counts[ImageLearningImageRole.OkLearning]);
        Assert.Single(AoiDatabase.GetAuditEvents(new LogFilter { ActionCategory = "IMAGE_LEARNING_IMAGE_DUPLICATE" }));
    }

    [Fact]
    public void MinimumLearningSetValidationAcceptsGoldenOrFiveOkLearningImages()
    {
        var project = ImageLearningProjectService.CreateProject("Minimum learning set", "BOARD-MIN", createdBy: "Engineer01 [Engineer]");

        var empty = ImageLearningProjectService.ValidateMinimumProjectRequirements(project.ProjectId);
        Assert.False(empty.CanTrain);

        for (var i = 0; i < 4; i++)
        {
            ImageLearningProjectService.ImportImageFiles(
                project.ProjectId,
                ImageLearningImageRole.OkLearning,
                new[] { WritePng($"ok-{i}.png", (byte)(20 + i), 30, 40) });
        }

        var fourOk = ImageLearningProjectService.ValidateMinimumProjectRequirements(project.ProjectId);
        Assert.False(fourOk.CanTrain);
        Assert.Contains("5 OK Learning", fourOk.BlockingIssues.Single(), StringComparison.OrdinalIgnoreCase);

        ImageLearningProjectService.ImportImageFiles(
            project.ProjectId,
            ImageLearningImageRole.OkLearning,
            new[] { WritePng("ok-4.png", 24, 30, 40) });

        var fiveOk = ImageLearningProjectService.ValidateMinimumProjectRequirements(project.ProjectId);
        Assert.True(fiveOk.CanTrain);

        var goldenProject = ImageLearningProjectService.CreateProject("Golden minimum", "BOARD-GOLDEN");
        ImageLearningProjectService.ImportImageFiles(
            goldenProject.ProjectId,
            ImageLearningImageRole.GoldenReference,
            new[] { WritePng("golden-min.png", 200, 201, 202) });

        var withGolden = ImageLearningProjectService.ValidateMinimumProjectRequirements(goldenProject.ProjectId);
        Assert.True(withGolden.CanTrain);
    }

    [Fact]
    public void ReadCountsByRoleReturnsEveryImageLearningRole()
    {
        var project = ImageLearningProjectService.CreateProject("Role counts", "BOARD-COUNT");

        ImageLearningProjectService.ImportImageFiles(project.ProjectId, ImageLearningImageRole.GoldenReference, new[] { WritePng("role-golden.png", 1, 2, 3) });
        ImageLearningProjectService.ImportImageFiles(project.ProjectId, ImageLearningImageRole.OkLearning, new[] { WritePng("role-ok-learn.png", 4, 5, 6) });
        ImageLearningProjectService.ImportImageFiles(project.ProjectId, ImageLearningImageRole.OkValidation, new[] { WritePng("role-ok-val.png", 7, 8, 9) });
        ImageLearningProjectService.ImportImageFiles(project.ProjectId, ImageLearningImageRole.Inspection, new[] { WritePng("role-inspect.png", 10, 11, 12) });
        ImageLearningProjectService.ImportImageFiles(project.ProjectId, ImageLearningImageRole.NgValidation, new[] { WritePng("role-ng.png", 13, 14, 15) });

        var counts = ImageLearningProjectService.GetImageCountsByRole(project.ProjectId);

        Assert.Equal(Enum.GetValues<ImageLearningImageRole>().Length, counts.Count);
        Assert.Equal(1, counts[ImageLearningImageRole.GoldenReference]);
        Assert.Equal(1, counts[ImageLearningImageRole.OkLearning]);
        Assert.Equal(1, counts[ImageLearningImageRole.OkValidation]);
        Assert.Equal(1, counts[ImageLearningImageRole.Inspection]);
        Assert.Equal(1, counts[ImageLearningImageRole.NgValidation]);
    }

    [Fact]
    public void PersistAndReadModelMetadataWithExpectedArtifacts()
    {
        var project = ImageLearningProjectService.CreateProject("Model metadata", "BOARD-MODEL");
        var artifactFolder = Path.Combine(_root, "runtime-artifacts", "ILM-001");
        var model = new LearnedPcbVisualModel
        {
            ModelId = "ILM-001",
            ModelVersion = "2026.07.02",
            ProjectId = project.ProjectId,
            GoldenCount = 2,
            OkLearningCount = 12,
            OkValidationCount = 5,
            InputWidth = 1024,
            InputHeight = 768,
            AlignmentMode = "FiducialRigid",
            BrightnessNormalizationMode = "PerImageMedian",
            LearnedThreshold = 0.42,
            FalseCallTarget = 0.03,
            FalseCallRate = 0.025,
            PossibleEscapeRate = 0.01,
            EvidenceMode = ImageLearningEvidenceMode.InternalDemo,
            CreatedBy = "Engineer01 [Engineer]",
            Artifacts = ImageLearningProjectService.CreateExpectedArtifactRecords("ILM-001", artifactFolder).ToList(),
        };

        var saved = ImageLearningProjectService.SaveModelMetadata(model);
        var loaded = AoiDatabase.GetLearnedPcbVisualModel("ILM-001");

        Assert.NotNull(loaded);
        Assert.Equal(saved.Id, loaded!.Id);
        Assert.Equal("2026.07.02", loaded.ModelVersion);
        Assert.Equal(12, loaded.OkLearningCount);
        Assert.Equal(ImageLearningEvidenceMode.InternalDemo, loaded.EvidenceMode);
        Assert.Contains(loaded.Artifacts, artifact => artifact.ArtifactName == "learned_reference.png");
        Assert.Contains(loaded.Artifacts, artifact => artifact.ArtifactName == "tolerance_map.png");
        Assert.Contains(loaded.Artifacts, artifact => artifact.ArtifactName == "anomaly_threshold_map.png");
        Assert.Contains(loaded.Artifacts, artifact => artifact.ArtifactName == "learning_summary.json");
        Assert.Contains(loaded.Artifacts, artifact => artifact.ArtifactName == "alignment_summary.csv");
        Assert.Contains(loaded.Artifacts, artifact => artifact.ArtifactName == "threshold_sweep.csv");
        Assert.Single(AoiDatabase.GetAuditEvents(new LogFilter { ActionCategory = "IMAGE_LEARNING_MODEL_CREATE" }));
    }

    [Fact]
    public void PersistAndReadAnomalyRegionsWithoutManualDefectLabels()
    {
        var project = ImageLearningProjectService.CreateProject("Anomaly regions", "BOARD-ANOM");
        var import = ImageLearningProjectService.ImportImageFiles(
            project.ProjectId,
            ImageLearningImageRole.Inspection,
            new[] { WritePng("inspect-anomaly.png", 90, 91, 92) }).Single();
        var model = ImageLearningProjectService.SaveModelMetadata(new LearnedPcbVisualModel
        {
            ModelId = "ILM-ANOM",
            ModelVersion = "1.0.0",
            ProjectId = project.ProjectId,
            InputWidth = 2,
            InputHeight = 2,
            CreatedBy = "Engineer01 [Engineer]",
        });
        var result = new ImageLearningInspectionResult
        {
            ResultId = "ILR-ANOM-001",
            ProjectId = project.ProjectId,
            ModelId = model.ModelId,
            ProjectImageId = import.Image!.Id,
            ImageSha256 = import.Image.Sha256,
            ImagePath = import.Image.VaultPath,
            Verdict = "REVIEW",
            AnomalyScore = 0.81,
            DecisionReason = "Image-only anomaly score exceeded learned threshold.",
            OperatorId = "Operator01 [Operator]",
            AnomalyRegions =
            [
                new ImageLearningAnomalyRegion
                {
                    RegionId = "A-001",
                    X = 0.10,
                    Y = 0.20,
                    Width = 0.30,
                    Height = 0.40,
                    Score = 0.81,
                    AreaPixels = 12,
                    Confidence = 0.73,
                    Severity = "REVIEW",
                    RegionType = "VisualAnomaly",
                    Reason = "Localized image-only anomaly.",
                    Notes = "No manual defect class label required.",
                },
            ],
        };

        var id = AoiDatabase.RecordImageLearningInspectionResult(result);
        var loaded = AoiDatabase.GetImageLearningInspectionResult(id);

        Assert.NotNull(loaded);
        Assert.Equal("ILR-ANOM-001", loaded!.ResultId);
        Assert.Equal("REVIEW", loaded.Verdict);
        var region = Assert.Single(loaded.AnomalyRegions);
        Assert.Equal("A-001", region.RegionId);
        Assert.Equal("VisualAnomaly", region.RegionType);
        Assert.Equal(0.81, region.Score, precision: 3);
        Assert.Equal(12, region.AreaPixels);
        Assert.Equal(0.73, region.Confidence, precision: 3);
        Assert.Contains("Localized image-only", region.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No manual defect class", region.Notes, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ArchiveAndDeleteProjectMetadataDoesNotDeleteSourceCustomerImages()
    {
        var project = ImageLearningProjectService.CreateProject("Archive cleanup", "BOARD-ARCHIVE");
        var source = WritePng("source-customer.png", 101, 102, 103);
        ImageLearningProjectService.ImportImageFiles(project.ProjectId, ImageLearningImageRole.GoldenReference, new[] { source });

        ImageLearningProjectService.ArchiveProject(project.ProjectId, "Admin01 [Admin]", "Customer requested project metadata cleanup.");
        var archived = AoiDatabase.GetImageLearningProject(project.ProjectId);
        ImageLearningProjectService.DeleteProjectMetadata(project.ProjectId, "Admin01 [Admin]", "Remove local project metadata only.");

        Assert.NotNull(archived);
        Assert.True(archived!.IsArchived);
        Assert.True(File.Exists(source));
        Assert.Null(AoiDatabase.GetImageLearningProject(project.ProjectId));
    }

    private string WritePng(string fileName, byte red, byte green, byte blue)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, fileName);
        var pixels = new byte[2 * 2 * 4];
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = blue;
            pixels[offset + 1] = green;
            pixels[offset + 2] = red;
            pixels[offset + 3] = 255;
        }

        var bitmap = BitmapSource.Create(
            2,
            2,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            2 * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }
}
