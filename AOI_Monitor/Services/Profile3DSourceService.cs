using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using System.Windows;

namespace AOI_Monitor.Services;

public interface IProfile3DSource
{
    string Name { get; }
    string Status { get; }
    string StatusMessage { get; }
    bool IsAcquiring { get; }
    void StartAcquisition();
    void StopAcquisition();
    Profile3DFrame? GetNextHeightMap(CancellationToken cancellationToken = default);
    IReadOnlyDictionary<string, string> GetDiagnostics();
}

public sealed class NullProfile3DSource : IProfile3DSource
{
    public string Name => "No 3D Profile Source";
    public string Status => "NotConnected";
    public string StatusMessage => "No 3D profile source is configured.";
    public bool IsAcquiring => false;

    public void StartAcquisition()
    {
    }

    public void StopAcquisition()
    {
    }

    public Profile3DFrame? GetNextHeightMap(CancellationToken cancellationToken = default) => null;

    public IReadOnlyDictionary<string, string> GetDiagnostics()
        => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["source"] = "none",
            ["sourceKind"] = "NotConnected",
            ["status"] = Status,
            ["message"] = StatusMessage,
        };
}

public sealed class CsvProfile3DSource : IProfile3DSource
{
    private readonly string _csvPath;
    private bool _started;

    public CsvProfile3DSource(string csvPath)
    {
        _csvPath = csvPath;
    }

    public string Name => "Sample CSV 3D Profile Source";
    public string Status => _started ? "Ready" : "Stopped";
    public string StatusMessage => string.IsNullOrWhiteSpace(_csvPath) ? "No CSV height map is selected." : $"CSV height map: {Path.GetFileName(_csvPath)}";
    public bool IsAcquiring => _started;

    public void StartAcquisition()
    {
        if (string.IsNullOrWhiteSpace(_csvPath) || !File.Exists(_csvPath))
            throw new FileNotFoundException("Sample 3D height-map CSV was not found.", _csvPath);

        _started = true;
    }

    public void StopAcquisition() => _started = false;

    public Profile3DFrame? GetNextHeightMap(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_started)
            StartAcquisition();

        var points = ParseHeightMap(_csvPath);
        if (points.Count == 0)
            return null;

        var minX = points.Min(point => point.X);
        var maxX = points.Max(point => point.X);
        var minY = points.Min(point => point.Y);
        var maxY = points.Max(point => point.Y);
        var width = maxX - minX + 1;
        var height = maxY - minY + 1;
        var values = Enumerable.Repeat(double.NaN, width * height).ToArray();
        foreach (var point in points)
        {
            var x = point.X - minX;
            var y = point.Y - minY;
            values[y * width + x] = point.Height;
        }

        return new Profile3DFrame
        {
            FrameId = $"CSV-{Path.GetFileNameWithoutExtension(_csvPath)}-{DateTime.UtcNow:yyyyMMddHHmmssfff}",
            SourceKind = "CSV Sample",
            IsSimulated = true,
            CapturedAtUtc = DateTime.UtcNow,
            Width = width,
            Height = height,
            Unit = "microns",
            XPitchMicrons = 1,
            YPitchMicrons = 1,
            HeightValues = values,
            ViewType = "Top",
        };
    }

    public IReadOnlyDictionary<string, string> GetDiagnostics()
        => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["source"] = "csv",
            ["sourceKind"] = "CSV Sample",
            ["path"] = _csvPath,
            ["status"] = Status,
            ["message"] = StatusMessage,
        };

    public static List<(int X, int Y, double Height)> ParseHeightMap(string path)
    {
        var rows = new List<(int X, int Y, double Height)>();
        var lines = File.ReadAllLines(path);
        if (lines.Length == 0)
            return rows;

        var start = 0;
        var header = SplitCsvLine(lines[0]).Select(h => h.Trim().ToLowerInvariant()).ToArray();
        var hasHeader = header.Contains("x") && header.Contains("y") && header.Contains("height");
        var xIndex = hasHeader ? Array.IndexOf(header, "x") : 0;
        var yIndex = hasHeader ? Array.IndexOf(header, "y") : 1;
        var heightIndex = hasHeader ? Array.IndexOf(header, "height") : 2;
        if (hasHeader)
            start = 1;

        foreach (var line in lines.Skip(start))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var cells = SplitCsvLine(line);
            if (cells.Count <= Math.Max(heightIndex, Math.Max(xIndex, yIndex)))
                continue;

            if (int.TryParse(cells[xIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) &&
                int.TryParse(cells[yIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) &&
                double.TryParse(cells[heightIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out var height))
            {
                rows.Add((x, y, height));
            }
        }

        return rows;
    }

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
}

public sealed class GenericProfile3DAdapter : IProfile3DSource
{
    public string Name => "Generic 3D Adapter Boundary";
    public string Status => "NotConnected";
    public string StatusMessage => "Generic 3D adapter boundary is ready for a vendor SDK implementation, but no real hardware adapter is configured.";
    public bool IsAcquiring => false;

    public void StartAcquisition() => throw new NotSupportedException(StatusMessage);
    public void StopAcquisition()
    {
    }

    public Profile3DFrame? GetNextHeightMap(CancellationToken cancellationToken = default) => null;

    public IReadOnlyDictionary<string, string> GetDiagnostics()
        => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["source"] = "generic-adapter-boundary",
            ["sourceKind"] = "NotConnected",
            ["status"] = Status,
            ["message"] = StatusMessage,
            ["productionClaim"] = "No real 3D camera movement or acquisition is validated by this boundary.",
        };
}

