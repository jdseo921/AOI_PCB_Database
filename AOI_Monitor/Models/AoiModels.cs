namespace AOI_Monitor.Models;

public record StatusCell(string Label, string Value, string Color);

public record DefectRecord(
    string Sample, string Board, string RefDes, string Defect,
    string Severity, string AiResult, string GroundTruth, string Risk,
    string ImageLink, string Date);

public record ImageLibraryRecord(
    string Sample,
    string Board,
    string RefDes,
    string Defect,
    string Severity,
    string AiResult,
    string GroundTruth,
    string Risk,
    string ImageLink,
    string Date,
    string VaultPath,
    string OriginalPath,
    string FileHash,
    bool IsDemo);

public class StationInfo
{
    public string Name        { get; set; }
    public string Status      { get; set; }
    public int    SampleCount { get; set; }
    public int    ReviewCount { get; set; }
    public int    WaitCount   { get; set; }
    public double Yield       { get; set; }
    public double DetectedPct { get; set; }
    public int    FalseCount  { get; set; }
    public string Description { get; set; }
    public string StatusColor { get; set; } // green / amber / red

    // Derived for binding
    public bool   IsRed       => StatusColor == "red";
    public bool   IsAmber     => StatusColor == "amber";
    public string HeadBg      => IsRed ? "#D6131A" : "#191F23";
    public string HeadFg      => IsRed ? "#FFFFFF"  : "#D9E1E6";
    public string TagBg       => IsRed ? "#581B1D"  : IsAmber ? "#8A570F" : "#173A21";
    public string TagBorder   => IsRed ? "#A83A3E"  : IsAmber ? "#CF8D28" : "#3E844A";
    public string TagFg       => IsRed ? "#FFCECE"  : IsAmber ? "#FFF1C5" : "#CFFFCE";
    public string GaugeColor  => IsRed ? "#F13B3F"  : IsAmber ? "#E1A334" : "#50F56E";

    public StationInfo(string name, string status, int samples, int review, int wait,
                       double yield, double detected, int falseCount, string desc, string color)
    {
        Name = name; Status = status; SampleCount = samples; ReviewCount = review;
        WaitCount = wait; Yield = yield; DetectedPct = detected; FalseCount = falseCount;
        Description = desc; StatusColor = color;
    }
}

public record SpcStat(string Label, string Value, bool IsAlert);
public record DbHealthRow(string Table, string Count, string Status);

public record BatchTestRunRecord(
    long Id,
    string ImageFolder,
    string? GroundTruthCsvPath,
    string EngineName,
    string ModelVersion,
    DateTime CreatedAtUtc,
    double Accuracy,
    double Precision,
    double Recall,
    double FalseCallRate,
    int TotalImages,
    int FailedCount,
    string ThresholdProfileId = "",
    string ThresholdProfileRevision = "");

public record BatchTestResultRecord(
    long Id,
    long RunId,
    string ImagePath,
    string ImageName,
    string GroundTruth,
    string EngineResult,
    string InspectionEngine,
    string ModelVersion,
    double Score,
    string PassFail,
    string DefectType,
    string NormalizedDefectClass,
    string NormalizedSide,
    string RoiId,
    string RoiType,
    string FailureCategory,
    double RoiX,
    double RoiY,
    double RoiWidth,
    double RoiHeight,
    string Side,
    string RefDes,
    string LotId,
    string BoardModel,
    string Notes,
    double ImageLoadMilliseconds,
    double PreprocessingMilliseconds,
    double InferenceMilliseconds,
    double OverlayRenderingMilliseconds,
    double TotalInspectionMilliseconds,
    DateTime CreatedAtUtc);

