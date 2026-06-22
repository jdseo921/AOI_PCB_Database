using System.Text.Json;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Xunit;

namespace AOI_Monitor.Tests;

public sealed class StandardsTraceabilityServiceTests : IDisposable
{
    private readonly string _root;

    public StandardsTraceabilityServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AOI_Monitor_StandardsTraceability_Tests", Guid.NewGuid().ToString("N"));
        StorageRootSettingsService.ConfigureSettingsDirectoryForTests(_root);
        AoiDatabase.ConfigureStorageRoot(_root);
        AoiDatabase.Initialize();
        WorkflowState.Instance.SetCurrentUser("TraceAdmin", UserRole.Admin);
        DeploymentProfileSettingsService.ResetForTests();
        OperatingModeSettingsService.ResetForTests();
        CrashReportService.ClearForTests();
    }

    public void Dispose()
    {
        CrashReportService.ClearForTests();
        DeploymentProfileSettingsService.ResetForTests();
        OperatingModeSettingsService.ResetForTests();
        StorageRootSettingsService.ConfigureSettingsDirectoryForTests(null);
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException ex)
        {
            System.Diagnostics.Trace.WriteLine($"Test cleanup failed for {_root}: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            System.Diagnostics.Trace.WriteLine($"Test cleanup failed for {_root}: {ex.Message}");
        }
    }

    [Fact]
    public void MatrixContainsRequiredSourcesAndCertificationBoundary()
    {
        var report = StandardsTraceabilityService.Evaluate(DeploymentProfile.Stage1ImageValidation, EmptyArtifactRoot());

        Assert.Contains(report.Items, item => item.StandardOrSource == "Project Spec");
        Assert.Contains(report.Items, item => item.StandardOrSource == "ISO 9241-style HMI");
        Assert.Contains(report.Items, item => item.StandardOrSource == "ISO/IEC 25010-style Quality");
        Assert.Contains(report.Items, item => item.StandardOrSource == "IEC 62682 / ISA-18.2-style Alarm Discipline");
        Assert.Contains("not formal", report.CertificationStatement, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ISO certified", StandardsTraceabilityService.BuildHtml(report), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingLayoutEvidenceIsVisibleAsReleaseBlocker()
    {
        var report = StandardsTraceabilityService.Evaluate(DeploymentProfile.Stage1ImageValidation, EmptyArtifactRoot());

        var row = Assert.Single(report.Items, item => item.ProjectRequirementId == "HMI-001");
        Assert.Equal(StandardsTraceabilityStatus.Missing, row.Status);
        Assert.Equal(StandardsTraceabilityBlockingLevel.ReleaseBlocker, row.BlockingLevel);
        Assert.True(report.ReleaseBlockerMissingCount > 0);
    }

    [Fact]
    public void ExportWritesHtmlPdfAndJson()
    {
        var folder = Path.Combine(_root, "traceability-export");

        var export = StandardsTraceabilityService.ExportToFolder(folder, DeploymentProfile.Stage1ImageValidation, EmptyArtifactRoot(), recordExport: false);

        Assert.True(File.Exists(export.JsonPath));
        Assert.True(File.Exists(export.HtmlPath));
        Assert.True(File.Exists(export.PdfPath));
        using var document = JsonDocument.Parse(File.ReadAllText(export.JsonPath));
        Assert.True(document.RootElement.TryGetProperty("certificationStatement", out var statement));
        Assert.Contains("not formal", statement.GetString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.True(document.RootElement.GetProperty("items").GetArrayLength() > 0);
    }

    [Fact]
    public void ClientDemoGateIncludesStandardsTraceabilitySummary()
    {
        var report = ClientDemoReadinessGateService.Evaluate(DeploymentProfile.Stage1ImageValidation);

        Assert.Contains(report.Checks, check => check.Name == "Standards traceability");
        Assert.Contains("Standards traceability", report.StandardsTraceabilitySummary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not certification", ClientDemoReadinessGateService.BuildText(report), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FactoryReadinessPackageIncludesStandardsTraceabilityFiles()
    {
        var result = FactoryReadinessService.ExportGoNoGoPackage(new FactoryReadinessCriteria
        {
            DeploymentProfile = DeploymentProfile.Stage1ImageValidation,
            Stage1Only = true,
            RequireSuccessfulLatestValidationPackage = false,
            RequireDatasetQualityEvidence = false,
            RequireNoExportVerificationErrors = false,
        }, Path.Combine(_root, "readiness"));

        Assert.True(File.Exists(Path.Combine(result.PackageFolder, "standards_traceability_matrix.json")));
        Assert.True(File.Exists(Path.Combine(result.PackageFolder, "standards_traceability_matrix.html")));
        Assert.True(File.Exists(Path.Combine(result.PackageFolder, "standards_traceability_matrix.pdf")));

        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(result.PackageFolder, "package_manifest.json")));
        Assert.Contains(
            manifest.RootElement.GetProperty("includedFiles").EnumerateArray(),
            file => file.GetProperty("relativePath").GetString() == "standards_traceability_matrix.json");
    }

    [Fact]
    public void ExportStandardsTraceabilityForCi()
    {
        var outputRoot = Environment.GetEnvironmentVariable("AOI_STANDARDS_TRACEABILITY_OUTPUT");
        if (string.IsNullOrWhiteSpace(outputRoot))
            outputRoot = Path.Combine(_root, "ci");

        Directory.CreateDirectory(outputRoot);
        var export = StandardsTraceabilityService.ExportToFolder(
            outputRoot,
            DeploymentProfile.Stage1ImageValidation,
            artifactRoot: outputRoot,
            recordExport: false);

        Assert.Equal(Path.Combine(outputRoot, "standards_traceability_matrix.json"), export.JsonPath);
        Assert.True(File.Exists(export.JsonPath));
        Assert.True(File.Exists(export.HtmlPath));
        Assert.True(File.Exists(export.PdfPath));
    }

    private string EmptyArtifactRoot()
    {
        var path = Path.Combine(_root, "empty-artifacts");
        Directory.CreateDirectory(path);
        return path;
    }
}
