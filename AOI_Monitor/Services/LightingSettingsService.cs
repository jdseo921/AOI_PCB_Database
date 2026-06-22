using System.IO;
using System.Text.Json;
using AOI_Monitor.Data;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public static class LightingSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static LightingSettings? _cached;

    public static event Action? SettingsChanged;

    public static string SettingsPath => Path.Combine(AoiDatabase.StorageRoot, "lighting_settings.json");

    public static LightingSettings Load()
    {
        if (_cached is not null)
            return Clone(_cached);

        Directory.CreateDirectory(AoiDatabase.StorageRoot);
        if (!File.Exists(SettingsPath))
        {
            _cached = new LightingSettings();
            Save(_cached, notify: false);
            return Clone(_cached);
        }

        try
        {
            var json = File.ReadAllText(SettingsPath);
            _cached = JsonSerializer.Deserialize<LightingSettings>(json) ?? new LightingSettings();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"Lighting settings load fallback used: {ex.Message}");
            _cached = new LightingSettings();
        }

        Normalize(_cached);
        return Clone(_cached);
    }

    public static void Save(LightingSettings settings)
        => Save(settings, notify: true);

    public static void ApplyIntegrationBoundary()
        => LightingControllerFactory.ApplyActiveController();

    public static IReadOnlyList<string> Validate(LightingSettings settings)
    {
        var mode = NormalizeMode(settings.Mode);
        var errors = new List<string>();
        if (mode == LightingModes.TcpText)
        {
            if (string.IsNullOrWhiteSpace(settings.TcpHost))
                errors.Add("TCP lighting mode requires a host.");
            if (settings.TcpPort is < 1 or > 65535)
                errors.Add("TCP lighting port must be between 1 and 65535.");
        }

        if (mode == LightingModes.SerialText)
        {
            if (string.IsNullOrWhiteSpace(settings.SerialPortName))
                errors.Add("Serial lighting mode requires a serial port name.");
            if (settings.BaudRate is < 1 or > 10000000)
                errors.Add("Serial lighting baud rate must be between 1 and 10000000.");
        }

        if (settings.ResponseTimeoutMs is < 1 or > 60000)
            errors.Add("Lighting response timeout must be between 1 and 60000 ms.");

        if (string.IsNullOrWhiteSpace(settings.CommandTemplate) ||
            !settings.CommandTemplate.Contains("{program}", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("Lighting command template must include {program}.");
        }

        return errors;
    }

    private static void Save(LightingSettings settings, bool notify)
    {
        Normalize(settings);
        Directory.CreateDirectory(AoiDatabase.StorageRoot);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
        _cached = Clone(settings);

        if (notify)
            SettingsChanged?.Invoke();
    }

    private static void Normalize(LightingSettings settings)
    {
        settings.Mode = NormalizeMode(settings.Mode);
        settings.TcpHost = settings.TcpHost?.Trim() ?? string.Empty;
        settings.TcpPort = Math.Clamp(settings.TcpPort <= 0 ? 5025 : settings.TcpPort, 1, 65535);
        settings.SerialPortName = settings.SerialPortName?.Trim() ?? string.Empty;
        settings.BaudRate = Math.Clamp(settings.BaudRate <= 0 ? 9600 : settings.BaudRate, 1, 10000000);
        settings.TopProgram = string.IsNullOrWhiteSpace(settings.TopProgram) ? "TOP" : settings.TopProgram.Trim();
        settings.SideProgram = string.IsNullOrWhiteSpace(settings.SideProgram) ? "SIDE" : settings.SideProgram.Trim();
        settings.BottomProgram = string.IsNullOrWhiteSpace(settings.BottomProgram) ? "BOTTOM" : settings.BottomProgram.Trim();
        settings.CommandTemplate = string.IsNullOrWhiteSpace(settings.CommandTemplate)
            ? "SET {view} {program}\\n"
            : settings.CommandTemplate;
        settings.ResponseTimeoutMs = Math.Clamp(settings.ResponseTimeoutMs <= 0 ? 500 : settings.ResponseTimeoutMs, 1, 60000);
    }

    public static string NormalizeMode(string? mode)
        => string.IsNullOrWhiteSpace(mode)
            ? LightingModes.None
            : mode.Trim().ToLowerInvariant() switch
            {
                LightingModes.Simulated or "simulation" or "sim" => LightingModes.Simulated,
                LightingModes.TcpText or "tcp" or "ethernet" => LightingModes.TcpText,
                LightingModes.SerialText or "serial" or "rs232" => LightingModes.SerialText,
                _ => LightingModes.None,
            };

    private static LightingSettings Clone(LightingSettings source)
        => new()
        {
            Mode = source.Mode,
            TcpHost = source.TcpHost,
            TcpPort = source.TcpPort,
            SerialPortName = source.SerialPortName,
            BaudRate = source.BaudRate,
            TopProgram = source.TopProgram,
            SideProgram = source.SideProgram,
            BottomProgram = source.BottomProgram,
            CommandTemplate = source.CommandTemplate,
            ResponseTimeoutMs = source.ResponseTimeoutMs,
        };
}
