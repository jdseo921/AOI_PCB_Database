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

    public static RecipeLoadResult LoadLatestRecipe(string boardProgram)
    {
        var normalizedBoard = string.IsNullOrWhiteSpace(boardProgram) ? "UNKNOWN" : boardProgram.Trim();
        lock (Sync)
        {
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
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(revision.RecipeJson))
            return new RecipeLoadResult(null, new[] { $"Recipe revision {revision.Id} has empty JSON." });

        try
        {
            var document = JsonSerializer.Deserialize<RecipeDocument>(revision.RecipeJson, JsonOptions);
            if (document is null)
                return new RecipeLoadResult(null, new[] { $"Recipe revision {revision.Id} JSON parsed to an empty document." });

            var recipe = new RecipeDefinition
            {
                RecipeName = string.IsNullOrWhiteSpace(document.RecipeName) ? revision.RecipeName : document.RecipeName.Trim(),
                Revision = revision.Revision,
                BoardProgram = string.IsNullOrWhiteSpace(document.BoardProgram) ? revision.BoardProgram : document.BoardProgram.Trim(),
                DetectionPriority = revision.DetectionPriority,
                BackgroundImagePath = string.IsNullOrWhiteSpace(document.BackgroundImagePath)
                    ? revision.BackgroundImagePath
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
                warnings.Add($"Recipe '{recipe.RecipeName}' contains no valid ROI definitions.");

            return new RecipeLoadResult(recipe, warnings);
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
