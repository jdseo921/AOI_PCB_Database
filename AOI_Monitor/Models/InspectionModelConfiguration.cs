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
