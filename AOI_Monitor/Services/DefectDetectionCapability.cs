using System;
using System.Collections.Generic;
using System.Linq;

namespace AOI_Monitor.Services;

/// <summary>
/// What is actually needed to legitimately detect each defect class, so the app never
/// implies a capability it does not have. Real AOI vendors publish a per-defect capability
/// sheet (2D vs 3D, algorithm vs AI); this is the honest equivalent for this PoC.
///
/// The image-only engines (Pixel Difference, Learned Visual) detect *anomalies* — a region
/// that differs from the learned/golden normal. They do not classify solder-joint quality and
/// cannot measure height or volume. This catalog encodes that boundary and pairs each class
/// with an informational IPC-A-610 reference for factory/customer communication.
///
/// Every class in <see cref="AOI_Monitor.Models.DefectClassCatalog"/> has an entry here, so
/// cataloguing a class for labelling can never be mistaken for a detection claim.
/// </summary>
public enum InspectionCapabilityTier
{
    /// <summary>Gross presence/placement defect visible in 2D top images; an image-only
    /// anomaly engine can flag the region (final class label still benefits from a model).</summary>
    Anomaly2D,

    /// <summary>2D-visible, but reliably separating it from acceptable variation needs a
    /// trained classifier (e.g. ONNX). Anomaly-only engines must not claim this class.</summary>
    RequiresTrainedClassifier,

    /// <summary>Needs an angled/side-view acquisition path. A single top-down camera cannot
    /// see under a can edge or judge connector seating height, regardless of training.</summary>
    RequiresSideViewImaging,

    /// <summary>Height / volume / coplanarity — requires 3D acquisition hardware. No 2D image
    /// engine can measure it, regardless of training.</summary>
    RequiresThreeDHardware,

    /// <summary>Belongs to a different inspection machine type (SPI, X-ray, ICT) that is outside
    /// every stage of this product's roadmap. Catalogued so the class can be labelled, reported,
    /// and MES-coded — never detected by this software.</summary>
    OutOfProductScope,
}

public sealed record DefectCapability(
    string CanonicalClass,
    string IpcReference,
    InspectionCapabilityTier Tier,
    string Note);

