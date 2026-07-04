using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Xunit;

namespace AOI_Monitor.Tests;

public sealed class CentroidRoiImportServiceTests
{
    private const int ImageWidth = 1000;
    private const int ImageHeight = 800;

    [Fact]
    public void ParseCentroidCsvReadsKiCadStyleDesignatorMidXMidYHeaders()
    {
        const string csv = """
            Designator,Mid X,Mid Y,Rotation,Layer,Footprint
            R1,10.5,20.25,90,Top,R_0603_1608Metric
            C1,-3.2,7.75,180,Bottom,C_0805_2012Metric
            """;

        var result = CentroidRoiImportService.ParseCentroidCsv(csv);

        Assert.Empty(result.Warnings);
        Assert.Equal(2, result.Records.Count);
        var r1 = result.Records[0];
        Assert.Equal("R1", r1.RefDes);
        Assert.Equal(10.5, r1.XMm, 6);
        Assert.Equal(20.25, r1.YMm, 6);
        Assert.Equal(90, r1.RotationDeg, 6);
        Assert.Equal("Top", r1.Side);
        Assert.Equal("R_0603_1608Metric", r1.Package);
        Assert.Equal("Bottom", result.Records[1].Side);
        Assert.Equal(-3.2, result.Records[1].XMm, 6);
    }

    [Fact]
    public void ParseCentroidCsvReadsAltiumStyleRefDesPosXPosYHeadersWithPreamble()
    {
        const string csv = """
            Altium Designer Pick and Place Locations

            RefDes,PosX,PosY,Rot,Side,Package
            U1,50.8mm,25.4mm,270,TopLayer,QFP-32
            """;

        var result = CentroidRoiImportService.ParseCentroidCsv(csv);

        Assert.Empty(result.Warnings);
        var record = Assert.Single(result.Records);
        Assert.Equal("U1", record.RefDes);
        Assert.Equal(50.8, record.XMm, 6);
        Assert.Equal(25.4, record.YMm, 6);
        Assert.Equal(270, record.RotationDeg, 6);
        Assert.Equal("Top", record.Side);
        Assert.Equal("QFP-32", record.Package);
    }

    [Fact]
    public void ParseCentroidCsvSupportsSemicolonDelimiterAndQuotedFields()
    {
        const string csv = """
            RefDes;PosX;PosY;Rotation;Side;Package
            "R1";1.5;2.5;0;Top;"RES;0603"
            """;

        var result = CentroidRoiImportService.ParseCentroidCsv(csv);

        Assert.Empty(result.Warnings);
        var record = Assert.Single(result.Records);
        Assert.Equal("R1", record.RefDes);
        Assert.Equal(1.5, record.XMm, 6);
        Assert.Equal(2.5, record.YMm, 6);
        Assert.Equal("RES;0603", record.Package);
    }

    [Fact]
    public void ParseCentroidCsvSupportsTabDelimiter()
    {
        var csv = "RefDes\tPosX\tPosY\tSide\nR1\t1.0\t2.0\tT\nR2\t3.0\t4.0\tB\n";

        var result = CentroidRoiImportService.ParseCentroidCsv(csv);

        Assert.Empty(result.Warnings);
        Assert.Equal(2, result.Records.Count);
        Assert.Equal("Top", result.Records[0].Side);
        Assert.Equal("Bottom", result.Records[1].Side);
        Assert.Equal(3.0, result.Records[1].XMm, 6);
    }

    [Fact]
    public void ParseCentroidCsvConvertsMilHeaderUnitsToMillimeters()
    {
        const string csv = """
            RefDes,PosX (mil),PosY (mil)
            R1,1000,2000
            """;

        var result = CentroidRoiImportService.ParseCentroidCsv(csv);

        Assert.Empty(result.Warnings);
        var record = Assert.Single(result.Records);
        Assert.Equal(25.4, record.XMm, 6);
        Assert.Equal(50.8, record.YMm, 6);
        Assert.Equal("Top", record.Side);
        Assert.Equal(0, record.RotationDeg, 6);
    }

