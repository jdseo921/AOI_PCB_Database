using System.IO;
using System.Windows.Media.Imaging;

namespace AOI_Monitor.Services;

public sealed record ImageCacheDiagnostics(
    int ImageCacheCount,
    int ThumbnailCacheCount,
    long EstimatedBytes,
    int LargeBitmapCount);

public static class ImageCacheService
{
    public const long DefaultMaxBytes = 320L * 1024 * 1024;
    private const int DefaultMaxEntries = 48;
    private const int LargeBitmapPixels = 4_000_000;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, CacheEntry> FullCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, CacheEntry> ThumbnailCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<string> FullLru = new();
    private static readonly LinkedList<string> ThumbnailLru = new();
    private static long _estimatedBytes;

    public static BitmapSource LoadBitmap(string path, int? decodePixelWidth = null, bool cache = true)
    {
        var key = BuildKey(path, decodePixelWidth);
        if (cache && TryGet(FullCache, FullLru, key, out var cached))
            return cached.Bitmap;

        var bitmap = LoadFrozenBitmap(path, decodePixelWidth);
        var estimatedBytes = EstimateBytes(bitmap);
        if (cache)
            Add(FullCache, FullLru, key, bitmap, estimatedBytes);

        TrimIfNeeded();
        ClearOnMemoryPressure();
        return bitmap;
    }

    public static BitmapSource LoadThumbnail(string path, int decodePixelWidth = 240)
    {
        var key = BuildKey(path, decodePixelWidth);
        if (TryGet(ThumbnailCache, ThumbnailLru, key, out var cached))
            return cached.Bitmap;

        var bitmap = LoadFrozenBitmap(path, decodePixelWidth);
        Add(ThumbnailCache, ThumbnailLru, key, bitmap, EstimateBytes(bitmap));
        TrimIfNeeded();
        return bitmap;
    }

    public static bool TryLoadThumbnail(string path, out BitmapSource? thumbnail, int decodePixelWidth = 240)
    {
        try
        {
            thumbnail = LoadThumbnail(path, decodePixelWidth);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
            thumbnail = null;
            return false;
        }
    }

    public static void ClearOnPageUnload()
    {
        lock (Gate)
        {
            ClearDictionary(FullCache, FullLru);
            RecalculateBytes();
        }
    }

    public static void ReleaseFullSizeImage(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        var prefix = Path.GetFullPath(path) + "|";
        lock (Gate)
        {
            foreach (var key in FullCache.Keys.Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                if (FullCache.Remove(key, out var removed))
                    _estimatedBytes -= removed.EstimatedBytes;
                FullLru.Remove(key);
            }

            RecalculateBytes();
        }
    }

    public static void ClearOnMemoryPressure()
    {
        if (!IsCachePressureHigh())
            return;

        lock (Gate)
        {
            ClearDictionary(FullCache, FullLru);
            Trim(ThumbnailCache, ThumbnailLru, maxEntries: Math.Max(8, DefaultMaxEntries / 3));
            RecalculateBytes();
        }

        GC.Collect(2, GCCollectionMode.Optimized, blocking: false, compacting: false);
    }

    public static ImageCacheDiagnostics GetDiagnostics()
    {
        lock (Gate)
        {
            return new ImageCacheDiagnostics(
                FullCache.Count,
                ThumbnailCache.Count,
                _estimatedBytes,
                FullCache.Values.Concat(ThumbnailCache.Values).Count(entry => IsLarge(entry.Bitmap)));
        }
    }

    public static void ClearForTests()
    {
        lock (Gate)
        {
            ClearDictionary(FullCache, FullLru);
            ClearDictionary(ThumbnailCache, ThumbnailLru);
            _estimatedBytes = 0;
        }
    }

    private static BitmapSource LoadFrozenBitmap(string path, int? decodePixelWidth)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Image path is required.", nameof(path));

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bmp.StreamSource = stream;
        if (decodePixelWidth is > 0)
            bmp.DecodePixelWidth = decodePixelWidth.Value;
        bmp.EndInit();
        if (bmp.CanFreeze)
            bmp.Freeze();
        return bmp;
    }

    private static bool TryGet(
        Dictionary<string, CacheEntry> cache,
        LinkedList<string> lru,
        string key,
        out CacheEntry entry)
    {
        lock (Gate)
        {
            if (cache.TryGetValue(key, out entry!))
            {
                lru.Remove(key);
                lru.AddFirst(key);
                return true;
            }
        }

        entry = default!;
        return false;
    }

    private static void Add(
        Dictionary<string, CacheEntry> cache,
        LinkedList<string> lru,
        string key,
        BitmapSource bitmap,
        long estimatedBytes)
    {
        lock (Gate)
        {
            if (cache.Remove(key, out var existing))
                _estimatedBytes -= existing.EstimatedBytes;

            cache[key] = new CacheEntry(bitmap, estimatedBytes);
            lru.Remove(key);
            lru.AddFirst(key);
            _estimatedBytes += estimatedBytes;
            TrimIfNeededUnsafe(cache, lru);
        }
    }

    private static void TrimIfNeeded()
    {
        lock (Gate)
        {
            TrimIfNeededUnsafe(FullCache, FullLru);
            TrimIfNeededUnsafe(ThumbnailCache, ThumbnailLru);
        }
    }

    private static void TrimIfNeededUnsafe(Dictionary<string, CacheEntry> cache, LinkedList<string> lru)
    {
        while ((cache.Count > DefaultMaxEntries || _estimatedBytes > DefaultMaxBytes) && lru.Last is { } last)
        {
            if (cache.Remove(last.Value, out var removed))
                _estimatedBytes -= removed.EstimatedBytes;
            lru.RemoveLast();
        }
    }

    private static void Trim(Dictionary<string, CacheEntry> cache, LinkedList<string> lru, int maxEntries)
    {
        while (cache.Count > maxEntries && lru.Last is { } last)
        {
            if (cache.Remove(last.Value, out var removed))
                _estimatedBytes -= removed.EstimatedBytes;
            lru.RemoveLast();
        }
    }

    private static bool IsCachePressureHigh()
    {
        lock (Gate)
            return _estimatedBytes > DefaultMaxBytes || FullCache.Values.Any(entry => IsLarge(entry.Bitmap));
    }

    private static void ClearDictionary(Dictionary<string, CacheEntry> cache, LinkedList<string> lru)
    {
        cache.Clear();
        lru.Clear();
    }

    private static void RecalculateBytes()
        => _estimatedBytes = FullCache.Values.Concat(ThumbnailCache.Values).Sum(entry => entry.EstimatedBytes);

    private static long EstimateBytes(BitmapSource bitmap)
    {
        var bitsPerPixel = Math.Max(1, bitmap.Format.BitsPerPixel);
        return (long)bitmap.PixelWidth * bitmap.PixelHeight * bitsPerPixel / 8;
    }

    private static bool IsLarge(BitmapSource bitmap)
        => (long)bitmap.PixelWidth * bitmap.PixelHeight >= LargeBitmapPixels;

    private static string BuildKey(string path, int? decodePixelWidth)
    {
        var fullPath = Path.GetFullPath(path);
        var stamp = File.Exists(fullPath) ? File.GetLastWriteTimeUtc(fullPath).Ticks : 0;
        return $"{fullPath}|{decodePixelWidth?.ToString() ?? "full"}|{stamp}";
    }

    private sealed record CacheEntry(BitmapSource Bitmap, long EstimatedBytes);
}
