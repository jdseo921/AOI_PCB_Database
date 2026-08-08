using AOI_Monitor.Models;

namespace AOI_Monitor.Tools;

/// <summary>
/// Shared parser for the <c>--priority</c> option. Every command that takes a detection policy
/// uses this one, so the accepted spellings and the error text cannot drift apart between
/// commands — a tester copying a policy name from one Stage 1 command into another must not hit
/// "unknown value".
/// </summary>
public static class DetectionPriorityOption
{
    /// <summary>Human-readable list of the canonical spellings, for usage and error text.</summary>
    public const string Choices = "balanced|minimize-false-positives|maximize-defect-recall";

    public static bool TryParse(string? value, out DetectionPriority priority)
    {
        // Tolerate underscores and the shorter "maximize-recall" alias so the option reads the
        // same whether it was copied from a script, a document, or another command's output.
        var normalized = (value ?? string.Empty)
            .Trim()
            .ToLowerInvariant()
            .Replace("_", "-", StringComparison.Ordinal);

        switch (normalized)
        {
            case "balanced":
                priority = DetectionPriority.Balanced;
                return true;
            case "minimize-false-positives":
            case "minimizefalsepositives":
            case "min-false-calls":
                priority = DetectionPriority.MinimizeFalsePositives;
                return true;
            case "maximize-defect-recall":
            case "maximize-recall":
            case "maximizedefectrecall":
                priority = DetectionPriority.MaximizeDefectRecall;
                return true;
            default:
                priority = DetectionPriority.Balanced;
                return false;
        }
    }

    public static string FailureMessage(string? value)
        => $"Unknown --priority value: {value}. Use {Choices}.";
}
