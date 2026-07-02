using AOI_Monitor.Models;
using AOI_Monitor.Services;

namespace AOI_Monitor.Tools;

public static class ImageLearningProjectImportCommand
{
    public static int Execute(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            WriteUsage(output);
            return args.Length == 0 ? 2 : 0;
        }

        if (!string.Equals(args[0], "import-image-learning-project", StringComparison.OrdinalIgnoreCase))
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
            var result = ImageLearningFolderImportService.ImportProjectFolder(
                parse.ProjectFolder,
                parse.OperatorId,
                parse.EvidenceMode);
            AiTrainingSetupService.SelectProject(
                result.Project.ProjectId,
                result.RoleSummaries.ToDictionary(summary => summary.Role, summary => summary.SourceFolder));

            output.WriteLine("OK Image-only learning project import complete.");
            output.WriteLine($"projectId: {result.Project.ProjectId}");
            output.WriteLine("counts by role:");
            foreach (var role in Enum.GetValues<ImageLearningImageRole>())
                output.WriteLine($"  {role}: {result.CountsByRole.GetValueOrDefault(role)}");

            output.WriteLine("warnings:");
            if (result.Warnings.Count == 0)
            {
                output.WriteLine("  none");
            }
            else
            {
                foreach (var warning in result.Warnings)
                    output.WriteLine($"  - {warning}");
            }

            output.WriteLine($"next suggested command: {result.NextSuggestedCommand}");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            error.WriteLine($"FAIL Image-only learning project import failed: {ex.Message}");
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

        if (!values.TryGetValue("project-folder", out var projectFolder) || string.IsNullOrWhiteSpace(projectFolder))
            return ParseResult.Fail("Missing required option --project-folder.");
        if (!values.TryGetValue("operator", out var operatorId) || string.IsNullOrWhiteSpace(operatorId))
            return ParseResult.Fail("Missing required option --operator.");
        if (!values.TryGetValue("evidence-mode", out var evidenceModeText) || string.IsNullOrWhiteSpace(evidenceModeText))
            return ParseResult.Fail("Missing required option --evidence-mode.");
        if (!Enum.TryParse<ImageLearningEvidenceMode>(evidenceModeText, ignoreCase: true, out var evidenceMode))
            return ParseResult.Fail("Invalid --evidence-mode. Use CustomerData, InternalDemo, or SyntheticDemo.");

        return new ParseResult(true, string.Empty, projectFolder, operatorId, evidenceMode);
    }

    private static bool IsHelp(string value)
        => string.Equals(value, "-h", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, "--help", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, "help", StringComparison.OrdinalIgnoreCase);

    private static void WriteUsage(TextWriter writer)
    {
        writer.WriteLine("Usage:");
        writer.WriteLine("  AOI_Monitor.Tools import-image-learning-project --project-folder <folder> --operator <id> --evidence-mode CustomerData|InternalDemo|SyntheticDemo");
        writer.WriteLine("Folder convention:");
        writer.WriteLine("  project_folder/golden");
        writer.WriteLine("  project_folder/ok_learning");
        writer.WriteLine("  project_folder/ok_validation");
        writer.WriteLine("  project_folder/inspection");
        writer.WriteLine("  project_folder/ng_validation optional");
    }

    private sealed record ParseResult(
        bool Success,
        string Message,
        string ProjectFolder,
        string OperatorId,
        ImageLearningEvidenceMode EvidenceMode)
    {
        public static ParseResult Fail(string message)
            => new(false, message, string.Empty, string.Empty, ImageLearningEvidenceMode.CustomerData);
    }
}