public static class DefectDetectionCapability
{
    public static IReadOnlyList<DefectCapability> Catalog { get; } = new DefectCapability[]
    {
        // ---- Tier 1: gross 2D anomalies an image-only engine may legitimately flag ----
        new("Missing Component", "IPC-A-610 component placement", InspectionCapabilityTier.Anomaly2D,
            "Absent component is a gross 2D anomaly."),
        new("Tombstone", "IPC-A-610 component placement/orientation", InspectionCapabilityTier.Anomaly2D,
            "Lifted/standing chip is a strong 2D silhouette anomaly."),
        new("Misalignment", "IPC-A-610 component placement/offset", InspectionCapabilityTier.Anomaly2D,
            "Shift/offset from the learned position is visible in 2D."),
        new("Rotation Error", "IPC-A-610 component orientation", InspectionCapabilityTier.Anomaly2D,
            "90-degree rotation changes the 2D silhouette. A 180-degree flip of a symmetric package is a marking read and belongs to Polarity Error."),
        new("Solder Bridge", "IPC-A-610 soldering (bridging/shorts)", InspectionCapabilityTier.Anomaly2D,
            "A short between pads is a gross 2D visual anomaly."),
        new("Excess Solder", "IPC-A-610 soldering (fillet)", InspectionCapabilityTier.Anomaly2D,
            "A gross solder blob is a 2D area/shape anomaly. Quantifying the excess is a 3D measurement (see Solder Volume)."),
        new("Contamination", "IPC-A-610 cleanliness/foreign material", InspectionCapabilityTier.Anomaly2D,
            "Foreign material on an otherwise learned-clean surface is a 2D anomaly."),
        new("Scratch", "IPC-A-610 laminate/surface", InspectionCapabilityTier.Anomaly2D,
            "Surface abrasion differs visibly from the learned surface in 2D."),
        new("Silkscreen Error", "IPC-A-610 marking/legend", InspectionCapabilityTier.Anomaly2D,
            "Missing or wrong legend is a 2D difference against the golden reference."),
        new("Copper Exposure", "IPC-A-610 solder mask", InspectionCapabilityTier.Anomaly2D,
            "Mask breakdown shows as a colour/region difference in 2D."),
        new("Trace Damage", "IPC-A-610 conductor damage", InspectionCapabilityTier.Anomaly2D,
            "A visibly broken or gouged conductor differs from the golden reference in 2D, at sufficient resolution."),

        // ---- Tier 2: 2D-visible but needs a trained classifier to separate from OK ----
        new("Insufficient Solder", "IPC-A-610 soldering (fillet)", InspectionCapabilityTier.RequiresTrainedClassifier,
            "Fillet adequacy overlaps acceptable variation; needs a trained classifier."),
        new("Cold Joint", "IPC-A-610 soldering (wetting)", InspectionCapabilityTier.RequiresTrainedClassifier,
            "Dull/poor-wetting texture requires a trained classifier to separate from OK."),
        new("Poor Wetting", "IPC-A-610 soldering (wetting)", InspectionCapabilityTier.RequiresTrainedClassifier,
            "Wetting angle and spread overlap acceptable variation; needs a trained classifier."),
        new("Solder Crack", "IPC-A-610 soldering (joint integrity)", InspectionCapabilityTier.RequiresTrainedClassifier,
            "Hairline cracks need resolution plus a trained classifier; many are only visible in cross-section."),
        new("Solder Ball", "IPC-A-610 soldering (solder balls)", InspectionCapabilityTier.RequiresTrainedClassifier,
            "Small spheres are easily confused with speckle/flux; needs a trained classifier."),
        new("Fillet Shape Defect", "IPC-A-610 soldering (fillet geometry)", InspectionCapabilityTier.RequiresTrainedClassifier,
            "Fillet geometry grading is a classification problem, not a generic anomaly."),
        new("Polarity Error", "IPC-A-610 component orientation", InspectionCapabilityTier.RequiresTrainedClassifier,
            "Polarity marks are subtle; requires a trained classifier, not generic anomaly."),
        new("Bent Lead", "IPC-A-610 lead condition", InspectionCapabilityTier.RequiresTrainedClassifier,
            "Top-down 2D sees lead deviation only partially; a side view or classifier is needed for reliable calls."),
        new("Damaged Component", "IPC-A-610 component damage", InspectionCapabilityTier.RequiresTrainedClassifier,
            "Cracked/chipped packages need a trained classifier to separate from marking and texture variation."),
        new("Open Circuit", "IPC-A-610 conductor continuity", InspectionCapabilityTier.RequiresTrainedClassifier,
            "Only the optically visible broken-trace subset is image-detectable; electrical continuity needs ICT, which is outside every roadmap stage."),

        // ---- Tier 3: needs an angled / side-view acquisition path (Stage 2) ----
        new("Shield Can Gap", "IPC-A-610 mechanical/mounting", InspectionCapabilityTier.RequiresSideViewImaging,
            "A gap under a can edge is not observable from a top-down camera; the customer spec lists Side-View AOI."),
        new("Partial Insertion", "IPC-A-610 connector seating", InspectionCapabilityTier.RequiresSideViewImaging,
            "Seating depth is a height relationship; a top-down view cannot judge it reliably."),
        new("Bent Pin", "IPC-A-610 connector pin condition", InspectionCapabilityTier.RequiresSideViewImaging,
            "Pin deformation is mostly out-of-plane; it needs a side or angled view."),
        new("Pad Lift", "IPC-A-610 laminate/pad condition", InspectionCapabilityTier.RequiresSideViewImaging,
            "Pad elevation off the laminate is out-of-plane; top-down 2D cannot see it."),

        // ---- Tier 4: needs 3D acquisition hardware (Stage 2) ----
        new("Solder Volume", "IPC-A-610 / SPI dimensional", InspectionCapabilityTier.RequiresThreeDHardware,
            "Volume is a 3D measurement; cannot be validated from 2D images."),
        new("3D Coplanarity", "IPC-A-610 lead coplanarity", InspectionCapabilityTier.RequiresThreeDHardware,
            "Coplanarity requires height data from a 3D sensor."),
        new("Connector Pin Height", "IPC-A-610 dimensional", InspectionCapabilityTier.RequiresThreeDHardware,
            "Pin height is a 3D measurement."),
        new("Height Error", "IPC-A-610 dimensional", InspectionCapabilityTier.RequiresThreeDHardware,
            "Height requires a 3D profile source."),

        // ---- Tier 5: other machine types, outside this product's roadmap ----
        new("Paste Misalignment", "IPC-7527 solder paste print", InspectionCapabilityTier.OutOfProductScope,
            "Paste print inspection is an SPI station before reflow; this product inspects assembled boards."),
        new("Paste Insufficient", "IPC-7527 solder paste print", InspectionCapabilityTier.OutOfProductScope,
            "SPI measurement of deposited paste volume; outside every roadmap stage."),
        new("Paste Excess", "IPC-7527 solder paste print", InspectionCapabilityTier.OutOfProductScope,
            "SPI measurement of deposited paste volume; outside every roadmap stage."),
        new("Paste Slump", "IPC-7527 solder paste print", InspectionCapabilityTier.OutOfProductScope,
            "SPI post-print measurement; outside every roadmap stage."),
        new("Paste Void", "IPC-A-610 voiding", InspectionCapabilityTier.OutOfProductScope,
            "Voids inside paste require X-ray; outside every roadmap stage."),
        new("Via Defect", "IPC-A-600 plated-through hole", InspectionCapabilityTier.OutOfProductScope,
            "Via plating integrity requires X-ray; outside every roadmap stage."),

        // ---- Catch-all ----
        new("Anomaly", "Vendor-defined (not an IPC class)", InspectionCapabilityTier.Anomaly2D,
            "Generic 'differs from learned normal' region. Deliberately unclassified — it names a region, not a defect mechanism."),
        new("OK", "n/a", InspectionCapabilityTier.Anomaly2D,
            "The pass class. Present so every catalogued class has a capability row."),
    };

