using System.IO;
using System.Text.Json;
using AOI_Monitor.Data;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public static class RecipeService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly object Sync = new();
    private static readonly Dictionary<string, RecipeLoadResult> Cache = new(StringComparer.OrdinalIgnoreCase);

    // Transient in-editor preview: when set, LoadLatestRecipe returns this instead of the saved
    // revision so "Test Run" can exercise unsaved ROI/parameter edits. Always cleared in a finally.
    private static string? _previewBoard;
    private static RecipeLoadResult? _previewOverride;

    public static RecipeLoadResult LoadLatestRecipe(string boardProgram)
    {
        var normalizedBoard = string.IsNullOrWhiteSpace(boardProgram) ? "UNKNOWN" : boardProgram.Trim();
        lock (Sync)
        {
            if (_previewOverride is not null &&
                string.Equals(_previewBoard, normalizedBoard, StringComparison.OrdinalIgnoreCase))
            {
                return _previewOverride;
            }

            if (Cache.TryGetValue(normalizedBoard, out var cached))
                return cached;
        }

        var loaded = LoadLatestRecipeUncached(normalizedBoard);
        lock (Sync)
        {
            Cache[normalizedBoard] = loaded;
        }

        return loaded;
    }

    public static void SetPreviewOverride(string boardProgram, RecipeLoadResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (Sync)
        {
            _previewBoard = string.IsNullOrWhiteSpace(boardProgram) ? "UNKNOWN" : boardProgram.Trim();
            _previewOverride = result;
        }
    }

    public static void ClearPreviewOverride()
    {
        lock (Sync)
        {
            _previewBoard = null;
            _previewOverride = null;
        }
    }

    public static void Invalidate(string? boardProgram = null)
    {
        lock (Sync)
        {
            if (string.IsNullOrWhiteSpace(boardProgram))
                Cache.Clear();
            else
                Cache.Remove(boardProgram.Trim());
        }
    }

    public static RecipeLoadResult ParseRevision(RecipeRevisionRecord revision)
    {
        if (string.IsNullOrWhiteSpace(revision.RecipeJson))
            return new RecipeLoadResult(null, new[] { $"Recipe revision {revision.Id} has empty JSON." });

        try
        {
            var document = JsonSerializer.Deserialize<RecipeDocument>(revision.RecipeJson, JsonOptions);
            if (document is null)
                return new RecipeLoadResult(null, new[] { $"Recipe revision {revision.Id} JSON parsed to an empty document." });

            HealLocalizedTokens(document);
            return ParseDocument(
                document,
                revision.Revision,
                revision.DetectionPriority,
                revision.BoardProgram,
                revision.BackgroundImagePath,
                revision.RecipeName,
                $"revision {revision.Id}");
        }
        catch (JsonException ex)
        {
            return new RecipeLoadResult(null, new[] { $"Recipe revision {revision.Id} JSON is malformed: {ex.Message}" });
        }
        catch (NotSupportedException ex)
        {
            return new RecipeLoadResult(null, new[] { $"Recipe revision {revision.Id} JSON could not be parsed: {ex.Message}" });
        }
    }

    /// <summary>
    /// Rewrites Korean display strings back to English tokens in a deserialized recipe
    /// document. Builds that predate ComboBoxTokens let the localization walker rewrite
    /// ComboBoxItem.Content, so recipes saved in Korean mode may carry Korean ROI types
    /// and rule tokens that the rest of the pipeline compares against English.
    /// </summary>
    public static void HealLocalizedTokens(RecipeDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.IpcClass = UiPreferencesService.ToEnglishUiToken(document.IpcClass);
        document.LightingProfile = UiPreferencesService.ToEnglishUiToken(document.LightingProfile);
        document.FalseCallPolicy = UiPreferencesService.ToEnglishUiToken(document.FalseCallPolicy);
        foreach (var roi in document.Rois)
            roi.RoiType = UiPreferencesService.ToEnglishUiToken(roi.RoiType);
    }

    public static RecipeLoadResult ParseDocument(
        RecipeDocument document,
        string revision,
        string detectionPriority,
        string boardProgram,
        string backgroundImagePath,
        string fallbackRecipeName,
        string sourceLabel)
    {
        ArgumentNullException.ThrowIfNull(document);
        var warnings = new List<string>();
        var recipe = new RecipeDefinition
        {
            RecipeName = string.IsNullOrWhiteSpace(document.RecipeName)
                ? (string.IsNullOrWhiteSpace(fallbackRecipeName) ? "AOI_RECIPE" : fallbackRecipeName.Trim())
                : document.RecipeName.Trim(),
            Revision = revision,
            BoardProgram = string.IsNullOrWhiteSpace(document.BoardProgram) ? boardProgram : document.BoardProgram.Trim(),
            DetectionPriority = detectionPriority,
            BackgroundImagePath = string.IsNullOrWhiteSpace(document.BackgroundImagePath)
                ? backgroundImagePath
                : document.BackgroundImagePath.Trim(),
        };

        var index = 1;
        foreach (var roiDoc in document.Rois)
        {
            var roi = NormalizeRoi(roiDoc, index++, warnings);
            if (roi is not null)
                recipe.Rois.Add(roi);
        }

        if (recipe.Rois.Count == 0)
            warnings.Add($"Recipe '{recipe.RecipeName}' ({sourceLabel}) contains no valid ROI definitions.");

        return new RecipeLoadResult(recipe, warnings);
    }

    private static RecipeLoadResult LoadLatestRecipeUncached(string boardProgram)
    {
        try
        {
            var revision = AoiDatabase.GetLatestRecipeRevision(boardProgram);
            return revision is null
                ? new RecipeLoadResult(null, Array.Empty<string>())
                : ParseRevision(revision);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return new RecipeLoadResult(null, new[] { $"Recipe load failed for board program '{boardProgram}': {ex.Message}" });
        }
    }

    private static RecipeRoi? NormalizeRoi(RecipeRoiDocument source, int index, ICollection<string> warnings)
    {
        var id = string.IsNullOrWhiteSpace(source.Id)
            ? $"ROI-{index:000}"
            : source.Id.Trim();
        var width = Math.Clamp(source.Width, 0, 1);
        var height = Math.Clamp(source.Height, 0, 1);
        if (width <= 0 || height <= 0)
        {
            warnings.Add($"ROI '{id}' has zero width or height and was ignored.");
            return null;
        }

        var x = Math.Clamp(source.X, 0, 1);
        var y = Math.Clamp(source.Y, 0, 1);
        if (x + width > 1)
            width = Math.Max(0, 1 - x);
        if (y + height > 1)
            height = Math.Max(0, 1 - y);

        if (width <= 0 || height <= 0)
        {
            warnings.Add($"ROI '{id}' is outside the normalized image bounds and was ignored.");
            return null;
        }

        return new RecipeRoi
        {
            RoiId = id,
            RoiName = string.IsNullOrWhiteSpace(source.Name) ? id : source.Name.Trim(),
            RoiType = string.IsNullOrWhiteSpace(source.RoiType) ? "Presence" : source.RoiType.Trim(),
            X = x,
            Y = y,
            Width = width,
            Height = height,
            Enabled = source.Enabled,
            Thresholds = new RecipeThresholds
            {
                AiScoreThreshold = Math.Clamp(source.AiScoreThreshold, 0, 1),
                HeightMin = source.HeightMin,
                HeightMax = source.HeightMax,
                VolumeMin = source.VolumeMin,
                VolumeMax = source.VolumeMax,
            },
        };
    }
}
