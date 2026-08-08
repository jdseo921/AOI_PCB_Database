using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

/// <summary>
/// Layouts this service recognises. Public PCB datasets each ship in their own shape, and none of
/// them match the Stage 1 dataset contract, so a tester would otherwise hand-write a manifest for
/// hundreds of images before finding out whether the data is even usable.
/// </summary>
public enum DatasetSourceLayout
{
    /// <summary>Detect from the folder structure.</summary>
    Auto,

    /// <summary>MVTec-AD style: <c>train/good</c>, <c>test/good</c>, <c>test/&lt;defect&gt;</c>.</summary>
    MvTec,

    /// <summary>VisA style: <c>Data/Images/Normal</c> and <c>Data/Images/Anomaly</c>.</summary>
    Visa,

    /// <summary>One folder per class; a good/ok/normal/pass folder marks the known-good class.</summary>
    ClassFolders,

    /// <summary>Sample and template share a stem, e.g. DeepPCB's <c>*_test.jpg</c> / <c>*_temp.jpg</c>.</summary>
    PairedTemplate,
}

/// <summary>How each sample gets the golden reference the pixel-difference engine compares against.</summary>
public enum GoldenAssignmentStrategy
{
    Auto,

    /// <summary>Per-sample template sharing the file stem (paired-template layouts).</summary>
    Paired,

    /// <summary>Template picked from an explicit folder by longest matching name prefix.</summary>
    PerBoard,

    /// <summary>Promote known-good images to goldens. Only sound when captures are registered.</summary>
    FromNormal,

    /// <summary>No golden. Valid for the learned/ONNX engines; pixel-difference cannot run.</summary>
    None,
}

public sealed class DatasetPreparationRequest
{
    public string SourceFolder { get; init; } = string.Empty;
    public string OutputFolder { get; init; } = string.Empty;
    public DatasetSourceLayout Layout { get; init; } = DatasetSourceLayout.Auto;
    public GoldenAssignmentStrategy GoldenStrategy { get; init; } = GoldenAssignmentStrategy.Auto;

    /// <summary>Explicit template/reference folder for <see cref="GoldenAssignmentStrategy.PerBoard"/>.</summary>
    public string GoldenFolder { get; init; } = string.Empty;

    public string BoardModel { get; init; } = "CUSTOMER-BOARD";
    public string LotId { get; init; } = "LOT-EVAL";

    /// <summary>Cap on known-good images copied. 0 = no cap.</summary>
    public int MaxOkImages { get; init; }

    /// <summary>Cap on known-defect images copied per defect class. 0 = no cap.</summary>
    public int MaxNgImagesPerClass { get; init; }

    /// <summary>Seeded so the same source folder always yields the same prepared dataset.</summary>
    public int Seed { get; init; } = 20260808;

    /// <summary>Also emit the image-only learning role folders.</summary>
    public bool EmitLearningLayout { get; init; }
}

public sealed record PreparedDatasetClass(string DefectClass, string CanonicalClass, int Count, bool IsKnownToTaxonomy);

public sealed class DatasetPreparationResult
{
    public string SchemaVersion { get; init; } = "dataset-preparation/v1";
    public DateTime GeneratedAtUtc { get; init; } = DateTime.UtcNow;
    public string SourceFolder { get; set; } = string.Empty;
    public string OutputFolder { get; set; } = string.Empty;
    public string DatasetFolder { get; set; } = string.Empty;
    public string ManifestPath { get; set; } = string.Empty;
    public string LearningFolder { get; set; } = string.Empty;
    public DatasetSourceLayout DetectedLayout { get; set; }
    public GoldenAssignmentStrategy GoldenStrategy { get; set; }
    public int OkCount { get; set; }
    public int NgCount { get; set; }
    public int GoldenCount { get; set; }
    public int SamplesWithoutGolden { get; set; }
    public int SkippedFiles { get; set; }
    public List<PreparedDatasetClass> Classes { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public List<string> Limitations { get; set; } = new();
    public string NextCommand { get; set; } = string.Empty;
}

/// <summary>
/// Converts a third-party PCB image dataset into the Stage 1 dataset contract
/// (<c>images/</c>, <c>golden/</c>, <c>customer_validation_manifest.csv</c>) and, optionally, the
/// image-only learning role folders.
///
/// **This service never downloads anything.** It works on a folder the operator has already
/// obtained and is responsible for licensing. Copying is one-way: the source folder is only read.
///
/// It also never asserts that a prepared dataset is *good*. It reports what it inferred, what it
/// could not infer, and which Stage 1 preflight gates the result will and will not satisfy, so the
/// tester learns that before running a validation instead of after.
/// </summary>
public static class DatasetPreparationService
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp"];

