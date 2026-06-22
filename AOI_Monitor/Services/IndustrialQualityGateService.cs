using System.IO;
using System.Reflection;
using System.Text.Json;
using AOI_Monitor.Data;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public enum IndustrialGateStatus
{
    Pass,
    Warn,
    Fail,
}

public sealed class IndustrialQualityGateReport
{
    public string SchemaVersion { get; init; } = "industrial-quality-gate-report/v1";
    public DateTime GeneratedAtUtc { get; init; } = DateTime.UtcNow;
    public string AppVersion { get; init; } = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
    public DeploymentProfile DeploymentProfile { get; init; }
    public IndustrialGateStatus OverallStatus { get; init; }
    public List<IndustrialQualityGateCategoryResult> Categories { get; init; } = new();
    public List<IndustrialQualityGateCheckResult> Checks { get; init; } = new();
    public List<string> BlockingIssues { get; init; } = new();
    public List<string> Warnings { get; init; } = new();
}

public sealed class IndustrialQualityGateCategoryResult
{
    public string Category { get; init; } = string.Empty;
    public IndustrialGateStatus Status { get; init; }
    public int PassCount { get; init; }
    public int WarnCount { get; init; }
    public int FailCount { get; init; }
}

public sealed class IndustrialQualityGateCheckResult
{
    public string Id { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string Requirement { get; init; } = string.Empty;
    public string EvidenceRequired { get; init; } = string.Empty;
    public bool AutomatedCheck { get; init; }
    public bool ManualCheck { get; init; }
    public string BlockingLevel { get; init; } = string.Empty;
    public IndustrialGateStatus Status { get; init; }
    public string Evidence { get; init; } = string.Empty;
    public string NextAction { get; init; } = string.Empty;
}

public sealed class IndustrialQualityEvidence
{
    public bool HmiLayoutEvidence { get; set; }
    public bool AlarmEventEvidence { get; set; }
    public bool PerformanceEvidence { get; set; }
    public bool ReliabilityEvidence { get; set; }
    public bool SecurityAccountabilityEvidence { get; set; }
    public bool ExportReportEvidence { get; set; }
    public bool RealHardwareEvidence { get; set; }
    public bool SimulationHardwareEvidence { get; set; }
    public bool MesCentralSyncEvidence { get; set; }
    public bool ReleasePackagingEvidence { get; set; }
    public bool BuildTestEvidence { get; set; }
    public Dictionary<string, string> Details { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class IndustrialQualityGateConfig
{
    public string SchemaVersion { get; set; } = string.Empty;
    public string Baseline { get; set; } = string.Empty;
    public string Checklist { get; set; } = string.Empty;
    public string CertificationNotice { get; set; } = string.Empty;
    public IndustrialQualityMinimums Minimums { get; set; } = new();
    public Dictionary<string, IndustrialQualityProfileConfig> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Categories { get; set; } = new();
    public List<IndustrialQualityGateDefinition> Gates { get; set; } = new();
}

public sealed class IndustrialQualityMinimums
{
    public IndustrialQualityResolution Resolution { get; set; } = new();
    public int MinimumFontPt { get; set; }
    public int MinimumPrimaryButtonWidthPx { get; set; }
    public int MinimumPrimaryButtonHeightPx { get; set; }
    public int VisualizationTargetMs { get; set; }
    public int StablePocHours { get; set; }
}

public sealed class IndustrialQualityResolution
{
    public int Width { get; set; }
    public int Height { get; set; }
}

public sealed class IndustrialQualityProfileConfig
{
    public string Description { get; set; } = string.Empty;
    public string MissingBuildTestEvidence { get; set; } = "FAIL";
    public bool RequireRealHardware { get; set; }
    public bool RequireMesEvidence { get; set; }
    public bool RequireReleasePackaging { get; set; }
}

public sealed class IndustrialQualityGateDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Requirement { get; set; } = string.Empty;
    public string EvidenceRequired { get; set; } = string.Empty;
    public string EvidenceKey { get; set; } = string.Empty;
    public bool AutomatedCheck { get; set; }
    public bool ManualCheck { get; set; }
    public string BlockingLevel { get; set; } = "Release Blocker";
    public bool AllowsSimulation { get; set; } = true;
    public string ProfileRequirement { get; set; } = string.Empty;
    public string ProfileMissingEvidencePolicy { get; set; } = string.Empty;
}

public static class IndustrialQualityGateService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static IndustrialQualityGateConfig LoadConfig(string? configPath = null)
    {
        var path = string.IsNullOrWhiteSpace(configPath) ? FindDefaultConfigPath() : configPath.Trim();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException("Industrial quality gate config was not found.", path);

