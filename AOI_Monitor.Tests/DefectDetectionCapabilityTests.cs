using System;
using System.Linq;
using AOI_Monitor.Services;
using Xunit;

namespace AOI_Monitor.Tests;

public class DefectDetectionCapabilityTests
{
    [Theory]
    [InlineData("Solder Volume")]
    [InlineData("3D Coplanarity")]
    [InlineData("Connector Pin Height")]
    [InlineData("Height Error")]
    public void ThreeDDefectsAreNotDetectableFromImagesAlone(string canonicalClass)
    {
        Assert.True(DefectDetectionCapability.RequiresThreeD(canonicalClass));
        Assert.False(DefectDetectionCapability.CanImageOnlyEngineDetect(canonicalClass));
        Assert.Equal(InspectionCapabilityTier.RequiresThreeDHardware, DefectDetectionCapability.Find(canonicalClass)!.Tier);
    }

    [Theory]
    [InlineData("Missing Component")]
    [InlineData("Tombstone")]
    [InlineData("Misalignment")]
    [InlineData("Solder Bridge")]
    public void GrossPlacementDefectsAreImageOnlyAnomalyDetectable(string canonicalClass)
    {
        Assert.True(DefectDetectionCapability.CanImageOnlyEngineDetect(canonicalClass));
        Assert.False(DefectDetectionCapability.RequiresThreeD(canonicalClass));
    }

    [Theory]
    [InlineData("Insufficient Solder")]
    [InlineData("Cold Joint")]
    [InlineData("Polarity Error")]
    public void SubtleSolderClassesRequireATrainedClassifierNotGenericAnomaly(string canonicalClass)
    {
        Assert.False(DefectDetectionCapability.CanImageOnlyEngineDetect(canonicalClass));
        Assert.False(DefectDetectionCapability.RequiresThreeD(canonicalClass));
        Assert.Equal(InspectionCapabilityTier.RequiresTrainedClassifier, DefectDetectionCapability.Find(canonicalClass)!.Tier);
    }

    [Theory]
    // The customer spec lists these as Side-View AOI / out-of-plane observations. A single
    // top-down camera cannot see them, so image-only detectability must not be claimed.
    [InlineData("Shield Can Gap")]
    [InlineData("Partial Insertion")]
    [InlineData("Bent Pin")]
    [InlineData("Pad Lift")]
    public void OutOfPlaneDefectsRequireASideViewAndAreNotImageOnlyDetectable(string canonicalClass)
    {
        Assert.True(DefectDetectionCapability.RequiresSideView(canonicalClass));
        Assert.False(DefectDetectionCapability.CanImageOnlyEngineDetect(canonicalClass));
        Assert.False(DefectDetectionCapability.RequiresThreeD(canonicalClass));
        Assert.Equal(InspectionCapabilityTier.RequiresSideViewImaging, DefectDetectionCapability.Find(canonicalClass)!.Tier);
    }

    [Theory]
    // SPI / X-ray classes exist for labelling and MES coding only; this product never inspects them.
    [InlineData("Paste Misalignment")]
    [InlineData("Paste Insufficient")]
    [InlineData("Paste Excess")]
    [InlineData("Paste Slump")]
    [InlineData("Paste Void")]
    [InlineData("Via Defect")]
    public void OtherMachineTypeDefectsAreOutOfProductScope(string canonicalClass)
    {
        Assert.True(DefectDetectionCapability.IsOutOfProductScope(canonicalClass));
        Assert.False(DefectDetectionCapability.IsInspectableByThisProduct(canonicalClass));
        Assert.False(DefectDetectionCapability.CanImageOnlyEngineDetect(canonicalClass));
    }

    [Fact]
    public void ExcessSolderIsClaimedIn2DButQuantifyingItIsNot()
    {
        Assert.True(DefectDetectionCapability.CanImageOnlyEngineDetect("Excess Solder"));
        Assert.True(DefectDetectionCapability.RequiresThreeD("Solder Volume"));
    }

    [Fact]
    public void UnknownClassesAreTreatedAsInspectableSoCustomerExtensionsAreNotHidden()
    {
        Assert.True(DefectDetectionCapability.IsInspectableByThisProduct("Customer Special Class"));
        Assert.False(DefectDetectionCapability.IsOutOfProductScope("Customer Special Class"));
        Assert.Equal(string.Empty, DefectDetectionCapability.RequirementSummary("Customer Special Class"));
    }

    [Fact]
    public void CatalogHasNoDuplicateClasses()
    {
        var duplicates = DefectDetectionCapability.Catalog
            .GroupBy(c => c.CanonicalClass, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void LookupIsCaseInsensitiveAndUnknownReturnsNull()
    {
        Assert.NotNull(DefectDetectionCapability.Find("solder bridge"));
        Assert.Null(DefectDetectionCapability.Find("Not A Real Defect"));
        Assert.False(DefectDetectionCapability.CanImageOnlyEngineDetect("Not A Real Defect"));
    }

    [Fact]
    public void EveryCatalogEntryHasAnIpcReferenceAndNote()
    {
        Assert.NotEmpty(DefectDetectionCapability.Catalog);
        Assert.All(DefectDetectionCapability.Catalog, c =>
        {
            Assert.False(string.IsNullOrWhiteSpace(c.CanonicalClass));
            Assert.False(string.IsNullOrWhiteSpace(c.IpcReference));
            Assert.False(string.IsNullOrWhiteSpace(c.Note));
        });
    }
}
