namespace AOI_Monitor.Models;

public sealed class RobotInspectionContract
{
    public string SchemaVersion { get; set; } = "1.0.0";
    public string InspectionId { get; set; } = string.Empty;
    public DateTime TimestampUtc { get; set; }
    public string StationId { get; set; } = "AOI-LIB-01";
    public string BoardProgram { get; set; } = "TBOX-MAIN";
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
    public string DecisionReason { get; set; } = string.Empty;
    public List<string> Evidence { get; set; } = new();
    public MachineActionHints MachineHints { get; set; } = new();
    public TraceabilityInfo Traceability { get; set; } = new();
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
