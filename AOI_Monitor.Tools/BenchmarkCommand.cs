using System.Globalization;
using AOI_Monitor.Models;
using AOI_Monitor.Services;

namespace AOI_Monitor.Tools;

/// <summary>
/// Headless driver for the inspection performance benchmark (the same service the
/// Export &amp; Trace &gt; Performance Benchmark screen runs), for repeatable ACC-11-02
/// evidence. Thin CLI shell only: parsing, console progress, exit codes
/// (0 = PASS, 1 = FAIL or SIMULATION_ONLY, 2 = usage error).
/// </summary>
public static class BenchmarkCommand
{
    private static readonly HashSet<string> ValueOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "images",
        "golden",
        "output",
        "count",
        "warmup",
        "priority",
        "threshold-ms",
    };

    /// <summary>Executes the benchmark command; see class summary for the exit-code contract.</summary>
    public static int Execute(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length == 0 || !string.Equals(args[0], "benchmark", StringComparison.OrdinalIgnoreCase))
        {
            error.WriteLine("FAIL benchmark command was not selected.");
            WriteUsage(error);
            return 2;
        }

        var parse = Parse(args.Skip(1).ToArray());
        if (!parse.Success)
        {
            error.WriteLine($"FAIL {parse.Message}");
            WriteUsage(error);
            return 2;
        }

        try
        {
            output.WriteLine("AOI Monitor inspection performance benchmark (headless).");
            output.WriteLine("Uploaded-image pipeline evidence: image-folder benchmarks never satisfy Stage 2+ real-camera readiness gates.");
            var progress = new ConsoleProgress(output);
            var result = BenchmarkInspectionService.Run(parse.Options, progress);

            output.WriteLine($"{result.Status} Benchmark run {result.RunId} completed.");
            output.WriteLine(FormattableString.Invariant(
                $"Engine: {result.EngineName} / {result.EngineVersion}; provider: {result.ExecutionProvider}; priority: {result.DetectionPriority}."));
            output.WriteLine(FormattableString.Invariant(
                $"Measured: {result.CompletedCount}; cold-start: {result.ColdStartSampleCount} (max {result.ColdStartMaxFrameToOverlayMs:F1} ms, over threshold {result.ColdStartOverThresholdCount})."));
            output.WriteLine(FormattableString.Invariant(
                $"Frame-to-overlay p50/p90/p95/p99/max: {result.P50FrameToOverlayMs:F1}/{result.P90FrameToOverlayMs:F1}/{result.P95FrameToOverlayMs:F1}/{result.P99FrameToOverlayMs:F1}/{result.MaxFrameToOverlayMs:F1} ms; over {result.AcceptanceThresholdMs:F0} ms: {result.OverOneSecondCount}."));
            output.WriteLine(FormattableString.Invariant(
                $"Stage p95 load/preprocess/inference/overlay/persist: {result.P95LoadMs:F1}/{result.P95PreprocessingMs:F1}/{result.P95InferenceMs:F1}/{result.P95OverlayMs:F1}/{result.P95PersistenceMs:F1} ms."));
            foreach (var message in result.Messages)
                output.WriteLine($"NOTE {message}");
            output.WriteLine($"HTML report: {result.HtmlPath}");
            output.WriteLine($"JSON report: {result.JsonPath}");
            output.WriteLine($"CSV results: {result.CsvPath}");
            return string.Equals(result.Status, "PASS", StringComparison.Ordinal) ? 0 : 1;
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException or ArgumentException)
        {
            error.WriteLine($"FAIL {ex.Message}");
            WriteUsage(error);
            return 2;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            error.WriteLine($"FAIL Benchmark run failed: {ex.Message}");
            return 1;
        }
    }

    private static ParseResult Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            if (!key.StartsWith("--", StringComparison.Ordinal))
                return ParseResult.Fail($"Unexpected argument: {key}");
            var name = key[2..];
            if (!ValueOptions.Contains(name))
                return ParseResult.Fail($"Unknown option: {key}");
            if (values.ContainsKey(name))
                return ParseResult.Fail($"Duplicate option: {key}");
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                return ParseResult.Fail($"Missing value for {key}.");

            values[name] = args[++i];
        }

        foreach (var name in new[] { "images", "output" })
        {
            if (!values.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
                return ParseResult.Fail($"Missing required option --{name}.");
        }

        var count = 25;
        if (values.TryGetValue("count", out var countText) &&
            (!int.TryParse(countText, NumberStyles.Integer, CultureInfo.InvariantCulture, out count) || count <= 0))
        {
            return ParseResult.Fail($"Invalid --count value: {countText}");
        }

        var warmup = 1;
        if (values.TryGetValue("warmup", out var warmupText) &&
            (!int.TryParse(warmupText, NumberStyles.Integer, CultureInfo.InvariantCulture, out warmup) || warmup < 0 || warmup > 3))
        {
            return ParseResult.Fail($"Invalid --warmup value (0-3): {warmupText}");
        }

        var thresholdMs = 1000d;
        if (values.TryGetValue("threshold-ms", out var thresholdText) &&
            (!double.TryParse(thresholdText, NumberStyles.Float, CultureInfo.InvariantCulture, out thresholdMs) || thresholdMs <= 0 || thresholdMs > 1000))
        {
            return ParseResult.Fail($"Invalid --threshold-ms value: {thresholdText}. The acceptance criterion is 1000 ms; only equal or stricter (lower) thresholds are allowed.");
        }

        var priority = DetectionPriority.Balanced;
        if (values.TryGetValue("priority", out var priorityText) && !DetectionPriorityOption.TryParse(priorityText, out priority))
            return ParseResult.Fail(DetectionPriorityOption.FailureMessage(priorityText));

        // The inspection engines require absolute paths (image URIs); resolve here so
        // relative CLI arguments behave the same as the UI's absolute-path pickers.
        return new ParseResult(true, string.Empty, new BenchmarkInspectionOptions
        {
            SourceKind = BenchmarkInspectionSourceKind.ImageFolder,
            ImageFolder = Path.GetFullPath(values["images"]),
            GoldenImagePath = values.TryGetValue("golden", out var golden) && !string.IsNullOrWhiteSpace(golden)
                ? Path.GetFullPath(golden)
                : string.Empty,
            OutputRoot = Path.GetFullPath(values["output"]),
            RunCount = count,
            WarmupCount = warmup,
            AcceptanceThresholdMs = thresholdMs,
            DetectionPriority = priority,
        });
    }

    private static void WriteUsage(TextWriter writer)
    {
        writer.WriteLine("Usage:");
        writer.WriteLine("  AOI_Monitor.Tools benchmark --images <folder> --output <folder>");
        writer.WriteLine("      [--golden <image>] [--count <n>] [--warmup <0-3>] [--threshold-ms <n>]");
        writer.WriteLine("      [--priority balanced|minimize-false-positives|maximize-defect-recall]");
        writer.WriteLine("Supply --golden to benchmark the operator golden-compare workload; without it the");
        writer.WriteLine("default engine measures the lighter no-reference path (the report says which ran).");
    }

    private sealed class ConsoleProgress : IProgress<string>
    {
        private readonly TextWriter _output;

        public ConsoleProgress(TextWriter output)
            => _output = output;

        public void Report(string value)
            => _output.WriteLine(value);
    }

    private sealed record ParseResult(bool Success, string Message, BenchmarkInspectionOptions Options)
    {
        public static ParseResult Fail(string message)
            => new(false, message, new BenchmarkInspectionOptions());
    }
}
