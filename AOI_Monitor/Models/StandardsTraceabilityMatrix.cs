namespace AOI_Monitor.Models;

public sealed class StandardsTraceabilityMatrix
{
    public string StandardOrSource { get; set; } = string.Empty;
    public string PrincipleOrRequirement { get; set; } = string.Empty;
    public string ProjectRequirementId { get; set; } = string.Empty;
    public string EvidenceType { get; set; } = string.Empty;
    public string EvidencePath { get; set; } = string.Empty;
    public string Status { get; set; } = StandardsTraceabilityStatus.Missing;
    public string BlockingLevel { get; set; } = StandardsTraceabilityBlockingLevel.Warning;
    public string Notes { get; set; } = string.Empty;
}

public sealed class StandardsTraceabilityReport
{
    public string SchemaVersion { get; set; } = "standards-traceability-matrix/v1";
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
    public DeploymentProfile DeploymentProfile { get; set; }
    public string OverallStatus { get; set; } = StandardsTraceabilityStatus.Partial;
    public string CertificationStatement { get; set; } =
        "This matrix is standards-aligned evidence for project governance. It is not formal ISO, IEC, ISA, or third-party certification.";
    public int SatisfiedCount { get; set; }
    public int PartialCount { get; set; }
    public int MissingCount { get; set; }
    public int NotApplicableCount { get; set; }
    public int ReleaseBlockerMissingCount { get; set; }
    public List<StandardsTraceabilityMatrix> Items { get; set; } = new();
}

public static class StandardsTraceabilityStatus
{
    public const string Satisfied = "Satisfied";
    public const string Partial = "Partial";
    public const string Missing = "Missing";
    public const string NotApplicable = "NotApplicable";
}

public static class StandardsTraceabilityBlockingLevel
{
    public const string Info = "Info";
    public const string Warning = "Warning";
    public const string ReleaseBlocker = "Release Blocker";
}
