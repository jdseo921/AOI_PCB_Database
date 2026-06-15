using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public static class ImageAnalysisService
{
    public static AnalysisResult Analyze(string samplePath, string? goldenPath, DetectionPriority priority)
    {
        var engine = InspectionEngineFactory.Create();
        return engine.Analyze(samplePath, goldenPath, priority);
    }
}
