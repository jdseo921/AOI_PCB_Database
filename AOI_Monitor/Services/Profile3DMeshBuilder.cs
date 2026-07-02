using System.Windows;
using System.Windows.Media.Media3D;

namespace AOI_Monitor.Services;

/// <summary>
/// Pure geometry helpers for the 3D Profile Viewer. Turns a dense height grid (row-major, NaN for
/// missing samples) into a WPF <see cref="MeshGeometry3D"/> surface, and finds local-maxima peaks in
/// a height slice. Kept free of UI/dispatcher state so it can be unit tested without a window.
/// </summary>
public static class Profile3DMeshBuilder
{
    /// <summary>
    /// Builds a surface mesh centered on the origin. X and Z span [-0.5, 0.5]; Y is the normalized
    /// height scaled by <paramref name="zScale"/>. Texture X is the normalized height (0..1) so a
    /// horizontal gradient brush colours the surface by height; a quad is only emitted when all four
    /// of its corner samples are present (finite).
    /// </summary>
    public static MeshGeometry3D BuildSurface(
        double[] heights,
        int width,
        int height,
        double minHeight,
        double maxHeight,
        double zScale)
    {
        ArgumentNullException.ThrowIfNull(heights);
        var mesh = new MeshGeometry3D();
        if (width <= 1 || height <= 1 || heights.Length < width * height)
            return mesh;

        var range = Math.Max(1e-6, maxHeight - minHeight);
        var vertexIndex = new int[width * height];
        Array.Fill(vertexIndex, -1);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var value = heights[(y * width) + x];
                if (double.IsNaN(value))
                    continue;

                var t = Math.Clamp((value - minHeight) / range, 0.0, 1.0);
                vertexIndex[(y * width) + x] = mesh.Positions.Count;
                var px = ((double)x / (width - 1)) - 0.5;
                var pz = ((double)y / (height - 1)) - 0.5;
                mesh.Positions.Add(new Point3D(px, t * zScale, pz));
                mesh.TextureCoordinates.Add(new Point(t, 0.5));
            }
        }

        for (var y = 0; y < height - 1; y++)
        {
            for (var x = 0; x < width - 1; x++)
            {
                var i00 = vertexIndex[(y * width) + x];
                var i10 = vertexIndex[(y * width) + x + 1];
                var i01 = vertexIndex[((y + 1) * width) + x];
                var i11 = vertexIndex[((y + 1) * width) + x + 1];
                if (i00 < 0 || i10 < 0 || i01 < 0 || i11 < 0)
                    continue;

                mesh.TriangleIndices.Add(i00);
                mesh.TriangleIndices.Add(i01);
                mesh.TriangleIndices.Add(i11);

                mesh.TriangleIndices.Add(i00);
                mesh.TriangleIndices.Add(i11);
                mesh.TriangleIndices.Add(i10);
            }
        }

        return mesh;
    }

    /// <summary>
    /// Returns the indices of prominent local maxima in a height slice: interior points that are
    /// greater than both neighbours and rise at least <paramref name="sigmaFactor"/> standard
    /// deviations above the mean. The global maximum is always included.
    /// </summary>
    public static IReadOnlyList<int> FindPeakIndices(IReadOnlyList<double> values, double sigmaFactor = 1.0)
    {
        ArgumentNullException.ThrowIfNull(values);
        var peaks = new List<int>();
        if (values.Count == 0)
            return peaks;

        var mean = values.Average();
        var variance = values.Count > 1
            ? values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1)
            : 0.0;
        var threshold = mean + (sigmaFactor * Math.Sqrt(variance));

        var globalMaxIndex = 0;
        for (var i = 1; i < values.Count; i++)
        {
            if (values[i] > values[globalMaxIndex])
                globalMaxIndex = i;
        }

        for (var i = 1; i < values.Count - 1; i++)
        {
            if (values[i] > values[i - 1] && values[i] >= values[i + 1] && values[i] >= threshold)
                peaks.Add(i);
        }

        if (!peaks.Contains(globalMaxIndex))
            peaks.Add(globalMaxIndex);

        peaks.Sort();
        return peaks;
    }
}
