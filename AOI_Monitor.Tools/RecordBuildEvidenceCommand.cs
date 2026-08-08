using AOI_Monitor.Services;

namespace AOI_Monitor.Tools;

/// <summary>
/// Records the outcome of a local build/test/quality-gate run as persisted evidence, so the
/// Stage 1 readiness gate's "App build/test evidence" check has something truthful to read.
///
/// The statuses are supplied by whoever ran the gates — this command does not run or infer them.
/// That is deliberate: a tool that asserted its own PASS would be evidence of nothing. Callers
/// pass the real results (the Stage 1 test procedure shows the exact invocation), and the record
/// carries the git commit, configuration, machine, and operator for traceability.
///
/// Exit codes: 0 = recorded, 2 = usage error.
/// </summary>
public static class RecordBuildEvidenceCommand
{
    private static readonly HashSet<string> ValueOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "configuration",
        "hygiene",
        "restore",
        "build",
        "test",
        "publish-validation",
        "test-results",
        "operator",
    };

    public static int Execute(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length == 0 || !string.Equals(args[0], "record-build-evidence", StringComparison.OrdinalIgnoreCase))
        {
            error.WriteLine("FAIL record-build-evidence command was not selected.");
            WriteUsage(error);
            return 2;
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var rest = args.Skip(1).ToArray();
        for (var i = 0; i < rest.Length; i++)
        {
            var key = rest[i];
            if (!key.StartsWith("--", StringComparison.Ordinal))
            {
                error.WriteLine($"FAIL Unexpected argument: {key}");
                WriteUsage(error);
                return 2;
            }

            var name = key[2..];
            if (!ValueOptions.Contains(name))
            {
                error.WriteLine($"FAIL Unknown option: {key}");
                WriteUsage(error);
                return 2;
            }

            if (i + 1 >= rest.Length || rest[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                error.WriteLine($"FAIL Missing value for {key}.");
                WriteUsage(error);
                return 2;
            }

            values[name] = rest[++i];
        }

        try
        {
            var evidence = BuildTestEvidenceService.CreateLocalEvidence(
                configuration: Get(values, "configuration", "Release"),
                hygieneStatus: Get(values, "hygiene", "PASS"),
                restoreStatus: Get(values, "restore", "PASS"),
                buildStatus: Get(values, "build", "PASS"),
                testStatus: Get(values, "test", "PASS"),
                publishValidationStatus: Get(values, "publish-validation", "PASS"),
                testResultPath: Get(values, "test-results", string.Empty),
                operatorId: Get(values, "operator", "UNKNOWN"));

            output.WriteLine("OK Build/test evidence recorded.");
            output.WriteLine($"Commit: {(string.IsNullOrWhiteSpace(evidence.GitCommit) ? "(not a git checkout)" : evidence.GitCommit)}");
            output.WriteLine($"Configuration: {evidence.Configuration}; machine: {evidence.MachineName}; operator: {evidence.OperatorId}");
            output.WriteLine($"Hygiene/restore/build/test/publish: {evidence.HygieneStatus}/{evidence.RestoreStatus}/{evidence.BuildStatus}/{evidence.TestStatus}/{evidence.PublishValidationStatus}");
            output.WriteLine($"Evidence: {evidence.EvidencePath}");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            error.WriteLine($"FAIL Build/test evidence could not be recorded: {ex.Message}");
            return 2;
        }
    }

    private static string Get(IReadOnlyDictionary<string, string> values, string name, string fallback)
        => values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;

    private static void WriteUsage(TextWriter writer)
    {
        writer.WriteLine("Usage:");
        writer.WriteLine("  AOI_Monitor.Tools record-build-evidence [--configuration Release]");
        writer.WriteLine("        [--hygiene PASS|FAIL] [--restore PASS|FAIL] [--build PASS|FAIL]");
        writer.WriteLine("        [--test PASS|FAIL] [--publish-validation PASS|FAIL]");
        writer.WriteLine("        [--test-results <path>] [--operator <id>]");
        writer.WriteLine();
        writer.WriteLine("  Pass the statuses actually observed. Defaults are PASS, so always state a failure");
        writer.WriteLine("  explicitly rather than relying on the default.");
    }
}