public sealed class ValidationBreakdownMetric
{
    public long Id { get; set; }
    public long RunId { get; set; }
    public string BreakdownType { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int Total { get; set; }
    public int TruePositive { get; set; }
    public int TrueNegative { get; set; }
    public int FalsePositive { get; set; }
    public int FalseNegative { get; set; }
    public int WrongDefectClass { get; set; }
    public int WrongSide { get; set; }
    public int UnknownGroundTruth { get; set; }
    public double Precision { get; set; }
    public double Recall { get; set; }
    public double FalseCallRate { get; set; }
}

public sealed class ValidationBreakdownSummary
{
    public List<ValidationBreakdownMetric> DefectClassMetrics { get; set; } = new();
    public List<ValidationBreakdownMetric> SideMetrics { get; set; } = new();
    public List<ValidationBreakdownMetric> RoiMetrics { get; set; } = new();
    public List<ValidationBreakdownMetric> RoiTypeMetrics { get; set; } = new();
    public List<ValidationBreakdownMetric> TopFalseCallContributors { get; set; } = new();
    public List<ValidationBreakdownMetric> TopPossibleEscapeContributors { get; set; } = new();
}

public sealed class ThresholdProfile
{
    public string ProfileId { get; set; } = Guid.NewGuid().ToString("N");
    public string Revision { get; set; } = "R0001";
    public string BoardModel { get; set; } = "ANY";
    public string BoardProgram { get; set; } = "ANY";
    public string RecipeName { get; set; } = "ANY";
    public string RecipeRevision { get; set; } = "ANY";
    public string Status { get; set; } = "Draft";
    public long? SourceValidationRunId { get; set; }
    public long? SourceFalseCallReductionRunId { get; set; }
    public string CreatedBy { get; set; } = "UNKNOWN";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string ApprovedBy { get; set; } = string.Empty;
    public DateTime? ApprovedAtUtc { get; set; }
    public List<ThresholdProfileRule> Rules { get; set; } = new();
}

public sealed class ThresholdProfileRule
{
    public long Id { get; set; }
    public string ProfileId { get; set; } = string.Empty;
    public string Revision { get; set; } = string.Empty;
    public string ViewType { get; set; } = "Any";
    public string RoiType { get; set; } = "Any";
    public string DefectClass { get; set; } = "Any";
    public double ReviewThreshold { get; set; }
    public double NgThreshold { get; set; }
    public double ConfidenceThreshold { get; set; } = 0.65;
    public double MinimumAreaPixels { get; set; }
    public double MaxAllowedFalseCallRate { get; set; } = 1.0;
    public int SpecificityScore =>
        Specificity(ViewType) + Specificity(RoiType) + Specificity(DefectClass);

