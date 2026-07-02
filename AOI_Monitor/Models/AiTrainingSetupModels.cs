using AOI_Monitor.Services;

namespace AOI_Monitor.Models;

public sealed class AiTrainingSetupState
{
    public string CurrentProjectId { get; set; } = string.Empty;
    public string CurrentModelId { get; set; } = string.Empty;
    public string ProjectName { get; set; } = string.Empty;
    public string BoardModel { get; set; } = string.Empty;
    public Dictionary<ImageLearningImageRole, string> LastRoleFolders { get; set; } = new();
    public int? FalseCallsBeforeLearning { get; set; }
    public int? FalseCallsAfterLearning { get; set; }
    public double? FalseCallRateBeforeLearning { get; set; }
    public double? FalseCallRateAfterLearning { get; set; }
    public int? PossibleEscapeCount { get; set; }
    public double? PossibleEscapeRate { get; set; }
    public double? RecommendedThreshold { get; set; }
    public bool InspectionComplete { get; set; }
    public bool ReportExported { get; set; }
    public string LastReportPath { get; set; } = string.Empty;
    public string LastFalseCallComparisonReportPath { get; set; } = string.Empty;
    public string LastVisualEvidenceExportPath { get; set; } = string.Empty;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class AiTrainingSetupSnapshot
{
    public AiTrainingSetupState State { get; set; } = new();
    public ImageLearningProject? Project { get; set; }
    public LearnedPcbVisualModel? Model { get; set; }
    public IReadOnlyList<AiTrainingRoleCard> RoleCards { get; set; } = Array.Empty<AiTrainingRoleCard>();
    public IReadOnlyList<AiTrainingMetricCard> MetricCards { get; set; } = Array.Empty<AiTrainingMetricCard>();
    public IReadOnlyList<AiTrainingTimelineItem> Timeline { get; set; } = Array.Empty<AiTrainingTimelineItem>();
    public bool CanCreateProject { get; set; }
    public bool CanImportImages { get; set; }
    public bool CanRunLearning { get; set; }
    public bool CanRunCalibration { get; set; }
    public bool CanRunInspection { get; set; }
    public bool CanExportReport { get; set; }
    public bool CanExportVisualEvidence { get; set; }
    public bool CanSetActiveInspectionModel { get; set; }
    public string Guidance { get; set; } = string.Empty;
    public string RoleRestrictionText { get; set; } = string.Empty;
    public string ActiveInspectionModelDisplay { get; set; } = string.Empty;
    public string LearnedReferencePath { get; set; } = string.Empty;
    public string ToleranceMapPath { get; set; } = string.Empty;
    public string AnomalyHeatmapPath { get; set; } = string.Empty;
    public string FalseCallSummaryText { get; set; } = string.Empty;
    public string PossibleEscapeSummaryText { get; set; } = string.Empty;
    public string RecommendedThresholdSummaryText { get; set; } = string.Empty;
}

public sealed record AiTrainingRoleCard(
    ImageLearningImageRole Role,
    string Title,
    string Explanation,
    int ImageCount,
    string LastFolder,
    bool IsOptional,
    bool CanEdit);

public sealed record AiTrainingMetricCard(
    string Label,
    string Value,
    string Detail);

public sealed record AiTrainingTimelineItem(
    string Label,
    string Status,
    string Detail,
    bool IsComplete);

public sealed record AiTrainingSetupLearningOutcome(
    ImageOnlyPcbLearningResult LearningResult,
    AiTrainingSetupSnapshot Snapshot);

public sealed record AiTrainingSetupInspectionOutcome(
    int RequestedCount,
    int CompletedCount,
    int ReviewOrNgCount,
    AiTrainingSetupSnapshot Snapshot);

public sealed class TrainedDataManagementSnapshot
{
    public IReadOnlyList<string> Instructions { get; set; } = Array.Empty<string>();
    public IReadOnlyList<TrainedDataProjectRow> Projects { get; set; } = Array.Empty<TrainedDataProjectRow>();
    public IReadOnlyList<TrainedDataModelVersionRow> ModelVersions { get; set; } = Array.Empty<TrainedDataModelVersionRow>();
    public IReadOnlyList<TrainedDataCalibrationRow> CalibrationRuns { get; set; } = Array.Empty<TrainedDataCalibrationRow>();
    public IReadOnlyList<TrainedDataExportRow> ExportedReports { get; set; } = Array.Empty<TrainedDataExportRow>();
    public IReadOnlyList<TrainedDataModelComparisonRow> ModelComparisons { get; set; } = Array.Empty<TrainedDataModelComparisonRow>();
    public TrainedDataManagementActionPermissions Permissions { get; set; } = new();
    public string ActiveModelDisplay { get; set; } = "Active learned model: not set.";
    public string SummaryText { get; set; } = string.Empty;
}

public sealed class TrainedDataManagementActionPermissions
{
    public bool CanOpenProject { get; set; } = true;
    public bool CanRenameProject { get; set; }
    public bool CanArchiveProject { get; set; }
    public bool CanDuplicateProject { get; set; }
    public bool CanRebuildModel { get; set; }
    public bool CanSetActiveModel { get; set; }
    public bool CanExportModelArtifacts { get; set; }
    public bool CanExportLearningReport { get; set; }
    public bool CanOpenArtifactFolder { get; set; } = true;
    public bool CanDeleteGeneratedArtifacts { get; set; }
}

public sealed class TrainedDataProjectRow
{
    public string ProjectId { get; set; } = string.Empty;
    public string ProjectName { get; set; } = string.Empty;
    public string RenameText { get; set; } = string.Empty;
    public string BoardModel { get; set; } = string.Empty;
    public string EvidenceMode { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime UpdatedAtUtc { get; set; }
    public int GoldenReferenceCount { get; set; }
    public int OkLearningCount { get; set; }
    public int OkValidationCount { get; set; }
    public int InspectionCount { get; set; }
    public int NgValidationCount { get; set; }
    public int LearnedModelCount { get; set; }
    public int CalibrationRunCount { get; set; }
    public int ExportedReportCount { get; set; }
    public string ImageGroupSummary { get; set; } = string.Empty;
    public string MetricsSummary { get; set; } = string.Empty;
    public bool IsArchived { get; set; }
}

public sealed class TrainedDataModelVersionRow
{
    public string ModelId { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string ProjectName { get; set; } = string.Empty;
    public string ModelVersion { get; set; } = string.Empty;
    public string VersionDisplay { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public int ImagesLearnedFrom { get; set; }
    public int OkValidationCount { get; set; }
    public double FalseCallRate { get; set; }
    public double PossibleEscapeRate { get; set; }
    public double RecommendedThreshold { get; set; }
    public string MetricsSummary { get; set; } = string.Empty;
    public string ArtifactFolder { get; set; } = string.Empty;
    public string ArtifactStatus { get; set; } = string.Empty;
    public bool IsActive { get; set; }
}

public sealed class TrainedDataCalibrationRow
{
    public string CalibrationId { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public int OkValidationCount { get; set; }
    public int NgValidationCount { get; set; }
    public string FalseCallRateDisplay { get; set; } = string.Empty;
    public string PossibleEscapeRateDisplay { get; set; } = string.Empty;
    public string RecommendedThresholdDisplay { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
}

public sealed class TrainedDataExportRow
{
    public long ExportHistoryId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string ExportType { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string OperatorId { get; set; } = string.Empty;
}

public sealed class TrainedDataModelComparisonRow
{
    public string ProjectId { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
    public string VersionDisplay { get; set; } = string.Empty;
    public string FalseCallRateDisplay { get; set; } = string.Empty;
    public string PossibleEscapeRateDisplay { get; set; } = string.Empty;
    public string ComparisonSummary { get; set; } = string.Empty;
    public bool IsActive { get; set; }
}

public sealed record TrainedDataManagementExportResult(
    string OutputFolder,
    string ManifestPath,
    long ExportHistoryId,
    IReadOnlyList<string> Warnings);
