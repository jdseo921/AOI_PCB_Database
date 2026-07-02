using System.Windows.Media.Media3D;
using AOI_Monitor.Services;
using Xunit;

namespace AOI_Monitor.Tests;

public sealed class Profile3DMeshBuilderTests
{
    [Fact]
    public void BuildSurfaceEmitsVertexPerSampleAndTwoTrianglesPerQuad()
    {
        // 3x2 grid, all samples present.
        var heights = new double[] { 0, 1, 2, 3, 4, 5 };

        var mesh = Profile3DMeshBuilder.BuildSurface(heights, width: 3, height: 2, minHeight: 0, maxHeight: 5, zScale: 0.4);

        Assert.Equal(6, mesh.Positions.Count);
        Assert.Equal(6, mesh.TextureCoordinates.Count);
        // (width-1) * (height-1) quads * 2 triangles * 3 indices = 2 * 1 * 2 * 3 = 12.
        Assert.Equal(12, mesh.TriangleIndices.Count);
    }

    [Fact]
    public void BuildSurfaceSkipsQuadsWithMissingSamples()
    {
        // 2x2 grid with one NaN gap: the single quad touches the gap, so no triangles are emitted.
        var heights = new double[] { 1, 2, double.NaN, 4 };

        var mesh = Profile3DMeshBuilder.BuildSurface(heights, width: 2, height: 2, minHeight: 1, maxHeight: 4, zScale: 0.4);

        Assert.Equal(3, mesh.Positions.Count);
        Assert.Empty(mesh.TriangleIndices);
    }

    [Fact]
    public void BuildSurfaceReturnsEmptyMeshForDegenerateDimensions()
    {
        var mesh = Profile3DMeshBuilder.BuildSurface(new double[] { 1, 2 }, width: 2, height: 1, minHeight: 1, maxHeight: 2, zScale: 0.4);

        Assert.Empty(mesh.Positions);
        Assert.Empty(mesh.TriangleIndices);
    }

    [Fact]
    public void BuildSurfaceMapsTextureCoordinateToNormalizedHeight()
    {
        // First vertex sits at the minimum height (t=0), the rest at the maximum (t=1).
        var heights = new double[] { 0, 10, 10, 10 };

        var mesh = Profile3DMeshBuilder.BuildSurface(heights, width: 2, height: 2, minHeight: 0, maxHeight: 10, zScale: 0.4);

        Assert.Equal(0.0, mesh.TextureCoordinates[0].X, 3);
        Assert.Equal(1.0, mesh.TextureCoordinates[1].X, 3);
        // Height is scaled by zScale on the Y axis.
        Assert.Equal(0.0, mesh.Positions[0].Y, 3);
        Assert.Equal(0.4, mesh.Positions[1].Y, 3);
    }

    [Fact]
    public void FindPeakIndicesReturnsInteriorLocalMaximum()
    {
        var peaks = Profile3DMeshBuilder.FindPeakIndices(new double[] { 1, 2, 3, 2, 1 });

        Assert.Contains(2, peaks);
    }

    [Fact]
    public void FindPeakIndicesAlwaysIncludesGlobalMaximum()
    {
        // The maximum is at the boundary (index 0) and is not an interior local maximum,
        // but it must still be reported.
        var peaks = Profile3DMeshBuilder.FindPeakIndices(new double[] { 5, 1, 1, 1, 1 });

        Assert.Contains(0, peaks);
    }

    [Fact]
    public void FindPeakIndicesReturnsSortedResults()
    {
        var peaks = Profile3DMeshBuilder.FindPeakIndices(new double[] { 0, 4, 0, 5, 0, 6, 0 }, sigmaFactor: 0.5);

        var sorted = peaks.OrderBy(i => i).ToList();
        Assert.Equal(sorted, peaks);
    }

    [Fact]
    public void FindPeakIndicesHandlesEmptyInput()
    {
        Assert.Empty(Profile3DMeshBuilder.FindPeakIndices(Array.Empty<double>()));
    }
}
