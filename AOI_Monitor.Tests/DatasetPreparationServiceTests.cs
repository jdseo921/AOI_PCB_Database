using System.Globalization;
using AOI_Monitor.Data;
using AOI_Monitor.Services;
using Xunit;

namespace AOI_Monitor.Tests;

/// <summary>
/// Tests for converting third-party PCB datasets into the Stage 1 dataset contract.
///
/// The contract these pin is narrow but unforgiving: the emitted manifest must satisfy
/// <see cref="CustomerDatasetPreflightService"/> exactly, because a manifest that is 99 % right
/// costs a full batch run to discover. Both defects found while building this service — golden
/// paths written relative to the wrong directory, and ROI metadata emitted partially when preflight
/// requires it whole — were of that kind, and both are covered here.
/// </summary>
public sealed class DatasetPreparationServiceTests : IDisposable
{
    private readonly string _root;

    public DatasetPreparationServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AOI_Monitor_DatasetPrep_Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        AoiDatabase.ConfigureStorageRoot(Path.Combine(_root, "storage"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException ex)
        {
            System.Diagnostics.Trace.WriteLine($"Dataset prep test cleanup skipped: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            System.Diagnostics.Trace.WriteLine($"Dataset prep test cleanup skipped: {ex.Message}");
        }
    }

    [Fact]
    public void MvTecStyleLayoutIsDetectedAndSplitIntoOkAndNg()
    {
        var source = Path.Combine(_root, "mvtec");
        WriteImages(Path.Combine(source, "train", "good"), 6);
        WriteImages(Path.Combine(source, "test", "good"), 3);
        WriteImages(Path.Combine(source, "test", "solder_bridge"), 4);
        WriteImages(Path.Combine(source, "test", "missing_component"), 4);

        var result = Prepare(source, "out-mvtec");

        Assert.Equal(DatasetSourceLayout.MvTec, result.DetectedLayout);
        Assert.Equal(9, result.OkCount);
        Assert.Equal(8, result.NgCount);
        Assert.Equal(2, result.Classes.Count);
    }

    [Fact]
    public void VisaStyleNormalAndAnomalyFoldersAreDetected()
    {
        var source = Path.Combine(_root, "visa");
        WriteImages(Path.Combine(source, "Data", "Images", "Normal"), 5);
        WriteImages(Path.Combine(source, "Data", "Images", "Anomaly"), 4);

        var result = Prepare(source, "out-visa");

        Assert.Equal(DatasetSourceLayout.Visa, result.DetectedLayout);
        Assert.Equal(5, result.OkCount);
        Assert.Equal(4, result.NgCount);
        // VisA carries no per-image defect type, so anomalous boards must not be given one.
        Assert.Equal("Anomaly", Assert.Single(result.Classes).CanonicalClass);
    }

    [Fact]
    public void PairedTemplateLayoutUsesTheTemplateAsGoldenAndReadsAnnotationSidecars()
    {
        var source = Path.Combine(_root, "paired", "group0");
        Directory.CreateDirectory(source);
        for (var i = 0; i < 3; i++)
        {
            WriteImage(Path.Combine(source, $"{i:D5}_test.png"));
            WriteImage(Path.Combine(source, $"{i:D5}_temp.png"));
            // Non-empty annotation = defects listed for this board.
            File.WriteAllText(Path.Combine(source, $"{i:D5}.txt"), "10 20 30 40 1\n");
        }

        for (var i = 3; i < 5; i++)
        {
            WriteImage(Path.Combine(source, $"{i:D5}_test.png"));
            WriteImage(Path.Combine(source, $"{i:D5}_temp.png"));
            File.WriteAllText(Path.Combine(source, $"{i:D5}.txt"), string.Empty);
        }

        var result = Prepare(Path.Combine(_root, "paired"), "out-paired");

        Assert.Equal(DatasetSourceLayout.PairedTemplate, result.DetectedLayout);
        Assert.Equal(GoldenAssignmentStrategy.Paired, result.GoldenStrategy);
        Assert.Equal(2, result.OkCount);
        Assert.Equal(3, result.NgCount);
        Assert.Equal(0, result.SamplesWithoutGolden);
    }

    [Fact]
    public void EmittedManifestSatisfiesDatasetPreflight()
    {
        // The whole point of the service: what it writes must pass the real gate, not merely look
        // plausible. Both bugs found in development would fail this test.
        var source = Path.Combine(_root, "gate");
        WriteImages(Path.Combine(source, "train", "good"), 26);
        WriteImages(Path.Combine(source, "test", "solder_bridge"), 12);
        WriteImages(Path.Combine(source, "test", "missing_component"), 12);

        var result = Prepare(source, "out-gate");
        var preflight = CustomerDatasetPreflightService.Validate(result.DatasetFolder, result.ManifestPath);

        Assert.Empty(preflight.BlockingFailures);
        Assert.Equal("PASS", preflight.Status);
    }

    [Fact]
    public void ManifestPathsResolveRelativeToTheManifestDirectory()
    {
        var source = Path.Combine(_root, "paths");
        WriteImages(Path.Combine(source, "train", "good"), 3);
        WriteImages(Path.Combine(source, "test", "solder_bridge"), 2);

        var result = Prepare(source, "out-paths");
        var manifestDirectory = Path.GetDirectoryName(result.ManifestPath)!;
        var rows = File.ReadAllLines(result.ManifestPath).Skip(1).Where(line => !string.IsNullOrWhiteSpace(line));

        foreach (var row in rows)
        {
            var cells = row.Split(',').Select(cell => cell.Trim('"')).ToArray();
            Assert.True(File.Exists(Path.Combine(manifestDirectory, cells[0])), $"image path did not resolve: {cells[0]}");
            Assert.True(File.Exists(Path.Combine(manifestDirectory, cells[2])), $"golden path did not resolve: {cells[2]}");
        }
    }

    [Fact]
    public void RoiMetadataIsEmittedCompletelyBecausePartialMetadataIsABlockingFailure()
    {
        var source = Path.Combine(_root, "roi");
        WriteImages(Path.Combine(source, "train", "good"), 2);
        WriteImages(Path.Combine(source, "test", "solder_bridge"), 2);

        var result = Prepare(source, "out-roi");
        var header = File.ReadLines(result.ManifestPath).First().Split(',').Select(cell => cell.Trim('"')).ToArray();
        var row = File.ReadLines(result.ManifestPath).Skip(1).First().Split(',').Select(cell => cell.Trim('"')).ToArray();

        foreach (var column in new[] { "refdes", "roi_id", "roi_type" })
        {
            var index = Array.FindIndex(header, item => string.Equals(item, column, StringComparison.OrdinalIgnoreCase));
            Assert.True(index >= 0, $"manifest is missing column {column}");
            Assert.False(string.IsNullOrWhiteSpace(row[index]), $"{column} was emitted blank");
        }
    }

    [Fact]
    public void DefectFolderNamesAreNormalizedThroughTheActiveTaxonomy()
    {
        var source = Path.Combine(_root, "taxonomy");
        WriteImages(Path.Combine(source, "train", "good"), 2);
        WriteImages(Path.Combine(source, "test", "solder_bridge"), 2);
        WriteImages(Path.Combine(source, "test", "vendor_special_thing"), 2);

        var result = Prepare(source, "out-taxonomy");

        var known = result.Classes.Single(item => item.DefectClass == "solder_bridge");
        Assert.True(known.IsKnownToTaxonomy);
        Assert.Equal("Solder Bridge", known.CanonicalClass);

        // An unmapped class must be reported, not silently absorbed into a neighbouring class.
        var unknown = result.Classes.Single(item => item.DefectClass == "vendor_special_thing");
        Assert.False(unknown.IsKnownToTaxonomy);
        Assert.Contains(result.Warnings, warning => warning.Contains("vendor_special_thing", StringComparison.Ordinal));
    }

    [Fact]
    public void PreflightGateShortfallsAreReportedBeforeAnyRun()
    {
        var source = Path.Combine(_root, "thin");
        WriteImages(Path.Combine(source, "train", "good"), 3);
        WriteImages(Path.Combine(source, "test", "solder_bridge"), 2);

        var result = Prepare(source, "out-thin");

        Assert.Contains(result.Warnings, warning => warning.Contains("at least 50 total", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, warning => warning.Contains("at least 20", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, warning => warning.Contains("at least 2", StringComparison.Ordinal));
    }

    [Fact]
    public void PromotedGoldenCarriesAnExplicitRegistrationCaveat()
    {
        // Promoting a known-good image to golden is only sound for registered captures. Saying so
        // is the difference between usable evidence and a misleading difference score.
        var source = Path.Combine(_root, "caveat");
        WriteImages(Path.Combine(source, "train", "good"), 3);
        WriteImages(Path.Combine(source, "test", "solder_bridge"), 2);

        var result = Prepare(source, "out-caveat");

        Assert.Equal(GoldenAssignmentStrategy.FromNormal, result.GoldenStrategy);
        Assert.Contains(result.Limitations, item => item.Contains("registered", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SameNamedFilesFromDifferentClassFoldersDoNotOverwriteEachOther()
    {
        // Public datasets number files per class, so 000.png exists in every folder.
        var source = Path.Combine(_root, "collide");
        WriteImages(Path.Combine(source, "train", "good"), 2);
        WriteImages(Path.Combine(source, "test", "solder_bridge"), 2, seed: 40);
        WriteImages(Path.Combine(source, "test", "missing_component"), 2, seed: 80);

        var result = Prepare(source, "out-collide");
        var copied = Directory.GetFiles(Path.Combine(result.DatasetFolder, "images"));

        Assert.Equal(6, copied.Length);
    }

    [Fact]
    public void SamplingIsSeededSoTwoRunsProduceTheSameDataset()
    {
        var source = Path.Combine(_root, "seeded");
        WriteImages(Path.Combine(source, "train", "good"), 12);
        WriteImages(Path.Combine(source, "test", "solder_bridge"), 8);

        var first = Prepare(source, "out-seed-1", maxOk: 5);
        var second = Prepare(source, "out-seed-2", maxOk: 5);

        var firstRows = File.ReadAllLines(first.ManifestPath).Skip(1).Select(NormalizeRowForComparison).Order().ToArray();
        var secondRows = File.ReadAllLines(second.ManifestPath).Skip(1).Select(NormalizeRowForComparison).Order().ToArray();

        Assert.Equal(firstRows, secondRows);
        Assert.Equal(5, first.OkCount);
    }

    [Fact]
    public void OutputInsideTheSourceFolderIsRejected()
    {
        var source = Path.Combine(_root, "nested");
        WriteImages(Path.Combine(source, "train", "good"), 2);

        Assert.Throws<ArgumentException>(() => DatasetPreparationService.Prepare(new DatasetPreparationRequest
        {
            SourceFolder = source,
            OutputFolder = Path.Combine(source, "prepared"),
        }));
    }

    [Fact]
    public void SourceFolderWithNoLabelledImagesFailsWithAnActionableMessage()
    {
        var source = Path.Combine(_root, "empty");
        Directory.CreateDirectory(source);

        var error = Assert.Throws<InvalidDataException>(() => Prepare(source, "out-empty"));

        Assert.Contains("--layout", error.Message, StringComparison.Ordinal);
    }

    private DatasetPreparationResult Prepare(string source, string outputName, int maxOk = 0)
        => DatasetPreparationService.Prepare(new DatasetPreparationRequest
        {
            SourceFolder = source,
            OutputFolder = Path.Combine(_root, outputName),
            BoardModel = "TEST-BOARD",
            MaxOkImages = maxOk,
        });

    // Drops the roi_id column, which is a positional counter and legitimately differs between runs.
    private static string NormalizeRowForComparison(string row)
    {
        var cells = row.Split(',');
        return cells.Length > 6 ? string.Join(",", cells.Take(6).Concat(cells.Skip(7))) : row;
    }

    // Each image must be byte-distinct: identical content is a real dataset problem the service
    // reports, and a fixture that accidentally produced duplicates would silently test that path
    // instead. The seed is derived from the full path with a stable hash - string.GetHashCode is
    // randomized per process, so using it here made the fixture collide on some runs and not others.
    private static void WriteImages(string folder, int count, int seed = 0)
    {
        for (var i = 0; i < count; i++)
        {
            var path = Path.Combine(folder, $"{i:D3}.png");
            WriteImage(path, seed + StableSeed(path));
        }
    }

    private static int StableSeed(string value)
    {
        // FNV-1a: deterministic across processes, unlike string.GetHashCode.
        unchecked
        {
            var hash = 2166136261;
            foreach (var c in value)
            {
                hash ^= c;
                hash *= 16777619;
            }

            // Full 32-bit range: narrowing it would reintroduce fixture collisions.
            return (int)hash;
        }
    }

    private static void WriteImage(string path, int seed = 0)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        const int size = 16;
        var pixels = new byte[size * size * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var pixelIndex = i / 4;
            pixels[i] = (byte)((seed + pixelIndex) & 0xFF);
            pixels[i + 1] = (byte)((seed + (pixelIndex * 3)) & 0xFF);
            pixels[i + 2] = (byte)((seed + (pixelIndex * 5)) & 0xFF);
            pixels[i + 3] = 255;
        }

        // Encode the seed verbatim into the first pixel so distinct seeds are provably distinct
        // images. Arithmetic patterns alone repeat on a modulus and can collide.
        pixels[0] = (byte)(seed & 0xFF);
        pixels[1] = (byte)((seed >> 8) & 0xFF);
        pixels[2] = (byte)((seed >> 16) & 0xFF);
        pixels[4] = (byte)((seed >> 24) & 0xFF);

        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(
            size, size, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, size * 4);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