        var config = JsonSerializer.Deserialize<IndustrialQualityGateConfig>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidOperationException("Industrial quality gate config is empty or invalid.");
        ValidateConfig(config);
        return config;
    }

    public static IndustrialQualityGateReport Evaluate(
        DeploymentProfile? profile = null,
        IndustrialQualityEvidence? evidence = null,
        string? configPath = null)
    {
        var config = LoadConfig(configPath);
        var deploymentProfile = profile ?? DeploymentProfileSettingsService.Load();
        var profileConfig = GetProfileConfig(config, deploymentProfile);
        var snapshot = evidence ?? CollectLocalEvidence(deploymentProfile);
        var checks = config.Gates.Select(gate => EvaluateGate(gate, profileConfig, snapshot)).ToList();

        var categories = config.Categories
            .Select(category =>
            {
                var categoryChecks = checks.Where(check => check.Category.Equals(category, StringComparison.OrdinalIgnoreCase)).ToArray();
                var failCount = categoryChecks.Count(check => check.Status == IndustrialGateStatus.Fail);
                var warnCount = categoryChecks.Count(check => check.Status == IndustrialGateStatus.Warn);
                return new IndustrialQualityGateCategoryResult
                {
                    Category = category,
                    Status = failCount > 0 ? IndustrialGateStatus.Fail : warnCount > 0 ? IndustrialGateStatus.Warn : IndustrialGateStatus.Pass,
                    PassCount = categoryChecks.Count(check => check.Status == IndustrialGateStatus.Pass),
                    WarnCount = warnCount,
                    FailCount = failCount,
                };
            })
            .ToList();

        var blocking = checks
            .Where(check => check.Status == IndustrialGateStatus.Fail)
            .Select(check => $"{check.Id}: {check.Evidence}")
            .ToList();
        var warnings = checks
            .Where(check => check.Status == IndustrialGateStatus.Warn)
            .Select(check => $"{check.Id}: {check.Evidence}")
            .ToList();

        return new IndustrialQualityGateReport
        {
            DeploymentProfile = deploymentProfile,
            Checks = checks,
            Categories = categories,
            BlockingIssues = blocking,
            Warnings = warnings,
            OverallStatus = blocking.Count > 0
                ? IndustrialGateStatus.Fail
                : warnings.Count > 0 ? IndustrialGateStatus.Warn : IndustrialGateStatus.Pass,
        };
    }

    public static IndustrialQualityEvidence CollectLocalEvidence(DeploymentProfile profile)
    {
        AoiDatabase.Initialize();
        var evidence = new IndustrialQualityEvidence();

        AddLayoutEvidence(evidence);
        AddPerformanceEvidence(evidence);
        AddReliabilityEvidence(evidence);
        AddSecurityEvidence(evidence);
        AddExportEvidence(evidence);
        AddHardwareEvidence(evidence);
        AddMesEvidence(evidence);
        AddReleaseEvidence(evidence, profile);

        return evidence;
    }

    private static IndustrialQualityGateCheckResult EvaluateGate(
        IndustrialQualityGateDefinition gate,
        IndustrialQualityProfileConfig profile,
        IndustrialQualityEvidence evidence)
    {
        if (!IsGateRequiredForProfile(gate, profile))
        {
            return Result(gate, IndustrialGateStatus.Pass, "Gate is not required for this deployment profile.", "No action required.");
        }

        var present = HasEvidence(gate.EvidenceKey, evidence);
        if (present)
        {
            return Result(gate, IndustrialGateStatus.Pass, Detail(gate.EvidenceKey, evidence, "Required evidence is present."), "No action required.");
        }

        if (gate.EvidenceKey.Equals("realHardwareEvidence", StringComparison.OrdinalIgnoreCase) &&
            evidence.SimulationHardwareEvidence &&
            !gate.AllowsSimulation)
        {
            return Result(gate, IndustrialGateStatus.Fail, "Simulation evidence exists, but real hardware evidence is required and simulation cannot satisfy this gate.", "Run and record real hardware acceptance tests.");
        }

        var status = MissingStatus(gate, profile);
        return Result(gate, status, Detail(gate.EvidenceKey, evidence, "Required evidence is missing."), NextAction(gate, status));
    }

