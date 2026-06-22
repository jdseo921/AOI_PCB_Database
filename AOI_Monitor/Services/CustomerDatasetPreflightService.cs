using System.IO;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public sealed class CustomerDatasetPreflightCriteria
{
    public ValidationDatasetQualityCriteria DatasetQualityCriteria { get; set; } = new();
    public bool RequireImagesSubfolder { get; set; } = true;
    public bool RequireGoldenSubfolder { get; set; } = true;
    public bool TreatMissingGoldenAsFailure { get; set; } = true;
}

public sealed class CustomerDatasetPreflightResult
{
    public string Status { get; set; } = "FAIL";
    public string DatasetFolder { get; set; } = string.Empty;
    public string ManifestPath { get; set; } = string.Empty;
    public int ManifestRows { get; set; }
    public int ImageCount { get; set; }
    public int OkCount { get; set; }
    public int NgCount { get; set; }
    public int DefectClassCount { get; set; }
    public int MissingImageCount { get; set; }
    public int MissingGoldenCount { get; set; }
    public int DuplicateImageCount { get; set; }
    public int DuplicateFileHashCount { get; set; }
    public int SideCoverageCount { get; set; }
    public int MissingMetadataCount { get; set; }
    public DatasetQualitySummary DatasetQuality { get; set; } = new();
    public List<string> BlockingFailures { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

public static class CustomerDatasetPreflightService
{
    public static readonly string[] RequiredManifestColumns =
    [
        "image",
        "ground_truth",
        "golden_image",
        "defect_type",
        "side",
        "refdes",
        "roi_id",
        "roi_type",
        "lot_id",
        "board_model",
        "notes",
    ];

    private static readonly HashSet<string> SupportedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg",
    };

    public static CustomerDatasetPreflightResult Validate(
        string datasetFolder,
        string manifestPath,
        CustomerDatasetPreflightCriteria? criteria = null)
    {
        criteria ??= new CustomerDatasetPreflightCriteria();
        var result = new CustomerDatasetPreflightResult
        {
            DatasetFolder = datasetFolder?.Trim() ?? string.Empty,
            ManifestPath = manifestPath?.Trim() ?? string.Empty,
        };

        if (string.IsNullOrWhiteSpace(datasetFolder) || !Directory.Exists(datasetFolder))
        {
            result.BlockingFailures.Add(PlainLanguageGlossaryService.EvidenceMissing(
                $"The dataset folder was not found: {datasetFolder}",
                "The customer image set cannot be checked.",
                "Choose a readable dataset folder before running preflight."));
            return Finalize(result);
        }

        if (criteria.RequireImagesSubfolder && !Directory.Exists(Path.Combine(datasetFolder, "images")))
            result.BlockingFailures.Add(PlainLanguageGlossaryService.EvidenceMissing(
                "The dataset folder has no images/ subfolder.",
                "The app cannot find the board images to test.",
                "Add the images/ folder or select the correct dataset root."));
        if (criteria.RequireGoldenSubfolder && !Directory.Exists(Path.Combine(datasetFolder, "golden")))
            result.Warnings.Add(PlainLanguageGlossaryService.EvidenceMissing(
                "The dataset folder has no golden/ subfolder.",
                "Golden images are the reference images used to compare known-good boards.",
                "Add golden/ reference images when customer validation requires image comparison."));

        if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
        {
            result.BlockingFailures.Add(PlainLanguageGlossaryService.EvidenceMissing(
                $"The manifest CSV was not found: {manifestPath}",
                "The app does not know which images are OK, NG, or linked to defects.",
                "Provide the labeled manifest CSV."));
            return Finalize(result);
        }

        try
        {
            ValidateColumns(result, manifestPath);
            var manifest = BatchValidationService.LoadValidationManifest(manifestPath, datasetFolder);
            result.ManifestRows = manifest.OrderedEntries.Count;
            result.ImageCount = manifest.OrderedEntries.Count(entry => !string.IsNullOrWhiteSpace(entry.ImagePath));
            result.OkCount = manifest.OrderedEntries.Count(entry => BatchValidationService.NormalizeBinaryLabel(entry.Label) == "OK");
            result.NgCount = manifest.OrderedEntries.Count(entry => BatchValidationService.NormalizeBinaryLabel(entry.Label) == "NG");

            var duplicateImages = manifest.OrderedEntries
                .Select(entry => entry.ImagePath ?? string.Empty)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .GroupBy(path => Path.GetFullPath(path), StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .SelectMany(group => group)
                .ToArray();
            result.DuplicateImageCount = duplicateImages.Length;
            if (duplicateImages.Length > 0)
                result.BlockingFailures.Add($"Manifest contains {duplicateImages.Length} duplicate image row(s).");

            foreach (var entry in manifest.OrderedEntries)
            {
                if (string.IsNullOrWhiteSpace(entry.ImagePath) || !File.Exists(entry.ImagePath))
                {
                    result.MissingImageCount++;
                    result.BlockingFailures.Add(PlainLanguageGlossaryService.EvidenceMissing(
                        $"An image file is missing: {entry.ImagePath ?? "(blank)"}",
                        "The validation result would not cover every manifest row.",
                        "Fix the image path or remove the row from the manifest."));
                    continue;
                }

                if (!SupportedImageExtensions.Contains(Path.GetExtension(entry.ImagePath)))
                    result.Warnings.Add($"Image file uses a non-preferred extension: {entry.ImagePath}. Use PNG/JPG/JPEG.");

                ValidateNaming(result, entry.ImagePath);
                ValidateMetadataCompleteness(result, entry);

                if (string.IsNullOrWhiteSpace(entry.GoldenPath) || !File.Exists(entry.GoldenPath))
                {
                    result.MissingGoldenCount++;
                    var message = PlainLanguageGlossaryService.EvidenceMissing(
                        $"Golden image is missing for {Path.GetFileName(entry.ImagePath)}: {entry.GoldenPath ?? "(blank)"}",
                        "The app cannot compare this board image against the expected reference.",
                        "Provide the golden image path or mark this dataset as not using golden references.");
                    if (criteria.TreatMissingGoldenAsFailure)
                        result.BlockingFailures.Add(message);
                    else
                        result.Warnings.Add(message);
                }
            }

            var rows = manifest.OrderedEntries
                .Where(entry => !string.IsNullOrWhiteSpace(entry.ImagePath))
                .Select(entry => new BatchTestRow
                {
                    ImagePath = entry.ImagePath!,
                    Image = Path.GetFileName(entry.ImagePath!),
                    GoldenImagePath = entry.GoldenPath ?? string.Empty,
                    GroundTruth = entry.Label,
                    DefectType = entry.DefectType,
                    NormalizedDefectClass = BatchValidationService.NormalizeDefectClass(entry.DefectType),
                    Side = entry.Side,
                    RoiId = entry.RoiId,
                    RoiType = entry.RoiType,
                    LotId = entry.LotId,
                    BoardModel = entry.BoardModel,
                    Notes = entry.Notes,
                })
                .ToArray();

            result.DatasetQuality = DatasetQualityService.Analyze(rows, manifest, criteria.DatasetQualityCriteria);
            result.DefectClassCount = result.DatasetQuality.DefectClassCount;
            result.DuplicateFileHashCount = result.DatasetQuality.DuplicateFileHashes;
            if (result.DuplicateFileHashCount > 0)
                result.BlockingFailures.Add(PlainLanguageGlossaryService.EvidenceMissing(
                    $"Dataset contains {result.DuplicateFileHashCount} row(s) with duplicate image file hashes.",
                    "Duplicate images can make model results look better than they really are.",
                    "Remove duplicate image rows before client validation."));
            result.SideCoverageCount = rows
                .Select(row => BatchValidationService.NormalizeSide(row.Side))
                .Where(side => side != "UNASSIGNED")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            if (rows.Length > 0 && result.SideCoverageCount == 0)
                result.BlockingFailures.Add(PlainLanguageGlossaryService.EvidenceMissing(
                    "The manifest does not identify top/side/bottom view coverage.",
                    "Operators need to know which board view was inspected.",
                    "Fill the side/view column for validation rows."));
            result.BlockingFailures.AddRange(result.DatasetQuality.BlockingFailures);
            result.Warnings.AddRange(result.DatasetQuality.Warnings.Where(warning => !warning.Contains("duplicate file hashes", StringComparison.OrdinalIgnoreCase)));
            result.Warnings.AddRange(manifest.Warnings);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            result.BlockingFailures.Add(PlainLanguageGlossaryService.EvidenceMissing(
                "Dataset preflight could not read the manifest CSV.",
                "The dataset cannot be trusted until the label file opens cleanly.",
                $"Fix file access or CSV format. Detail: {ex.GetType().Name}."));
        }

        return Finalize(result);
    }

    private static void ValidateColumns(CustomerDatasetPreflightResult result, string manifestPath)
    {
        var firstLine = File.ReadLines(manifestPath).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(firstLine))
        {
            result.BlockingFailures.Add("Manifest CSV is empty.");
            return;
        }

        var headers = SplitCsvLine(firstLine)
            .Select(header => header.Trim().Replace(" ", "_", StringComparison.Ordinal).Replace("-", "_", StringComparison.Ordinal).ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var column in RequiredManifestColumns)
        {
            if (!headers.Contains(column))
                result.BlockingFailures.Add(PlainLanguageGlossaryService.EvidenceMissing(
                    $"Manifest CSV is missing required column '{column}'.",
                    "The app cannot verify all required dataset evidence.",
                    $"Add the '{column}' column."));
        }
    }

    private static void ValidateNaming(CustomerDatasetPreflightResult result, string imagePath)
    {
        var fileName = Path.GetFileName(imagePath);
        if (string.IsNullOrWhiteSpace(fileName))
            return;

        if (fileName.Any(char.IsWhiteSpace))
            result.Warnings.Add($"Image file name contains whitespace: {fileName}. Use stable snake_case names.");
    }

    private static void ValidateMetadataCompleteness(CustomerDatasetPreflightResult result, GroundTruthEntry entry)
    {
        if (BatchValidationService.NormalizeSide(entry.Side) == "UNASSIGNED")
        {
            result.MissingMetadataCount++;
            result.BlockingFailures.Add(PlainLanguageGlossaryService.EvidenceMissing(
                $"Manifest row is missing side/view metadata for {Path.GetFileName(entry.ImagePath ?? string.Empty)}.",
                "The validation evidence cannot show which board view was checked.",
                "Fill the side/view field."));
        }

        var hasAnyRoiMetadata =
            !string.IsNullOrWhiteSpace(entry.RefDes) ||
            !string.IsNullOrWhiteSpace(entry.RoiId) ||
            !string.IsNullOrWhiteSpace(entry.RoiType);
        var hasAllRoiMetadata =
            !string.IsNullOrWhiteSpace(entry.RefDes) &&
            !string.IsNullOrWhiteSpace(entry.RoiId) &&
            !string.IsNullOrWhiteSpace(entry.RoiType);

        if (hasAnyRoiMetadata && !hasAllRoiMetadata)
        {
            result.MissingMetadataCount++;
            result.BlockingFailures.Add(PlainLanguageGlossaryService.EvidenceMissing(
                $"ROI/refdes metadata is incomplete for {Path.GetFileName(entry.ImagePath ?? string.Empty)}.",
                $"{PlainLanguageGlossaryService.Explain("ROI")} Missing ROI/refdes data makes defect location evidence unclear.",
                "Provide refdes, roi_id, and roi_type together."));
        }
    }

    private static CustomerDatasetPreflightResult Finalize(CustomerDatasetPreflightResult result)
    {
        result.BlockingFailures = result.BlockingFailures
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        result.Warnings = result.Warnings
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        result.Status = result.BlockingFailures.Count > 0
            ? "FAIL"
            : result.Warnings.Count > 0 ? "CONDITIONAL" : "PASS";
        return result;
    }

    private static List<string> SplitCsvLine(string line)
    {
        var cells = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                cells.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        cells.Add(current.ToString());
        return cells;
    }
}
