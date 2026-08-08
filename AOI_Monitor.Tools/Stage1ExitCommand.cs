using AOI_Monitor.Models;
using AOI_Monitor.Services;

namespace AOI_Monitor.Tools;

public static class Stage1ExitCommand
{
    public static int Execute(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            WriteUsage(output);
            return args.Length == 0 ? 2 : 0;
        }

        if (!string.Equals(args[0], "stage1-exit", StringComparison.OrdinalIgnoreCase))
        {
            error.WriteLine($"FAIL Unknown command: {args[0]}");
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
            var result = Stage1ExitEvidenceService.Run(new Stage1ExitEvidenceRequest
            {
                DatasetFolder = parse.DatasetFolder,
                ManifestPath = parse.ManifestPath,
                OutputFolder = parse.OutputFolder,
                OperatorId = parse.OperatorId,
                AllowSimulation = parse.AllowSimulation,
                DetectionPriority = parse.DetectionPriority,
            });

            output.WriteLine($"{result.Status} Stage 1 exit evidence workflow complete.");
            output.WriteLine($"Detection priority: {parse.DetectionPriority}");
            output.WriteLine($"Preflight: {result.PreflightStatus}; rows={result.ManifestRows}; OK={result.OkCount}; NG={result.NgCount}");
            output.WriteLine($"Metrics: precision={result.Precision:P1}; recall={result.Recall:P1}; false-call={result.FalseCallRate:P1}; possible-escapes={result.PossibleEscapeCount}");
            output.WriteLine($"Model acceptance: {result.ModelAcceptanceStatus}");
            if (result.PrototypeOnly)
                output.WriteLine("Production model readiness: NOT CLAIMED; prototype-only evidence.");
            if (result.DryRun)
                output.WriteLine($"Evidence boundary: {Stage1ExitEvidenceService.SimulationDryRunNotice}.");
            output.WriteLine($"Validation package: {result.Outputs.ValidationPackageFolder}");
            output.WriteLine($"Export verification: {result.ExportVerificationStatus}; {result.Outputs.ExportVerificationTextPath}");
            output.WriteLine($"Factory readiness: {result.FactoryReadinessStatus}; {result.Outputs.FactoryReadinessFolder}");
            output.WriteLine($"Summary: {result.Outputs.SummaryTextPath}");
            return string.Equals(result.Status, "FAIL", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        }
        catch (Stage1ExitEvidenceException ex)
        {
            error.WriteLine($"FAIL {ex.Message}");
            return 1;
        }
        catch (OperationCanceledException)
        {
            error.WriteLine("FAIL Stage 1 exit evidence workflow was canceled.");
            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            error.WriteLine($"FAIL Stage 1 exit evidence workflow failed: {ex.Message}");
            return 1;
        }
    }

    private static ParseResult Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            if (IsHelp(key))
                return ParseResult.Fail("Use the command form shown below.");
            if (!key.StartsWith("--", StringComparison.Ordinal))
                return ParseResult.Fail($"Unexpected argument: {key}");
            if (key.Equals("--allow-simulation", StringComparison.OrdinalIgnoreCase))
            {
                values[key[2..]] = "true";
                continue;
            }
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                return ParseResult.Fail($"Missing value for {key}.");

            values[key[2..]] = args[++i];
        }

        var required = new[] { "dataset", "manifest", "output", "operator" };
        foreach (var name in required)
        {
            if (!values.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
                return ParseResult.Fail($"Missing required option --{name}.");
        }

        var priority = DetectionPriority.Balanced;
        if (values.TryGetValue("priority", out var priorityText) && !DetectionPriorityOption.TryParse(priorityText, out priority))
            return ParseResult.Fail(DetectionPriorityOption.FailureMessage(priorityText));

        return new ParseResult(
            true,
            string.Empty,
            values["dataset"],
            values["manifest"],
            values["output"],
            values["operator"],
            values.ContainsKey("allow-simulation"),
            priority);
    }


    private static bool IsHelp(string value)
        => string.Equals(value, "-h", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, "--help", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, "help", StringComparison.OrdinalIgnoreCase);

    private static void WriteUsage(TextWriter writer)
    {
        writer.WriteLine("Usage:");
        writer.WriteLine("  AOI_Monitor.Tools stage1-exit --dataset <folder> --manifest <csv> --output <folder> --operator <id>");
        writer.WriteLine("                               [--priority balanced|minimize-false-positives|maximize-defect-recall] [--allow-simulation]");
        writer.WriteLine();
        writer.WriteLine("  --priority selects the detection policy thresholds (default: balanced).");
    }

    private sealed record ParseResult(
        bool Success,
        string Message,
        string DatasetFolder,
        string ManifestPath,
        string OutputFolder,
        string OperatorId,
        bool AllowSimulation,
        DetectionPriority DetectionPriority)
    {
        public static ParseResult Fail(string message)
            => new(false, message, string.Empty, string.Empty, string.Empty, string.Empty, false, DetectionPriority.Balanced);
    }
}
