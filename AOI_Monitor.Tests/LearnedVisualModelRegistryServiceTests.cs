using System.Windows.Media;
using System.Windows.Media.Imaging;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Xunit;

namespace AOI_Monitor.Tests;

public sealed class LearnedVisualModelRegistryServiceTests : IDisposable
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

    public LearnedVisualModelRegistryServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AOI_Monitor_LearnedVisualModel_Tests", Guid.NewGuid().ToString("N"));
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
            System.Diagnostics.Trace.WriteLine("Learned visual model registry test cleanup skipped because the temporary folder was still in use.");
        }
        catch (UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.WriteLine("Learned visual model registry test cleanup skipped because the temporary folder was not accessible.");
        }
    }

    [Fact]
    public void LearnedVisualModelActiveSelectionCanBeSelected()
    {
        var learning = TrainStandardModel("Active selection");

        var active = LearnedVisualModelRegistryService.SetActiveLearnedVisualModel(
            learning.Model.ModelId,
            UserRole.Engineer,
            "Engineer01 [Engineer]");
        var configuration = InspectionModelConfigurationService.Load();
        var engine = InspectionEngineFactory.Create();
        var listed = LearnedVisualModelRegistryService.ListLearnedModels();

        Assert.Equal(InspectionEngineFactory.LearnedVisualEngineKey, configuration.SelectedEngineKey);
        Assert.Equal(learning.Model.ModelId, configuration.ActiveModelId);
        Assert.Equal(InspectionEngineStatus.LearnedVisualModelReady, InspectionModelConfigurationService.GetStatus(configuration));
        Assert.Equal(ImageOnlyPcbLearningService.EngineName, engine.Name);
        Assert.Equal(learning.Model.ModelId, active.ModelId);
        Assert.Contains(listed, row => row.ModelId == learning.Model.ModelId && row.IsActive);
    }

    [Fact]
    public void LearnedVisualModelRunInspectionUsesLearnedEngine()
    {
        var learning = TrainStandardModel("Run inspection");
        LearnedVisualModelRegistryService.SetActiveLearnedVisualModel(
            learning.Model.ModelId,
            UserRole.Engineer,
            "Engineer01 [Engineer]");
        var sample = WriteBoardPng("inspection-ng.png", anomaly: true, variant: 71);

        var result = InspectionEngineFactory.Create().Analyze(sample, goldenPath: null, DetectionPriority.Balanced);

        Assert.Equal(ImageOnlyPcbLearningService.EngineName, result.InspectionEngine);
        Assert.NotEqual("OK", result.Verdict);
        Assert.NotEmpty(result.Defects);
        Assert.Contains(result.Evidence, item => item.Contains("Learned from 6 OK sample images", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Evidence, item => item.Contains("False-call target", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Evidence, item => item.Contains("Recommended threshold", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LearnedVisualModelMissingModelReturnsReviewWarning()
    {
        InspectionModelConfigurationService.Save(new InspectionModelConfiguration
        {
            SelectedEngineKey = InspectionEngineFactory.LearnedVisualEngineKey,
            ActiveModelId = "MISSING-MODEL",
            ModelVersion = ImageOnlyPcbLearningService.EngineVersion,
        });
        var sample = WriteBoardPng("missing-model-sample.png");

        var result = InspectionEngineFactory.Create().Analyze(sample, goldenPath: null, DetectionPriority.Balanced);

        Assert.Equal(ImageOnlyPcbLearningService.EngineName, result.InspectionEngine);
        Assert.Equal("REVIEW", result.Verdict);
        Assert.Equal("IMAGE_LEARNING_INSPECTION_FAILED", result.ErrorCode);
        Assert.Contains("failed", result.DecisionReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LearnedVisualModelExportPackageIncludesMetadataAndArtifacts()
    {
        var learning = TrainStandardModel("Export metadata");
        LearnedVisualModelRegistryService.SetActiveLearnedVisualModel(
            learning.Model.ModelId,
            UserRole.Engineer,
            "Engineer01 [Engineer]");
        var configuration = InspectionModelConfigurationService.Load();

        var package = CustomerValidationPackageService.CreatePackage(new CustomerValidationPackageRequest
        {
            OutputRoot = Path.Combine(_root, "exports"),
            StationId = "AOI-TEST",
            OperatorId = "Engineer01",
            OperatorRole = UserRole.Engineer.ToString(),
            BoardModel = "BOARD-IMAGE-ONLY",
            LotId = "LOT-TEST",
            ModelId = configuration.ActiveModelId,
            ModelSha256 = configuration.ActiveModelSha256,
            ModelValidationStatus = configuration.ActiveModelValidationStatus,
            EngineName = ImageOnlyPcbLearningService.EngineName,
            ModelVersion = configuration.ModelVersion,
            ModelFileName = Path.GetFileName(configuration.ModelFilePath),
            Metrics = BatchValidationService.CalculateMetrics(Array.Empty<BatchTestRow>()),
            PerformanceSummary = BatchValidationService.CalculatePerformanceSummary(Array.Empty<BatchTestRow>()),
            Rows = Array.Empty<BatchTestRow>(),
            LearnedVisualModel = LearnedVisualModelRegistryService.GetActiveSummary(),
        });

        var manifestJson = File.ReadAllText(package.ManifestPath);
        var learnedFolder = Path.Combine(package.PackageFolder, "learned_visual_model");

        Assert.NotNull(package.Manifest.LearnedVisualModel);
        Assert.Contains(learning.Model.ModelId, manifestJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("learnedVisualModel", manifestJson, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(learnedFolder, "learned_visual_model_summary.json")));
        Assert.True(File.Exists(Path.Combine(learnedFolder, "learned_reference.png")));
        Assert.True(File.Exists(Path.Combine(learnedFolder, "tolerance_map.png")));
    }

    [Fact]
    public void LearnedVisualModelBatchValidationUsesLearnedEngineAndCalculatesRates()
    {
        var learning = TrainStandardModel("Batch validation");
        LearnedVisualModelRegistryService.SetActiveLearnedVisualModel(
            learning.Model.ModelId,
            UserRole.Engineer,
            "Engineer01 [Engineer]");
        var okPath = WriteBoardPng("batch-ok.png", brightnessShift: 6, variant: 81);
        var ngPath = WriteBoardPng("batch-ng.png", anomaly: true, variant: 82);
        var engine = InspectionEngineFactory.Create();

        var rows = new[]
        {
            BatchValidationService.ToRow(
                okPath,
                new GroundTruthEntry("OK", null, string.Empty, "Top", string.Empty, string.Empty, string.Empty, "LOT-TEST", "BOARD-IMAGE-ONLY", string.Empty, okPath),
                engine.Analyze(okPath, goldenPath: null, DetectionPriority.Balanced)),
            BatchValidationService.ToRow(
                ngPath,
                new GroundTruthEntry("NG", null, "Anomaly", "Top", string.Empty, string.Empty, string.Empty, "LOT-TEST", "BOARD-IMAGE-ONLY", string.Empty, ngPath),
                engine.Analyze(ngPath, goldenPath: null, DetectionPriority.Balanced)),
        };

        var metrics = BatchValidationService.CalculateMetrics(rows);

        Assert.All(rows, row => Assert.Equal(ImageOnlyPcbLearningService.EngineName, row.InspectionEngine));
        Assert.InRange(metrics.FalseCallRate, 0.0, 1.0);
        Assert.InRange(metrics.PossibleEscape, 0, 1);
        Assert.Equal(2, metrics.OkCount + metrics.NgCount + metrics.ReviewCount);
    }

    private ImageOnlyPcbLearningResult TrainStandardModel(string name)
    {
        var project = ImageLearningProjectService.CreateProject(name, "BOARD-IMAGE-ONLY", ImageLearningEvidenceMode.SyntheticDemo, "Engineer01 [Engineer]");
        ImportImages(project, ImageLearningImageRole.GoldenReference, WriteBoardPng($"{name}-golden.png"));
        for (var i = 0; i < 5; i++)
            ImportImages(project, ImageLearningImageRole.OkLearning, WriteBoardPng($"{name}-ok-{i}.png", variant: i + 1));
        ImportImages(project, ImageLearningImageRole.OkValidation, WriteBoardPng($"{name}-ok-validation.png", brightnessShift: 10, variant: 21));

        return ImageOnlyPcbLearningService.TrainProject(project.ProjectId, _options, "Engineer01 [Engineer]");
    }

    private static IReadOnlyList<ImageLearningImportResult> ImportImages(
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
        bool anomaly = false)
    {
        Directory.CreateDirectory(_root);
        var safeFileName = fileName.Replace(' ', '_');
        var path = Path.Combine(_root, safeFileName);
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
