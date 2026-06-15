using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public interface IInspectionEngine
{
    string Name { get; }
    string Version { get; }

    AnalysisResult Analyze(string samplePath, string? goldenPath, DetectionPriority priority);
}
