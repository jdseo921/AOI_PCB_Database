using System.IO;

namespace AOI_Monitor.Models;

public enum InspectionEngineStatus
{
    PrototypeEngine,
    MlModelReady,
    MlModelNotTested,
    MlModelMissing,
    MlInvalidLabelMap,
    MlRuntimeError,
    MlUnsupportedOutputFormat,
}

public enum ModelConfigurationTestStatus
{
    NotTested,
    Ready,
    MissingModel,
    InvalidLabelMap,
    RuntimeError,
    UnsupportedOutputFormat,
}

public class InspectionEngineSettings
{
    public string SelectedEngineKey { get; set; } = "pixel-difference";
    public string ActiveModelId { get; set; } = string.Empty;
    public string ActiveModelSha256 { get; set; } = string.Empty;
    public string ActiveModelValidationStatus { get; set; } = ModelConfigurationTestStatus.NotTested.ToString();
    public string ModelFilePath { get; set; } = string.Empty;
    public string ModelVersion { get; set; } = "UNCONFIGURED";
    public int InputImageWidth { get; set; } = 640;
    public int InputImageHeight { get; set; } = 640;
    public string InputTensorName { get; set; } = string.Empty;
    public string OutputTensorName { get; set; } = string.Empty;
    public double ConfidenceThreshold { get; set; } = 0.65;
    public string LabelMapPath { get; set; } = string.Empty;
    public DateTime? LastModelCheckTimestampUtc { get; set; }
    public ModelConfigurationTestStatus LastModelCheckResult { get; set; } = ModelConfigurationTestStatus.NotTested;
    public string LastModelCheckMessage { get; set; } = "Not tested.";
    public string LastModelCheckConfigurationHash { get; set; } = string.Empty;
    public Dictionary<int, string> BuiltInLabelMap { get; set; } = new()
    {
        [0] = "OK",
        [1] = "Solder Bridge",
        [2] = "Insufficient Solder",
        [3] = "Polarity Error",
        [4] = "Tombstone",
        [5] = "Pin Height Error",
        [6] = "Anomaly",
    };

    public bool IsOnnxSelected =>
        string.Equals(SelectedEngineKey, "onnx", StringComparison.OrdinalIgnoreCase);

    public bool HasModelFile =>
        !string.IsNullOrWhiteSpace(ModelFilePath) && File.Exists(ModelFilePath);

    public string EffectiveModelVersion =>
        string.IsNullOrWhiteSpace(ModelVersion) ? Path.GetFileNameWithoutExtension(ModelFilePath) : ModelVersion.Trim();
}

public sealed class InspectionModelConfiguration : InspectionEngineSettings
{
}

public sealed class ModelRegistrationRequest
{
    public string ModelFilePath { get; set; } = string.Empty;
    public string LabelMapPath { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Version { get; set; } = "UNCONFIGURED";
    public DateTime? CreatedAtUtc { get; set; }
    public string InputTensorName { get; set; } = string.Empty;
    public string OutputTensorName { get; set; } = string.Empty;
    public int InputWidth { get; set; } = 640;
    public int InputHeight { get; set; } = 640;
    public double ConfidenceThreshold { get; set; } = 0.65;
    public string Notes { get; set; } = string.Empty;
}

public sealed class ModelRegistryEntry
{
    public string ModelId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Version { get; set; } = "UNCONFIGURED";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime RegisteredAtUtc { get; set; } = DateTime.UtcNow;
    public string SourceFileName { get; set; } = string.Empty;
    public string StoredModelPath { get; set; } = string.Empty;
    public string StoredLabelMapPath { get; set; } = string.Empty;
    public string MetadataPath { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public string InputTensorName { get; set; } = string.Empty;
    public string OutputTensorName { get; set; } = string.Empty;
    public int InputWidth { get; set; } = 640;
    public int InputHeight { get; set; } = 640;
    public double ConfidenceThreshold { get; set; } = 0.65;
    public List<string> Labels { get; set; } = new();
    public ModelConfigurationTestStatus ValidationStatus { get; set; } = ModelConfigurationTestStatus.NotTested;
    public DateTime? LastValidatedAtUtc { get; set; }
    public string ValidationMessage { get; set; } = "Not validated.";
    public string Notes { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public ModelLifecycleState LifecycleState { get; set; } = ModelLifecycleState.Registered;
    public string LatestAcceptanceStatus { get; set; } = string.Empty;
    public long? LatestAcceptanceRunId { get; set; }
    public long? LatestReleasePackageId { get; set; }
    public string LatestReleasePackagePath { get; set; } = string.Empty;
    public string DeploymentWaiverReason { get; set; } = string.Empty;
    public DateTime? WaiverExpiresAtUtc { get; set; }
    public string DeploymentWaivedBy { get; set; } = string.Empty;
    public DateTime? DeploymentWaivedAtUtc { get; set; }
    public DateTime? DeployedAtUtc { get; set; }
    public string RetiredReason { get; set; } = string.Empty;
    public DateTime? RetiredAtUtc { get; set; }
}

public enum ModelLifecycleState
{
    Registered,
    RuntimeValidated,
    AcceptanceFailed,
    AcceptanceConditional,
    AcceptancePassed,
    ProductionCandidate,
    Deployed,
    Retired,
}

public record ModelRegistryRecord(
    long Id,
    string ModelId,
    string DisplayName,
    string Version,
    DateTime CreatedAtUtc,
    DateTime RegisteredAtUtc,
    string SourceFileName,
    string StoredModelPath,
    string StoredLabelMapPath,
    string MetadataPath,
    string Sha256,
    string InputTensorName,
    string OutputTensorName,
    int InputWidth,
    int InputHeight,
    double ConfidenceThreshold,
    IReadOnlyList<string> Labels,
    ModelConfigurationTestStatus ValidationStatus,
    DateTime? LastValidatedAtUtc,
    string ValidationMessage,
    string Notes,
    bool IsActive,
    long? AuditEventId,
    ModelLifecycleState LifecycleState,
    string LatestAcceptanceStatus,
    long? LatestAcceptanceRunId,
    long? LatestReleasePackageId,
    string LatestReleasePackagePath,
    string DeploymentWaiverReason,
    DateTime? WaiverExpiresAtUtc,
    string DeploymentWaivedBy,
    DateTime? DeploymentWaivedAtUtc,
    DateTime? DeployedAtUtc,
    string RetiredReason,
    DateTime? RetiredAtUtc);
