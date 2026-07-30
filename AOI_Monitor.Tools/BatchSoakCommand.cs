using System.Data.Common;
using System.Globalization;
using AOI_Monitor.Models;
using AOI_Monitor.Services;

namespace AOI_Monitor.Tools;

/// <summary>
/// Headless driver for the Stage 1 batch-inspection soak. Thin CLI shell only:
/// parsing, console progress, crash marker, and exit codes (0 = PASS, 1 = FAIL or
/// CANCELED, 2 = usage error); all soak behavior lives in BatchSoakTestService.
/// </summary>
public static class BatchSoakCommand
{
    private static readonly HashSet<string> ValueOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "images",
        "manifest",
        "output",
        "operator",
        "profile",
        "duration-minutes",
        "delay-seconds",
        "engine",
        "priority",
        "max-passes",
        "stuck-timeout-minutes",
        "memory-slope-fail-mb-per-hour",
        "memory-growth-fail-mb",
        "board-model",
        "lot-id",
    };

    /// <summary>Executes the batch-soak command; see class summary for the exit-code contract.</summary>
    public static async Task<int> ExecuteAsync(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length == 0 || !string.Equals(args[0], "batch-soak", StringComparison.OrdinalIgnoreCase))
        {
            error.WriteLine("FAIL batch-soak command was not selected.");
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

        var runFolder = Path.Combine(
            Path.GetFullPath(parse.Options.OutputFolder),
            "batch_soak_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture));
        var options = parse.Options with { OutputFolder = runFolder };

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            output.WriteLine("Cancellation requested; abandoning the in-flight image and writing partial evidence...");
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Benign race: the run already completed and Execute is returning.
            }
        };
        Console.CancelKeyPress += cancelHandler;
        UnhandledExceptionEventHandler crashHandler = (_, eventArgs) =>
            BatchSoakTestService.WriteCrashMarker(runFolder, eventArgs.ExceptionObject?.ToString() ?? "Unknown unhandled exception.", error);
        AppDomain.CurrentDomain.UnhandledException += crashHandler;

        try
        {
            WriteBanner(output, options, runFolder);
            var result = await BatchSoakTestService.RunAsync(options, new ConsoleProgress(output), cancellation.Token);
            var reports = BatchSoakReportService.WriteReports(result, runFolder);
            WriteSummary(output, result, reports);
            return string.Equals(result.Status, "PASS", StringComparison.Ordinal) ? 0 : 1;
        }
        catch (ArgumentException ex)
        {
            error.WriteLine($"FAIL {ex.Message}");
            WriteUsage(error);
            return 2;
        }
        catch (InvalidDataException ex)
        {
            error.WriteLine($"FAIL Manifest CSV is not usable: {ex.Message}");
            WriteUsage(error);
            return 2;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or DbException)
        {
            BatchSoakTestService.WriteCrashMarker(runFolder, ex.ToString(), error);
            error.WriteLine($"FAIL Batch soak run failed: {ex.Message}");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            AppDomain.CurrentDomain.UnhandledException -= crashHandler;
        }
    }

    private static void WriteBanner(TextWriter output, BatchSoakOptions options, string runFolder)
    {
        output.WriteLine("AOI Monitor Stage 1 batch-inspection soak test.");
        output.WriteLine(BatchSoakTestService.ScopeStatement);
        output.WriteLine($"Images: {options.ImageFolder}");
        output.WriteLine($"Manifest: {(string.IsNullOrWhiteSpace(options.ManifestPath) ? "none (unlabeled soak)" : options.ManifestPath)}");
        output.WriteLine($"Engine key: {(string.IsNullOrWhiteSpace(options.EngineKey) ? "(configured engine selection)" : options.EngineKey)}; detection priority: {options.DetectionPriority}.");
        output.WriteLine(FormattableString.Invariant(
            $"Duration: {options.Duration}; delay between passes: {options.DelayBetweenPasses}; stuck-image timeout: {options.StuckImageTimeout}."));
        output.WriteLine($"Output: {runFolder}");
    }

    private static void WriteSummary(TextWriter output, BatchSoakResult result, BatchSoakReportPaths reports)
    {
        output.WriteLine($"{result.Status} Batch soak run {result.RunId} completed.");
        output.WriteLine(FormattableString.Invariant(
            $"Passes: {result.TotalPasses}; images: {result.TotalImagesProcessed}; errors: {result.TotalErrorCount}; over 1 s: {result.CountOverOneSecond}."));
        output.WriteLine(FormattableString.Invariant(
            $"Managed memory start/end/peak: {result.StartManagedMemoryMegabytes:F1}/{result.EndManagedMemoryMegabytes:F1}/{result.PeakManagedMemoryMegabytes:F1} MB."));
        output.WriteLine(FormattableString.Invariant(
            $"Handles start/end/peak: {result.StartHandleCount}/{result.EndHandleCount}/{result.PeakHandleCount}; SQLite growth: {result.DatabaseGrowthMegabytes:F2} MB."));
        output.WriteLine($"Memory trend: {result.MemoryTrend.Description}");
        if (result.FailReasons.Count > 0)
            output.WriteLine($"Failure conditions: {string.Join(", ", result.FailReasons.Distinct(StringComparer.Ordinal))}.");
        if (result.WasCanceled)
            output.WriteLine($"Canceled: {result.CancellationReason} A canceled run is not acceptance evidence.");
        output.WriteLine($"8-hour uploaded-image PoC evidence: {(result.IsEightHourUploadedImagePoCEvidence ? "YES" : "NO")}.");
        output.WriteLine($"HTML report: {reports.HtmlPath}");
        output.WriteLine($"JSON report: {reports.JsonPath}");
        output.WriteLine($"Passes CSV: {reports.PassesCsvPath}");
    }

    private static ParseResult Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            if (!key.StartsWith("--", StringComparison.Ordinal))
                return ParseResult.Fail($"Unexpected argument: {key}");
            var name = key[2..];
            if (name.Equals("no-persist-batch-runs", StringComparison.OrdinalIgnoreCase))
            {
                flags.Add(name);
                continue;
            }
            if (!ValueOptions.Contains(name))
                return ParseResult.Fail($"Unknown option: {key}");
            if (values.ContainsKey(name))
                return ParseResult.Fail($"Duplicate option: {key}");
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                return ParseResult.Fail($"Missing value for {key}.");

            values[name] = args[++i];
        }

        foreach (var name in new[] { "images", "output", "operator" })
        {
            if (!values.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
                return ParseResult.Fail($"Missing required option --{name}.");
        }

        if (!TryParseDuration(values, out var duration, out var durationError))
            return ParseResult.Fail(durationError);
        if (!TryParsePositiveDouble(values, "delay-seconds", 2, allowZero: true, out var delaySeconds, out var delayError))
            return ParseResult.Fail(delayError);
        if (!TryParsePositiveDouble(values, "stuck-timeout-minutes", 5, allowZero: false, out var stuckMinutes, out var stuckError))
            return ParseResult.Fail(stuckError);
        if (!TryParsePositiveDouble(values, "memory-slope-fail-mb-per-hour", 64, allowZero: true, out var slopeFail, out var slopeError))
            return ParseResult.Fail(slopeError);
        if (!TryParsePositiveDouble(values, "memory-growth-fail-mb", 256, allowZero: true, out var growthFail, out var growthError))
            return ParseResult.Fail(growthError);
        if (!TryParseEngine(values, out var engineKey, out var engineError))
            return ParseResult.Fail(engineError);
        if (!TryParsePriority(values, out var priority, out var priorityError))
            return ParseResult.Fail(priorityError);

        int? maxPasses = null;
        if (values.TryGetValue("max-passes", out var maxPassesText))
        {
            if (!int.TryParse(maxPassesText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
                return ParseResult.Fail($"Invalid --max-passes value: {maxPassesText}");
            maxPasses = parsed;
        }

        // The inspection engines require absolute paths (image URIs); resolve here so
        // relative CLI arguments behave the same as the UI's absolute-path pickers.
        return new ParseResult(true, string.Empty, new BatchSoakOptions
        {
            ImageFolder = Path.GetFullPath(values["images"]),
            ManifestPath = values.TryGetValue("manifest", out var manifest) && !string.IsNullOrWhiteSpace(manifest)
                ? Path.GetFullPath(manifest)
                : null,
            OutputFolder = Path.GetFullPath(values["output"]),
            OperatorId = values["operator"],
            Duration = duration,
            DelayBetweenPasses = TimeSpan.FromSeconds(delaySeconds),
            StuckImageTimeout = TimeSpan.FromMinutes(stuckMinutes),
            MemorySlopeFailMegabytesPerHour = slopeFail,
            MemoryGrowthFailFloorMegabytes = growthFail,
            MaxPasses = maxPasses,
            EngineKey = engineKey,
            DetectionPriority = priority,
            BoardModel = values.TryGetValue("board-model", out var board) ? board : "TBOX-MAIN",
            LotId = values.TryGetValue("lot-id", out var lot) ? lot : "BATCH-SOAK",
            PersistBatchRuns = !flags.Contains("no-persist-batch-runs"),
        });
    }

    private static bool TryParseDuration(Dictionary<string, string> values, out TimeSpan duration, out string error)
    {
        duration = TimeSpan.FromMinutes(5);
        error = string.Empty;
        if (values.TryGetValue("profile", out var profile))
        {
            duration = profile.ToLowerInvariant() switch
            {
                "smoke" => TimeSpan.FromMinutes(5),
                "thirty-minute" => TimeSpan.FromMinutes(30),
                "eight-hour" => TimeSpan.FromHours(8),
                _ => TimeSpan.Zero,
            };
            if (duration == TimeSpan.Zero)
            {
                error = $"Unknown profile: {profile}. Use smoke, thirty-minute, or eight-hour.";
                return false;
            }
        }

        if (values.TryGetValue("duration-minutes", out var durationText))
        {
            if (!double.TryParse(durationText, NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes) || minutes <= 0)
            {
                error = $"Invalid --duration-minutes value: {durationText}";
                return false;
            }

            duration = TimeSpan.FromMinutes(minutes);
        }

        return true;
    }

    private static bool TryParsePositiveDouble(
        Dictionary<string, string> values,
        string name,
        double defaultValue,
        bool allowZero,
        out double parsedValue,
        out string error)
    {
        parsedValue = defaultValue;
        error = string.Empty;
        if (!values.TryGetValue(name, out var text))
            return true;

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out parsedValue) ||
            parsedValue < 0 ||
            (!allowZero && parsedValue == 0))
        {
            error = $"Invalid --{name} value: {text}";
            return false;
        }

        return true;
    }

    private static bool TryParseEngine(Dictionary<string, string> values, out string engineKey, out string error)
    {
        engineKey = string.Empty;
        error = string.Empty;
        if (!values.TryGetValue("engine", out var requested))
            return true;

        var normalized = InspectionEngineFactory.NormalizeEngineKey(requested);
        if (normalized is not (InspectionEngineFactory.DefaultEngineKey
            or InspectionEngineFactory.OnnxEngineKey
            or InspectionEngineFactory.LearnedVisualEngineKey))
        {
            error = $"Unknown --engine value: {requested}. Use pixel-difference, onnx, or learned-pcb-visual.";
            return false;
        }

        engineKey = normalized;
        return true;
    }

    private static bool TryParsePriority(Dictionary<string, string> values, out DetectionPriority priority, out string error)
    {
        priority = DetectionPriority.Balanced;
        error = string.Empty;
        if (!values.TryGetValue("priority", out var requested))
            return true;

        switch (requested.Trim().ToLowerInvariant())
        {
            case "balanced":
                priority = DetectionPriority.Balanced;
                return true;
            case "minimize-false-positives":
                priority = DetectionPriority.MinimizeFalsePositives;
                return true;
            case "maximize-defect-recall":
                priority = DetectionPriority.MaximizeDefectRecall;
                return true;
            default:
                error = $"Unknown --priority value: {requested}. Use balanced, minimize-false-positives, or maximize-defect-recall.";
                return false;
        }
    }

    private static void WriteUsage(TextWriter writer)
    {
        writer.WriteLine("Usage:");
        writer.WriteLine("  AOI_Monitor.Tools batch-soak --images <folder> --output <folder> --operator <id>");
        writer.WriteLine("      [--manifest <csv>] [--profile smoke|thirty-minute|eight-hour] [--duration-minutes <n>]");
        writer.WriteLine("      [--delay-seconds <n>] [--engine pixel-difference|onnx|learned-pcb-visual]");
        writer.WriteLine("      [--priority balanced|minimize-false-positives|maximize-defect-recall]");
        writer.WriteLine("      [--max-passes <n>] [--stuck-timeout-minutes <n>]");
        writer.WriteLine("      [--memory-slope-fail-mb-per-hour <n>] [--memory-growth-fail-mb <n>]");
        writer.WriteLine("      [--board-model <name>] [--lot-id <id>] [--no-persist-batch-runs]");
        writer.WriteLine("Default profile is smoke (5 minutes). Use --profile eight-hour for the customer 8-hour PoC soak.");
        writer.WriteLine("Without --engine the configured engine selection is used (pixel-difference prototype unless a model is configured and ready).");
    }

    private sealed class ConsoleProgress : IProgress<BatchSoakProgress>
    {
        private readonly TextWriter _output;

        public ConsoleProgress(TextWriter output)
            => _output = output;

        public void Report(BatchSoakProgress value)
            => _output.WriteLine(FormattableString.Invariant(
                $"[{value.Elapsed:hh\\:mm\\:ss} elapsed, {value.Remaining:hh\\:mm\\:ss} remaining] {value.Message}"));
    }

    private sealed record ParseResult(bool Success, string Message, BatchSoakOptions Options)
    {
        public static ParseResult Fail(string message)
            => new(false, message, new BatchSoakOptions());
    }
}
