using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using AOI_Monitor.Views;
using Xunit;

namespace AOI_Monitor.Tests;

/// <summary>
/// End-to-end regression tests: switching the UI language to Korean must never change
/// persisted recipe values. ComboBoxLocalizationRegressionTests pins the ComboBoxTokens
/// mechanics per view; these tests drive the real RecipeView load -> edit -> save-button
/// path against the database in Korean mode, and cover healing of recipes that were
/// persisted with Korean tokens by builds that predate ComboBoxTokens.
/// </summary>
public sealed class KoreanLocalizationPersistenceTests : IDisposable
{
    private readonly string _root;

    public KoreanLocalizationPersistenceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AOI_Monitor_KoLoc_Tests", Guid.NewGuid().ToString("N"));
        StorageRootSettingsService.ConfigureSettingsDirectoryForTests(_root);
        AoiDatabase.ConfigureStorageRoot(_root);
        AoiDatabase.Initialize();
        WorkflowState.Instance.SetCurrentUser("KoLocAdmin", UserRole.Admin);
        UiPreferencesService.ResetForTests();
        RecipeService.Invalidate();
    }

    public void Dispose()
    {
        RecipeService.Invalidate();
        UiPreferencesService.ResetForTests();
        StorageRootSettingsService.ConfigureSettingsDirectoryForTests(null);
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Test cleanup failed for {nameof(KoreanLocalizationPersistenceTests)}: {ex.Message}");
        }
    }

    [Fact]
    public void ToEnglishUiTokenMapsUniqueKoreanValuesBack()
    {
        Assert.Equal("Solder Volume", UiPreferencesService.ToEnglishUiToken("솔더 체적"));
        Assert.Equal("Placement", UiPreferencesService.ToEnglishUiToken("배치"));
        Assert.Equal("Top", UiPreferencesService.ToEnglishUiToken("상면"));

        // English tokens and unknown strings pass through unchanged.
        Assert.Equal("Solder Volume", UiPreferencesService.ToEnglishUiToken("Solder Volume"));
        Assert.Equal("Presence", UiPreferencesService.ToEnglishUiToken("Presence"));
        Assert.Equal(string.Empty, UiPreferencesService.ToEnglishUiToken(null));

        // "결함 검토" is the translation of both "Review Defects" and "Defect Review";
        // an ambiguous reverse mapping must not guess.
        Assert.Equal("결함 검토", UiPreferencesService.ToEnglishUiToken("결함 검토"));
    }

    [Fact]
    public void RecipeSavedThroughUiInKoreanModePersistsEnglishTokens()
    {
        UiNavigationSmokeTests.RunOnStaForTests(() =>
        {
            UiNavigationSmokeTests.EnsureApplicationResourcesForTests();
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

            var png = WriteTinyPng();
            var boardProgram = WorkflowState.Instance.BoardProgram;
            SeedRecipeRevision(boardProgram, png, roiType: "Misalignment");

            var view = new RecipeView();
            UiPreferencesService.ApplyLocalization(view, UiLanguage.Korean);
            PumpUntilCompleted(view.OnNavigatedToAsync(CancellationToken.None));

            // The recipe persisted with English tokens still round-trips into the combos
            // while the UI is Korean.
            var ipcCombo = (ComboBox)view.FindName("IpcClassCombo");
            var lightingCombo = (ComboBox)view.FindName("LightingProfileCombo");
            var policyCombo = (ComboBox)view.FindName("FalseCallPolicyCombo");
            Assert.Equal("IPC Class 3", ComboBoxTokens.SelectedToken(ipcCombo, "IPC Class 2"));
            Assert.Equal("Side marking light", ComboBoxTokens.SelectedToken(lightingCombo, "Top bright field"));
            Assert.Equal(
                "Strict: hold every suspected defect",
                ComboBoxTokens.SelectedToken(policyCombo, "Balanced: review benign visual noise"));

            var roiGrid = (DataGrid)view.FindName("RoiGrid");
            var row = Assert.Single(roiGrid.ItemsSource.OfType<RecipeView.RecipeRoiRow>());
            Assert.Equal("Misalignment", row.RoiType);

            // Operator changes the active ROI's type in Korean mode. "Solder Volume" is a
            // KoreanText dictionary key ("솔더 체적"), so before ComboBoxTokens the translated
            // Content string was written into the ROI row and persisted.
            var roiTypeCombo = (ComboBox)view.FindName("RoiTypeCombo");
            Assert.True(ComboBoxTokens.SelectByToken(roiTypeCombo, "Solder Volume"));
            Assert.Equal("솔더 체적", ((ComboBoxItem)roiTypeCombo.SelectedItem).Content);
            Assert.Equal("Solder Volume", row.RoiType);

            // Save through the real button-click path and read back what was persisted.
            var saveButton = (Button)view.FindName("SaveRecipeButton");
            saveButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            var revision = AoiDatabase.GetLatestRecipeRevision(boardProgram);
            Assert.NotNull(revision);
            var persisted = JsonSerializer.Deserialize<RecipeDocument>(revision!.RecipeJson);
            Assert.NotNull(persisted);
            var persistedRoi = Assert.Single(persisted!.Rois);
            Assert.Equal("Solder Volume", persistedRoi.RoiType);
            Assert.Equal("IPC Class 3", persisted.IpcClass);
            Assert.Equal("Side marking light", persisted.LightingProfile);
            Assert.Equal("Strict: hold every suspected defect", persisted.FalseCallPolicy);
            Assert.False(ContainsHangul(revision.RecipeJson),
                $"Persisted recipe JSON must not contain Korean text:\n{revision.RecipeJson}");
        });
    }

    [Fact]
    public void RecipeCorruptedWithKoreanTokensHealsToEnglishOnLoad()
    {
        UiNavigationSmokeTests.RunOnStaForTests(() =>
        {
            UiNavigationSmokeTests.EnsureApplicationResourcesForTests();
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

            var png = WriteTinyPng();
            var boardProgram = WorkflowState.Instance.BoardProgram;
            // A recipe saved by a build that predates ComboBoxTokens while the UI was
            // Korean: the ROI type carries the translated display string, not the token.
            SeedRecipeRevision(boardProgram, png, roiType: "솔더 체적");

            // The inspection engine heals the token when parsing the revision.
            RecipeService.Invalidate(boardProgram);
            var engineResult = RecipeService.LoadLatestRecipe(boardProgram);
            Assert.NotNull(engineResult.Recipe);
            Assert.Equal("Solder Volume", Assert.Single(engineResult.Recipe!.Rois).RoiType);

            // The editor heals the token on load as well, so the next save persists English.
            var view = new RecipeView();
            UiPreferencesService.ApplyLocalization(view, UiLanguage.Korean);
            PumpUntilCompleted(view.OnNavigatedToAsync(CancellationToken.None));

            var roiGrid = (DataGrid)view.FindName("RoiGrid");
            var row = Assert.Single(roiGrid.ItemsSource.OfType<RecipeView.RecipeRoiRow>());
            Assert.Equal("Solder Volume", row.RoiType);
        });
    }

    private static void SeedRecipeRevision(string boardProgram, string backgroundImagePath, string roiType)
    {
        var document = new RecipeDocument
        {
            RecipeName = "KO_REG",
            BoardProgram = boardProgram,
            BackgroundImagePath = backgroundImagePath,
            IpcClass = "IPC Class 3",
            LightingProfile = "Side marking light",
            FalseCallPolicy = "Strict: hold every suspected defect",
            Rois =
            {
                new RecipeRoiDocument
                {
                    Id = "roi-1",
                    Name = "R1",
                    RoiType = roiType,
                    X = 0.1,
                    Y = 0.1,
                    Width = 0.2,
                    Height = 0.2,
                },
            },
        };

        AoiDatabase.SaveRecipeRevision(
            document.RecipeName,
            boardProgram,
            "KoLocAdmin",
            "Balanced",
            backgroundImagePath,
            JsonSerializer.Serialize(document));
        RecipeService.Invalidate(boardProgram);
    }

    private string WriteTinyPng()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "recipe_bg.png");
        var bitmap = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null, new byte[4 * 4 * 4], 4 * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    private static bool ContainsHangul(string text)
        => text.Any(c => c >= '가' && c <= '힣');

    private static void PumpUntilCompleted(Task task)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        task.ContinueWith(
            _ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)),
            TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }
}
