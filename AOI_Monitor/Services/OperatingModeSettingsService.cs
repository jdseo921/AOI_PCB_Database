using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AOI_Monitor.Data;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public sealed class OperatingModeSettings
{
    public OperatingMode Mode { get; set; } = OperatingMode.Demo;
    public string PilotAuthenticationWaiverReason { get; set; } = string.Empty;
    public string PilotAuthenticationWaivedBy { get; set; } = string.Empty;
    public DateTime? PilotAuthenticationWaiverExpiresAtUtc { get; set; }
}

public static class OperatingModeSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static OperatingModeSettings? _cached;

    public static event Action? SettingsChanged;

    public static string SettingsPath => Path.Combine(AoiDatabase.StorageRoot, "operating_mode_settings.json");

    public static OperatingMode Load()
        => LoadSettings().Mode;

    public static OperatingModeSettings LoadSettings()
    {
        if (_cached is not null)
            return Clone(_cached);

        Directory.CreateDirectory(AoiDatabase.StorageRoot);
        if (!File.Exists(SettingsPath))
        {
            _cached = new OperatingModeSettings();
            Save(_cached, "SYSTEM", notify: false);
            return Clone(_cached);
        }

        try
        {
            _cached = JsonSerializer.Deserialize<OperatingModeSettings>(File.ReadAllText(SettingsPath), JsonOptions) ?? new OperatingModeSettings();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"Operating mode settings load fallback used: {ex.Message}");
            _cached = new OperatingModeSettings();
        }

        Normalize(_cached);
        return Clone(_cached);
    }

    public static void Save(OperatingMode mode, string operatorId = "UNKNOWN")
    {
        var settings = LoadSettings();
        settings.Mode = mode;
        if (mode != OperatingMode.Pilot)
        {
            settings.PilotAuthenticationWaiverReason = string.Empty;
            settings.PilotAuthenticationWaivedBy = string.Empty;
            settings.PilotAuthenticationWaiverExpiresAtUtc = null;
        }

        Save(settings, operatorId, notify: true);
    }

    public static void RecordPilotAuthenticationWaiver(string reason, DateTime expiresAtUtc, UserRole role, string operatorId)
    {
        if (!RoleAuthorization.CanManageSettings(role))
            throw new UnauthorizedAccessException(RoleAuthorization.DeniedMessage(role, "recording Pilot authentication waiver"));
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("Waiver reason is required.", nameof(reason));
        if (expiresAtUtc.ToUniversalTime() <= DateTime.UtcNow)
            throw new ArgumentException("Waiver expiry must be in the future.", nameof(expiresAtUtc));

        var settings = LoadSettings();
        settings.Mode = OperatingMode.Pilot;
        settings.PilotAuthenticationWaiverReason = SecretProtectionService.RedactKnownSecrets(reason.Trim());
        settings.PilotAuthenticationWaivedBy = operatorId;
        settings.PilotAuthenticationWaiverExpiresAtUtc = expiresAtUtc.ToUniversalTime();
        Save(settings, operatorId, notify: true);
    }

    public static bool HasActivePilotAuthenticationWaiver()
    {
        var settings = LoadSettings();
        return settings.Mode == OperatingMode.Pilot &&
            !string.IsNullOrWhiteSpace(settings.PilotAuthenticationWaiverReason) &&
            settings.PilotAuthenticationWaiverExpiresAtUtc is { } expires &&
            expires.ToUniversalTime() > DateTime.UtcNow;
    }

    public static void ResetForTests()
    {
        _cached = null;
        SettingsChanged = null;
    }

    private static void Save(OperatingModeSettings settings, string operatorId, bool notify)
    {
        Normalize(settings);
        Directory.CreateDirectory(AoiDatabase.StorageRoot);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
        _cached = Clone(settings);
        AoiDatabase.RecordAuditEvent(
            "OPERATING_MODE",
            $"Operating mode set to {settings.Mode}; pilotAuthWaiverActive={HasActiveWaiver(settings)}.",
            operatorWithRole: operatorId,
            relatedEntityType: "OperatingMode",
            relatedEntityId: settings.Mode.ToString());
        if (notify)
            SettingsChanged?.Invoke();
    }

    private static bool HasActiveWaiver(OperatingModeSettings settings)
        => settings.PilotAuthenticationWaiverExpiresAtUtc is { } expires &&
            expires.ToUniversalTime() > DateTime.UtcNow &&
            !string.IsNullOrWhiteSpace(settings.PilotAuthenticationWaiverReason);

    private static void Normalize(OperatingModeSettings settings)
    {
        if (!Enum.IsDefined(settings.Mode))
            settings.Mode = OperatingMode.Demo;
        if (settings.Mode != OperatingMode.Pilot)
        {
            settings.PilotAuthenticationWaiverReason = string.Empty;
            settings.PilotAuthenticationWaivedBy = string.Empty;
            settings.PilotAuthenticationWaiverExpiresAtUtc = null;
        }
    }

    private static OperatingModeSettings Clone(OperatingModeSettings source)
        => new()
        {
            Mode = source.Mode,
            PilotAuthenticationWaiverReason = source.PilotAuthenticationWaiverReason,
            PilotAuthenticationWaivedBy = source.PilotAuthenticationWaivedBy,
            PilotAuthenticationWaiverExpiresAtUtc = source.PilotAuthenticationWaiverExpiresAtUtc,
        };
}

public static class OperatingModePolicyService
{
    public static bool IsDemoDataAllowed(OperatingMode? mode = null)
        => (mode ?? OperatingModeSettingsService.Load()) == OperatingMode.Demo;

    public static bool ShouldShowDemoRows(int realRowCount, OperatingMode? mode = null)
        => realRowCount == 0 && IsDemoDataAllowed(mode);

    public static bool IsDemoRoleSelectorAllowed(OperatingMode? mode = null)
        => (mode ?? OperatingModeSettingsService.Load()) == OperatingMode.Demo;

    public static bool IsSimulatedSourceAllowed(OperatingMode? mode = null)
        => (mode ?? OperatingModeSettingsService.Load()) != OperatingMode.Production;

    public static bool RequiresProductionReadinessGates(OperatingMode? mode = null)
        => (mode ?? OperatingModeSettingsService.Load()) == OperatingMode.Production;
}