    private static int Specificity(string value)
        => string.IsNullOrWhiteSpace(value) || string.Equals(value, "Any", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
}

public sealed class ThresholdProfileRevision
{
    public string ProfileId { get; set; } = string.Empty;
    public string Revision { get; set; } = string.Empty;
    public string Status { get; set; } = "Draft";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string CreatedBy { get; set; } = "UNKNOWN";
}

public sealed class ThresholdProfileDeployment
{
    public string ProfileId { get; set; } = string.Empty;
    public string Revision { get; set; } = string.Empty;
    public string BoardModel { get; set; } = "ANY";
    public string BoardProgram { get; set; } = "ANY";
    public string RecipeName { get; set; } = "ANY";
    public DateTime DeployedAtUtc { get; set; } = DateTime.UtcNow;
    public string DeployedBy { get; set; } = "UNKNOWN";
    public bool IsActive { get; set; }
}

public sealed class ThresholdProfileAuditSummary
{
    public string ProfileId { get; set; } = string.Empty;
    public string Revision { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int RuleCount { get; set; }
    public string CreatedBy { get; set; } = "UNKNOWN";
    public string ApprovedBy { get; set; } = string.Empty;
    public DateTime? ApprovedAtUtc { get; set; }
}

public sealed class EffectiveThresholdRule
{
    public string Source { get; set; } = "Built-in policy default";
    public string ProfileId { get; set; } = string.Empty;
    public string Revision { get; set; } = string.Empty;
    public double ReviewThreshold { get; set; }
    public double NgThreshold { get; set; }
    public double ConfidenceThreshold { get; set; } = 0.65;
    public double MinimumAreaPixels { get; set; }
}

public sealed class LogFilter
{
    public DateTime? FromDate { get; set; }
    public DateTime? ToDate { get; set; }
    public string? BoardProgram { get; set; }
    public string? OperatorId { get; set; }
    public string? Result { get; set; }
    public string? UserRole { get; set; }
    public string? ActionCategory { get; set; }
}

public record InspectionHistoryRecord(
    long Id,
    DateTime CreatedAtUtc,
    string BoardProgram,
    string OperatorId,
    string InspectionEngine,
    string ModelVersion,
    string ModelFilePath,
    double ConfidenceThreshold,
    string SampleImagePath,
    string GoldenImagePath,
    string Verdict,
    double DifferenceScore,
    double Confidence,
    string SuggestedDefect,
    string DecisionReason,
    double HotspotX,
    double HotspotY,
    double HotspotWidth,
    double HotspotHeight,
    double ImageLoadMilliseconds,
    double PreprocessingMilliseconds,
    double InferenceMilliseconds,
    double OverlayRenderingMilliseconds,
    double TotalInspectionMilliseconds);

public record ReviewEventRecord(
    long Id,
    DateTime EventTimeUtc,
    string Category,
    string OperatorId,
    string Disposition,
    string Message);

public record ExportHistoryRecord(
    long Id,
    DateTime CreatedAtUtc,
    string ExportType,
    string FilePath,
    string Status,
    string OperatorId,
    long? AuditEventId);

public enum ExportVerificationStatus
{
    OK,
    WARN,
    ERROR,
}

public sealed class ExportVerificationResult
{
    public string ExportPath { get; set; } = string.Empty;
    public string ExportType { get; set; } = string.Empty;
    public ExportVerificationStatus Status { get; set; } = ExportVerificationStatus.ERROR;
    public string Sha256 { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime CheckedAtUtc { get; set; } = DateTime.UtcNow;
    public List<string> Messages { get; set; } = new();
    public Dictionary<string, string> ArtifactChecksums { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public record ExportVerificationRecord(
    long Id,
    long? ExportHistoryId,
    DateTime CheckedAtUtc,
    string ExportType,
    string ExportPath,
    string Status,
    string Sha256,
    long SizeBytes,
    string MessagesJson,
    string ArtifactChecksumsJson);

public record ValidationPackageRecord(
    long Id,
    DateTime CreatedAtUtc,
    string PackageId,
    string PackagePath,
    string ManifestPath,
    string AcceptanceStatus,
    string Summary,
    long? RunId,
    string OperatorId,
    long? AuditEventId);

public sealed class ValidationAcceptanceCriteria
{
    public double MinimumAccuracy { get; set; } = 0.90;
    public double MinimumPrecision { get; set; } = 0.90;
    public double MinimumRecall { get; set; } = 0.90;
    public double MaximumFalseCallRate { get; set; } = 0.05;
    public int MaximumImagesOverOneSecond { get; set; } = 0;
    public bool RequireFormalManifest { get; set; }
}

public sealed class ValidationAcceptanceSummary
{
    public string Status { get; set; } = "CONDITIONAL";
    public bool MetricsComputed { get; set; }
    public bool FormalManifestPresent { get; set; }
    public bool NumericGatesPassed { get; set; }
    public string DatasetQualityStatus { get; set; } = "CONDITIONAL";
    public List<string> Messages { get; set; } = new();
}

public sealed class ValidationDatasetQualityCriteria
{
    public int MinimumTotalImages { get; set; } = 50;
    public int MinimumKnownGroundTruthImages { get; set; } = 50;
    public int MinimumOkImages { get; set; } = 20;
    public int MinimumNgImages { get; set; } = 20;
    public int MinimumDefectClasses { get; set; } = 2;
    public bool RequireGoldenImageForPixelDifference { get; set; } = true;
    public double MaximumUnknownLabelRate { get; set; } = 0.05;
    public double MaximumMissingGoldenRate { get; set; } = 0.10;
    public int MinimumImagesPerDefectClass { get; set; } = 5;
}

public sealed class DatasetQualitySummary
{
    public string Status { get; set; } = "CONDITIONAL";
    public int TotalImages { get; set; }
    public int KnownGroundTruthImages { get; set; }
    public int OkImages { get; set; }
    public int NgImages { get; set; }
    public int UnknownLabelImages { get; set; }
    public int MissingGoldenImages { get; set; }
    public int DuplicateImageNames { get; set; }
    public int DuplicateFileHashes { get; set; }
    public int DefectClassCount { get; set; }
    public double UnknownLabelRate { get; set; }
    public double MissingGoldenRate { get; set; }
    public Dictionary<string, int> ImagesPerDefectClass { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Warnings { get; set; } = new();
    public List<string> BlockingFailures { get; set; } = new();
}

public sealed class CameraAcceptanceCriteria
{
    public int FramesPerView { get; set; } = 5;
    public List<string> RequiredViews { get; set; } = new() { "Top" };
    public double MaxConnectMs { get; set; } = 2000;
    public double MaxFirstFrameMs { get; set; } = 1000;
    public double MaxAverageFrameIntervalMs { get; set; } = 500;
    public double MaxDroppedFrameRate { get; set; } = 0.05;
    public double MaxTriggerFailureRate { get; set; } = 0.01;
    public int MinimumWidth { get; set; } = 1;
    public int MinimumHeight { get; set; } = 1;
    public List<string> RequiredPixelFormats { get; set; } = new();
}

public sealed class CameraAcceptanceFrameRecord
{
    public string ViewType { get; set; } = string.Empty;
    public int Sequence { get; set; }
    public string FrameId { get; set; } = string.Empty;
    public string CameraId { get; set; } = string.Empty;
    public DateTime CapturedAtUtc { get; set; } = DateTime.UtcNow;
    public int Width { get; set; }
    public int Height { get; set; }
    public string PixelFormat { get; set; } = string.Empty;
    public string SourceKind { get; set; } = string.Empty;
    public bool IsSimulated { get; set; }
    public double LatencyMs { get; set; }
    public double IntervalMs { get; set; }
    public bool MetadataValid { get; set; }
    public string Message { get; set; } = string.Empty;
}

public sealed class CameraAcceptanceViewMetrics
{
    public string ViewType { get; set; } = string.Empty;
    public int RequestedFrames { get; set; }
    public int ReceivedFrames { get; set; }
    public double ConnectMs { get; set; }
    public double FirstFrameMs { get; set; }
    public double AverageFrameIntervalMs { get; set; }
    public int DroppedFrameCount { get; set; }
    public int TriggerFailureCount { get; set; }
    public int TimeoutCount { get; set; }
    public double DroppedFrameRate { get; set; }
    public double TriggerFailureRate { get; set; }
    public string Status { get; set; } = "FAIL";
    public List<string> Warnings { get; set; } = new();
    public List<string> Failures { get; set; } = new();
}

public sealed class CameraAcceptanceRun
{
    public long Id { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string AdapterName { get; set; } = string.Empty;
    public string SourceKey { get; set; } = string.Empty;
    public string SettingsSummary { get; set; } = string.Empty;
    public CameraAcceptanceCriteria Criteria { get; set; } = new();
    public string Status { get; set; } = "FAIL";
    public string FactoryReadinessStatus { get; set; } = "NOT VALIDATED";
    public bool IsRealHardware { get; set; }
    public int TotalRequestedFrames { get; set; }
    public int TotalReceivedFrames { get; set; }
    public int DroppedFrameCount { get; set; }
    public int TriggerFailureCount { get; set; }
    public int TimeoutCount { get; set; }
    public double MaxConnectMs { get; set; }
    public double MaxFirstFrameMs { get; set; }
    public double AverageFrameIntervalMs { get; set; }
    public List<string> Warnings { get; set; } = new();
    public List<string> Failures { get; set; } = new();
    public List<CameraAcceptanceViewMetrics> ViewMetrics { get; set; } = new();
    public List<CameraAcceptanceFrameRecord> Frames { get; set; } = new();
}

public sealed class CameraAcceptanceSummary
{
    public string Status { get; set; } = "NOT VALIDATED";
    public string AcceptanceStatus { get; set; } = "NOT RUN";
    public bool IsRealHardware { get; set; }
    public DateTime? CreatedAtUtc { get; set; }
    public string AdapterName { get; set; } = string.Empty;
    public string SourceKey { get; set; } = string.Empty;
    public int TotalRequestedFrames { get; set; }
    public int TotalReceivedFrames { get; set; }
    public int DroppedFrameCount { get; set; }
    public int TriggerFailureCount { get; set; }
    public int TimeoutCount { get; set; }
    public List<string> Messages { get; set; } = new();
}

public sealed class LightingAcceptanceCriteria
{
    public List<string> RequiredViews { get; set; } = new() { "Top" };
    public double MaxCommandLatencyMs { get; set; } = 1000;
    public double MaxTriggerToFrameLatencyMs { get; set; } = 1000;
    public bool RequireFrameWhenCameraSourceProvided { get; set; } = true;
}

public sealed class LightingAcceptanceStep
{
    public string ViewType { get; set; } = string.Empty;
    public string ProgramName { get; set; } = string.Empty;
    public string CommandText { get; set; } = string.Empty;
    public double CommandLatencyMs { get; set; }
    public double TriggerToFrameLatencyMs { get; set; }
    public bool CommandAccepted { get; set; }
    public bool FrameReceived { get; set; }
    public string FrameId { get; set; } = string.Empty;
    public string CameraId { get; set; } = string.Empty;
    public string Status { get; set; } = "FAIL";
    public string Message { get; set; } = string.Empty;
}

public sealed class LightingAcceptanceRun
{
    public long Id { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string ControllerName { get; set; } = string.Empty;
    public string Mode { get; set; } = LightingModes.None;
    public string SettingsSummary { get; set; } = string.Empty;
    public LightingAcceptanceCriteria Criteria { get; set; } = new();
    public string Status { get; set; } = "FAIL";
    public bool IsSimulated { get; set; }
    public int StepCount { get; set; }
    public int PassedStepCount { get; set; }
    public int FailedStepCount { get; set; }
    public double MaxCommandLatencyMs { get; set; }
    public double MaxTriggerToFrameLatencyMs { get; set; }
    public List<string> Warnings { get; set; } = new();
    public List<string> Failures { get; set; } = new();
    public List<LightingAcceptanceStep> Steps { get; set; } = new();
}

public sealed class RobotAcceptanceCriteria
{
    public double MaxLoadMs { get; set; } = 1000;
    public double MaxMoveToInspectMs { get; set; } = 1000;
    public double MaxInspectionMs { get; set; } = 1000;
    public double MaxUnloadMs { get; set; } = 1000;
    public double MaxFullCycleMs { get; set; } = 5000;
    public bool RequireEmergencyStopTest { get; set; } = true;
    public bool RequireInvalidTransitionTest { get; set; } = true;
    public bool RequireAuditEvents { get; set; } = true;
}

public sealed class RobotAcceptanceStep
{
    public string StepName { get; set; } = string.Empty;
    public string FromState { get; set; } = string.Empty;
    public string ToState { get; set; } = string.Empty;
    public double ElapsedMs { get; set; }
    public bool Accepted { get; set; }
    public string Status { get; set; } = "FAIL";
    public string Message { get; set; } = string.Empty;
}

public sealed class RobotAcceptanceRun
{
    public long Id { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string ControllerName { get; set; } = string.Empty;
    public string EmergencyStopName { get; set; } = string.Empty;
    public string SourceKind { get; set; } = "Simulated";
    public RobotAcceptanceCriteria Criteria { get; set; } = new();
    public string Status { get; set; } = "FAIL";
    public string FinalState { get; set; } = string.Empty;
    public double LoadMs { get; set; }
    public double MoveToInspectMs { get; set; }
    public double InspectionMs { get; set; }
    public double UnloadMs { get; set; }
    public double FullCycleMs { get; set; }
    public bool InvalidTransitionRejected { get; set; }
    public bool EmergencyStopBlocked { get; set; }
    public bool ResetReturnedIdle { get; set; }
    public int AuditEventCount { get; set; }
    public List<string> Warnings { get; set; } = new();
    public List<string> Failures { get; set; } = new();
    public List<RobotAcceptanceStep> Steps { get; set; } = new();
}

public sealed class RobotAcceptanceSummary
{
    public string Status { get; set; } = "NOT VALIDATED";
    public string SourceKind { get; set; } = "NotValidated";
    public DateTime? CreatedAtUtc { get; set; }
    public string ControllerName { get; set; } = string.Empty;
    public double FullCycleMs { get; set; }
    public bool EmergencyStopBlocked { get; set; }
    public bool InvalidTransitionRejected { get; set; }
    public bool ResetReturnedIdle { get; set; }
    public List<string> Messages { get; set; } = new();
}

public sealed class ValidationPackageManifest
{
    public string SchemaVersion { get; set; } = "stage1-validation-package/v1";
    public string PackageId { get; set; } = string.Empty;
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
    public string AppVersion { get; set; } = string.Empty;
    public string DeploymentProfile { get; set; } = AOI_Monitor.Models.DeploymentProfile.Stage1ImageValidation.ToString();
    public string StationId { get; set; } = string.Empty;
    public string OperatorId { get; set; } = string.Empty;
    public string BoardModel { get; set; } = string.Empty;
    public string LotId { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
    public string ModelSha256 { get; set; } = string.Empty;
    public string ModelValidationStatus { get; set; } = string.Empty;
    public string ModelVersion { get; set; } = string.Empty;
    public string EngineName { get; set; } = string.Empty;
    public double ActiveConfidenceThreshold { get; set; }
    public string DatasetFolderHashOrName { get; set; } = string.Empty;
    public string GroundTruthCsvName { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public ValidationMetricSummary MetricSummary { get; set; } = new();
    public ValidationPackagePerformanceSummary PerformanceSummary { get; set; } = new();
    public ValidationBreakdownSummary BreakdownSummary { get; set; } = new();
    public DatasetQualitySummary DatasetQualitySummary { get; set; } = new();
    public CameraAcceptanceSummary CameraAcceptanceSummary { get; set; } = new();
    public RobotAcceptanceSummary RobotAcceptanceSummary { get; set; } = new();
    public MesReadinessSummary MesReadinessSummary { get; set; } = new();
    public string AcceptanceStatus { get; set; } = "CONDITIONAL";
    public ValidationAcceptanceCriteria Criteria { get; set; } = new();
    public FalseCallRecommendationSummary? FalseCallRecommendation { get; set; }
    public List<ValidationIncludedFile> IncludedFiles { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public List<string> Limitations { get; set; } = new();
}

public enum FalseCallReductionMode
{
    MinimizeFalsePositives,
    Balanced,
    MaximizeDefectRecall,
}

public sealed class FalseCallReductionCriteria
{
    public double MinimumThreshold { get; set; } = 0.05;
    public double MaximumThreshold { get; set; } = 0.95;
    public double ThresholdStep { get; set; } = 0.05;
    public double ReviewBand { get; set; } = 0.05;
    public int MinimumKnownOk { get; set; } = 1;
    public int MinimumKnownNg { get; set; } = 1;
    public double MaximumPossibleEscapeRate { get; set; } = 0.02;
    public double MaximumFalseCallRate { get; set; } = 0.10;
    public double ManualReviewMinutesPerImage { get; set; } = 2.0;
    public FalseCallReductionMode Mode { get; set; } = FalseCallReductionMode.Balanced;
}

public sealed class ThresholdSweepPoint
{
    public double ConfidenceThreshold { get; set; }
    public double DifferenceThreshold { get; set; }
    public int TruePositive { get; set; }
    public int TrueNegative { get; set; }
    public int FalsePositive { get; set; }
    public int FalseNegative { get; set; }
    public int ReviewCount { get; set; }
    public int NgCount { get; set; }
    public int KnownGroundTruthCount { get; set; }
    public int UnknownGroundTruthCount { get; set; }
    public double Precision { get; set; }
    public double Recall { get; set; }
    public double FalseCallRate { get; set; }
    public double PossibleEscapeRate { get; set; }
    public double ReviewRate { get; set; }
    public double NgRate { get; set; }
    public double EstimatedManualReviewMinutes { get; set; }
    public bool MeetsConstraints { get; set; }
    public string Status { get; set; } = "CONDITIONAL";
}

public sealed class OperatingPointRecommendation
{
    public string Status { get; set; } = "INVALID";
    public string Mode { get; set; } = FalseCallReductionMode.Balanced.ToString();
    public ThresholdSweepPoint? Point { get; set; }
    public List<string> Messages { get; set; } = new();
}

public sealed class ThresholdSweepResult
{
    public IReadOnlyList<ThresholdSweepPoint> Points { get; init; } = Array.Empty<ThresholdSweepPoint>();
    public OperatingPointRecommendation Recommendation { get; init; } = new();
}

public sealed class FalseCallReductionRun
{
    public long Id { get; set; }
    public long? BatchRunId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string EngineName { get; set; } = string.Empty;
    public string ModelVersion { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
    public string ModelSha256 { get; set; } = string.Empty;
    public FalseCallReductionCriteria Criteria { get; set; } = new();
    public OperatingPointRecommendation Recommendation { get; set; } = new();
    public IReadOnlyList<ThresholdSweepPoint> Points { get; set; } = Array.Empty<ThresholdSweepPoint>();
}

public sealed class FalseCallRecommendationSummary
{
    public long? RunId { get; set; }
    public string Status { get; set; } = "Not available";
    public string Mode { get; set; } = string.Empty;
    public double SelectedThreshold { get; set; }
    public double FalseCallRate { get; set; }
    public double PossibleEscapeRate { get; set; }
    public int PossibleEscapeCount { get; set; }
    public double ReviewRate { get; set; }
    public double EstimatedManualReviewMinutes { get; set; }
    public List<string> Limitations { get; set; } = new();
}

public sealed class ValidationMetricSummary
{
    public int TotalImages { get; set; }
    public int KnownGroundTruthImages { get; set; }
    public int UnknownGroundTruthImages { get; set; }
    public double Accuracy { get; set; }
    public double Precision { get; set; }
    public double Recall { get; set; }
    public double FalseCallRate { get; set; }
    public int TruePositive { get; set; }
    public int TrueNegative { get; set; }
    public int FalsePositive { get; set; }
    public int FalseNegative { get; set; }
    public int FalseCall { get; set; }
    public int PossibleEscape { get; set; }
    public int VerifiedNg { get; set; }
    public int OkCount { get; set; }
    public int NgCount { get; set; }
    public int ReviewCount { get; set; }
}

public sealed class ValidationPackagePerformanceSummary
{
    public double AverageMilliseconds { get; set; }
    public double MaxMilliseconds { get; set; }
    public double MinMilliseconds { get; set; }
    public int CountOverOneSecond { get; set; }
    public int TimedImageCount { get; set; }
}

public sealed class ValidationIncludedFile
{
    public string RelativePath { get; set; } = string.Empty;
    public string FileType { get; set; } = string.Empty;
    public long Bytes { get; set; }
}

public enum FactoryReadinessOverallStatus
{
    Go,
    Conditional,
    NoGo,
}

public enum DeploymentProfile
{
    Stage1ImageValidation,
    Stage2CameraPilot,
    Stage3RobotPilot,
    Stage4MesPilot,
    FullFactoryAutomation,
}

public sealed class FactoryReadinessCriteria
{
    public DeploymentProfile DeploymentProfile { get; set; } = DeploymentProfile.Stage1ImageValidation;
    public bool Stage1Only { get; set; } = true;
    public bool RequireSuccessfulLatestValidationPackage { get; set; } = true;
    public bool RequireDatasetQualityEvidence { get; set; } = true;
    public bool RequireProductionModel { get; set; }
    public double MaximumFalseCallRate { get; set; } = 0.10;
    public bool RequireFalseCallEvidence { get; set; }
    public bool RequireNoExportVerificationErrors { get; set; } = true;
    public bool RequireNoPendingMesQueueForProductionMode { get; set; } = true;
    public bool RequireCameraAcceptance { get; set; }
    public bool RequireLightingAcceptance { get; set; }
    public bool RequireRobotAcceptance { get; set; }
    public bool RequireRealHardwareAcceptance { get; set; }
    public bool RequireSoakTestEvidenceForFactoryPilot { get; set; }
}

public sealed class FactoryReadinessReport
{
    public string SchemaVersion { get; set; } = "factory-readiness/v1";
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
    public string DeploymentProfile { get; set; } = AOI_Monitor.Models.DeploymentProfile.Stage1ImageValidation.ToString();
    public string Scope { get; set; } = "Stage 1 readiness";
    public string OverallStatus { get; set; } = FactoryReadinessOverallStatus.Conditional.ToString();
    public List<FactoryReadinessCategory> Categories { get; set; } = new();
    public List<string> BlockingIssues { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public List<string> UnmetCriteria { get; set; } = new();
    public List<string> RecommendedNextActions { get; set; } = new();
    public List<string> KnownLimitations { get; set; } = new();
}

public sealed class FactoryReadinessCategory
{
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = "Conditional";
    public string Evidence { get; set; } = string.Empty;
    public string NextAction { get; set; } = string.Empty;
}

public sealed record FactoryReadinessPackageResult(
    string PackageFolder,
    string SummaryHtmlPath,
    string SummaryJsonPath,
    string ReadmePath);

public record AuditEventRecord(
    long Id,
    DateTime TimestampUtc,
    DateTime LocalTimestamp,
    string UserId,
    string UserRole,
    string StationId,
    string ActionCategory,
    string ActionDetail,
    string RelatedEntityType,
    string RelatedEntityId,
    string RelatedPath);

public record RecipeRevisionRecord(
    long Id,
    string RecipeName,
    string Revision,
    string BoardProgram,
    string OperatorId,
    string DetectionPriority,
    string BackgroundImagePath,
    string RecipeJson,
    DateTime CreatedAtUtc);

public record CalibrationPointInput(
    double ImageX,
    double ImageY,
    double BoardXMillimeters,
    double BoardYMillimeters);

public record CalibrationPointRecord(
    long Id,
    long ProfileId,
    double ImageX,
    double ImageY,
    double BoardXMillimeters,
    double BoardYMillimeters);

public record CalibrationProfileRecord(
    long Id,
    string ProfileName,
    string BoardModel,
    string ViewType,
    string SampleImagePath,
    string OperatorId,
    int PointCount,
    double ScaleX,
    double OffsetX,
    double ScaleY,
    double OffsetY,
    string TransformSummary,
    DateTime CreatedAtUtc,
    IReadOnlyList<CalibrationPointRecord> Points)
{
    public bool HasTransform => PointCount >= 2;
    public string DisplayName => $"{ProfileName} | {BoardModel} | {ViewType} | {PointCount} pt";
}

public record CalibrationTransform(
    bool IsAvailable,
    int PointCount,
    double ScaleX,
    double OffsetX,
    double ScaleY,
    double OffsetY,
    string Summary);

public sealed class RecipeDocument
{
    public string RecipeName { get; set; } = "TBOX_TOP";
    public string BoardProgram { get; set; } = "TBOX-MAIN";
    public string BackgroundImagePath { get; set; } = string.Empty;
    public List<RecipeRoiDocument> Rois { get; set; } = new();
}

public sealed class RecipeRoiDocument
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string RoiType { get; set; } = "Presence";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public double AiScoreThreshold { get; set; } = 0.65;
    public double HeightMin { get; set; }
    public double HeightMax { get; set; }
    public double VolumeMin { get; set; }
    public double VolumeMax { get; set; }
    public bool Enabled { get; set; } = true;
}

public sealed class RecipeDefinition
{
    public string RecipeName { get; set; } = "AOI_RECIPE";
    public string Revision { get; set; } = string.Empty;
    public string BoardProgram { get; set; } = "UNKNOWN";
    public string DetectionPriority { get; set; } = string.Empty;
    public string BackgroundImagePath { get; set; } = string.Empty;
    public List<RecipeRoi> Rois { get; set; } = new();
}

public sealed class RecipeRoi
{
    public string RoiId { get; set; } = string.Empty;
    public string RoiName { get; set; } = string.Empty;
    public string RoiType { get; set; } = "Presence";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public RecipeThresholds Thresholds { get; set; } = new();
    public bool Enabled { get; set; } = true;

    public string DisplayName => string.IsNullOrWhiteSpace(RoiName) ? RoiId : RoiName;
}

public sealed class RecipeThresholds
{
    public double AiScoreThreshold { get; set; } = 0.65;
    public double HeightMin { get; set; }
    public double HeightMax { get; set; }
    public double VolumeMin { get; set; }
    public double VolumeMax { get; set; }
}

public sealed class RecipeLoadResult
{
    public RecipeLoadResult(RecipeDefinition? recipe, IReadOnlyList<string> warnings)
    {
        Recipe = recipe;
        Warnings = warnings;
    }

    public RecipeDefinition? Recipe { get; }
    public IReadOnlyList<string> Warnings { get; }
    public bool HasEnabledRois => Recipe?.Rois.Any(roi => roi.Enabled) == true;
}
