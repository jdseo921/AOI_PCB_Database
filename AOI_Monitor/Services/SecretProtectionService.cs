using System.Security.Cryptography;
using System.Text;

namespace AOI_Monitor.Services;

public static class SecretProtectionService
{
    public const string ProtectedPrefix = "dpapi:v1:";
    public const string Redacted = "***";

    public static string Protect(string? secret)
    {
        if (string.IsNullOrEmpty(secret))
            return string.Empty;
        if (IsProtected(secret))
            return secret;

        var bytes = Encoding.UTF8.GetBytes(secret);
        var protectedBytes = ProtectedData.Protect(bytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        return ProtectedPrefix + Convert.ToBase64String(protectedBytes);
    }

    public static string Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        if (!IsProtected(value))
            return value;

        var payload = value[ProtectedPrefix.Length..];
        var bytes = Convert.FromBase64String(payload);
        var unprotected = ProtectedData.Unprotect(bytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(unprotected);
    }

    public static bool IsProtected(string? value)
        => !string.IsNullOrWhiteSpace(value) &&
           value.StartsWith(ProtectedPrefix, StringComparison.Ordinal);

    public static string RedactKnownSecrets(string? text, params string?[] secrets)
    {
        var redacted = text ?? string.Empty;
        foreach (var secret in secrets)
        {
            if (!string.IsNullOrWhiteSpace(secret))
                redacted = redacted.Replace(secret, Redacted, StringComparison.Ordinal);
        }

        return redacted;
    }
}
