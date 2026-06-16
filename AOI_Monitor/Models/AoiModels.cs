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
    DateTime CreatedAtUtc);

public sealed class LogFilter
{
    public DateTime? FromDate { get; set; }
    public DateTime? ToDate { get; set; }
    public string? BoardProgram { get; set; }
    public string? OperatorId { get; set; }
    public string? Result { get; set; }
}

public record InspectionHistoryRecord(
    long Id,
    DateTime CreatedAtUtc,
    string BoardProgram,
    string OperatorId,
    string InspectionEngine,
    string ModelVersion,
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
    double HotspotHeight);

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
    string OperatorId);

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
}
