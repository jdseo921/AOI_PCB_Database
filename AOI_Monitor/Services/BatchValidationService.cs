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

        return new BatchTestRow
        {
            ImagePath = imagePath,
            Image = Path.GetFileName(imagePath),
            GroundTruth = expected,
            EngineResult = analysis.Verdict,
            InspectionEngine = analysis.InspectionEngine,
            ModelVersion = analysis.ModelVersion,
            Score = analysis.DifferenceScore,
            PassFail = passFail,
            DefectType = string.IsNullOrWhiteSpace(manifest.DefectType)
                ? defect?.DefectType ?? analysis.SuggestedDefect
                : manifest.DefectType,
            Side = manifest.Side,
            RefDes = manifest.RefDes,
            LotId = manifest.LotId,
            BoardModel = manifest.BoardModel,
            Notes = manifest.Notes,
            RoiX = roi.X,
            RoiY = roi.Y,
            RoiWidth = roi.Width,
            RoiHeight = roi.Height,
        };
    }

    public static BatchTestRow ToErrorRow(
        string imagePath,
        GroundTruthEntry manifest,
        string message,
        string inspectionEngine = "Pixel Difference",
        string modelVersion = "PIXEL_DIFF_0.1")
    {
        return new BatchTestRow
        {
            ImagePath = imagePath,
            Image = string.IsNullOrWhiteSpace(imagePath) ? "(missing)" : Path.GetFileName(imagePath),
            GroundTruth = string.IsNullOrWhiteSpace(manifest.Label) ? "UNKNOWN" : manifest.Label.Trim().ToUpperInvariant(),
            EngineResult = "REVIEW",
            InspectionEngine = inspectionEngine,
            ModelVersion = modelVersion,
            Score = 0,
            PassFail = "N/A",
            DefectType = message,
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

    private static int CountResult(IEnumerable<BatchTestRow> rows, string result)
        => rows.Count(r => string.Equals(r.EngineResult, result, StringComparison.OrdinalIgnoreCase));

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
            return new ValidationManifest(entries, ordered, false);

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
        var lotIndex = FindHeader(headers, "lotid", "lot_id", "lot");
        var boardIndex = FindHeader(headers, "boardmodel", "board_model", "model", "board");
        var notesIndex = FindHeader(headers, "notes", "note", "comment", "comments");
        var isFormalManifest = HasHeader(headers, "image")
            && HasHeader(headers, "ground_truth", "groundtruth")
            && HasHeader(headers, "golden_image", "goldenimage")
            && HasHeader(headers, "defect_type", "defecttype")
            && HasHeader(headers, "side")
            && HasHeader(headers, "refdes")
            && HasHeader(headers, "lot_id", "lotid")
            && HasHeader(headers, "board_model", "boardmodel");

        if (imageIndex < 0 || truthIndex < 0)
            throw new InvalidDataException("Ground-truth CSV must include image and ground_truth/label columns.");

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

        return new ValidationManifest(entries, ordered, isFormalManifest);
    }

    public static string BuildResultsCsv(IEnumerable<BatchTestRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Image,Ground Truth,AI/Engine Result,Inspection Engine,Model Version,Score,Pass/Fail,Defect Type,Side,RefDes,LotId,BoardModel,Notes,Image Path,RoiX,RoiY,RoiWidth,RoiHeight");
        foreach (var row in rows)
        {
            sb.AppendLine(string.Join(",",
                EscapeCsv(row.Image),
                EscapeCsv(row.GroundTruth),
                EscapeCsv(row.EngineResult),
                EscapeCsv(row.InspectionEngine),
                EscapeCsv(row.ModelVersion),
                row.Score.ToString("F4", CultureInfo.InvariantCulture),
                EscapeCsv(row.PassFail),
                EscapeCsv(row.DefectType),
                EscapeCsv(row.Side),
                EscapeCsv(row.RefDes),
                EscapeCsv(row.LotId),
                EscapeCsv(row.BoardModel),
                EscapeCsv(row.Notes),
                EscapeCsv(row.ImagePath),
                row.RoiX.ToString("F4", CultureInfo.InvariantCulture),
                row.RoiY.ToString("F4", CultureInfo.InvariantCulture),
                row.RoiWidth.ToString("F4", CultureInfo.InvariantCulture),
                row.RoiHeight.ToString("F4", CultureInfo.InvariantCulture)));
        }

        return sb.ToString();
    }

    private static bool HasHeader(string[] headers, params string[] names)
        => names.Any(name => headers.Contains(name, StringComparer.OrdinalIgnoreCase));

    private static string Cell(IReadOnlyList<string> cells, int index)
        => index >= 0 && cells.Count > index ? cells[index].Trim() : string.Empty;

    private static string? ResolveOptionalPath(string path, string csvDir, string imageFolder)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        if (Path.IsPathRooted(path))
            return File.Exists(path) ? path : null;

        var csvRelative = Path.Combine(csvDir, path);
        if (File.Exists(csvRelative))
            return csvRelative;

        var folderRelative = Path.Combine(imageFolder, path);
        return File.Exists(folderRelative) ? folderRelative : null;
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

public sealed record ValidationManifest(
    IReadOnlyDictionary<string, GroundTruthEntry> ByImageName,
    IReadOnlyList<GroundTruthEntry> OrderedEntries,
    bool IsFormalManifest);

public sealed record RunItem(string ImagePath, GroundTruthEntry Manifest);

public sealed record GroundTruthEntry(
    string Label,
    string? GoldenPath,
    string DefectType,
    string Side,
    string RefDes,
    string LotId,
    string BoardModel,
    string Notes,
    string? ImagePath)
{
    public static GroundTruthEntry Unknown { get; } = new("UNKNOWN", null, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, null);
}

public sealed class BatchTestRow
{
    public string ImagePath { get; set; } = string.Empty;
    public string Image { get; set; } = string.Empty;
    public string GroundTruth { get; set; } = "UNKNOWN";
    public string EngineResult { get; set; } = "REVIEW";
    public string InspectionEngine { get; set; } = "Pixel Difference";
    public string ModelVersion { get; set; } = "PIXEL_DIFF_0.1";
    public double Score { get; set; }
    public string ScoreDisplay => $"{Score:F1}%";
    public string PassFail { get; set; } = "N/A";
    public bool IsFailed => PassFail == "FAIL";
    public string DefectType { get; set; } = "Unknown";
    public string Side { get; set; } = string.Empty;
    public string RefDes { get; set; } = string.Empty;
    public string LotId { get; set; } = string.Empty;
    public string BoardModel { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public double RoiX { get; set; }
    public double RoiY { get; set; }
    public double RoiWidth { get; set; }
    public double RoiHeight { get; set; }

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
            RoiX,
            RoiY,
            RoiWidth,
            RoiHeight,
            Side,
            RefDes,
            LotId,
            BoardModel,
            Notes,
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
            Side = record.Side,
            RefDes = record.RefDes,
            LotId = record.LotId,
            BoardModel = record.BoardModel,
            Notes = record.Notes,
            RoiX = record.RoiX,
            RoiY = record.RoiY,
            RoiWidth = record.RoiWidth,
            RoiHeight = record.RoiHeight,
        };
    }
}
