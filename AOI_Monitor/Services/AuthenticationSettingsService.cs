using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AOI_Monitor.Data;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public sealed class AuthenticationSettings
{
    public AuthenticationMode Mode { get; set; } = AuthenticationMode.DemoLocalRoleSelector;
}

public sealed class LocalUserRecord
{
    public string UserId { get; set; } = string.Empty;
    public UserRole Role { get; set; } = UserRole.Operator;
    public string PasswordHash { get; set; } = string.Empty;
    public string PasswordSalt { get; set; } = string.Empty;
    public int Iterations { get; set; } = 120_000;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string CreatedBy { get; set; } = "UNKNOWN";
    public DateTime? UpdatedAtUtc { get; set; }
    public bool IsDisabled { get; set; }
}

public sealed class LocalUserStore
{
    public List<LocalUserRecord> Users { get; set; } = new();
}

public static class AuthenticationSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static AuthenticationSettings? _cachedSettings;
    private static LocalUserStore? _cachedUsers;

    public static event Action? AuthenticationChanged;

    public static string SettingsPath => Path.Combine(AoiDatabase.StorageRoot, "authentication_settings.json");
    public static string LocalUsersPath => Path.Combine(AoiDatabase.StorageRoot, "local_users.json");

    public static AuthenticationMode CurrentMode => Load().Mode;

    public static AuthenticationSettings Load()
    {
        if (_cachedSettings is not null)
            return Clone(_cachedSettings);

        Directory.CreateDirectory(AoiDatabase.StorageRoot);
        if (!File.Exists(SettingsPath))
        {
            _cachedSettings = new AuthenticationSettings();
            Save(_cachedSettings, "SYSTEM", notify: false);
            return Clone(_cachedSettings);
        }

        try
        {
            _cachedSettings = JsonSerializer.Deserialize<AuthenticationSettings>(File.ReadAllText(SettingsPath), JsonOptions) ?? new AuthenticationSettings();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"Authentication settings load fallback used: {ex.Message}");
            _cachedSettings = new AuthenticationSettings();
        }

        Normalize(_cachedSettings);
        return Clone(_cachedSettings);
    }

    public static void Save(AuthenticationSettings settings, string operatorId = "UNKNOWN")
        => Save(settings, operatorId, notify: true);

    public static LocalUserRecord CreateUser(string userId, UserRole role, string password, UserRole actingRole, string createdBy)
    {
        if (!RoleAuthorization.CanManageSettings(actingRole))
            throw new UnauthorizedAccessException(RoleAuthorization.DeniedMessage(actingRole, "creating local users"));
        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("User ID is required.", nameof(userId));
        if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
            throw new ArgumentException("Local user password must be at least 8 characters.", nameof(password));

        var store = LoadUsersInternal();
        var normalizedUserId = userId.Trim();
        if (store.Users.Any(user => string.Equals(user.UserId, normalizedUserId, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Local user already exists.");

        var (hash, salt, iterations) = CreatePasswordHash(password);
        var record = new LocalUserRecord
        {
            UserId = normalizedUserId,
            Role = role,
            PasswordHash = hash,
            PasswordSalt = salt,
            Iterations = iterations,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = createdBy,
        };

        store.Users.Add(record);
        SaveUsersInternal(store);
        AoiDatabase.UpsertLocalUserMetadata(record.UserId, record.Role.ToString(), record.IsDisabled, record.CreatedAtUtc, record.CreatedBy, record.UpdatedAtUtc);
        AoiDatabase.RecordAuditEvent(
            "LOCAL_USER_CREATE",
            $"Local user created: {normalizedUserId}; role={role}.",
            operatorWithRole: createdBy,
            relatedEntityType: "LocalUser",
            relatedEntityId: normalizedUserId);
        return Clone(record);
    }

    public static bool TryAuthenticate(string userId, string password, out LocalUserRecord user)
    {
        var store = LoadUsersInternal();
        var candidate = store.Users.FirstOrDefault(item =>
            string.Equals(item.UserId, userId?.Trim(), StringComparison.OrdinalIgnoreCase));
        var normalizedUserId = userId?.Trim() ?? string.Empty;
        if (candidate is null)
        {
            AoiDatabase.RecordLocalUserSession(normalizedUserId, "UNKNOWN", AuthenticationMode.LocalUsers.ToString(), success: false, "Local user not found.");
            user = new LocalUserRecord();
            return false;
        }

        if (candidate.IsDisabled)
        {
            AoiDatabase.RecordLocalUserSession(candidate.UserId, candidate.Role.ToString(), AuthenticationMode.LocalUsers.ToString(), success: false, "Local user disabled.");
            user = new LocalUserRecord();
            return false;
        }

        if (string.IsNullOrWhiteSpace(candidate.PasswordHash) || string.IsNullOrWhiteSpace(candidate.PasswordSalt))
        {
            AoiDatabase.RecordLocalUserSession(candidate.UserId, candidate.Role.ToString(), AuthenticationMode.LocalUsers.ToString(), success: false, "Local user password metadata incomplete.");
            user = new LocalUserRecord();
            return false;
        }

        var salt = Convert.FromBase64String(candidate.PasswordSalt);
        var expected = Convert.FromBase64String(candidate.PasswordHash);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password ?? string.Empty, salt, candidate.Iterations, HashAlgorithmName.SHA256, expected.Length);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            AoiDatabase.RecordLocalUserSession(candidate.UserId, candidate.Role.ToString(), AuthenticationMode.LocalUsers.ToString(), success: false, "Local user password mismatch.");
            user = new LocalUserRecord();
            return false;
        }

        user = Clone(candidate);
        AoiDatabase.RecordLocalUserSession(candidate.UserId, candidate.Role.ToString(), AuthenticationMode.LocalUsers.ToString(), success: true, "Local user authenticated.");
        return true;
    }

    public static IReadOnlyList<LocalUserRecord> GetUsers()
        => LoadUsersInternal().Users.Select(Clone).ToArray();

    public static LocalUserRecord DisableUser(string userId, UserRole actingRole, string operatorWithRole, string reason = "")
    {
        if (!RoleAuthorization.CanManageSettings(actingRole))
            throw new UnauthorizedAccessException(RoleAuthorization.DeniedMessage(actingRole, "disabling local users"));

        var store = LoadUsersInternal();
        var record = FindUser(store, userId);
        record.IsDisabled = true;
        record.UpdatedAtUtc = DateTime.UtcNow;
        SaveUsersInternal(store);
        AoiDatabase.UpsertLocalUserMetadata(record.UserId, record.Role.ToString(), record.IsDisabled, record.CreatedAtUtc, record.CreatedBy, record.UpdatedAtUtc);
        AoiDatabase.RecordAuditEvent(
            "LOCAL_USER_DISABLE",
            $"Local user disabled: {record.UserId}; reason={RedactReason(reason)}.",
            operatorWithRole: operatorWithRole,
            relatedEntityType: "LocalUser",
            relatedEntityId: record.UserId);
        return Clone(record);
    }

    public static void DeleteUser(string userId, UserRole actingRole, string operatorWithRole)
    {
        if (!RoleAuthorization.CanManageSettings(actingRole))
            throw new UnauthorizedAccessException(RoleAuthorization.DeniedMessage(actingRole, "deleting local users"));

        var store = LoadUsersInternal();
        var record = FindUser(store, userId);
        store.Users.Remove(record);
        SaveUsersInternal(store);
        AoiDatabase.MarkLocalUserDeleted(record.UserId, operatorWithRole);
    }

    public static LocalUserRecord ChangePassword(string userId, string newPassword, UserRole actingRole, string operatorWithRole)
    {
        if (!RoleAuthorization.CanManageSettings(actingRole))
            throw new UnauthorizedAccessException(RoleAuthorization.DeniedMessage(actingRole, "changing local user passwords"));
        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 8)
            throw new ArgumentException("Local user password must be at least 8 characters.", nameof(newPassword));

        var store = LoadUsersInternal();
        var record = FindUser(store, userId);
        var (hash, salt, iterations) = CreatePasswordHash(newPassword);
        record.PasswordHash = hash;
        record.PasswordSalt = salt;
        record.Iterations = iterations;
        record.UpdatedAtUtc = DateTime.UtcNow;
        SaveUsersInternal(store);
        AoiDatabase.UpsertLocalUserMetadata(record.UserId, record.Role.ToString(), record.IsDisabled, record.CreatedAtUtc, record.CreatedBy, record.UpdatedAtUtc);
        AoiDatabase.RecordAuditEvent(
            "LOCAL_USER_PASSWORD_CHANGE",
            $"Local user password changed: {record.UserId}.",
            operatorWithRole: operatorWithRole,
            relatedEntityType: "LocalUser",
            relatedEntityId: record.UserId);
        return Clone(record);
    }

    public static void ResetForTests()
    {
        _cachedSettings = null;
        _cachedUsers = null;
    }

    private static void Save(AuthenticationSettings settings, string operatorId, bool notify)
    {
        Normalize(settings);
        Directory.CreateDirectory(AoiDatabase.StorageRoot);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
        _cachedSettings = Clone(settings);
        AoiDatabase.RecordAuditEvent(
            "AUTHENTICATION_MODE",
            $"Authentication mode set to {settings.Mode}.",
            operatorWithRole: operatorId,
            relatedEntityType: "AuthenticationSettings",
            relatedEntityId: settings.Mode.ToString());
        if (notify)
            AuthenticationChanged?.Invoke();
    }

    private static LocalUserStore LoadUsersInternal()
    {
        if (_cachedUsers is not null)
            return Clone(_cachedUsers);

        Directory.CreateDirectory(AoiDatabase.StorageRoot);
        if (!File.Exists(LocalUsersPath))
        {
            _cachedUsers = new LocalUserStore();
            SaveUsersInternal(_cachedUsers);
            return Clone(_cachedUsers);
        }

        try
        {
            _cachedUsers = JsonSerializer.Deserialize<LocalUserStore>(File.ReadAllText(LocalUsersPath), JsonOptions) ?? new LocalUserStore();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"Local user store load fallback used: {ex.Message}");
            _cachedUsers = new LocalUserStore();
        }

        return Clone(_cachedUsers);
    }

    private static void SaveUsersInternal(LocalUserStore store)
    {
        Directory.CreateDirectory(AoiDatabase.StorageRoot);
        File.WriteAllText(LocalUsersPath, JsonSerializer.Serialize(store, JsonOptions));
        _cachedUsers = Clone(store);
    }

    private static void Normalize(AuthenticationSettings settings)
    {
        if (!Enum.IsDefined(settings.Mode))
            settings.Mode = AuthenticationMode.DemoLocalRoleSelector;
    }

    private static LocalUserRecord FindUser(LocalUserStore store, string userId)
    {
        var normalizedUserId = userId?.Trim() ?? string.Empty;
        return store.Users.FirstOrDefault(user => string.Equals(user.UserId, normalizedUserId, StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidOperationException("Local user does not exist.");
    }

    private static (string Hash, string Salt, int Iterations) CreatePasswordHash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var iterations = 120_000;
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt), iterations);
    }

    private static string RedactReason(string reason)
        => string.IsNullOrWhiteSpace(reason) ? "not specified" : SecretProtectionService.RedactKnownSecrets(reason.Trim());

    private static AuthenticationSettings Clone(AuthenticationSettings source)
        => new() { Mode = source.Mode };

    private static LocalUserStore Clone(LocalUserStore source)
        => new() { Users = source.Users.Select(Clone).ToList() };

    private static LocalUserRecord Clone(LocalUserRecord source)
        => new()
        {
            UserId = source.UserId,
            Role = source.Role,
            PasswordHash = source.PasswordHash,
            PasswordSalt = source.PasswordSalt,
            Iterations = source.Iterations,
            CreatedAtUtc = source.CreatedAtUtc,
            CreatedBy = source.CreatedBy,
            UpdatedAtUtc = source.UpdatedAtUtc,
            IsDisabled = source.IsDisabled,
        };
}
