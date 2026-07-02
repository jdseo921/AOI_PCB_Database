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
