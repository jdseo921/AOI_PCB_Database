using AOI_Monitor.Services;
using Xunit;

namespace AOI_Monitor.Tests;

public sealed class HashUtilTests : IDisposable
{
    private readonly string _dir;

    public HashUtilTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "AOI_HashUtil_Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        catch (IOException ex)
        {
            System.Diagnostics.Trace.WriteLine($"HashUtil test cleanup skipped: {ex.Message}");
        }
    }

    [Fact]
    public void ComputeSha256ReturnsKnownLowercaseHexDigest()
    {
        var path = Path.Combine(_dir, "hello.txt");
        File.WriteAllText(path, "hello");

        var hash = HashUtil.ComputeSha256(path);

        // Well-known SHA-256 of the ASCII bytes "hello", lowercase hex.
        Assert.Equal("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", hash);
    }

    [Fact]
    public void ComputeSha256IsStableAcrossCalls()
    {
        var path = Path.Combine(_dir, "data.bin");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5 });

        Assert.Equal(HashUtil.ComputeSha256(path), HashUtil.ComputeSha256(path));
    }
}
