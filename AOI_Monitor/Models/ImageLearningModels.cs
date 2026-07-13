namespace AOI_Monitor.Models;

public enum ImageLearningImageRole
{
    GoldenReference,
    OkLearning,
    OkValidation,
    Inspection,
    NgValidation,
}

public enum ImageLearningEvidenceMode
{
    CustomerData,
    InternalDemo,
    SyntheticDemo,
}

public sealed class ImageLearningProject
{
    public long Id { get; set; }
    public string ProjectId { get; set; } = string.Empty;
    public string ProjectName { get; set; } = string.Empty;
    public string BoardModel { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public ImageLearningEvidenceMode EvidenceMode { get; set; } = ImageLearningEvidenceMode.CustomerData;
    public string CreatedBy { get; set; } = "UNKNOWN";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public bool IsArchived { get; set; }
    public string ArchivedBy { get; set; } = string.Empty;
    public DateTime? ArchivedAtUtc { get; set; }
    public string ArchiveReason { get; set; } = string.Empty;
}

public sealed class ImageLearningProjectImage
{
    public long Id { get; set; }
    public string ProjectId { get; set; } = string.Empty;
    public ImageLearningImageRole Role { get; set; } = ImageLearningImageRole.Inspection;
    public string OriginalPath { get; set; } = string.Empty;
    public string VaultPath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public string BoardModel { get; set; } = string.Empty;
    public string LotId { get; set; } = string.Empty;
    public string ViewType { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public string ImportedBy { get; set; } = "UNKNOWN";
    public DateTime ImportedAtUtc { get; set; } = DateTime.UtcNow;
    public string ImageLevelTruth { get; set; } = "UNKNOWN";
    public string Notes { get; set; } = string.Empty;
}

public sealed class LearnedPcbVisualModel
{
    public long Id { get; set; }
    public string ModelId { get; set; } = string.Empty;
    public string ModelVersion { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string ProjectId { get; set; } = string.Empty;
    public int GoldenCount { get; set; }
    public int OkLearningCount { get; set; }
    public int OkValidationCount { get; set; }
    public int InputWidth { get; set; }
    public int InputHeight { get; set; }
    public string AlignmentMode { get; set; } = string.Empty;
    public string BrightnessNormalizationMode { get; set; } = string.Empty;
    public double LearnedThreshold { get; set; }
    public double FalseCallTarget { get; set; }
    public double FalseCallRate { get; set; }
    public double PossibleEscapeRate { get; set; }
    public ImageLearningEvidenceMode EvidenceMode { get; set; } = ImageLearningEvidenceMode.CustomerData;
    public string CreatedBy { get; set; } = "UNKNOWN";
    public long? AuditEventId { get; set; }
    public List<LearnedPcbVisualModelArtifact> Artifacts { get; set; } = new();
}

public sealed class LearnedPcbVisualModelArtifact
{
    public long Id { get; set; }
    public string ModelId { get; set; } = string.Empty;
    public string ArtifactName { get; set; } = string.Empty;
    public string ArtifactPath { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed record ImageLearningArtifactDeletionResult(
    string ModelId,
    int ArtifactRecordsDeleted,
    int FilesDeleted,
    int MissingFiles,
    IReadOnlyList<string> DeletedPaths,
    IReadOnlyList<string> MissingPaths);

public sealed class LearnedVisualModelEvidenceSummary
{
    public string ModelId { get; set; } = string.Empty;
    public string ModelVersion { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string ProjectId { get; set; } = string.Empty;
    public string ProjectName { get; set; } = string.Empty;
    public string BoardModel { get; set; } = string.Empty;
    public string EvidenceMode { get; set; } = string.Empty;
    public int GoldenCount { get; set; }
    public int OkLearningCount { get; set; }
    public int OkValidationCount { get; set; }
    public int ImagesLearnedFrom { get; set; }
    public int InputWidth { get; set; }
    public int InputHeight { get; set; }
    public string AlignmentMode { get; set; } = string.Empty;
    public string BrightnessNormalizationMode { get; set; } = string.Empty;
    public double LearnedThreshold { get; set; }
    public double FalseCallTarget { get; set; }
    public double FalseCallRate { get; set; }
    public double PossibleEscapeRate { get; set; }
    public string LearnedReferenceArtifactPath { get; set; } = string.Empty;
    public string ToleranceMapArtifactPath { get; set; } = string.Empty;
    public string AnomalyThresholdMapArtifactPath { get; set; } = string.Empty;
    public List<string> EvidenceLines { get; set; } = new();
    public string BoundaryNote { get; set; } = "Image-only Stage 1 learning evidence. Not live camera validation or production acceptance.";
}

public sealed class ImageLearningInspectionResult
{
    public long Id { get; set; }
    public string ResultId { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
    public long ProjectImageId { get; set; }
    public string ImageSha256 { get; set; } = string.Empty;
    public string ImagePath { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string Verdict { get; set; } = "REVIEW";
    public double AnomalyScore { get; set; }
    public string DecisionReason { get; set; } = string.Empty;
    public string OperatorId { get; set; } = "UNKNOWN";
    public ImageLearningEvidenceMode EvidenceMode { get; set; } = ImageLearningEvidenceMode.CustomerData;
    public List<ImageLearningAnomalyRegion> AnomalyRegions { get; set; } = new();
}

public sealed class ImageLearningAnomalyRegion
{
    public long Id { get; set; }
    public long InspectionResultId { get; set; }
    public string RegionId { get; set; } = string.Empty;
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public double Score { get; set; }
    public int AreaPixels { get; set; }
    public double Confidence { get; set; }
    public string Severity { get; set; } = "REVIEW";
    public string RegionType { get; set; } = "Anomaly";
    public string Reason { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
}

public sealed class ImageLearningCalibrationResult
{
    public long Id { get; set; }
    public string CalibrationId { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public int OkValidationCount { get; set; }
    public int NgValidationCount { get; set; }
    public double LearnedThreshold { get; set; }
    public double FalseCallTarget { get; set; }
    public double FalseCallRate { get; set; }
    public double PossibleEscapeRate { get; set; }
    public string Status { get; set; } = "REVIEW";
    public string Summary { get; set; } = string.Empty;

    // Held-out estimate: when enough OK Validation images exist, the threshold is selected on a
    // calibration half and the false-call rate is measured on the untouched held-out half, so the
    // headline number is free of threshold-selection bias. Null when the set was too small to split.
    public int HeldOutOkCount { get; set; }
    public int HeldOutFalseCalls { get; set; }
    public double? HeldOutFalseCallRate { get; set; }
}

public sealed class ImageLearningComparisonResult
{
    public long Id { get; set; }
    public string ComparisonId { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
    public long ProjectImageId { get; set; }
    public string ImageSha256 { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public double DifferenceScore { get; set; }
    public double AnomalyScore { get; set; }
    public string Verdict { get; set; } = "REVIEW";
    public string Summary { get; set; } = string.Empty;
}

public sealed record ImageLearningImportResult(
    ImageLearningProjectImage? Image,
    bool Imported,
    bool IsDuplicate,
    string Status,
    string Message);

public sealed record ImageLearningRoleImportSummary(
    ImageLearningImageRole Role,
    string SourceFolder,
    int RequestedCount,
    int ImportedCount,
    int DuplicateCount,
    int UnsupportedCount,
    int InvalidCount,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<ImageLearningImportResult> Results);

public sealed record ImageLearningFolderImportResult(
    ImageLearningProject Project,
    IReadOnlyDictionary<ImageLearningImageRole, int> CountsByRole,
    IReadOnlyList<ImageLearningRoleImportSummary> RoleSummaries,
    IReadOnlyList<string> Warnings,
    string NextSuggestedCommand);

public sealed record ImageLearningProjectValidationResult(
    bool CanTrain,
    int GoldenReferenceCount,
    int OkLearningCount,
    IReadOnlyDictionary<ImageLearningImageRole, int> Counts,
    IReadOnlyList<string> BlockingIssues);

public sealed class ImageOnlyPcbLearningOptions
{
    public int InputWidth { get; set; } = 768;
    public int InputHeight { get; set; } = 768;
    public int SafeFallbackWidth { get; set; } = 512;
    public int SafeFallbackHeight { get; set; } = 512;
    public int AlignmentSearchRadiusPixels { get; set; } = 20;

    /// <summary>
    /// Small-angle rotation search range in degrees (searched in 0.5-degree steps both ways,
    /// refined to 0.25 degrees at the winner). 0 disables rotation search. Ignored for images
    /// smaller than 96px where rotation displacement is sub-pixel.
    /// </summary>
    public double RotationSearchDegrees { get; set; } = 2.0;
    public double TargetMeanBrightness { get; set; } = 128.0;
    public double MinimumTolerance { get; set; } = 4.0;
    public double DefaultLearnedThreshold { get; set; } = 6.0;
    public double FalseCallTarget { get; set; } = 0.05;
    public double MaxAllowedPossibleEscapeRate { get; set; } = 0.0;
    public int MinimumAnomalyAreaPixels { get; set; } = 16;
}

public sealed record ImageOnlyPcbLearningResult(
    LearnedPcbVisualModel Model,
    ImageLearningCalibrationResult Calibration,
    string ArtifactFolder,
    IReadOnlyList<ImageLearningAlignmentRecord> AlignmentRecords,
    IReadOnlyList<ImageLearningSkippedImage> SkippedImages,
    IReadOnlyList<ImageLearningThresholdSweepRow> ThresholdSweep,
    IReadOnlyList<string> Warnings);

public sealed record ImageOnlyPcbInspectionResult(
    ImageLearningInspectionResult InspectionResult,
    ImageLearningComparisonResult ComparisonResult,
    IReadOnlyList<ImageLearningAnomalyRegion> Regions,
    int AlignmentOffsetX,
    int AlignmentOffsetY,
    double AlignmentRotationDegrees = 0);

public sealed record ImageLearningAlignmentRecord(
    long ProjectImageId,
    string FileName,
    ImageLearningImageRole Role,
    int OffsetX,
    int OffsetY,
    double AlignmentError,
    string Status,
    string Message,
    double RotationDegrees = 0);

public sealed record ImageLearningSkippedImage(
    long ProjectImageId,
    string FileName,
    ImageLearningImageRole Role,
    string Path,
    string Reason);

public sealed record ImageLearningThresholdSweepRow(
    double Threshold,
    int OkFalseCalls,
    int OkValidationCount,
    double FalseCallRate,
    int NgPossibleEscapes,
    int NgValidationCount,
    double PossibleEscapeRate,
    bool MeetsFalseCallTarget,
    bool MeetsPossibleEscapeLimit);

public sealed record ImageLearningFalseCallComparisonResult(
    string ProjectId,
    string ModelId,
    DateTime CreatedAtUtc,
    ImageLearningFalseCallComparisonMetrics Metrics,
    IReadOnlyList<ImageLearningFalseCallImageResult> ImageResults,
    IReadOnlyList<ImageLearningFalseCallThresholdSweepRow> ThresholdSweep,
    IReadOnlyList<string> Warnings,
    string OutputFolder,
    string HtmlReportPath,
    string JsonReportPath,
    string ResultsCsvPath,
    string ThresholdSweepCsvPath);

public sealed record ImageLearningFalseCallComparisonMetrics(
    int OkValidationCount,
    int InspectionCount,
    int NgValidationCount,
    int FalseCallsBeforeLearning,
    int FalseCallsAfterLearning,
    double FalseCallRateBeforeLearning,
    double FalseCallRateAfterLearning,
    double FalseCallReductionPercentage,
    int AnomalyRegionsBeforeLearning,
    int AnomalyRegionsAfterLearning,
    int PossibleEscapes,
    double? PossibleEscapeRate,
    int ReviewBurdenBeforeLearning,
    int ReviewBurdenAfterLearning,
    double ReviewBurdenRateBeforeLearning,
    double ReviewBurdenRateAfterLearning,
    double BaselineReviewThreshold,
    double? RecommendedThreshold,
    string RecommendationStatus,
    string MissedDefectEvidenceNote);

public sealed record ImageLearningFalseCallImageResult(
    long ProjectImageId,
    string FileName,
    ImageLearningImageRole Role,
    string Truth,
    string ImagePath,
    double BaselineScore,
    string BaselineVerdict,
    int BaselineAnomalyRegions,
    double LearnedScore,
    string LearnedVerdict,
    int LearnedAnomalyRegions,
    bool IsFalseCallBeforeLearning,
    bool IsFalseCallAfterLearning,
    bool IsPossibleEscapeAfterLearning,
    string Notes);

public sealed record ImageLearningFalseCallThresholdSweepRow(
    double Threshold,
    int FalseCalls,
    int OkValidationCount,
    double FalseCallRate,
    int PossibleEscapes,
    int NgValidationCount,
    double PossibleEscapeRate,
    int ReviewCount,
    int EvaluatedCount,
    double ReviewRate,
    bool MeetsFalseCallTarget,
    bool MeetsPossibleEscapeLimit,
    bool IsRecommended);

public sealed class ImageLearningOverlayExportOptions
{
    public int MaxBatchExamples { get; set; } = 10;
    public ImageOnlyPcbLearningOptions? LearningOptions { get; set; }
}

public sealed record ImageLearningOverlayExportResult(
    string ProjectId,
    string ModelId,
    DateTime CreatedAtUtc,
    string OutputFolder,
    string ManifestPath,
    IReadOnlyList<ImageLearningOverlayExportItem> Items,
    IReadOnlyList<string> Warnings,
    long ExportHistoryId);

public sealed record ImageLearningOverlayExportItem(
    string Category,
    long? InspectionResultId,
    long ProjectImageId,
    string FileName,
    ImageLearningImageRole Role,
    string Verdict,
    double AnomalyScore,
    int RegionCount,
    string OriginalPath,
    string HeatmapPath,
    string AnnotatedOverlayPath,
    string ReferenceVsInspectedPath,
    string BaselineVsLearnedPath,
    IReadOnlyList<string> Warnings);

public sealed class ImageOnlyLearningReportOptions
{
    public int OkValidationExampleCount { get; set; } = 3;
    public int AnomalyExampleCount { get; set; } = 3;
    public bool GeneratePdf { get; set; } = true;
    public ImageOnlyPcbLearningOptions? LearningOptions { get; set; }
}

public sealed record ImageOnlyLearningReportResult(
    string ProjectId,
    string ModelId,
    DateTime CreatedAtUtc,
    string OutputFolder,
    string HtmlPath,
    string JsonPath,
    string PdfPath,
    string FalseCallComparisonHtmlPath,
    string VisualEvidenceManifestPath,
    IReadOnlyList<string> OkValidationExamplePaths,
    IReadOnlyList<string> AnomalyExampleOverlayPaths,
    string BeforeAfterOverlayPath,
    IReadOnlyList<string> Warnings,
    long ExportHistoryId);
