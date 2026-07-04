using System.Globalization;
using System.Text;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

/// <summary>
/// Imports pick-and-place centroid (CAD) data and converts it into draft recipe ROIs, which is how
/// production AOI machines are typically programmed (from CAD/centroid exports rather than hand-drawn ROIs).
/// Honesty boundary: this produces approximate placement from centroid data without calibration;
/// positions are auto-fit to the loaded image and must be reviewed/adjusted in the Recipe Editor.
/// </summary>
public static class CentroidRoiImportService
{
    private const double MilToMm = 0.0254;
    private const double MinExtentHalfMm = 0.5;
    private const double MinRoiSizePixels = 8;
    private const double PackageSizePaddingFactor = 1.5;

    private static readonly string[] RefDesKeys = { "refdes", "designator", "ref", "reference", "refdesignator" };
    private static readonly string[] XKeys = { "x", "midx", "posx", "centerx", "locationx" };
    private static readonly string[] YKeys = { "y", "midy", "posy", "centery", "locationy" };
    private static readonly string[] RotationKeys = { "rotation", "rot", "angle" };
    private static readonly string[] SideKeys = { "layer", "side", "tb", "surface", "boardside", "mountside" };
    private static readonly string[] PackageKeys = { "package", "footprint", "pattern", "device" };
    private static readonly string[] UnitKeys = { "unit", "units" };

