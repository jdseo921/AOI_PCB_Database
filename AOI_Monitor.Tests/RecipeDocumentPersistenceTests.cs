using System.Text.Json;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Xunit;

namespace AOI_Monitor.Tests;

public sealed class RecipeDocumentPersistenceTests : IDisposable
{
    private const string PreviewBoard = "PROFILE-PREVIEW-TEST-BOARD";
    private readonly string _root;

    public RecipeDocumentPersistenceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AOI_Recipe_Tests", Guid.NewGuid().ToString("N"));
        AoiDatabase.ConfigureStorageRoot(_root);
        RecipeService.Invalidate();
        RecipeService.ClearPreviewOverride();
    }

    public void Dispose()
    {
        RecipeService.ClearPreviewOverride();
        RecipeService.Invalidate();
        StorageRootSettingsService.ConfigureSettingsDirectoryForTests(null);
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException ex)
        {
            System.Diagnostics.Trace.WriteLine($"Recipe test cleanup skipped: {ex.Message}");
        }
    }

    [Fact]
    public void RecipeDocumentRoundTripsProcessingAndToleranceFields()
    {
        var doc = new RecipeDocument
        {
            RecipeName = "ROUND_TRIP",
            PlacementToleranceMm = 0.035,
            RotationToleranceDeg = 2.5,
            IpcClass = "IPC Class 3",
            LightingProfile = "Low-angle dark field",
            FalseCallPolicy = "Strict: escalate all anomalies",
        };

        // Mirrors the editor's save (WriteIndented) and load (default) round-trip.
        var json = JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
        var restored = JsonSerializer.Deserialize<RecipeDocument>(json);

        Assert.NotNull(restored);
        Assert.Equal(0.035, restored!.PlacementToleranceMm);
        Assert.Equal(2.5, restored.RotationToleranceDeg);
        Assert.Equal("IPC Class 3", restored.IpcClass);
        Assert.Equal("Low-angle dark field", restored.LightingProfile);
        Assert.Equal("Strict: escalate all anomalies", restored.FalseCallPolicy);
    }

    [Fact]
    public void PerRoiHeightAndVolumeLimitsSurviveSaveLoadAndParse()
    {
        // Customer spec §4.2 requires Height Min/Max and Volume Min/Max as per-ROI parameters.
        // Only AiScoreThreshold was previously asserted anywhere, so the four dimensional limits
        // could have silently stopped persisting without any test noticing.
        var doc = new RecipeDocument
        {
            RecipeName = "DIMENSIONAL",
            BoardProgram = "BOARD-DIM",
            Rois = new List<RecipeRoiDocument>
            {
                new()
                {
                    Id = "ROI-DIM-1",
                    Name = "J1 Pin Field",
                    RoiType = "Connector Pin Height",
                    X = 0.10,
                    Y = 0.20,
                    Width = 0.30,
                    Height = 0.15,
                    AiScoreThreshold = 0.80,
                    HeightMin = 0.045,
                    HeightMax = 0.310,
                    VolumeMin = 1.25,
                    VolumeMax = 9.75,
                },
            },
        };

        var json = JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
        var restored = JsonSerializer.Deserialize<RecipeDocument>(json);

        Assert.NotNull(restored);
        var restoredRoi = Assert.Single(restored!.Rois);
        Assert.Equal(0.045, restoredRoi.HeightMin);
        Assert.Equal(0.310, restoredRoi.HeightMax);
        Assert.Equal(1.25, restoredRoi.VolumeMin);
        Assert.Equal(9.75, restoredRoi.VolumeMax);

        var parsed = RecipeService.ParseDocument(restored, "REV-DIM", "Balanced", "BOARD-DIM", string.Empty, "DIMENSIONAL", "unit test");

        Assert.NotNull(parsed.Recipe);
        var thresholds = Assert.Single(parsed.Recipe!.Rois).Thresholds;
        Assert.Equal(0.80, thresholds.AiScoreThreshold);
        Assert.Equal(0.045, thresholds.HeightMin);
        Assert.Equal(0.310, thresholds.HeightMax);
        Assert.Equal(1.25, thresholds.VolumeMin);
        Assert.Equal(9.75, thresholds.VolumeMax);
    }

    [Fact]
    public void LegacyRecipeJsonWithoutNewFieldsFallsBackToDefaults()
    {
        const string legacyJson = "{\"RecipeName\":\"OLD\",\"BoardProgram\":\"B\",\"Rois\":[]}";

        var restored = JsonSerializer.Deserialize<RecipeDocument>(legacyJson);

        Assert.NotNull(restored);
        Assert.Equal(0.020, restored!.PlacementToleranceMm);
        Assert.Equal(1.0, restored.RotationToleranceDeg);
        Assert.Equal("IPC Class 2", restored.IpcClass);
        Assert.Equal("Top bright field", restored.LightingProfile);
        Assert.Equal("Balanced: review benign visual noise", restored.FalseCallPolicy);
    }

    [Fact]
    public void ParseDocumentUsesFallbackNameAndNormalizesRois()
    {
        var doc = new RecipeDocument
        {
            RecipeName = string.Empty,
            BoardProgram = string.Empty, // force the fallback board-program parameter to be used
            Rois = new List<RecipeRoiDocument>
            {
                new() { Name = "R1", X = 0.1, Y = 0.1, Width = 0.2, Height = 0.2 },
            },
        };

        var result = RecipeService.ParseDocument(doc, "REV-1", "Balanced", "BOARD-A", string.Empty, "FALLBACK_NAME", "unit test");

        Assert.NotNull(result.Recipe);
        Assert.Equal("FALLBACK_NAME", result.Recipe!.RecipeName);
        Assert.Equal("REV-1", result.Recipe.Revision);
        Assert.Equal("BOARD-A", result.Recipe.BoardProgram);
        Assert.Single(result.Recipe.Rois);
        Assert.True(result.HasEnabledRois);
    }

    [Fact]
    public void ParseDocumentWarnsWhenNoValidRoisRemain()
    {
        var doc = new RecipeDocument { RecipeName = "NO_ROI", Rois = new List<RecipeRoiDocument>() };

        var result = RecipeService.ParseDocument(doc, "REV", "Balanced", "BOARD", string.Empty, "FB", "unit test");

        Assert.NotNull(result.Recipe);
        Assert.Empty(result.Recipe!.Rois);
        Assert.Contains(result.Warnings, w => w.Contains("no valid ROI", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PreviewOverrideMakesLoadLatestReturnInEditorRecipeThenClears()
    {
        AoiDatabase.Initialize();

        var doc = new RecipeDocument
        {
            RecipeName = "IN_EDITOR",
            Rois = new List<RecipeRoiDocument>
            {
                new() { Name = "R1", X = 0.1, Y = 0.1, Width = 0.3, Height = 0.3 },
            },
        };
        var preview = RecipeService.ParseDocument(doc, "IN-EDITOR PREVIEW", "Balanced", PreviewBoard, string.Empty, "IN_EDITOR", "in-editor preview");

        RecipeService.SetPreviewOverride(PreviewBoard, preview);
        try
        {
            var loaded = RecipeService.LoadLatestRecipe(PreviewBoard);
            Assert.Same(preview, loaded);
            Assert.Equal("IN_EDITOR", loaded.Recipe!.RecipeName);
        }
        finally
        {
            RecipeService.ClearPreviewOverride();
        }

        var afterClear = RecipeService.LoadLatestRecipe(PreviewBoard);
        Assert.NotSame(preview, afterClear);
    }
}
