using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Microsoft.Data.Sqlite;

namespace AOI_Monitor.Data;

public static partial class AoiDatabase
{
    public static long RecordAuditEvent(
        string actionCategory,
        string actionDetail,
        DateTime? timestampLocal = null,
        string? operatorWithRole = null,
        string? userId = null,
        string? userRole = null,
        string? stationId = null,
        string relatedEntityType = "",
        string relatedEntityId = "",
        string relatedPath = "")
    {
        EnsureInitialized();

        var localTimestamp = timestampLocal ?? DateTime.Now;
        var timestampUtc = localTimestamp.ToUniversalTime();
        var (parsedUserId, parsedRole) = SplitOperatorWithRole(operatorWithRole);
        var effectiveUserId = NullIfWhiteSpace(userId) ?? parsedUserId ?? AuditUserIdProvider?.Invoke() ?? "UNKNOWN";
        var effectiveRole = NullIfWhiteSpace(userRole) ?? parsedRole ?? AuditUserRoleProvider?.Invoke() ?? ExtractRole(AuditOperatorProvider?.Invoke()) ?? "UNKNOWN";
        var effectiveStation = NullIfWhiteSpace(stationId) ?? AuditStationProvider?.Invoke() ?? "UNKNOWN";

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO AuditEvents
                (TimestampUtc, LocalTimestamp, UserId, UserRole, StationId,
                 ActionCategory, ActionDetail, RelatedEntityType, RelatedEntityId, RelatedPath)
            VALUES
                ($timestampUtc, $localTimestamp, $userId, $userRole, $stationId,
                 $actionCategory, $actionDetail, $relatedEntityType, $relatedEntityId, $relatedPath);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$timestampUtc", timestampUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$localTimestamp", localTimestamp.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$userId", effectiveUserId);
        command.Parameters.AddWithValue("$userRole", effectiveRole);
        command.Parameters.AddWithValue("$stationId", effectiveStation);
        command.Parameters.AddWithValue("$actionCategory", string.IsNullOrWhiteSpace(actionCategory) ? "UNKNOWN" : actionCategory);
        command.Parameters.AddWithValue("$actionDetail", string.IsNullOrWhiteSpace(actionDetail) ? string.Empty : actionDetail);
        command.Parameters.AddWithValue("$relatedEntityType", relatedEntityType);
        command.Parameters.AddWithValue("$relatedEntityId", relatedEntityId);
        command.Parameters.AddWithValue("$relatedPath", relatedPath);
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    public static void UpsertLocalUserMetadata(string userId, string role, bool isDisabled, DateTime createdAtUtc, string createdBy, DateTime? updatedAtUtc = null)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO LocalUsers (UserId, Role, IsDisabled, CreatedAtUtc, CreatedBy, UpdatedAtUtc)
            VALUES ($userId, $role, $isDisabled, $createdAtUtc, $createdBy, $updatedAtUtc)
            ON CONFLICT(UserId) DO UPDATE SET
                Role = excluded.Role,
                IsDisabled = excluded.IsDisabled,
                CreatedAtUtc = excluded.CreatedAtUtc,
                CreatedBy = excluded.CreatedBy,
                UpdatedAtUtc = excluded.UpdatedAtUtc;
            """;
        command.Parameters.AddWithValue("$userId", userId);
        command.Parameters.AddWithValue("$role", role);
        command.Parameters.AddWithValue("$isDisabled", isDisabled ? 1 : 0);
        command.Parameters.AddWithValue("$createdAtUtc", createdAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$createdBy", createdBy);
        command.Parameters.AddWithValue("$updatedAtUtc", (updatedAtUtc ?? DateTime.UtcNow).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    public static void MarkLocalUserDeleted(string userId, string operatorWithRole)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE LocalUsers
            SET IsDeleted = 1,
                IsDisabled = 1,
                UpdatedAtUtc = $updatedAtUtc
            WHERE UserId = $userId;
            """;
        command.Parameters.AddWithValue("$userId", userId);
        command.Parameters.AddWithValue("$updatedAtUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
        RecordAuditEvent("LOCAL_USER_DELETE", $"Local user deleted: {userId}.", operatorWithRole: operatorWithRole, relatedEntityType: "LocalUser", relatedEntityId: userId);
    }

    public static long RecordLocalUserSession(string userId, string role, string authenticationMode, bool success, string message)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO LocalUserSessions
                (UserId, UserRole, AuthenticationMode, LoginAtUtc, Success, Message)
            VALUES
                ($userId, $userRole, $authenticationMode, $loginAtUtc, $success, $message);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$userId", userId);
        command.Parameters.AddWithValue("$userRole", role);
        command.Parameters.AddWithValue("$authenticationMode", authenticationMode);
        command.Parameters.AddWithValue("$loginAtUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$success", success ? 1 : 0);
        command.Parameters.AddWithValue("$message", message);
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    public static void RecordTrainingSample(string sourcePath, string label, string notes)
    {
        EnsureInitialized();

        var targetPath = sourcePath;
        if (File.Exists(sourcePath) && !IsUnderDirectory(sourcePath, TrainingVaultPath))
        {
            var targetName = MakeVaultFileName(DateTime.UtcNow, "training", Path.GetFileName(sourcePath));
            targetPath = Path.Combine(TrainingVaultPath, targetName);
            File.Copy(sourcePath, targetPath, overwrite: true);
        }

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO TrainingSamples (SourceImagePath, VaultPath, Label, Notes, CreatedAtUtc)
            VALUES ($sourceImagePath, $vaultPath, $label, $notes, $createdAtUtc);
            """;
        command.Parameters.AddWithValue("$sourceImagePath", sourcePath);
        command.Parameters.AddWithValue("$vaultPath", targetPath);
        command.Parameters.AddWithValue("$label", label);
        command.Parameters.AddWithValue("$notes", notes);
        command.Parameters.AddWithValue("$createdAtUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

}
