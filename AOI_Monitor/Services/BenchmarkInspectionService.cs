using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using AOI_Monitor.Data;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public static class BenchmarkInspectionService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff",
    };

    public static string BenchmarkRoot => Path.Combine(AoiDatabase.StorageRoot, "exports", "performance_benchmarks");
    public static string LatestSummaryPath => Path.Combine(BenchmarkRoot, "latest_benchmark_summary.json");

    public static BenchmarkInspectionResult Run(
        BenchmarkInspectionOptions options,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default,
        IInspectionEngine? engineOverride = null,
        ICameraSource? cameraSourceOverride = null)
    {
        AoiDatabase.Initialize();
        Normalize(options);

        var engine = engineOverride ?? InspectionEngineFactory.Create();
        var result = new BenchmarkInspectionResult
        {
            RunId = $"BENCH-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..6]}",
            StartedAtUtc = DateTime.UtcNow,
            SourceKind = options.SourceKind,
            SourceDescription = BuildSourceDescription(options),
            EngineName = engine.Name,
            EngineVersion = engine.Version,
            ModelId = InspectionModelConfigurationService.Load().ActiveModelId,
            AcceptanceThresholdMs = options.AcceptanceThresholdMs,
            RequestedCount = options.RunCount,
        };

        try
        {
            if (options.SourceKind == BenchmarkInspectionSourceKind.ImageFolder)
                RunImageFolder(options, engine, result, progress, cancellationToken);
            else
                RunCameraSource(options, engine, result, cameraSourceOverride, progress, cancellationToken);
        }
        finally
        {
            result.EndedAtUtc = DateTime.UtcNow;
            result.DurationSeconds = Math.Max(0.001, (result.EndedAtUtc - result.StartedAtUtc).TotalSeconds);
            CompleteSummary(result);
            WriteReports(result, options.OutputRoot);
        }

        AoiDatabase.RecordAuditEvent(
            "PERFORMANCE_BENCHMARK",
            $"Inspection benchmark {result.Status}: source={result.SourceKind}; count={result.CompletedCount}; p95={result.P95FrameToOverlayMs:F1} ms; realCamera={result.IsRealCameraSource}.",
            operatorWithRole: AoiDatabase.AuditOperatorProvider?.Invoke() ?? "UNKNOWN",
            relatedPath: result.ReportFolder);
        return result;
    }

    public static BenchmarkInspectionResult? GetLatestBenchmark()
    {
        try
        {
            if (!File.Exists(LatestSummaryPath))
                return null;

            return JsonSerializer.Deserialize<BenchmarkInspectionResult>(File.ReadAllText(LatestSummaryPath), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static bool LatestSatisfiesRealCameraRequirement(double thresholdMs, out BenchmarkInspectionResult? latest)
    {
        latest = GetLatestBenchmark();
        return latest is not null &&
            latest.IsRealCameraSource &&
            latest.CompletedCount > 0 &&
            latest.P95FrameToOverlayMs <= thresholdMs &&
            latest.OverOneSecondCount == 0;
    }

    public static double Percentile(IEnumerable<double> values, double percentile)
    {
        var sorted = values.Where(value => value >= 0).OrderBy(value => value).ToArray();
        if (sorted.Length == 0)
            return 0;
        if (sorted.Length == 1)
            return sorted[0];

        var position = (sorted.Length - 1) * Math.Clamp(percentile, 0, 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper)
            return sorted[lower];

        var fraction = position - lower;
        return sorted[lower] + (sorted[upper] - sorted[lower]) * fraction;
    }

    private static void RunImageFolder(
        BenchmarkInspectionOptions options,
        IInspectionEngine engine,
        BenchmarkInspectionResult result,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var images = Directory.EnumerateFiles(options.ImageFolder, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path => ImageExtensions.Contains(Path.GetExtension(path)))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (images.Length == 0)
            throw new InvalidOperationException("Benchmark image folder contains no supported image files.");

        var deadline = options.Duration is { } duration ? DateTime.UtcNow.Add(duration) : (DateTime?)null;
        for (var i = 0; ShouldContinue(i, options.RunCount, deadline); i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = images[i % images.Length];
            progress?.Report($"Benchmarking image {i + 1}: {Path.GetFileName(path)}");
            result.Samples.Add(RunOneImage(engine, path, null, options, i + 1, "BenchmarkImageFolder", false, string.Empty, DateTime.UtcNow));
        }
    }

    private static void RunCameraSource(
        BenchmarkInspectionOptions options,
        IInspectionEngine engine,
        BenchmarkInspectionResult result,
        ICameraSource? sourceOverride,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var source = sourceOverride ?? CreateCameraSource(options);
        source.StartAcquisition();
        try
        {
            var deadline = options.Duration is { } duration ? DateTime.UtcNow.Add(duration) : (DateTime?)null;
            for (var i = 0; ShouldContinue(i, options.RunCount, deadline); i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report($"Benchmarking camera frame {i + 1} from {source.Name}...");
                var frame = source.GetNextFrame();
                if (frame is null || string.IsNullOrWhiteSpace(frame.ImagePath) || !File.Exists(frame.ImagePath))
                {
                    result.Messages.Add($"Frame {i + 1}: camera source did not provide a readable image.");
                    continue;
                }

                result.Samples.Add(RunOneImage(
                    engine,
                    frame.ImagePath,
                    null,
                    options,
                    i + 1,
                    frame.SourceKind,
                    frame.IsSimulated,
                    frame.FrameId,
                    frame.EffectiveCapturedAtUtc));
            }
        }
        finally
        {
            source.StopAcquisition();
        }
    }

    private static BenchmarkInspectionSample RunOneImage(
        IInspectionEngine engine,
        string imagePath,
        string? goldenPath,
        BenchmarkInspectionOptions options,
        int sequence,
        string sourceKind,
        bool isSimulated,
        string frameId,
        DateTime frameCapturedAtUtc)
    {
        var metadata = ReadImageMetadata(imagePath);
        var frameStart = frameCapturedAtUtc.ToUniversalTime();
        var frameReceived = DateTime.UtcNow;
        var analysisStart = DateTime.UtcNow;
        var analysis = engine.Analyze(imagePath, goldenPath, options.DetectionPriority);
        analysis.SourceKind = sourceKind;
        analysis.SourceFrameId = string.IsNullOrWhiteSpace(frameId) ? Path.GetFileName(imagePath) : frameId;
        analysis.IsSimulatedSource = isSimulated;
        var analysisEnd = DateTime.UtcNow;

        var loadStart = frameReceived;
        var preprocessStart = loadStart.AddMilliseconds(Math.Max(0, analysis.Timing.ImageLoadMilliseconds));
        var inferenceStart = preprocessStart.AddMilliseconds(Math.Max(0, analysis.Timing.PreprocessingMilliseconds));
        var overlayStart = inferenceStart.AddMilliseconds(Math.Max(0, analysis.Timing.InferenceMilliseconds));
        var overlayEnd = overlayStart.AddMilliseconds(Math.Max(0, analysis.Timing.OverlayRenderingMilliseconds));
        if (overlayEnd < analysisEnd)
            overlayEnd = analysisEnd;
        var persistWatch = Stopwatch.StartNew();
        var trace = new InspectionLatencyTrace
        {
            TraceId = Guid.NewGuid().ToString("N"),
            CreatedAtUtc = frameReceived,
            FrameCapturedAtUtc = frameStart,
            FrameReceivedAtUtc = frameReceived,
            PreprocessingStartUtc = preprocessStart,
            PreprocessingEndUtc = inferenceStart,
            InferenceStartUtc = inferenceStart,
            InferenceEndUtc = overlayStart,
            PostprocessStartUtc = overlayStart,
            PostprocessEndUtc = overlayStart,
            OverlayRenderStartUtc = overlayStart,
            OverlayRenderEndUtc = overlayEnd,
            SourceKind = sourceKind,
            Engine = engine.Name,
            ModelId = engine.Version,
            ImageWidth = metadata.Width,
            ImageHeight = metadata.Height,
            Verdict = analysis.Verdict,
        };
        trace.ResultPersistStartUtc = DateTime.UtcNow;
        trace.ResultPersistEndUtc = trace.ResultPersistStartUtc.Value.AddMilliseconds(0.01);
        persistWatch.Stop();
        trace.ResultPersistEndUtc = trace.ResultPersistStartUtc.Value.AddMilliseconds(Math.Max(0.01, persistWatch.Elapsed.TotalMilliseconds));
        var persisted = InspectionLatencyService.Persist(trace);

        return new BenchmarkInspectionSample
        {
            Sequence = sequence,
            SourcePath = imagePath,
            FrameId = string.IsNullOrWhiteSpace(frameId) ? Path.GetFileName(imagePath) : frameId,
            IsSimulated = isSimulated,
            SourceKind = sourceKind,
            Verdict = analysis.Verdict,
            LoadMs = analysis.Timing.ImageLoadMilliseconds,
            PreprocessingMs = analysis.Timing.PreprocessingMilliseconds,
            InferenceMs = analysis.Timing.InferenceMilliseconds,
            OverlayMs = analysis.Timing.OverlayRenderingMilliseconds,
            PersistenceMs = Duration(persisted.ResultPersistStartUtc, persisted.ResultPersistEndUtc),
            FrameToOverlayMs = persisted.TotalFrameToOverlayMs,
            FrameToSavedResultMs = persisted.TotalFrameToSavedResultMs,
            TraceId = persisted.TraceId,
            Warnings = persisted.Warnings.ToList(),
        };
    }

    private static ICameraSource CreateCameraSource(BenchmarkInspectionOptions options)
    {
        if (options.SourceKind == BenchmarkInspectionSourceKind.ActiveCameraSource)
            return CameraSourceFactory.ActiveSource;

        var folder = string.IsNullOrWhiteSpace(options.ImageFolder)
            ? CameraSourceSettingsService.Load().TopFolder
            : options.ImageFolder;
        return CameraSourceFactory.Create(new CameraSourceSettings
        {
            SourceKey = CameraSourceFactory.FolderSimulationSourceKey,
            TopFolder = folder,
            BoardModel = "BENCHMARK",
            LotId = "BENCH",
        });
    }

    private static void CompleteSummary(BenchmarkInspectionResult result)
    {
        result.CompletedCount = result.Samples.Count;
        result.IsRealCameraSource = result.SourceKind == BenchmarkInspectionSourceKind.ActiveCameraSource &&
            result.Samples.Count > 0 &&
            result.Samples.All(sample => !sample.IsSimulated) &&
            result.Samples.All(sample => !sample.SourceKind.Contains("Simulation", StringComparison.OrdinalIgnoreCase));
        result.ThroughputImagesPerMinute = result.DurationSeconds <= 0 ? 0 : result.CompletedCount / result.DurationSeconds * 60.0;
        result.P50FrameToOverlayMs = Percentile(result.Samples.Select(sample => sample.FrameToOverlayMs), 0.50);
        result.P90FrameToOverlayMs = Percentile(result.Samples.Select(sample => sample.FrameToOverlayMs), 0.90);
        result.P95FrameToOverlayMs = Percentile(result.Samples.Select(sample => sample.FrameToOverlayMs), 0.95);
        result.P99FrameToOverlayMs = Percentile(result.Samples.Select(sample => sample.FrameToOverlayMs), 0.99);
        result.MaxFrameToOverlayMs = result.Samples.Count == 0 ? 0 : result.Samples.Max(sample => sample.FrameToOverlayMs);
        result.OverOneSecondCount = result.Samples.Count(sample => sample.FrameToOverlayMs > result.AcceptanceThresholdMs);
        result.P95LoadMs = Percentile(result.Samples.Select(sample => sample.LoadMs), 0.95);
        result.P95PreprocessingMs = Percentile(result.Samples.Select(sample => sample.PreprocessingMs), 0.95);
        result.P95InferenceMs = Percentile(result.Samples.Select(sample => sample.InferenceMs), 0.95);
        result.P95OverlayMs = Percentile(result.Samples.Select(sample => sample.OverlayMs), 0.95);
        result.P95PersistenceMs = Percentile(result.Samples.Select(sample => sample.PersistenceMs), 0.95);
        if (result.SourceKind != BenchmarkInspectionSourceKind.ImageFolder && !result.IsRealCameraSource)
            result.Messages.Add("Camera benchmark source is simulated or not proven real hardware; it cannot satisfy Stage 2 or Full Factory real-camera readiness.");
        result.Status = result.CompletedCount == 0
            ? "FAIL"
            : result.OverOneSecondCount > 0
                ? "FAIL"
                : result.SourceKind == BenchmarkInspectionSourceKind.ActiveCameraSource && !result.IsRealCameraSource
                    ? "SIMULATION_ONLY"
                    : "PASS";
    }

    private static void WriteReports(BenchmarkInspectionResult result, string outputRoot)
    {
        var root = string.IsNullOrWhiteSpace(outputRoot) ? BenchmarkRoot : outputRoot.Trim();
        Directory.CreateDirectory(root);
        var folder = Path.Combine(root, $"benchmark_{result.StartedAtUtc:yyyyMMdd_HHmmss}_{result.RunId}");
        Directory.CreateDirectory(folder);
        result.ReportFolder = folder;
        result.JsonPath = Path.Combine(folder, "benchmark_report.json");
        result.HtmlPath = Path.Combine(folder, "benchmark_report.html");
        result.PdfPath = Path.Combine(folder, "benchmark_report.pdf");
        result.CsvPath = Path.Combine(folder, "benchmark_results.csv");

        File.WriteAllText(result.CsvPath, BuildCsv(result), Encoding.UTF8);
        File.WriteAllText(result.HtmlPath, BuildHtml(result), Encoding.UTF8);
        PdfExportService.ExportHtmlFileToPdf(result.HtmlPath, result.PdfPath, "Inspection Performance Benchmark");
        File.WriteAllText(result.JsonPath, JsonSerializer.Serialize(result, JsonOptions), Encoding.UTF8);
        Directory.CreateDirectory(BenchmarkRoot);
        File.WriteAllText(LatestSummaryPath, JsonSerializer.Serialize(result, JsonOptions), Encoding.UTF8);
        ExportVerificationService.RecordVerifiedExport("PerformanceBenchmark", folder, result.Status == "PASS" ? "OK" : "WARN");
    }

    private static string BuildCsv(BenchmarkInspectionResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Sequence,FrameId,SourceKind,IsSimulated,Verdict,LoadMs,PreprocessingMs,InferenceMs,OverlayMs,PersistenceMs,FrameToOverlayMs,FrameToSavedResultMs,TraceId,SourcePath");
        foreach (var sample in result.Samples)
        {
            sb.AppendLine(string.Join(
                ',',
                sample.Sequence.ToString(CultureInfo.InvariantCulture),
                Csv(sample.FrameId),
                Csv(sample.SourceKind),
                sample.IsSimulated ? "true" : "false",
                Csv(sample.Verdict),
                sample.LoadMs.ToString("F3", CultureInfo.InvariantCulture),
                sample.PreprocessingMs.ToString("F3", CultureInfo.InvariantCulture),
                sample.InferenceMs.ToString("F3", CultureInfo.InvariantCulture),
                sample.OverlayMs.ToString("F3", CultureInfo.InvariantCulture),
                sample.PersistenceMs.ToString("F3", CultureInfo.InvariantCulture),
                sample.FrameToOverlayMs.ToString("F3", CultureInfo.InvariantCulture),
                sample.FrameToSavedResultMs.ToString("F3", CultureInfo.InvariantCulture),
                Csv(sample.TraceId),
                Csv(sample.SourcePath)));
        }

        return sb.ToString();
    }

    private static string BuildHtml(BenchmarkInspectionResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html><html><head><meta charset=\"utf-8\"><title>Inspection Performance Benchmark</title>");
        sb.AppendLine("<style>body{font-family:Segoe UI,Arial,sans-serif;margin:24px;color:#18222c;background:#f7f9fb}table{border-collapse:collapse;width:100%;background:#fff}td,th{border:1px solid #d7e0e8;padding:7px;text-align:left}.PASS{color:#176b3a}.FAIL{color:#a12626}.SIMULATION_ONLY{color:#8a5a00}</style></head><body>");
        sb.AppendLine($"<h1>Inspection Performance Benchmark</h1><p><strong class=\"{Html(result.Status)}\">{Html(result.Status)}</strong> | source={Html(result.SourceKind.ToString())} | realCamera={result.IsRealCameraSource} | run={Html(result.RunId)}</p>");
        sb.AppendLine("<table><tr><th>Metric</th><th>Value</th></tr>");
        Row("Completed", result.CompletedCount.ToString(CultureInfo.InvariantCulture));
        Row("Throughput", $"{result.ThroughputImagesPerMinute:F1} images/min");
        Row("P50 frame-to-overlay", $"{result.P50FrameToOverlayMs:F1} ms");
        Row("P90 frame-to-overlay", $"{result.P90FrameToOverlayMs:F1} ms");
        Row("P95 frame-to-overlay", $"{result.P95FrameToOverlayMs:F1} ms");
        Row("P99 frame-to-overlay", $"{result.P99FrameToOverlayMs:F1} ms");
        Row("Max frame-to-overlay", $"{result.MaxFrameToOverlayMs:F1} ms");
        Row("Over threshold", result.OverOneSecondCount.ToString(CultureInfo.InvariantCulture));
        Row("P95 load/preprocess/inference/overlay/persist", $"{result.P95LoadMs:F1} / {result.P95PreprocessingMs:F1} / {result.P95InferenceMs:F1} / {result.P95OverlayMs:F1} / {result.P95PersistenceMs:F1} ms");
        sb.AppendLine("</table>");
        if (result.Messages.Count > 0)
        {
            sb.AppendLine("<h2>Messages</h2><ul>");
            foreach (var message in result.Messages)
                sb.AppendLine($"<li>{Html(message)}</li>");
            sb.AppendLine("</ul>");
        }

        sb.AppendLine("<h2>Samples</h2><table><tr><th>#</th><th>Frame</th><th>Source</th><th>Simulated</th><th>Verdict</th><th>Load</th><th>Pre</th><th>Inference</th><th>Overlay</th><th>Persist</th><th>Frame-to-overlay</th></tr>");
        foreach (var sample in result.Samples)
            sb.AppendLine($"<tr><td>{sample.Sequence}</td><td>{Html(sample.FrameId)}</td><td>{Html(sample.SourceKind)}</td><td>{sample.IsSimulated}</td><td>{Html(sample.Verdict)}</td><td>{sample.LoadMs:F1}</td><td>{sample.PreprocessingMs:F1}</td><td>{sample.InferenceMs:F1}</td><td>{sample.OverlayMs:F1}</td><td>{sample.PersistenceMs:F1}</td><td>{sample.FrameToOverlayMs:F1}</td></tr>");
        sb.AppendLine("</table></body></html>");
        return sb.ToString();

        void Row(string name, string value) => sb.AppendLine($"<tr><td>{Html(name)}</td><td>{Html(value)}</td></tr>");
    }

    private static bool ShouldContinue(int completed, int runCount, DateTime? deadline)
    {
        if (deadline is not null && DateTime.UtcNow >= deadline.Value)
            return false;
        return completed < runCount || (deadline is not null && DateTime.UtcNow < deadline.Value);
    }

    private static void Normalize(BenchmarkInspectionOptions options)
    {
        options.RunCount = Math.Clamp(options.RunCount, 1, 100000);
        options.AcceptanceThresholdMs = options.AcceptanceThresholdMs <= 0 ? 1000 : options.AcceptanceThresholdMs;
        if (options.SourceKind is BenchmarkInspectionSourceKind.ImageFolder or BenchmarkInspectionSourceKind.FolderCameraSimulation &&
            (string.IsNullOrWhiteSpace(options.ImageFolder) || !Directory.Exists(options.ImageFolder)))
        {
            throw new DirectoryNotFoundException("Benchmark image folder was not found.");
        }
    }

    private static string BuildSourceDescription(BenchmarkInspectionOptions options)
        => options.SourceKind switch
        {
            BenchmarkInspectionSourceKind.ImageFolder => options.ImageFolder,
            BenchmarkInspectionSourceKind.FolderCameraSimulation => $"Folder camera simulation: {options.ImageFolder}",
            BenchmarkInspectionSourceKind.ActiveCameraSource => $"Active camera source: {CameraSourceFactory.ActiveSource.Name}",
            _ => options.SourceKind.ToString(),
        };

    private static (int Width, int Height) ReadImageMetadata(string path)
    {
        try
        {
            var frame = BitmapFrame.Create(new Uri(path, UriKind.Absolute), BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnLoad);
            return (frame.PixelWidth, frame.PixelHeight);
        }
        catch
        {
            return (0, 0);
        }
    }

    private static double Duration(DateTime? start, DateTime? end)
        => start is { } s && end is { } e && e >= s ? (e - s).TotalMilliseconds : 0;

    private static string Csv(string value)
        => $"\"{(value ?? string.Empty).Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static string Html(string value)
        => System.Net.WebUtility.HtmlEncode(value ?? string.Empty);

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
