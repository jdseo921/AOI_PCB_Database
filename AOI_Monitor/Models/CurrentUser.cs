using AOI_Monitor.Services;

namespace AOI_Monitor.Models;

public sealed class CurrentUser
{
    public string UserId { get; set; } = "Engineer01";
    public UserRole Role { get; set; } = UserRole.Operator;
    public AuthenticationMode AuthenticationMode { get; set; } = AuthenticationMode.DemoLocalRoleSelector;

    public string AuditId => $"{UserId} [{Role}]";
}

public enum AuthenticationMode
{
    DemoLocalRoleSelector,
    LocalUsers,
    MesAuthenticationBoundary,
}
