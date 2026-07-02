using System.Collections.ObjectModel;
using System.IO;
using AOI_Monitor.Models;
using AOI_Monitor.Services;

namespace AOI_Monitor.ViewModels;

public sealed class AiTrainingSetupViewModel : ViewModelBase
{
    private AiTrainingSetupSnapshot _snapshot = new();
    private TrainedDataManagementSnapshot _trainedData = new();
    private string _statusText = "Create or load an AI Training Setup project.";
    private string _projectName = "Image-only PCB learning";
    private string _boardModel = WorkflowState.Instance.BoardProgram;
    private bool _isBusy;

    public ObservableCollection<AiTrainingRoleCard> RoleCards { get; } = new();
    public ObservableCollection<AiTrainingMetricCard> MetricCards { get; } = new();
    public ObservableCollection<AiTrainingTimelineItem> Timeline { get; } = new();
    public ObservableCollection<string> TrainedDataInstructions { get; } = new();
    public ObservableCollection<TrainedDataProjectRow> TrainedDataProjects { get; } = new();
    public ObservableCollection<TrainedDataModelVersionRow> TrainedDataModelVersions { get; } = new();
    public ObservableCollection<TrainedDataCalibrationRow> TrainedDataCalibrationRuns { get; } = new();
    public ObservableCollection<TrainedDataExportRow> TrainedDataExportedReports { get; } = new();
    public ObservableCollection<TrainedDataModelComparisonRow> TrainedDataModelComparisons { get; } = new();

    public AiTrainingSetupSnapshot Snapshot
    {
        get => _snapshot;
        private set => SetField(ref _snapshot, value);
    }

