using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public static class LocalUserService
{
    public static LocalUserRecord CreateUser(string userId, UserRole role, string password, UserRole actingRole, string createdBy)
        => AuthenticationSettingsService.CreateUser(userId, role, password, actingRole, createdBy);

    public static bool TryAuthenticate(string userId, string password, out LocalUserRecord user)
        => AuthenticationSettingsService.TryAuthenticate(userId, password, out user);

    public static IReadOnlyList<LocalUserRecord> GetUsers()
        => AuthenticationSettingsService.GetUsers();

    public static LocalUserRecord DisableUser(string userId, UserRole actingRole, string operatorWithRole, string reason = "")
        => AuthenticationSettingsService.DisableUser(userId, actingRole, operatorWithRole, reason);

    public static void DeleteUser(string userId, UserRole actingRole, string operatorWithRole)
        => AuthenticationSettingsService.DeleteUser(userId, actingRole, operatorWithRole);

    public static LocalUserRecord ChangePassword(string userId, string newPassword, UserRole actingRole, string operatorWithRole)
        => AuthenticationSettingsService.ChangePassword(userId, newPassword, actingRole, operatorWithRole);
}
