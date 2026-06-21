using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AOI_Monitor.Data;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public static class ExportVerificationService
{
    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    private static readonly byte[] PdfSignature = Encoding.ASCII.GetBytes("%PDF");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static ExportVerificationResult Verify(
        string exportPath,
        string exportType,
        long? exportHistoryId = null,
        bool persist = false)
    {
        var result = VerifyCore(exportPath, exportType);
        if (persist)
            AoiDatabase.RecordExportVerification(result, exportHistoryId);

        return result;
    }

    public static VerifiedExportRecord RecordVerifiedExport(
        string exportType,
        string exportPath,
        string creationStatus = "OK",
        string? operatorId = null)
    {
        var verification = Verify(exportPath, exportType);
        var status = MergeExportStatus(creationStatus, verification.Status);
        var exportHistoryId = AoiDatabase.RecordExport(exportType, exportPath, status, operatorId);
        AoiDatabase.RecordExportVerification(verification, exportHistoryId);
        return new VerifiedExportRecord(exportHistoryId, status, verification);
    }

    public static ExportVerificationReportPaths ExportReport(
        ExportVerificationResult result,
        string outputFolder)
    {
        Directory.CreateDirectory(outputFolder);
        var timestamp = result.CheckedAtUtc.ToLocalTime().ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var safeType = SafeFilePart(result.ExportType, "export");
        var jsonPath = Path.Combine(outputFolder, $"export_verification_{safeType}_{timestamp}.json");
        var textPath = Path.Combine(outputFolder, $"export_verification_{safeType}_{timestamp}.txt");

        File.WriteAllText(jsonPath, JsonSerializer.Serialize(result, JsonOptions), Encoding.UTF8);
        File.WriteAllText(textPath, BuildTextReport(result), Encoding.UTF8);

        return new ExportVerificationReportPaths(jsonPath, textPath);
    }

    public static string BuildTextReport(ExportVerificationResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("AOI Monitor Export Verification");
        sb.AppendLine($"Checked UTC: {result.CheckedAtUtc:O}");
        sb.AppendLine($"Export type: {result.ExportType}");
        sb.AppendLine($"Status: {result.Status}");
        sb.AppendLine($"Path: {result.ExportPath}");
        sb.AppendLine($"Size bytes: {result.SizeBytes}");
        sb.AppendLine($"SHA-256: {result.Sha256}");
        sb.AppendLine();
        sb.AppendLine("Messages:");
        foreach (var message in result.Messages.DefaultIfEmpty("No verification messages."))
            sb.AppendLine($"- {message}");

        if (result.ArtifactChecksums.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Artifact checksums:");
            foreach (var item in result.ArtifactChecksums.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine($"- {item.Key}: {item.Value}");
        }

        return sb.ToString();
    }

    public static ExportVerificationResult FromRecord(ExportVerificationRecord record)
    {
        return new ExportVerificationResult
        {
            ExportPath = record.ExportPath,
            ExportType = record.ExportType,
            Status = Enum.TryParse<ExportVerificationStatus>(record.Status, ignoreCase: true, out var status)
                ? status
                : ExportVerificationStatus.ERROR,
            Sha256 = record.Sha256,
            SizeBytes = record.SizeBytes,
            CheckedAtUtc = record.CheckedAtUtc,
            Messages = Deserialize<List<string>>(record.MessagesJson) ?? new List<string>(),
            ArtifactChecksums = Deserialize<Dictionary<string, string>>(record.ArtifactChecksumsJson)
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        };
    }

    private static ExportVerificationResult VerifyCore(string exportPath, string exportType)
    {
        var result = new ExportVerificationResult
        {
            ExportPath = exportPath,
            ExportType = exportType,
            Status = ExportVerificationStatus.OK,
            CheckedAtUtc = DateTime.UtcNow,
        };

        if (string.IsNullOrWhiteSpace(exportPath))
        {
            Add(result, ExportVerificationStatus.ERROR, "Export path is empty.");
            return Finish(result);
        }

        try
        {
            if (File.Exists(exportPath))
            {
                VerifyFile(result, exportPath, exportType);
            }
            else if (Directory.Exists(exportPath))
            {
                VerifyDirectory(result, exportPath, exportType);
            }
            else
            {
                Add(result, ExportVerificationStatus.ERROR, "Export path does not exist.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            Add(result, ExportVerificationStatus.ERROR, $"Verification failed: {ex.Message}");
        }

        return Finish(result);
    }

    private static void VerifyFile(ExportVerificationResult result, string path, string exportType)
    {
        var info = new FileInfo(path);
        result.SizeBytes = info.Length;
        result.Sha256 = ComputeSha256(path);
        result.ArtifactChecksums[Path.GetFileName(path)] = result.Sha256;

        Add(result, ExportVerificationStatus.OK, "File exists.");
        if (info.Length == 0)
        {
            Add(result, ExportVerificationStatus.ERROR, "File is empty.");
            return;
        }

        Add(result, ExportVerificationStatus.OK, $"File length is {info.Length} byte(s).");
        var extension = Path.GetExtension(path);
        if (extension.Equals(".csv", StringComparison.OrdinalIgnoreCase))
            VerifyCsv(result, path, exportType);
        else if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
            VerifyPng(result, path);
        else if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            VerifyPdf(result, path);
        else if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
            VerifyJson(result, path, exportType);
        else if (extension.Equals(".ndjson", StringComparison.OrdinalIgnoreCase))
            VerifyNdjson(result, path, exportType);
        else if (extension.Equals(".html", StringComparison.OrdinalIgnoreCase) ||
                 extension.Equals(".htm", StringComparison.OrdinalIgnoreCase) ||
                 extension.Equals(".txt", StringComparison.OrdinalIgnoreCase) ||
                 extension.Equals(".md", StringComparison.OrdinalIgnoreCase))
            Add(result, ExportVerificationStatus.OK, "Text artifact passed existence and checksum checks.");
        else
            Add(result, ExportVerificationStatus.WARN, $"No format-specific verifier is registered for '{extension}'.");
    }

    private static void VerifyDirectory(ExportVerificationResult result, string folder, string exportType)
    {
        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .OrderBy(path => Path.GetRelativePath(folder, path), StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (files.Length == 0)
        {
            Add(result, ExportVerificationStatus.ERROR, "Export folder exists but contains no files.");
            return;
        }

        foreach (var file in files)
        {
            var relative = NormalizeRelative(Path.GetRelativePath(folder, file));
            var hash = ComputeSha256(file);
            result.ArtifactChecksums[relative] = hash;
            result.SizeBytes += new FileInfo(file).Length;
        }

        result.Sha256 = ComputeAggregateSha256(result.ArtifactChecksums);
        Add(result, ExportVerificationStatus.OK, $"Folder contains {files.Length} file(s).");
        Add(result, ExportVerificationStatus.OK, $"Aggregate folder SHA-256 computed from {files.Length} artifact checksum(s).");

        if (IsPackageExport(exportType, folder))
            VerifyPackageManifest(result, folder, files);

        foreach (var png in files.Where(path => Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase)))
            VerifyPng(result, png, NormalizeRelative(Path.GetRelativePath(folder, png)));

        foreach (var pdf in files.Where(path => Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase)))
            VerifyPdf(result, pdf, NormalizeRelative(Path.GetRelativePath(folder, pdf)));

        foreach (var csv in files.Where(path => Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase)))
            VerifyCsv(result, csv, exportType);

        foreach (var json in files.Where(path => Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase)))
            VerifyJson(result, json, exportType);

        foreach (var ndjson in files.Where(path => Path.GetExtension(path).Equals(".ndjson", StringComparison.OrdinalIgnoreCase)))
            VerifyNdjson(result, ndjson, exportType);
    }

    private static void VerifyCsv(ExportVerificationResult result, string path, string exportType)
    {
        var firstLine = File.ReadLines(path).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(firstLine))
        {
            Add(result, ExportVerificationStatus.ERROR, $"CSV has no header: {Path.GetFileName(path)}.");
            return;
        }

        var headers = SplitCsvLine(firstLine)
            .Select(NormalizeHeader)
            .Where(header => !string.IsNullOrWhiteSpace(header))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var required = RequiredCsvColumns(exportType, path).Select(NormalizeHeader).ToArray();
        var missing = required.Where(column => !headers.Contains(column)).ToArray();
        if (missing.Length == 0)
            Add(result, ExportVerificationStatus.OK, $"CSV header verified: {Path.GetFileName(path)}.");
        else
            Add(result, ExportVerificationStatus.ERROR, $"CSV header missing required column(s) in {Path.GetFileName(path)}: {string.Join(", ", missing)}.");
    }

    private static void VerifyPng(ExportVerificationResult result, string path, string? displayPath = null)
    {
        using var stream = File.OpenRead(path);
        Span<byte> signature = stackalloc byte[PngSignature.Length];
        var read = stream.Read(signature);
        if (read == PngSignature.Length && signature.SequenceEqual(PngSignature))
            Add(result, ExportVerificationStatus.OK, $"PNG signature verified: {displayPath ?? Path.GetFileName(path)}.");
        else
            Add(result, ExportVerificationStatus.ERROR, $"PNG signature invalid: {displayPath ?? Path.GetFileName(path)}.");
    }

    private static void VerifyPdf(ExportVerificationResult result, string path, string? displayPath = null)
    {
        using var stream = File.OpenRead(path);
        Span<byte> signature = stackalloc byte[PdfSignature.Length];
        var read = stream.Read(signature);
        if (read == PdfSignature.Length && signature.SequenceEqual(PdfSignature))
            Add(result, ExportVerificationStatus.OK, $"PDF signature verified: {displayPath ?? Path.GetFileName(path)}.");
        else
            Add(result, ExportVerificationStatus.ERROR, $"PDF signature invalid: {displayPath ?? Path.GetFileName(path)}.");
    }

    private static void VerifyJson(ExportVerificationResult result, string path, string exportType)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (document.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
        {
            Add(result, ExportVerificationStatus.ERROR, $"JSON root must be object or array: {Path.GetFileName(path)}.");
            return;
        }

        if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            Add(result, ExportVerificationStatus.OK, $"JSON array parsed: {Path.GetFileName(path)}.");
            return;
        }

        var required = RequiredJsonFields(exportType, path);
        var missing = required
            .Where(field => !document.RootElement.TryGetProperty(field, out _))
            .ToArray();
        if (missing.Length == 0)
            Add(result, ExportVerificationStatus.OK, $"JSON parsed and required fields verified: {Path.GetFileName(path)}.");
        else
            Add(result, ExportVerificationStatus.ERROR, $"JSON missing required field(s) in {Path.GetFileName(path)}: {string.Join(", ", missing)}.");
    }

    private static void VerifyNdjson(ExportVerificationResult result, string path, string exportType)
    {
        var lineNumber = 0;
        var parsed = 0;
        var required = RequiredJsonFields(exportType, path);

        foreach (var line in File.ReadLines(path))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
                continue;

            using var document = JsonDocument.Parse(line);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                Add(result, ExportVerificationStatus.ERROR, $"NDJSON line {lineNumber} is not a JSON object: {Path.GetFileName(path)}.");
                continue;
            }

            var missing = required
                .Where(field => !document.RootElement.TryGetProperty(field, out _))
                .ToArray();
            if (missing.Length > 0)
                Add(result, ExportVerificationStatus.ERROR, $"NDJSON line {lineNumber} missing required field(s): {string.Join(", ", missing)}.");
            parsed++;
        }

        if (parsed == 0)
            Add(result, ExportVerificationStatus.ERROR, $"NDJSON file has no JSON records: {Path.GetFileName(path)}.");
        else
            Add(result, ExportVerificationStatus.OK, $"NDJSON parsed {parsed} record(s): {Path.GetFileName(path)}.");
    }

    private static void VerifyPackageManifest(ExportVerificationResult result, string folder, IReadOnlyCollection<string> actualFiles)
    {
        var manifestPath = Directory.EnumerateFiles(folder, "validation_manifest.json", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(folder, "package_manifest.json", SearchOption.AllDirectories))
            .OrderBy(path => Path.GetRelativePath(folder, path), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (manifestPath is null)
        {
            Add(result, ExportVerificationStatus.ERROR, "Customer package is missing validation_manifest.json or package_manifest.json.");
            return;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        if (!document.RootElement.TryGetProperty("includedFiles", out var includedFiles) ||
            includedFiles.ValueKind != JsonValueKind.Array)
        {
            Add(result, ExportVerificationStatus.ERROR, "Customer package manifest is missing includedFiles array.");
            return;
        }

        var manifestDir = Path.GetDirectoryName(manifestPath) ?? folder;
        var manifestRelativePath = NormalizeRelative(Path.GetRelativePath(manifestDir, manifestPath));
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in includedFiles.EnumerateArray())
        {
            if (item.TryGetProperty("relativePath", out var relative) &&
                relative.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(relative.GetString()))
            {
                expected.Add(NormalizeRelative(relative.GetString()!));
            }
        }

        var actual = actualFiles
            .Select(path => NormalizeRelative(Path.GetRelativePath(manifestDir, path)))
            .Where(relative => !relative.Equals(manifestRelativePath, StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        expected.Remove(manifestRelativePath);

        var missing = expected.Except(actual, StringComparer.OrdinalIgnoreCase).OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray();
        var extra = actual.Except(expected, StringComparer.OrdinalIgnoreCase).OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray();

        if (missing.Length == 0 && extra.Length == 0)
        {
            Add(result, ExportVerificationStatus.OK, "Customer package manifest file list matches actual files.");
            return;
        }

        if (missing.Length > 0)
            Add(result, ExportVerificationStatus.ERROR, $"Customer package manifest lists missing file(s): {string.Join(", ", missing.Take(8))}.");
        if (extra.Length > 0)
            Add(result, ExportVerificationStatus.ERROR, $"Customer package contains file(s) not listed in manifest: {string.Join(", ", extra.Take(8))}.");
    }

    private static IReadOnlyList<string> RequiredCsvColumns(string exportType, string path)
    {
        var fileName = Path.GetFileName(path);
        if (exportType.Contains("ReviewDispositionLog", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("review_disposition", StringComparison.OrdinalIgnoreCase))
            return new[] { "TimestampUtc", "StationId", "OperatorId", "Sample", "Golden", "Verdict", "Action" };

        if (exportType.Contains("Review", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("review", StringComparison.OrdinalIgnoreCase))
            return new[] { "Id", "TimestampUtc", "Category", "Operator", "Disposition", "Message" };

        if (exportType.Contains("Audit", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("audit", StringComparison.OrdinalIgnoreCase))
            return new[] { "Id", "TimestampUtc", "LocalTimestamp", "UserId", "UserRole", "StationId", "ActionCategory" };

        if (exportType.Contains("InspectionHistory", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("inspection_history", StringComparison.OrdinalIgnoreCase))
            return new[] { "Id", "TimestampUtc", "BoardProgram", "Operator", "Result", "Score" };

        if (exportType.Contains("ImageIndex", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("image_index", StringComparison.OrdinalIgnoreCase))
            return new[] { "Path", "Bytes", "LastWriteUtc" };

        return new[] { "Image", "Ground Truth", "AI/Engine Result", "Inspection Engine", "Score", "Pass/Fail" };
    }

    private static IReadOnlyList<string> RequiredJsonFields(string exportType, string path)
    {
        var fileName = Path.GetFileName(path);
        if (fileName.Equals("validation_manifest.json", StringComparison.OrdinalIgnoreCase))
            return new[] { "schemaVersion", "packageId", "generatedAtUtc", "acceptanceStatus", "includedFiles" };
        if (fileName.Equals("package_manifest.json", StringComparison.OrdinalIgnoreCase))
            return new[] { "schemaVersion", "packageId", "generatedAtUtc", "includedFiles" };

        if (fileName.Contains("disposition", StringComparison.OrdinalIgnoreCase))
            return new[] { "schemaVersion", "timestampUtc", "eventType", "action" };

        if (exportType.Contains("MachineInterface", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("decision", StringComparison.OrdinalIgnoreCase))
            return new[] { "schemaVersion", "inspectionId", "timestampUtc", "stationId", "verdict", "defects" };

        if (exportType.Contains("Mes", StringComparison.OrdinalIgnoreCase) ||
            exportType.Contains("Traceability", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("traceability", StringComparison.OrdinalIgnoreCase))
            return new[] { "integrationMode", "lotId", "boardModel", "result", "timestampUtc", "defectSummary" };

        return Array.Empty<string>();
    }

    private static bool IsPackageExport(string exportType, string folder)
        => exportType.Contains("Package", StringComparison.OrdinalIgnoreCase) ||
           Directory.EnumerateFiles(folder, "validation_manifest.json", SearchOption.AllDirectories).Any();

    private static ExportVerificationResult Finish(ExportVerificationResult result)
    {
        if (result.Messages.Count == 0)
            Add(result, ExportVerificationStatus.OK, "Verification completed.");

        if (string.IsNullOrWhiteSpace(result.Sha256) && result.ArtifactChecksums.Count > 0)
            result.Sha256 = ComputeAggregateSha256(result.ArtifactChecksums);

        return result;
    }

    private static void Add(ExportVerificationResult result, ExportVerificationStatus status, string message)
    {
        if (status > result.Status)
            result.Status = status;

        result.Messages.Add(message);
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string ComputeAggregateSha256(IReadOnlyDictionary<string, string> checksums)
    {
        var text = string.Join(
            "\n",
            checksums
                .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .Select(item => $"{item.Key}|{item.Value}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    private static List<string> SplitCsvLine(string line)
    {
        var cells = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                cells.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }

        cells.Add(sb.ToString());
        return cells;
    }

    private static string NormalizeHeader(string value)
        => value.Trim().Trim('\uFEFF').Replace(" ", "", StringComparison.Ordinal).Replace("-", "_", StringComparison.Ordinal);

    private static string NormalizeRelative(string path)
        => path.Replace('\\', '/').TrimStart('/');

    private static T? Deserialize<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json);
        }
        catch
        {
            return default;
        }
    }

    private static string SafeFilePart(string value, string fallback)
    {
        var text = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        foreach (var ch in Path.GetInvalidFileNameChars())
            text = text.Replace(ch, '_');
        return text.Replace(' ', '_');
    }

    private static string MergeExportStatus(string creationStatus, ExportVerificationStatus verificationStatus)
    {
        var created = ParseStatus(creationStatus);
        var merged = verificationStatus > created ? verificationStatus : created;
        return merged.ToString();
    }

    private static ExportVerificationStatus ParseStatus(string status)
    {
        if (Enum.TryParse<ExportVerificationStatus>(status, ignoreCase: true, out var parsed))
            return parsed;

        return status.ToUpperInvariant() switch
        {
            "PASS" => ExportVerificationStatus.OK,
            "CONDITIONAL" => ExportVerificationStatus.WARN,
            "FAIL" => ExportVerificationStatus.ERROR,
            _ => ExportVerificationStatus.WARN,
        };
    }
}

public sealed record ExportVerificationReportPaths(string JsonPath, string TextPath);

public sealed record VerifiedExportRecord(
    long ExportHistoryId,
    string ExportStatus,
    ExportVerificationResult Verification);