    private static bool IsGateRequiredForProfile(IndustrialQualityGateDefinition gate, IndustrialQualityProfileConfig profile)
    {
        return gate.ProfileRequirement switch
        {
            "" => true,
            "requireRealHardware" => profile.RequireRealHardware,
            "requireMesEvidence" => profile.RequireMesEvidence,
            "requireReleasePackaging" => profile.RequireReleasePackaging,
            _ => true,
        };
    }

    private static IndustrialGateStatus MissingStatus(IndustrialQualityGateDefinition gate, IndustrialQualityProfileConfig profile)
    {
        if (gate.ProfileMissingEvidencePolicy.Equals("missingBuildTestEvidence", StringComparison.OrdinalIgnoreCase))
            return ParseStatus(profile.MissingBuildTestEvidence, IndustrialGateStatus.Fail);

        return gate.BlockingLevel.Equals("Info", StringComparison.OrdinalIgnoreCase)
            ? IndustrialGateStatus.Pass
            : gate.BlockingLevel.Equals("Warning", StringComparison.OrdinalIgnoreCase)
                ? IndustrialGateStatus.Warn
                : IndustrialGateStatus.Fail;
    }

    private static IndustrialGateStatus ParseStatus(string value, IndustrialGateStatus fallback)
        => value.Trim().ToUpperInvariant() switch
        {
            "PASS" => IndustrialGateStatus.Pass,
            "WARN" or "WARNING" => IndustrialGateStatus.Warn,
            "FAIL" or "BLOCKED" or "RELEASE BLOCKER" => IndustrialGateStatus.Fail,
            _ => fallback,
        };

    private static bool HasEvidence(string key, IndustrialQualityEvidence evidence)
        => key switch
        {
            "hmiLayoutEvidence" => evidence.HmiLayoutEvidence,
            "alarmEventEvidence" => evidence.AlarmEventEvidence,
            "performanceEvidence" => evidence.PerformanceEvidence,
            "reliabilityEvidence" => evidence.ReliabilityEvidence,
            "securityAccountabilityEvidence" => evidence.SecurityAccountabilityEvidence,
            "exportReportEvidence" => evidence.ExportReportEvidence,
            "realHardwareEvidence" => evidence.RealHardwareEvidence,
            "mesCentralSyncEvidence" => evidence.MesCentralSyncEvidence,
            "releasePackagingEvidence" => evidence.ReleasePackagingEvidence,
            "buildTestEvidence" => evidence.BuildTestEvidence,
            _ => evidence.Details.ContainsKey(key),
        };

    private static string Detail(string key, IndustrialQualityEvidence evidence, string fallback)
        => evidence.Details.TryGetValue(key, out var detail) && !string.IsNullOrWhiteSpace(detail)
            ? detail
            : fallback;

    private static string NextAction(IndustrialQualityGateDefinition gate, IndustrialGateStatus status)
    {
        if (status == IndustrialGateStatus.Pass)
            return "No action required.";
        if (gate.EvidenceKey.Equals("buildTestEvidence", StringComparison.OrdinalIgnoreCase))
            return "Record passing Release restore/build/test/publish validation evidence.";
        if (gate.EvidenceKey.Equals("realHardwareEvidence", StringComparison.OrdinalIgnoreCase))
            return "Run real camera, lighting, 3D profile, robot, and safety acceptance checks for the selected profile.";
        if (gate.EvidenceKey.Equals("mesCentralSyncEvidence", StringComparison.OrdinalIgnoreCase))
            return "Run MES traceability signoff or central sync queue/readiness evidence.";

        return "Create, verify, and attach the required evidence before release.";
    }

