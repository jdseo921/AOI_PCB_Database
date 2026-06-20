namespace AOI_Monitor.Models;

public enum DiagnosticStatus
{
    OK,
    Warn,
    Error,
}

public sealed class DiagnosticCheck
{
    public string Category { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DiagnosticStatus Status { get; set; }
    public string Message { get; set; } = string.Empty;
    public string Remediation { get; set; } = string.Empty;
}

public sealed class SystemDiagnosticReport
{
    public string SchemaVersion { get; set; } = "system-diagnostics/v1";
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
    public string StorageRoot { get; set; } = string.Empty;
    public List<DiagnosticCheck> Checks { get; set; } = new();
    public int OkCount => Checks.Count(check => check.Status == DiagnosticStatus.OK);
    public int WarnCount => Checks.Count(check => check.Status == DiagnosticStatus.Warn);
    public int ErrorCount => Checks.Count(check => check.Status == DiagnosticStatus.Error);
}

public sealed class FirstRunSettings
{
    public const string CurrentWizardVersion = "first-run/v1";

    public bool Completed { get; set; }
    public string VersionCompleted { get; set; } = string.Empty;
    public DateTime? CompletedAtUtc { get; set; }
}

public sealed class DiagnosticExportResult
{
    public string JsonPath { get; init; } = string.Empty;
    public string HtmlPath { get; init; } = string.Empty;
    public string TextPath { get; init; } = string.Empty;
}