    public TrainedDataManagementSnapshot TrainedData
    {
        get => _trainedData;
        private set => SetField(ref _trainedData, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetField(ref _statusText, value);
    }

    public string ProjectName
    {
        get => _projectName;
        set => SetField(ref _projectName, value);
    }

    public string BoardModel
    {
        get => _boardModel;
        set => SetField(ref _boardModel, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (!SetField(ref _isBusy, value))
                return;

            OnPropertyChanged(nameof(CanCreateProject));
            OnPropertyChanged(nameof(CanImportImages));
            OnPropertyChanged(nameof(CanRunLearning));
            OnPropertyChanged(nameof(CanRunCalibration));
            OnPropertyChanged(nameof(CanRunInspection));
            OnPropertyChanged(nameof(CanExportReport));
            OnPropertyChanged(nameof(CanExportVisualEvidence));
            OnPropertyChanged(nameof(CanSetActiveInspectionModel));
            OnPropertyChanged(nameof(CanManageOpenProject));
            OnPropertyChanged(nameof(CanManageRenameProject));
            OnPropertyChanged(nameof(CanManageArchiveProject));
            OnPropertyChanged(nameof(CanManageDuplicateProject));
            OnPropertyChanged(nameof(CanManageRebuildModel));
            OnPropertyChanged(nameof(CanManageSetActiveModel));
            OnPropertyChanged(nameof(CanManageExportModelArtifacts));
            OnPropertyChanged(nameof(CanManageExportLearningReport));
            OnPropertyChanged(nameof(CanManageOpenArtifactFolder));
            OnPropertyChanged(nameof(CanManageDeleteGeneratedArtifacts));
        }
    }

    public string CurrentProjectDisplay => Snapshot.Project is null
        ? "No training project selected"
        : $"{Snapshot.Project.ProjectName} / {Snapshot.Project.ProjectId}";

    public string CurrentModelDisplay => Snapshot.Model is null
        ? "No learned visual model yet"
        : $"{Snapshot.Model.ModelId} / threshold {Snapshot.Model.LearnedThreshold:F2}";

    public string ActiveInspectionModelDisplay => Snapshot.ActiveInspectionModelDisplay;
    public string Guidance => Snapshot.Guidance;
    public string RoleRestrictionText => Snapshot.RoleRestrictionText;
    public string LearnedReferencePath => ExistingOrEmpty(Snapshot.LearnedReferencePath);
    public string ToleranceMapPath => ExistingOrEmpty(Snapshot.ToleranceMapPath);
    public string AnomalyHeatmapPath => ExistingOrEmpty(Snapshot.AnomalyHeatmapPath);
    public string FalseCallSummaryText => Snapshot.FalseCallSummaryText;
    public string PossibleEscapeSummaryText => Snapshot.PossibleEscapeSummaryText;
    public string RecommendedThresholdSummaryText => Snapshot.RecommendedThresholdSummaryText;
    public string TrainedDataSummaryText => TrainedData.SummaryText;
    public string ActiveTrainedDataModelDisplay => TrainedData.ActiveModelDisplay;
    public bool CanCreateProject => !IsBusy && Snapshot.CanCreateProject;
    public bool CanImportImages => !IsBusy && Snapshot.CanImportImages;
    public bool CanRunLearning => !IsBusy && Snapshot.CanRunLearning;
    public bool CanRunCalibration => !IsBusy && Snapshot.CanRunCalibration;
    public bool CanRunInspection => !IsBusy && Snapshot.CanRunInspection;
    public bool CanExportReport => !IsBusy && Snapshot.CanExportReport;
    public bool CanExportVisualEvidence => !IsBusy && Snapshot.CanExportVisualEvidence;
    public bool CanSetActiveInspectionModel => !IsBusy && Snapshot.CanSetActiveInspectionModel;
    public bool CanManageOpenProject => !IsBusy && TrainedData.Permissions.CanOpenProject;
    public bool CanManageRenameProject => !IsBusy && TrainedData.Permissions.CanRenameProject;
    public bool CanManageArchiveProject => !IsBusy && TrainedData.Permissions.CanArchiveProject;
    public bool CanManageDuplicateProject => !IsBusy && TrainedData.Permissions.CanDuplicateProject;
    public bool CanManageRebuildModel => !IsBusy && TrainedData.Permissions.CanRebuildModel;
    public bool CanManageSetActiveModel => !IsBusy && TrainedData.Permissions.CanSetActiveModel;
    public bool CanManageExportModelArtifacts => !IsBusy && TrainedData.Permissions.CanExportModelArtifacts;
    public bool CanManageExportLearningReport => !IsBusy && TrainedData.Permissions.CanExportLearningReport;
    public bool CanManageOpenArtifactFolder => !IsBusy && TrainedData.Permissions.CanOpenArtifactFolder;
    public bool CanManageDeleteGeneratedArtifacts => !IsBusy && TrainedData.Permissions.CanDeleteGeneratedArtifacts;

    public void Refresh(UserRole? role = null)
    {
        Snapshot = AiTrainingSetupService.GetSnapshot(role ?? WorkflowState.Instance.CurrentRole);
        TrainedData = ImageLearningTrainedDataManagementService.GetSnapshot(role ?? WorkflowState.Instance.CurrentRole);
        Replace(RoleCards, Snapshot.RoleCards);
        Replace(MetricCards, Snapshot.MetricCards);
        Replace(Timeline, Snapshot.Timeline);
        Replace(TrainedDataInstructions, TrainedData.Instructions);
        Replace(TrainedDataProjects, TrainedData.Projects);
        Replace(TrainedDataModelVersions, TrainedData.ModelVersions);
        Replace(TrainedDataCalibrationRuns, TrainedData.CalibrationRuns);
        Replace(TrainedDataExportedReports, TrainedData.ExportedReports);
        Replace(TrainedDataModelComparisons, TrainedData.ModelComparisons);
        if (Snapshot.Project is not null)
        {
            ProjectName = Snapshot.Project.ProjectName;
            BoardModel = Snapshot.Project.BoardModel;
        }

        StatusText = Snapshot.Guidance;
        RaiseSnapshotProperties();
    }

    public void CreateProject(UserRole? role = null)
    {
        var userRole = role ?? WorkflowState.Instance.CurrentRole;
        var project = AiTrainingSetupService.CreateTrainingProject(ProjectName, BoardModel, userRole, WorkflowState.Instance.OperatorWithRole);
        StatusText = $"Training project created: {project.ProjectId}.";
        Refresh(userRole);
    }

    public IReadOnlyList<ImageLearningImportResult> ImportImages(
        ImageLearningImageRole role,
        IReadOnlyList<string> paths,
        UserRole? userRole = null)
    {
        var currentRole = userRole ?? WorkflowState.Instance.CurrentRole;
        var result = AiTrainingSetupService.ImportImages(role, paths, currentRole, WorkflowState.Instance.OperatorWithRole);
        var imported = result.Count(item => item.Imported);
        var duplicates = result.Count(item => item.IsDuplicate);
        StatusText = $"Imported {imported} image(s) for {DisplayRole(role)}. Duplicates skipped: {duplicates}.";
        Refresh(currentRole);
        return result;
    }

    public AiTrainingSetupLearningOutcome RunLearning(UserRole? role = null)
    {
        var currentRole = role ?? WorkflowState.Instance.CurrentRole;
        var outcome = AiTrainingSetupService.RunLearning(currentRole, createdBy: WorkflowState.Instance.OperatorWithRole);
        StatusText = $"Learned normal PCB appearance: {outcome.LearningResult.Model.ModelId}.";
        Refresh(currentRole);
        return outcome;
    }

    public AiTrainingSetupInspectionOutcome InspectSamples(UserRole? role = null)
    {
        var currentRole = role ?? WorkflowState.Instance.CurrentRole;
        var outcome = AiTrainingSetupService.InspectSamples(currentRole, operatorId: WorkflowState.Instance.OperatorWithRole);
        StatusText = $"Inspection complete: {outcome.CompletedCount}/{outcome.RequestedCount} image(s), {outcome.ReviewOrNgCount} review/NG.";
        Refresh(currentRole);
        return outcome;
    }

    public string ExportReport(UserRole? role = null)
    {
        var currentRole = role ?? WorkflowState.Instance.CurrentRole;
        var report = AiTrainingSetupService.ExportClientLearningReport(currentRole, WorkflowState.Instance.OperatorWithRole);
        StatusText = $"Client learning report exported: {Path.GetFileName(report)}.";
        Refresh(currentRole);
        return report;
    }

    public ImageLearningOverlayExportResult ExportVisualEvidence(UserRole? role = null)
    {
        var currentRole = role ?? WorkflowState.Instance.CurrentRole;
        var result = AiTrainingSetupService.ExportVisualEvidence(currentRole, WorkflowState.Instance.OperatorWithRole);
        StatusText = $"Visual evidence exported: {Path.GetFileName(result.ManifestPath)}.";
        Refresh(currentRole);
        return result;
    }

    public LearnedVisualModelRegistryEntry SetCurrentModelAsActive(UserRole? role = null)
    {
        var currentRole = role ?? WorkflowState.Instance.CurrentRole;
        var active = AiTrainingSetupService.SetCurrentModelAsActive(currentRole, WorkflowState.Instance.OperatorWithRole);
        StatusText = $"Active inspection model set: {active.ModelId} / {active.ModelVersion}.";
        Refresh(currentRole);
        return active;
    }

    public ImageLearningProject OpenManagedProject(string projectId, UserRole? role = null)
    {
        var currentRole = role ?? WorkflowState.Instance.CurrentRole;
        var project = ImageLearningTrainedDataManagementService.OpenProject(projectId, currentRole);
        StatusText = $"Opened training project: {project.ProjectName}.";
        Refresh(currentRole);
        return project;
    }

    public ImageLearningProject RenameManagedProject(string projectId, string newName, UserRole? role = null)
    {
        var currentRole = role ?? WorkflowState.Instance.CurrentRole;
        var project = ImageLearningTrainedDataManagementService.RenameProject(
            projectId,
            newName,
            currentRole,
            WorkflowState.Instance.OperatorWithRole);
        StatusText = $"Training project renamed: {project.ProjectName}.";
        Refresh(currentRole);
        return project;
    }

    public ImageLearningProject ArchiveManagedProject(string projectId, UserRole? role = null)
    {
        var currentRole = role ?? WorkflowState.Instance.CurrentRole;
        var project = ImageLearningTrainedDataManagementService.ArchiveProject(
            projectId,
            currentRole,
            WorkflowState.Instance.OperatorWithRole);
        StatusText = $"Training project archived: {project.ProjectName}.";
        Refresh(currentRole);
        return project;
    }

    public ImageLearningProject DuplicateManagedProject(string projectId, UserRole? role = null)
    {
        var currentRole = role ?? WorkflowState.Instance.CurrentRole;
        var project = ImageLearningTrainedDataManagementService.DuplicateProject(
            projectId,
            currentRole,
            WorkflowState.Instance.OperatorWithRole);
        StatusText = $"Training project duplicated: {project.ProjectName}.";
        Refresh(currentRole);
        return project;
    }

    public AiTrainingSetupLearningOutcome RebuildManagedModel(string projectId, UserRole? role = null)
    {
        var currentRole = role ?? WorkflowState.Instance.CurrentRole;
        var outcome = ImageLearningTrainedDataManagementService.RebuildLearnedModel(
            projectId,
            currentRole,
            operatorId: WorkflowState.Instance.OperatorWithRole);
        StatusText = $"Learned model version created: {outcome.LearningResult.Model.ModelId}.";
        Refresh(currentRole);
        return outcome;
    }

    public LearnedVisualModelRegistryEntry SetManagedModelAsActive(string modelId, UserRole? role = null)
    {
        var currentRole = role ?? WorkflowState.Instance.CurrentRole;
        var active = ImageLearningTrainedDataManagementService.SetModelAsActive(
            modelId,
            currentRole,
            WorkflowState.Instance.OperatorWithRole);
        StatusText = $"Active learned model set: {active.ModelId}.";
        Refresh(currentRole);
        return active;
    }

    public TrainedDataManagementExportResult ExportManagedModelArtifacts(string modelId, UserRole? role = null)
    {
        var currentRole = role ?? WorkflowState.Instance.CurrentRole;
        var result = ImageLearningTrainedDataManagementService.ExportModelArtifacts(
            modelId,
            currentRole,
            WorkflowState.Instance.OperatorWithRole);
        StatusText = $"Model artifacts exported: {Path.GetFileName(result.ManifestPath)}.";
        Refresh(currentRole);
        return result;
    }

    public ImageOnlyLearningReportResult ExportManagedLearningReport(string projectId, string modelId, UserRole? role = null)
    {
        var currentRole = role ?? WorkflowState.Instance.CurrentRole;
        var result = ImageLearningTrainedDataManagementService.ExportLearningReport(
            projectId,
            modelId,
            currentRole,
            WorkflowState.Instance.OperatorWithRole);
        StatusText = $"Learning report exported: {Path.GetFileName(result.HtmlPath)}.";
        Refresh(currentRole);
        return result;
    }

    public string ManagedArtifactFolder(string modelId)
        => ImageLearningTrainedDataManagementService.GetArtifactFolder(modelId);

    public ImageLearningArtifactDeletionResult DeleteManagedGeneratedArtifacts(string modelId, bool explicitConfirmation, UserRole? role = null)
    {
        var currentRole = role ?? WorkflowState.Instance.CurrentRole;
        var result = ImageLearningTrainedDataManagementService.DeleteGeneratedArtifacts(
            modelId,
            currentRole,
            explicitConfirmation,
            WorkflowState.Instance.OperatorWithRole);
        StatusText = $"Generated artifacts deleted: {result.FilesDeleted} file(s), {result.MissingFiles} missing.";
        Refresh(currentRole);
        return result;
    }

    public static string? RoleFolder(ImageLearningImageRole role)
        => AiTrainingSetupService.RoleFolder(role);

    public static string? PreviewImagePath(ImageLearningImageRole role)
        => AiTrainingSetupService.FirstImagePath(role);

    private void RaiseSnapshotProperties()
    {
        OnPropertyChanged(nameof(CurrentProjectDisplay));
        OnPropertyChanged(nameof(CurrentModelDisplay));
        OnPropertyChanged(nameof(ActiveInspectionModelDisplay));
        OnPropertyChanged(nameof(Guidance));
        OnPropertyChanged(nameof(RoleRestrictionText));
        OnPropertyChanged(nameof(LearnedReferencePath));
        OnPropertyChanged(nameof(ToleranceMapPath));
        OnPropertyChanged(nameof(AnomalyHeatmapPath));
        OnPropertyChanged(nameof(FalseCallSummaryText));
        OnPropertyChanged(nameof(PossibleEscapeSummaryText));
        OnPropertyChanged(nameof(RecommendedThresholdSummaryText));
        OnPropertyChanged(nameof(TrainedDataSummaryText));
        OnPropertyChanged(nameof(ActiveTrainedDataModelDisplay));
        OnPropertyChanged(nameof(CanCreateProject));
        OnPropertyChanged(nameof(CanImportImages));
        OnPropertyChanged(nameof(CanRunLearning));
        OnPropertyChanged(nameof(CanRunCalibration));
        OnPropertyChanged(nameof(CanRunInspection));
        OnPropertyChanged(nameof(CanExportReport));
        OnPropertyChanged(nameof(CanExportVisualEvidence));
        OnPropertyChanged(nameof(CanSetActiveInspectionModel));
        OnPropertyChanged(nameof(CanManageOpenProject));
        OnPropertyChanged(nameof(CanManageRenameProject));
        OnPropertyChanged(nameof(CanManageArchiveProject));
        OnPropertyChanged(nameof(CanManageDuplicateProject));
        OnPropertyChanged(nameof(CanManageRebuildModel));
        OnPropertyChanged(nameof(CanManageSetActiveModel));
        OnPropertyChanged(nameof(CanManageExportModelArtifacts));
        OnPropertyChanged(nameof(CanManageExportLearningReport));
        OnPropertyChanged(nameof(CanManageOpenArtifactFolder));
        OnPropertyChanged(nameof(CanManageDeleteGeneratedArtifacts));
    }

    private static string ExistingOrEmpty(string path)
        => !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : string.Empty;

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

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source)
            target.Add(item);
    }
}
