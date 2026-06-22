using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Xunit;

namespace AOI_Monitor.Tests;

public sealed class PilotIssueServiceTests : IDisposable
{
    private readonly string _root;

    public PilotIssueServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AOI_Monitor_PilotIssue_Tests", Guid.NewGuid().ToString("N"));
        AoiDatabase.ConfigureStorageRoot(_root);
        AoiDatabase.Initialize();
        AuthenticationSettingsService.ResetForTests();
        OperatingModeSettingsService.ResetForTests();
        OperatingModeSettingsService.Save(OperatingMode.Demo, "Admin01 [Admin]");
        DeploymentProfileSettingsService.ResetForTests();
        CentralSyncSettingsService.ResetForTests();
        MesIntegrationSettingsService.Save(new MesIntegrationSettings());
    }

    public void Dispose()
    {
        AuthenticationSettingsService.ResetForTests();
        OperatingModeSettingsService.ResetForTests();
        DeploymentProfileSettingsService.ResetForTests();
        CentralSyncSettingsService.ResetForTests();
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void CreateUpdateAndCloseIssue()
    {
        var created = PilotIssueService.Create(new PilotIssue
        {
            Category = PilotIssueCategory.FalseCall,
            Severity = "High",
            BoardModel = "BOARD-A",
            LotId = "LOT-1",
            ImagePath = @"C:\customer\images\board-a.png",
            PageName = "AI Model Test",
            ReproductionSteps = "Open AI Model Test, load pilot image, switch tabs twice.",
            ExpectedBehavior = "The tab switch remains responsive and labels stay visible.",
            ActualBehavior = "The status card clips the message after switching tabs.",
            ScreenshotPath = @"C:\customer\screenshots\clip-a.png",
            RelatedInspectionId = "42",
            Owner = "Engineer01 [Engineer]",
            Notes = "False call under customer lighting.",
        }, "Engineer01 [Engineer]");

        Assert.StartsWith("ISSUE-", created.IssueId, StringComparison.Ordinal);
        Assert.Equal(PilotIssueStatus.Open, created.Status);
        Assert.Equal("AI Model Test", created.PageName);
        Assert.Equal("Open AI Model Test, load pilot image, switch tabs twice.", created.ReproductionSteps);
        Assert.Equal("The tab switch remains responsive and labels stay visible.", created.ExpectedBehavior);
        Assert.Equal("The status card clips the message after switching tabs.", created.ActualBehavior);
        Assert.Equal(@"C:\customer\screenshots\clip-a.png", created.ScreenshotPath);

        created.Status = PilotIssueStatus.Investigating;
        created.Owner = "Admin01 [Admin]";
        var updated = PilotIssueService.Update(created, "Assigned for model review.", "Admin01 [Admin]");

        Assert.Equal(PilotIssueStatus.Investigating, updated.Status);
        Assert.Equal("Admin01 [Admin]", updated.Owner);
        Assert.Equal("AI Model Test", updated.PageName);

        var closed = PilotIssueService.Close(updated.IssueId, "Threshold profile updated and verified.", "Admin01 [Admin]");
        var events = AoiDatabase.GetPilotIssueEvents(updated.IssueId);

        Assert.Equal(PilotIssueStatus.Closed, closed.Status);
        Assert.NotNull(closed.ClosedAtUtc);
        Assert.Equal("Threshold profile updated and verified.", closed.Resolution);
        Assert.Equal(3, events.Count);
        Assert.Contains(events, item => item.EventType == "CREATE");
        Assert.Contains(events, item => item.EventType == "UPDATE");
    }

    [Fact]
    public void CriticalOpenIssueDegradesReadiness()
    {
        PilotIssueService.Create(new PilotIssue
        {
            Category = PilotIssueCategory.PossibleEscape,
            Severity = "Critical",
            BoardModel = "BOARD-A",
            LotId = "LOT-1",
            Notes = "Customer flagged possible escape during pilot validation.",
        }, "Engineer01 [Engineer]");

        var report = FactoryReadinessService.Evaluate(new FactoryReadinessCriteria
        {
            DeploymentProfile = DeploymentProfile.Stage1ImageValidation,
            Stage1Only = true,
            RequireSuccessfulLatestValidationPackage = false,
            RequireDatasetQualityEvidence = false,
            RequireNoExportVerificationErrors = false,
        });

        Assert.Contains(report.Categories, category =>
            category.Name == "Pilot issue status" &&
            category.Status == "Conditional" &&
            category.Evidence.Contains("criticalOpen=1", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(report.Warnings, warning => warning.Contains("Pilot issue status", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExportRedactsImagePathsWhenConfigured()
    {
        var imagePath = Path.Combine(_root, "customer", "images", "board-a.png");
        var screenshotPath = Path.Combine(_root, "customer", "screenshots", "clip-a.png");
        PilotIssueService.Create(new PilotIssue
        {
            Category = PilotIssueCategory.Camera,
            Severity = "Medium",
            BoardModel = "BOARD-A",
            LotId = "LOT-1",
            ImagePath = imagePath,
            PageName = "Main Inspection",
            ReproductionSteps = "Open the lot, select the failed panel, export the finding.",
            ExpectedBehavior = "Export keeps evidence available without exposing raw customer paths.",
            ActualBehavior = "Pilot evidence references a customer screenshot path.",
            ScreenshotPath = screenshotPath,
            Notes = "Camera exposure issue references customer image path.",
        }, "Engineer01 [Engineer]");

        var export = PilotIssueService.Export(new PilotIssueExportOptions
        {
            OutputRoot = Path.Combine(_root, "exports"),
            RedactImagePaths = true,
        }, "Admin01 [Admin]");

        var json = File.ReadAllText(export.JsonPath);
        var csv = File.ReadAllText(export.CsvPath);
        var html = File.ReadAllText(export.HtmlPath);

        Assert.DoesNotContain(imagePath, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(imagePath, csv, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(imagePath, html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(screenshotPath, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(screenshotPath, csv, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(screenshotPath, html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Main Inspection", json, StringComparison.Ordinal);
        Assert.Contains("reproductionSteps", json, StringComparison.Ordinal);
        Assert.Contains("[REDACTED_IMAGE_PATH]", json, StringComparison.Ordinal);
        Assert.Contains("[REDACTED_IMAGE_PATH]", csv, StringComparison.Ordinal);
        Assert.Contains("[REDACTED_IMAGE_PATH]", html, StringComparison.Ordinal);
    }
}
