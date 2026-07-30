using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AOI_Monitor.Services;

/// <summary>
/// Fully materialized golden reference at the engine's normalized comparison size:
/// BGRA pixels (stride = Width * 4) and the derived grayscale plane.
/// </summary>
public sealed record GoldenNormalizedPixels(int Width, int Height, byte[] Bgra, double[] Gray)
{
    /// <summary>Rebuilds a frozen BitmapSource with pixel-identical content (for the recipe ROI path).</summary>
    public BitmapSource ToBitmapSource()
    {
        var source = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgra32, null, Bgra, Width * 4);
        source.Freeze();
        return source;
    }
}

/// <summary>
/// Bounded cache of normalized golden-reference pixels for the pixel-difference engine.
/// Golden images are decoded, downscaled, extracted, and grayscaled once per file
/// version instead of once per inspection. Entries are keyed by full path, file size,
/// last-write time, AND a head/tail content fingerprint (SHA-256 of the first and last
/// 4 KB), so overwriting a golden invalidates its entry even when a sync tool preserves
/// size and timestamp. The key is re-validated after the build so a file replaced
/// mid-build is never cached under the old key. Cached bytes are bit-identical to the
/// uncached pipeline, so scores, verdicts, and hotspots are unchanged (verified by tests).
/// </summary>
public static class PixelDifferenceGoldenCache
{
    private const int MaxEntries = 4;
    private const int FingerprintSpanBytes = 4096;

    private sealed record CacheKey(string FullPath, long Length, DateTime LastWriteUtc, string ContentFingerprint);

    private static readonly object Gate = new();
    private static readonly Dictionary<CacheKey, (GoldenNormalizedPixels Pixels, long Touch)> Entries = new();
    private static long _touchCounter;
    private static long _hits;
    private static long _misses;

    public static long Hits => Interlocked.Read(ref _hits);
    public static long Misses => Interlocked.Read(ref _misses);

    /// <summary>
    /// Returns the normalized golden pixels for <paramref name="goldenPath"/>, building
    /// them via <paramref name="factory"/> on a miss. Returns null (and caches nothing)
    /// when the file is missing or the factory cannot produce pixels, so load-failure
    /// behavior matches the uncached engine exactly.
    /// </summary>
    public static GoldenNormalizedPixels? GetOrCreate(string goldenPath, Func<GoldenNormalizedPixels?> factory)
    {
        var key = TryBuildKey(goldenPath, out var fileExists);
        if (!fileExists)
            return null;
        if (key is null)
            return factory();

        lock (Gate)
        {
            if (Entries.TryGetValue(key, out var entry))
            {
                Interlocked.Increment(ref _hits);
                Entries[key] = (entry.Pixels, ++_touchCounter);
                return entry.Pixels;
            }
        }

        // Built outside the lock so a slow decode never blocks other goldens; a
        // concurrent duplicate build is benign (identical bytes, last insert wins).
        Interlocked.Increment(ref _misses);
        var pixels = factory();
        if (pixels is null)
            return null;

        // Re-validate the key after the build: an atomic file replace landing between
        // the probe and the decoder's read would otherwise poison the old key with the
        // new file's pixels. On mismatch the result is returned uncached; the next call
        // re-probes the current file and self-corrects.
        var keyAfterBuild = TryBuildKey(goldenPath, out var stillExists);
        if (!stillExists || keyAfterBuild is null || keyAfterBuild != key)
            return pixels;

        lock (Gate)
        {
            Entries[key] = (pixels, ++_touchCounter);
            while (Entries.Count > MaxEntries)
            {
                var oldest = Entries.OrderBy(pair => pair.Value.Touch).First().Key;
                Entries.Remove(oldest);
            }
        }

        return pixels;
    }

    public static void ClearForTests()
    {
        lock (Gate)
        {
            Entries.Clear();
        }

        Interlocked.Exchange(ref _hits, 0);
        Interlocked.Exchange(ref _misses, 0);
    }

    private static CacheKey? TryBuildKey(string goldenPath, out bool fileExists)
    {
        try
        {
            var info = new FileInfo(Path.GetFullPath(goldenPath));
            fileExists = info.Exists;
            if (!fileExists)
                return null;

            return new CacheKey(info.FullName, info.Length, info.LastWriteTimeUtc, ComputeHeadTailFingerprint(info));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // A file we cannot probe is treated as present-but-uncacheable so the
            // factory still runs and load failures surface through the engine's
            // normal error path.
            System.Diagnostics.Trace.WriteLine($"Golden cache key probe failed for '{goldenPath}': {ex.Message}");
            fileExists = true;
            return null;
        }
    }

    private static string ComputeHeadTailFingerprint(FileInfo info)
    {
        using var stream = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var headLength = (int)Math.Min(FingerprintSpanBytes, info.Length);
        var buffer = new byte[headLength + (int)Math.Min(FingerprintSpanBytes, Math.Max(0, info.Length - headLength))];
        stream.ReadExactly(buffer.AsSpan(0, headLength));
        var tailLength = buffer.Length - headLength;
        if (tailLength > 0)
        {
            stream.Seek(-tailLength, SeekOrigin.End);
            stream.ReadExactly(buffer.AsSpan(headLength, tailLength));
        }

        return Convert.ToHexString(SHA256.HashData(buffer));
    }
}
