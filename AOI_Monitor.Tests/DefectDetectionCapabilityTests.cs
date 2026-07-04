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
