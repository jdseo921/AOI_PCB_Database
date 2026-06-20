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
    int FailedCount);

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
    public List<string> Messages { get; set; } = new();
}

public sealed class ValidationPackageManifest
{
    public string SchemaVersion { get; set; } = "stage1-validation-package/v1";
    public string PackageId { get; set; } = string.Empty;
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
    public string AppVersion { get; set; } = string.Empty;
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
    public string AcceptanceStatus { get; set; } = "CONDITIONAL";
    public ValidationAcceptanceCriteria Criteria { get; set; } = new();
    public List<ValidationIncludedFile> IncludedFiles { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
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
