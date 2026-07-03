using System.Globalization;
using System.IO;
using System.Windows.Media.Imaging;
using AOI_Monitor.Data;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public static class ImageLearningProjectService
{
    public static readonly IReadOnlyList<string> ExpectedArtifactFileNames =
    [
        "learned_reference.png",
        "tolerance_map.png",
        "anomaly_threshold_map.png",
        "learning_summary.json",
        "alignment_summary.csv",
        "threshold_sweep.csv",
    ];

    private static readonly HashSet<string> SupportedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg",
    };

    public static ImageLearningProject CreateProject(
        string projectName,
        string boardModel,
        ImageLearningEvidenceMode evidenceMode = ImageLearningEvidenceMode.CustomerData,
        string createdBy = "UNKNOWN",
        string description = "")
        => CreateProject(new ImageLearningProject
        {
            ProjectName = projectName,
            BoardModel = boardModel,
            EvidenceMode = evidenceMode,
            CreatedBy = createdBy,
            Description = description,
        });

    public static ImageLearningProject CreateProject(ImageLearningProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        AoiDatabase.CreateImageLearningProject(project);
        return AoiDatabase.GetImageLearningProject(project.ProjectId) ?? project;
    }

    public static IReadOnlyList<ImageLearningImportResult> ImportImageFiles(
        string projectId,
        ImageLearningImageRole role,
        IEnumerable<string> imagePaths,
        string boardModel = "",
        string lotId = "",
        string viewType = "Top",
        string importedBy = "UNKNOWN",
        string? imageLevelTruth = null,
        string notes = "")
    {
        ArgumentNullException.ThrowIfNull(imagePaths);

        var project = AoiDatabase.GetImageLearningProject(projectId)
            ?? throw new InvalidOperationException("Image-only learning project does not exist.");
        if (project.IsArchived)
            throw new InvalidOperationException("Archived image-only learning projects cannot import new images.");

        var results = new List<ImageLearningImportResult>();
        foreach (var imagePath in imagePaths)
        {
            results.Add(ImportSingleImage(
                project,
                role,
                imagePath,
                boardModel,
                lotId,
                viewType,
                importedBy,
                imageLevelTruth,
                notes));
        }

        return results;
    }

    public static IReadOnlyDictionary<ImageLearningImageRole, int> GetImageCountsByRole(string projectId)
        => AoiDatabase.GetImageLearningImageCountsByRole(projectId);

    public static ImageLearningProjectValidationResult ValidateMinimumProjectRequirements(string projectId)
    {
        var counts = AoiDatabase.GetImageLearningImageCountsByRole(projectId);
        var goldenCount = counts.GetValueOrDefault(ImageLearningImageRole.GoldenReference);
        var okLearningCount = counts.GetValueOrDefault(ImageLearningImageRole.OkLearning);
        var canTrain = goldenCount >= 1 || okLearningCount >= 5;
        var blockers = new List<string>();
        if (!canTrain)
            blockers.Add("Training requires at least one Golden / Reference image or at least 5 OK Learning images.");

        return new ImageLearningProjectValidationResult(canTrain, goldenCount, okLearningCount, counts, blockers);
    }

    public static IReadOnlyList<ImageLearningProjectImage> GetProjectImages(string projectId, ImageLearningImageRole? role = null)
        => AoiDatabase.GetImageLearningProjectImages(projectId, role);

    public static IReadOnlyList<ImageLearningProject> ListProjects(bool includeArchived = false)
        => AoiDatabase.GetImageLearningProjects(includeArchived);

    public static LearnedPcbVisualModel SaveModelMetadata(LearnedPcbVisualModel model)
    {
        AoiDatabase.RecordLearnedPcbVisualModel(model);
        return AoiDatabase.GetLearnedPcbVisualModel(model.ModelId) ?? model;
    }

    public static IReadOnlyList<LearnedPcbVisualModelArtifact> CreateExpectedArtifactRecords(string modelId, string artifactFolder)
        => ExpectedArtifactFileNames
            .Select(name => new LearnedPcbVisualModelArtifact
            {
                ModelId = modelId,
                ArtifactName = name,
                ArtifactPath = Path.Combine(artifactFolder, name),
            })
            .ToArray();

    public static void ArchiveProject(string projectId, string archivedBy = "UNKNOWN", string reason = "")
        => AoiDatabase.ArchiveImageLearningProject(projectId, archivedBy, reason);

    public static void DeleteProjectMetadata(string projectId, string deletedBy = "UNKNOWN", string reason = "")
        => AoiDatabase.DeleteImageLearningProjectMetadata(projectId, deletedBy, reason);

    public static ImageLearningArtifactDeletionResult DeleteGeneratedLearnedModelArtifacts(
        string modelId,
        bool explicitConfirmation,
        string deletedBy = "UNKNOWN",
        string reason = "")
    {
        if (!explicitConfirmation)
            throw new InvalidOperationException("Explicit confirmation is required before deleting generated learned-model artifacts.");

        var model = AoiDatabase.GetLearnedPcbVisualModel(modelId)
            ?? throw new InvalidOperationException("Learned PCB visual model metadata does not exist.");
        var expectedNames = new HashSet<string>(ExpectedArtifactFileNames, StringComparer.OrdinalIgnoreCase);
        var generatedArtifacts = model.Artifacts
            .Where(artifact => expectedNames.Contains(artifact.ArtifactName))
            .ToArray();
        var deletedPaths = new List<string>();
        var missingPaths = new List<string>();

        foreach (var artifact in generatedArtifacts)
        {
            if (string.IsNullOrWhiteSpace(artifact.ArtifactPath))
            {
                missingPaths.Add($"{artifact.ArtifactName}: <empty path>");
                continue;
            }

            var path = Path.GetFullPath(artifact.ArtifactPath);
            if (!File.Exists(path))
            {
                missingPaths.Add(path);
                continue;
            }

            try
            {
                File.Delete(path);
                deletedPaths.Add(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException($"Generated image-learning artifact could not be deleted: {path}", ex);
            }
        }

        var recordsDeleted = AoiDatabase.DeleteLearnedPcbVisualModelArtifactRecords(model.ModelId, expectedNames);
        AoiDatabase.RecordAuditEvent(
            "IMAGE_LEARNING_MODEL_ARTIFACTS_DELETED",
            $"Generated image-only learning artifacts deleted: model={model.ModelId}; filesDeleted={deletedPaths.Count}; missingFiles={missingPaths.Count}; imported project images were not deleted; reason={reason}.",
            operatorWithRole: string.IsNullOrWhiteSpace(deletedBy) ? "UNKNOWN" : deletedBy.Trim(),
            relatedEntityType: "LearnedPcbVisualModel",
            relatedEntityId: model.ModelId,
            relatedPath: deletedPaths.FirstOrDefault() ?? missingPaths.FirstOrDefault() ?? string.Empty);

        return new ImageLearningArtifactDeletionResult(
            model.ModelId,
            recordsDeleted,
            deletedPaths.Count,
            missingPaths.Count,
            deletedPaths,
            missingPaths);
    }

    public static VerifiedExportRecord RecordReportExport(
        string projectId,
        string reportPath,
        string status = "OK",
        string operatorId = "UNKNOWN")
    {
        var verified = ExportVerificationService.RecordVerifiedExport("ImageLearningReport", reportPath, status, operatorId);
        AoiDatabase.RecordAuditEvent(
            "IMAGE_LEARNING_REPORT_EXPORT",
            $"Image-only learning report exported: project={projectId}; status={verified.ExportStatus}; path={reportPath}.",
            operatorWithRole: operatorId,
            relatedEntityType: "ImageLearningProject",
            relatedEntityId: projectId,
            relatedPath: reportPath);
        return verified;
    }

    private static ImageLearningImportResult ImportSingleImage(
        ImageLearningProject project,
        ImageLearningImageRole role,
        string sourcePath,
        string boardModel,
        string lotId,
        string viewType,
        string importedBy,
        string? imageLevelTruth,
        string notes)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            return new ImageLearningImportResult(null, false, false, "Missing", "Image file does not exist.");

        var extension = Path.GetExtension(sourcePath);
        if (!SupportedImageExtensions.Contains(extension))
            return new ImageLearningImportResult(null, false, false, "Unsupported", "Only PNG, JPG, and JPEG images are supported.");

        var fullPath = Path.GetFullPath(sourcePath);
        string hash;
        try
        {
            hash = HashUtil.ComputeSha256(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ImageLearningImportResult(null, false, false, "Unreadable", ex.Message);
        }

        if (AoiDatabase.GetImageLearningProjectImageByHash(project.ProjectId, hash) is { } duplicate)
        {
            var duplicateMessage = duplicate.Role == role
                ? "Duplicate ignored: image already exists in this role."
                : $"Duplicate ignored: image already exists in role {duplicate.Role}.";
            AoiDatabase.RecordAuditEvent(
                "IMAGE_LEARNING_IMAGE_DUPLICATE",
                $"Duplicate image-only learning import skipped: project={project.ProjectId}; role={role}; file={Path.GetFileName(sourcePath)}.",
                operatorWithRole: importedBy,
                relatedEntityType: "ImageLearningProjectImage",
                relatedEntityId: duplicate.Id.ToString(CultureInfo.InvariantCulture),
                relatedPath: duplicate.VaultPath);
            return new ImageLearningImportResult(duplicate, false, true, "Duplicate", duplicateMessage);
        }

        ImageDimensions dimensions;
        try
        {
            dimensions = ReadImageDimensions(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or FileFormatException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            return new ImageLearningImportResult(null, false, false, "Invalid", ex.Message);
        }

        var importedAtUtc = DateTime.UtcNow;
        var vaultPath = CopyToLearningVault(fullPath, project.ProjectId, role, importedAtUtc);
        var image = new ImageLearningProjectImage
        {
            ProjectId = project.ProjectId,
            Role = role,
            OriginalPath = fullPath,
            VaultPath = vaultPath,
            FileName = Path.GetFileName(fullPath),
            Sha256 = hash,
            BoardModel = string.IsNullOrWhiteSpace(boardModel) ? project.BoardModel : boardModel.Trim(),
            LotId = lotId?.Trim() ?? string.Empty,
            ViewType = string.IsNullOrWhiteSpace(viewType) ? "Top" : viewType.Trim(),
            Width = dimensions.Width,
            Height = dimensions.Height,
            ImportedBy = string.IsNullOrWhiteSpace(importedBy) ? "UNKNOWN" : importedBy.Trim(),
            ImportedAtUtc = importedAtUtc,
            ImageLevelTruth = ResolveImageLevelTruth(role, imageLevelTruth),
            Notes = notes?.Trim() ?? string.Empty,
        };

        var saved = AoiDatabase.InsertImageLearningProjectImage(image);
        return new ImageLearningImportResult(saved, true, false, "Imported", "Image copied into the image-only learning vault.");
    }

    private static string CopyToLearningVault(string sourcePath, string projectId, ImageLearningImageRole role, DateTime importedAtUtc)
    {
        var folder = Path.Combine(AoiDatabase.ImageVaultPath, "image_learning", SafePathPart(projectId), role.ToString());
        Directory.CreateDirectory(folder);

        var fileName = Path.GetFileName(sourcePath);
        var candidate = Path.Combine(folder, $"{importedAtUtc:yyyyMMdd_HHmmss_fff}_{SafePathPart(fileName)}");
        if (!File.Exists(candidate))
        {
            File.Copy(sourcePath, candidate, overwrite: false);
            return candidate;
        }

        var fallback = Path.Combine(folder, $"{importedAtUtc:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}_{SafePathPart(fileName)}");
        File.Copy(sourcePath, fallback, overwrite: false);
        return fallback;
    }

    private static ImageDimensions ReadImageDimensions(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0)
            throw new InvalidDataException("Image decoder found no frames.");

        var frame = decoder.Frames[0];
        return new ImageDimensions(frame.PixelWidth, frame.PixelHeight);
    }

    private static string ResolveImageLevelTruth(ImageLearningImageRole role, string? truth)
    {
        if (!string.IsNullOrWhiteSpace(truth))
            return NormalizeTruth(truth);

        return role switch
        {
            ImageLearningImageRole.GoldenReference or
            ImageLearningImageRole.OkLearning or
            ImageLearningImageRole.OkValidation => "OK",
            ImageLearningImageRole.NgValidation => "NG",
            _ => "UNKNOWN",
        };
    }

    private static string NormalizeTruth(string truth)
    {
        if (string.Equals(truth.Trim(), "OK", StringComparison.OrdinalIgnoreCase))
            return "OK";
        if (string.Equals(truth.Trim(), "NG", StringComparison.OrdinalIgnoreCase))
            return "NG";

        return "UNKNOWN";
    }

    private static string SafePathPart(string value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "image" : value.Trim();
        foreach (var ch in Path.GetInvalidFileNameChars().Concat(Path.GetInvalidPathChars()).Distinct())
            text = text.Replace(ch, '_');

        return text;
    }

    private readonly record struct ImageDimensions(int Width, int Height);
}
