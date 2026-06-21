using System.Globalization;
using System.IO;
using System.Text;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public static class BatchValidationService
{
    public static BatchTestRow ToRow(string imagePath, GroundTruthEntry manifest, AnalysisResult analysis)
    {
        var defect = analysis.Defects.FirstOrDefault();
        var expected = string.IsNullOrWhiteSpace(manifest.Label) ? "UNKNOWN" : manifest.Label.Trim().ToUpperInvariant();
        var passFail = CalculatePassFail(expected, analysis.Verdict);
        var roi = defect?.BoundingBox ?? analysis.Hotspot;
        var predictedDefectClass = NormalizeDefectClass(defect?.DefectType ?? analysis.SuggestedDefect);
        var expectedDefectClass = NormalizeDefectClass(manifest.DefectType);
        var expectedSide = NormalizeSide(manifest.Side);
        var actualSide = NormalizeSide(defect?.SideOrViewType ?? string.Empty);
        var roiId = NormalizeAssignment(string.IsNullOrWhiteSpace(manifest.RoiId) ? defect?.RoiId : manifest.RoiId);
        var roiType = NormalizeAssignment(string.IsNullOrWhiteSpace(manifest.RoiType) ? defect?.RoiType : manifest.RoiType);

        return new BatchTestRow
        {
            ImagePath = imagePath,
            Image = Path.GetFileName(imagePath),
            GoldenImagePath = manifest.GoldenPath ?? string.Empty,
            GroundTruth = expected,
            EngineResult = analysis.Verdict,
            InspectionEngine = analysis.InspectionEngine,
            ModelVersion = analysis.ModelVersion,
            ThresholdProfileId = analysis.ThresholdProfileId,
            ThresholdProfileRevision = analysis.ThresholdProfileRevision,
            Score = analysis.DifferenceScore,
            PassFail = passFail,
            DefectType = string.IsNullOrWhiteSpace(defect?.DefectType) ? analysis.SuggestedDefect : defect.DefectType,
            NormalizedDefectClass = expectedDefectClass,
            NormalizedSide = expectedSide,
            FailureCategory = DetermineFailureCategory(expected, analysis.Verdict, expectedDefectClass, predictedDefectClass, expectedSide, actualSide, false),
            Side = manifest.Side,
            RefDes = manifest.RefDes,
            LotId = manifest.LotId,
            BoardModel = manifest.BoardModel,
            Notes = manifest.Notes,
            RecipeName = analysis.RecipeName,
            RecipeRevision = analysis.RecipeRevision,
            RoiId = roiId,
            RoiType = roiType,
            RoiX = roi.X,
            RoiY = roi.Y,
            RoiWidth = roi.Width,
            RoiHeight = roi.Height,
            ImageLoadMilliseconds = analysis.Timing.ImageLoadMilliseconds,
            PreprocessingMilliseconds = analysis.Timing.PreprocessingMilliseconds,
            InferenceMilliseconds = analysis.Timing.InferenceMilliseconds,
            OverlayRenderingMilliseconds = analysis.Timing.OverlayRenderingMilliseconds,
            TotalInspectionMilliseconds = analysis.Timing.TotalInspectionMilliseconds,
        };
    }

    public static BatchTestRow ToErrorRow(
        string imagePath,
        GroundTruthEntry manifest,
        string message,
        string inspectionEngine = "Pixel Difference Prototype Engine",
        string modelVersion = "PIXEL_DIFF_0.1")
    {
        return new BatchTestRow
        {
            ImagePath = imagePath,
            Image = string.IsNullOrWhiteSpace(imagePath) ? "(missing)" : Path.GetFileName(imagePath),
            GoldenImagePath = manifest.GoldenPath ?? string.Empty,
            GroundTruth = string.IsNullOrWhiteSpace(manifest.Label) ? "UNKNOWN" : manifest.Label.Trim().ToUpperInvariant(),
            EngineResult = "REVIEW",
            InspectionEngine = inspectionEngine,
            ModelVersion = modelVersion,
            Score = 0,
            PassFail = "N/A",
            DefectType = message,
            NormalizedDefectClass = NormalizeDefectClass(manifest.DefectType),
            NormalizedSide = NormalizeSide(manifest.Side),
            RoiId = NormalizeAssignment(manifest.RoiId),
            RoiType = NormalizeAssignment(manifest.RoiType),
            FailureCategory = "ERROR",
            Side = manifest.Side,
            RefDes = manifest.RefDes,
            LotId = manifest.LotId,
            BoardModel = manifest.BoardModel,
            Notes = manifest.Notes,
        };
    }

    public static string CalculatePassFail(string groundTruth, string engineResult)
    {
        var expected = NormalizeBinaryLabel(groundTruth);
        if (expected == "UNKNOWN")
            return "N/A";

        var actual = NormalizeBinaryLabel(engineResult);
        return expected == actual ? "PASS" : "FAIL";
    }

    public static BatchMetrics CalculateMetrics(IReadOnlyCollection<BatchTestRow> rows)
    {
        var known = rows.Where(r => NormalizeBinaryLabel(r.GroundTruth) != "UNKNOWN").ToArray();
        if (known.Length == 0)
            return new BatchMetrics(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, rows.Count, CountResult(rows, "OK"), CountResult(rows, "NG"), CountResult(rows, "REVIEW"));

        var tp = known.Count(r => NormalizeBinaryLabel(r.GroundTruth) == "NG" && NormalizeBinaryLabel(r.EngineResult) == "NG");
        var tn = known.Count(r => NormalizeBinaryLabel(r.GroundTruth) == "OK" && NormalizeBinaryLabel(r.EngineResult) == "OK");
        var fp = known.Count(r => NormalizeBinaryLabel(r.GroundTruth) == "OK" && NormalizeBinaryLabel(r.EngineResult) == "NG");
        var fn = known.Count(r => NormalizeBinaryLabel(r.GroundTruth) == "NG" && NormalizeBinaryLabel(r.EngineResult) == "OK");

        var accuracy = (tp + tn) / (double)known.Length;
        var precision = tp + fp == 0 ? 0 : tp / (double)(tp + fp);
        var recall = tp + fn == 0 ? 0 : tp / (double)(tp + fn);
        var falseCallRate = fp + tn == 0 ? 0 : fp / (double)(fp + tn);
        var unknown = rows.Count(r => NormalizeBinaryLabel(r.GroundTruth) == "UNKNOWN");
        return new BatchMetrics(accuracy, precision, recall, falseCallRate, tp, tn, fp, fn, fp, fn, tp, unknown, CountResult(rows, "OK"), CountResult(rows, "NG"), CountResult(rows, "REVIEW"));
    }

    public static BatchPerformanceSummary CalculatePerformanceSummary(IReadOnlyCollection<BatchTestRow> rows)
    {
        var timings = rows
            .Select(row => row.TotalInspectionMilliseconds)
            .Where(value => value > 0)
            .ToArray();

        if (timings.Length == 0)
            return new BatchPerformanceSummary(0, 0, 0, 0, 0);

        return new BatchPerformanceSummary(
            timings.Average(),
            timings.Max(),
            timings.Min(),
            timings.Count(value => value > 1000.0),
            timings.Length);
    }

    private static int CountResult(IEnumerable<BatchTestRow> rows, string result)
        => rows.Count(r => string.Equals(r.EngineResult, result, StringComparison.OrdinalIgnoreCase));

    public static string NormalizeDefectClass(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "UNASSIGNED";

        var normalized = value.Trim().ToUpperInvariant()
            .Replace("-", "_", StringComparison.Ordinal)
            .Replace(" ", "_", StringComparison.Ordinal);
        return normalized switch
        {
            "OK" or "PASS" or "GOOD" => "OK",
            "NG" or "FAIL" or "FAILED" or "DEFECT" or "DEFECTIVE" or "BAD" => "DEFECT",
            "UNKNOWN" or "N/A" or "NA" => "UNASSIGNED",
            _ => normalized,
        };
    }

    public static string NormalizeSide(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "UNASSIGNED";

        var normalized = value.Trim().ToUpperInvariant();
        return normalized switch
        {
            "TOP" or "T" => "TOP",
            "BOTTOM" or "BOT" or "B" => "BOTTOM",
            "SIDE" or "SIDE_VIEW" => "SIDE",
            "SAMPLE" or "UNKNOWN" or "N/A" or "NA" => "UNASSIGNED",
            _ => normalized.Replace(" ", "_", StringComparison.Ordinal),
        };
    }

    public static string NormalizeAssignment(string? value)
        => string.IsNullOrWhiteSpace(value) ? "UNASSIGNED" : value.Trim();

    public static string DetermineFailureCategory(
        string groundTruth,
        string engineResult,
        string expectedDefectClass,
        string actualDefectClass,
        string expectedSide,
        string actualSide,
        bool isError)
    {
        if (isError)
            return "ERROR";

        var expected = NormalizeBinaryLabel(groundTruth);
        if (expected == "UNKNOWN")
            return "UNKNOWN_GT";

        var actual = NormalizeBinaryLabel(engineResult);
        if (expected == "OK" && actual == "NG")
            return "FALSE_CALL";
        if (expected == "NG" && actual == "OK")
            return "POSSIBLE_ESCAPE";

        if (expected == "NG" && actual == "NG")
        {
            if (expectedDefectClass != "UNASSIGNED" &&
                actualDefectClass != "UNASSIGNED" &&
                !string.Equals(expectedDefectClass, actualDefectClass, StringComparison.OrdinalIgnoreCase))
            {
                return "WRONG_DEFECT_CLASS";
            }

            if (expectedSide != "UNASSIGNED" &&
                actualSide != "UNASSIGNED" &&
                !string.Equals(expectedSide, actualSide, StringComparison.OrdinalIgnoreCase))
            {
                return "WRONG_SIDE";
            }
        }

        return "PASS";
    }

    public static string NormalizeBinaryLabel(string label)
    {
        var normalized = label.Trim().ToUpperInvariant();
        return normalized switch
        {
            "OK" or "PASS" or "GOOD" or "TRUE_NEGATIVE" => "OK",
            "NG" or "FAIL" or "FAILED" or "DEFECT" or "DEFECTIVE" or "BAD" or "REVIEW" => "NG",
            _ => "UNKNOWN",
        };
    }

    public static IReadOnlyList<RunItem> BuildRunItems(IReadOnlyList<string> imageFiles, ValidationManifest manifest)
    {
        if (manifest.IsFormalManifest && manifest.OrderedEntries.Count > 0)
        {
            return manifest.OrderedEntries
                .Where(entry => !string.IsNullOrWhiteSpace(entry.ImagePath))
                .Select(entry => new RunItem(entry.ImagePath!, entry))
                .ToArray();
        }

        return imageFiles
            .Select(path =>
            {
                var imageName = Path.GetFileName(path);
                return new RunItem(
                    path,
                    manifest.ByImageName.TryGetValue(imageName, out var entry)
                        ? entry
                        : GroundTruthEntry.Unknown);
            })
            .ToArray();
    }

    public static ValidationManifest LoadValidationManifest(string? csvPath, string imageFolder)
    {
        var entries = new Dictionary<string, GroundTruthEntry>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<GroundTruthEntry>();
        if (string.IsNullOrWhiteSpace(csvPath) || !File.Exists(csvPath))
            return new ValidationManifest(entries, ordered, false, Array.Empty<string>());

        var lines = File.ReadAllLines(csvPath);
        if (lines.Length < 2)
            throw new InvalidDataException("Ground-truth CSV has no data rows.");

        var headers = SplitCsvLine(lines[0]).Select(NormalizeHeader).ToArray();
        var imageIndex = FindHeader(headers, "image", "filename", "file", "image_name", "sample");
        var truthIndex = FindHeader(headers, "groundtruth", "ground_truth", "gt", "label", "verdict", "expected");
        var goldenIndex = FindHeader(headers, "golden", "goldenpath", "golden_path", "goldenimage", "golden_image");
        var defectIndex = FindHeader(headers, "defecttype", "defect_type", "defect");
        var sideIndex = FindHeader(headers, "side", "view", "viewtype", "view_type");
        var refDesIndex = FindHeader(headers, "refdes", "ref_des", "reference", "reference_designator");
        var roiIdIndex = FindHeader(headers, "roiid", "roi_id", "roi");
        var roiTypeIndex = FindHeader(headers, "roitype", "roi_type");
        var lotIndex = FindHeader(headers, "lotid", "lot_id", "lot");
        var boardIndex = FindHeader(headers, "boardmodel", "board_model", "model", "board");
        var notesIndex = FindHeader(headers, "notes", "note", "comment", "comments");
        var isFormalManifest = HasHeader(headers, "image")
            && HasHeader(headers, "ground_truth", "groundtruth")
            && HasHeader(headers, "golden_image", "goldenimage")
            && HasHeader(headers, "defect_type", "defecttype")
            && HasHeader(headers, "side")
            && HasHeader(headers, "refdes")
            && HasHeader(headers, "roi_id", "roiid")
            && HasHeader(headers, "roi_type", "roitype")
            && HasHeader(headers, "lot_id", "lotid")
            && HasHeader(headers, "board_model", "boardmodel");

        if (imageIndex < 0 || truthIndex < 0)
            throw new InvalidDataException("Ground-truth CSV must include image and ground_truth/label columns.");

        var warnings = BuildManifestWarnings(headers);

        var csvDir = Path.GetDirectoryName(csvPath) ?? imageFolder;
        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var cells = SplitCsvLine(line);
            if (cells.Count <= Math.Max(imageIndex, truthIndex))
                continue;

            var imageName = Path.GetFileName(cells[imageIndex].Trim());
            var label = cells[truthIndex].Trim();
            var imagePath = ResolveOptionalPath(cells[imageIndex].Trim(), csvDir, imageFolder);
            var goldenPath = goldenIndex >= 0 && cells.Count > goldenIndex
                ? ResolveOptionalPath(cells[goldenIndex].Trim(), csvDir, imageFolder)
                : null;
            var entry = new GroundTruthEntry(
                label,
                goldenPath,
                Cell(cells, defectIndex),
                Cell(cells, sideIndex),
                Cell(cells, refDesIndex),
                Cell(cells, roiIdIndex),
                Cell(cells, roiTypeIndex),
                Cell(cells, lotIndex),
                Cell(cells, boardIndex),
                Cell(cells, notesIndex),
                imagePath);

            if (!string.IsNullOrWhiteSpace(imageName))
            {
                entries[imageName] = entry;
                ordered.Add(entry);
            }
        }

        return new ValidationManifest(entries, ordered, isFormalManifest, warnings);
    }

    public static string BuildResultsCsv(IEnumerable<BatchTestRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Image,Ground Truth,AI/Engine Result,Inspection Engine,Model Version,Recipe Name,Recipe Revision,Score,Pass/Fail,Defect Type,Normalized Defect Class,Normalized Side,Failure Category,ROI ID,ROI Type,Side,RefDes,LotId,BoardModel,Notes,Image Path,RoiX,RoiY,RoiWidth,RoiHeight,ImageLoadMs,PreprocessingMs,InferenceMs,OverlayRenderingMs,TotalInspectionMs");
        foreach (var row in rows)
        {
            sb.AppendLine(string.Join(",",
                EscapeCsv(row.Image),
                EscapeCsv(row.GroundTruth),
                EscapeCsv(row.EngineResult),
                EscapeCsv(row.InspectionEngine),
                EscapeCsv(row.ModelVersion),
                EscapeCsv(row.RecipeName),
                EscapeCsv(row.RecipeRevision),
                row.Score.ToString("F4", CultureInfo.InvariantCulture),
                EscapeCsv(row.PassFail),
                EscapeCsv(row.DefectType),
                EscapeCsv(row.NormalizedDefectClass),
                EscapeCsv(row.NormalizedSide),
                EscapeCsv(row.FailureCategory),
                EscapeCsv(row.RoiId),
                EscapeCsv(row.RoiType),
                EscapeCsv(row.Side),
                EscapeCsv(row.RefDes),
                EscapeCsv(row.LotId),
                EscapeCsv(row.BoardModel),
                EscapeCsv(row.Notes),
                EscapeCsv(row.ImagePath),
                row.RoiX.ToString("F4", CultureInfo.InvariantCulture),
                row.RoiY.ToString("F4", CultureInfo.InvariantCulture),
                row.RoiWidth.ToString("F4", CultureInfo.InvariantCulture),
                row.RoiHeight.ToString("F4", CultureInfo.InvariantCulture),
                row.ImageLoadMilliseconds.ToString("F1", CultureInfo.InvariantCulture),
                row.PreprocessingMilliseconds.ToString("F1", CultureInfo.InvariantCulture),
                row.InferenceMilliseconds.ToString("F1", CultureInfo.InvariantCulture),
                row.OverlayRenderingMilliseconds.ToString("F1", CultureInfo.InvariantCulture),
                row.TotalInspectionMilliseconds.ToString("F1", CultureInfo.InvariantCulture)));
        }

        return sb.ToString();
    }

    private static bool HasHeader(string[] headers, params string[] names)
        => names.Any(name => headers.Contains(name, StringComparer.OrdinalIgnoreCase));

    private static IReadOnlyList<string> BuildManifestWarnings(string[] headers)
    {
        var warnings = new List<string>();
        AddMissingColumnWarning(headers, warnings, "golden_image", "goldenimage");
        AddMissingColumnWarning(headers, warnings, "defect_type", "defecttype");
        AddMissingColumnWarning(headers, warnings, "side");
        AddMissingColumnWarning(headers, warnings, "refdes");
        AddMissingColumnWarning(headers, warnings, "roi_id", "roiid");
        AddMissingColumnWarning(headers, warnings, "roi_type", "roitype");
        AddMissingColumnWarning(headers, warnings, "lot_id", "lotid");
        AddMissingColumnWarning(headers, warnings, "board_model", "boardmodel");
        AddMissingColumnWarning(headers, warnings, "notes");
        return warnings;
    }

    private static void AddMissingColumnWarning(string[] headers, List<string> warnings, params string[] names)
    {
        if (!HasHeader(headers, names))
            warnings.Add($"Ground-truth CSV is missing optional column '{names[0]}'; related breakdown evidence will use UNASSIGNED/UNKNOWN where needed.");
    }

    private static string Cell(IReadOnlyList<string> cells, int index)
        => index >= 0 && cells.Count > index ? cells[index].Trim() : string.Empty;

    private static string? ResolveOptionalPath(string path, string csvDir, string imageFolder)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            if (Path.IsPathRooted(path))
                return path;

            var csvRelative = Path.GetFullPath(Path.Combine(csvDir, path));
            if (File.Exists(csvRelative))
                return csvRelative;

            var folderRelative = Path.GetFullPath(Path.Combine(imageFolder, path));
            return folderRelative;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException($"Invalid path in validation CSV: '{path}'. {ex.Message}", ex);
        }
    }

    private static int FindHeader(string[] headers, params string[] names)
    {
        for (var i = 0; i < headers.Length; i++)
        {
            if (names.Contains(headers[i], StringComparer.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    private static string NormalizeHeader(string value)
        => value.Trim().Replace(" ", "", StringComparison.Ordinal).Replace("-", "_", StringComparison.Ordinal).ToLowerInvariant();

    private static List<string> SplitCsvLine(string line)
    {
        var cells = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                cells.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }

        cells.Add(sb.ToString());
        return cells;
    }

    private static string EscapeCsv(string value)
        => $"\"{value.Replace("\"", "\"\"")}\"";
}