    [Fact]
    public void ParseCentroidCsvSkipsAndWarnsOnMalformedRowsWithoutThrowing()
    {
        const string csv = """
            RefDes,PosX,PosY
            R1,1.0,2.0
            R2,abc,2.0
            ,3.0,4.0
            R4,5.0,6.0
            """;

        var result = CentroidRoiImportService.ParseCentroidCsv(csv);

        Assert.Equal(2, result.Records.Count);
        Assert.Equal(new[] { "R1", "R4" }, result.Records.Select(r => r.RefDes).ToArray());
        Assert.Equal(2, result.Warnings.Count);
        Assert.Contains(result.Warnings, w => w.Contains("not numeric", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Warnings, w => w.Contains("reference designator", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ParseCentroidCsvReturnsWarningForEmptyOrHeaderlessText()
    {
        var empty = CentroidRoiImportService.ParseCentroidCsv(string.Empty);
        Assert.Empty(empty.Records);
        Assert.Single(empty.Warnings);

        var headerless = CentroidRoiImportService.ParseCentroidCsv("just,some,random\nvalues,1,2\n");
        Assert.Empty(headerless.Records);
        Assert.Contains(headerless.Warnings, w => w.Contains("header", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GenerateRoisFlipsYAxisSoMinCadYMapsToImageBottom()
    {
        var records = new[]
        {
            new CentroidRecord("LOW", 5.0, 0.0, 0.0, "Top", string.Empty),
            new CentroidRecord("HIGH", 5.0, 10.0, 0.0, "Top", string.Empty),
        };

        var rois = CentroidRoiImportService.GenerateRois(records, ImageWidth, ImageHeight, new CentroidRoiImportOptions());

        Assert.Equal(2, rois.Count);
        var lowCenterY = CenterYPixels(rois.Single(r => r.Name == "LOW"));
        var highCenterY = CenterYPixels(rois.Single(r => r.Name == "HIGH"));
        Assert.True(lowCenterY > ImageHeight / 2.0, $"Record with min CAD Y should map to the bottom half of the image but was at {lowCenterY}px.");
        Assert.True(highCenterY < ImageHeight / 2.0, $"Record with max CAD Y should map to the top half of the image but was at {highCenterY}px.");
        Assert.True(lowCenterY > highCenterY);
    }

    [Fact]
    public void GenerateRoisAutoFitKeepsAllRoisInsideImageBoundsWithMargin()
    {
        var records = new[]
        {
            new CentroidRecord("BL", 0.0, 0.0, 0.0, "Top", string.Empty),
            new CentroidRecord("BR", 100.0, 0.0, 0.0, "Top", string.Empty),
            new CentroidRecord("TL", 0.0, 50.0, 0.0, "Top", string.Empty),
            new CentroidRecord("TR", 100.0, 50.0, 0.0, "Top", string.Empty),
            new CentroidRecord("MID", 50.0, 25.0, 0.0, "Top", string.Empty),
        };

        var rois = CentroidRoiImportService.GenerateRois(records, ImageWidth, ImageHeight, new CentroidRoiImportOptions());

        Assert.Equal(5, rois.Count);
        foreach (var roi in rois)
        {
            Assert.True(roi.Width > 0 && roi.Height > 0);
            Assert.True(roi.X > 0, $"ROI {roi.Name} X {roi.X} should be inset by the board margin.");
            Assert.True(roi.Y > 0, $"ROI {roi.Name} Y {roi.Y} should be inset by the board margin.");
            Assert.True(roi.X + roi.Width < 1, $"ROI {roi.Name} exceeds the right image edge.");
            Assert.True(roi.Y + roi.Height < 1, $"ROI {roi.Name} exceeds the bottom image edge.");
        }
    }

    [Fact]
    public void GenerateRoisFiltersRecordsBySide()
    {
        var records = new[]
        {
            new CentroidRecord("R1", 1.0, 1.0, 0.0, "Top", string.Empty),
            new CentroidRecord("R2", 2.0, 2.0, 0.0, "Bottom", string.Empty),
        };

        var topRois = CentroidRoiImportService.GenerateRois(records, ImageWidth, ImageHeight, new CentroidRoiImportOptions());
        var bottomRois = CentroidRoiImportService.GenerateRois(records, ImageWidth, ImageHeight, new CentroidRoiImportOptions { Side = "Bottom" });

        Assert.Equal("R1", Assert.Single(topRois).Name);
        Assert.Equal("R2", Assert.Single(bottomRois).Name);
    }

    [Fact]
    public void GenerateRoisProducesDeterministicUniqueIds()
    {
        var records = new[]
        {
            new CentroidRecord("R1", 0.0, 0.0, 0.0, "Top", string.Empty),
            new CentroidRecord("R1", 5.0, 5.0, 0.0, "Top", string.Empty),
            new CentroidRecord("R2", 10.0, 10.0, 0.0, "Top", string.Empty),
        };

        var first = CentroidRoiImportService.GenerateRois(records, ImageWidth, ImageHeight, new CentroidRoiImportOptions());
        var second = CentroidRoiImportService.GenerateRois(records, ImageWidth, ImageHeight, new CentroidRoiImportOptions());

        Assert.Equal(3, first.Select(r => r.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(first.Select(r => r.Id), second.Select(r => r.Id));
        Assert.Equal("CENTROID-R1", first[0].Id);
        Assert.Equal("CENTROID-R2", first[2].Id);
    }

    [Fact]
    public void GenerateRoisEmitsPresenceRoisCenteredWithDefaultSize()
    {
        var records = new[] { new CentroidRecord("R7", 12.5, 30.0, 0.0, "Top", string.Empty) };

        var rois = CentroidRoiImportService.GenerateRois(records, ImageWidth, ImageHeight, new CentroidRoiImportOptions());

        var roi = Assert.Single(rois);
        Assert.Equal("R7", roi.Name);
        Assert.Equal("Presence", roi.RoiType);
        Assert.True(roi.Enabled);
        Assert.Equal(0.65, roi.AiScoreThreshold, 6);
        // Single record: auto-fit centers it, so the 48 px square sits centered in the image.
        Assert.Equal(48.0 / ImageWidth, roi.Width, 5);
        Assert.Equal(48.0 / ImageHeight, roi.Height, 5);
        Assert.Equal((ImageWidth - 48.0) / 2.0 / ImageWidth, roi.X, 3);
        Assert.Equal((ImageHeight - 48.0) / 2.0 / ImageHeight, roi.Y, 3);
    }

    [Fact]
    public void GenerateRoisAppliesChipPackageSizeHintAndRotationSwap()
    {
        var records = new[]
        {
            new CentroidRecord("C1", 0.0, 0.0, 0.0, "Top", "0603"),
            new CentroidRecord("C2", 20.0, 10.0, 90.0, "Top", "R_0603_1608Metric"),
        };

        var rois = CentroidRoiImportService.GenerateRois(records, ImageWidth, ImageHeight, new CentroidRoiImportOptions());

        var flat = rois.Single(r => r.Name == "C1");
        var rotated = rois.Single(r => r.Name == "C2");
        Assert.True(flat.Width * ImageWidth > flat.Height * ImageHeight, "Unrotated 0603 ROI should be wider than tall.");
        Assert.True(rotated.Height * ImageHeight > rotated.Width * ImageWidth, "90-degree rotated 0603 ROI should be taller than wide.");
    }

    private static double CenterYPixels(RecipeRoiDocument roi)
        => (roi.Y + roi.Height / 2.0) * ImageHeight;
}
