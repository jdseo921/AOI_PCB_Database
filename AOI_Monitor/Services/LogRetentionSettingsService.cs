using System.IO;
using System.Text.Json;
using AOI_Monitor.Data;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public sealed class LogRetentionSettings
{
    public bool Enabled { get; set; } = true;
    public int RetentionDays { get; set; } = 30;
    public bool WarningEnabled { get; set; } = true;
    public int WarningLeadDays { get; set; } = 7;
}

/// <summary>
/// Persists the log-retention policy (archive-then-purge) as a local JSON settings file, mirroring
/// the other per-concern settings services. Defaults are compliant with the "archive logs older than
/// 30 days" requirement and can be changed or disabled by an administrator.
/// </summary>
public static class LogRetentionSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private static LogRetentionSettings? _cached;

    public static event Action? SettingsChanged;

    public static string SettingsPath => Path.Combine(AoiDatabase.StorageRoot, "log_retention_settings.json");

    public static LogRetentionSettings LoadSettings()
    {
        if (_cached is not null)
            return Clone(_cached);

        Directory.CreateDirectory(AoiDatabase.StorageRoot);
        if (!File.Exists(SettingsPath))
        {
            _cached = new LogRetentionSettings();
            Save(_cached, "SYSTEM", notify: false);
            return Clone(_cached);
        }

        try
        {
            _cached = JsonSerializer.Deserialize<LogRetentionSettings>(File.ReadAllText(SettingsPath), JsonOptions) ?? new LogRetentionSettings();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"Log retention settings load fallback used: {ex.Message}");
            _cached = new LogRetentionSettings();
        }

        Normalize(_cached);
        return Clone(_cached);
    }

    public static LogRetentionPolicy LoadPolicy()
    {
        var settings = LoadSettings();
        return new LogRetentionPolicy
        {
            Enabled = settings.Enabled,
            RetentionDays = settings.RetentionDays,
            WarningLeadDays = settings.WarningLeadDays,
        };
    }

    public static void Save(LogRetentionSettings settings, string operatorId = "UNKNOWN")
        => Save(settings, operatorId, notify: true);

    public static void ResetForTests()
    {
        _cached = null;
        SettingsChanged = null;
    }

    private static void Save(LogRetentionSettings settings, string operatorId, bool notify)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Normalize(settings);
        Directory.CreateDirectory(AoiDatabase.StorageRoot);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
        _cached = Clone(settings);
        AoiDatabase.RecordAuditEvent(
            "LOG_RETENTION",
            $"Log retention policy set: enabled={settings.Enabled}; retentionDays={settings.RetentionDays}; warning={settings.WarningEnabled}/{settings.WarningLeadDays}d.",
            operatorWithRole: operatorId,
            relatedEntityType: "LogRetention",
            relatedEntityId: settings.RetentionDays.ToString());
        if (notify)
            SettingsChanged?.Invoke();
    }

    private static void Normalize(LogRetentionSettings settings)
    {
        settings.RetentionDays = Math.Clamp(settings.RetentionDays, 1, 3650);
        settings.WarningLeadDays = Math.Clamp(settings.WarningLeadDays, 0, settings.RetentionDays);
    }

    private static LogRetentionSettings Clone(LogRetentionSettings source) => new()
    {
        Enabled = source.Enabled,
        RetentionDays = source.RetentionDays,
        WarningEnabled = source.WarningEnabled,
        WarningLeadDays = source.WarningLeadDays,
    };
}

/// <summary>
/// Runs log retention once at application startup using the configured policy. Failures are
/// contained so a retention problem can never block the application from starting.
/// </summary>
public static class LogRetentionService
{
    public static LogRetentionResult RunStartupRetention()
    {
        try
        {
            var policy = LogRetentionSettingsService.LoadPolicy();
            if (!policy.Enabled)
                return new LogRetentionResult(0, 0);

            var result = AoiDatabase.RunLogRetention(policy);
            if (result.PurgedRowCount > 0)
            {
                AoiDatabase.RecordAuditEvent(
                    "LOG_RETENTION",
                    $"Startup log retention archived {result.ArchivedRowCount} row(s) and purged {result.PurgedRowCount} row(s) older than {policy.RetentionDays} day(s).",
                    operatorWithRole: "SYSTEM",
                    relatedEntityType: "LogRetention",
                    relatedEntityId: policy.RetentionDays.ToString());
            }

            return result;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"Startup log retention skipped after error: {ex.Message}");
            return new LogRetentionResult(0, 0);
        }
    }
}
