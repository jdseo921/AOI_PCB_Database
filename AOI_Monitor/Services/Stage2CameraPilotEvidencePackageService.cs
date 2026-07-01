using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AOI_Monitor.Data;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public static class Stage2CameraPilotEvidencePackageService
{
    public const string SimulationNotice = "simulation-only; not customer hardware acceptance";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static Stage2CameraPilotEvidenceResult CreatePackage(Stage2CameraPilotEvidenceRequest request)
    {
        ValidateRequest(request);
        AoiDatabase.Initialize();
        if (request.AllowSimulation)
            EnsureSimulationDryRunEvidence(request.OperatorId);

        var generatedAtUtc = DateTime.UtcNow;
        var packageFolder = CreatePackageFolder(request.OutputFolder, generatedAtUtc);
        var criteria = FactoryReadinessService.CriteriaForProfile(DeploymentProfile.Stage2CameraPilot);
        var factoryReport = FactoryReadinessService.Evaluate(criteria);
        var factoryPackage = FactoryReadinessService.ExportGoNoGoPackage(
            criteria,
            Path.Combine(packageFolder, "factory_readiness"));

        var latestStage1 = AoiDatabase.GetValidationPackages(1).FirstOrDefault();
        var latestCamera = AoiDatabase.GetLatestCameraAcceptanceRun(realHardwareOnly: false);
        var latestLighting = AoiDatabase.GetLatestLightingAcceptanceRun();
        var latestProfile3D = AoiDatabase.GetLatestProfile3DAcceptanceRun();

        var stage1Status = Stage1EvidenceStatus(latestStage1);
        var cameraStatus = CameraEvidenceStatus(latestCamera);
        var lightingStatus = LightingEvidenceStatus(latestLighting);
        var profile3DStatus = Profile3DEvidenceStatus(latestProfile3D);
        var simulationPresent =
            IsSimulationCameraEvidence(latestCamera) ||
            IsSimulationLightingEvidence(latestLighting) ||
            IsSimulationProfile3DEvidence(latestProfile3D);
        var realHardwareValidated =
            IsRealCameraValidated(latestCamera) &&
            IsRealLightingValidated(latestLighting) &&
            IsRealProfile3DValidated(latestProfile3D);

        WriteStage1Evidence(packageFolder, latestStage1);
        WriteLatestAcceptanceReports(packageFolder, latestCamera, latestLighting, latestProfile3D);
        WriteLatestExportVerification(packageFolder);
        WriteCriteria(packageFolder, criteria);

        var blockingIssues = BuildBlockingIssues(
            factoryReport,
            stage1Status,
            cameraStatus,
            lightingStatus,
            profile3DStatus,
            simulationPresent,
            realHardwareValidated,
            request.AllowSimulation);
        var recommendedNextActions = BuildRecommendedNextActions(factoryReport, blockingIssues);
        var status = DetermineStatus(factoryReport, stage1Status, realHardwareValidated, simulationPresent, request.AllowSimulation);

        var readmePath = Path.Combine(packageFolder, "README.txt");
        var manifestPath = Path.Combine(packageFolder, "package_manifest.json");
        var manifest = new Stage2CameraPilotPackageManifest
        {
            PackageId = $"STAGE2-CAMERA-PILOT-{generatedAtUtc:yyyyMMddHHmmss}",
            GeneratedAtUtc = generatedAtUtc,
            DeploymentProfile = DeploymentProfile.Stage2CameraPilot.ToString(),
            OperatorId = request.OperatorId.Trim(),
            Status = status,
            FactoryReadinessStatus = factoryReport.OverallStatus,
            Stage1EvidenceStatus = stage1Status,
            CameraEvidenceStatus = cameraStatus,
            LightingEvidenceStatus = lightingStatus,
            Profile3DEvidenceStatus = profile3DStatus,
            SimulationEvidencePresent = simulationPresent,
            RealHardwareValidated = realHardwareValidated,
            DryRun = request.AllowSimulation && simulationPresent && !realHardwareValidated,
            EvidenceBoundary = (request.AllowSimulation || simulationPresent) && !realHardwareValidated
                ? SimulationNotice
                : "Real hardware readiness depends on the explicit camera, lighting, and 3D profile evidence statuses.",
            FactoryReadinessPackagePath = NormalizeRelative(Path.GetRelativePath(packageFolder, factoryPackage.PackageFolder)),
            BlockingIssues = blockingIssues,
            RecommendedNextActions = recommendedNextActions,
        };

        File.WriteAllText(readmePath, BuildReadme(manifest), Encoding.UTF8);
        manifest.IncludedFiles = BuildIncludedFiles(packageFolder, manifestPath);
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions), Encoding.UTF8);

        var verified = ExportVerificationService.RecordVerifiedExport(
            "Stage2CameraPilotEvidencePackage",
            packageFolder,
            status is "PASS" or "WARN" ? "OK" : "WARN",
            request.OperatorId);

        return new Stage2CameraPilotEvidenceResult
        {
            Status = status,
            PackageFolder = packageFolder,
            ManifestPath = manifestPath,
            ReadmePath = readmePath,
            FactoryReadinessFolder = factoryPackage.PackageFolder,
            FactoryReadinessStatus = factoryReport.OverallStatus,
            ExportVerificationStatus = verified.ExportStatus,
            Stage1EvidenceStatus = stage1Status,
            CameraEvidenceStatus = cameraStatus,
            LightingEvidenceStatus = lightingStatus,
            Profile3DEvidenceStatus = profile3DStatus,
            SimulationEvidencePresent = simulationPresent,
            RealHardwareValidated = realHardwareValidated,
            DryRun = manifest.DryRun,
            BlockingIssues = blockingIssues,
            RecommendedNextActions = recommendedNextActions,
        };
    }

    private static void ValidateRequest(Stage2CameraPilotEvidenceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.OutputFolder))
            throw new Stage2CameraPilotEvidenceException("Missing required option --output.");
        if (string.IsNullOrWhiteSpace(request.OperatorId))
            throw new Stage2CameraPilotEvidenceException("Missing required option --operator.");
    }

    private static void EnsureSimulationDryRunEvidence(string operatorId)
    {
        var generatedAtUtc = DateTime.UtcNow;
        var operatorName = string.IsNullOrWhiteSpace(operatorId) ? "UNKNOWN" : operatorId.Trim();

        if (AoiDatabase.GetLatestCameraAcceptanceRun(realHardwareOnly: false) is null)
        {
            AoiDatabase.RecordCameraAcceptanceRun(new CameraAcceptanceRun
            {
                CreatedAtUtc = generatedAtUtc,
                AdapterName = "CI Simulation Dry Run Camera",
                SourceKey = "folder-simulation-dry-run",
                SettingsSummary = $"{SimulationNotice}; generated by stage2-camera-pilot --allow-simulation.",
                Status = "WARN",
                FactoryReadinessStatus = "NOT VALIDATED",
                IsRealHardware = false,
                TotalRequestedFrames = 1,
                TotalReceivedFrames = 1,
                Warnings =
                {
                    SimulationNotice,
                    "No vendor camera adapter or real camera metadata was validated.",
                },
                ViewMetrics =
                {
                    new CameraAcceptanceViewMetrics
                    {
                        ViewType = "Top",
                        RequestedFrames = 1,
                        ReceivedFrames = 1,
                        TriggerMode = "Simulation",
                        LightingProgramSelected = "Simulation dry run",
                        SyncStatus = "SIMULATION ONLY",
                        Status = "WARN",
                        Warnings = { SimulationNotice },
                    },
                },
                Frames =
                {
                    new CameraAcceptanceFrameRecord
                    {
                        ViewType = "Top",
                        Sequence = 1,
                        FrameId = "CI-SIM-CAM-001",
                        CameraId = "CI-SIM",
                        CapturedAtUtc = generatedAtUtc,
                        Width = 1,
                        Height = 1,
                        PixelFormat = "Mono8",
                        SourceKind = "FolderSimulation",
                        IsSimulated = true,
                        MetadataValid = true,
                        Message = SimulationNotice,
                    },
                },
            }, operatorName);
        }

        if (AoiDatabase.GetLatestLightingAcceptanceRun() is null)
        {
            AoiDatabase.RecordLightingAcceptanceRun(new LightingAcceptanceRun
            {
                CreatedAtUtc = generatedAtUtc,
                ControllerName = "CI Simulation Dry Run Lighting",
                Mode = LightingModes.Simulated,
                SettingsSummary = $"{SimulationNotice}; no real lighting controller was exercised.",
                Status = "WARN",
                IsSimulated = true,
                StepCount = 1,
                PassedStepCount = 1,
                Warnings =
                {
                    SimulationNotice,
                    "No real lighting trigger/sync evidence was collected.",
                },
                Steps =
                {
                    new LightingAcceptanceStep
                    {
                        ViewType = "Top",
                        ProgramName = "CI-SIM",
                        CommandText = "SIMULATED_LIGHT_ON",
                        CommandAccepted = true,
                        FrameReceived = false,
                        Status = "WARN",
                        Message = SimulationNotice,
                    },
                },
            }, operatorName);
        }

        if (AoiDatabase.GetLatestProfile3DAcceptanceRun() is null)
        {
            AoiDatabase.RecordProfile3DAcceptanceRun(new Profile3DAcceptanceRun
            {
                CreatedAtUtc = generatedAtUtc,
                SourceName = "CI Simulation Dry Run 3D Profile",
                SourceKind = "SimulationDryRun",
                IsSimulated = true,
                Status = "WARN",
                FactoryReadinessStatus = "NOT VALIDATED",
                Width = 2,
                Height = 2,
                Unit = "microns",
                XPitchMicrons = 1,
                YPitchMicrons = 1,
                FrameId = "CI-SIM-3D-001",
                Diagnostics =
                {
                    ["evidenceBoundary"] = SimulationNotice,
                },
                Warnings =
                {
                    SimulationNotice,
                    "No real 3D acquisition source was validated.",
                },
            }, operatorName);
        }
    }

    private static string CreatePackageFolder(string outputFolder, DateTime generatedAtUtc)
    {
        Directory.CreateDirectory(outputFolder);
        var baseName = $"stage2_camera_pilot_{generatedAtUtc:yyyyMMdd_HHmmss}";
        var folder = Path.Combine(outputFolder, baseName);
        var suffix = 1;
        while (Directory.Exists(folder))
            folder = Path.Combine(outputFolder, $"{baseName}_{suffix++:00}");

        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string Stage1EvidenceStatus(ValidationPackageRecord? latest)
    {
        if (latest is null)
            return "MISSING";

        return NormalizeStatus(latest.AcceptanceStatus);
    }

    private static string CameraEvidenceStatus(CameraAcceptanceRun? run)
    {
        if (run is null)
            return "MISSING";
        if (!IsRealCameraValidated(run))
            return "NOT VALIDATED";

        return NormalizeStatus(run.FactoryReadinessStatus);
    }

    private static string LightingEvidenceStatus(LightingAcceptanceRun? run)
    {
        if (run is null)
            return "MISSING";
        if (!IsRealLightingValidated(run))
            return "NOT VALIDATED";

        return NormalizeStatus(run.Status);
    }

    private static string Profile3DEvidenceStatus(Profile3DAcceptanceRun? run)
    {
        if (run is null)
            return "MISSING";
        if (!IsRealProfile3DValidated(run))
            return "NOT VALIDATED";

        return NormalizeStatus(run.FactoryReadinessStatus);
    }

    private static bool IsRealCameraValidated(CameraAcceptanceRun? run)
        => run is not null &&
           run.IsRealHardware &&
           IsPassOrWarn(run.FactoryReadinessStatus);

    private static bool IsRealLightingValidated(LightingAcceptanceRun? run)
        => run is not null &&
           !run.IsSimulated &&
           IsPassOrWarn(run.Status) &&
           !UsesFakeAcquisitionEvidence(run);

    private static bool IsRealProfile3DValidated(Profile3DAcceptanceRun? run)
        => run is not null &&
           !run.IsSimulated &&
           IsPassOrWarn(run.FactoryReadinessStatus);

    private static bool IsSimulationCameraEvidence(CameraAcceptanceRun? run)
        => run is not null &&
           (!run.IsRealHardware ||
            run.Frames.Any(frame => frame.IsSimulated) ||
            run.SourceKey.Contains("folder", StringComparison.OrdinalIgnoreCase) ||
            run.SourceKey.Contains("null", StringComparison.OrdinalIgnoreCase) ||
            run.AdapterName.Contains("fake", StringComparison.OrdinalIgnoreCase) ||
            run.AdapterName.Contains("simulation", StringComparison.OrdinalIgnoreCase));

    private static bool IsSimulationLightingEvidence(LightingAcceptanceRun? run)
        => run is not null && (run.IsSimulated || UsesFakeAcquisitionEvidence(run));

    private static bool IsSimulationProfile3DEvidence(Profile3DAcceptanceRun? run)
        => run is not null && run.IsSimulated;

    private static bool UsesFakeAcquisitionEvidence(LightingAcceptanceRun run)
        => run.Warnings.Any(warning =>
            warning.Contains("fake acquisition", StringComparison.OrdinalIgnoreCase) ||
            warning.Contains("No camera source", StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<string> BuildBlockingIssues(
        FactoryReadinessReport factoryReport,
        string stage1Status,
        string cameraStatus,
        string lightingStatus,
        string profile3DStatus,
        bool simulationPresent,
        bool realHardwareValidated,
        bool allowSimulation)
    {
        var issues = new List<string>();
        if (!string.Equals(stage1Status, "PASS", StringComparison.OrdinalIgnoreCase))
            issues.Add($"Stage 1 evidence status is {stage1Status}; a PASS Stage 1 validation package is required.");
        if (!IsPassOrWarn(cameraStatus))
            issues.Add($"Camera evidence status is {cameraStatus}; real vendor camera acceptance is required.");
        if (!IsPassOrWarn(lightingStatus))
            issues.Add($"Lighting evidence status is {lightingStatus}; real lighting trigger/sync evidence is required.");
        if (!IsPassOrWarn(profile3DStatus))
            issues.Add($"3D profile evidence status is {profile3DStatus}; real 3D acquisition evidence is required.");
        if (simulationPresent && !realHardwareValidated)
            issues.Add($"{SimulationNotice}.");
        if (simulationPresent && !allowSimulation)
            issues.Add("Simulation evidence was detected and --allow-simulation was not supplied; real hardware readiness remains No-Go.");

        issues.AddRange(factoryReport.BlockingIssues);
        return issues
            .Where(issue => !string.IsNullOrWhiteSpace(issue))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<string> BuildRecommendedNextActions(
        FactoryReadinessReport factoryReport,
        IReadOnlyCollection<string> blockingIssues)
    {
        var actions = new List<string>
        {
            "Run Stage 1 exit validation against the approved customer dataset and keep the exported package verification record.",
            "Run camera acceptance with the real vendor adapter and production camera metadata.",
            "Run lighting synchronization with the real lighting controller and camera trigger-to-frame evidence.",
            "Run 3D profile acceptance with the real 3D acquisition source when height/coplanarity is in scope.",
            "Re-run the Stage 2 camera pilot package command after all blocking evidence is recorded.",
        };

        actions.AddRange(factoryReport.RecommendedNextActions);
        if (blockingIssues.Count == 0)
            actions.Insert(0, "Review customer signoff checklist before describing Stage 2 pilot readiness.");

        return actions
            .Where(action => !string.IsNullOrWhiteSpace(action))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string DetermineStatus(
        FactoryReadinessReport factoryReport,
        string stage1Status,
        bool realHardwareValidated,
        bool simulationPresent,
        bool allowSimulation)
    {
        var stage1Ok = string.Equals(stage1Status, "PASS", StringComparison.OrdinalIgnoreCase);
        if (!stage1Ok || !realHardwareValidated || string.Equals(factoryReport.OverallStatus, "NoGo", StringComparison.OrdinalIgnoreCase))
            return allowSimulation && simulationPresent ? "DRY RUN" : "NO-GO";

        return string.Equals(factoryReport.OverallStatus, "Conditional", StringComparison.OrdinalIgnoreCase)
            ? "WARN"
            : "PASS";
    }

    private static void WriteStage1Evidence(string packageFolder, ValidationPackageRecord? latest)
    {
        var folder = Path.Combine(packageFolder, "stage1_evidence");
        Directory.CreateDirectory(folder);
        if (latest is null)
        {
            File.WriteAllText(
                Path.Combine(folder, "latest_stage1_validation_package_record.json"),
                JsonSerializer.Serialize(new { status = "MISSING", message = "No Stage 1 validation package has been recorded." }, JsonOptions),
                Encoding.UTF8);
            return;
        }

        File.WriteAllText(Path.Combine(folder, "latest_stage1_validation_package_record.json"), JsonSerializer.Serialize(latest, JsonOptions), Encoding.UTF8);
        if (File.Exists(latest.ManifestPath))
            File.Copy(latest.ManifestPath, Path.Combine(folder, "latest_validation_manifest.json"), overwrite: true);
    }

    private static void WriteLatestAcceptanceReports(
        string packageFolder,
        CameraAcceptanceRun? camera,
        LightingAcceptanceRun? lighting,
        Profile3DAcceptanceRun? profile3D)
    {
        var folder = Path.Combine(packageFolder, "latest_acceptance_reports");
        Directory.CreateDirectory(folder);

        if (camera is not null)
            CameraAcceptanceTestService.ExportReport(camera, folder);
        else
            File.WriteAllText(Path.Combine(folder, "camera_acceptance_missing.json"), JsonSerializer.Serialize(new { status = "MISSING" }, JsonOptions), Encoding.UTF8);

        if (lighting is not null)
            LightingAcceptanceTestService.ExportReport(lighting, folder);
        else
            File.WriteAllText(Path.Combine(folder, "lighting_acceptance_missing.json"), JsonSerializer.Serialize(new { status = "MISSING" }, JsonOptions), Encoding.UTF8);

        if (profile3D is not null)
            Profile3DAcceptanceTestService.ExportReport(profile3D, folder);
        else
            File.WriteAllText(Path.Combine(folder, "profile_3d_acceptance_missing.json"), JsonSerializer.Serialize(new { status = "MISSING" }, JsonOptions), Encoding.UTF8);
    }

    private static void WriteLatestExportVerification(string packageFolder)
    {
        var folder = Path.Combine(packageFolder, "export_verification_evidence");
        Directory.CreateDirectory(folder);
        var records = AoiDatabase.GetExportVerifications(100);
        File.WriteAllText(
            Path.Combine(folder, "latest_export_verification_records.json"),
            JsonSerializer.Serialize(records, JsonOptions),
            Encoding.UTF8);
    }

    private static void WriteCriteria(string packageFolder, FactoryReadinessCriteria criteria)
    {
        File.WriteAllText(
            Path.Combine(packageFolder, "factory_readiness_criteria.json"),
            JsonSerializer.Serialize(criteria, JsonOptions),
            Encoding.UTF8);
    }

    private static IReadOnlyList<ValidationIncludedFile> BuildIncludedFiles(string packageFolder, string manifestPath)
        => Directory.EnumerateFiles(packageFolder, "*", SearchOption.AllDirectories)
            .Where(path => !path.Equals(manifestPath, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => Path.GetRelativePath(packageFolder, path), StringComparer.OrdinalIgnoreCase)
            .Select(path => new ValidationIncludedFile
            {
                RelativePath = NormalizeRelative(Path.GetRelativePath(packageFolder, path)),
                FileType = Classify(path),
                Bytes = new FileInfo(path).Length,
                Sha256 = ComputeSha256(path),
            })
            .ToList();

    private static string BuildReadme(Stage2CameraPilotPackageManifest manifest)
        => $"""
        AOI Monitor Stage 2 Camera Pilot Evidence Package

        Status: {manifest.Status}
        Deployment profile: {manifest.DeploymentProfile}
        Operator: {manifest.OperatorId}
        Generated UTC: {manifest.GeneratedAtUtc:O}
        Factory readiness status: {manifest.FactoryReadinessStatus}

        Stage evidence:
        - Stage 1 validation: {manifest.Stage1EvidenceStatus}
        - Camera acceptance: {manifest.CameraEvidenceStatus}
        - Lighting acceptance: {manifest.LightingEvidenceStatus}
        - 3D profile acceptance: {manifest.Profile3DEvidenceStatus}
        - Simulation evidence present: {manifest.SimulationEvidencePresent}
        - Real hardware validated: {manifest.RealHardwareValidated}

        Evidence boundary:
        {manifest.EvidenceBoundary}
        Simulated, folder, null, fake, or template adapter evidence is {SimulationNotice}.
        This package is standards-aligned project evidence only and is not formal ISO, IEC, ISA, safety, cybersecurity, or third-party certification.

        Contents:
        - package_manifest.json: machine-readable package manifest and evidence status.
        - factory_readiness/: nested Stage 2 Go/No-Go package exported from FactoryReadinessService.
        - stage1_evidence/: latest Stage 1 validation package record and manifest when available.
        - latest_acceptance_reports/: latest camera, lighting, and 3D profile acceptance reports when available.
        - export_verification_evidence/: latest local export verification records.

        Blocking issues:
        {FormatLines(manifest.BlockingIssues)}

        Recommended next actions:
        {FormatLines(manifest.RecommendedNextActions)}
        """;

    private static string Classify(string path)
    {
        var fileName = Path.GetFileName(path);
        if (fileName.Equals("README.txt", StringComparison.OrdinalIgnoreCase))
            return "README";
        if (fileName.Equals("factory_readiness_criteria.json", StringComparison.OrdinalIgnoreCase))
            return "Factory readiness criteria";
        if (fileName.Contains("factory_readiness", StringComparison.OrdinalIgnoreCase))
            return "Factory readiness evidence";
        if (fileName.Contains("validation_manifest", StringComparison.OrdinalIgnoreCase) || fileName.Contains("stage1", StringComparison.OrdinalIgnoreCase))
            return "Stage 1 validation evidence";
        if (fileName.Contains("camera_acceptance", StringComparison.OrdinalIgnoreCase))
            return "Camera acceptance evidence";
        if (fileName.Contains("lighting_acceptance", StringComparison.OrdinalIgnoreCase))
            return "Lighting acceptance evidence";
        if (fileName.Contains("profile_3d_acceptance", StringComparison.OrdinalIgnoreCase))
            return "3D profile acceptance evidence";
        if (fileName.Contains("export_verification", StringComparison.OrdinalIgnoreCase))
            return "Export verification evidence";
        return "Stage 2 camera pilot evidence";
    }

    private static bool IsPassOrWarn(string status)
        => status.Equals("PASS", StringComparison.OrdinalIgnoreCase) ||
           status.Equals("WARN", StringComparison.OrdinalIgnoreCase) ||
           status.Equals("Go", StringComparison.OrdinalIgnoreCase) ||
           status.Equals("Conditional", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeStatus(string status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return "MISSING";

        return status.Trim().ToUpperInvariant() switch
        {
            "GO" => "PASS",
            "CONDITIONAL" => "WARN",
            "NO-GO" or "NOGO" => "FAIL",
            var value => value,
        };
    }

    private static string NormalizeRelative(string path)
        => path.Replace('\\', '/').TrimStart('/');

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string FormatLines(IReadOnlyCollection<string> lines)
        => lines.Count == 0 ? "- None recorded." : string.Join(Environment.NewLine, lines.Select(line => $"- {line}"));
}

public sealed class Stage2CameraPilotEvidenceRequest
{
    public string OutputFolder { get; set; } = string.Empty;
    public string OperatorId { get; set; } = string.Empty;
    public bool AllowSimulation { get; set; }
}

public sealed class Stage2CameraPilotEvidenceResult
{
    public string Status { get; set; } = "NO-GO";
    public string PackageFolder { get; set; } = string.Empty;
    public string ManifestPath { get; set; } = string.Empty;
    public string ReadmePath { get; set; } = string.Empty;
    public string FactoryReadinessFolder { get; set; } = string.Empty;
    public string FactoryReadinessStatus { get; set; } = string.Empty;
    public string ExportVerificationStatus { get; set; } = string.Empty;
    public string Stage1EvidenceStatus { get; set; } = "MISSING";
    public string CameraEvidenceStatus { get; set; } = "MISSING";
    public string LightingEvidenceStatus { get; set; } = "MISSING";
    [JsonPropertyName("profile3dEvidenceStatus")]
    public string Profile3DEvidenceStatus { get; set; } = "MISSING";
    public bool SimulationEvidencePresent { get; set; }
    public bool RealHardwareValidated { get; set; }
    public bool DryRun { get; set; }
    public IReadOnlyList<string> BlockingIssues { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> RecommendedNextActions { get; set; } = Array.Empty<string>();
}

public sealed class Stage2CameraPilotPackageManifest
{
    public string SchemaVersion { get; set; } = "stage2-camera-pilot-package/v1";
    public string PackageId { get; set; } = string.Empty;
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
    public string DeploymentProfile { get; set; } = AOI_Monitor.Models.DeploymentProfile.Stage2CameraPilot.ToString();
    public string OperatorId { get; set; } = string.Empty;
    public string Status { get; set; } = "NO-GO";
    public string FactoryReadinessStatus { get; set; } = string.Empty;
    public string Stage1EvidenceStatus { get; set; } = "MISSING";
    public string CameraEvidenceStatus { get; set; } = "MISSING";
    public string LightingEvidenceStatus { get; set; } = "MISSING";
    [JsonPropertyName("profile3dEvidenceStatus")]
    public string Profile3DEvidenceStatus { get; set; } = "MISSING";
    public bool SimulationEvidencePresent { get; set; }
    public bool RealHardwareValidated { get; set; }
    public bool DryRun { get; set; }
    public string EvidenceBoundary { get; set; } = string.Empty;
    public string FactoryReadinessPackagePath { get; set; } = string.Empty;
    public IReadOnlyList<string> BlockingIssues { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> RecommendedNextActions { get; set; } = Array.Empty<string>();
    public IReadOnlyList<ValidationIncludedFile> IncludedFiles { get; set; } = Array.Empty<ValidationIncludedFile>();
}

public sealed class Stage2CameraPilotEvidenceException : Exception
{
    public Stage2CameraPilotEvidenceException(string message)
        : base(message)
    {
    }
}
