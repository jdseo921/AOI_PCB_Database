using AOI_Monitor.Services;

namespace AOI_Monitor.Tools;

public static class Stage2CameraPilotCommand
{
    public static int Execute(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            WriteUsage(output);
            return args.Length == 0 ? 2 : 0;
        }

        if (!string.Equals(args[0], "stage2-camera-pilot", StringComparison.OrdinalIgnoreCase))
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
            var result = Stage2CameraPilotEvidencePackageService.CreatePackage(new Stage2CameraPilotEvidenceRequest
            {
                OutputFolder = parse.OutputFolder,
                OperatorId = parse.OperatorId,
                AllowSimulation = parse.AllowSimulation,
            });

            output.WriteLine($"{result.Status} Stage 2 camera pilot evidence package complete.");
            output.WriteLine($"Stage 1: {result.Stage1EvidenceStatus}");
            output.WriteLine($"Camera: {result.CameraEvidenceStatus}");
            output.WriteLine($"Lighting: {result.LightingEvidenceStatus}");
            output.WriteLine($"3D profile: {result.Profile3DEvidenceStatus}");
            output.WriteLine($"Factory readiness: {result.FactoryReadinessStatus}");
            output.WriteLine($"Simulation evidence present: {result.SimulationEvidencePresent}");
            output.WriteLine($"Real hardware validated: {result.RealHardwareValidated}");
            if (result.DryRun)
                output.WriteLine($"WARN {Stage2CameraPilotEvidencePackageService.SimulationNotice}.");
            output.WriteLine($"Package: {result.PackageFolder}");
            output.WriteLine($"Manifest: {result.ManifestPath}");
            output.WriteLine($"README: {result.ReadmePath}");
            output.WriteLine($"Export verification: {result.ExportVerificationStatus}");

            foreach (var issue in result.BlockingIssues)
                error.WriteLine($"BLOCKED {issue}");

            return result.Status switch
            {
                "PASS" or "WARN" or "DRY RUN" => 0,
                _ => 1,
            };
        }
        catch (Stage2CameraPilotEvidenceException ex)
        {
            error.WriteLine($"FAIL {ex.Message}");
            return 1;
        }
        catch (OperationCanceledException)
        {
            error.WriteLine("FAIL Stage 2 camera pilot package workflow was canceled.");
            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            error.WriteLine($"FAIL Stage 2 camera pilot package workflow failed: {ex.Message}");
            return 1;
        }
    }

    private static ParseResult Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var allowSimulation = false;
        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            if (IsHelp(key))
                return ParseResult.Fail("Use the command form shown below.");
            if (!key.StartsWith("--", StringComparison.Ordinal))
                return ParseResult.Fail($"Unexpected argument: {key}");
            if (key.Equals("--allow-simulation", StringComparison.OrdinalIgnoreCase))
            {
                allowSimulation = true;
                continue;
            }

            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                return ParseResult.Fail($"Missing value for {key}.");

            values[key[2..]] = args[++i];
        }

        if (!values.TryGetValue("output", out var outputFolder) || string.IsNullOrWhiteSpace(outputFolder))
            return ParseResult.Fail("Missing required option --output.");
        if (!values.TryGetValue("operator", out var operatorId) || string.IsNullOrWhiteSpace(operatorId))
            return ParseResult.Fail("Missing required option --operator.");

        return new ParseResult(true, string.Empty, outputFolder, operatorId, allowSimulation);
    }

    private static bool IsHelp(string value)
        => string.Equals(value, "-h", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, "--help", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, "help", StringComparison.OrdinalIgnoreCase);

    private static void WriteUsage(TextWriter writer)
    {
        writer.WriteLine("Usage:");
        writer.WriteLine("  AOI_Monitor.Tools stage2-camera-pilot --output <folder> --operator <id> [--allow-simulation]");
    }

    private sealed record ParseResult(
        bool Success,
        string Message,
        string OutputFolder,
        string OperatorId,
        bool AllowSimulation)
    {
        public static ParseResult Fail(string message)
            => new(false, message, string.Empty, string.Empty, false);
    }
}
