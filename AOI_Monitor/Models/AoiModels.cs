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