public sealed record BatchMetrics(
    double Accuracy,
    double Precision,
    double Recall,
    double FalseCallRate,
    int TruePositive,
    int TrueNegative,
    int FalsePositive,
    int FalseNegative,
    int FalseCall,
    int PossibleEscape,
    int VerifiedNg,
    int Unknown,
    int OkCount,
    int NgCount,
    int ReviewCount);

public sealed record BatchPerformanceSummary(
    double AverageMilliseconds,
    double MaxMilliseconds,
    double MinMilliseconds,
    int CountOverOneSecond,
    int TimedImageCount);

public sealed record ValidationManifest(
    IReadOnlyDictionary<string, GroundTruthEntry> ByImageName,
    IReadOnlyList<GroundTruthEntry> OrderedEntries,
    bool IsFormalManifest,
    IReadOnlyList<string> Warnings);

public sealed record RunItem(string ImagePath, GroundTruthEntry Manifest);

public sealed record GroundTruthEntry(
    string Label,
    string? GoldenPath,
    string DefectType,
    string Side,
    string RefDes,
    string RoiId,
    string RoiType,
    string LotId,
    string BoardModel,
    string Notes,
    string? ImagePath)
{
    public static GroundTruthEntry Unknown { get; } = new("UNKNOWN", null, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, null);
}

