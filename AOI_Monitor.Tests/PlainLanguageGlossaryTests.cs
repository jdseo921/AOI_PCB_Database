using System.Text.Json;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Xunit;

namespace AOI_Monitor.Tests;

public sealed class PlainLanguageGlossaryTests : IDisposable
{
    private readonly string _root;

    public PlainLanguageGlossaryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AOI_Monitor_PlainLanguage_Tests", Guid.NewGuid().ToString("N"));
        StorageRootSettingsService.ConfigureSettingsDirectoryForTests(_root);
        AoiDatabase.ConfigureStorageRoot(_root);
        AoiDatabase.Initialize();
        UiPreferencesService.ResetForTests();
        DeploymentProfileSettingsService.ResetForTests();
        OperatingModeSettingsService.ResetForTests();
    }

    public void Dispose()
    {
        UiPreferencesService.ResetForTests();
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
            System.Diagnostics.Trace.WriteLine($"Test cleanup failed for {nameof(PlainLanguageGlossaryTests)}: {ex.Message}");
        }
    }

    [Fact]
    public void GlossaryIncludesRequiredClientTerms()
    {
        var entries = PlainLanguageGlossaryService.All.ToDictionary(entry => entry.Key, StringComparer.OrdinalIgnoreCase);

        Assert.Equal("The system marked a good board as a defect.", entries["FalseCall"].PlainEnglish);
        Assert.Equal("The system may have missed a real defect.", entries["PossibleEscape"].PlainEnglish);
        Assert.Equal("Inspection area on the board.", entries["ROI"].PlainEnglish);
        Assert.Contains("traceability", entries["MES"].PlainEnglish, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AI model file", entries["ONNXModel"].PlainEnglish, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not final certification", entries["AcceptanceTest"].PlainEnglish, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GlossarySupportsKoreanFriendlyMajorTerms()
    {
        Directory.CreateDirectory(StorageRootSettingsService.SettingsDirectory);
        File.WriteAllText(
            UiPreferencesService.SettingsPath,
            JsonSerializer.Serialize(new UiPreferences { Language = UiLanguage.Korean, FontPreset = UiFontPreset.Standard }));
        UiPreferencesService.ResetForTests();

        Assert.Contains("정상 보드", PlainLanguageGlossaryService.Explain("FalseCall"), StringComparison.Ordinal);
        Assert.Contains("공장", PlainLanguageGlossaryService.Explain("MES"), StringComparison.Ordinal);
        Assert.Contains("최종 인증", PlainLanguageGlossaryService.Explain("AcceptanceTest"), StringComparison.Ordinal);
    }

    [Fact]
    public void SimulationEvidenceDoesNotClaimProductionReady()
    {
        OperatingModeSettingsService.Save(OperatingMode.Demo, "PlainLanguageTest [Admin]");

        var report = FactoryReadinessService.Evaluate(new FactoryReadinessCriteria
        {
            DeploymentProfile = DeploymentProfile.Stage1ImageValidation,
            Stage1Only = true,
            RequireSuccessfulLatestValidationPackage = false,
            RequireDatasetQualityEvidence = false,
            RequireNoExportVerificationErrors = false,
        });

        var customerText = string.Join(Environment.NewLine,
            report.Scope,
            string.Join(Environment.NewLine, report.KnownLimitations),
            string.Join(Environment.NewLine, report.Categories.SelectMany(category => new[] { category.Name, category.Evidence, category.NextAction })));

        Assert.DoesNotContain("production ready", customerText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("production-ready", customerText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OperatorErrorMessageDoesNotExposeRawExceptionMessage()
    {
        var result = CrashReportService.WriteReport(new CrashReportRequest
        {
            OperationName = "Dataset check",
            CurrentPage = "AI Model Test",
            Exception = new InvalidOperationException("SQLITE_BUSY raw path C:\\customer\\secret\\board.png"),
            IsFatal = false,
            IsUiThread = true,
        });

        Assert.Contains("failed safely", result.OperatorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SQLITE_BUSY", result.OperatorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", result.OperatorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("InvalidOperationException", result.OperatorMessage, StringComparison.OrdinalIgnoreCase);
    }
}
