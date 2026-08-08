using AOI_Monitor.Services;

namespace AOI_Monitor.Tools;

/// <summary>
/// Headless driver for the Stage 1 readiness gate — the same service behind
/// Export &amp; Trace &gt; Stage 1 Readiness. Exposing it without the GUI is what makes Stage 1
/// readiness reproducible: a tester or CI job can produce the identical HTML/PDF/JSON report and
/// act on the exit code instead of reading a screen.
///
/// Thin CLI shell only: parsing, console rendering, exit codes.
/// 0 = PASS, 1 = CONDITIONAL, 2 = FAIL, 3 = usage error.
/// The distinct CONDITIONAL code lets a pipeline treat "evidence incomplete" differently from
/// "evidence contradicts readiness".
/// </summary>
public static class Stage1ReadinessCommand
{
    private static readonly HashSet<string> ValueOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "dataset",
        "manifest",
        "output",
        "p95-target-ms",
    };

    public static int Execute(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length == 0 || !string.Equals(args[0], "stage1-readiness", StringComparison.OrdinalIgnoreCase))
        {
            error.WriteLine("FAIL stage1-readiness command was not selected.");
            WriteUsage(error);
            return 3;
        }

        var parse = Parse(args.Skip(1).ToArray());
        if (!parse.Success)
        {
            error.WriteLine($"FAIL {parse.Message}");
            WriteUsage(error);
            return 3;
        }

        try
        {
            var export = Stage1ReadinessGateService.ExportReport(parse.OutputFolder, parse.Options);
            var report = export.Report;

            output.WriteLine($"{report.OverallStatus} Stage 1 readiness gate.");
            output.WriteLine("Scope: Stage 1 uploaded-image validation only. This gate never claims real camera, lighting, robot, PLC safety, production MES, ERP, or factory automation readiness.");
            output.WriteLine($"Dataset: {Display(report.DatasetFolder)}");
            output.WriteLine($"Manifest: {Display(report.ManifestPath)}");
            output.WriteLine($"Images: {report.TotalImages}; OK/NG/REVIEW: {report.OkCount}/{report.NgCount}/{report.ReviewCount}; false calls: {report.FalseCallCount}; possible escapes: {report.PossibleEscapeCount}");
            output.WriteLine();

            var pass = 0;
            var conditional = 0;
            var fail = 0;
            foreach (var check in report.Checks)
            {
                switch (check.Status)
                {
                    case Stage1ReadinessGateService.Pass:
                        pass++;
                        break;
                    case Stage1ReadinessGateService.Conditional:
                        conditional++;
                        break;
                    default:
                        fail++;
                        break;
                }

                output.WriteLine($"[{check.Status,-11}] {check.Name}");
                if (check.Status != Stage1ReadinessGateService.Pass)
                {
                    output.WriteLine($"              {check.Evidence}");
                    output.WriteLine($"        Next: {check.NextAction}");
                }
            }

            output.WriteLine();
            output.WriteLine($"Checks: {pass} PASS, {conditional} CONDITIONAL, {fail} FAIL.");
            output.WriteLine($"Next recommended action: {report.NextRecommendedAction}");
            output.WriteLine($"Report folder: {export.Folder}");
            output.WriteLine($"  HTML: {export.HtmlPath}");
            output.WriteLine($"  PDF:  {export.PdfPath}");
            output.WriteLine($"  JSON: {export.JsonPath}");

            return report.OverallStatus switch
            {
                Stage1ReadinessGateService.Pass => 0,
                Stage1ReadinessGateService.Conditional => 1,
                _ => 2,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            error.WriteLine($"FAIL Stage 1 readiness gate could not run: {ex.Message}");
            return 2;
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
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                return ParseResult.Fail($"Missing value for {key}.");

            values[name] = args[++i];
        }

        var options = new Stage1ReadinessGateOptions
        {
            DatasetFolder = values.TryGetValue("dataset", out var dataset) ? dataset : string.Empty,
            ManifestPath = values.TryGetValue("manifest", out var manifest) ? manifest : string.Empty,
            P95FrameToOverlayTargetMs = values.TryGetValue("p95-target-ms", out var target) &&
                double.TryParse(target, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) &&
                parsed > 0
                    ? parsed
                    : 1000,
        };

        return new ParseResult(true, string.Empty, values.TryGetValue("output", out var output) ? output : string.Empty, options);
    }

    private static string Display(string value)
        => string.IsNullOrWhiteSpace(value) ? "(not available)" : value;

    private static void WriteUsage(TextWriter writer)
    {
        writer.WriteLine("Usage:");
        writer.WriteLine("  AOI_Monitor.Tools stage1-readiness [--dataset <folder>] [--manifest <csv>]");
        writer.WriteLine("                                     [--output <folder>] [--p95-target-ms <ms>]");
        writer.WriteLine();
        writer.WriteLine("  Evaluates the Stage 1 readiness gate against persisted evidence and writes an");
        writer.WriteLine("  HTML/PDF/JSON report. Omit --dataset/--manifest to use the latest persisted batch");
        writer.WriteLine("  run, or the generated SampleData/DemoSet_Quick dataset.");
        writer.WriteLine();
        writer.WriteLine("  Exit codes: 0 = PASS, 1 = CONDITIONAL, 2 = FAIL, 3 = usage error.");
    }

    private sealed record ParseResult(bool Success, string Message, string OutputFolder, Stage1ReadinessGateOptions Options)
    {
        public static ParseResult Fail(string message)
            => new(false, message, string.Empty, new Stage1ReadinessGateOptions());
    }
}
