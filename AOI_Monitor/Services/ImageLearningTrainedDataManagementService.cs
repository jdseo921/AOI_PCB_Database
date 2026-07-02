using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AOI_Monitor.Data;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public static class ImageLearningTrainedDataManagementService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static TrainedDataManagementSnapshot GetSnapshot(UserRole role)
    {
        var projects = AoiDatabase.GetImageLearningProjects(includeArchived: true);
        var models = AoiDatabase.GetLearnedPcbVisualModels(1000);
        var calibrations = AoiDatabase.GetImageLearningCalibrationResults(limit: 1000);
        var exports = AoiDatabase.GetExportHistory(500)
            .Where(IsImageLearningExport)
            .ToArray();
        var active = LearnedVisualModelRegistryService.GetActiveLearnedVisualModel();

        var projectRows = BuildProjectRows(projects, models, calibrations, exports);
        var modelRows = BuildModelRows(projects, models, active?.ModelId);
        var calibrationRows = BuildCalibrationRows(calibrations);
        var exportRows = BuildExportRows(projects, exports);

        return new TrainedDataManagementSnapshot
        {
            Instructions =
            [
                "Use more OK Learning images to teach normal variation.",
                "Use OK Validation images to tune false calls.",
                "Use optional NG Validation images to estimate missed defects.",
            ],
            Projects = projectRows,
            ModelVersions = modelRows,
            CalibrationRuns = calibrationRows,
            ExportedReports = exportRows,
            ModelComparisons = BuildModelComparisons(modelRows),
            Permissions = BuildPermissions(role),
            ActiveModelDisplay = active is null
                ? "Active learned model: not set."
                : $"Active learned model: {active.ModelId} / {active.ModelVersion} / {active.ProjectName}.",
            SummaryText = $"Manage Trained Data: {projects.Count} project(s), {models.Count} learned model version(s), {calibrations.Count} calibration run(s), {exports.Length} report/export record(s).",
        };
    }

    public static ImageLearningProject OpenProject(string projectId, UserRole role)
    {
        var project = AoiDatabase.GetImageLearningProject(projectId)
            ?? throw new InvalidOperationException("Image-only learning project does not exist.");
        AiTrainingSetupService.SelectProject(project.ProjectId);
        return project;
    }

    public static ImageLearningProject RenameProject(
        string projectId,
        string newName,
        UserRole role,
        string operatorId = "UNKNOWN")
    {
        EnsureCanManage(role, "Renaming image-only learning project");
        var project = AoiDatabase.GetImageLearningProject(projectId)
            ?? throw new InvalidOperationException("Image-only learning project does not exist.");
        if (project.IsArchived)
            throw new InvalidOperationException("Archived image-only learning projects cannot be renamed.");
        if (string.IsNullOrWhiteSpace(newName))
            throw new InvalidOperationException("Project name is required.");

        AoiDatabase.UpdateImageLearningProjectMetadata(
            project.ProjectId,
            newName,
            project.BoardModel,
            project.Description,
            operatorId);

        var state = AiTrainingSetupService.LoadState();
        if (string.Equals(state.CurrentProjectId, project.ProjectId, StringComparison.OrdinalIgnoreCase))
        {
            state.ProjectName = newName.Trim();
            AiTrainingSetupService.SaveState(state);
        }

        return AoiDatabase.GetImageLearningProject(project.ProjectId) ?? project;
    }

    public static ImageLearningProject ArchiveProject(
        string projectId,
        UserRole role,
        string operatorId = "UNKNOWN",
        string reason = "Archived from Manage Trained Data.")
    {
        EnsureCanArchive(role, "Archiving image-only learning project");
        ImageLearningProjectService.ArchiveProject(projectId, operatorId, reason);
        return AoiDatabase.GetImageLearningProject(projectId)
            ?? throw new InvalidOperationException("Image-only learning project does not exist.");
    }

    public static ImageLearningProject DuplicateProject(
        string projectId,
        UserRole role,
        string operatorId = "UNKNOWN")
    {
        EnsureCanManage(role, "Duplicating image-only learning project");
        var source = AoiDatabase.GetImageLearningProject(projectId)
            ?? throw new InvalidOperationException("Image-only learning project does not exist.");
        var duplicate = ImageLearningProjectService.CreateProject(
            $"{source.ProjectName} Copy",
            source.BoardModel,
            source.EvidenceMode,
            operatorId,
            $"Duplicated from image-only learning project {source.ProjectId}.");
        var now = DateTime.UtcNow;

        foreach (var image in AoiDatabase.GetImageLearningProjectImages(source.ProjectId).OrderBy(item => item.Id))
        {
            AoiDatabase.InsertImageLearningProjectImage(new ImageLearningProjectImage
            {
                ProjectId = duplicate.ProjectId,
                Role = image.Role,
                OriginalPath = image.OriginalPath,
                VaultPath = image.VaultPath,
                FileName = image.FileName,
                Sha256 = image.Sha256,
                BoardModel = image.BoardModel,
                LotId = image.LotId,
                ViewType = image.ViewType,
                Width = image.Width,
                Height = image.Height,
                ImportedBy = string.IsNullOrWhiteSpace(operatorId) ? "UNKNOWN" : operatorId.Trim(),
                ImportedAtUtc = now,
                ImageLevelTruth = image.ImageLevelTruth,
                Notes = AppendNote(image.Notes, $"Duplicated from project {source.ProjectId}; original/vault image files were not deleted or moved."),
            });
        }

        AoiDatabase.RecordAuditEvent(
            "IMAGE_LEARNING_PROJECT_DUPLICATED",
            $"Image-only learning project duplicated: source={source.ProjectId}; duplicate={duplicate.ProjectId}; image records copied only.",
            operatorWithRole: operatorId,
            relatedEntityType: "ImageLearningProject",
            relatedEntityId: duplicate.ProjectId);
        AiTrainingSetupService.SelectProject(duplicate.ProjectId);
        return duplicate;
    }

    public static AiTrainingSetupLearningOutcome RebuildLearnedModel(
        string projectId,
        UserRole role,
        ImageOnlyPcbLearningOptions? options = null,
        string operatorId = "UNKNOWN")
    {
        EnsureCanManage(role, "Rebuilding learned PCB visual model");
        AiTrainingSetupService.SelectProject(projectId);
        return AiTrainingSetupService.RunLearning(role, options, operatorId);
    }

    public static LearnedVisualModelRegistryEntry SetModelAsActive(
        string modelId,
        UserRole role,
        string operatorId = "UNKNOWN")
        => LearnedVisualModelRegistryService.SetActiveLearnedVisualModel(modelId, role, operatorId);

    public static TrainedDataManagementExportResult ExportModelArtifacts(
        string modelId,
        UserRole role,
        string operatorId = "UNKNOWN")
    {
        EnsureCanExport(role, "Exporting learned-model artifacts");
        var model = AoiDatabase.GetLearnedPcbVisualModel(modelId)
            ?? throw new InvalidOperationException("Learned PCB visual model metadata does not exist.");
        var project = AoiDatabase.GetImageLearningProject(model.ProjectId);
        var warnings = new List<string>();
        var outputFolder = Path.Combine(
            AoiDatabase.StorageRoot,
            "exports",
            "image_learning_model_artifacts",
            SafePathPart(model.ProjectId),
            SafePathPart(model.ModelId),
            DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture));
        var artifactFolder = Path.Combine(outputFolder, "artifacts");
        Directory.CreateDirectory(artifactFolder);

        var copied = new List<object>();
        var expectedNames = ImageLearningProjectService.ExpectedArtifactFileNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var artifact in model.Artifacts.Where(item => expectedNames.Contains(item.ArtifactName)).OrderBy(item => item.ArtifactName, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(artifact.ArtifactPath) || !File.Exists(artifact.ArtifactPath))
            {
                warnings.Add($"Artifact not found: {artifact.ArtifactName}.");
                continue;
            }

            var outputName = SafePathPart(string.IsNullOrWhiteSpace(artifact.ArtifactName) ? Path.GetFileName(artifact.ArtifactPath) : artifact.ArtifactName);
            var outputPath = Path.Combine(artifactFolder, outputName);
            File.Copy(artifact.ArtifactPath, outputPath, overwrite: true);
            copied.Add(new
            {
                artifact.ArtifactName,
                SourcePath = artifact.ArtifactPath,
                OutputPath = outputPath,
                artifact.Sha256,
            });
        }

        if (copied.Count == 0)
            warnings.Add("No generated learned-model artifact files were available to export.");

        var manifestPath = Path.Combine(outputFolder, "model_artifacts_manifest.json");
        File.WriteAllText(
            manifestPath,
            JsonSerializer.Serialize(new
            {
                SchemaVersion = "1.0",
                ExportedAtUtc = DateTime.UtcNow,
                ExportType = "ImageLearningModelArtifacts",
                model.ModelId,
                model.ModelVersion,
                model.ProjectId,
                ProjectName = project?.ProjectName ?? string.Empty,
                BoundaryNote = "Image-only Stage 1 learning artifacts. Not live camera validation or production acceptance.",
                Artifacts = copied,
                Warnings = warnings,
            }, JsonOptions),
            Encoding.UTF8);

        var verified = ExportVerificationService.RecordVerifiedExport(
            "ImageLearningModelArtifacts",
            outputFolder,
            warnings.Count == 0 ? "OK" : "WARN",
            operatorId);
        AoiDatabase.RecordAuditEvent(
            "IMAGE_LEARNING_MODEL_ARTIFACTS_EXPORTED",
            $"Generated image-only learning artifacts exported: model={model.ModelId}; artifactCount={copied.Count}; warnings={warnings.Count}.",
            operatorWithRole: operatorId,
            relatedEntityType: "LearnedPcbVisualModel",
            relatedEntityId: model.ModelId,
            relatedPath: outputFolder);

        return new TrainedDataManagementExportResult(outputFolder, manifestPath, verified.ExportHistoryId, warnings);
    }

    public static ImageOnlyLearningReportResult ExportLearningReport(
        string projectId,
        string modelId,
        UserRole role,
        string operatorId = "UNKNOWN")
    {
        EnsureCanExport(role, "Exporting image-only learning report");
        var model = AoiDatabase.GetLearnedPcbVisualModel(modelId)
            ?? throw new InvalidOperationException("Learned PCB visual model metadata does not exist.");
        if (!string.Equals(model.ProjectId, projectId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The learned visual model does not belong to the selected project.");

        return ImageOnlyLearningReportService.ExportReport(projectId, modelId, operatorId: operatorId);
    }

    public static string GetArtifactFolder(string modelId)
    {
        var model = AoiDatabase.GetLearnedPcbVisualModel(modelId)
            ?? throw new InvalidOperationException("Learned PCB visual model metadata does not exist.");
        var folder = ArtifactFolder(model);
        if (string.IsNullOrWhiteSpace(folder))
            throw new InvalidOperationException("No generated artifact folder is recorded for this learned model.");
        return folder;
    }

    public static ImageLearningArtifactDeletionResult DeleteGeneratedArtifacts(
        string modelId,
        UserRole role,
        bool explicitConfirmation,
        string operatorId = "UNKNOWN",
        string reason = "Deleted from Manage Trained Data.")
    {
        EnsureCanDeleteArtifacts(role, "Deleting generated learned-model artifacts");
        return ImageLearningProjectService.DeleteGeneratedLearnedModelArtifacts(modelId, explicitConfirmation, operatorId, reason);
    }

    private static IReadOnlyList<TrainedDataProjectRow> BuildProjectRows(
        IReadOnlyList<ImageLearningProject> projects,
        IReadOnlyList<LearnedPcbVisualModel> models,
        IReadOnlyList<ImageLearningCalibrationResult> calibrations,
        IReadOnlyList<ExportHistoryRecord> exports)
    {
        var modelsByProject = models
            .GroupBy(model => model.ProjectId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var calibrationsByProject = calibrations
            .GroupBy(row => row.ProjectId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        return projects.Select(project =>
        {
            var counts = AoiDatabase.GetImageLearningImageCountsByRole(project.ProjectId);
            var projectModels = modelsByProject.GetValueOrDefault(project.ProjectId) ?? Array.Empty<LearnedPcbVisualModel>();
            var latest = projectModels.OrderByDescending(model => model.CreatedAtUtc).ThenByDescending(model => model.Id).FirstOrDefault();
            var exportCount = exports.Count(export => ExportMatchesProject(export, project.ProjectId));
            return new TrainedDataProjectRow
            {
                ProjectId = project.ProjectId,
                ProjectName = project.ProjectName,
                RenameText = project.ProjectName,
                BoardModel = project.BoardModel,
                EvidenceMode = project.EvidenceMode.ToString(),
                Status = project.IsArchived ? "Archived" : "Active",
                UpdatedAtUtc = project.UpdatedAtUtc,
                GoldenReferenceCount = counts.GetValueOrDefault(ImageLearningImageRole.GoldenReference),
                OkLearningCount = counts.GetValueOrDefault(ImageLearningImageRole.OkLearning),
                OkValidationCount = counts.GetValueOrDefault(ImageLearningImageRole.OkValidation),
                InspectionCount = counts.GetValueOrDefault(ImageLearningImageRole.Inspection),
                NgValidationCount = counts.GetValueOrDefault(ImageLearningImageRole.NgValidation),
                LearnedModelCount = projectModels.Length,
                CalibrationRunCount = calibrationsByProject.GetValueOrDefault(project.ProjectId),
                ExportedReportCount = exportCount,
                ImageGroupSummary = BuildImageGroupSummary(counts),
                MetricsSummary = latest is null
                    ? "No learned model version yet."
                    : $"Latest model {latest.ModelId}: false calls {FormatRate(latest.FalseCallRate)}, possible escapes {FormatRate(latest.PossibleEscapeRate)}, threshold {latest.LearnedThreshold:F2}.",
                IsArchived = project.IsArchived,
            };
        }).ToArray();
    }

    private static IReadOnlyList<TrainedDataModelVersionRow> BuildModelRows(
        IReadOnlyList<ImageLearningProject> projects,
        IReadOnlyList<LearnedPcbVisualModel> models,
        string? activeModelId)
    {
        var projectsById = projects.ToDictionary(project => project.ProjectId, project => project, StringComparer.OrdinalIgnoreCase);
        var versionNumbers = models
            .GroupBy(model => model.ProjectId, StringComparer.OrdinalIgnoreCase)
            .SelectMany(group => group
                .OrderBy(model => model.CreatedAtUtc)
                .ThenBy(model => model.Id)
                .Select((model, index) => new { model.ModelId, VersionNumber = index + 1 }))
            .ToDictionary(item => item.ModelId, item => item.VersionNumber, StringComparer.OrdinalIgnoreCase);

        return models
            .OrderByDescending(model => model.CreatedAtUtc)
            .ThenByDescending(model => model.Id)
            .Select(model =>
            {
                var versionNumber = versionNumbers.GetValueOrDefault(model.ModelId, 1);
                var folder = ArtifactFolder(model);
                return new TrainedDataModelVersionRow
                {
                    ModelId = model.ModelId,
                    ProjectId = model.ProjectId,
                    ProjectName = projectsById.TryGetValue(model.ProjectId, out var project) ? project.ProjectName : model.ProjectId,
                    ModelVersion = model.ModelVersion,
                    VersionDisplay = $"Version {versionNumber}: {model.ModelVersion}",
                    CreatedAtUtc = model.CreatedAtUtc,
                    ImagesLearnedFrom = model.GoldenCount + model.OkLearningCount,
                    OkValidationCount = model.OkValidationCount,
                    FalseCallRate = model.FalseCallRate,
                    PossibleEscapeRate = model.PossibleEscapeRate,
                    RecommendedThreshold = model.LearnedThreshold,
                    MetricsSummary = $"Learned from {model.GoldenCount + model.OkLearningCount} image(s); OK validation {model.OkValidationCount}; false calls {FormatRate(model.FalseCallRate)}; possible escapes {FormatRate(model.PossibleEscapeRate)}; threshold {model.LearnedThreshold:F2}.",
                    ArtifactFolder = folder,
                    ArtifactStatus = BuildArtifactStatus(model),
                    IsActive = !string.IsNullOrWhiteSpace(activeModelId) &&
                               string.Equals(model.ModelId, activeModelId, StringComparison.OrdinalIgnoreCase),
                };
            })
            .ToArray();
    }

    private static IReadOnlyList<TrainedDataCalibrationRow> BuildCalibrationRows(IReadOnlyList<ImageLearningCalibrationResult> calibrations)
        => calibrations.Select(row => new TrainedDataCalibrationRow
        {
            CalibrationId = row.CalibrationId,
            ProjectId = row.ProjectId,
            ModelId = row.ModelId,
            CreatedAtUtc = row.CreatedAtUtc,
            OkValidationCount = row.OkValidationCount,
            NgValidationCount = row.NgValidationCount,
            FalseCallRateDisplay = FormatRate(row.FalseCallRate),
            PossibleEscapeRateDisplay = row.NgValidationCount > 0 ? FormatRate(row.PossibleEscapeRate) : "NG validation not provided",
            RecommendedThresholdDisplay = row.LearnedThreshold.ToString("F2", CultureInfo.InvariantCulture),
            Status = row.Status,
            Summary = row.Summary,
        }).ToArray();

    private static IReadOnlyList<TrainedDataExportRow> BuildExportRows(
        IReadOnlyList<ImageLearningProject> projects,
        IReadOnlyList<ExportHistoryRecord> exports)
        => exports.Select(row => new TrainedDataExportRow
        {
            ExportHistoryId = row.Id,
            CreatedAtUtc = row.CreatedAtUtc,
            ExportType = row.ExportType,
            ProjectId = ResolveProjectIdFromExportPath(row.FilePath, projects),
            FilePath = row.FilePath,
            Status = row.Status,
            OperatorId = row.OperatorId,
        }).ToArray();

    private static IReadOnlyList<TrainedDataModelComparisonRow> BuildModelComparisons(IReadOnlyList<TrainedDataModelVersionRow> modelRows)
        => modelRows
            .OrderBy(row => row.ProjectId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.FalseCallRate)
            .ThenBy(row => row.PossibleEscapeRate)
            .Select(row => new TrainedDataModelComparisonRow
            {
                ProjectId = row.ProjectId,
                ModelId = row.ModelId,
                VersionDisplay = row.VersionDisplay,
                FalseCallRateDisplay = FormatRate(row.FalseCallRate),
                PossibleEscapeRateDisplay = FormatRate(row.PossibleEscapeRate),
                ComparisonSummary = $"False-call rate {FormatRate(row.FalseCallRate)}; possible escapes {FormatRate(row.PossibleEscapeRate)}; recommended threshold {row.RecommendedThreshold:F2}.",
                IsActive = row.IsActive,
            })
            .ToArray();

    private static TrainedDataManagementActionPermissions BuildPermissions(UserRole role)
        => new()
        {
            CanOpenProject = true,
            CanRenameProject = RoleAuthorization.CanManageImageLearningTrainedData(role),
            CanArchiveProject = RoleAuthorization.CanArchiveImageLearningProjects(role),
            CanDuplicateProject = RoleAuthorization.CanManageImageLearningTrainedData(role),
            CanRebuildModel = RoleAuthorization.CanManageImageLearningTrainedData(role),
            CanSetActiveModel = RoleAuthorization.CanSetActiveLearnedVisualModel(role),
            CanExportModelArtifacts = RoleAuthorization.CanExportImageLearningReports(role),
            CanExportLearningReport = RoleAuthorization.CanExportImageLearningReports(role),
            CanOpenArtifactFolder = true,
            CanDeleteGeneratedArtifacts = RoleAuthorization.CanDeleteImageLearningArtifacts(role),
        };

    private static string BuildImageGroupSummary(IReadOnlyDictionary<ImageLearningImageRole, int> counts)
        => $"Golden {counts.GetValueOrDefault(ImageLearningImageRole.GoldenReference)} / OK Learning {counts.GetValueOrDefault(ImageLearningImageRole.OkLearning)} / OK Validation {counts.GetValueOrDefault(ImageLearningImageRole.OkValidation)} / Inspection {counts.GetValueOrDefault(ImageLearningImageRole.Inspection)} / NG Validation {counts.GetValueOrDefault(ImageLearningImageRole.NgValidation)}.";

    private static string BuildArtifactStatus(LearnedPcbVisualModel model)
    {
        var expected = ImageLearningProjectService.ExpectedArtifactFileNames;
        var present = expected.Count(name => model.Artifacts.Any(artifact =>
            string.Equals(artifact.ArtifactName, name, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(artifact.ArtifactPath) &&
            File.Exists(artifact.ArtifactPath)));
        return present == expected.Count
            ? $"Generated artifacts ready ({present}/{expected.Count})."
            : $"Generated artifacts incomplete ({present}/{expected.Count}).";
    }

    private static string ArtifactFolder(LearnedPcbVisualModel model)
        => model.Artifacts
               .Select(artifact => string.IsNullOrWhiteSpace(artifact.ArtifactPath) ? string.Empty : Path.GetDirectoryName(artifact.ArtifactPath) ?? string.Empty)
               .FirstOrDefault(folder => !string.IsNullOrWhiteSpace(folder)) ?? string.Empty;

    private static bool IsImageLearningExport(ExportHistoryRecord row)
        => row.ExportType.Contains("ImageLearning", StringComparison.OrdinalIgnoreCase) ||
           row.ExportType.Contains("ImageOnlyLearning", StringComparison.OrdinalIgnoreCase) ||
           row.FilePath.Contains("image_learning", StringComparison.OrdinalIgnoreCase) ||
           row.FilePath.Contains("image_only_learning", StringComparison.OrdinalIgnoreCase);

    private static bool ExportMatchesProject(ExportHistoryRecord row, string projectId)
        => row.FilePath.Contains(projectId, StringComparison.OrdinalIgnoreCase);

    private static string ResolveProjectIdFromExportPath(string exportPath, IReadOnlyList<ImageLearningProject> projects)
        => projects.FirstOrDefault(project => exportPath.Contains(project.ProjectId, StringComparison.OrdinalIgnoreCase))?.ProjectId
           ?? string.Empty;

    private static string AppendNote(string existing, string note)
        => string.IsNullOrWhiteSpace(existing) ? note : $"{existing.Trim()} {note}";

    private static string FormatRate(double value)
        => value.ToString("P1", CultureInfo.InvariantCulture);

    private static string SafePathPart(string value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "image_learning" : value.Trim();
        foreach (var ch in Path.GetInvalidFileNameChars().Concat(Path.GetInvalidPathChars()).Distinct())
            text = text.Replace(ch, '_');

        return text.Replace(' ', '_');
    }

    private static void EnsureCanManage(UserRole role, string action)
    {
        if (!RoleAuthorization.CanManageImageLearningTrainedData(role))
            throw new UnauthorizedAccessException(RoleAuthorization.DeniedMessage(role, action));
    }

    private static void EnsureCanArchive(UserRole role, string action)
    {
        if (!RoleAuthorization.CanArchiveImageLearningProjects(role))
            throw new UnauthorizedAccessException(RoleAuthorization.DeniedMessage(role, action));
    }

    private static void EnsureCanExport(UserRole role, string action)
    {
        if (!RoleAuthorization.CanExportImageLearningReports(role))
            throw new UnauthorizedAccessException(RoleAuthorization.DeniedMessage(role, action));
    }

    private static void EnsureCanDeleteArtifacts(UserRole role, string action)
    {
        if (!RoleAuthorization.CanDeleteImageLearningArtifacts(role))
            throw new UnauthorizedAccessException(RoleAuthorization.DeniedMessage(role, action));
    }
}