    // Nominal body sizes for the most common imperial chip packages. Matched on exact tokens only
    // (so "R_0201_0603Metric" does not accidentally match 0603); anything else uses the default square.
    private static readonly Dictionary<string, (double WidthMm, double HeightMm)> ChipPackageSizesMm =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["0402"] = (1.0, 0.5),
            ["0603"] = (1.6, 0.8),
            ["0805"] = (2.0, 1.25),
            ["1206"] = (3.2, 1.6),
        };

    /// <summary>
    /// Parses pick-and-place centroid CSV text into placement records. Supports the common centroid
    /// header shapes (RefDes/Designator, X/Mid X/PosX, Y/Mid Y/PosY, Rotation/Rot, Layer/Side/TB,
    /// Package/Footprint), comma/semicolon/tab delimiters, quoted fields, and mil-to-mm conversion when
    /// a header or unit hint says mil. Malformed rows are skipped with a warning; a bad row never throws.
    /// </summary>
    public static CentroidCsvParseResult ParseCentroidCsv(string csvText)
    {
        var records = new List<CentroidRecord>();
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(csvText))
        {
            warnings.Add("Centroid CSV content is empty.");
            return new CentroidCsvParseResult(records, warnings);
        }

        var lines = csvText.Split('\n');
        var delimiter = ',';
        ColumnMap? columns = null;
        var headerLineIndex = -1;
        var preambleSaysMil = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (line.TrimStart().StartsWith('#'))
            {
                preambleSaysMil |= LooksLikeMilUnitHint(line);
                continue;
            }

            var candidateDelimiter = DetectDelimiter(line);
            var candidate = BuildColumnMap(SplitDelimited(line, candidateDelimiter));
            if (candidate.IsValid)
            {
                delimiter = candidateDelimiter;
                columns = candidate;
                headerLineIndex = i;
                break;
            }

            // Preamble/title line (common in Altium exports) before the real header row.
            preambleSaysMil |= LooksLikeMilUnitHint(line);
        }

        if (columns is null)
        {
            warnings.Add("No header row with recognizable columns was found. Expected columns such as RefDes/Designator, X/Mid X/PosX, and Y/Mid Y/PosY.");
            return new CentroidCsvParseResult(records, warnings);
        }

        var defaultXScale = columns.XHasUnitHint ? columns.XHeaderScale : (preambleSaysMil ? MilToMm : 1.0);
        var defaultYScale = columns.YHasUnitHint ? columns.YHeaderScale : (preambleSaysMil ? MilToMm : 1.0);

        for (var i = headerLineIndex + 1; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
                continue;

            var fields = SplitDelimited(line, delimiter);
            var lineNumber = i + 1;
            var refDes = GetField(fields, columns.RefDes).Trim();
            if (refDes.Length == 0)
            {
                warnings.Add($"Line {lineNumber}: missing reference designator; row skipped.");
                continue;
            }

            var xScale = defaultXScale;
            var yScale = defaultYScale;
            var unitText = GetField(fields, columns.Unit).Trim();
            if (unitText.Contains("mm", StringComparison.OrdinalIgnoreCase))
            {
                xScale = 1.0;
                yScale = 1.0;
            }
            else if (unitText.Contains("mil", StringComparison.OrdinalIgnoreCase))
            {
                xScale = MilToMm;
                yScale = MilToMm;
            }

            if (!TryParseLength(GetField(fields, columns.X), xScale, out var xMm) ||
                !TryParseLength(GetField(fields, columns.Y), yScale, out var yMm))
            {
                warnings.Add($"Line {lineNumber}: X/Y coordinates are not numeric; row skipped.");
                continue;
            }

            var rotationText = GetField(fields, columns.Rotation).Trim();
            double.TryParse(rotationText, NumberStyles.Float, CultureInfo.InvariantCulture, out var rotation);

            records.Add(new CentroidRecord(
                refDes,
                xMm,
                yMm,
                rotation,
                NormalizeSide(GetField(fields, columns.Side)),
                GetField(fields, columns.Package).Trim()));
        }

        return new CentroidCsvParseResult(records, warnings);
    }

    /// <summary>
    /// Generates draft ROIs from centroid records for the requested board side. Board millimeters are
    /// mapped to the image by auto-fit: the placement extents (plus <see cref="CentroidRoiImportOptions.BoardMarginPercent"/>
    /// margin) are scaled to fit centered in the image preserving aspect ratio, with the CAD bottom-up Y axis
    /// flipped to the image top-down Y axis. Output ROI coordinates use the recipe's normalized 0..1 image
    /// space (the convention consumed by RecipeService and the Recipe Editor canvas).
    /// Honesty boundary: approximate placement from centroid data without calibration; positions are
    /// auto-fit to the loaded image and must be reviewed/adjusted in the Recipe Editor.
    /// </summary>
    public static IReadOnlyList<RecipeRoiDocument> GenerateRois(
        IReadOnlyList<CentroidRecord> records,
        int imagePixelWidth,
        int imagePixelHeight,
        CentroidRoiImportOptions options)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(options);

        var rois = new List<RecipeRoiDocument>();
        if (imagePixelWidth <= 0 || imagePixelHeight <= 0)
            return rois;

        var targetSide = string.IsNullOrWhiteSpace(options.Side) ? "Top" : options.Side.Trim();
        var placements = records
            .Where(r => string.Equals(r.Side, targetSide, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (placements.Count == 0)
            return rois;

        var minX = placements.Min(r => r.XMm);
        var maxX = placements.Max(r => r.XMm);
        var minY = placements.Min(r => r.YMm);
        var maxY = placements.Max(r => r.YMm);

        // Symmetric margin around the placement extents; because the fit is centered, the margin only
        // reduces the scale. Degenerate extents (single part or one row/column) get a nominal 1 mm span.
        var margin = Math.Max(0, options.BoardMarginPercent) / 100.0;
        var halfWidthMm = Math.Max((maxX - minX) / 2.0, MinExtentHalfMm) * (1.0 + 2.0 * margin);
        var halfHeightMm = Math.Max((maxY - minY) / 2.0, MinExtentHalfMm) * (1.0 + 2.0 * margin);
        var centerXMm = (minX + maxX) / 2.0;
        var centerYMm = (minY + maxY) / 2.0;
        var scale = Math.Min(imagePixelWidth / (2.0 * halfWidthMm), imagePixelHeight / (2.0 * halfHeightMm));

        var issuedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in placements)
        {
            var centerPx = imagePixelWidth / 2.0 + (record.XMm - centerXMm) * scale;
            // CAD Y grows bottom-up while image Y grows top-down, so flip around the image center.
            var centerPy = imagePixelHeight / 2.0 - (record.YMm - centerYMm) * scale;

            var (roiWidthPx, roiHeightPx) = ResolveRoiSizePixels(record, scale, options);
            roiWidthPx = Math.Min(roiWidthPx, imagePixelWidth);
            roiHeightPx = Math.Min(roiHeightPx, imagePixelHeight);

            var left = Math.Clamp(centerPx - roiWidthPx / 2.0, 0, imagePixelWidth - roiWidthPx);
            var top = Math.Clamp(centerPy - roiHeightPx / 2.0, 0, imagePixelHeight - roiHeightPx);

            rois.Add(new RecipeRoiDocument
            {
                Id = BuildDeterministicId(record.RefDes, issuedIds),
                Name = record.RefDes,
                RoiType = "Presence",
                X = Math.Round(left / imagePixelWidth, 5),
                Y = Math.Round(top / imagePixelHeight, 5),
                Width = Math.Round(roiWidthPx / imagePixelWidth, 5),
                Height = Math.Round(roiHeightPx / imagePixelHeight, 5),
                Enabled = true,
            });
        }

        return rois;
    }

    private static (double WidthPx, double HeightPx) ResolveRoiSizePixels(
        CentroidRecord record,
        double pixelsPerMm,
        CentroidRoiImportOptions options)
    {
        var defaultSize = Math.Max(MinRoiSizePixels, options.DefaultRoiSizePixels);
        if (!TryGetChipPackageSizeMm(record.Package, out var widthMm, out var heightMm))
            return (defaultSize, defaultSize);

        var rotationMod = ((record.RotationDeg % 180.0) + 180.0) % 180.0;
        if (rotationMod > 45.0 && rotationMod < 135.0)
            (widthMm, heightMm) = (heightMm, widthMm);

        return (
            Math.Max(MinRoiSizePixels, widthMm * pixelsPerMm * PackageSizePaddingFactor),
            Math.Max(MinRoiSizePixels, heightMm * pixelsPerMm * PackageSizePaddingFactor));
    }

    private static bool TryGetChipPackageSizeMm(string package, out double widthMm, out double heightMm)
    {
        widthMm = 0;
        heightMm = 0;
        if (string.IsNullOrWhiteSpace(package))
            return false;

        foreach (var token in TokenizePackage(package))
        {
            if (ChipPackageSizesMm.TryGetValue(token, out var size))
            {
                widthMm = size.WidthMm;
                heightMm = size.HeightMm;
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> TokenizePackage(string package)
    {
        var token = new StringBuilder();
        foreach (var ch in package)
        {
            if (char.IsLetterOrDigit(ch))
            {
                token.Append(ch);
                continue;
            }

            if (token.Length > 0)
            {
                yield return token.ToString();
                token.Clear();
            }
        }

        if (token.Length > 0)
            yield return token.ToString();
    }

    private static string BuildDeterministicId(string refDes, HashSet<string> issuedIds)
    {
        var builder = new StringBuilder("CENTROID-");
        foreach (var ch in refDes.Trim().ToUpperInvariant())
            builder.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_');

        var baseId = builder.ToString();
        var candidate = baseId;
        var suffix = 1;
        while (!issuedIds.Add(candidate))
        {
            suffix++;
            candidate = $"{baseId}-{suffix}";
        }

        return candidate;
    }

    private static ColumnMap BuildColumnMap(IReadOnlyList<string> headerFields)
    {
        var map = new ColumnMap();
        for (var i = 0; i < headerFields.Count; i++)
        {
            var (key, unitScale, hasUnitHint) = NormalizeHeaderField(headerFields[i]);
            if (key.Length == 0)
                continue;

            if (map.RefDes < 0 && RefDesKeys.Contains(key))
            {
                map.RefDes = i;
            }
            else if (map.X < 0 && XKeys.Contains(key))
            {
                map.X = i;
                map.XHeaderScale = unitScale;
                map.XHasUnitHint = hasUnitHint;
            }
            else if (map.Y < 0 && YKeys.Contains(key))
            {
                map.Y = i;
                map.YHeaderScale = unitScale;
                map.YHasUnitHint = hasUnitHint;
            }
            else if (map.Rotation < 0 && RotationKeys.Contains(key))
            {
                map.Rotation = i;
            }
            else if (map.Side < 0 && SideKeys.Contains(key))
            {
                map.Side = i;
            }
            else if (map.Package < 0 && PackageKeys.Contains(key))
            {
                map.Package = i;
            }
            else if (map.Unit < 0 && UnitKeys.Contains(key))
            {
                map.Unit = i;
            }
        }

        return map;
    }

    private static (string Key, double UnitScale, bool HasUnitHint) NormalizeHeaderField(string field)
    {
        var compact = new StringBuilder();
        foreach (var ch in field.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
                compact.Append(ch);
        }

        var key = compact.ToString();
        if (key.EndsWith("mm", StringComparison.Ordinal))
            return (key[..^2], 1.0, true);
        if (key.EndsWith("mils", StringComparison.Ordinal))
            return (key[..^4], MilToMm, true);
        if (key.EndsWith("mil", StringComparison.Ordinal))
            return (key[..^3], MilToMm, true);

        return (key, 1.0, false);
    }

    private static bool TryParseLength(string rawText, double defaultScale, out double valueMm)
    {
        valueMm = 0;
        var text = rawText.Trim();
        var scale = defaultScale;
        if (text.EndsWith("mm", StringComparison.OrdinalIgnoreCase))
        {
            text = text[..^2].Trim();
            scale = 1.0;
        }
        else if (text.EndsWith("mil", StringComparison.OrdinalIgnoreCase))
        {
            text = text[..^3].Trim();
            scale = MilToMm;
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return false;

        valueMm = value * scale;
        return true;
    }

    private static bool LooksLikeMilUnitHint(string line)
        => line.Contains("unit", StringComparison.OrdinalIgnoreCase) &&
           line.Contains("mil", StringComparison.OrdinalIgnoreCase) &&
           !line.Contains("mm", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeSide(string rawSide)
    {
        var side = rawSide.Trim();
        if (side.Length == 0)
            return "Top";

        if (side.Equals("b", StringComparison.OrdinalIgnoreCase) ||
            side.Contains("bot", StringComparison.OrdinalIgnoreCase))
        {
            return "Bottom";
        }

        return "Top";
    }

    private static char DetectDelimiter(string headerLine)
    {
        var commas = CountOutsideQuotes(headerLine, ',');
        var semicolons = CountOutsideQuotes(headerLine, ';');
        var tabs = CountOutsideQuotes(headerLine, '\t');
        if (tabs > commas && tabs > semicolons)
            return '\t';
        if (semicolons > commas)
            return ';';
        return ',';
    }

    private static int CountOutsideQuotes(string line, char target)
    {
        var count = 0;
        var inQuotes = false;
        foreach (var ch in line)
        {
            if (ch == '"')
                inQuotes = !inQuotes;
            else if (ch == target && !inQuotes)
                count++;
        }

        return count;
    }

    private static List<string> SplitDelimited(string line, char delimiter)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
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
            else if (ch == delimiter && !inQuotes)
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }

        fields.Add(current.ToString());
        return fields;
    }

    private static string GetField(IReadOnlyList<string> fields, int index)
        => index >= 0 && index < fields.Count ? fields[index] : string.Empty;

    private sealed class ColumnMap
    {
        public int RefDes { get; set; } = -1;
        public int X { get; set; } = -1;
        public int Y { get; set; } = -1;
        public int Rotation { get; set; } = -1;
        public int Side { get; set; } = -1;
        public int Package { get; set; } = -1;
        public int Unit { get; set; } = -1;
        public double XHeaderScale { get; set; } = 1.0;
        public double YHeaderScale { get; set; } = 1.0;
        public bool XHasUnitHint { get; set; }
        public bool YHasUnitHint { get; set; }

        public bool IsValid => RefDes >= 0 && X >= 0 && Y >= 0;
    }
}

/// <summary>
/// One placement row parsed from a pick-and-place centroid file. Coordinates are millimeters in CAD
/// board space (origin bottom-left, Y grows upward). Side is normalized to "Top" or "Bottom".
/// </summary>
public sealed record CentroidRecord(
    string RefDes,
    double XMm,
    double YMm,
    double RotationDeg,
    string Side,
    string Package);

/// <summary>Parsed centroid rows plus per-row warnings for skipped/malformed lines.</summary>
public sealed record CentroidCsvParseResult(
    IReadOnlyList<CentroidRecord> Records,
    IReadOnlyList<string> Warnings);

/// <summary>Options controlling centroid-to-ROI generation.</summary>
public sealed record CentroidRoiImportOptions
{
    /// <summary>Board side to import ("Top" or "Bottom").</summary>
    public string Side { get; init; } = "Top";

    /// <summary>Extra margin, as a percent of the placement extents, added on each side before auto-fit.</summary>
    public double BoardMarginPercent { get; init; } = 5.0;

    /// <summary>Square ROI edge length in image pixels when no package size hint applies.</summary>
    public double DefaultRoiSizePixels { get; init; } = 48.0;
}
