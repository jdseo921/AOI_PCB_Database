namespace AOI_Monitor.Models;

public sealed class LightingSettings
{
    public string Mode { get; set; } = LightingModes.None;
    public string TcpHost { get; set; } = string.Empty;
    public int TcpPort { get; set; } = 5025;
    public string SerialPortName { get; set; } = string.Empty;
    public int BaudRate { get; set; } = 9600;
    public string TopProgram { get; set; } = "TOP";
    public string SideProgram { get; set; } = "SIDE";
    public string BottomProgram { get; set; } = "BOTTOM";
    public string CommandTemplate { get; set; } = "SET {view} {program}\\n";
    public int ResponseTimeoutMs { get; set; } = 500;

    public bool IsSimulated =>
        string.Equals(Mode, LightingModes.Simulated, StringComparison.OrdinalIgnoreCase);
}

public static class LightingModes
{
    public const string None = "none";
    public const string Simulated = "simulated";
    public const string TcpText = "tcp-text";
    public const string SerialText = "serial-text";
}
