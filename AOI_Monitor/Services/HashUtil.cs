using System.IO;
using System.Security.Cryptography;

namespace AOI_Monitor.Services;

/// <summary>
/// Shared file-hashing helper. Centralizes the SHA-256 file digest that was previously duplicated
/// across several services and the database layer, so the hashing behavior (lowercase hex of the
/// file's bytes) has a single source of truth.
/// </summary>
public static class HashUtil
{
    /// <summary>Returns the lowercase hex SHA-256 digest of the file at <paramref name="path"/>.</summary>
    public static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
