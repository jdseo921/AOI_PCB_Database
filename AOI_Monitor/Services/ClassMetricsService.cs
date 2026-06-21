using System.Globalization;
using System.Text;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public static class ClassMetricsService
{
    public static ValidationBreakdownSummary Calculate(IReadOnlyCollection<BatchTestRow> rows)
    {
        var rowArray = rows.ToArray();
        var summary = new ValidationBreakdownSummary
        {
            DefectClassMetrics = BuildGroupMetrics(rowArray, "DefectClass", DefectClassKey).ToList(),
            SideMetrics = BuildGroupMetrics(rowArray, "Side", row => row.NormalizedSide).ToList(),
            RoiMetrics = BuildGroupMetrics(rowArray, "RoiId", row => BatchValidationService.NormalizeAssignment(row.RoiId)).ToList(),
            RoiTypeMetrics = BuildGroupMetrics(rowArray, "RoiType", row => BatchValidationService.NormalizeAssignment(row.RoiType)).ToList(),
        };

        summary.TopFalseCallContributors = summary.DefectClassMetrics
            .Concat(summary.SideMetrics)
            .Concat(summary.RoiMetrics)
            .Where(metric => metric.FalsePositive > 0)
            .OrderByDescending(metric => metric.FalsePositive)
            .ThenBy(metric => metric.BreakdownType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(metric => metric.Key, StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .Select(CloneAsContributor("FalseCallSource"))
            .ToList();

        summary.TopPossibleEscapeContributors = summary.DefectClassMetrics
            .Concat(summary.SideMetrics)
            .Concat(summary.RoiMetrics)
            .Where(metric => metric.FalseNegative > 0)
            .OrderByDescending(metric => metric.FalseNegative)
            .ThenBy(metric => metric.BreakdownType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(metric => metric.Key, StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .Select(CloneAsContributor("PossibleEscapeSource"))
            .ToList();

        return summary;
    }

    public static IReadOnlyList<ValidationBreakdownMetric> Flatten(ValidationBreakdownSummary summary)
        => summary.DefectClassMetrics
            .Concat(summary.SideMetrics)
            .Concat(summary.RoiMetrics)
            .Concat(summary.RoiTypeMetrics)
            .Concat(summary.TopFalseCallContributors)
            .Concat(summary.TopPossibleEscapeContributors)
            .ToArray();

    public static string BuildCsv(ValidationBreakdownSummary summary)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Breakdown Type,Key,Display Name,Total,TP,TN,FP,FN,Wrong Defect Class,Wrong Side,Unknown GT,Precision,Recall,False Call Rate");
        foreach (var metric in Flatten(summary))
        {
            sb.AppendLine(string.Join(",",
                Csv(metric.BreakdownType),
                Csv(metric.Key),
                Csv(metric.DisplayName),
                metric.Total.ToString(CultureInfo.InvariantCulture),
                metric.TruePositive.ToString(CultureInfo.InvariantCulture),
                metric.TrueNegative.ToString(CultureInfo.InvariantCulture),
                metric.FalsePositive.ToString(CultureInfo.InvariantCulture),
                metric.FalseNegative.ToString(CultureInfo.InvariantCulture),
                metric.WrongDefectClass.ToString(CultureInfo.InvariantCulture),
                metric.WrongSide.ToString(CultureInfo.InvariantCulture),
                metric.UnknownGroundTruth.ToString(CultureInfo.InvariantCulture),
                metric.Precision.ToString("F4", CultureInfo.InvariantCulture),
                metric.Recall.ToString("F4", CultureInfo.InvariantCulture),
                metric.FalseCallRate.ToString("F4", CultureInfo.InvariantCulture)));
        }

        return sb.ToString();
    }

    private static IEnumerable<ValidationBreakdownMetric> BuildGroupMetrics(
        IReadOnlyCollection<BatchTestRow> rows,
        string breakdownType,
        Func<BatchTestRow, string> keySelector)
    {
        return rows
            .GroupBy(row => NormalizeKey(keySelector(row)), StringComparer.OrdinalIgnoreCase)
            .Select(group => BuildMetric(breakdownType, group.Key, group.ToArray()))
            .OrderByDescending(metric => metric.FalsePositive + metric.FalseNegative + metric.WrongDefectClass + metric.WrongSide)
            .ThenBy(metric => metric.Key, StringComparer.OrdinalIgnoreCase);
    }

    private static string DefectClassKey(BatchTestRow row)
        => string.Equals(row.FailureCategory, "FALSE_CALL", StringComparison.OrdinalIgnoreCase)
            ? BatchValidationService.NormalizeDefectClass(row.DefectType)
            : row.NormalizedDefectClass;

    private static ValidationBreakdownMetric BuildMetric(string breakdownType, string key, IReadOnlyCollection<BatchTestRow> rows)
    {
        var known = rows.Where(row => BatchValidationService.NormalizeBinaryLabel(row.GroundTruth) != "UNKNOWN").ToArray();
        var tp = known.Count(row => BatchValidationService.NormalizeBinaryLabel(row.GroundTruth) == "NG" && BatchValidationService.NormalizeBinaryLabel(row.EngineResult) == "NG");
        var tn = known.Count(row => BatchValidationService.NormalizeBinaryLabel(row.GroundTruth) == "OK" && BatchValidationService.NormalizeBinaryLabel(row.EngineResult) == "OK");
        var fp = known.Count(row => string.Equals(row.FailureCategory, "FALSE_CALL", StringComparison.OrdinalIgnoreCase));
        var fn = known.Count(row => string.Equals(row.FailureCategory, "POSSIBLE_ESCAPE", StringComparison.OrdinalIgnoreCase));

        return new ValidationBreakdownMetric
        {
            BreakdownType = breakdownType,
            Key = key,
            DisplayName = key,
            Total = rows.Count,
            TruePositive = tp,
            TrueNegative = tn,
            FalsePositive = fp,
            FalseNegative = fn,
            WrongDefectClass = rows.Count(row => string.Equals(row.FailureCategory, "WRONG_DEFECT_CLASS", StringComparison.OrdinalIgnoreCase)),
            WrongSide = rows.Count(row => string.Equals(row.FailureCategory, "WRONG_SIDE", StringComparison.OrdinalIgnoreCase)),
            UnknownGroundTruth = rows.Count(row => string.Equals(row.FailureCategory, "UNKNOWN_GT", StringComparison.OrdinalIgnoreCase)),
            Precision = Divide(tp, tp + fp),
            Recall = Divide(tp, tp + fn),
            FalseCallRate = Divide(fp, fp + tn),
        };
    }

    private static Func<ValidationBreakdownMetric, ValidationBreakdownMetric> CloneAsContributor(string breakdownType)
        => source => new ValidationBreakdownMetric
        {
            BreakdownType = breakdownType,
            Key = $"{source.BreakdownType}:{source.Key}",
            DisplayName = $"{source.BreakdownType} / {source.DisplayName}",
            Total = source.Total,
            TruePositive = source.TruePositive,
            TrueNegative = source.TrueNegative,
            FalsePositive = source.FalsePositive,
            FalseNegative = source.FalseNegative,
            WrongDefectClass = source.WrongDefectClass,
            WrongSide = source.WrongSide,
            UnknownGroundTruth = source.UnknownGroundTruth,
            Precision = source.Precision,
            Recall = source.Recall,
            FalseCallRate = source.FalseCallRate,
        };

    private static string NormalizeKey(string? value)
        => string.IsNullOrWhiteSpace(value) ? "UNASSIGNED" : value.Trim();

    private static double Divide(int numerator, int denominator)
        => denominator <= 0 ? 0 : numerator / (double)denominator;

    private static string Csv(string value)
        => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
