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
/// </summary>
public enum InspectionCapabilityTier
{
    /// <summary>Gross presence/placement defect visible in 2D top images; an image-only
    /// anomaly engine can flag the region (final class label still benefits from a model).</summary>
    Anomaly2D,

    /// <summary>2D-visible, but reliably separating it from acceptable variation needs a
    /// trained classifier (e.g. ONNX). Anomaly-only engines must not claim this class.</summary>
    RequiresTrainedClassifier,

    /// <summary>Height / volume / coplanarity — requires 3D acquisition hardware. No 2D image
    /// engine can measure it, regardless of training.</summary>
    RequiresThreeDHardware,
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
        new("Missing Component", "IPC-A-610 component placement", InspectionCapabilityTier.Anomaly2D,
            "Absent component is a gross 2D anomaly."),
        new("Tombstone", "IPC-A-610 component placement/orientation", InspectionCapabilityTier.Anomaly2D,
            "Lifted/standing chip is a strong 2D silhouette anomaly."),
        new("Misalignment", "IPC-A-610 component placement/offset", InspectionCapabilityTier.Anomaly2D,
            "Shift/offset from the learned position is visible in 2D."),
        new("Shield Can Gap", "IPC-A-610 mechanical/mounting", InspectionCapabilityTier.Anomaly2D,
            "Gap/seating deviation is a 2D silhouette anomaly."),
        new("Solder Bridge", "IPC-A-610 soldering (bridging/shorts)", InspectionCapabilityTier.Anomaly2D,
            "A short between pads is a gross 2D visual anomaly."),

        new("Insufficient Solder", "IPC-A-610 soldering (fillet)", InspectionCapabilityTier.RequiresTrainedClassifier,
            "Fillet adequacy overlaps acceptable variation; needs a trained classifier."),
        new("Cold Joint", "IPC-A-610 soldering (wetting)", InspectionCapabilityTier.RequiresTrainedClassifier,
            "Dull/poor-wetting texture requires a trained classifier to separate from OK."),
        new("Polarity Error", "IPC-A-610 component orientation", InspectionCapabilityTier.RequiresTrainedClassifier,
            "Polarity marks are subtle; requires a trained classifier, not generic anomaly."),

        new("Solder Volume", "IPC-A-610 / SPI dimensional", InspectionCapabilityTier.RequiresThreeDHardware,
            "Volume is a 3D measurement; cannot be validated from 2D images."),
        new("3D Coplanarity", "IPC-A-610 lead coplanarity", InspectionCapabilityTier.RequiresThreeDHardware,
            "Coplanarity requires height data from a 3D sensor."),
        new("Connector Pin Height", "IPC-A-610 dimensional", InspectionCapabilityTier.RequiresThreeDHardware,
            "Pin height is a 3D measurement."),
        new("Height Error", "IPC-A-610 dimensional", InspectionCapabilityTier.RequiresThreeDHardware,
            "Height requires a 3D profile source."),
    };

    public static DefectCapability? Find(string canonicalClass)
        => Catalog.FirstOrDefault(c => string.Equals(c.CanonicalClass, canonicalClass, StringComparison.OrdinalIgnoreCase));

    /// <summary>True only for classes an image-only anomaly engine can legitimately flag.</summary>
    public static bool CanImageOnlyEngineDetect(string canonicalClass)
        => Find(canonicalClass)?.Tier == InspectionCapabilityTier.Anomaly2D;

    /// <summary>True for classes that cannot be validated without 3D acquisition hardware.</summary>
    public static bool RequiresThreeD(string canonicalClass)
        => Find(canonicalClass)?.Tier == InspectionCapabilityTier.RequiresThreeDHardware;
}