public static class Profile3DAcceptanceTestService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static Profile3DAcceptanceRun Run(
        IProfile3DSource source,
        Profile3DAcceptanceCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        criteria ??= new Profile3DAcceptanceCriteria();
        var run = new Profile3DAcceptanceRun
        {
            CreatedAtUtc = DateTime.UtcNow,
            SourceName = source.Name,
            SourceKind = ClassifySourceKind(source),
            Criteria = criteria,
            Diagnostics = source.GetDiagnostics().ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
        };

        var stopwatch = Stopwatch.StartNew();
        try
        {
            source.StartAcquisition();
            var frame = source.GetNextHeightMap(cancellationToken);
            stopwatch.Stop();
            run.AcquisitionMs = stopwatch.Elapsed.TotalMilliseconds;
            if (frame is null)
            {
                run.Failures.Add($"No height map was returned. Source status={source.Status}; {source.StatusMessage}");
            }
            else
            {
                PopulateFromFrame(run, frame);
                ValidateFrame(run, frame, criteria);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException or FileNotFoundException)
        {
            stopwatch.Stop();
            run.AcquisitionMs = stopwatch.Elapsed.TotalMilliseconds;
            run.Failures.Add(ex.Message);
        }
        finally
        {
            try
            {
                source.StopAcquisition();
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                run.Warnings.Add($"Stop acquisition warning: {ex.Message}");
            }
        }

        if (run.AcquisitionMs > criteria.MaxAcquisitionMs)
            run.Warnings.Add($"Acquisition time {run.AcquisitionMs:F1} ms exceeds target {criteria.MaxAcquisitionMs:F1} ms.");

        if (run.IsSimulated)
            run.Warnings.Add("3D profile acceptance was run against sample or simulated data. This is not real 3D camera validation.");

        run.Status = run.Failures.Count > 0 ? "FAIL" : run.Warnings.Count > 0 ? "WARN" : "PASS";
        run.FactoryReadinessStatus = !run.IsSimulated && run.Status is "PASS" or "WARN" ? run.Status : "NOT VALIDATED";
        return run;
    }

    public static long RunAndPersist(
        IProfile3DSource source,
        Profile3DAcceptanceCriteria? criteria = null,
        string? operatorId = null,
        CancellationToken cancellationToken = default)
    {
        var run = Run(source, criteria, cancellationToken);
        return AoiDatabase.RecordProfile3DAcceptanceRun(run, operatorId);
    }

    public static DefectResult EvaluateHeightRoi(Profile3DFrame frame, RecipeRoi roi)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(roi);

        var samples = ExtractRoiSamples(frame, roi).ToArray();
        var validSamples = samples.Where(value => !double.IsNaN(value) && !double.IsInfinity(value)).ToArray();
        var maxHeight = validSamples.Length == 0 ? double.NaN : validSamples.Max();
        var minHeight = validSamples.Length == 0 ? double.NaN : validSamples.Min();
        var baseline = validSamples.Length == 0 ? 0 : validSamples.Average();
        var volume = validSamples.Sum(value => Math.Max(0, value - baseline) * Math.Max(1, frame.XPitchMicrons) * Math.Max(1, frame.YPitchMicrons));
        var threshold = roi.Thresholds;
        var heightLimitEnabled = threshold.HeightMin != 0 || threshold.HeightMax != 0;
        var volumeLimitEnabled = threshold.VolumeMin != 0 || threshold.VolumeMax != 0;
        var heightNg =
            validSamples.Length == 0 ||
            (threshold.HeightMin != 0 && minHeight < threshold.HeightMin) ||
            (threshold.HeightMax != 0 && maxHeight > threshold.HeightMax);
        var volumeNg =
            volumeLimitEnabled &&
            ((threshold.VolumeMin != 0 && volume < threshold.VolumeMin) ||
             (threshold.VolumeMax != 0 && volume > threshold.VolumeMax));
        var judgment = heightNg || volumeNg ? "NG" : heightLimitEnabled || volumeLimitEnabled ? "OK" : "REVIEW";

        return new DefectResult
        {
            DefectType = $"{roi.RoiType} Height Profile",
            Confidence = judgment == "NG" ? 0.95 : judgment == "OK" ? 0.9 : 0.55,
            BoundingBox = new Rect(roi.X, roi.Y, roi.Width, roi.Height),
            XPosition = roi.X + roi.Width / 2.0,
            YPosition = roi.Y + roi.Height / 2.0,
            WidthPixels = roi.Width * frame.Width,
            HeightPixels = roi.Height * frame.Height,
            AreaPixels = roi.Width * frame.Width * roi.Height * frame.Height,
            HeightMicrons = double.IsNaN(maxHeight) ? null : maxHeight,
            VolumeEstimate = volume,
            SourceRoiId = roi.RoiId,
            RoiId = roi.RoiId,
            RoiName = roi.DisplayName,
            RoiType = roi.RoiType,
            SideOrViewType = frame.ViewType,
            JudgmentStatus = judgment,
            Severity = judgment == "NG" ? "High" : "Review",
        };
    }

    public static string ExportReport(Profile3DAcceptanceRun run, string outputFolder)
    {
        Directory.CreateDirectory(outputFolder);
        var stem = $"profile_3d_acceptance_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
        var jsonPath = Path.Combine(outputFolder, $"{stem}.json");
        var htmlPath = Path.Combine(outputFolder, $"{stem}.html");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(run, JsonOptions), Encoding.UTF8);
        File.WriteAllText(htmlPath, BuildHtml(run), Encoding.UTF8);
        return htmlPath;
    }

    private static void PopulateFromFrame(Profile3DAcceptanceRun run, Profile3DFrame frame)
    {
        run.SourceKind = frame.SourceKind;
        run.IsSimulated = frame.IsSimulated;
        run.Width = frame.Width;
        run.Height = frame.Height;
        run.Unit = frame.Unit;
        run.XPitchMicrons = frame.XPitchMicrons;
        run.YPitchMicrons = frame.YPitchMicrons;
        run.FrameId = frame.FrameId;
        run.MissingHeightCount = Math.Max(0, frame.Width * frame.Height - frame.HeightValues.Length);
        run.NaNHeightCount = frame.HeightValues.Count(value => double.IsNaN(value) || double.IsInfinity(value));
    }

    private static string ClassifySourceKind(IProfile3DSource source)
    {
        var diagnostics = source.GetDiagnostics();
        if (diagnostics.TryGetValue("sourceKind", out var sourceKind) && !string.IsNullOrWhiteSpace(sourceKind))
            return sourceKind;

        if (source.Status.Equals("NotConnected", StringComparison.OrdinalIgnoreCase))
            return "NotConnected";

        return source.Name.Contains("CSV", StringComparison.OrdinalIgnoreCase)
            ? "CSV Sample"
            : source.Name.Contains("Simulated", StringComparison.OrdinalIgnoreCase)
                ? "Simulation"
                : "Real Hardware";
    }

    private static void ValidateFrame(Profile3DAcceptanceRun run, Profile3DFrame frame, Profile3DAcceptanceCriteria criteria)
    {
        if (string.IsNullOrWhiteSpace(frame.FrameId))
            run.Failures.Add("FrameId is missing.");
        if (frame.Width < criteria.MinimumWidth || frame.Height < criteria.MinimumHeight)
            run.Failures.Add($"Height map dimensions {frame.Width}x{frame.Height} are below {criteria.MinimumWidth}x{criteria.MinimumHeight}.");
        if (frame.HeightValues.Length != frame.Width * frame.Height)
            run.Failures.Add($"Height value count {frame.HeightValues.Length} does not match dimensions {frame.Width}x{frame.Height}.");
        if (run.NaNHeightCount > 0)
            run.Failures.Add($"Height map contains {run.NaNHeightCount} NaN or infinite height value(s).");
        if (!criteria.AcceptedUnits.Any(unit => string.Equals(unit, frame.Unit, StringComparison.OrdinalIgnoreCase)))
            run.Failures.Add($"Height unit '{frame.Unit}' is not accepted.");
        if (criteria.RequirePositivePitch && (frame.XPitchMicrons <= 0 || frame.YPitchMicrons <= 0))
            run.Failures.Add("X/Y pitch must be positive microns.");
        if (string.IsNullOrWhiteSpace(frame.ViewType))
            run.Warnings.Add("ViewType is missing.");
    }

    private static IEnumerable<double> ExtractRoiSamples(Profile3DFrame frame, RecipeRoi roi)
    {
        var left = Math.Clamp((int)Math.Floor(roi.X * frame.Width), 0, Math.Max(0, frame.Width - 1));
        var top = Math.Clamp((int)Math.Floor(roi.Y * frame.Height), 0, Math.Max(0, frame.Height - 1));
        var right = Math.Clamp((int)Math.Ceiling((roi.X + roi.Width) * frame.Width), left + 1, frame.Width);
        var bottom = Math.Clamp((int)Math.Ceiling((roi.Y + roi.Height) * frame.Height), top + 1, frame.Height);
        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                var index = y * frame.Width + x;
                if (index >= 0 && index < frame.HeightValues.Length)
                    yield return frame.HeightValues[index];
            }
        }
    }

    private static string BuildHtml(Profile3DAcceptanceRun run)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html><html><head><meta charset=\"utf-8\"><title>3D Profile Acceptance</title>");
        sb.AppendLine("<style>body{font-family:Segoe UI,Arial,sans-serif;margin:24px;color:#17212b}table{border-collapse:collapse}td,th{border:1px solid #ccd5dd;padding:6px 8px}.bad{color:#a62929}.warn{color:#9b6500}</style></head><body>");
        sb.AppendLine("<h1>3D Profile Acceptance Report</h1>");
        sb.AppendLine($"<p><b>Status:</b> {Escape(run.Status)} &nbsp; <b>Factory readiness:</b> {Escape(run.FactoryReadinessStatus)} &nbsp; <b>Source:</b> {Escape(run.SourceName)} ({Escape(run.SourceKind)})</p>");
        if (run.IsSimulated)
            sb.AppendLine("<p class=\"warn\"><b>Limitation:</b> Simulation/sample CSV evidence only; no real 3D camera hardware was validated.</p>");
        sb.AppendLine("<table><tr><th>Metric</th><th>Value</th></tr>");
        sb.AppendLine($"<tr><td>Frame</td><td>{Escape(run.FrameId)}</td></tr>");
        sb.AppendLine($"<tr><td>Dimensions</td><td>{run.Width} x {run.Height}</td></tr>");
        sb.AppendLine($"<tr><td>Unit</td><td>{Escape(run.Unit)}</td></tr>");
        sb.AppendLine($"<tr><td>Pitch</td><td>{run.XPitchMicrons:F3} x {run.YPitchMicrons:F3} microns</td></tr>");
        sb.AppendLine($"<tr><td>Acquisition</td><td>{run.AcquisitionMs:F1} ms</td></tr>");
        sb.AppendLine($"<tr><td>Invalid heights</td><td>{run.NaNHeightCount}</td></tr>");
        sb.AppendLine("</table>");
        AppendList(sb, "Failures", run.Failures, "bad");
        AppendList(sb, "Warnings", run.Warnings, "warn");
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    private static void AppendList(StringBuilder sb, string title, IReadOnlyCollection<string> items, string cssClass)
    {
        if (items.Count == 0)
            return;

        sb.AppendLine($"<h2>{Escape(title)}</h2><ul class=\"{cssClass}\">");
        foreach (var item in items)
            sb.AppendLine($"<li>{Escape(item)}</li>");
        sb.AppendLine("</ul>");
    }

    private static string Escape(string? value) => System.Net.WebUtility.HtmlEncode(value ?? string.Empty);
}
