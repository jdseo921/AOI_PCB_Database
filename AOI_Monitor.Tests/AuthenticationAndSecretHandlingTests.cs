using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Xunit;

namespace AOI_Monitor.Tests;

public sealed class AuthenticationAndSecretHandlingTests : IDisposable
{
    private readonly string _root;

    public AuthenticationAndSecretHandlingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AOI_Monitor_AuthSecret_Tests", Guid.NewGuid().ToString("N"));
        AoiDatabase.ConfigureStorageRoot(_root);
        AuthenticationSettingsService.ResetForTests();
        DeploymentProfileSettingsService.ResetForTests();
        FirstRunSettingsService.ResetForTests();
    }

    public void Dispose()
    {
        AuthenticationSettingsService.ResetForTests();
        DeploymentProfileSettingsService.ResetForTests();
        FirstRunSettingsService.ResetForTests();
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void LocalUserPasswordHashIsNotPlaintext()
    {
        AoiDatabase.Initialize();
        const string password = "CorrectHorseBatteryStaple!";

        var user = LocalUserService.CreateUser("AdminLocal", UserRole.Admin, password, UserRole.Admin, "RootAdmin [Admin]");
        var userStoreJson = File.ReadAllText(AuthenticationSettingsService.LocalUsersPath);

        Assert.NotEqual(password, user.PasswordHash);
        Assert.DoesNotContain(password, userStoreJson, StringComparison.Ordinal);
        Assert.True(LocalUserService.TryAuthenticate("AdminLocal", password, out var authenticated));
        Assert.Equal(UserRole.Admin, authenticated.Role);
        Assert.False(LocalUserService.TryAuthenticate("AdminLocal", "wrong-password", out _));
    }

    [Fact]
    public void OperatorCannotAccessEngineerOrAdminLocalUserActions()
    {
        AoiDatabase.Initialize();

        Assert.Throws<UnauthorizedAccessException>(() =>
            LocalUserService.CreateUser("BlockedAdmin", UserRole.Admin, "CorrectHorseBatteryStaple!", UserRole.Operator, "Operator01 [Operator]"));
        Assert.Throws<UnauthorizedAccessException>(() =>
            LocalUserService.DisableUser("AnyUser", UserRole.Operator, "Operator01 [Operator]"));
        Assert.Throws<UnauthorizedAccessException>(() =>
            LocalUserService.ChangePassword("AnyUser", "CorrectHorseBatteryStaple!", UserRole.Operator, "Operator01 [Operator]"));
        Assert.Throws<UnauthorizedAccessException>(() =>
            LocalUserService.DeleteUser("AnyUser", UserRole.Operator, "Operator01 [Operator]"));
    }

    [Fact]
    public void DisabledUserCannotLogIn()
    {
        AoiDatabase.Initialize();
        const string password = "CorrectHorseBatteryStaple!";

        LocalUserService.CreateUser("EngineerLocal", UserRole.Engineer, password, UserRole.Admin, "RootAdmin [Admin]");

        Assert.True(LocalUserService.TryAuthenticate("EngineerLocal", password, out var authenticated));
        Assert.Equal(UserRole.Engineer, authenticated.Role);

        LocalUserService.DisableUser("EngineerLocal", UserRole.Admin, "RootAdmin [Admin]", "Offboarded test user");

        Assert.False(LocalUserService.TryAuthenticate("EngineerLocal", password, out _));
    }

    [Fact]
    public void SecretsDoNotAppearInSettingsAuditBackupOrReadinessExports()
    {
        AoiDatabase.Initialize();
        const string apiKey = "secret-api-key-123";
        const string bearer = "secret-bearer-token-456";
        const string password = "secret-basic-password-789";
        const string centralSecret = "secret-central-sync-abc";

        var mesSettings = new MesIntegrationSettings
        {
            Mode = MesIntegrationMode.Rest,
            BaseUrl = "https://mes.example.test",
            UploadResultPath = "/api/aoi/results",
            UploadImagePath = "/api/aoi/images",
            AuthMode = MesRestAuthMode.ApiKey,
            ApiKeyHeaderName = "X-API-Key",
            ApiKey = apiKey,
            BearerToken = bearer,
            Username = "mes-user",
            Password = password,
        };
        MesIntegrationSettingsService.Save(mesSettings);
        var centralSettings = new CentralSyncSettings
        {
            Mode = CentralSyncMode.RestApi,
            EndpointOrFolder = "https://central.example.test/sync",
            StationId = "AOI-TEST",
            SharedSecret = centralSecret,
            RedactEndpointInExports = true,
        };
        CentralSyncSettingsService.Save(centralSettings);
        AoiDatabase.RecordAuditEvent("MES_SETTINGS", MesIntegrationSettingsService.RedactedSummary(mesSettings), operatorWithRole: "Admin01 [Admin]");
        AoiDatabase.RecordAuditEvent("CENTRAL_SYNC_SETTINGS", CentralSyncSettingsService.RedactedSummary(centralSettings), operatorWithRole: "Admin01 [Admin]");

        var mesFile = File.ReadAllText(MesIntegrationSettingsService.SettingsPath);
        var centralFile = File.ReadAllText(CentralSyncSettingsService.SettingsPath);
        var backup = ConfigurationBackupService.Export(Path.Combine(_root, "backup"), "Admin01 [Admin]");
        var readiness = FactoryReadinessService.ExportGoNoGoPackage(outputRoot: Path.Combine(_root, "readiness"));
        var auditText = string.Join(Environment.NewLine, AoiDatabase.GetAuditEvents(new LogFilter()).Select(audit => audit.ActionDetail));
        var exportText = File.ReadAllText(backup.BackupPath) +
                         File.ReadAllText(readiness.SummaryJsonPath) +
                         File.ReadAllText(readiness.SummaryHtmlPath);

        foreach (var secret in new[] { apiKey, bearer, password, centralSecret })
        {
            Assert.DoesNotContain(secret, mesFile, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, centralFile, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, auditText, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, exportText, StringComparison.Ordinal);
        }

        Assert.Contains(SecretProtectionService.ProtectedPrefix, mesFile);
        Assert.Contains(SecretProtectionService.ProtectedPrefix, centralFile);
    }

    [Fact]
    public void DemoAuthenticationModeProducesReadinessWarning()
    {
        AoiDatabase.Initialize();
        AuthenticationSettingsService.Save(new AuthenticationSettings { Mode = AuthenticationMode.DemoLocalRoleSelector }, "Admin01 [Admin]");

        var report = FactoryReadinessService.Evaluate(FactoryReadinessService.CriteriaForProfile(DeploymentProfile.Stage1ImageValidation));
        var category = Assert.Single(report.Categories, item => item.Name == "Authentication mode");

        Assert.Equal("Conditional", category.Status);
        Assert.Contains("DemoLocalRoleSelector", category.Evidence);
        Assert.Contains(report.Warnings, warning => warning.Contains("DemoLocalRoleSelector", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AuditRecordsIncludeUserIdAndRole()
    {
        AoiDatabase.Initialize();

        AoiDatabase.RecordAuditEvent("UNIT_AUTH_AUDIT", "Authentication audit accountability test.", operatorWithRole: "EngineerAudit [Engineer]");

        var audit = Assert.Single(AoiDatabase.GetAuditEvents(new LogFilter { ActionCategory = "UNIT_AUTH_AUDIT" }));
        Assert.Equal("EngineerAudit", audit.UserId);
        Assert.Equal("Engineer", audit.UserRole);
    }
}
