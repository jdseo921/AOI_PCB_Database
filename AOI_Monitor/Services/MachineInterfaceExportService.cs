using System.IO;
using System.Reflection;
using System.Text.Json;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public static class MachineInterfaceExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string ExportInspectionDecision(AnalysisResult result)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "exports", "machine_interface");
        Directory.CreateDirectory(root);

        var contract = BuildContract(result);
        var latestPath = Path.Combine(root, "latest_decision.json");
        var snapshotPath = Path.Combine(root, $"decision_{result.Timestamp:yyyyMMdd_HHmmss}.json");
        var ndjsonPath = Path.Combine(root, "decision_history.ndjson");

        var json = JsonSerializer.Serialize(contract, JsonOptions);
        File.WriteAllText(latestPath, json);
        File.WriteAllText(snapshotPath, json);
        File.AppendAllText(ndjsonPath, JsonSerializer.Serialize(contract, JsonOptions) + Environment.NewLine);
        ExportVerificationService.RecordVerifiedExport("MachineInterfaceLatestJson", latestPath);
        ExportVerificationService.RecordVerifiedExport("MachineInterfaceDecisionJson", snapshotPath);
        ExportVerificationService.RecordVerifiedExport("MachineInterfaceDecisionHistory", ndjsonPath);

        return latestPath;
    }

    public static string ExportDispositionEvent(string action, AnalysisResult? result)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "exports", "machine_interface");
        Directory.CreateDirectory(root);

        var payload = new
        {
            schemaVersion = "1.0.0",
            timestampUtc = DateTime.UtcNow,
            eventType = "Disposition",
            action,
            samplePath = result?.SamplePath,
            verdict = result?.Verdict,
            confidence = result?.Confidence ?? 0,
            reason = result?.DecisionReason,
        };

        var path = Path.Combine(root, "disposition_events.ndjson");
        File.AppendAllText(path, JsonSerializer.Serialize(payload) + Environment.NewLine);
        ExportVerificationService.RecordVerifiedExport("MachineInterfaceDispositionNdjson", path);
        return path;
    }

    private static MachineInterfaceInspectionContract BuildContract(AnalysisResult result)
    {
        var state = WorkflowState.Instance;
        var verdict = result.Verdict.ToUpperInvariant();
        var verdictCode = verdict switch
        {
            "OK" => 0,
            "REVIEW" => 1,
            "NG" => 2,
            _ => 1,
        };

        return new MachineInterfaceInspectionContract
        {
            InspectionId = $"AOI-{result.Timestamp:yyyyMMddHHmmssfff}",
            TimestampUtc = result.Timestamp.ToUniversalTime(),
            StationId = state.StationId,
            BoardProgram = state.BoardProgram,
            ModelVersion = result.ModelVersion,
            Policy = result.PolicyName,
            SampleImagePath = result.SamplePath,
            GoldenImagePath = result.GoldenPath,
            Verdict = verdict,
            VerdictCode = verdictCode,
            Confidence = Math.Round(result.Confidence, 4),
            DifferenceScore = Math.Round(result.DifferenceScore, 2),
            ReviewThreshold = Math.Round(result.ReviewThreshold, 2),
            NgThreshold = Math.Round(result.NgThreshold, 2),
            Hotspot = new RectNormalized
            {
                X = Math.Round(result.Hotspot.X, 4),
                Y = Math.Round(result.Hotspot.Y, 4),
                Width = Math.Round(result.Hotspot.Width, 4),
                Height = Math.Round(result.Hotspot.Height, 4),
            },
            DecisionReason = result.DecisionReason,
            Evidence = result.Evidence,
            MachineHints = new MachineActionHints
            {
                HoldForReview = verdict == "REVIEW",
                StopLineRecommended = verdict == "NG" && result.Confidence >= 0.85,
                RequireHumanConfirmation = verdict != "OK",
            },
            Traceability = new TraceabilityInfo
            {
                AppVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0",
                Source = "AOI_Monitor",
            },
        };
    }
}
