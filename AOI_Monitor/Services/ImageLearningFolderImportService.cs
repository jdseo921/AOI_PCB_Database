using System.IO;
using AOI_Monitor.Data;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public static class ImageLearningFolderImportService
{
    public static readonly IReadOnlyDictionary<ImageLearningImageRole, string> ConventionFolders =
        new Dictionary<ImageLearningImageRole, string>
        {
            [ImageLearningImageRole.GoldenReference] = "golden",
            [ImageLearningImageRole.OkLearning] = "ok_learning",
            [ImageLearningImageRole.OkValidation] = "ok_validation",
            [ImageLearningImageRole.Inspection] = "inspection",
            [ImageLearningImageRole.NgValidation] = "ng_validation",
        };

    private static readonly HashSet<string> SupportedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png",
        ".jpg",
        ".jpeg",
    };

    private const string TruthFileName = "image_truth.csv";
    private const string NextSuggestedCommand = "Open AOI Monitor > AI / Models > AI Training Setup, then click Learn Normal PCB Appearance.";

    public static ImageLearningFolderImportResult ImportGuidedSelection(
        string projectId,
        ImageLearningImageRole role,
        IEnumerable<string> selectedPaths,
        string boardModel = "",
        string lotId = "",
        string viewType = "Top",
        string importedBy = "UNKNOWN")
    {
        ArgumentNullException.ThrowIfNull(selectedPaths);
        var project = AoiDatabase.GetImageLearningProject(projectId)
            ?? throw new InvalidOperationException("Image-only learning project does not exist.");
        var selection = ExpandSelection(selectedPaths);
        var truthFolders = selection.SearchFolders;
        var summary = ImportRoleFiles(
            project,
            role,
            selection.Files,
            truthFolders,
            boardModel,
            lotId,
            viewType,
            importedBy,
            selection.Warnings);
        var counts = ImageLearningProjectService.GetImageCountsByRole(project.ProjectId);
        var warnings = summary.Warnings.ToArray();
        RecordFolderImportAudit(project.ProjectId, importedBy, "GuidedSelection", warnings.Length);

        return new ImageLearningFolderImportResult(project, counts, [summary], warnings, NextSuggestedCommand);
    }

    public static ImageLearningFolderImportResult ImportProjectFolder(
        string projectFolder,
        string operatorId,
        ImageLearningEvidenceMode evidenceMode,
        string projectName = "",
        string boardModel = "")
    {
        if (string.IsNullOrWhiteSpace(projectFolder))
            throw new ArgumentException("Project folder is required.", nameof(projectFolder));

        var root = Path.GetFullPath(projectFolder);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Image learning project folder was not found: {root}");

        var folderName = new DirectoryInfo(root).Name;
        var project = ImageLearningProjectService.CreateProject(
            string.IsNullOrWhiteSpace(projectName) ? $"Image learning import - {folderName}" : projectName.Trim(),
            string.IsNullOrWhiteSpace(boardModel) ? folderName : boardModel.Trim(),
            evidenceMode,
            string.IsNullOrWhiteSpace(operatorId) ? "UNKNOWN" : operatorId.Trim(),
            "Image-only learning project imported from folder convention.");

        var summaries = new List<ImageLearningRoleImportSummary>();
        var warnings = new List<string>();
        foreach (var pair in ConventionFolders)
        {
            var role = pair.Key;
            var roleFolder = Path.Combine(root, pair.Value);
            if (!Directory.Exists(roleFolder))
            {
                if (role != ImageLearningImageRole.NgValidation)
                    warnings.Add($"Expected folder missing: {pair.Value}.");
                continue;
            }

            var files = Directory
                .EnumerateFiles(roleFolder, "*", SearchOption.TopDirectoryOnly)
                .Where(path => !IsTruthFile(path))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var summary = ImportRoleFiles(
                project,
                role,
                files,
                [root, roleFolder],
                project.BoardModel,
                string.Empty,
                "Top",
                operatorId,
                []);
            summaries.Add(summary);
            warnings.AddRange(summary.Warnings);
        }

        var counts = ImageLearningProjectService.GetImageCountsByRole(project.ProjectId);
        if (counts.Values.Sum() == 0)
            warnings.Add("No supported PCB images were imported. Accepted formats are PNG, JPG, and JPEG.");

        RecordFolderImportAudit(project.ProjectId, operatorId, "FolderConvention", warnings.Count);
        return new ImageLearningFolderImportResult(project, counts, summaries, warnings, NextSuggestedCommand);
    }

    private static ImageLearningRoleImportSummary ImportRoleFiles(
        ImageLearningProject project,
        ImageLearningImageRole role,
        IReadOnlyList<string> files,
        IReadOnlyList<string> truthSearchFolders,
        string boardModel,
        string lotId,
        string viewType,
        string importedBy,
        IReadOnlyList<string> startingWarnings)
    {
        var warnings = new List<string>(startingWarnings);
        var truth = LoadTruthRecords(role, truthSearchFolders, warnings);
        var results = new List<ImageLearningImportResult>();

        foreach (var file in files)
        {
            var truthRecord = FindTruthRecord(truth, file);
            var result = ImageLearningProjectService.ImportImageFiles(
                project.ProjectId,
                role,
                [file],
                boardModel,
                lotId,
                viewType,
                importedBy,
                truthRecord?.Truth,
                truthRecord?.Notes ?? string.Empty).Single();
            results.Add(result);

            if (!result.Imported)
                warnings.Add(BuildImportWarning(role, file, result));
        }

        if (files.Count == 0)
            warnings.Add($"No image files were found for {DisplayRole(role)}.");

        var sourceFolder = truthSearchFolders.LastOrDefault(Directory.Exists) ?? string.Empty;
        return new ImageLearningRoleImportSummary(
            role,
            sourceFolder,
            files.Count,
            results.Count(result => result.Imported),
            results.Count(result => result.IsDuplicate),
            results.Count(result => string.Equals(result.Status, "Unsupported", StringComparison.OrdinalIgnoreCase)),
            results.Count(result => !result.Imported &&
                                    !result.IsDuplicate &&
                                    !string.Equals(result.Status, "Unsupported", StringComparison.OrdinalIgnoreCase)),
            warnings,
            results);
    }

    private static SelectionResult ExpandSelection(IEnumerable<string> selectedPaths)
    {
        var files = new List<string>();
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();

        foreach (var selectedPath in selectedPaths)
        {
            if (string.IsNullOrWhiteSpace(selectedPath))
                continue;

            var fullPath = Path.GetFullPath(selectedPath);
            if (File.Exists(fullPath))
            {
                if (!IsTruthFile(fullPath))
                    files.Add(fullPath);
                if (Path.GetDirectoryName(fullPath) is { } parent)
                    folders.Add(parent);
                continue;
            }

            if (Directory.Exists(fullPath))
            {
                folders.Add(fullPath);
                files.AddRange(Directory
                    .EnumerateFiles(fullPath, "*", SearchOption.TopDirectoryOnly)
                    .Where(path => !IsTruthFile(path))
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
                continue;
            }

            warnings.Add($"Selected path was skipped because it does not exist: {selectedPath}");
        }

        return new SelectionResult(files, folders.ToArray(), warnings);
    }

    private static IReadOnlyDictionary<string, TruthRecord> LoadTruthRecords(
        ImageLearningImageRole role,
        IReadOnlyList<string> searchFolders,
        List<string> warnings)
    {
        var truthFiles = searchFolders
            .Where(Directory.Exists)
            .Select(folder => Path.Combine(folder, TruthFileName))
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (truthFiles.Length == 0)
            return new Dictionary<string, TruthRecord>(StringComparer.OrdinalIgnoreCase);

        if (!IsValidationRole(role))
            return new Dictionary<string, TruthRecord>(StringComparer.OrdinalIgnoreCase);

        var records = new Dictionary<string, TruthRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var truthFile in truthFiles)
            LoadTruthFile(truthFile, records, warnings);

        return records;
    }

    private static void LoadTruthFile(
        string truthFile,
        Dictionary<string, TruthRecord> records,
        List<string> warnings)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(truthFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"image_truth.csv could not be read: {truthFile}; {ex.Message}");
            return;
        }

        if (lines.Length == 0)
            return;

        var header = ParseCsvLine(lines[0]);
        var imageIndex = FindColumn(header, "image");
        var truthIndex = FindColumn(header, "truth");
        var notesIndex = FindColumn(header, "notes");
        if (imageIndex < 0 || truthIndex < 0)
        {
            warnings.Add($"image_truth.csv ignored because it must include image and truth columns: {truthFile}");
            return;
        }

        for (var i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
                continue;

            var columns = ParseCsvLine(lines[i]);
            if (imageIndex >= columns.Count || truthIndex >= columns.Count)
            {
                warnings.Add($"image_truth.csv row {i + 1} ignored because required columns are missing.");
                continue;
            }

            var image = columns[imageIndex].Trim();
            if (string.IsNullOrWhiteSpace(image))
                continue;

            var record = new TruthRecord(
                NormalizeTruth(columns[truthIndex]),
                notesIndex >= 0 && notesIndex < columns.Count ? columns[notesIndex].Trim() : string.Empty);
            records[NormalizeImageKey(image)] = record;
            records[Path.GetFileName(image)] = record;
        }
    }

    private static TruthRecord? FindTruthRecord(IReadOnlyDictionary<string, TruthRecord> records, string file)
    {
        if (records.Count == 0)
            return null;

        if (records.TryGetValue(NormalizeImageKey(file), out var fullPathRecord))
            return fullPathRecord;
        if (records.TryGetValue(Path.GetFileName(file), out var fileNameRecord))
            return fileNameRecord;

        return null;
    }

    private static string BuildImportWarning(ImageLearningImageRole role, string file, ImageLearningImportResult result)
    {
        var fileName = Path.GetFileName(file);
        if (result.IsDuplicate && result.Image is { } duplicate)
        {
            var duplicateRole = DisplayRole(duplicate.Role);
            if (duplicate.Role == role)
                return $"Duplicate ignored: {fileName} already exists in role {duplicateRole}.";

            return $"Duplicate ignored: {fileName} already exists in role {duplicateRole}; one image can belong to only one training group in a project.";
        }

        if (string.Equals(result.Status, "Unsupported", StringComparison.OrdinalIgnoreCase))
            return $"Unsupported image skipped: {fileName}. Accepted formats are PNG, JPG, and JPEG.";
        if (string.Equals(result.Status, "Invalid", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(result.Status, "Unreadable", StringComparison.OrdinalIgnoreCase))
            return $"Unreadable image skipped: {fileName}. {result.Message}";

        return $"{result.Status}: {fileName}; {result.Message}";
    }

    private static IReadOnlyList<string> ParseCsvLine(string line)
    {
        var values = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }

                continue;
            }

            if (ch == ',' && !inQuotes)
            {
                values.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(ch);
        }

        values.Add(current.ToString());
        return values;
    }

    private static int FindColumn(IReadOnlyList<string> header, string name)
    {
        for (var i = 0; i < header.Count; i++)
        {
            if (string.Equals(header[i].Trim(), name, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    private static string NormalizeTruth(string value)
    {
        var truth = value.Trim();
        if (string.Equals(truth, "OK", StringComparison.OrdinalIgnoreCase))
            return "OK";
        if (string.Equals(truth, "NG", StringComparison.OrdinalIgnoreCase))
            return "NG";

        return "UNKNOWN";
    }

    private static string NormalizeImageKey(string value)
        => value.Trim().Replace('\\', '/');

    private static bool IsValidationRole(ImageLearningImageRole role)
        => role is ImageLearningImageRole.OkValidation or ImageLearningImageRole.NgValidation;

    private static bool IsTruthFile(string path)
        => string.Equals(Path.GetFileName(path), TruthFileName, StringComparison.OrdinalIgnoreCase);

    private static string DisplayRole(ImageLearningImageRole role)
        => role switch
        {
            ImageLearningImageRole.GoldenReference => "Golden / Reference",
            ImageLearningImageRole.OkLearning => "OK Learning",
            ImageLearningImageRole.OkValidation => "OK Validation",
            ImageLearningImageRole.Inspection => "Inspection",
            ImageLearningImageRole.NgValidation => "Optional NG Validation",
            _ => role.ToString(),
        };

    private static void RecordFolderImportAudit(string projectId, string operatorId, string importMode, int warningCount)
        => AoiDatabase.RecordAuditEvent(
            "IMAGE_LEARNING_FOLDER_IMPORT",
            $"Image-only learning folder import complete: project={projectId}; mode={importMode}; warnings={warningCount}.",
            operatorWithRole: operatorId,
            relatedEntityType: "ImageLearningProject",
            relatedEntityId: projectId);

    private sealed record SelectionResult(
        IReadOnlyList<string> Files,
        IReadOnlyList<string> SearchFolders,
        IReadOnlyList<string> Warnings);

    private sealed record TruthRecord(string Truth, string Notes);
}
