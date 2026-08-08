namespace AOI_Monitor.Models;

/// <summary>
/// Canonical, allowed values for the customer specification's Severity column
/// (<c>Docs/customer-specs/PCBA_Defect_Classification_Table.md</c> §3).
/// </summary>
public static class DefectSeverityLevels
{
    public const string Critical = "Critical";
    public const string Major = "Major";
    public const string Minor = "Minor";

    /// <summary>Not a defect class (the OK class); carried so every entry has a value.</summary>
    public const string Informational = "Informational";

    public static IReadOnlyList<string> All { get; } = new[] { Critical, Major, Minor, Informational };

    public static bool IsKnown(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && All.Any(level => string.Equals(level, value.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Ranking used to sort/aggregate by seriousness (lower = more serious).</summary>
    public static int Rank(string? value) => (value ?? string.Empty).Trim().ToUpperInvariant() switch
    {
        "CRITICAL" => 0,
        "MAJOR" => 1,
        "MINOR" => 2,
        _ => 3,
    };
}

/// <summary>
/// One row of the customer defect classification table, plus the local identifiers the
/// application needs to carry it (model label id, MES code, aliases).
/// </summary>
/// <param name="CanonicalClass">Canonical class name used everywhere in the app.</param>
/// <param name="ModelLabelId">Stable model label id. Ids 0-13 are frozen so existing label maps keep working.</param>
/// <param name="MesCode">Short code emitted on MES/traceability payloads.</param>
/// <param name="IsRequired">True for the spec §4 Mandatory AOI Defect Set (plus two long-standing local additions).</param>
/// <param name="Severity">Spec §3 Severity column verbatim.</param>
/// <param name="DetectionMethod">Spec §3 Detection Method column verbatim.</param>
/// <param name="SpecReference">Spec section the row comes from, or "local" for non-spec additions.</param>
/// <param name="Aliases">Pipe-separated alternative labels that normalize onto this class.</param>
public sealed record DefectClassDefinition(
    string CanonicalClass,
    int ModelLabelId,
    string MesCode,
    bool IsRequired,
    string Severity,
    string DetectionMethod,
    string SpecReference,
    string Aliases);

/// <summary>
/// The shipped default defect classification catalogue, transcribed from the customer
/// specification (<c>Docs/customer-specs/PCBA_Defect_Classification_Table.md</c>).
///
/// Every one of the specification's 33 classification-table rows is represented, so the table
/// can be used for labelling, reporting, and MES coding. **Presence in this catalogue is not a
/// detection claim.** What this software can actually inspect for each class is a separate,
/// deliberately conservative statement held in
/// <c>AOI_Monitor/Services/DefectDetectionCapability.cs</c>; classes belonging to other machine
/// types (SPI, X-ray, ICT) are catalogued for labelling only and are marked out of product scope
/// there.
///
/// Model label ids 0-13 are frozen: existing customer label maps and persisted results depend on
/// them. New classes append from 14 upwards and must never renumber an existing id.
/// </summary>
public static class DefectClassCatalog
{
    /// <summary>Spec §4 Mandatory AOI Defect Set — must exist in every AOI recipe.</summary>
    public static IReadOnlyList<string> MandatoryAoiDefectSet { get; } = new[]
    {
        "Missing Component",
        "Misalignment",
        "Polarity Error",
        "Solder Bridge",
        "Tombstone",
        "Cold Joint",
        "Shield Can Gap",
        "Connector Pin Height",
        "3D Coplanarity",
        "Solder Volume",
    };

    /// <summary>
    /// The full default catalogue in display order: spec §3.1 → §3.6, then the local additions.
    /// </summary>
    public static IReadOnlyList<DefectClassDefinition> Default { get; } = new DefectClassDefinition[]
    {
        // Not a defect: the pass class. Kept first so label id 0 stays OK.
        new("OK", 0, "OK", false, DefectSeverityLevels.Informational, "n/a", "local", "PASS|GOOD"),

        // §3.1 Solder-related defects
        new("Solder Bridge", 1, "SB", true, DefectSeverityLevels.Critical, "AOI / Visual", "3.1",
            "Bridge|SolderBridge|Short|Short Circuit|Solder Short"),
        new("Insufficient Solder", 2, "IS", true, DefectSeverityLevels.Major, "AOI / 3D", "3.1",
            "Insufficient|Low Solder|Open Solder"),
        new("Excess Solder", 14, "ES", false, DefectSeverityLevels.Major, "AOI", "3.1",
            "Excessive Solder|Too Much Solder"),
        new("Cold Joint", 9, "CJ", true, DefectSeverityLevels.Major, "Visual", "3.1",
            "Cold Solder|Cold Solder Joint|Dry Joint"),
        new("Poor Wetting", 15, "PW", false, DefectSeverityLevels.Major, "AOI", "3.1",
            "Non Wetting|Dewetting|Wetting Defect"),
        new("Solder Crack", 16, "SCK", false, DefectSeverityLevels.Major, "Visual", "3.1",
            "Cracked Joint|Joint Crack"),
        new("Solder Ball", 17, "SBL", false, DefectSeverityLevels.Minor, "AOI", "3.1",
            "Solder Balls|Solder Splash|Solder Bead"),
        new("Fillet Shape Defect", 18, "FSD", false, DefectSeverityLevels.Minor, "AOI", "3.1",
            "Fillet Defect|Bad Fillet|Fillet Shape"),

        // §3.2 Component-related defects
        new("Missing Component", 5, "MISS", true, DefectSeverityLevels.Critical, "AOI", "3.2",
            "Missing|Missing Part|Component Missing"),
        new("Misalignment", 10, "MIS", true, DefectSeverityLevels.Major, "AOI", "3.2",
            "Misaligned|Shift|Offset|Placement Shift"),
        new("Tombstone", 4, "TOMB", true, DefectSeverityLevels.Major, "AOI", "3.2",
            "Tombstoned|Tombstoning|Manhattan"),
        new("Polarity Error", 3, "POL", true, DefectSeverityLevels.Critical, "AOI / Visual", "3.2",
            "Polarity|Reversed|Wrong Polarity"),
        new("Rotation Error", 19, "ROT", false, DefectSeverityLevels.Major, "AOI", "3.2",
            "Rotated|Rotation|Wrong Rotation"),
        new("Bent Lead", 20, "BL", false, DefectSeverityLevels.Major, "AOI / Visual", "3.2",
            "Lead Bent|Bent Leads|Lifted Lead"),
        new("Damaged Component", 21, "DMG", false, DefectSeverityLevels.Major, "Visual", "3.2",
            "Cracked Component|Chipped Component|Broken Component"),

        // §3.3 Solder paste printing defects (SPI / X-ray machine types — labelling classes only)
        new("Paste Misalignment", 22, "PMA", false, DefectSeverityLevels.Major, "SPI / AOI", "3.3",
            "Paste Offset|Print Offset"),
        new("Paste Insufficient", 23, "PIN", false, DefectSeverityLevels.Major, "SPI", "3.3",
            "Insufficient Paste|Low Paste"),
        new("Paste Excess", 24, "PEX", false, DefectSeverityLevels.Major, "SPI", "3.3",
            "Excess Paste|Too Much Paste"),
        new("Paste Slump", 25, "PSL", false, DefectSeverityLevels.Major, "SPI", "3.3",
            "Slump|Paste Bleed"),
        new("Paste Void", 26, "PVD", false, DefectSeverityLevels.Minor, "X-ray", "3.3",
            "Void|Paste Voiding"),

        // §3.4 PCB / pad / surface defects
        new("Pad Lift", 27, "PDL", false, DefectSeverityLevels.Critical, "Visual", "3.4",
            "Lifted Pad|Pad Lifted"),
        new("Contamination", 28, "CON", false, DefectSeverityLevels.Major, "AOI / Visual", "3.4",
            "Dust|Flux Residue|Foreign Material|FOD"),
        new("Scratch", 29, "SCR", false, DefectSeverityLevels.Minor, "Visual", "3.4",
            "Surface Scratch|Abrasion"),
        new("Silkscreen Error", 30, "SLK", false, DefectSeverityLevels.Minor, "Visual", "3.4",
            "Silkscreen|Marking Error|Missing Marking"),
        new("Copper Exposure", 31, "CUE", false, DefectSeverityLevels.Major, "Visual", "3.4",
            "Exposed Copper|Mask Defect|Solder Mask Defect"),

        // §3.5 Electrical / circuit defects
        // Spec row "Short Circuit" is deliberately folded into Solder Bridge for the
        // optically visible case (standard deviation SD-16) and normalizes via its alias.
        new("Open Circuit", 32, "OPC", false, DefectSeverityLevels.Critical, "ICT / AOI", "3.5",
            "Open|Broken Trace|Open Connection"),
        new("Trace Damage", 33, "TRD", false, DefectSeverityLevels.Major, "Visual", "3.5",
            "Damaged Trace|Scratched Trace|Broken Copper"),
        new("Via Defect", 34, "VIA", false, DefectSeverityLevels.Major, "X-ray", "3.5",
            "Bad Via|Via Void|Via Plating Defect"),

        // §3.6 Connector / mechanical defects
        new("Bent Pin", 35, "BPN", false, DefectSeverityLevels.Major, "AOI / Visual", "3.6",
            "Pin Bent|Deformed Pin|Bent Pins"),
        new("Connector Pin Height", 11, "CPH", true, DefectSeverityLevels.Major, "3D AOI", "3.6",
            "Pin Height Error|Pin Height"),
        new("Partial Insertion", 36, "PIS", false, DefectSeverityLevels.Critical, "AOI / Visual", "3.6",
            "Not Fully Seated|Unseated Connector|Incomplete Insertion"),
        new("Shield Can Gap", 13, "SCG", true, DefectSeverityLevels.Major, "Side-View AOI", "3.6",
            "Shield Gap|Can Gap|Shield Can"),

        // Local additions. §4 names "3D Coplanarity" and "Solder Volume" as mandatory although
        // neither appears as a §3 classification-table row, so their severity is assigned locally.
        new("Solder Volume", 8, "VOL", true, DefectSeverityLevels.Major, "3D AOI", "4",
            "Volume Error|Solder Volume Error"),
        new("3D Coplanarity", 12, "COP", true, DefectSeverityLevels.Major, "3D AOI", "4",
            "Coplanarity|Lead Coplanarity"),
        new("Height Error", 6, "HGT", true, DefectSeverityLevels.Major, "3D AOI", "local",
            "Height|Height Defect"),
        new("Anomaly", 7, "ANOM", false, DefectSeverityLevels.Major, "AOI", "local",
            "Unknown Defect|Other"),
    };

    public static DefectClassDefinition? Find(string? canonicalClass)
        => string.IsNullOrWhiteSpace(canonicalClass)
            ? null
            : Default.FirstOrDefault(entry => string.Equals(entry.CanonicalClass, canonicalClass.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Spec §3 severity for a canonical class, or empty when the class is not catalogued.</summary>
    public static string SeverityFor(string? canonicalClass)
        => Find(canonicalClass)?.Severity ?? string.Empty;

    /// <summary>Spec §3 detection method for a canonical class, or empty when not catalogued.</summary>
    public static string DetectionMethodFor(string? canonicalClass)
        => Find(canonicalClass)?.DetectionMethod ?? string.Empty;
}
