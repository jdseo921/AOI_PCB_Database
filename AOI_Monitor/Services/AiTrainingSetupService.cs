using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using AOI_Monitor.Data;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public static class AiTrainingSetupService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private static string SettingsPath => Path.Combine(AoiDatabase.StorageRoot, "ai_training_setup_state.json");

    public static AiTrainingSetupState LoadState()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AiTrainingSetupState>(File.ReadAllText(SettingsPath), JsonOptions) ?? new AiTrainingSetupState();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            System.Diagnostics.Trace.WriteLine($"AI training setup state could not be loaded: {ex.Message}");
        }

        return new AiTrainingSetupState();
    }

    public static void SaveState(AiTrainingSetupState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Directory.CreateDirectory(AoiDatabase.StorageRoot);
        state.UpdatedAtUtc = DateTime.UtcNow;
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(state, JsonOptions), Encoding.UTF8);
    }

    public static void ResetForTests()
    {
        try
        {
            if (File.Exists(SettingsPath))
                File.Delete(SettingsPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.WriteLine($"AI training setup test state reset skipped: {ex.Message}");
        }
    }

    public static ImageLearningProject CreateTrainingProject(
        string projectName,
        string boardModel,
        UserRole role,
        string createdBy = "UNKNOWN")
    {
        EnsureCanRunLearning(role, "Creating an AI Training Setup project");
        var project = ImageLearningProjectService.CreateProject(
            string.IsNullOrWhiteSpace(projectName) ? "AI Training Setup" : projectName.Trim(),
            string.IsNullOrWhiteSpace(boardModel) ? WorkflowState.Instance.BoardProgram : boardModel.Trim(),
            ImageLearningEvidenceMode.CustomerData,
            createdBy,
            "Image-only Stage 1 learning project created from AI Training Setup.");
        SelectProject(project.ProjectId);
        return project;
    }

    public static void SelectProject(
        string projectId,
        IReadOnlyDictionary<ImageLearningImageRole, string>? roleFolders = null)
    {
        var project = AoiDatabase.GetImageLearningProject(projectId)
            ?? throw new InvalidOperationException("Image-only learning project does not exist.");
        var state = LoadState();
        state.CurrentProjectId = project.ProjectId;
        state.CurrentModelId = string.Empty;
        state.ProjectName = project.ProjectName;
        state.BoardModel = project.BoardModel;
        state.FalseCallsBeforeLearning = null;
        state.FalseCallsAfterLearning = null;
        state.FalseCallRateBeforeLearning = null;
        state.FalseCallRateAfterLearning = null;
        state.PossibleEscapeCount = null;
        state.PossibleEscapeRate = null;
        state.RecommendedThreshold = null;
        state.InspectionComplete = false;
        state.ReportExported = false;
        state.LastReportPath = string.Empty;
        state.LastFalseCallComparisonReportPath = string.Empty;
        state.LastVisualEvidenceExportPath = string.Empty;
        state.LastRoleFolders.Clear();
        if (roleFolders is not null)
        {
            foreach (var pair in roleFolders)
            {
                if (!string.IsNullOrWhiteSpace(pair.Value))
                    state.LastRoleFolders[pair.Key] = pair.Value;
            }
        }

        SaveState(state);
    }

    public static IReadOnlyList<ImageLearningImportResult> ImportImages(
        ImageLearningImageRole role,
        IReadOnlyList<string> imagePaths,
        UserRole userRole,
        string importedBy = "UNKNOWN")
    {
        EnsureCanImport(userRole, "Importing AI Training Setup images");
        ArgumentNullException.ThrowIfNull(imagePaths);
        var state = LoadState();
        if (string.IsNullOrWhiteSpace(state.CurrentProjectId))
            throw new InvalidOperationException("Create a training project before adding images.");

        var result = ImageLearningFolderImportService.ImportGuidedSelection(
            state.CurrentProjectId,
            role,
            imagePaths,
            boardModel: state.BoardModel,
            viewType: "Top",
            importedBy: importedBy);
        var roleSummary = result.RoleSummaries.FirstOrDefault(summary => summary.Role == role);
        if (roleSummary is not null && !string.IsNullOrWhiteSpace(roleSummary.SourceFolder))
        {
            state.LastRoleFolders[role] = roleSummary.SourceFolder;
        }

        state.ReportExported = false;
        SaveState(state);
        return result.RoleSummaries.SelectMany(summary => summary.Results).ToArray();
    }

    public static ImageLearningFolderImportResult ImportProjectFolder(
        string projectFolder,
        UserRole userRole,
        string operatorId,
        ImageLearningEvidenceMode evidenceMode = ImageLearningEvidenceMode.CustomerData)
    {
        EnsureCanImport(userRole, "Importing AI Training Setup project folder");
        var result = ImageLearningFolderImportService.ImportProjectFolder(projectFolder, operatorId, evidenceMode);
        SelectProject(
            result.Project.ProjectId,
            result.RoleSummaries.ToDictionary(summary => summary.Role, summary => summary.SourceFolder));
        return result;
    }

    public static AiTrainingSetupLearningOutcome RunLearning(
        UserRole role,
        ImageOnlyPcbLearningOptions? options = null,
        string createdBy = "UNKNOWN")
    {
        EnsureCanRunLearning(role, "Learning normal PCB appearance");
        var state = LoadState();
        if (string.IsNullOrWhiteSpace(state.CurrentProjectId))
            throw new InvalidOperationException("Create a training project before learning.");

        var validation = ImageLearningProjectService.ValidateMinimumProjectRequirements(state.CurrentProjectId);
        if (!validation.CanTrain)
            throw new InvalidOperationException(string.Join(" ", validation.BlockingIssues));

        var result = ImageOnlyPcbLearningService.TrainProject(state.CurrentProjectId, options, createdBy);
        var comparison = ImageLearningFalseCallComparisonService.RunComparison(
            state.CurrentProjectId,
            result.Model.ModelId,
            options,
            createdBy);
        state.CurrentModelId = result.Model.ModelId;
        state.FalseCallsBeforeLearning = comparison.Metrics.FalseCallsBeforeLearning;
        state.FalseCallsAfterLearning = comparison.Metrics.FalseCallsAfterLearning;
        state.FalseCallRateBeforeLearning = comparison.Metrics.OkValidationCount > 0
            ? comparison.Metrics.FalseCallRateBeforeLearning
            : null;
        state.FalseCallRateAfterLearning = comparison.Metrics.OkValidationCount > 0
            ? comparison.Metrics.FalseCallRateAfterLearning
            : null;
        state.PossibleEscapeCount = comparison.Metrics.NgValidationCount > 0
            ? comparison.Metrics.PossibleEscapes
            : null;
        state.PossibleEscapeRate = comparison.Metrics.PossibleEscapeRate;
        state.RecommendedThreshold = comparison.Metrics.RecommendedThreshold;
        state.LastFalseCallComparisonReportPath = comparison.HtmlReportPath;
        state.InspectionComplete = false;
        state.ReportExported = false;
        SaveState(state);
        return new AiTrainingSetupLearningOutcome(result, GetSnapshot(role));
    }

    public static AiTrainingSetupInspectionOutcome InspectSamples(
        UserRole role,
        ImageOnlyPcbLearningOptions? options = null,
        string operatorId = "UNKNOWN")
    {
        EnsureCanRunLearning(role, "Inspecting AI Training Setup samples");
        var state = LoadState();
        if (string.IsNullOrWhiteSpace(state.CurrentProjectId) || string.IsNullOrWhiteSpace(state.CurrentModelId))
            throw new InvalidOperationException("Learn a PCB visual model before inspecting samples.");

        var images = ImageLearningProjectService.GetProjectImages(state.CurrentProjectId, ImageLearningImageRole.Inspection);
        var completed = 0;
        var reviewOrNg = 0;
        foreach (var image in images)
        {
            var result = ImageOnlyPcbLearningService.InspectProjectImage(state.CurrentModelId, image, options, operatorId);
            completed++;
            if (!string.Equals(result.InspectionResult.Verdict, "OK", StringComparison.OrdinalIgnoreCase))
                reviewOrNg++;
        }

        state.InspectionComplete = completed > 0;
        SaveState(state);
        return new AiTrainingSetupInspectionOutcome(images.Count, completed, reviewOrNg, GetSnapshot(role));
    }

    public static string ExportClientLearningReport(UserRole role, string operatorId = "UNKNOWN")
    {
        if (!RoleAuthorization.CanExportImageLearningReports(role))
            throw new UnauthorizedAccessException(RoleAuthorization.DeniedMessage(role, "Exporting AI Training Setup learning report"));

        var snapshot = GetSnapshot(role);
        if (snapshot.Project is null)
            throw new InvalidOperationException("Create a training project before exporting a learning report.");
        if (snapshot.Model is null)
            throw new InvalidOperationException("Learn a PCB visual model before exporting a visual learning report.");

        var report = ImageOnlyLearningReportService.ExportReport(
            snapshot.Project.ProjectId,
            snapshot.Model.ModelId,
            operatorId: operatorId);

        var state = LoadState();
        state.ReportExported = true;
        state.LastReportPath = report.HtmlPath;
        state.LastFalseCallComparisonReportPath = report.FalseCallComparisonHtmlPath;
        state.LastVisualEvidenceExportPath = report.VisualEvidenceManifestPath;
        SaveState(state);
        return report.HtmlPath;
    }

    public static ImageLearningOverlayExportResult ExportVisualEvidence(UserRole role, string operatorId = "UNKNOWN")
    {
        if (!RoleAuthorization.CanExportImageLearningReports(role))
            throw new UnauthorizedAccessException(RoleAuthorization.DeniedMessage(role, "Exporting AI Training Setup visual evidence"));

        var state = LoadState();
        if (string.IsNullOrWhiteSpace(state.CurrentProjectId) || string.IsNullOrWhiteSpace(state.CurrentModelId))
            throw new InvalidOperationException("Learn a PCB visual model before exporting visual evidence.");

        var result = ImageLearningOverlayExportService.ExportProjectVisualEvidence(
            state.CurrentProjectId,
            state.CurrentModelId,
            operatorId: operatorId);
        state.LastVisualEvidenceExportPath = result.ManifestPath;
        SaveState(state);
        return result;
    }

    public static AiTrainingSetupSnapshot GetSnapshot(UserRole role)
    {
        var state = LoadState();
        var project = string.IsNullOrWhiteSpace(state.CurrentProjectId)
            ? null
            : AoiDatabase.GetImageLearningProject(state.CurrentProjectId);
        var model = string.IsNullOrWhiteSpace(state.CurrentModelId)
            ? null
            : AoiDatabase.GetLearnedPcbVisualModel(state.CurrentModelId);
        var counts = project is null
            ? Enum.GetValues<ImageLearningImageRole>().ToDictionary(item => item, _ => 0)
            : ImageLearningProjectService.GetImageCountsByRole(project.ProjectId).ToDictionary();
        var validation = project is null
            ? null
            : ImageLearningProjectService.ValidateMinimumProjectRequirements(project.ProjectId);
        var canEdit = RoleAuthorization.CanImportImageLearningImages(role);
        var canLearn = RoleAuthorization.CanRunImageOnlyLearning(role) && validation?.CanTrain == true;
        var okValidationCount = counts.GetValueOrDefault(ImageLearningImageRole.OkValidation);
        var inspectionCount = counts.GetValueOrDefault(ImageLearningImageRole.Inspection);
        var canCalibrate = canLearn && okValidationCount > 0;
        var canInspect = RoleAuthorization.CanRunImageOnlyLearning(role) && model is not null && inspectionCount > 0;
        var canExport = RoleAuthorization.CanExportImageLearningReports(role) && project is not null && model is not null;
        var canExportVisualEvidence = RoleAuthorization.CanExportImageLearningReports(role) && project is not null && model is not null;
        var canSetActiveInspectionModel = RoleAuthorization.CanSetActiveLearnedVisualModel(role) && model is not null;

        return new AiTrainingSetupSnapshot
        {
            State = state,
            Project = project,
            Model = model,
            RoleCards = BuildRoleCards(counts, state, canEdit),
            MetricCards = BuildMetricCards(counts, state, model),
            Timeline = BuildTimeline(counts, state, model),
            CanCreateProject = RoleAuthorization.CanRunImageOnlyLearning(role),
            CanImportImages = canEdit && project is not null,
            CanRunLearning = canLearn,
            CanRunCalibration = canCalibrate,
            CanRunInspection = canInspect,
            CanExportReport = canExport,
            CanExportVisualEvidence = canExportVisualEvidence,
            CanSetActiveInspectionModel = canSetActiveInspectionModel,
            Guidance = BuildGuidance(project, validation, counts),
            RoleRestrictionText = RoleAuthorization.CanRunImageOnlyLearning(role)
                ? "Engineer/Admin mode: image import, learning, calibration, inspection, and report export are enabled."
                : "Operator mode: view only. Engineer/Admin role is required to import images or run learning.",
            ActiveInspectionModelDisplay = BuildActiveInspectionModelDisplay(model),
            LearnedReferencePath = ArtifactPath(model, "learned_reference.png"),
            ToleranceMapPath = ArtifactPath(model, "tolerance_map.png"),
            AnomalyHeatmapPath = ArtifactPath(model, "anomaly_threshold_map.png"),
            FalseCallSummaryText = BuildFalseCallSummary(state),
            PossibleEscapeSummaryText = BuildPossibleEscapeSummary(state),
            RecommendedThresholdSummaryText = BuildRecommendedThresholdSummary(state),
        };
    }

    public static string? FirstImagePath(ImageLearningImageRole role)
    {
        var state = LoadState();
        if (string.IsNullOrWhiteSpace(state.CurrentProjectId))
            return null;

        return ImageLearningProjectService.GetProjectImages(state.CurrentProjectId, role)
            .Select(image => string.IsNullOrWhiteSpace(image.VaultPath) ? image.OriginalPath : image.VaultPath)
            .FirstOrDefault(File.Exists);
    }

    public static string? RoleFolder(ImageLearningImageRole role)
    {
        var state = LoadState();
        if (state.LastRoleFolders.TryGetValue(role, out var folder) && Directory.Exists(folder))
            return folder;

        if (string.IsNullOrWhiteSpace(state.CurrentProjectId))
            return null;

        var path = ImageLearningProjectService.GetProjectImages(state.CurrentProjectId, role)
            .Select(image => Path.GetDirectoryName(string.IsNullOrWhiteSpace(image.VaultPath) ? image.OriginalPath : image.VaultPath))
            .FirstOrDefault(Directory.Exists);
        return path;
    }

    public static LearnedVisualModelRegistryEntry SetCurrentModelAsActive(UserRole role, string operatorId = "UNKNOWN")
    {
        var state = LoadState();
        if (string.IsNullOrWhiteSpace(state.CurrentModelId))
            throw new InvalidOperationException("Learn a PCB visual model before setting it active for inspection.");

        return LearnedVisualModelRegistryService.SetActiveLearnedVisualModel(state.CurrentModelId, role, operatorId);
    }

    private static IReadOnlyList<AiTrainingRoleCard> BuildRoleCards(
        IReadOnlyDictionary<ImageLearningImageRole, int> counts,
        AiTrainingSetupState state,
        bool canEdit)
    {
        AiTrainingRoleCard RoleCard(ImageLearningImageRole role, string title, string explanation, bool optional)
            => new(
                role,
                title,
                explanation,
                counts.GetValueOrDefault(role),
                state.LastRoleFolders.GetValueOrDefault(role) ?? string.Empty,
                optional,
                canEdit);

        return
        [
            RoleCard(ImageLearningImageRole.GoldenReference, "Golden / Reference", "Best known reference boards used as the starting visual example.", false),
            RoleCard(ImageLearningImageRole.OkLearning, "OK Learning", "Good board images used to learn normal lighting, position, and visual variation.", false),
            RoleCard(ImageLearningImageRole.OkValidation, "OK Validation", "Good board images used to calibrate tolerance and reduce false calls.", false),
            RoleCard(ImageLearningImageRole.Inspection, "Inspection", "New board images to inspect after the model learns normal appearance.", false),
            RoleCard(ImageLearningImageRole.NgValidation, "Optional NG Validation", "Known-bad images used only to estimate possible missed defects.", true),
        ];
    }

    private static IReadOnlyList<AiTrainingMetricCard> BuildMetricCards(
        IReadOnlyDictionary<ImageLearningImageRole, int> counts,
        AiTrainingSetupState state,
        LearnedPcbVisualModel? model)
    {
        var learnedFrom = model is null
            ? counts.GetValueOrDefault(ImageLearningImageRole.GoldenReference) + counts.GetValueOrDefault(ImageLearningImageRole.OkLearning)
            : model.GoldenCount + model.OkLearningCount;
        var before = state.FalseCallRateBeforeLearning;
        var after = state.FalseCallRateAfterLearning;
        double? reduction = before.HasValue && after.HasValue
            ? before.Value > 0
                ? Math.Max(0.0, (before.Value - after.Value) / before.Value)
                : 0
            : null;
        var beforeValue = state.FalseCallsBeforeLearning.HasValue && before.HasValue
            ? $"{state.FalseCallsBeforeLearning.Value} ({before.Value:P1})"
            : FormatRate(before);
        var afterValue = state.FalseCallsAfterLearning.HasValue && after.HasValue
            ? $"{state.FalseCallsAfterLearning.Value} ({after.Value:P1})"
            : FormatRate(after);
        return
        [
            new("Images learned from", learnedFrom.ToString(CultureInfo.InvariantCulture), "Golden / Reference plus OK Learning images."),
            new("OK validation images", counts.GetValueOrDefault(ImageLearningImageRole.OkValidation).ToString(CultureInfo.InvariantCulture), "Good boards used to calibrate tolerance and estimate false calls."),
            new("False calls before learning", beforeValue, "Pixel Difference Prototype Engine on OK Validation images."),
            new("False calls after learning", afterValue, "Learned PCB Visual Model v1 on OK Validation images."),
            new("False-call reduction", FormatRate(reduction), "Relative reduction from before-learning false calls; higher means less unnecessary review."),
            new("Possible escapes", state.PossibleEscapeCount.HasValue ? $"{state.PossibleEscapeCount.Value} ({FormatRate(state.PossibleEscapeRate)})" : "No NG set", "Shown only when optional NG Validation images are available."),
            new("Recommended threshold", state.RecommendedThreshold?.ToString("F2", CultureInfo.InvariantCulture) ?? "--", "Threshold selected by OK/NG validation sweep."),
        ];
    }

    private static IReadOnlyList<AiTrainingTimelineItem> BuildTimeline(
        IReadOnlyDictionary<ImageLearningImageRole, int> counts,
        AiTrainingSetupState state,
        LearnedPcbVisualModel? model)
    {
        var totalImages = counts.Values.Sum();
        return
        [
            new("Images imported", totalImages > 0 ? "COMPLETE" : "WAITING", $"{totalImages} image(s) grouped by role.", totalImages > 0),
            new("Model learned", model is null ? "WAITING" : "COMPLETE", model is null ? "Run Learn Normal PCB Appearance." : model.ModelId, model is not null),
            new("False-call calibration complete", state.FalseCallRateAfterLearning.HasValue ? "COMPLETE" : "WAITING", state.FalseCallRateAfterLearning.HasValue ? $"False calls after learning {FormatRate(state.FalseCallRateAfterLearning)}." : "Add OK Validation images, then calibrate.", state.FalseCallRateAfterLearning.HasValue),
            new("Inspection complete", state.InspectionComplete ? "COMPLETE" : "WAITING", state.InspectionComplete ? "Inspection images were processed." : "Add Inspection images, then inspect samples.", state.InspectionComplete),
            new("Report exported", state.ReportExported ? "COMPLETE" : "WAITING", state.ReportExported ? Path.GetFileName(state.LastReportPath) : "Export client learning report when ready.", state.ReportExported),
        ];
    }

    private static string BuildGuidance(
        ImageLearningProject? project,
        ImageLearningProjectValidationResult? validation,
        IReadOnlyDictionary<ImageLearningImageRole, int> counts)
    {
        if (project is null)
            return "Create a training project, then add Golden / Reference images or at least 5 OK Learning images.";
        if (validation?.CanTrain != true)
            return validation is { BlockingIssues.Count: > 0 }
                ? validation.BlockingIssues[0]
                : "Training requires at least one Golden / Reference image or at least 5 OK Learning images.";
        if (counts.GetValueOrDefault(ImageLearningImageRole.OkValidation) == 0)
            return "Learning is available. Add OK Validation images before calibrating false calls.";
        return "Ready to learn normal PCB appearance and calibrate false calls from images only.";
    }

    private static string BuildFalseCallSummary(AiTrainingSetupState state)
        => state.FalseCallsBeforeLearning.HasValue && state.FalseCallsAfterLearning.HasValue
            ? $"False calls reduced from {state.FalseCallsBeforeLearning.Value} to {state.FalseCallsAfterLearning.Value}"
            : "False calls reduced from A to B after OK Validation comparison is run.";

    private static string BuildPossibleEscapeSummary(AiTrainingSetupState state)
        => state.PossibleEscapeCount.HasValue
            ? $"Possible escapes: {state.PossibleEscapeCount.Value}"
            : "Possible escapes: NG Validation not provided; missed-defect rate cannot yet be proven.";

    private static string BuildRecommendedThresholdSummary(AiTrainingSetupState state)
        => state.RecommendedThreshold.HasValue
            ? $"Recommended threshold: {state.RecommendedThreshold.Value:F2}"
            : "Recommended threshold: waiting for learning and calibration.";

    private static string BuildActiveInspectionModelDisplay(LearnedPcbVisualModel? model)
    {
        var active = LearnedVisualModelRegistryService.GetActiveLearnedVisualModel();
        if (active is null)
            return "Active inspection model: not set to Learned PCB Visual Model.";
        if (model is not null && string.Equals(active.ModelId, model.ModelId, StringComparison.OrdinalIgnoreCase))
            return $"Active inspection model: Learned PCB Visual Model {active.ModelVersion} / {active.ProjectName}.";

        return $"Active inspection model: Learned PCB Visual Model {active.ModelId}.";
    }

    private static string ArtifactPath(LearnedPcbVisualModel? model, string artifactName)
        => model?.Artifacts.FirstOrDefault(artifact => string.Equals(artifact.ArtifactName, artifactName, StringComparison.OrdinalIgnoreCase))?.ArtifactPath
           ?? string.Empty;

    private static string FormatRate(double? value)
        => value.HasValue ? value.Value.ToString("P1", CultureInfo.InvariantCulture) : "--";

    private static void EnsureCanImport(UserRole role, string action)
    {
        if (!RoleAuthorization.CanImportImageLearningImages(role))
            throw new UnauthorizedAccessException(RoleAuthorization.DeniedMessage(role, action));
    }

    private static void EnsureCanRunLearning(UserRole role, string action)
    {
        if (!RoleAuthorization.CanRunImageOnlyLearning(role))
            throw new UnauthorizedAccessException(RoleAuthorization.DeniedMessage(role, action));
    }
}