    private static IndustrialQualityGateCheckResult Result(
        IndustrialQualityGateDefinition gate,
        IndustrialGateStatus status,
        string evidence,
        string nextAction)
        => new()
        {
            Id = gate.Id,
            Category = gate.Category,
            Requirement = gate.Requirement,
            EvidenceRequired = gate.EvidenceRequired,
            AutomatedCheck = gate.AutomatedCheck,
            ManualCheck = gate.ManualCheck,
            BlockingLevel = gate.BlockingLevel,
            Status = status,
            Evidence = evidence,
            NextAction = nextAction,
        };

    private static IndustrialQualityProfileConfig GetProfileConfig(IndustrialQualityGateConfig config, DeploymentProfile profile)
        => config.Profiles.TryGetValue(profile.ToString(), out var profileConfig)
            ? profileConfig
            : new IndustrialQualityProfileConfig();

    private static void ValidateConfig(IndustrialQualityGateConfig config)
    {
        if (!config.SchemaVersion.Equals("industrial-quality-gates/v1", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unsupported industrial quality gate schema version.");
        if (config.Gates.Count == 0)
            throw new InvalidDataException("Industrial quality gate config must define at least one gate.");
        if (config.Categories.Count == 0)
            throw new InvalidDataException("Industrial quality gate config must define categories.");
        if (config.Minimums.Resolution.Width < 1920 || config.Minimums.Resolution.Height < 1080)
            throw new InvalidDataException("Industrial HMI minimum resolution must be at least 1920x1080.");
        if (config.Minimums.MinimumFontPt < 14)
            throw new InvalidDataException("Industrial HMI minimum font must be at least 14 pt.");
        if (config.Minimums.MinimumPrimaryButtonWidthPx < 120 || config.Minimums.MinimumPrimaryButtonHeightPx < 40)
            throw new InvalidDataException("Industrial HMI primary button minimum must be at least 120x40 px.");
        if (!config.CertificationNotice.Contains("not formal", StringComparison.OrdinalIgnoreCase) &&
            !config.CertificationNotice.Contains("not certified", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Industrial quality gate config must state that it is standards-aligned, not certified.");
        }
    }

    private static string FindDefaultConfigPath()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "Tools", "quality-gates", "industrial_quality_gates.json");
            if (File.Exists(candidate))
                return candidate;
            current = current.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, "Tools", "quality-gates", "industrial_quality_gates.json");
    }

