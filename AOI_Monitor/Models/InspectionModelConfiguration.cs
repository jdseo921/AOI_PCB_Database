using System.IO;

namespace AOI_Monitor.Models;

public enum InspectionEngineStatus
{
    PrototypeEngine,
    MlModelConfigured,
    MlModelMissing,
    MlRuntimeError,
}

public class InspectionEngineSettings
{
    public string SelectedEngineKey { get; set; } = "pixel-difference";
    public string ModelFilePath { get; set; } = string.Empty;
    public string ModelVersion { get; set; } = "UNCONFIGURED";
    public int InputImageWidth { get; set; } = 640;
    public int InputImageHeight { get; set; } = 640;
    public double ConfidenceThreshold { get; set; } = 0.65;
    public string LabelMapPath { get; set; } = string.Empty;
    public Dictionary<int, string> BuiltInLabelMap { get; set; } = new()
    {
        [0] = "OK",
        [1] = "Presence",
        [2] = "Polarity",
        [3] = "Solder Bridge",
        [4] = "Height",
        [5] = "Anomaly",
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
