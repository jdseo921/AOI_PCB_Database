using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Xunit;

namespace AOI_Monitor.Tests;

public sealed class ClientDemoReadinessGateTests : IDisposable
{
    private readonly string _root;

    public ClientDemoReadinessGateTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AOI_Monitor_ClientDemoGate_Tests", Guid.NewGuid().ToString("N"));
        StorageRootSettingsService.ConfigureSettingsDirectoryForTests(_root);
        AoiDatabase.ConfigureStorageRoot(_root);
        AoiDatabase.Initialize();
        WorkflowState.Instance.SetCurrentUser("GateAdmin", UserRole.Admin);
        DeploymentProfileSettingsService.ResetForTests();
        OperatingModeSettingsService.ResetForTests();
        CrashReportService.ClearForTests();
        // Gate outcomes must not depend on artifacts other tests (or a concurrently running
        // UI-test process) leave in the shared repository TestResults folder, nor on alarm
        // state carried over from earlier test classes in this process.
        ClientDemoReadinessGateService.OverrideArtifactRootForTests(ArtifactRoot);
        AlarmEventService.ClearForTests();
    }

    private string ArtifactRoot => Path.Combine(_root, "TestResults");

    public void Dispose()
    {
        ClientDemoReadinessGateService.OverrideArtifactRootForTests(null);
        AlarmEventService.ClearForTests();
        CrashReportService.ClearForTests();
        DeploymentProfileSettingsService.ResetForTests();
        OperatingModeSettingsService.ResetForTests();
        StorageRootSettingsService.ConfigureSettingsDirectoryForTests(null);
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"Test cleanup failed for {nameof(ClientDemoReadinessGateTests)}: {ex.Message}");
        }
    }

    [Fact]
    public void CrashReportCausesGateBlocked()
    {
        SeedPassingStage1Evidence();
        CrashReportService.WriteReport(new CrashReportRequest
        {
            Exception = new InvalidOperationException("simulated crash"),
            OperationName = "test",
            CurrentPage = "reports",
        });

        var report = ClientDemoReadinessGateService.Evaluate(DeploymentProfile.Stage1ImageValidation);

        Assert.Equal(ClientDemoGateStatus.Blocked, report.OverallStatus);
        Assert.Contains(report.Checks, check => check.Name == "Crash reports in latest session" && check.Status == ClientDemoGateStatus.Blocked);
    }

    [Fact]
    public void FailingLayoutAuditBlocksClientGate()
    {
        BuildTestEvidenceService.CreateLocalEvidence(testResultPath: Path.Combine(_root, "unit_tests.trx"), operatorId: "GateAdmin [Admin]");
        SeedStage1Package();
        RecordOkExportVerification();
        WriteFailingHmiLayoutAudit();
        WritePassingNavigationPerformanceReport();

        var stage1 = ClientDemoReadinessGateService.Evaluate(DeploymentProfile.Stage1ImageValidation);

        Assert.Contains(stage1.Checks, check => check.Name == "UI layout audit" && check.Status == ClientDemoGateStatus.Blocked);
    }

    [Fact]
    public void PassingBuildUiNoCrashGateAllowsClientPackageWithHardwareWarning()
    {
        SeedPassingStage1Evidence();

        var report = ClientDemoReadinessGateService.Evaluate(DeploymentProfile.Stage1ImageValidation);

        Assert.True(report.OverallStatus != ClientDemoGateStatus.Blocked, FormatBlockingIssues(report));
        Assert.True(report.CanCreateClientPackage);
        Assert.Contains(report.Checks, check => check.Name == "Repository hygiene" && check.Status == ClientDemoGateStatus.Pass);
        Assert.Contains(report.Checks, check => check.Name == "Release build" && check.Status == ClientDemoGateStatus.Pass);
        Assert.Contains(report.Checks, check => check.Name == "Release tests" && check.Status == ClientDemoGateStatus.Pass);
        Assert.Contains(report.Checks, check => check.Name == "UI layout audit" && check.Status == ClientDemoGateStatus.Pass);
        Assert.Contains(report.Checks, check => check.Name == "Navigation performance" && check.Status == ClientDemoGateStatus.Pass);
        Assert.Contains(report.Checks, check => check.Name == "UI stability soak" && check.Status == ClientDemoGateStatus.Warning);
        Assert.Contains(report.Checks, check => check.Name == "Export verification" && check.Status == ClientDemoGateStatus.Pass);
        Assert.Contains(report.Checks, check => check.Name == "Standards traceability" && check.Status == ClientDemoGateStatus.Warning);
        Assert.Contains("not certification", report.StandardsTraceabilitySummary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(report.Checks, check => check.Name == "Real hardware validation" && check.Status == ClientDemoGateStatus.Warning);
    }

    [Fact]
    public void MissingUiStabilitySoakBlocksClientPackage()
    {
        SeedPassingStage1Evidence(includeUiStabilityEvidence: false);

        var report = ClientDemoReadinessGateService.Evaluate(DeploymentProfile.Stage1ImageValidation);

        Assert.Equal(ClientDemoGateStatus.Blocked, report.OverallStatus);
        Assert.Contains(report.Checks, check => check.Name == "UI stability soak" && check.Status == ClientDemoGateStatus.Blocked);
    }

    [Fact]
    public void ThirtyMinuteUiStabilitySoakPassesClientGate()
    {
        SeedPassingStage1Evidence(includeUiStabilityEvidence: false);
        WriteUiStabilityReport(UiNavigationSoakProfile.ThirtyMinuteLocalStability, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));

        var report = ClientDemoReadinessGateService.Evaluate(DeploymentProfile.Stage1ImageValidation);

        Assert.Contains(report.Checks, check => check.Name == "UI stability soak" && check.Status == ClientDemoGateStatus.Pass);
    }

    [Fact]
    public void NavigationPerformanceWarningsDoNotBlockClientPackage()
    {
        SeedPassingStage1Evidence();
        WriteNavigationPerformanceReportWithWarning();

        var report = ClientDemoReadinessGateService.Evaluate(DeploymentProfile.Stage1ImageValidation);

        Assert.True(report.OverallStatus != ClientDemoGateStatus.Blocked, FormatBlockingIssues(report));
        Assert.Contains(report.Checks, check => check.Name == "Navigation performance" && check.Status == ClientDemoGateStatus.Warning);
    }

    [Fact]
    public void ExportVerificationErrorBlocksClientPackage()
    {
        SeedPassingStage1Evidence();
        ExportVerificationService.RecordVerifiedExport("SyntheticExport", Path.Combine(_root, "missing.csv"), "EXPORT_ERROR", "GateAdmin [Admin]");

        var report = ClientDemoReadinessGateService.Evaluate(DeploymentProfile.Stage1ImageValidation);

        Assert.Equal(ClientDemoGateStatus.Blocked, report.OverallStatus);
        Assert.Contains(report.Checks, check => check.Name == "Export verification" && check.Status == ClientDemoGateStatus.Blocked);
    }

    [Fact]
    public void HighOpenPilotIssueBlocksClientPackage()
    {
        SeedPassingStage1Evidence();
        PilotIssueService.Create(new PilotIssue
        {
            Category = PilotIssueCategory.NavigationPerformance,
            Severity = "High",
            PageName = "Management Dashboard",
            ReproductionSteps = "Switch from Main Inspection to Management Dashboard during pilot review.",
            ExpectedBehavior = "Menu click responds immediately.",
            ActualBehavior = "Navigation pauses long enough for operators to retry.",
            Notes = "Manual pilot issue must block client-facing package until closed or waived.",
        }, "GateAdmin [Admin]");

        var report = ClientDemoReadinessGateService.Evaluate(DeploymentProfile.Stage1ImageValidation);

        Assert.Equal(ClientDemoGateStatus.Blocked, report.OverallStatus);
        Assert.False(report.CanCreateClientPackage);
        Assert.Contains(report.Checks, check =>
            check.Name == "High/Critical pilot issues" &&
            check.Status == ClientDemoGateStatus.Blocked);
    }

    private void SeedPassingStage1Evidence(bool includeUiStabilityEvidence = true)
    {
        BuildTestEvidenceService.CreateLocalEvidence(testResultPath: Path.Combine(_root, "ui_smoke_report.json"), operatorId: "GateAdmin [Admin]");
        SeedStage1Package();
        RecordOkExportVerification();
        WritePassingHmiLayoutAudit();
        WritePassingNavigationPerformanceReport();
        if (includeUiStabilityEvidence)
            WriteUiStabilityReport(UiNavigationSoakProfile.FiveMinuteSmoke, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    private void SeedStage1Package()
    {
        var packageFolder = Path.Combine(_root, "stage1_package");
        Directory.CreateDirectory(packageFolder);
        var manifestPath = Path.Combine(packageFolder, "manifest.json");
        File.WriteAllText(manifestPath, "{}");
        AoiDatabase.RecordValidationPackage(
            "PKG-GATE-STAGE1",
            packageFolder,
            manifestPath,
            "PASS",
            "Synthetic gate package",
            operatorId: "GateAdmin [Admin]");
    }

    private void RecordOkExportVerification()
    {
        var path = Path.Combine(_root, "verified_export.txt");
        File.WriteAllText(path, "verified export evidence");
        ExportVerificationService.RecordVerifiedExport("ClientDemoGateEvidence", path, "OK", "GateAdmin [Admin]");
    }

    private void WritePassingHmiLayoutAudit()
    {
        WriteHmiLayoutAudit(passed: true, failCount: 0);
    }

    private void WriteFailingHmiLayoutAudit()
    {
        WriteHmiLayoutAudit(passed: false, failCount: 1);
    }

    private void WriteHmiLayoutAudit(bool passed, int failCount)
    {
        var folder = ArtifactRoot;
        Directory.CreateDirectory(folder);
        var issue = failCount > 0
            ? """
                {
                  "severity": "Fail",
                  "approved": false,
                  "viewName": "Synthetic",
                  "message": "Synthetic clipped text"
                }
              """
            : "";
        File.WriteAllText(Path.Combine(folder, "hmi_layout_audit.json"), $$"""
        {
          "schemaVersion": "hmi-layout-audit/v1",
          "passed": {{passed.ToString().ToLowerInvariant()}},
          "issues": [{{issue}}]
        }
        """);
    }

    private void WritePassingNavigationPerformanceReport()
    {
        WriteNavigationPerformanceReport(visualMs: 40, cachedMs: 120);
    }

    private void WriteNavigationPerformanceReportWithWarning()
    {
        WriteNavigationPerformanceReport(visualMs: 150, cachedMs: 350);
    }

    private void WriteNavigationPerformanceReport(long visualMs, long cachedMs)
    {
        var folder = ArtifactRoot;
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "ui_navigation_performance.json"), $$"""
        {
          "schemaVersion": "ui-navigation-performance/v1",
          "visualResponseThresholdMilliseconds": 100,
          "cachedSwitchThresholdMilliseconds": 300,
          "events": [
            { "category": "NAVIGATION_VISUAL_RESPONSE", "name": "reports", "durationMilliseconds": {{visualMs}} },
            { "category": "CACHED_PAGE_SWITCH", "name": "reports", "durationMilliseconds": {{cachedMs}} }
          ]
        }
        """);
    }

    private static void WriteUiStabilityReport(UiNavigationSoakProfile profile, TimeSpan requested, TimeSpan actual)
    {
        var started = DateTime.UtcNow.Subtract(actual);
        var result = new UiNavigationSoakResult
        {
            StartedAtUtc = started,
            CompletedAtUtc = started.Add(actual),
            Profile = profile,
            RequestedDuration = requested,
            Status = "PASS",
            OperatorId = "GateAdmin [Admin]",
            StartWorkingSetMegabytes = 100,
            EndWorkingSetMegabytes = 112,
            MaxWorkingSetMegabytes = 118,
            CyclesCompleted = 3,
            PageAttempts = 42,
            SlowNavigationCount = 0,
            CrashCount = 0,
        };
        result.Events.Add(new UiNavigationSoakEvent
        {
            PageName = "Main Inspection",
            Status = "OK",
            PageLoadMilliseconds = 50,
            WorkingSetMegabytes = 112,
            Message = "Synthetic stability evidence for gate test.",
        });

        UiNavigationSoakTestService.WriteReports(result, UiNavigationSoakTestService.DefaultOutputRoot);
    }

    private static string FormatBlockingIssues(ClientDemoReadinessGateReport report)
        => string.Join(Environment.NewLine, report.BlockingIssues);
}
