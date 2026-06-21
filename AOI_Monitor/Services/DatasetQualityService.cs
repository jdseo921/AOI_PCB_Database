using System.IO;
using System.Security.Cryptography;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public static class DatasetQualityService
{
    public static DatasetQualitySummary Analyze(
        IReadOnlyCollection<BatchTestRow> rows,
        ValidationManifest? manifest = null,
        ValidationDatasetQualityCriteria? criteria = null)
    {
        criteria ??= new ValidationDatasetQualityCriteria();
        var rowArray = rows.ToArray();
        var summary = new DatasetQualitySummary
        {
            TotalImages = rowArray.Length,
            OkImages = rowArray.Count(row => BatchValidationService.NormalizeBinaryLabel(row.GroundTruth) == "OK"),
            NgImages = rowArray.Count(row => BatchValidationService.NormalizeBinaryLabel(row.GroundTruth) == "NG"),
            UnknownLabelImages = rowArray.Count(row => BatchValidationService.NormalizeBinaryLabel(row.GroundTruth) == "UNKNOWN"),
        };
        summary.KnownGroundTruthImages = summary.OkImages + summary.NgImages;
        summary.UnknownLabelRate = summary.TotalImages == 0 ? 0 : summary.UnknownLabelImages / (double)summary.TotalImages;

        var missingGolden = CountMissingGolden(rowArray, manifest, criteria.RequireGoldenImageForPixelDifference);
        summary.MissingGoldenImages = missingGolden;
        summary.MissingGoldenRate = summary.TotalImages == 0 ? 0 : missingGolden / (double)summary.TotalImages;

        summary.ImagesPerDefectClass = rowArray
            .Where(row => BatchValidationService.NormalizeBinaryLabel(row.GroundTruth) == "NG")
            .GroupBy(row => BatchValidationService.NormalizeDefectClass(row.NormalizedDefectClass == "UNASSIGNED" ? row.DefectType : row.NormalizedDefectClass), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Key != "UNASSIGNED" && group.Key != "OK")
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        summary.DefectClassCount = summary.ImagesPerDefectClass.Count;

        summary.DuplicateImageNames = rowArray
            .GroupBy(row => string.IsNullOrWhiteSpace(row.Image) ? Path.GetFileName(row.ImagePath) : row.Image, StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key) && group.Count() > 1)
            .Sum(group => group.Count());
        summary.DuplicateFileHashes = CountDuplicateHashes(rowArray);

        AddBlockingChecks(summary, criteria);
        AddWarnings(summary, criteria);
        summary.Status = summary.BlockingFailures.Count > 0
            ? "FAIL"
            : summary.Warnings.Count > 0
                ? "CONDITIONAL"
                : "PASS";
        return summary;
    }

    private static void AddBlockingChecks(DatasetQualitySummary summary, ValidationDatasetQualityCriteria criteria)
    {
        if (summary.TotalImages < criteria.MinimumTotalImages)
            summary.BlockingFailures.Add($"Dataset has {summary.TotalImages} image(s); minimum required is {criteria.MinimumTotalImages}.");
        if (summary.KnownGroundTruthImages < criteria.MinimumKnownGroundTruthImages)
            summary.BlockingFailures.Add($"Dataset has {summary.KnownGroundTruthImages} known ground-truth image(s); minimum required is {criteria.MinimumKnownGroundTruthImages}.");
        if (summary.OkImages < criteria.MinimumOkImages)
            summary.BlockingFailures.Add($"Dataset has {summary.OkImages} OK image(s); minimum required is {criteria.MinimumOkImages}.");
        if (summary.NgImages < criteria.MinimumNgImages)
            summary.BlockingFailures.Add($"Dataset has {summary.NgImages} NG image(s); minimum required is {criteria.MinimumNgImages}.");
        if (summary.DefectClassCount < criteria.MinimumDefectClasses)
            summary.BlockingFailures.Add($"Dataset has {summary.DefectClassCount} defect class(es); minimum required is {criteria.MinimumDefectClasses}.");
        if (summary.UnknownLabelRate > criteria.MaximumUnknownLabelRate)
            summary.BlockingFailures.Add($"Unknown label rate {summary.UnknownLabelRate:P1} exceeds maximum {criteria.MaximumUnknownLabelRate:P1}.");
        if (criteria.RequireGoldenImageForPixelDifference && summary.MissingGoldenRate > criteria.MaximumMissingGoldenRate)
            summary.BlockingFailures.Add($"Missing golden-image rate {summary.MissingGoldenRate:P1} exceeds maximum {criteria.MaximumMissingGoldenRate:P1}.");

        foreach (var (defectClass, count) in summary.ImagesPerDefectClass.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (count < criteria.MinimumImagesPerDefectClass)
                summary.BlockingFailures.Add($"Defect class {defectClass} has {count} image(s); minimum required is {criteria.MinimumImagesPerDefectClass}.");
        }
    }

    private static void AddWarnings(DatasetQualitySummary summary, ValidationDatasetQualityCriteria criteria)
    {
        if (summary.DuplicateImageNames > 0)
            summary.Warnings.Add($"Dataset contains {summary.DuplicateImageNames} row(s) with duplicate image names.");
        if (summary.DuplicateFileHashes > 0)
            summary.Warnings.Add($"Dataset contains {summary.DuplicateFileHashes} row(s) with duplicate file hashes.");
        if (summary.UnknownLabelImages > 0 && summary.UnknownLabelRate <= criteria.MaximumUnknownLabelRate)
            summary.Warnings.Add($"Dataset contains {summary.UnknownLabelImages} unknown-label image(s); these rows cannot support acceptance metrics.");
        if (summary.MissingGoldenImages > 0 && summary.MissingGoldenRate <= criteria.MaximumMissingGoldenRate)
            summary.Warnings.Add($"Dataset contains {summary.MissingGoldenImages} image(s) without a golden reference.");
    }

    private static int CountMissingGolden(
        IReadOnlyCollection<BatchTestRow> rows,
        ValidationManifest? manifest,
        bool requireGolden)
    {
        if (!requireGolden || rows.Count == 0)
            return 0;

        if (manifest is not null && manifest.OrderedEntries.Count > 0)
            return manifest.OrderedEntries.Count(entry => string.IsNullOrWhiteSpace(entry.GoldenPath) || !File.Exists(entry.GoldenPath));

        return rows.Count(row => string.IsNullOrWhiteSpace(row.GoldenImagePath) || !File.Exists(row.GoldenImagePath));
    }

    private static int CountDuplicateHashes(IReadOnlyCollection<BatchTestRow> rows)
    {
        var hashes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.ImagePath) || !File.Exists(row.ImagePath))
                continue;

            try
            {
                using var stream = File.OpenRead(row.ImagePath);
                var hash = Convert.ToHexString(SHA256.HashData(stream));
                hashes[hash] = hashes.TryGetValue(hash, out var count) ? count + 1 : 1;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return hashes.Values.Where(count => count > 1).Sum();
    }
}