public sealed class BatchTestRow
{
    public string ImagePath { get; set; } = string.Empty;
    public string Image { get; set; } = string.Empty;
    public string GoldenImagePath { get; set; } = string.Empty;
    public string GroundTruth { get; set; } = "UNKNOWN";
    public string EngineResult { get; set; } = "REVIEW";
    public string InspectionEngine { get; set; } = "Pixel Difference Prototype Engine";
    public string ModelVersion { get; set; } = "PIXEL_DIFF_0.1";
    public string ThresholdProfileId { get; set; } = string.Empty;
    public string ThresholdProfileRevision { get; set; } = string.Empty;
    public double Score { get; set; }
    public string ScoreDisplay => $"{Score:F1}%";
    public string PassFail { get; set; } = "N/A";
    public bool IsFailed => PassFail == "FAIL";
    public string DefectType { get; set; } = "Unknown";
    public string NormalizedDefectClass { get; set; } = "UNASSIGNED";
    public string NormalizedSide { get; set; } = "UNASSIGNED";
    public string FailureCategory { get; set; } = "UNKNOWN_GT";
    public string Side { get; set; } = string.Empty;
    public string RefDes { get; set; } = string.Empty;
    public string LotId { get; set; } = string.Empty;
    public string BoardModel { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public string RecipeName { get; set; } = string.Empty;
    public string RecipeRevision { get; set; } = string.Empty;
    public string RoiId { get; set; } = string.Empty;
    public string RoiType { get; set; } = string.Empty;
    public double RoiX { get; set; }
    public double RoiY { get; set; }
    public double RoiWidth { get; set; }
    public double RoiHeight { get; set; }
    public double ImageLoadMilliseconds { get; set; }
    public double PreprocessingMilliseconds { get; set; }
    public double InferenceMilliseconds { get; set; }
    public double OverlayRenderingMilliseconds { get; set; }
    public double TotalInspectionMilliseconds { get; set; }
    public bool IsOverOneSecond => TotalInspectionMilliseconds > 1000.0;
    public string TotalTimeDisplay => TotalInspectionMilliseconds <= 0
        ? "--"
        : $"{TotalInspectionMilliseconds:F0} ms";

    public BatchTestResultRecord ToRecord()
    {
        return new BatchTestResultRecord(
            0,
            0,
            ImagePath,
            Image,
            GroundTruth,
            EngineResult,
            InspectionEngine,
            ModelVersion,
            Score,
            PassFail,
            DefectType,
            NormalizedDefectClass,
            NormalizedSide,
            RoiId,
            RoiType,
            FailureCategory,
            RoiX,
            RoiY,
            RoiWidth,
            RoiHeight,
            Side,
            RefDes,
            LotId,
            BoardModel,
            BuildPersistedNotes(Notes, RecipeName, RecipeRevision, RoiId, RoiType),
            ImageLoadMilliseconds,
            PreprocessingMilliseconds,
            InferenceMilliseconds,
            OverlayRenderingMilliseconds,
            TotalInspectionMilliseconds,
            DateTime.UtcNow);
    }

    public static BatchTestRow FromRecord(BatchTestResultRecord record)
    {
        return new BatchTestRow
        {
            ImagePath = record.ImagePath,
            Image = record.ImageName,
            GroundTruth = record.GroundTruth,
            EngineResult = record.EngineResult,
            InspectionEngine = record.InspectionEngine,
            ModelVersion = record.ModelVersion,
            Score = record.Score,
            PassFail = record.PassFail,
            DefectType = record.DefectType,
            NormalizedDefectClass = record.NormalizedDefectClass,
            NormalizedSide = record.NormalizedSide,
            RoiId = record.RoiId,
            RoiType = record.RoiType,
            FailureCategory = record.FailureCategory,
            Side = record.Side,
            RefDes = record.RefDes,
            LotId = record.LotId,
            BoardModel = record.BoardModel,
            Notes = record.Notes,
            ImageLoadMilliseconds = record.ImageLoadMilliseconds,
            PreprocessingMilliseconds = record.PreprocessingMilliseconds,
            InferenceMilliseconds = record.InferenceMilliseconds,
            OverlayRenderingMilliseconds = record.OverlayRenderingMilliseconds,
            TotalInspectionMilliseconds = record.TotalInspectionMilliseconds,
            RoiX = record.RoiX,
            RoiY = record.RoiY,
            RoiWidth = record.RoiWidth,
            RoiHeight = record.RoiHeight,
        };
    }

    private static string BuildPersistedNotes(string notes, string recipeName, string recipeRevision, string roiId, string roiType)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(notes))
            parts.Add(notes);
        if (!string.IsNullOrWhiteSpace(recipeName) || !string.IsNullOrWhiteSpace(recipeRevision))
            parts.Add($"Recipe={recipeName} rev={recipeRevision}");
        if (!string.IsNullOrWhiteSpace(roiId) || !string.IsNullOrWhiteSpace(roiType))
            parts.Add($"ROI={roiId} type={roiType}");
        return string.Join("; ", parts);
    }
}