    public static DefectCapability? Find(string canonicalClass)
        => Catalog.FirstOrDefault(c => string.Equals(c.CanonicalClass, canonicalClass, StringComparison.OrdinalIgnoreCase));

    /// <summary>True only for classes an image-only anomaly engine can legitimately flag.</summary>
    public static bool CanImageOnlyEngineDetect(string canonicalClass)
        => Find(canonicalClass)?.Tier == InspectionCapabilityTier.Anomaly2D;

    /// <summary>True for classes that cannot be validated without 3D acquisition hardware.</summary>
    public static bool RequiresThreeD(string canonicalClass)
        => Find(canonicalClass)?.Tier == InspectionCapabilityTier.RequiresThreeDHardware;

    /// <summary>True for classes that need an angled/side-view acquisition path (Stage 2).</summary>
    public static bool RequiresSideView(string canonicalClass)
        => Find(canonicalClass)?.Tier == InspectionCapabilityTier.RequiresSideViewImaging;

    /// <summary>
    /// True when the class belongs to another machine type (SPI/X-ray/ICT) and this product
    /// will never detect it. Such classes exist for labelling, reporting, and MES coding only.
    /// </summary>
    public static bool IsOutOfProductScope(string canonicalClass)
        => Find(canonicalClass)?.Tier == InspectionCapabilityTier.OutOfProductScope;

    /// <summary>
    /// True when this product can inspect for the class at some roadmap stage. Unknown classes
    /// (customer-imported taxonomy entries with no capability row) are treated as inspectable so
    /// a customer extension is never silently hidden from the recipe editor.
    /// </summary>
    public static bool IsInspectableByThisProduct(string canonicalClass)
        => Find(canonicalClass)?.Tier != InspectionCapabilityTier.OutOfProductScope;

    /// <summary>
    /// One-line, operator-safe statement of what is required to detect the class, for evidence
    /// text and model-acceptance messages. Empty when the class has no capability row.
    /// </summary>
    public static string RequirementSummary(string canonicalClass) => Find(canonicalClass)?.Tier switch
    {
        InspectionCapabilityTier.Anomaly2D => "detectable by the Stage 1 image-only anomaly engines",
        InspectionCapabilityTier.RequiresTrainedClassifier => "requires a trained classifier model; image-only anomaly engines must not claim it",
        InspectionCapabilityTier.RequiresSideViewImaging => "requires an angled/side-view acquisition path (Stage 2 camera hardware)",
        InspectionCapabilityTier.RequiresThreeDHardware => "requires 3D acquisition hardware (Stage 2)",
        InspectionCapabilityTier.OutOfProductScope => "belongs to another machine type (SPI/X-ray/ICT) outside this product's roadmap; labelling and reporting only",
        _ => string.Empty,
    };
}
