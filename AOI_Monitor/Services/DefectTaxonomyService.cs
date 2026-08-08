using System.Globalization;
using System.IO;
using System.Text;
using AOI_Monitor.Data;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public static class DefectTaxonomyService
{
    public const string DefaultTaxonomyId = "default-aoi-defect-taxonomy";

    public static DefectTaxonomySnapshot GetActiveTaxonomy()
    {
        var snapshot = AoiDatabase.GetActiveDefectTaxonomy();
        if (snapshot is not null && snapshot.Entries.Count > 0)
            return snapshot;

        var seeded = CreateDefaultTaxonomy();
        AoiDatabase.SaveDefectTaxonomySnapshot(seeded, "SYSTEM");
        return AoiDatabase.GetActiveDefectTaxonomy() ?? seeded;
    }

    public static DefectTaxonomyNormalization Normalize(string? label)
    {
        var input = string.IsNullOrWhiteSpace(label) ? "UNASSIGNED" : label.Trim();
        if (string.IsNullOrWhiteSpace(label))
            return new DefectTaxonomyNormalization(input, "UNASSIGNED", "UNASSIGNED", string.Empty, true, string.Empty);

        var snapshot = GetActiveTaxonomy();
        var key = Key(input);
        var entry = snapshot.Entries.FirstOrDefault(item => item.IsActive && Key(item.CanonicalClass) == key);
        if (entry is null)
        {
            var alias = snapshot.Aliases.FirstOrDefault(item => Key(item.Alias) == key);
            if (alias is not null)
                entry = snapshot.Entries.FirstOrDefault(item => item.IsActive && Key(item.CanonicalClass) == Key(alias.CanonicalClass));
        }

        if (entry is null)
        {
            var legacy = LegacyKey(input);
            return new DefectTaxonomyNormalization(
                input,
                legacy,
                input,
                string.Empty,
                false,
                $"Unknown defect label '{input}' is not mapped in the active defect taxonomy.");
        }

        var mes = snapshot.MesMappings.FirstOrDefault(item => Key(item.CanonicalClass) == Key(entry.CanonicalClass))?.MesCode ?? string.Empty;
        var customer = string.IsNullOrWhiteSpace(entry.CustomerLabel) ? entry.CanonicalClass : entry.CustomerLabel;
        return new DefectTaxonomyNormalization(input, entry.CanonicalClass, customer, mes, true, string.Empty)
        {
            Severity = ResolveSeverity(entry),
            DetectionMethod = ResolveDetectionMethod(entry),
        };
    }

    /// <summary>
    /// Customer classification-table Severity for a label, resolved through the active taxonomy.
    /// Returns an empty string when the label is unknown or the class carries no severity.
    /// </summary>
    public static string SeverityFor(string? label)
        => Normalize(label).Severity;

    /// <summary>
    /// Customer classification-table Detection Method for a label, resolved through the active
    /// taxonomy. Returns an empty string when the label is unknown or unclassified.
    /// </summary>
    public static string DetectionMethodFor(string? label)
        => Normalize(label).DetectionMethod;

    // Persisted values win; the shipped catalogue is the fallback so taxonomies saved before
    // severity/detection-method existed still resolve without a destructive rewrite.
    private static string ResolveSeverity(DefectTaxonomyEntry entry)
        => string.IsNullOrWhiteSpace(entry.Severity)
            ? DefectClassCatalog.SeverityFor(entry.CanonicalClass)
            : entry.Severity.Trim();

    private static string ResolveDetectionMethod(DefectTaxonomyEntry entry)
        => string.IsNullOrWhiteSpace(entry.DetectionMethod)
            ? DefectClassCatalog.DetectionMethodFor(entry.CanonicalClass)
            : entry.DetectionMethod.Trim();

    public static string NormalizeForBatch(string? label)
    {
        if (!string.IsNullOrWhiteSpace(label))
        {
            var legacyInput = LegacyKey(label);
            if (string.Equals(label.Trim(), legacyInput, StringComparison.OrdinalIgnoreCase))
                return legacyInput;
        }

        var result = Normalize(label);
        return result.IsKnown ? LegacyKey(result.CanonicalClass) : result.CanonicalClass;
    }

    public static string CustomerLabelFor(string? label)
        => Normalize(label).CustomerLabel;

    public static string MesCodeFor(string? label)
    {
        var normalized = Normalize(label);
        return string.IsNullOrWhiteSpace(normalized.MesCode) ? LegacyKey(normalized.CanonicalClass) : normalized.MesCode;
    }

    public static IReadOnlyList<string> ActiveCanonicalClasses(bool includeOk = false)
        => GetActiveTaxonomy().Entries
            .Where(entry => entry.IsActive && (includeOk || !string.Equals(entry.CanonicalClass, "OK", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(entry => entry.SortOrder)
            .Select(entry => entry.CanonicalClass)
            .ToArray();

    /// <summary>
    /// Active classes this product can actually inspect for at some roadmap stage. Classes that
    /// belong to another machine type (SPI/X-ray/ICT) are catalogued for labelling and reporting
    /// but are excluded here, so operator-facing pickers never imply an inspection capability
    /// this software does not have.
    /// </summary>
    public static IReadOnlyList<string> InspectableCanonicalClasses(bool includeOk = false)
        => ActiveCanonicalClasses(includeOk)
            .Where(DefectDetectionCapability.IsInspectableByThisProduct)
            .ToArray();

    public static ModelLabelTaxonomyValidation ValidateModelLabels(IReadOnlyDictionary<int, string> labelMap)
        => ValidateModelLabels(labelMap.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToArray());

    public static ModelLabelTaxonomyValidation ValidateModelLabels(IReadOnlyList<string> labels)
    {
        var result = new ModelLabelTaxonomyValidation();
        if (labels.Count == 0)
        {
            result.Status = "FAIL";
            result.Messages.Add("FAIL: Model label map is empty and cannot be checked against the active defect taxonomy.");
            return result;
        }

        var mapped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var label in labels)
        {
            var normalized = Normalize(label);
            if (normalized.IsKnown)
            {
                mapped.Add(normalized.CanonicalClass);
            }
            else
            {
                result.UnknownLabels.Add(label);
                result.Messages.Add($"CONDITIONAL: Unknown model label '{label}' is not mapped in the active defect taxonomy.");
            }
        }

        // Truthfulness gate: a label map may only claim classes this product can actually
        // inspect for. Classes needing hardware this station does not have (3D, side view) or
        // belonging to another machine type (SPI/X-ray/ICT) are called out explicitly so a model
        // can never silently imply a capability the software does not deliver.
        foreach (var claimed in mapped.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            var capability = DefectDetectionCapability.Find(claimed);
            if (capability is null || capability.Tier is InspectionCapabilityTier.Anomaly2D or InspectionCapabilityTier.RequiresTrainedClassifier)
                continue;

            result.HardwareDependentClasses.Add(claimed);
            result.Messages.Add(
                $"CONDITIONAL: Model label map claims '{claimed}', which {DefectDetectionCapability.RequirementSummary(claimed)}.");
        }

        var required = GetActiveTaxonomy().Entries
            .Where(entry => entry.IsActive && entry.IsRequired && !string.Equals(entry.CanonicalClass, "OK", StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.CanonicalClass)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var requiredClass in required)
        {
            if (!mapped.Contains(requiredClass))
            {
                result.MissingRequiredClasses.Add(requiredClass);
                result.Messages.Add($"CONDITIONAL: Model label map is missing required defect class '{requiredClass}'.");
            }
        }

        if (result.Messages.Any(message => message.StartsWith("FAIL:", StringComparison.OrdinalIgnoreCase)))
            result.Status = "FAIL";
        else if (result.Messages.Count > 0)
            result.Status = "CONDITIONAL";
        else
            result.Messages.Add("PASS: Model label map covers the active defect taxonomy.");

        return result;
    }

    public static string ExportCsv(string path)
    {
        var snapshot = GetActiveTaxonomy();
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? AoiDatabase.StorageRoot);
        var sb = new StringBuilder();
        sb.AppendLine("canonical_class,customer_label,model_label_id,mes_code,is_required,severity,detection_method,detection_capability,aliases");
        foreach (var entry in snapshot.Entries.OrderBy(item => item.SortOrder))
        {
            var aliases = snapshot.Aliases
                .Where(alias => string.Equals(alias.CanonicalClass, entry.CanonicalClass, StringComparison.OrdinalIgnoreCase))
                .Select(alias => alias.Alias);
            var mes = snapshot.MesMappings.FirstOrDefault(mapping => string.Equals(mapping.CanonicalClass, entry.CanonicalClass, StringComparison.OrdinalIgnoreCase))?.MesCode ?? string.Empty;
            sb.AppendLine(string.Join(",",
                Csv(entry.CanonicalClass),
                Csv(string.IsNullOrWhiteSpace(entry.CustomerLabel) ? entry.CanonicalClass : entry.CustomerLabel),
                Csv(entry.ModelLabelId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
                Csv(mes),
                Csv(entry.IsRequired ? "true" : "false"),
                Csv(ResolveSeverity(entry)),
                Csv(ResolveDetectionMethod(entry)),
                // Honest statement of what THIS software needs to detect the class; exported so a
                // customer reading the taxonomy never mistakes a labelling class for a capability.
                Csv(DefectDetectionCapability.RequirementSummary(entry.CanonicalClass)),
                Csv(string.Join("|", aliases))));
        }

        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        ExportVerificationService.RecordVerifiedExport("DefectTaxonomyCsv", path);
        return path;
    }

    public static DefectTaxonomySnapshot ImportCsv(string path, UserRole role, string operatorWithRole)
    {
        if (!RoleAuthorization.CanChangeThresholds(role))
            throw new UnauthorizedAccessException(RoleAuthorization.DeniedMessage(role, "importing defect taxonomy CSV"));
        if (!File.Exists(path))
            throw new FileNotFoundException("Defect taxonomy CSV was not found.", path);

        var lines = File.ReadAllLines(path).Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
        if (lines.Length < 2)
            throw new InvalidDataException("Defect taxonomy CSV must include a header and at least one entry.");

        var snapshot = new DefectTaxonomySnapshot
        {
            Taxonomy = new DefectTaxonomyRecord
            {
                TaxonomyId = $"taxonomy-{DateTime.UtcNow:yyyyMMddHHmmss}",
                Name = Path.GetFileNameWithoutExtension(path),
                CustomerName = Path.GetFileNameWithoutExtension(path),
                IsActive = true,
            },
        };

        var headers = SplitCsv(lines[0]).Select(header => header.Trim().ToLowerInvariant()).ToArray();
        var canonicalIndex = Array.IndexOf(headers, "canonical_class");
        if (canonicalIndex < 0)
            throw new InvalidDataException("Defect taxonomy CSV must include canonical_class.");

        var customerIndex = Array.IndexOf(headers, "customer_label");
        var labelIdIndex = Array.IndexOf(headers, "model_label_id");
        var mesIndex = Array.IndexOf(headers, "mes_code");
        var requiredIndex = Array.IndexOf(headers, "is_required");
        var severityIndex = Array.IndexOf(headers, "severity");
        var detectionMethodIndex = Array.IndexOf(headers, "detection_method");
        var aliasesIndex = Array.IndexOf(headers, "aliases");

        for (var i = 1; i < lines.Length; i++)
        {
            var cells = SplitCsv(lines[i]);
            if (cells.Count <= canonicalIndex || string.IsNullOrWhiteSpace(cells[canonicalIndex]))
                continue;

            var canonical = cells[canonicalIndex].Trim();
            var severity = Cell(cells, severityIndex, string.Empty);
            if (!string.IsNullOrWhiteSpace(severity) && !DefectSeverityLevels.IsKnown(severity))
            {
                throw new InvalidDataException(
                    $"Defect taxonomy CSV row {i + 1} ('{canonical}') has severity '{severity}'. " +
                    $"Allowed values are {string.Join(", ", DefectSeverityLevels.All)}.");
            }

            var entry = new DefectTaxonomyEntry
            {
                TaxonomyId = snapshot.Taxonomy.TaxonomyId,
                CanonicalClass = canonical,
                CustomerLabel = Cell(cells, customerIndex, canonical),
                ModelLabelId = int.TryParse(Cell(cells, labelIdIndex, string.Empty), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : null,
                IsRequired = !bool.TryParse(Cell(cells, requiredIndex, "true"), out var required) || required,
                Severity = severity,
                DetectionMethod = Cell(cells, detectionMethodIndex, string.Empty),
                SortOrder = snapshot.Entries.Count,
                IsActive = true,
            };
            snapshot.Entries.Add(entry);
            var mes = Cell(cells, mesIndex, string.Empty);
            if (!string.IsNullOrWhiteSpace(mes))
                snapshot.MesMappings.Add(new MesDefectCodeMappingRecord { TaxonomyId = snapshot.Taxonomy.TaxonomyId, CanonicalClass = canonical, MesCode = mes.Trim() });
            foreach (var alias in Cell(cells, aliasesIndex, string.Empty).Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                snapshot.Aliases.Add(new DefectClassAliasRecord { TaxonomyId = snapshot.Taxonomy.TaxonomyId, Alias = alias, CanonicalClass = canonical });
        }

        AoiDatabase.SaveDefectTaxonomySnapshot(snapshot, operatorWithRole);
        return snapshot;
    }

    /// <summary>
    /// The shipped default taxonomy: every row of the customer defect classification table
    /// (<c>Docs/customer-specs/PCBA_Defect_Classification_Table.md</c>) as a first-class entry,
    /// carrying the spec's Severity and Detection Method columns.
    ///
    /// Cataloguing a class is a labelling/reporting capability, not a detection claim — what
    /// this software can actually inspect for is stated separately and conservatively in
    /// <see cref="DefectDetectionCapability"/>.
    /// </summary>
    public static DefectTaxonomySnapshot CreateDefaultTaxonomy()
    {
        var snapshot = new DefectTaxonomySnapshot
        {
            Taxonomy = new DefectTaxonomyRecord
            {
                TaxonomyId = DefaultTaxonomyId,
                Name = "Default AOI Defect Taxonomy",
                CustomerName = "Default",
                IsActive = true,
            },
        };

        for (var i = 0; i < DefectClassCatalog.Default.Count; i++)
        {
            var definition = DefectClassCatalog.Default[i];
            snapshot.Entries.Add(new DefectTaxonomyEntry
            {
                TaxonomyId = DefaultTaxonomyId,
                CanonicalClass = definition.CanonicalClass,
                CustomerLabel = definition.CanonicalClass,
                ModelLabelId = definition.ModelLabelId,
                IsRequired = definition.IsRequired,
                Severity = definition.Severity,
                DetectionMethod = definition.DetectionMethod,
                SortOrder = i,
                IsActive = true,
            });
            snapshot.MesMappings.Add(new MesDefectCodeMappingRecord
            {
                TaxonomyId = DefaultTaxonomyId,
                CanonicalClass = definition.CanonicalClass,
                MesCode = definition.MesCode,
            });
            foreach (var alias in definition.Aliases.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                snapshot.Aliases.Add(new DefectClassAliasRecord { TaxonomyId = DefaultTaxonomyId, Alias = alias, CanonicalClass = definition.CanonicalClass });
        }

        return snapshot;
    }

    private static string LegacyKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "UNASSIGNED";

        var normalized = value.Trim().ToUpperInvariant()
            .Replace("-", "_", StringComparison.Ordinal)
            .Replace(" ", "_", StringComparison.Ordinal);
        return normalized switch
        {
            "OK" or "PASS" or "GOOD" => "OK",
            "NG" or "FAIL" or "FAILED" or "DEFECT" or "DEFECTIVE" or "BAD" => "DEFECT",
            "UNKNOWN" or "N/A" or "NA" => "UNASSIGNED",
            _ => normalized,
        };
    }

    private static string Key(string value)
        => LegacyKey(value).Replace("_", string.Empty, StringComparison.Ordinal);

    private static string Csv(string value)
        => $"\"{(value ?? string.Empty).Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static string Cell(IReadOnlyList<string> cells, int index, string fallback)
        => index >= 0 && cells.Count > index ? cells[index].Trim() : fallback;

    private static List<string> SplitCsv(string line)
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
}
