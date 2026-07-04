using System.Diagnostics;
using System.Windows.Controls;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using AOI_Monitor.Views;
using Xunit;

namespace AOI_Monitor.Tests;

/// <summary>
/// Korean mode rewrites ComboBoxItem.Content for display, so any value that is persisted or
/// compared must round-trip through the invariant token in Tag (ComboBoxTokens). These tests
/// pin the regression where recipe ROI types and inspection view names were persisted as
/// translated strings and English persisted values no longer matched any combo item.
/// </summary>
public sealed class ComboBoxLocalizationRegressionTests : IDisposable
{
    private readonly string _root;

    public ComboBoxLocalizationRegressionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AOI_Monitor_ComboLocalization_Tests", Guid.NewGuid().ToString("N"));
        StorageRootSettingsService.ConfigureSettingsDirectoryForTests(_root);
        AoiDatabase.ConfigureStorageRoot(_root);
        AoiDatabase.Initialize();
        WorkflowState.Instance.SetCurrentUser("LocalizationAdmin", UserRole.Admin);
        UiPreferencesService.ResetForTests();
    }

    public void Dispose()
    {
        UiPreferencesService.ResetForTests();
        StorageRootSettingsService.ConfigureSettingsDirectoryForTests(null);
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Test cleanup failed for {nameof(ComboBoxLocalizationRegressionTests)}: {ex.Message}");
        }
    }

    [Fact]
    public void SelectedTokenPrefersInvariantTagOverTranslatedContent()
    {
        UiNavigationSmokeTests.RunOnStaForTests(() =>
        {
            var combo = new ComboBox();
            var item = new ComboBoxItem { Content = "솔더 체적", Tag = "Solder Volume" };
            combo.Items.Add(item);
            combo.SelectedItem = item;

            Assert.Equal("Solder Volume", ComboBoxTokens.SelectedToken(combo, "Presence"));
        });
    }

    [Fact]
    public void TokenFallsBackToContentWhenTagIsMissing()
    {
        UiNavigationSmokeTests.RunOnStaForTests(() =>
        {
            var combo = new ComboBox();
            var item = new ComboBoxItem { Content = "IPC Class 2" };
            combo.Items.Add(item);
            combo.SelectedItem = item;

            Assert.Equal("IPC Class 2", ComboBoxTokens.SelectedToken(combo, "IPC Class 1"));
        });
    }

    [Fact]
    public void SelectByTokenMatchesTagWhenContentIsTranslatedAndReportsUnknownTokens()
    {
        UiNavigationSmokeTests.RunOnStaForTests(() =>
        {
            var combo = new ComboBox();
            combo.Items.Add(new ComboBoxItem { Content = "상면", Tag = "Top" });
            combo.Items.Add(new ComboBoxItem { Content = "하면", Tag = "Bottom" });
            combo.SelectedIndex = 0;

            Assert.True(ComboBoxTokens.SelectByToken(combo, "Bottom"));
            Assert.Equal("Bottom", ComboBoxTokens.SelectedToken(combo, "Top"));

            Assert.False(ComboBoxTokens.SelectByToken(combo, "Side"));
            Assert.Equal("Bottom", ComboBoxTokens.SelectedToken(combo, "Top"));
        });
    }

    [Fact]
    public void KoreanModeRecipeRoiTypeStaysInvariantAndRoundTripsFromEnglish()
    {
        UiNavigationSmokeTests.RunOnStaForTests(() =>
        {
            UiNavigationSmokeTests.EnsureApplicationResourcesForTests();
            var view = new RecipeView();
            UiPreferencesService.ApplyLocalization(view, UiLanguage.Korean);

            var roiTypeCombo = Assert.IsType<ComboBox>(view.FindName("RoiTypeCombo"));

            // Restoring a recipe persisted in English mode must still find the item.
            Assert.True(ComboBoxTokens.SelectByToken(roiTypeCombo, "Solder Volume"));

            // Display is translated, but the value that BuildRecipeDocument persists stays English.
            var selected = Assert.IsType<ComboBoxItem>(roiTypeCombo.SelectedItem);
            Assert.Equal("솔더 체적", selected.Content);
            Assert.Equal("Solder Volume", ComboBoxTokens.SelectedToken(roiTypeCombo, "Presence"));
        });
    }

    [Fact]
    public void KoreanModeRecipeProcessingRulesRoundTripFromPersistedEnglish()
    {
        UiNavigationSmokeTests.RunOnStaForTests(() =>
        {
            UiNavigationSmokeTests.EnsureApplicationResourcesForTests();
            var view = new RecipeView();
            UiPreferencesService.ApplyLocalization(view, UiLanguage.Korean);

            var ipcCombo = Assert.IsType<ComboBox>(view.FindName("IpcClassCombo"));
            Assert.True(ComboBoxTokens.SelectByToken(ipcCombo, "IPC Class 3"));
            Assert.Equal("IPC Class 3", ComboBoxTokens.SelectedToken(ipcCombo, "IPC Class 2"));

            var policyCombo = Assert.IsType<ComboBox>(view.FindName("FalseCallPolicyCombo"));
            Assert.True(ComboBoxTokens.SelectByToken(policyCombo, "Strict: hold every suspected defect"));
            Assert.Equal(
                "Strict: hold every suspected defect",
                ComboBoxTokens.SelectedToken(policyCombo, "Balanced: review benign visual noise"));
        });
    }

    [Fact]
    public void KoreanModeMonitorViewSelectionKeepsInvariantViewTokens()
    {
        UiNavigationSmokeTests.RunOnStaForTests(() =>
        {
            UiNavigationSmokeTests.EnsureApplicationResourcesForTests();
            var view = new MonitorView();
            UiPreferencesService.ApplyLocalization(view, UiLanguage.Korean);

            var selector = Assert.IsType<ComboBox>(view.FindName("ViewSelector"));
            Assert.Equal("Top", ComboBoxTokens.SelectedToken(selector, "Top"));

            var expectedDisplay = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Top"] = "상면",
                ["Side"] = "측면",
                ["Bottom"] = "하면",
            };
            foreach (var item in selector.Items.OfType<ComboBoxItem>())
            {
                var token = ComboBoxTokens.Token(item);
                Assert.NotNull(token);
                Assert.Equal(expectedDisplay[token!], item.Content);
            }
        });
    }

    [Fact]
    public void KoreanModeCalibrationViewTypeRoundTripsFromEnglish()
    {
        UiNavigationSmokeTests.RunOnStaForTests(() =>
        {
            UiNavigationSmokeTests.EnsureApplicationResourcesForTests();
            var view = new CalibrationView();
            UiPreferencesService.ApplyLocalization(view, UiLanguage.Korean);

            var viewTypeCombo = Assert.IsType<ComboBox>(view.FindName("ViewTypeCombo"));
            Assert.True(ComboBoxTokens.SelectByToken(viewTypeCombo, "Bottom"));

            var selected = Assert.IsType<ComboBoxItem>(viewTypeCombo.SelectedItem);
            Assert.Equal("하면", selected.Content);
            Assert.Equal("Bottom", ComboBoxTokens.SelectedToken(viewTypeCombo, "Top"));
        });
    }
}
