using AOI_Monitor.Services;

namespace AOI_Monitor.Tools;

public static class CameraAdapterValidationCommand
{
    public static int Execute(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            WriteUsage(output);
            return args.Length == 0 ? 2 : 0;
        }

        if (!string.Equals(args[0], "camera-adapter-validate", StringComparison.OrdinalIgnoreCase))
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
            var result = CameraAdapterPackageValidationService.Run(new CameraAdapterPackageValidationRequest
            {
                AdapterFolder = parse.AdapterFolder,
                SettingsJson = parse.SettingsJson,
                OutputFolder = parse.OutputFolder,
            });

            output.WriteLine($"{result.Status} Camera adapter package validation complete.");
            output.WriteLine($"Manifest: {result.ManifestStatus}; {result.ManifestPath}");
            output.WriteLine($"Load: {result.LoadStatus}; {result.LoadMessage}");
            output.WriteLine($"Discovery: {result.DiscoveryStatus}; devices={result.DiscoveredDeviceCount}");
            output.WriteLine($"Acceptance: {result.AcceptanceStatus}; readiness={result.FactoryReadinessStatus}; realHardware={result.IsRealHardwareReady}; simulatedEvidence={result.IsSimulatedEvidence}");
            if (!string.IsNullOrWhiteSpace(result.AcceptanceJsonPath))
                output.WriteLine($"Acceptance JSON: {result.AcceptanceJsonPath}");
            if (!string.IsNullOrWhiteSpace(result.AcceptanceHtmlPath))
                output.WriteLine($"Acceptance HTML: {result.AcceptanceHtmlPath}");
            output.WriteLine($"Summary JSON: {result.SummaryJsonPath}");
            output.WriteLine($"Summary HTML: {result.SummaryHtmlPath}");

            foreach (var failure in result.Failures)
                error.WriteLine($"FAIL {failure}");
            foreach (var warning in result.Warnings)
                output.WriteLine($"WARN {warning}");

            return string.Equals(result.Status, "FAIL", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        }
        catch (CameraAdapterPackageValidationException ex)
        {
            error.WriteLine($"FAIL {ex.Message}");
            return 1;
        }
        catch (OperationCanceledException)
        {
            error.WriteLine("FAIL Camera adapter package validation was canceled.");
            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            error.WriteLine($"FAIL Camera adapter package validation failed: {ex.Message}");
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
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                return ParseResult.Fail($"Missing value for {key}.");

            values[key[2..]] = args[++i];
        }

        if (!values.TryGetValue("adapter-folder", out var adapterFolder) || string.IsNullOrWhiteSpace(adapterFolder))
            return ParseResult.Fail("Missing required option --adapter-folder.");
        if (!values.TryGetValue("output-folder", out var outputFolder) || string.IsNullOrWhiteSpace(outputFolder))
            return ParseResult.Fail("Missing required option --output-folder.");
        values.TryGetValue("settings-json", out var settingsJson);
        values.TryGetValue("settings", out var settingsAlias);

        return new ParseResult(
            true,
            string.Empty,
            adapterFolder,
            string.IsNullOrWhiteSpace(settingsJson) ? settingsAlias ?? string.Empty : settingsJson,
            outputFolder);
    }

    private static bool IsHelp(string value)
        => string.Equals(value, "-h", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, "--help", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, "help", StringComparison.OrdinalIgnoreCase);

    private static void WriteUsage(TextWriter writer)
    {
        writer.WriteLine("Usage:");
        writer.WriteLine("  AOI_Monitor.Tools camera-adapter-validate --adapter-folder <path> [--settings-json <path>] --output-folder <path>");
    }

    private sealed record ParseResult(
        bool Success,
        string Message,
        string AdapterFolder,
        string SettingsJson,
        string OutputFolder)
    {
        public static ParseResult Fail(string message)
            => new(false, message, string.Empty, string.Empty, string.Empty);
    }
}
