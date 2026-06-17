namespace AOI_Monitor.Services;

public enum UserRole
{
    Operator,
    Engineer,
    Admin,
}

public static class RoleAuthorization
{
    public static bool CanEditRecipes(UserRole role) => role >= UserRole.Engineer;
    public static bool CanEditCalibration(UserRole role) => role >= UserRole.Engineer;
    public static bool CanRunModelTests(UserRole role) => role >= UserRole.Engineer;
    public static bool CanTestModelConfiguration(UserRole role) => role >= UserRole.Engineer;
    public static bool CanChangeThresholds(UserRole role) => role >= UserRole.Engineer;
    public static bool CanExportLogs(UserRole role) => role >= UserRole.Admin;
    public static bool CanManageSettings(UserRole role) => role >= UserRole.Admin;
    public static bool CanUseMaintenanceActions(UserRole role) => role >= UserRole.Admin;

    public static bool CanAccessPage(UserRole role, string pageKey)
    {
        return pageKey switch
        {
            "recipe" => CanEditRecipes(role),
            "calibration" => CanEditCalibration(role),
            "modeltest" => CanRunModelTests(role),
            "reports" or "spc" => CanExportLogs(role),
            "settings" => role >= UserRole.Engineer,
            "install" => CanManageSettings(role),
            _ => true,
        };
    }

    public static string DeniedMessage(UserRole role, string action)
        => $"{action} requires a higher local role. Current role: {role}.";
}
