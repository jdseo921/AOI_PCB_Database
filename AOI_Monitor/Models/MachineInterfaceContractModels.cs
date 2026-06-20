namespace AOI_Monitor.Models;

public sealed class MachineInterfaceInspectionContract
{
    public string SchemaVersion { get; set; } = AnalysisResult.CurrentSchemaVersion;
    public string InspectionId { get; set; } = string.Empty;
    public DateTime TimestampUtc { get; set; }
    public string StationId { get; set; } = "AOI-LIB-01";
    public string BoardProgram { get; set; } = "TBOX-MAIN";
    public string BoardId { get; set; } = "UNKNOWN";
    public string LotId { get; set; } = "UNKNOWN";
    public string ViewType { get; set; } = "Top";
    public string SourceFrameId { get; set; } = string.Empty;
    public string SourceKind { get; set; } = "File";
    public bool IsSimulatedSource { get; set; }
    public string ModelVersion { get; set; } = "PIXEL_DIFF_0.1";
    public string Policy { get; set; } = "Minimize False Positives";
    public string SampleImagePath { get; set; } = string.Empty;
    public string? GoldenImagePath { get; set; }
    public string Verdict { get; set; } = "REVIEW";
    public int VerdictCode { get; set; }
    public double Confidence { get; set; }
    public double DifferenceScore { get; set; }
    public double ReviewThreshold { get; set; }
    public double NgThreshold { get; set; }
    public RectNormalized Hotspot { get; set; } = new();
    public string ErrorCode { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public string DecisionReason { get; set; } = string.Empty;
    public List<string> Evidence { get; set; } = new();
    public List<MachineInterfaceDefectContract> Defects { get; set; } = new();
    public MachineActionHints MachineHints { get; set; } = new();
    public TraceabilityInfo Traceability { get; set; } = new();
}

public sealed class MachineInterfaceDefectContract
{
    public string DefectId { get; set; } = string.Empty;
    public int? ClassId { get; set; }
    public string DefectType { get; set; } = string.Empty;
    public string RefDes { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public double Confidence { get; set; }
    public RectNormalized BoundingBox { get; set; } = new();
    public double XPosition { get; set; }
    public double YPosition { get; set; }
    public double WidthPixels { get; set; }
    public double HeightPixels { get; set; }
    public double AreaPixels { get; set; }
    public double? BoardXMillimeters { get; set; }
    public double? BoardYMillimeters { get; set; }
    public double? HeightMicrons { get; set; }
    public double? VolumeEstimate { get; set; }
    public string SourceRoiId { get; set; } = string.Empty;
    public string SideOrViewType { get; set; } = string.Empty;
    public string RoiId { get; set; } = string.Empty;
    public string JudgmentStatus { get; set; } = string.Empty;
}

public sealed class RectNormalized
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

public sealed class MachineActionHints
{
    public bool HoldForReview { get; set; }
    public bool StopLineRecommended { get; set; }
    public bool RequireHumanConfirmation { get; set; }
}

public sealed class TraceabilityInfo
{
    public string AppVersion { get; set; } = string.Empty;
    public string Source { get; set; } = "AOI_Monitor";
}
