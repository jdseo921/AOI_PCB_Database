using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Xunit;

namespace AOI_Monitor.Tests;

public sealed class ImageMemoryDiagnosticsTests : IDisposable
{
    private readonly string _root;

    public ImageMemoryDiagnosticsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AOI_Monitor_ImageMemory_Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        StorageRootSettingsService.ConfigureSettingsDirectoryForTests(_root);
        AoiDatabase.ConfigureStorageRoot(_root);
        AoiDatabase.Initialize();
        WorkflowState.Instance.SetCurrentUser("ImageMemoryAdmin", UserRole.Admin);
        ImageCacheService.ClearForTests();
        MemoryDiagnosticsService.ClearForTests();
    }

    public void Dispose()
    {
        ImageCacheService.ClearForTests();
        MemoryDiagnosticsService.ClearForTests();
        StorageRootSettingsService.ConfigureSettingsDirectoryForTests(null);
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"Test cleanup failed for {nameof(ImageMemoryDiagnosticsTests)}: {ex.Message}");
        }
    }

    [Fact]
    public void RepeatedImageLoadDoesNotKeepFileLocked()
    {
        UiNavigationSmokeTests.RunOnStaForTests(() =>
        {
            var imagePath = Path.Combine(_root, "preview.png");
            WriteTinyPng(imagePath);

            for (var i = 0; i < 10; i++)
            {
                var bitmap = ImageCacheService.LoadBitmap(imagePath, decodePixelWidth: 64);
                Assert.True(bitmap.IsFrozen);
            }

            ImageCacheService.ReleaseFullSizeImage(imagePath);
            Assert.Equal(0, ImageCacheService.GetDiagnostics().ImageCacheCount);

            ImageCacheService.ClearOnPageUnload();
            var renamed = Path.Combine(_root, "preview_renamed.png");
            File.Move(imagePath, renamed);
            Assert.True(File.Exists(renamed));
        });
    }

    [Fact]
    public void ThumbnailGenerationHandlesCorruptImagesWithoutCrash()
    {
        UiNavigationSmokeTests.RunOnStaForTests(() =>
        {
            var corruptPath = Path.Combine(_root, "corrupt.png");
            File.WriteAllText(corruptPath, "not an image");

            var loaded = ImageCacheService.TryLoadThumbnail(corruptPath, out var thumbnail);

            Assert.False(loaded);
            Assert.Null(thumbnail);
        });
    }

    [Fact]
    public void RepeatedPageLikeImageNavigationDoesNotGrowCacheAfterRelease()
    {
        UiNavigationSmokeTests.RunOnStaForTests(() =>
        {
            var paths = Enumerable.Range(0, 12)
                .Select(i =>
                {
                    var path = Path.Combine(_root, $"nav_{i}.png");
                    WriteTinyPng(path);
                    return path;
                })
                .ToArray();

            for (var round = 0; round < 8; round++)
            {
                foreach (var path in paths)
                    _ = ImageCacheService.LoadBitmap(path, decodePixelWidth: 64);

                ImageCacheService.ClearOnPageUnload();
                MemoryDiagnosticsService.SetPageCacheCount(3);
            }

            var snapshot = MemoryDiagnosticsService.Capture();
            Assert.Equal(0, snapshot.ImageCacheCount);
            Assert.True(snapshot.ImageCacheBytes < 8 * 1024 * 1024);
            Assert.Equal(3, snapshot.ActivePageCount);
            Assert.Equal(3, snapshot.PageCacheCount);
        });
    }

    [Fact]
    public void LatestExportMemoryPeakIsTracked()
    {
        MemoryDiagnosticsService.RecordExportCheckpoint("unit-test export", 4);
        MemoryDiagnosticsService.RecordExportCheckpoint("unit-test export", 9);

        var snapshot = MemoryDiagnosticsService.Capture();

        Assert.Equal("unit-test export", snapshot.LatestExportOperation);
        Assert.Equal(9, snapshot.LatestExportProcessedCount);
        Assert.True(snapshot.LatestExportWorkingSetPeakBytes > 0);
        Assert.True(snapshot.LatestExportManagedPeakBytes > 0);
        Assert.NotNull(snapshot.LatestExportCapturedAtUtc);
    }

    [Fact]
    public void SupportBundleIncludesMemoryDiagnostics()
    {
        MemoryDiagnosticsService.SetActivePageCount(2);
        MemoryDiagnosticsService.RecordExportCheckpoint("support export", 7);
        MemoryDiagnosticsService.RecordExportMemoryWarning("test warning");

        var result = SupportBundleService.Export(
            new SupportBundleOptions { OutputRoot = Path.Combine(_root, "support") },
            WorkflowState.Instance.OperatorWithRole);

        var contextJson = File.ReadAllText(Path.Combine(result.StagingFolder, "support_context.json"));
        Assert.Contains("memoryDiagnostics", contextJson);
        Assert.Contains("imageCacheCount", contextJson);
        Assert.Contains("activePageCount", contextJson);
        Assert.Contains("latestExportWorkingSetPeakBytes", contextJson);
        Assert.Contains("test warning", contextJson);
    }

    private static void WriteTinyPng(string path)
    {
        var bytes = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
        File.WriteAllBytes(path, bytes);
    }
}
