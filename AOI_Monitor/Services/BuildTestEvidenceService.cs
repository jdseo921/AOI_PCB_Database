using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using AOI_Monitor.Data;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public static class BuildTestEvidenceService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static string EvidenceFolder => Path.Combine(AoiDatabase.StorageRoot, "exports", "build_evidence");

    public static BuildTestEvidenceRecord CreateLocalEvidence(
        string configuration = "Release",
        string hygieneStatus = "PASS",
        string restoreStatus = "PASS",
        string buildStatus = "PASS",
        string testStatus = "PASS",
        string publishValidationStatus = "PASS",
        string testResultPath = "",
        string operatorId = "UNKNOWN")
    {
        Directory.CreateDirectory(EvidenceFolder);
        var generatedAt = DateTime.UtcNow;
        var evidence = new BuildTestEvidenceRecord
        {
            GeneratedAtUtc = generatedAt,
            GitCommit = TryGetGitCommit(),
            Configuration = string.IsNullOrWhiteSpace(configuration) ? "Release" : configuration.Trim(),
            HygieneStatus = NormalizeStatus(hygieneStatus),
            RestoreStatus = NormalizeStatus(restoreStatus),
            BuildStatus = NormalizeStatus(buildStatus),
            TestStatus = NormalizeStatus(testStatus),
            PublishValidationStatus = NormalizeStatus(publishValidationStatus),
            TestResultPath = testResultPath?.Trim() ?? string.Empty,
            OperatorId = string.IsNullOrWhiteSpace(operatorId) ? "UNKNOWN" : operatorId.Trim(),
            MachineName = Environment.MachineName,
            CreatedAtUtc = generatedAt,
        };
        evidence.EvidencePath = Path.Combine(EvidenceFolder, $"build_test_evidence_{generatedAt:yyyyMMdd_HHmmss}.json");

        File.WriteAllText(evidence.EvidencePath, JsonSerializer.Serialize(evidence, JsonOptions), Encoding.UTF8);
        evidence.Id = AoiDatabase.RecordBuildTestEvidence(evidence);
        return evidence;
    }

    public static BuildTestEvidenceRecord ImportEvidence(string sourcePath, string operatorId = "UNKNOWN")
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            throw new FileNotFoundException("Build/test evidence JSON was not found.", sourcePath);

        var imported = JsonSerializer.Deserialize<BuildTestEvidenceRecord>(
            File.ReadAllText(sourcePath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Build/test evidence JSON could not be parsed.");

        Directory.CreateDirectory(EvidenceFolder);
        imported.GeneratedAtUtc = imported.GeneratedAtUtc == DateTime.MinValue ? DateTime.UtcNow : imported.GeneratedAtUtc.ToUniversalTime();
        imported.GitCommit = imported.GitCommit?.Trim() ?? string.Empty;
        imported.Configuration = string.IsNullOrWhiteSpace(imported.Configuration) ? "Release" : imported.Configuration.Trim();
        imported.HygieneStatus = NormalizeStatus(imported.HygieneStatus);
        imported.RestoreStatus = NormalizeStatus(imported.RestoreStatus);
        imported.BuildStatus = NormalizeStatus(imported.BuildStatus);
        imported.TestStatus = NormalizeStatus(imported.TestStatus);
        imported.PublishValidationStatus = NormalizeStatus(imported.PublishValidationStatus);
        imported.TestResultPath = imported.TestResultPath?.Trim() ?? string.Empty;
        imported.OperatorId = string.IsNullOrWhiteSpace(operatorId) ? imported.OperatorId : operatorId.Trim();
        imported.OperatorId = string.IsNullOrWhiteSpace(imported.OperatorId) ? "UNKNOWN" : imported.OperatorId;
        imported.MachineName = string.IsNullOrWhiteSpace(imported.MachineName) ? Environment.MachineName : imported.MachineName.Trim();
        imported.CreatedAtUtc = DateTime.UtcNow;

        var destination = Path.Combine(EvidenceFolder, $"imported_build_test_evidence_{imported.GeneratedAtUtc:yyyyMMdd_HHmmss}.json");
        if (!Path.GetFullPath(sourcePath).Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
        {
            destination = EnsureUniqueFile(destination);
            imported.EvidencePath = destination;
            File.WriteAllText(destination, JsonSerializer.Serialize(imported, JsonOptions), Encoding.UTF8);
        }
        else
        {
            imported.EvidencePath = sourcePath;
        }

        imported.Id = AoiDatabase.RecordBuildTestEvidence(imported);
        return imported;
    }

    public static BuildTestEvidenceSummary GetSummary()
    {
        var latest = AoiDatabase.GetLatestBuildTestEvidence();
        if (latest is null)
        {
            return new BuildTestEvidenceSummary
            {
                Status = "NoEvidence",
                Messages = { "No local build/test evidence artifact has been imported or recorded." },
            };
        }

        var failures = RequiredStatuses(latest)
            .Where(item => !IsPassingStatus(item.Status))
            .Select(item => $"{item.Name}={item.Status}")
            .ToArray();

        return new BuildTestEvidenceSummary
        {
            Status = failures.Length == 0 ? "PASS" : "FAIL",
            Latest = latest,
            IsPassing = failures.Length == 0,
            HasFailure = failures.Length > 0,
            Messages = failures.Length == 0
                ? new List<string> { "Latest local hygiene, restore, build, test, and publish validation evidence all pass." }
                : new List<string> { $"Latest build/test evidence has failing or unknown checks: {string.Join(", ", failures)}." },
        };
    }

    public static bool IsPassingStatus(string? status)
    {
        var normalized = NormalizeStatus(status);
        return normalized is "PASS" or "OK" or "SUCCESS" or "SUCCEEDED";
    }

    private static IEnumerable<(string Name, string Status)> RequiredStatuses(BuildTestEvidenceRecord evidence)
    {
        yield return ("hygiene", evidence.HygieneStatus);
        yield return ("restore", evidence.RestoreStatus);
        yield return ("build", evidence.BuildStatus);
        yield return ("test", evidence.TestStatus);
        yield return ("publishValidation", evidence.PublishValidationStatus);
    }

    private static string NormalizeStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return "UNKNOWN";

        var normalized = status.Trim().ToUpperInvariant();
        return normalized switch
        {
            "PASSED" => "PASS",
            "FAILED" => "FAIL",
            "ERROR" => "FAIL",
            _ => normalized,
        };
    }

    private static string TryGetGitCommit()
    {
        try
        {
            var startInfo = new ProcessStartInfo("git", "rev-parse HEAD")
            {
                WorkingDirectory = AppContext.BaseDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(startInfo);
            if (process is null)
                return string.Empty;

            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(2000);
            return process.ExitCode == 0 ? output : string.Empty;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Git commit evidence lookup failed: {ex.Message}");
            return string.Empty;
        }
    }

    private static string EnsureUniqueFile(string path)
    {
        if (!File.Exists(path))
            return path;

        var directory = Path.GetDirectoryName(path) ?? EvidenceFolder;
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var i = 2; i < 1000; i++)
        {
            var candidate = Path.Combine(directory, $"{stem}_{i:D2}{extension}");
            if (!File.Exists(candidate))
                return candidate;
        }

        return Path.Combine(directory, $"{stem}_{Guid.NewGuid():N}{extension}");
    }
}
