using System.Windows.Media;
using System.Windows.Media.Imaging;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Xunit;

namespace AOI_Monitor.Tests;

public sealed class ImageOnlyPcbLearningServiceTests : IDisposable
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

    public ImageOnlyPcbLearningServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AOI_Monitor_ImageOnlyPcbLearning_Tests", Guid.NewGuid().ToString("N"));
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
            System.Diagnostics.Trace.WriteLine("Image-only PCB learning test cleanup skipped because the temporary folder was still in use.");
        }
        catch (UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.WriteLine("Image-only PCB learning test cleanup skipped because the temporary folder was not accessible.");
        }
    }

    [Fact]
    public void ImageOnlyPcbLearningModelArtifactGenerationCreatesVisibleRuntimeFiles()
    {
        var project = CreateLearningProject("Artifact generation");
        ImportStandardTrainingSet(project);
        ImportImages(project, ImageLearningImageRole.OkValidation, WriteBoardPng("ok-validation-bright.png", brightnessShift: 18, variant: 11));

        var result = ImageOnlyPcbLearningService.TrainProject(project.ProjectId, _options, "Engineer01 [Engineer]");
        var loaded = AoiDatabase.GetLearnedPcbVisualModel(result.Model.ModelId);

        Assert.NotNull(loaded);
        Assert.Equal(ImageOnlyPcbLearningService.EngineName, result.Model.ModelVersion);
        Assert.Equal(1, result.Model.OkValidationCount);
        Assert.True(File.Exists(Path.Combine(result.ArtifactFolder, "learned_reference.png")));
        Assert.True(File.Exists(Path.Combine(result.ArtifactFolder, "tolerance_map.png")));
        Assert.True(File.Exists(Path.Combine(result.ArtifactFolder, "anomaly_threshold_map.png")));
        Assert.True(File.Exists(Path.Combine(result.ArtifactFolder, "learning_summary.json")));
        Assert.True(File.Exists(Path.Combine(result.ArtifactFolder, "alignment_summary.csv")));
        Assert.True(File.Exists(Path.Combine(result.ArtifactFolder, "threshold_sweep.csv")));
        Assert.All(result.Model.Artifacts, artifact => Assert.False(string.IsNullOrWhiteSpace(artifact.Sha256)));
    }

    [Fact]
    public void ImageOnlyPcbLearningAlignmentOffsetsSavedForShiftedOkLearningImage()
    {
        var project = CreateLearningProject("Alignment offsets");
        ImportImages(project, ImageLearningImageRole.GoldenReference, WriteBoardPng("golden.png"));
        ImportImages(project, ImageLearningImageRole.OkLearning, WriteBoardPng("shifted-ok.png", offsetX: 2, offsetY: -1, variant: 1));
        for (var i = 0; i < 4; i++)
            ImportImages(project, ImageLearningImageRole.OkLearning, WriteBoardPng($"ok-{i}.png", variant: i + 2));

        var result = ImageOnlyPcbLearningService.TrainProject(project.ProjectId, _options);
        var shifted = Assert.Single(result.AlignmentRecords, row => row.FileName == "shifted-ok.png");
        var alignmentCsv = File.ReadAllText(Path.Combine(result.ArtifactFolder, "alignment_summary.csv"));

        Assert.True(Math.Abs(shifted.OffsetX) > 0 || Math.Abs(shifted.OffsetY) > 0);
        Assert.Contains("shifted-ok.png", alignmentCsv, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ImageOnlyPcbLearningAnomalyDetectedOnChangedInspectionImage()
    {
        var project = CreateLearningProject("Anomaly inspection");
        ImportStandardTrainingSet(project);
        ImportImages(project, ImageLearningImageRole.OkValidation, WriteBoardPng("ok-validation.png", variant: 20));
        var learning = ImageOnlyPcbLearningService.TrainProject(project.ProjectId, _options);
        var inspectionImport = ImportImages(
            project,
            ImageLearningImageRole.Inspection,
            WriteBoardPng("inspection-ng.png", anomaly: true, variant: 31)).Single();

        var inspection = ImageOnlyPcbLearningService.InspectProjectImage(
            learning.Model.ModelId,
            inspectionImport.Image!,
            _options,
            "Operator01 [Operator]");
        var persisted = AoiDatabase.GetImageLearningInspectionResult(inspection.InspectionResult.Id);

        Assert.NotEqual("OK", inspection.InspectionResult.Verdict);
        Assert.NotEmpty(inspection.Regions);
        Assert.NotNull(persisted);
        var region = Assert.Single(persisted!.AnomalyRegions);
        Assert.True(region.X is >= 0 and <= 1);
        Assert.True(region.Width > 0);
        Assert.True(region.AreaPixels >= _options.MinimumAnomalyAreaPixels);
        Assert.True(region.Confidence > 0.55);
        Assert.Contains("learned tolerance", region.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ImageOnlyPcbLearningOkValidationLowersFalseCallsVsRawPixelDifference()
    {
        var project = CreateLearningProject("OK calibration");
        var goldenPath = WriteBoardPng("golden.png");
        ImportImages(project, ImageLearningImageRole.GoldenReference, goldenPath);
        for (var i = 0; i < 5; i++)
            ImportImages(project, ImageLearningImageRole.OkLearning, WriteBoardPng($"ok-{i}.png", variant: i + 1));
        var brighterOk = WriteBoardPng("ok-validation-bright.png", brightnessShift: 30, variant: 12);
        ImportImages(project, ImageLearningImageRole.OkValidation, brighterOk);

        var rawDifference = ImageOnlyPcbLearningService.CalculateRawDifferencePercentForTest(goldenPath, brighterOk, _options);
        var result = ImageOnlyPcbLearningService.TrainProject(project.ProjectId, _options);

        Assert.True(rawDifference > 5.0);
        Assert.Equal(1, result.Calibration.OkValidationCount);
        Assert.Equal(0.0, result.Calibration.FalseCallRate, precision: 3);
        Assert.Contains(result.ThresholdSweep, row => row.OkValidationCount == 1);
    }

    [Fact]
    public void ImageOnlyPcbLearningNgValidationPossibleEscapesCalculated()
    {
        var project = CreateLearningProject("NG validation");
        ImportStandardTrainingSet(project);
        ImportImages(project, ImageLearningImageRole.OkValidation, WriteBoardPng("ok-validation.png", variant: 22));
        ImportImages(project, ImageLearningImageRole.NgValidation, WriteBoardPng("ng-validation-identical.png", variant: 22, tinyMarker: true));

        var result = ImageOnlyPcbLearningService.TrainProject(project.ProjectId, _options);

        Assert.Equal(1, result.Calibration.NgValidationCount);
        Assert.InRange(result.Calibration.PossibleEscapeRate, 0.0, 1.0);
        Assert.Contains("possible escape rate", result.Calibration.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.ThresholdSweep, row => row.NgValidationCount == 1 && row.NgPossibleEscapes == 1);
    }

    [Fact]
    public void ImageOnlyPcbLearningSkipsUnreadableValidationImageAndWritesSummaryWarning()
    {
        var project = CreateLearningProject("Unreadable validation skip");
        ImportStandardTrainingSet(project);
        var unreadable = ImportImages(
            project,
            ImageLearningImageRole.OkValidation,
            WriteBoardPng("ok-validation-corrupt.png", variant: 33)).Single();
        File.WriteAllText(unreadable.Image!.VaultPath, "not a readable image");

        var result = ImageOnlyPcbLearningService.TrainProject(project.ProjectId, _options);
        var summary = File.ReadAllText(Path.Combine(result.ArtifactFolder, "learning_summary.json"));

        var skipped = Assert.Single(result.SkippedImages);
        Assert.Equal("ok-validation-corrupt.png", skipped.FileName);
        Assert.Equal(ImageLearningImageRole.OkValidation, skipped.Role);
        Assert.Equal(0, result.Calibration.OkValidationCount);
        Assert.Contains(result.Warnings, warning => warning.Contains("skipped", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("skippedImages", summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ok-validation-corrupt.png", summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ImageOnlyPcbLearningSkipsTruncatedImageWithoutAbortingRun()
    {
        // L4 regression: a truncated-but-recognized PNG makes WPF's BitmapDecoder throw
        // FileFormatException / COMException. These previously escaped the skip filter and
        // aborted the entire training run. Training must now skip the file and continue.
        var project = CreateLearningProject("Truncated image skip");
        ImportStandardTrainingSet(project);
        var truncated = ImportImages(
            project,
            ImageLearningImageRole.OkValidation,
            WriteBoardPng("ok-validation-truncated.png", variant: 34)).Single();

        var bytes = File.ReadAllBytes(truncated.Image!.VaultPath);
        File.WriteAllBytes(truncated.Image!.VaultPath, bytes.Take(Math.Min(40, bytes.Length)).ToArray());

        var result = ImageOnlyPcbLearningService.TrainProject(project.ProjectId, _options);

        var skipped = Assert.Single(result.SkippedImages);
        Assert.Equal("ok-validation-truncated.png", skipped.FileName);
        Assert.Equal(ImageLearningImageRole.OkValidation, skipped.Role);
        Assert.Contains(result.Warnings, warning => warning.Contains("skipped", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ImageOnlyPcbLearningMissingOkLearningImagesFailClearly()
    {
        var project = CreateLearningProject("Missing minimum set");
        for (var i = 0; i < 4; i++)
            ImportImages(project, ImageLearningImageRole.OkLearning, WriteBoardPng($"ok-{i}.png", variant: i + 1));

        var ex = Assert.Throws<InvalidOperationException>(
            () => ImageOnlyPcbLearningService.TrainProject(project.ProjectId, _options));

        Assert.Contains("Golden / Reference", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("5 OK Learning", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ImageOnlyPcbLearningEngineReturnsAnalysisResultFromLearnedModel()
    {
        var project = CreateLearningProject("Inspection engine");
        ImportStandardTrainingSet(project);
        var learning = ImageOnlyPcbLearningService.TrainProject(project.ProjectId, _options);
        var sample = WriteBoardPng("engine-sample-ng.png", anomaly: true, variant: 41);
        var engine = new LearnedPcbVisualInspectionEngine(learning.Model.ModelId, _options);

        var result = engine.Analyze(sample, goldenPath: null, DetectionPriority.Balanced);

        Assert.Equal(ImageOnlyPcbLearningService.EngineName, result.InspectionEngine);
        Assert.NotEqual("OK", result.Verdict);
        Assert.NotEmpty(result.Defects);
        Assert.Contains(result.Evidence, item => item.Contains("does not require manual defect labels", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ImageOnlyPcbLearningWithoutOkValidationRetainsDefaultThresholdAndReviewStatus()
    {
        // L1 guard: training without OK Validation images must not select the aggressive minimum
        // sweep threshold (which would flag nearly everything). The default threshold is retained
        // and the run is marked REVIEW so calibration is visibly pending.
        var project = CreateLearningProject("No OK validation guard");
        ImportStandardTrainingSet(project);

        var result = ImageOnlyPcbLearningService.TrainProject(project.ProjectId, _options);

        Assert.Equal(0, result.Calibration.OkValidationCount);
        Assert.Equal(_options.DefaultLearnedThreshold, result.Calibration.LearnedThreshold, precision: 3);
        Assert.Equal(_options.DefaultLearnedThreshold, result.Model.LearnedThreshold, precision: 3);
        Assert.Equal("REVIEW", result.Calibration.Status);
        Assert.Contains("OK Validation", result.Calibration.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.Warnings, warning => warning.Contains("OK Validation", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ImageOnlyPcbLearningCalibratedThresholdHoldsAtRuntimeForOkValidation()
    {
        // L2 parity: OK Validation images that calibration counts as passing (false-call rate 0)
        // must also be scored OK by runtime inspection, which loads the model from disk. This fails
        // on the old code because calibration and runtime used different region-gating thresholds.
        var project = CreateLearningProject("Calibration runtime parity");
        ImportStandardTrainingSet(project);
        var okValidationImports = new List<ImageLearningImportResult>();
        for (var i = 0; i < 4; i++)
        {
            okValidationImports.AddRange(ImportImages(
                project,
                ImageLearningImageRole.OkValidation,
                WriteBoardPng($"ok-validation-{i}.png", brightnessShift: 8 + i * 4, variant: 50 + i)));
        }

        var result = ImageOnlyPcbLearningService.TrainProject(project.ProjectId, _options);

        Assert.Equal(4, result.Calibration.OkValidationCount);
        Assert.Equal(0.0, result.Calibration.FalseCallRate, precision: 3);
        Assert.Equal("OK", result.Calibration.Status);

        foreach (var import in okValidationImports)
        {
            var inspection = ImageOnlyPcbLearningService.InspectImagePath(
                result.Model.ModelId,
                import.Image!.VaultPath,
                _options,
                "Operator01 [Operator]");
            Assert.Equal("OK", inspection.InspectionResult.Verdict);
        }
    }

    [Fact]
    public void ImageOnlyPcbLearningToleranceMapPersistedAsSixteenBitGrayscale()
    {
        // L3 lossless persistence: the tolerance (standard-deviation) map is stored as 16-bit
        // grayscale so it survives the disk round-trip without the coarse 1/16 quantization and
        // 15.94 saturation of the previous 8-bit artifact.
        var project = CreateLearningProject("Tolerance persistence");
        ImportStandardTrainingSet(project);
        ImportImages(project, ImageLearningImageRole.OkValidation, WriteBoardPng("ok-validation.png", variant: 60));

        var result = ImageOnlyPcbLearningService.TrainProject(project.ProjectId, _options);
        var tolerancePath = Path.Combine(result.ArtifactFolder, "tolerance_map.png");

        using var stream = File.OpenRead(tolerancePath);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

        Assert.Equal(16, decoder.Frames[0].Format.BitsPerPixel);
    }

    [Fact]
    public void ImageOnlyPcbLearningCoarseToFineAlignmentRecoversShiftOnLargeBoard()
    {
        // Coarse-to-fine alignment: at 192x192 the alignment search scores coarse candidates on a
        // sparse pixel grid (sampleStep = 2), then refines the winner at full resolution. A
        // whole-board (3,2) shift of an otherwise-OK copy must be recovered exactly, because
        // without recovery the global checkerboard diff (verified below via the raw difference
        // percent) would flag the board.
        var options = new ImageOnlyPcbLearningOptions
        {
            InputWidth = 192,
            InputHeight = 192,
            AlignmentSearchRadiusPixels = 6,
            MinimumAnomalyAreaPixels = 6,
            DefaultLearnedThreshold = 4.0,
            FalseCallTarget = 0.05,
            MaxAllowedPossibleEscapeRate = 0.0,
        };
        var project = CreateLearningProject("Large board alignment refinement");
        var goldenPath = WriteLargeBoardPng("large-golden.png", 192);
        ImportImages(project, ImageLearningImageRole.GoldenReference, goldenPath);
        for (var i = 0; i < 5; i++)
            ImportImages(project, ImageLearningImageRole.OkLearning, WriteLargeBoardPng($"large-ok-{i}.png", 192, variant: i + 1));

        var learning = ImageOnlyPcbLearningService.TrainProject(project.ProjectId, options);
        var shiftedOk = WriteLargeBoardPng("large-shifted-ok.png", 192, offsetX: 3, offsetY: 2, variant: 3);
        var rawDifference = ImageOnlyPcbLearningService.CalculateRawDifferencePercentForTest(goldenPath, shiftedOk, options);

        var first = ImageOnlyPcbLearningService.InspectImagePath(
            learning.Model.ModelId, shiftedOk, options, "Operator01 [Operator]");
        var second = ImageOnlyPcbLearningService.InspectImagePath(
            learning.Model.ModelId, shiftedOk, options, "Operator01 [Operator]");

        // Unaligned, the shifted checkerboard differs across a large share of the board.
        Assert.True(rawDifference > 5.0);
        // Alignment recovered the shift exactly (sign depends on search convention), so the
        // shifted OK copy passes.
        Assert.Equal(3, Math.Abs(first.AlignmentOffsetX));
        Assert.Equal(2, Math.Abs(first.AlignmentOffsetY));
        Assert.Equal("OK", first.InspectionResult.Verdict);
        // Determinism: two identical inspections yield bit-identical scores and offsets.
        Assert.Equal(first.InspectionResult.AnomalyScore, second.InspectionResult.AnomalyScore);
        Assert.Equal(first.AlignmentOffsetX, second.AlignmentOffsetX);
        Assert.Equal(first.AlignmentOffsetY, second.AlignmentOffsetY);
    }

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

    private string WriteLargeBoardPng(
        string fileName,
        int size,
        int offsetX = 0,
        int offsetY = 0,
        int variant = 0)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, fileName);
        var pixels = new byte[size * size * 4];

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var value = LargeBoardPattern(x - offsetX, y - offsetY, size, variant);
                var index = (y * size + x) * 4;
                pixels[index] = value;
                pixels[index + 1] = value;
                pixels[index + 2] = value;
                pixels[index + 3] = 255;
            }
        }

        var bitmap = BitmapSource.Create(
            size,
            size,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            size * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    private static byte LargeBoardPattern(int x, int y, int size, int variant)
    {
        // Fine high-frequency texture: a checkerboard of 3px cells covering every coordinate
        // (biased by a multiple of the cell size so small negative pattern positions produced
        // by shifting keep the same cell grid).
        var cellX = (x + 3 * size) / 3;
        var cellY = (y + 3 * size) / 3;
        var value = ((cellX + cellY) & 1) == 0 ? 88 : 168;

        // A few distinct solid blocks break the 6px checkerboard periodicity so the translation
        // search has a unique optimum; variant nudges their brightness like WriteBoardPng does.
        if (InBlock(x, y, size / 8, size / 8, size / 6))
            value = 214 + variant % 3;
        if (InBlock(x, y, size * 5 / 8, size / 4, size / 6))
            value = 34 + variant % 2;
        if (InBlock(x, y, size / 4, size * 5 / 8, size / 6))
            value = 240 - variant % 4;
        if (InBlock(x, y, size * 2 / 3, size * 2 / 3, size / 5))
            value = 130 + variant % 5;

        return ClampByte(value);

        static bool InBlock(int patternX, int patternY, int left, int top, int extent)
            => patternX >= left && patternX < left + extent && patternY >= top && patternY < top + extent;
    }

    private static byte ClampByte(int value)
        => (byte)Math.Clamp(value, 0, 255);
}