    private static readonly string[] KnownGoodFolderNames =
        ["good", "ok", "normal", "pass", "nodefect", "no_defect", "defectfree", "defect_free", "negative"];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static DatasetPreparationResult Prepare(DatasetPreparationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.SourceFolder) || !Directory.Exists(request.SourceFolder))
            throw new DirectoryNotFoundException($"Source dataset folder was not found: {request.SourceFolder}");
        if (string.IsNullOrWhiteSpace(request.OutputFolder))
            throw new ArgumentException("Output folder is required.", nameof(request));

        var source = Path.GetFullPath(request.SourceFolder);
        var output = Path.GetFullPath(request.OutputFolder);
        if (IsSameOrNested(source, output))
            throw new ArgumentException("Output folder must not be inside the source folder; the importer would re-read its own output.", nameof(request));

        var result = new DatasetPreparationResult
        {
            SourceFolder = source,
            OutputFolder = output,
        };

        var layout = request.Layout == DatasetSourceLayout.Auto ? DetectLayout(source) : request.Layout;
        result.DetectedLayout = layout;

        var samples = Discover(source, layout, result).ToList();
        if (samples.Count == 0)
        {
            throw new InvalidDataException(
                $"No labelled images were found under {source} using layout {layout}. " +
                "Pass an explicit --layout, or point --source at the folder that directly contains the class folders.");
        }

        samples = ApplyCaps(samples, request, result);

        var datasetFolder = Path.Combine(output, "dataset");
        var imagesFolder = Path.Combine(datasetFolder, "images");
        var goldenFolder = Path.Combine(datasetFolder, "golden");
        Directory.CreateDirectory(imagesFolder);
        Directory.CreateDirectory(goldenFolder);
        result.DatasetFolder = datasetFolder;

        var strategy = request.GoldenStrategy == GoldenAssignmentStrategy.Auto
            ? ChooseGoldenStrategy(layout, samples, request)
            : request.GoldenStrategy;
        result.GoldenStrategy = strategy;

        var goldens = AssignGoldens(samples, strategy, request, goldenFolder, result);
        result.GoldenCount = goldens.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count(name => !string.IsNullOrEmpty(name));

        var manifestPath = Path.Combine(datasetFolder, "customer_validation_manifest.csv");
        WriteManifest(samples, goldens, imagesFolder, manifestPath, request, result);
        result.ManifestPath = manifestPath;

        if (request.EmitLearningLayout)
            result.LearningFolder = WriteLearningLayout(samples, goldens, datasetFolder, output, request);

        AppendGateAdvice(result, request);
        WriteReports(result, output);

        result.NextCommand =
            $"dotnet run --project AOI_Monitor.Tools -c Release -- stage1-exit --dataset \"{imagesFolder}\" " +
            $"--manifest \"{manifestPath}\" --output \"{Path.Combine(output, "evidence")}\" --operator <id> --priority maximize-defect-recall";

        return result;
    }

    /// <summary>
    /// Layout detection is deliberately conservative: an unrecognised structure returns
    /// <see cref="DatasetSourceLayout.ClassFolders"/> so discovery can still report exactly what it
    /// saw, rather than guessing a shape and silently mislabelling images.
    /// </summary>
    public static DatasetSourceLayout DetectLayout(string source)
    {
        if (Directory.Exists(Path.Combine(source, "Data", "Images", "Normal")) ||
            Directory.Exists(Path.Combine(source, "Data", "Images", "Anomaly")))
            return DatasetSourceLayout.Visa;

        var test = Path.Combine(source, "test");
        if (Directory.Exists(test) && Directory.EnumerateDirectories(test).Any())
            return DatasetSourceLayout.MvTec;

        if (HasPairedTemplates(source))
            return DatasetSourceLayout.PairedTemplate;

        return DatasetSourceLayout.ClassFolders;
    }

    private static bool HasPairedTemplates(string source)
    {
        // DeepPCB-style: <stem>_test.jpg beside <stem>_temp.jpg, nested a few levels deep.
        var candidates = EnumerateImages(source).Take(400).ToArray();
        var tests = candidates.Where(path => Path.GetFileNameWithoutExtension(path).EndsWith("_test", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (tests.Length == 0)
            return false;

        return tests.Take(20).Any(path => TryFindPairedTemplate(path) is not null);
    }

    private static string? TryFindPairedTemplate(string testPath)
    {
        var directory = Path.GetDirectoryName(testPath);
        if (string.IsNullOrEmpty(directory))
            return null;

        var stem = Path.GetFileNameWithoutExtension(testPath);
        if (!stem.EndsWith("_test", StringComparison.OrdinalIgnoreCase))
            return null;

        var baseStem = stem[..^"_test".Length];
        foreach (var extension in ImageExtensions)
        {
            var candidate = Path.Combine(directory, baseStem + "_temp" + extension);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static IEnumerable<DiscoveredSample> Discover(string source, DatasetSourceLayout layout, DatasetPreparationResult result)
        => layout switch
        {
            DatasetSourceLayout.Visa => DiscoverVisa(source),
            DatasetSourceLayout.MvTec => DiscoverMvTec(source),
            DatasetSourceLayout.PairedTemplate => DiscoverPairedTemplate(source, result),
            _ => DiscoverClassFolders(source, result),
        };

    private static IEnumerable<DiscoveredSample> DiscoverVisa(string source)
    {
        var normal = Path.Combine(source, "Data", "Images", "Normal");
        var anomaly = Path.Combine(source, "Data", "Images", "Anomaly");

        foreach (var path in EnumerateImages(normal))
            yield return new DiscoveredSample(path, IsOk: true, "OK", null);

        // VisA does not name the anomaly type per image, so every anomalous board is labelled with
        // the generic taxonomy class rather than being given a defect class it does not carry.
        foreach (var path in EnumerateImages(anomaly))
            yield return new DiscoveredSample(path, IsOk: false, "Anomaly", null);
    }

    private static IEnumerable<DiscoveredSample> DiscoverMvTec(string source)
    {
        foreach (var split in new[] { "train", "test" })
        {
            var splitFolder = Path.Combine(source, split);
            if (!Directory.Exists(splitFolder))
                continue;

            foreach (var classFolder in Directory.EnumerateDirectories(splitFolder))
            {
                var name = Path.GetFileName(classFolder);
                var isOk = IsKnownGoodName(name);
                foreach (var path in EnumerateImages(classFolder))
                    yield return new DiscoveredSample(path, isOk, isOk ? "OK" : name, null);
            }
        }
    }

    private static IEnumerable<DiscoveredSample> DiscoverPairedTemplate(string source, DatasetPreparationResult result)
    {
        var unpaired = 0;
        foreach (var path in EnumerateImages(source))
        {
            var stem = Path.GetFileNameWithoutExtension(path);
            if (stem.EndsWith("_temp", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!stem.EndsWith("_test", StringComparison.OrdinalIgnoreCase))
                continue;

            var template = TryFindPairedTemplate(path);
            if (template is null)
                unpaired++;

            // A paired-template dataset carries no per-image OK/NG label in its filenames; the
            // defect list lives in a sidecar annotation file whose format varies by dataset. The
            // sample is labelled NG only when a non-empty annotation sidecar exists, and OK when an
            // empty one does; otherwise the label is left unknown for the operator to supply.
            var (isOk, defectClass) = ReadPairedAnnotation(path);
            yield return new DiscoveredSample(path, isOk, defectClass, template);
        }

        if (unpaired > 0)
            result.Warnings.Add($"{unpaired} paired-layout sample(s) had no matching _temp template; they were imported without a golden reference.");
    }

    private static (bool IsOk, string DefectClass) ReadPairedAnnotation(string imagePath)
    {
        var directory = Path.GetDirectoryName(imagePath) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(imagePath);
        var baseStem = stem.EndsWith("_test", StringComparison.OrdinalIgnoreCase) ? stem[..^"_test".Length] : stem;

        foreach (var candidate in new[]
        {
            Path.Combine(directory, stem + ".txt"),
            Path.Combine(directory, baseStem + ".txt"),
            Path.Combine(directory, "..", Path.GetFileName(directory) + "_not", baseStem + ".txt"),
        })
        {
            if (!File.Exists(candidate))
                continue;

            var lines = File.ReadAllLines(candidate).Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
            return lines.Length == 0 ? (true, "OK") : (false, "Anomaly");
        }

        return (false, "UNLABELED");
    }

    private static IEnumerable<DiscoveredSample> DiscoverClassFolders(string source, DatasetPreparationResult result)
    {
        var classFolders = Directory.EnumerateDirectories(source).ToArray();
        if (classFolders.Length == 0)
        {
            result.Warnings.Add(
                "The source folder contains no class subfolders, so ground truth cannot be inferred from structure. " +
                "Sort images into a good/ folder plus one folder per defect class, or supply your own manifest.");
            yield break;
        }

        foreach (var folder in classFolders)
        {
            var name = Path.GetFileName(folder);
            // Annotation/mask folders sit beside class folders in several public datasets.
            if (name.Equals("Annotations", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Masks", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("labels", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("ground_truth", StringComparison.OrdinalIgnoreCase))
                continue;

            var isOk = IsKnownGoodName(name);
            foreach (var path in EnumerateImages(folder))
                yield return new DiscoveredSample(path, isOk, isOk ? "OK" : name, null);
        }
    }

    private static bool IsKnownGoodName(string folderName)
    {
        var normalized = folderName.Trim().ToLowerInvariant().Replace("-", "_", StringComparison.Ordinal);
        return KnownGoodFolderNames.Contains(normalized) ||
               KnownGoodFolderNames.Contains(normalized.Replace("_", string.Empty, StringComparison.Ordinal));
    }

    private static List<DiscoveredSample> ApplyCaps(List<DiscoveredSample> samples, DatasetPreparationRequest request, DatasetPreparationResult result)
    {
        var random = new Random(request.Seed);
        var kept = new List<DiscoveredSample>();

        var ok = samples.Where(sample => sample.IsOk).ToList();
        if (request.MaxOkImages > 0 && ok.Count > request.MaxOkImages)
        {
            result.Warnings.Add($"Known-good images sampled down from {ok.Count} to {request.MaxOkImages} (seeded, reproducible).");
            ok = Shuffle(ok, random).Take(request.MaxOkImages).ToList();
        }

        kept.AddRange(ok);

        foreach (var group in samples.Where(sample => !sample.IsOk).GroupBy(sample => sample.DefectClass, StringComparer.OrdinalIgnoreCase))
        {
            var items = group.ToList();
            if (request.MaxNgImagesPerClass > 0 && items.Count > request.MaxNgImagesPerClass)
            {
                result.Warnings.Add($"Defect class '{group.Key}' sampled down from {items.Count} to {request.MaxNgImagesPerClass} (seeded, reproducible).");
                items = Shuffle(items, random).Take(request.MaxNgImagesPerClass).ToList();
            }

            kept.AddRange(items);
        }

        return kept;
    }

    private static List<T> Shuffle<T>(List<T> items, Random random)
    {
        var copy = new List<T>(items);
        for (var i = copy.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (copy[i], copy[j]) = (copy[j], copy[i]);
        }

        return copy;
    }

    private static GoldenAssignmentStrategy ChooseGoldenStrategy(
        DatasetSourceLayout layout,
        IReadOnlyCollection<DiscoveredSample> samples,
        DatasetPreparationRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.GoldenFolder))
            return GoldenAssignmentStrategy.PerBoard;
        if (layout == DatasetSourceLayout.PairedTemplate && samples.Any(sample => sample.TemplatePath is not null))
            return GoldenAssignmentStrategy.Paired;
        if (samples.Any(sample => sample.IsOk))
            return GoldenAssignmentStrategy.FromNormal;
        return GoldenAssignmentStrategy.None;
    }

    private static Dictionary<string, string> AssignGoldens(
        List<DiscoveredSample> samples,
        GoldenAssignmentStrategy strategy,
        DatasetPreparationRequest request,
        string goldenFolder,
        DatasetPreparationResult result)
    {
        var goldens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (strategy == GoldenAssignmentStrategy.None)
        {
            result.Warnings.Add("No golden references were assigned. The Pixel Difference engine cannot run; use the learned visual model or a configured ONNX model, and expect dataset preflight to flag missing goldens.");
            return goldens;
        }

        switch (strategy)
        {
            case GoldenAssignmentStrategy.Paired:
                foreach (var sample in samples.Where(sample => sample.TemplatePath is not null))
                {
                    var goldenName = CopyUnique(sample.TemplatePath!, goldenFolder);
                    goldens[sample.SourcePath] = goldenName;
                }

                break;

            case GoldenAssignmentStrategy.PerBoard:
                {
                    var templates = EnumerateImages(request.GoldenFolder).ToArray();
                    if (templates.Length == 0)
                    {
                        result.Warnings.Add($"Golden folder contained no images: {request.GoldenFolder}. Samples were left without goldens.");
                        break;
                    }

                    var copied = templates.ToDictionary(path => path, path => CopyUnique(path, goldenFolder), StringComparer.OrdinalIgnoreCase);
                    foreach (var sample in samples)
                    {
                        var match = BestPrefixMatch(sample.SourcePath, templates);
                        if (match is not null)
                            goldens[sample.SourcePath] = copied[match];
                    }

                    break;
                }

            case GoldenAssignmentStrategy.FromNormal:
                {
                    var normal = samples.FirstOrDefault(sample => sample.IsOk);
                    if (normal is null)
                    {
                        result.Warnings.Add("FromNormal golden strategy needs at least one known-good image; none was found.");
                        break;
                    }

                    var goldenName = CopyUnique(normal.SourcePath, goldenFolder);
                    foreach (var sample in samples)
                        goldens[sample.SourcePath] = goldenName;

                    result.Limitations.Add(
                        "Golden reference was promoted from a known-good image in this dataset. That is only sound when captures are registered " +
                        "(same camera pose, framing, and lighting). On unregistered photographs the difference score reflects alignment, not defects. " +
                        "Check a few Golden Compare overlays before trusting any pixel-difference number from this dataset.");
                    break;
                }
        }

        result.SamplesWithoutGolden = samples.Count(sample => !goldens.ContainsKey(sample.SourcePath));
        if (result.SamplesWithoutGolden > 0)
            result.Warnings.Add($"{result.SamplesWithoutGolden} sample(s) have no golden reference; dataset preflight treats missing goldens as blocking by default.");

        return goldens;
    }

    private static string? BestPrefixMatch(string samplePath, IReadOnlyCollection<string> templates)
    {
        var stem = Path.GetFileNameWithoutExtension(samplePath);
        string? best = null;
        var bestLength = 0;
        foreach (var template in templates)
        {
            var templateStem = Path.GetFileNameWithoutExtension(template);
            if (templateStem.Length > bestLength && stem.StartsWith(templateStem, StringComparison.OrdinalIgnoreCase))
            {
                best = template;
                bestLength = templateStem.Length;
            }
        }

        return best;
    }

    private static void WriteManifest(
        List<DiscoveredSample> samples,
        IReadOnlyDictionary<string, string> goldens,
        string imagesFolder,
        string manifestPath,
        DatasetPreparationRequest request,
        DatasetPreparationResult result)
    {
        var sb = new StringBuilder();
        // Header comes from the preflight service's own column list, so the emitted manifest can
        // never drift from what preflight requires.
        sb.AppendLine(string.Join(",", CustomerDatasetPreflightService.RequiredManifestColumns.Select(Csv)));

        var index = 0;
        var classCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        // Byte-identical source images (common when a public dataset repeats a board across class
        // folders) would otherwise collapse into one copied file and produce duplicate manifest
        // rows. Duplicates inflate apparent performance, so they are reported here rather than
        // being discovered as a preflight failure after the fact.
        var copiedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var duplicateSourceImages = 0;

        foreach (var sample in samples)
        {
            index++;
            var copiedName = CopyUnique(sample.SourcePath, imagesFolder);
            if (!copiedNames.Add(copiedName))
                duplicateSourceImages++;
            var groundTruth = sample.IsOk ? "OK" : "NG";
            var defectClass = sample.IsOk ? "OK" : DefectTaxonomyService.Normalize(sample.DefectClass).CanonicalClass;

            // Manifest paths are resolved relative to the manifest's own directory (the dataset
            // root), which is why both columns carry the images/ and golden/ prefix.
            var golden = goldens.TryGetValue(sample.SourcePath, out var goldenName) ? $"golden/{goldenName}" : string.Empty;

            if (!sample.IsOk)
                classCounts[sample.DefectClass] = classCounts.GetValueOrDefault(sample.DefectClass) + 1;

            sb.AppendLine(string.Join(",",
                Csv($"images/{copiedName}"),
                Csv(groundTruth),
                Csv(golden),
                Csv(defectClass),
                Csv(InferSide(sample.SourcePath)),
                // Preflight requires refdes/roi_id/roi_type together — partial ROI metadata is a
                // blocking failure. Public datasets label whole boards, not reference designators,
                // so the whole-board convention is used rather than inventing a component name.
                Csv("BOARD"),
                Csv($"ROI-{index:D4}"),
                Csv("board"),
                Csv(request.LotId),
                Csv(request.BoardModel),
                Csv($"imported from {Path.GetFileName(Path.GetDirectoryName(sample.SourcePath)) ?? "source"}")));
        }

        File.WriteAllText(manifestPath, sb.ToString(), Encoding.UTF8);

        result.OkCount = samples.Count(sample => sample.IsOk);
        result.NgCount = samples.Count(sample => !sample.IsOk);
        result.Classes = classCounts
            .Select(pair =>
            {
                var normalized = DefectTaxonomyService.Normalize(pair.Key);
                return new PreparedDatasetClass(pair.Key, normalized.CanonicalClass, pair.Value, normalized.IsKnown);
            })
            .OrderByDescending(item => item.Count)
            .ToList();

        if (duplicateSourceImages > 0)
        {
            result.Warnings.Add(
                $"{duplicateSourceImages} source image(s) are byte-identical to another image in this dataset and now share a copied file, " +
                "so the manifest has duplicate image rows. Dataset preflight blocks duplicate rows: de-duplicate the source dataset, " +
                "or restrict --source to one split. Duplicates also make accuracy look better than it is.");
        }

        foreach (var unknown in result.Classes.Where(item => !item.IsKnownToTaxonomy))
        {
            result.Warnings.Add(
                $"Defect class '{unknown.DefectClass}' ({unknown.Count} image(s)) is not in the active defect taxonomy. " +
                "Add it as an alias or entry via System Settings > Defect Taxonomy CSV import, or dataset preflight will count these rows as unknown labels.");
        }
    }

    private static string WriteLearningLayout(
        List<DiscoveredSample> samples,
        IReadOnlyDictionary<string, string> goldens,
        string datasetFolder,
        string output,
        DatasetPreparationRequest request)
    {
        var learningFolder = Path.Combine(output, "learning");
        foreach (var role in new[] { "golden", "ok_learning", "ok_validation", "inspection", "ng_validation" })
            Directory.CreateDirectory(Path.Combine(learningFolder, role));

        // Split known-good images 60/40 into learning and validation. The learned model calibrates
        // its threshold on the validation half, so the halves must not overlap.
        var ok = Shuffle(samples.Where(sample => sample.IsOk).ToList(), new Random(request.Seed));
        var learnCount = Math.Max(1, (int)Math.Round(ok.Count * 0.6));
        for (var i = 0; i < ok.Count; i++)
            CopyUnique(ok[i].SourcePath, Path.Combine(learningFolder, i < learnCount ? "ok_learning" : "ok_validation"));

        foreach (var ng in samples.Where(sample => !sample.IsOk))
        {
            CopyUnique(ng.SourcePath, Path.Combine(learningFolder, "ng_validation"));
            CopyUnique(ng.SourcePath, Path.Combine(learningFolder, "inspection"));
        }

        var goldenSource = Path.Combine(datasetFolder, "golden");
        if (Directory.Exists(goldenSource))
        {
            foreach (var golden in EnumerateImages(goldenSource).Take(1))
                CopyUnique(golden, Path.Combine(learningFolder, "golden"));
        }
        else if (ok.Count > 0)
        {
            CopyUnique(ok[0].SourcePath, Path.Combine(learningFolder, "golden"));
        }

        return learningFolder;
    }

    /// <summary>
    /// States, before any validation run, which Stage 1 preflight gates this dataset will fail.
    /// Finding out here costs seconds; finding out after a batch run costs the run.
    /// </summary>
    private static void AppendGateAdvice(DatasetPreparationResult result, DatasetPreparationRequest request)
    {
        var total = result.OkCount + result.NgCount;
        if (total < 50)
            result.Warnings.Add($"Dataset has {total} image(s); default preflight requires at least 50 total.");
        if (result.OkCount < 20)
            result.Warnings.Add($"Dataset has {result.OkCount} known-good image(s); default preflight requires at least 20.");
        if (result.NgCount < 20)
            result.Warnings.Add($"Dataset has {result.NgCount} known-defect image(s); default preflight requires at least 20.");
        if (result.Classes.Count < 2)
            result.Warnings.Add($"Dataset has {result.Classes.Count} defect class(es); default preflight requires at least 2.");

        foreach (var thin in result.Classes.Where(item => item.Count < 5))
            result.Warnings.Add($"Defect class '{thin.DefectClass}' has only {thin.Count} image(s); default preflight requires at least 5 per class.");

        result.Limitations.Add("Prepared datasets are Stage 1 uploaded-image evidence only. They never constitute real camera, lighting, 3D, robot, PLC safety, or MES readiness.");
        result.Limitations.Add("Licensing and redistribution rights for the source images are the operator's responsibility. This tool copies local files and downloads nothing.");
        if (result.Classes.Any(item => !item.IsKnownToTaxonomy))
            result.Limitations.Add("Unknown defect labels are reported as such by preflight; they do not silently become a known class.");
    }

    private static void WriteReports(DatasetPreparationResult result, string output)
    {
        Directory.CreateDirectory(output);
        var jsonPath = Path.Combine(output, "prepare_dataset_report.json");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(result, JsonOptions), Encoding.UTF8);

        var sb = new StringBuilder();
        sb.AppendLine("AOI Monitor Stage 1 dataset preparation");
        sb.AppendLine($"Generated UTC : {result.GeneratedAtUtc:O}");
        sb.AppendLine($"Source        : {result.SourceFolder}");
        sb.AppendLine($"Detected      : layout={result.DetectedLayout}, golden strategy={result.GoldenStrategy}");
        sb.AppendLine($"Images        : {result.OkCount} OK, {result.NgCount} NG, {result.GoldenCount} golden reference(s)");
        sb.AppendLine($"Manifest      : {result.ManifestPath}");
        sb.AppendLine();
        sb.AppendLine("Defect classes:");
        foreach (var item in result.Classes)
            sb.AppendLine($"  {item.DefectClass} -> {item.CanonicalClass} ({item.Count}){(item.IsKnownToTaxonomy ? string.Empty : "  [NOT IN TAXONOMY]")}");
        sb.AppendLine();
        sb.AppendLine("Warnings:");
        if (result.Warnings.Count == 0)
            sb.AppendLine("  none");
        foreach (var warning in result.Warnings)
            sb.AppendLine($"  - {warning}");
        sb.AppendLine();
        sb.AppendLine("Limitations:");
        foreach (var limitation in result.Limitations)
            sb.AppendLine($"  - {limitation}");

        File.WriteAllText(Path.Combine(output, "prepare_dataset_report.txt"), sb.ToString(), Encoding.UTF8);
        ExportVerificationService.RecordVerifiedExport("Stage1DatasetPreparation", output, result.Warnings.Count == 0 ? "OK" : "WARN");
    }

    private static string InferSide(string path)
    {
        var text = path.ToLowerInvariant();
        if (text.Contains("bottom", StringComparison.Ordinal)) return "bottom";
        if (text.Contains("side", StringComparison.Ordinal)) return "side";
        return "top";
    }

    private static string CopyUnique(string sourcePath, string destinationFolder)
    {
        Directory.CreateDirectory(destinationFolder);
        var name = Path.GetFileName(sourcePath);
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        var candidate = name;
        var counter = 2;

        // Public datasets reuse file names across class folders (000.png in every class), so a flat
        // copy would silently overwrite. Existing identical copies are reused rather than duplicated.
        while (File.Exists(Path.Combine(destinationFolder, candidate)))
        {
            if (FilesAreIdentical(sourcePath, Path.Combine(destinationFolder, candidate)))
                return candidate;
            candidate = $"{stem}_{counter++}{extension}";
        }

        File.Copy(sourcePath, Path.Combine(destinationFolder, candidate));
        return candidate;
    }

    private static bool FilesAreIdentical(string left, string right)
    {
        var leftInfo = new FileInfo(left);
        var rightInfo = new FileInfo(right);
        if (leftInfo.Length != rightInfo.Length)
            return false;

        return string.Equals(HashUtil.ComputeSha256(left), HashUtil.ComputeSha256(right), StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> EnumerateImages(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return Array.Empty<string>();

        return Directory
            .EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(path => ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.Ordinal);
    }

    private static bool IsSameOrNested(string source, string output)
    {
        var normalizedSource = source.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedOutput = output.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return normalizedOutput.StartsWith(normalizedSource, StringComparison.OrdinalIgnoreCase);
    }

    private static string Csv(string value)
        => $"\"{(value ?? string.Empty).Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private sealed record DiscoveredSample(string SourcePath, bool IsOk, string DefectClass, string? TemplatePath);
}