    private static void AddLayoutEvidence(IndustrialQualityEvidence evidence)
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "exports", "layout-stress");
        var latest = Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "layout_stress_*.txt").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            : null;
        evidence.HmiLayoutEvidence = latest is not null && !File.ReadAllText(latest).Contains("blocking", StringComparison.OrdinalIgnoreCase);
        evidence.AlarmEventEvidence = AoiDatabase.GetAuditEvents(new LogFilter(), 25).Any();
        evidence.Details["hmiLayoutEvidence"] = latest is null ? "No layout stress report found." : $"Layout report: {latest}.";
        evidence.Details["alarmEventEvidence"] = evidence.AlarmEventEvidence ? "Audit/event records are present." : "No audit/event records found.";
    }

    private static void AddPerformanceEvidence(IndustrialQualityEvidence evidence)
    {
        var verifications = AoiDatabase.GetExportVerifications(100);
        evidence.PerformanceEvidence = verifications.Any(item =>
            item.Status.Equals("OK", StringComparison.OrdinalIgnoreCase) &&
            (item.ExportType.Contains("Performance", StringComparison.OrdinalIgnoreCase) ||
             item.ExportType.Contains("Benchmark", StringComparison.OrdinalIgnoreCase) ||
             item.ExportType.Contains("UiStability", StringComparison.OrdinalIgnoreCase)));
        evidence.Details["performanceEvidence"] = evidence.PerformanceEvidence ? "Performance or stability export verification is present." : "No performance verification evidence found.";
    }

    private static void AddReliabilityEvidence(IndustrialQualityEvidence evidence)
    {
        var noCrash = string.IsNullOrWhiteSpace(CrashReportService.LastCrashReportPath) ||
            !File.Exists(CrashReportService.LastCrashReportPath);
        var stability = UiNavigationSoakTestService.IsThirtyMinuteOrBetterPass(UiNavigationSoakTestService.GetLatestReport());
        evidence.ReliabilityEvidence = noCrash && stability;
        evidence.Details["reliabilityEvidence"] = evidence.ReliabilityEvidence ? "No current crash and UI stability evidence is present." : "Missing stability evidence or current crash report exists.";
    }

    private static void AddSecurityEvidence(IndustrialQualityEvidence evidence)
    {
        var hasAudit = AoiDatabase.GetAuditEvents(new LogFilter(), 25).Any();
        evidence.SecurityAccountabilityEvidence = hasAudit;
        evidence.Details["securityAccountabilityEvidence"] = hasAudit ? "Audit records are present." : "No audit records found.";
    }

    private static void AddExportEvidence(IndustrialQualityEvidence evidence)
    {
        var verifications = AoiDatabase.GetExportVerifications(100);
        evidence.ExportReportEvidence = verifications.Any() &&
            verifications.All(item => !item.Status.Equals("ERROR", StringComparison.OrdinalIgnoreCase));
        evidence.Details["exportReportEvidence"] = evidence.ExportReportEvidence ? "Export verification records are present with no ERROR status." : "No clean export verification evidence found.";
    }

    private static void AddHardwareEvidence(IndustrialQualityEvidence evidence)
    {
        var camera = AoiDatabase.GetLatestCameraAcceptanceRun(realHardwareOnly: false);
        var realCamera = AoiDatabase.GetLatestCameraAcceptanceRun(realHardwareOnly: true);
        var robot = AoiDatabase.GetLatestRobotAcceptanceRun();
        var lighting = AoiDatabase.GetLatestLightingAcceptanceRun();
        var profile3D = AoiDatabase.GetLatestProfile3DAcceptanceRun();

        evidence.SimulationHardwareEvidence =
            camera?.IsRealHardware == false ||
            lighting?.IsSimulated == true ||
            profile3D?.IsSimulated == true ||
            robot?.SourceKind.Equals("Simulated", StringComparison.OrdinalIgnoreCase) == true;
        evidence.RealHardwareEvidence =
            realCamera?.FactoryReadinessStatus is "PASS" or "WARN" &&
            lighting is { IsSimulated: false, Status: "PASS" } &&
            profile3D is { IsSimulated: false, Status: "PASS" } &&
            robot is not null &&
            !robot.SourceKind.Equals("Simulated", StringComparison.OrdinalIgnoreCase) &&
            robot.Status.Equals("PASS", StringComparison.OrdinalIgnoreCase);
        evidence.Details["realHardwareEvidence"] = evidence.RealHardwareEvidence
            ? "Real camera, lighting, 3D profile, and robot evidence is present."
            : evidence.SimulationHardwareEvidence ? "Only simulated or incomplete hardware evidence is present." : "No hardware readiness evidence found.";
    }

    private static void AddMesEvidence(IndustrialQualityEvidence evidence)
    {
        var traceability = AoiDatabase.GetLatestTraceabilityTestReport();
        var central = CentralSyncService.EvaluateReadiness();
        evidence.MesCentralSyncEvidence =
            traceability?.Status.Equals("PASS", StringComparison.OrdinalIgnoreCase) == true ||
            central.Status.Equals("Central Sync Ready", StringComparison.OrdinalIgnoreCase);
        evidence.Details["mesCentralSyncEvidence"] = evidence.MesCentralSyncEvidence
            ? "MES traceability or central sync readiness evidence is present."
            : "No passing MES traceability or central sync readiness evidence found.";
    }

    private static void AddReleaseEvidence(IndustrialQualityEvidence evidence, DeploymentProfile profile)
    {
        var build = BuildTestEvidenceService.GetSummary();
        evidence.BuildTestEvidence = build.IsPassing;
        evidence.ReleasePackagingEvidence = build.IsPassing && (profile == DeploymentProfile.Stage1ImageValidation ||
            AoiDatabase.GetValidationPackages(1).Any());
        evidence.Details["buildTestEvidence"] = build.Latest is null
            ? "No build/test evidence is recorded."
            : string.Join(" ", build.Messages);
        evidence.Details["releasePackagingEvidence"] = evidence.ReleasePackagingEvidence
            ? "Build/test and package evidence is present for the selected profile."
            : "Missing build/test evidence or release package evidence.";
    }
}
