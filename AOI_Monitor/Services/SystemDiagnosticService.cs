using System.IO;
using System.Text;
using System.Text.Json;
using AOI_Monitor.Data;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public static class SystemDiagnosticService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static SystemDiagnosticReport RunDiagnostics(string? storageRootOverride = null)
    {
        var storageRoot = string.IsNullOrWhiteSpace(storageRootOverride)
            ? AoiDatabase.StorageRoot
            : Path.GetFullPath(storageRootOverride.Trim());
        var report = new SystemDiagnosticReport
        {
            GeneratedAtUtc = DateTime.UtcNow,
            StorageRoot = storageRoot,
        };

        AddStorageChecks(report, storageRoot);
        AddDatabaseChecks(report, storageRoot);
        AddVaultChecks(report, storageRoot);
        AddExportChecks(report, storageRoot);
        AddInspectionEngineChecks(report);
        AddCameraChecks(report);
        AddIntegrationChecks(report);
        AddModelRegistryChecks(report);

        return report;
    }

    public static DiagnosticExportResult ExportReport(SystemDiagnosticReport report, string? outputRoot = null)
    {
        var root = string.IsNullOrWhiteSpace(outputRoot)
            ? Path.Combine(AoiDatabase.StorageRoot, "exports", "diagnostics")
            : outputRoot.Trim();
        Directory.CreateDirectory(root);

        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmssfff");
        var jsonPath = Path.Combine(root, $"diagnostics_{stamp}.json");
        var htmlPath = Path.Combine(root, $"diagnostics_{stamp}.html");
        var textPath = Path.Combine(root, $"diagnostics_{stamp}.txt");

        File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, JsonOptions), Encoding.UTF8);
        File.WriteAllText(htmlPath, BuildHtml(report), Encoding.UTF8);
        File.WriteAllText(textPath, BuildText(report), Encoding.UTF8);

        var status = report.ErrorCount == 0 ? "OK" : "WARN";
        ExportVerificationService.RecordVerifiedExport("DiagnosticsReportJson", jsonPath, status);
        ExportVerificationService.RecordVerifiedExport("DiagnosticsReportHtml", htmlPath, status);
        ExportVerificationService.RecordVerifiedExport("DiagnosticsReportText", textPath, status);
        return new DiagnosticExportResult
        {
            JsonPath = jsonPath,
            HtmlPath = htmlPath,
            TextPath = textPath,
        };
    }

    private static void AddStorageChecks(SystemDiagnosticReport report, string storageRoot)
    {
        if (string.IsNullOrWhiteSpace(storageRoot))
        {
            Add(report, "Storage", "Storage root", DiagnosticStatus.Error, "Storage root is blank.", "Choose a writable local folder in Settings or the setup wizard.");
            return;
        }

        if (File.Exists(storageRoot))
        {
            Add(report, "Storage", "Storage root", DiagnosticStatus.Error, "Storage root points to a file, not a folder.", "Choose a folder path for AOI Monitor storage.");
            return;
        }

        try
        {
            Directory.CreateDirectory(storageRoot);
            var probe = ProbeWrite(storageRoot);
            Add(report, "Storage", "Storage root", DiagnosticStatus.OK, $"Writable storage root: {storageRoot}", $"Probe file created and removed: {Path.GetFileName(probe)}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Add(report, "Storage", "Storage root", DiagnosticStatus.Error, $"Storage root is not writable: {ex.Message}", "Choose a local folder where the current Windows user has read/write access.");
        }
    }

    private static void AddDatabaseChecks(SystemDiagnosticReport report, string storageRoot)
    {
        var isActiveStorageRoot = SamePath(storageRoot, AoiDatabase.StorageRoot);
        if (!isActiveStorageRoot)
        {
            Add(report, "Database", "SQLite integrity", DiagnosticStatus.Warn, "Database integrity check skipped for non-active storage root.", "Apply the storage root before running a live SQLite integrity check.");
            return;
        }

        try
        {
            AoiDatabase.Initialize();
            var integrity = AoiDatabase.RunIntegrityCheck();
            var ok = string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase);
            Add(
                report,
                "Database",
                "SQLite integrity",
                ok ? DiagnosticStatus.OK : DiagnosticStatus.Error,
                ok ? $"SQLite opened successfully: {AoiDatabase.DatabasePath}" : $"SQLite integrity check returned: {integrity}",
                ok ? "No action required." : "Back up the storage root and inspect the SQLite database.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Add(report, "Database", "SQLite integrity", DiagnosticStatus.Error, $"SQLite could not be opened: {ex.Message}", "Verify the storage folder and database file are not locked or read-only.");
        }
    }

    private static void AddVaultChecks(SystemDiagnosticReport report, string storageRoot)
    {
        CheckFolderWritable(report, "Image Vault", "Image vault folder", Path.Combine(storageRoot, "image_vault"), DiagnosticStatus.OK);
        CheckFolderWritable(report, "Image Vault", "Training folder", Path.Combine(storageRoot, "image_vault", "training"), DiagnosticStatus.OK);
    }

    private static void AddExportChecks(SystemDiagnosticReport report, string storageRoot)
        => CheckFolderWritable(report, "Exports", "Runtime export folder", Path.Combine(storageRoot, "exports"), DiagnosticStatus.OK);

    private static void AddInspectionEngineChecks(SystemDiagnosticReport report)
    {
        var configuration = InspectionModelConfigurationService.Load();
        var status = InspectionModelConfigurationService.GetStatus(configuration);
        var statusText = InspectionModelConfigurationService.GetStatusText(status);
        var diagnosticStatus = status switch
        {
            InspectionEngineStatus.PrototypeEngine => DiagnosticStatus.OK,
            InspectionEngineStatus.MlModelReady => DiagnosticStatus.OK,
            InspectionEngineStatus.LearnedVisualModelReady => DiagnosticStatus.Warn,
            InspectionEngineStatus.MlModelMissing => DiagnosticStatus.Warn,
            InspectionEngineStatus.MlModelNotTested => DiagnosticStatus.Warn,
            _ => DiagnosticStatus.Error,
        };

        Add(
            report,
            "Inspection Engine",
            "Active engine",
            diagnosticStatus,
            $"{statusText}; selected={configuration.SelectedEngineKey}; version={configuration.ModelVersion}",
            diagnosticStatus == DiagnosticStatus.OK
                ? "No action required for Stage 1 pixel-difference validation."
                : configuration.IsLearnedVisualModelSelected
                    ? "Learned visual model is image-only Stage 1 evidence; use customer validation and live hardware checks before acceptance."
                    : "Use Pixel Difference for Stage 1 or validate the ONNX model registry/configuration.");
    }

    private static void AddCameraChecks(SystemDiagnosticReport report)
    {
        var source = CameraSourceFactory.ActiveSource;
        var status = source.ConnectionStatus switch
        {
            CameraSourceStatus.Ready => DiagnosticStatus.OK,
            CameraSourceStatus.Simulated => DiagnosticStatus.OK,
            CameraSourceStatus.Error => DiagnosticStatus.Error,
            _ => DiagnosticStatus.Warn,
        };

        Add(
            report,
            "Camera",
            "Active camera source",
            status,
            $"{source.Name}: {source.StatusMessage}",
            status == DiagnosticStatus.Warn
                ? "No real camera is required for Stage 1. Configure Folder Simulation for demo acquisition."
                : "No action required.");
    }

    private static void AddIntegrationChecks(SystemDiagnosticReport report)
    {
        AddIntegration(report, "Lighting", IntegrationBoundaryRegistry.LightingController, "Lighting is optional for Stage 1. Simulated or NotConnected is acceptable.");
        AddIntegration(report, "Robot", IntegrationBoundaryRegistry.RobotController, "Robot/handler is optional for Stage 1. Simulated or NotConnected is acceptable.");
        AddMesReadiness(report);
        AddIntegration(report, "E-Stop", IntegrationBoundaryRegistry.EmergencyStopMonitor, "No safety-certified interlock is claimed in this prototype.");
    }

    private static void AddMesReadiness(SystemDiagnosticReport report)
    {
        var readiness = MesSpoolService.EvaluateReadiness();
        var status = readiness.Status switch
        {
            "MES REST Ready" or "MES Mock Only" or "MES Not Configured" => DiagnosticStatus.OK,
            "MES Queue Pending" => DiagnosticStatus.Warn,
            _ => DiagnosticStatus.Error,
        };
        Add(
            report,
            "MES / Traceability",
            "MES REST / spool readiness",
            status,
            $"{readiness.Status}; mode={readiness.Mode}; pending={readiness.PendingCount}; failed={readiness.FailedCount}; sent={readiness.SentCount}; abandoned={readiness.AbandonedCount}",
            readiness.PendingCount > 0 || readiness.FailedCount > 0
                ? "Open Log & Export > MES Queue and retry or resolve failed spool items."
                : "MES writeback is optional for Stage 1. Local exports remain available.");
    }

    private static void AddModelRegistryChecks(SystemDiagnosticReport report)
    {
        var configuration = InspectionModelConfigurationService.Load();
        if (!configuration.IsOnnxSelected)
        {
            Add(report, "Model Registry", "ONNX model configuration", DiagnosticStatus.OK, "ONNX is not selected. Pixel Difference engine is active.", "No model registry action required for Stage 1.");
            return;
        }

        var activeModel = ModelRegistryService.GetActiveModel();
        if (activeModel is null)
        {
            Add(report, "Model Registry", "ONNX model configuration", DiagnosticStatus.Warn, "ONNX is selected but no active registered model was found.", "Register and validate a local ONNX model or switch to Pixel Difference.");
            return;
        }

        var status = activeModel.ValidationStatus == ModelConfigurationTestStatus.Ready
            ? DiagnosticStatus.OK
            : DiagnosticStatus.Warn;
        Add(report, "Model Registry", "ONNX model configuration", status, $"Active model {activeModel.ModelId} v{activeModel.Version}; validation={activeModel.ValidationStatus}.", "Validate the active model before evaluator/customer runs.");
    }

    private static void CheckFolderWritable(SystemDiagnosticReport report, string category, string name, string folderPath, DiagnosticStatus okStatus)
    {
        try
        {
            Directory.CreateDirectory(folderPath);
            ProbeWrite(folderPath);
            Add(report, category, name, okStatus, $"Writable: {folderPath}", "No action required.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Add(report, category, name, DiagnosticStatus.Error, $"{name} is not writable: {ex.Message}", "Choose a writable local storage root or fix folder permissions.");
        }
    }

    private static void AddIntegration(SystemDiagnosticReport report, string category, IIntegrationEndpoint endpoint, string optionalMessage)
    {
        var status = endpoint.Status switch
        {
            IntegrationConnectionStatus.Ready => DiagnosticStatus.OK,
            IntegrationConnectionStatus.Simulated => DiagnosticStatus.OK,
            IntegrationConnectionStatus.Error => DiagnosticStatus.Error,
            _ => DiagnosticStatus.Warn,
        };

        Add(
            report,
            category,
            endpoint.Name,
            status,
            endpoint.StatusMessage,
            status == DiagnosticStatus.Warn ? optionalMessage : "No action required.");
    }

    private static string ProbeWrite(string folderPath)
    {
        var path = Path.Combine(folderPath, $".aoi_write_probe_{Guid.NewGuid():N}.tmp");
        File.WriteAllText(path, "probe");
        File.Delete(path);
        return path;
    }

    private static void Add(SystemDiagnosticReport report, string category, string name, DiagnosticStatus status, string message, string remediation)
        => report.Checks.Add(new DiagnosticCheck
        {
            Category = category,
            Name = name,
            Status = status,
            Message = message,
            Remediation = remediation,
        });

    private static string BuildText(SystemDiagnosticReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("AOI Monitor Diagnostics Report");
        sb.AppendLine($"GeneratedUtc: {report.GeneratedAtUtc:O}");
        sb.AppendLine($"StorageRoot: {report.StorageRoot}");
        sb.AppendLine($"Summary: OK={report.OkCount}, Warn={report.WarnCount}, Error={report.ErrorCount}");
        foreach (var check in report.Checks)
            sb.AppendLine($"[{check.Status}] {check.Category} / {check.Name}: {check.Message} Remediation: {check.Remediation}");
        return sb.ToString();
    }

    private static string BuildHtml(SystemDiagnosticReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html><html><head><meta charset=\"utf-8\"><title>AOI Diagnostics</title>");
        sb.AppendLine("<style>body{font-family:Segoe UI,Arial;background:#0b0e10;color:#dce5eb}table{border-collapse:collapse;width:100%}td,th{border:1px solid #343d44;padding:6px}th{background:#1a2024}.OK{color:#50f56e}.Warn{color:#e1a334}.Error{color:#f27777}</style>");
        sb.AppendLine("</head><body>");
        sb.AppendLine("<h1>AOI Monitor Diagnostics Report</h1>");
        sb.AppendLine($"<p>Generated UTC: {Html(report.GeneratedAtUtc.ToString("O"))}<br>Storage root: {Html(report.StorageRoot)}<br>OK={report.OkCount}, Warn={report.WarnCount}, Error={report.ErrorCount}</p>");
        sb.AppendLine("<table><thead><tr><th>Status</th><th>Category</th><th>Name</th><th>Message</th><th>Remediation</th></tr></thead><tbody>");
        foreach (var check in report.Checks)
            sb.AppendLine($"<tr><td class=\"{check.Status}\">{check.Status}</td><td>{Html(check.Category)}</td><td>{Html(check.Name)}</td><td>{Html(check.Message)}</td><td>{Html(check.Remediation)}</td></tr>");
        sb.AppendLine("</tbody></table></body></html>");
        return sb.ToString();
    }

    private static bool SamePath(string left, string right)
        => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static string Html(string value)
        => value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);
}
