using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using AOI_Monitor.Data;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public sealed class CustomerPilotSnapshot
{
    public CustomerPilotSessionRecord Session { get; set; } = new();
    public List<CustomerPilotStepRecord> Steps { get; set; } = new();
}

public sealed record CustomerPilotExportResult(string Folder, string JsonPath, string HtmlPath, string PdfPath);

public static class CustomerPilotWizardService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static readonly CustomerPilotStepKind[] OrderedSteps =
    [
        CustomerPilotStepKind.ConfirmDeploymentProfile,
        CustomerPilotStepKind.RunSystemDiagnostics,
        CustomerPilotStepKind.SelectCustomerDataset,
        CustomerPilotStepKind.RunDatasetPreflight,
        CustomerPilotStepKind.RunBatchValidation,
        CustomerPilotStepKind.RunFalseCallReduction,
        CustomerPilotStepKind.RunModelAcceptance,
        CustomerPilotStepKind.ExportCustomerValidationPackage,
        CustomerPilotStepKind.RunStage2Acceptance,
        CustomerPilotStepKind.ExportReadinessAndChecklist,
    ];

    public static CustomerPilotSnapshot StartNew(DeploymentProfile profile, string operatorId)
    {
        AoiDatabase.Initialize();
        var session = new CustomerPilotSessionRecord
        {
            DeploymentProfile = profile,
            OperatorId = string.IsNullOrWhiteSpace(operatorId) ? "UNKNOWN" : operatorId.Trim(),
            Status = "InProgress",
        };
        AoiDatabase.CreateCustomerPilotSession(session);

        for (var i = 0; i < OrderedSteps.Length; i++)
        {
            var step = new CustomerPilotStepRecord
            {
                SessionId = session.Id,
                StepKey = OrderedSteps[i],
                StepOrder = i + 1,
                Status = StepIsOutOfScope(profile, OrderedSteps[i])
                    ? CustomerPilotStepStatus.Skipped
                    : CustomerPilotStepStatus.NotStarted,
                Messages = StepIsOutOfScope(profile, OrderedSteps[i])
                    ? new List<string> { "Not required for Stage 1 customer image validation." }
                    : new List<string>(),
            };
            AoiDatabase.UpsertCustomerPilotStep(step);
        }

        AoiDatabase.RecordAuditEvent(
            "CUSTOMER_PILOT_START",
            $"Customer pilot wizard started for {profile}.",
            operatorWithRole: session.OperatorId,
            relatedEntityType: "CustomerPilotSession",
            relatedEntityId: session.Id.ToString(CultureInfo.InvariantCulture));
        return Load(session.Id);
    }

    public static CustomerPilotSnapshot StartOrResume(DeploymentProfile profile, string operatorId)
    {
        var latest = AoiDatabase.GetLatestIncompleteCustomerPilotSession();
        return latest is null ? StartNew(profile, operatorId) : Load(latest.Id);
    }

    public static CustomerPilotSnapshot Load(long sessionId)
    {
        var session = AoiDatabase.GetCustomerPilotSession(sessionId)
            ?? throw new InvalidOperationException($"Customer pilot session {sessionId} was not found.");
        var steps = AoiDatabase.GetCustomerPilotSteps(sessionId).ToList();
        return new CustomerPilotSnapshot { Session = session, Steps = steps };
    }

    public static CustomerPilotSnapshot ConfirmDeploymentProfile(long sessionId, DeploymentProfile profile, string operatorId)
    {
        var snapshot = Load(sessionId);
        snapshot.Session.DeploymentProfile = profile;
        snapshot.Session.OperatorId = string.IsNullOrWhiteSpace(operatorId) ? snapshot.Session.OperatorId : operatorId.Trim();
        AoiDatabase.UpdateCustomerPilotSession(snapshot.Session);
        RecordStep(sessionId, CustomerPilotStepKind.ConfirmDeploymentProfile, CustomerPilotStepStatus.Passed, string.Empty, $"Deployment profile confirmed: {FactoryReadinessService.DisplayName(profile)}.");
        return Load(sessionId);
    }

    public static CustomerPilotSnapshot RecordDatasetSelection(long sessionId, string datasetFolder, string manifestPath)
    {
        var snapshot = Load(sessionId);
        snapshot.Session.DatasetFolder = datasetFolder?.Trim() ?? string.Empty;
        snapshot.Session.ManifestPath = manifestPath?.Trim() ?? string.Empty;
        AoiDatabase.UpdateCustomerPilotSession(snapshot.Session);
        RecordStep(
            sessionId,
            CustomerPilotStepKind.SelectCustomerDataset,
            Directory.Exists(snapshot.Session.DatasetFolder) && File.Exists(snapshot.Session.ManifestPath) ? CustomerPilotStepStatus.Passed : CustomerPilotStepStatus.Failed,
            snapshot.Session.ManifestPath,
            $"Dataset folder={snapshot.Session.DatasetFolder}. Manifest={snapshot.Session.ManifestPath}.");
        return Load(sessionId);
    }

    public static CustomerPilotSnapshot RunSystemDiagnostics(long sessionId)
    {
        var report = SystemDiagnosticService.RunDiagnostics();
        var export = SystemDiagnosticService.ExportReport(report, Path.Combine(SessionExportFolder(sessionId), "diagnostics"));
        var status = report.ErrorCount > 0
            ? CustomerPilotStepStatus.Failed
            : report.WarnCount > 0
                ? CustomerPilotStepStatus.Conditional
                : CustomerPilotStepStatus.Passed;
        RecordStep(sessionId, CustomerPilotStepKind.RunSystemDiagnostics, status, export.JsonPath, $"Diagnostics: errors={report.ErrorCount}; warnings={report.WarnCount}.");
        return Load(sessionId);
    }

    public static CustomerPilotSnapshot RunDatasetPreflight(long sessionId)
    {
        var snapshot = Load(sessionId);
        var result = CustomerDatasetPreflightService.Validate(snapshot.Session.DatasetFolder, snapshot.Session.ManifestPath);
        var folder = Path.Combine(SessionExportFolder(sessionId), "dataset_preflight");
        Directory.CreateDirectory(folder);
        var jsonPath = Path.Combine(folder, "dataset_preflight.json");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(result, JsonOptions), Encoding.UTF8);
        RecordStep(
            sessionId,
            CustomerPilotStepKind.RunDatasetPreflight,
            result.Status.Equals("PASS", StringComparison.OrdinalIgnoreCase) ? CustomerPilotStepStatus.Passed : CustomerPilotStepStatus.Failed,
            jsonPath,
            new[] { $"Dataset preflight status={result.Status}; rows={result.ManifestRows}; blocking={result.BlockingFailures.Count}; warnings={result.Warnings.Count}." }
                .Concat(result.BlockingFailures)
                .Concat(result.Warnings));
        return Load(sessionId);
    }

    public static CustomerPilotSnapshot RecordBatchValidationEvidence(long sessionId, string evidencePath, CustomerPilotStepStatus status, string message)
    {
        EnsurePreflightAllowsBatchValidation(sessionId);
        RecordStep(sessionId, CustomerPilotStepKind.RunBatchValidation, status, evidencePath, message);
        return Load(sessionId);
    }

    public static CustomerPilotSnapshot RecordFalseCallReductionEvidence(long sessionId, string evidencePath, CustomerPilotStepStatus status, string message)
    {
        RecordStep(sessionId, CustomerPilotStepKind.RunFalseCallReduction, status, evidencePath, message);
        return Load(sessionId);
    }

    public static CustomerPilotSnapshot EvaluateModelAcceptance(long sessionId)
    {
        var configuration = InspectionModelConfigurationService.Load();
        var status = InspectionModelConfigurationService.GetStatus(configuration);
        if (status != InspectionEngineStatus.MlModelReady)
        {
            RecordStep(sessionId, CustomerPilotStepKind.RunModelAcceptance, CustomerPilotStepStatus.Skipped, string.Empty, "No ONNX model is configured. Prototype engine limitation remains in customer evidence.");
            return Load(sessionId);
        }

        var latest = AoiDatabase.GetLatestModelAcceptanceRun(configuration.ActiveModelId);
        if (latest is null)
        {
            RecordStep(sessionId, CustomerPilotStepKind.RunModelAcceptance, CustomerPilotStepStatus.Failed, string.Empty, $"ONNX model {configuration.ActiveModelId} is configured but no model acceptance run exists.");
        }
        else
        {
            var passed = latest.Status.Equals("PASS", StringComparison.OrdinalIgnoreCase);
            RecordStep(sessionId, CustomerPilotStepKind.RunModelAcceptance, passed ? CustomerPilotStepStatus.Passed : CustomerPilotStepStatus.Failed, $"SQLite:ModelAcceptanceRuns/{latest.Id}", $"Model acceptance status={latest.Status}; model={latest.ModelId}; productionCandidate={latest.IsProductionCandidate}.");
        }

        return Load(sessionId);
    }

    public static CustomerPilotSnapshot EvaluateCustomerValidationPackage(long sessionId)
    {
        var latest = AoiDatabase.GetValidationPackages(1).FirstOrDefault();
        RecordStep(
            sessionId,
            CustomerPilotStepKind.ExportCustomerValidationPackage,
            latest is null ? CustomerPilotStepStatus.Failed : CustomerPilotStepStatus.Passed,
            latest?.PackagePath ?? string.Empty,
            latest is null ? "No customer validation package export is recorded." : $"Latest package {latest.PackageId}; acceptance={latest.AcceptanceStatus}; manifest={latest.ManifestPath}.");
        return Load(sessionId);
    }

    public static CustomerPilotSnapshot EvaluateStage2Acceptance(long sessionId)
    {
        var snapshot = Load(sessionId);
        if (StepIsOutOfScope(snapshot.Session.DeploymentProfile, CustomerPilotStepKind.RunStage2Acceptance))
        {
            RecordStep(sessionId, CustomerPilotStepKind.RunStage2Acceptance, CustomerPilotStepStatus.Skipped, string.Empty, "Stage 2 hardware evidence is not required for the Stage 1 profile.");
            return Load(sessionId);
        }

        var camera = AoiDatabase.GetLatestCameraAcceptanceRun(realHardwareOnly: false);
        var lighting = AoiDatabase.GetLatestLightingAcceptanceRun();
        var profile3D = AoiDatabase.GetLatestProfile3DAcceptanceRun();
        var missing = new List<string>();
        if (camera is null || !IsPassOrWarn(camera.Status)) missing.Add("camera acceptance");
        if (lighting is null || !IsPassOrWarn(lighting.Status)) missing.Add("lighting acceptance");
        if (profile3D is null || !IsPassOrWarn(profile3D.Status)) missing.Add("3D profile acceptance");

        var messages = new List<string>
        {
            camera is null ? "No camera acceptance run." : $"Camera status={camera.Status}; realHardware={camera.IsRealHardware}; source={camera.SourceKey}.",
            lighting is null ? "No lighting acceptance run." : $"Lighting status={lighting.Status}; simulated={lighting.IsSimulated}; mode={lighting.Mode}.",
            profile3D is null ? "No 3D profile acceptance run." : $"3D status={profile3D.Status}; simulated={profile3D.IsSimulated}; source={profile3D.SourceKind}.",
        };
        if (missing.Count > 0)
            messages.Add($"Missing required Stage 2 evidence: {string.Join(", ", missing)}.");
        if ((camera?.IsRealHardware == false) || (lighting?.IsSimulated == true) || (profile3D?.IsSimulated == true))
            messages.Add("Simulation/sample evidence is labeled for pilot review and is not real production hardware validation.");

        RecordStep(sessionId, CustomerPilotStepKind.RunStage2Acceptance, missing.Count == 0 ? CustomerPilotStepStatus.Passed : CustomerPilotStepStatus.Failed, string.Empty, messages);
        return Load(sessionId);
    }

    public static CustomerPilotSnapshot ExportReadinessAndChecklist(long sessionId)
    {
        var snapshot = Load(sessionId);
        var folder = Path.Combine(SessionExportFolder(sessionId), "readiness");
        var readiness = FactoryReadinessService.ExportGoNoGoPackage(FactoryReadinessService.CriteriaForProfile(snapshot.Session.DeploymentProfile), folder);
        var checklist = FactoryAcceptanceChecklistService.Export(snapshot.Session.DeploymentProfile, folder);
        RecordStep(
            sessionId,
            CustomerPilotStepKind.ExportReadinessAndChecklist,
            CustomerPilotStepStatus.Passed,
            readiness.PackageFolder,
            $"Factory readiness package exported: {readiness.PackageFolder}. Factory acceptance checklist exported: {checklist.Folder}.");
        return Load(sessionId);
    }

    public static CustomerPilotSnapshot RecordWaiver(long sessionId, CustomerPilotStepKind stepKey, string reason, UserRole role, string operatorId)
    {
        if (role < UserRole.Engineer)
            throw new UnauthorizedAccessException(RoleAuthorization.DeniedMessage(role, "recording customer pilot evidence waiver"));
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("Waiver reason is required.", nameof(reason));

        var snapshot = Load(sessionId);
        var step = snapshot.Steps.FirstOrDefault(item => item.StepKey == stepKey)
            ?? throw new InvalidOperationException($"Pilot step {stepKey} was not found.");
        step.Waived = true;
        step.WaiverReason = reason.Trim();
        step.WaivedBy = string.IsNullOrWhiteSpace(operatorId) ? "UNKNOWN" : operatorId.Trim();
        step.WaivedAtUtc = DateTime.UtcNow;
        step.Messages.Add($"WAIVER: {step.WaiverReason}");
        if (step.Status == CustomerPilotStepStatus.Failed)
            step.Status = CustomerPilotStepStatus.Conditional;
        AoiDatabase.UpsertCustomerPilotStep(step);
        AoiDatabase.RecordAuditEvent(
            "CUSTOMER_PILOT_WAIVER",
            $"Customer pilot waiver recorded for {stepKey}: {step.WaiverReason}",
            operatorWithRole: step.WaivedBy,
            relatedEntityType: "CustomerPilotSession",
            relatedEntityId: sessionId.ToString(CultureInfo.InvariantCulture));
        return Load(sessionId);
    }

    public static CustomerPilotExportResult ExportPilotEvidenceBundle(long sessionId, string? outputRoot = null)
    {
        var snapshot = Load(sessionId);
        var root = string.IsNullOrWhiteSpace(outputRoot)
            ? Path.Combine(AoiDatabase.StorageRoot, "exports", "customer_pilot")
            : outputRoot.Trim();
        var folder = Path.Combine(root, $"customer_pilot_{snapshot.Session.SessionId}_{DateTime.UtcNow:yyyyMMdd_HHmmss}");
        Directory.CreateDirectory(folder);
        var jsonPath = Path.Combine(folder, "pilot_execution_summary.json");
        var htmlPath = Path.Combine(folder, "pilot_execution_summary.html");
        var pdfPath = Path.Combine(folder, "pilot_execution_summary.pdf");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(snapshot, JsonOptions), Encoding.UTF8);
        File.WriteAllText(htmlPath, BuildHtml(snapshot), Encoding.UTF8);
        PdfExportService.ExportHtmlFileToPdf(htmlPath, pdfPath, "Customer Pilot Execution Summary");
        ExportVerificationService.RecordVerifiedExport("CustomerPilotEvidenceBundle", folder, "OK", snapshot.Session.OperatorId);
        AoiDatabase.RecordAuditEvent("CUSTOMER_PILOT_EXPORT", $"Customer pilot evidence bundle exported: {Path.GetFileName(folder)}.", operatorWithRole: snapshot.Session.OperatorId, relatedEntityType: "CustomerPilotSession", relatedEntityId: sessionId.ToString(CultureInfo.InvariantCulture), relatedPath: folder);
        return new CustomerPilotExportResult(folder, jsonPath, htmlPath, pdfPath);
    }

    public static bool CanRunBatchValidation(CustomerPilotSnapshot snapshot)
    {
        var preflight = snapshot.Steps.FirstOrDefault(item => item.StepKey == CustomerPilotStepKind.RunDatasetPreflight);
        return preflight is { Status: CustomerPilotStepStatus.Passed } or { Waived: true };
    }

    public static string DisplayName(CustomerPilotStepKind step)
        => step switch
        {
            CustomerPilotStepKind.ConfirmDeploymentProfile => "Confirm deployment profile",
            CustomerPilotStepKind.RunSystemDiagnostics => "Run system diagnostics",
            CustomerPilotStepKind.SelectCustomerDataset => "Import or select customer dataset",
            CustomerPilotStepKind.RunDatasetPreflight => "Run dataset preflight",
            CustomerPilotStepKind.RunBatchValidation => "Run batch validation",
            CustomerPilotStepKind.RunFalseCallReduction => "Run false-call reduction",
            CustomerPilotStepKind.RunModelAcceptance => "Run model acceptance if ONNX configured",
            CustomerPilotStepKind.ExportCustomerValidationPackage => "Export customer validation package",
            CustomerPilotStepKind.RunStage2Acceptance => "Run camera/lighting/3D acceptance",
            CustomerPilotStepKind.ExportReadinessAndChecklist => "Export readiness package and checklist",
            _ => step.ToString(),
        };

    private static void EnsurePreflightAllowsBatchValidation(long sessionId)
    {
        var snapshot = Load(sessionId);
        if (!CanRunBatchValidation(snapshot))
            throw new InvalidOperationException("Dataset preflight failed or has not passed. Engineer/Admin waiver is required before batch validation.");
    }

    private static void RecordStep(long sessionId, CustomerPilotStepKind stepKey, CustomerPilotStepStatus status, string evidencePath, string message)
        => RecordStep(sessionId, stepKey, status, evidencePath, new[] { message });

    private static void RecordStep(long sessionId, CustomerPilotStepKind stepKey, CustomerPilotStepStatus status, string evidencePath, IEnumerable<string> messages)
    {
        var snapshot = Load(sessionId);
        var step = snapshot.Steps.FirstOrDefault(item => item.StepKey == stepKey)
            ?? new CustomerPilotStepRecord { SessionId = sessionId, StepKey = stepKey, StepOrder = Array.IndexOf(OrderedSteps, stepKey) + 1 };
        step.Status = status;
        step.EvidencePath = evidencePath?.Trim() ?? string.Empty;
        step.Messages = messages.Where(message => !string.IsNullOrWhiteSpace(message)).Select(message => message.Trim()).ToList();
        AoiDatabase.UpsertCustomerPilotStep(step);
        snapshot.Session.UpdatedAtUtc = DateTime.UtcNow;
        AoiDatabase.UpdateCustomerPilotSession(snapshot.Session);
    }

    private static bool StepIsOutOfScope(DeploymentProfile profile, CustomerPilotStepKind step)
        => profile == DeploymentProfile.Stage1ImageValidation && step == CustomerPilotStepKind.RunStage2Acceptance;

    private static bool IsPassOrWarn(string? status)
        => string.Equals(status, "PASS", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(status, "WARN", StringComparison.OrdinalIgnoreCase);

    private static string SessionExportFolder(long sessionId)
    {
        var folder = Path.Combine(AoiDatabase.StorageRoot, "exports", "customer_pilot", $"session_{sessionId}");
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string BuildHtml(CustomerPilotSnapshot snapshot)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html><html><head><meta charset=\"utf-8\"><title>Customer Pilot Execution Summary</title>");
        sb.AppendLine("<style>body{font-family:Segoe UI,Arial,sans-serif;margin:24px;color:#17212b}table{border-collapse:collapse;width:100%}td,th{border:1px solid #cfd8e3;padding:8px;text-align:left;vertical-align:top}.Passed{color:#176b3a}.Conditional,.Skipped{color:#8a5a00}.Failed{color:#a12626}.Running{color:#245a9c}</style></head><body>");
        sb.AppendLine($"<h1>Customer Pilot Execution Summary</h1><p><strong>Session:</strong> {Html(snapshot.Session.SessionId)}<br><strong>Profile:</strong> {Html(snapshot.Session.DeploymentProfile.ToString())}<br><strong>Status:</strong> {Html(snapshot.Session.Status)}<br><strong>Dataset:</strong> {Html(snapshot.Session.DatasetFolder)}<br><strong>Manifest:</strong> {Html(snapshot.Session.ManifestPath)}</p>");
        sb.AppendLine("<p>Simulated camera, lighting, 3D, robot, or MES evidence is pilot workflow evidence only and is not production hardware validation.</p>");
        sb.AppendLine("<table><tr><th>#</th><th>Step</th><th>Status</th><th>Evidence</th><th>Messages</th><th>Waiver</th></tr>");
        foreach (var step in snapshot.Steps.OrderBy(item => item.StepOrder))
        {
            var waiver = step.Waived ? $"{step.WaiverReason} ({step.WaivedBy})" : string.Empty;
            sb.AppendLine($"<tr><td>{step.StepOrder}</td><td>{Html(DisplayName(step.StepKey))}</td><td class=\"{step.Status}\">{step.Status}</td><td>{Html(step.EvidencePath)}</td><td>{Html(string.Join(" ", step.Messages))}</td><td>{Html(waiver)}</td></tr>");
        }
        sb.AppendLine("</table></body></html>");
        return sb.ToString();
    }

    private static string Html(string value)
        => System.Net.WebUtility.HtmlEncode(value ?? string.Empty);
}
