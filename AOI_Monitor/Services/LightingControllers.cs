using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public static class LightingCommandFormatter
{
    public static string BuildCommand(string template, string viewType, string programName)
    {
        var view = string.IsNullOrWhiteSpace(viewType) ? "Top" : viewType.Trim();
        var program = string.IsNullOrWhiteSpace(programName) ? "DEFAULT" : programName.Trim();
        var command = (string.IsNullOrWhiteSpace(template) ? "SET {view} {program}\\n" : template)
            .Replace("{view}", view, StringComparison.OrdinalIgnoreCase)
            .Replace("{program}", program, StringComparison.OrdinalIgnoreCase);

        return command
            .Replace("\\r", "\r", StringComparison.Ordinal)
            .Replace("\\n", "\n", StringComparison.Ordinal);
    }

    public static string ProgramForView(LightingSettings settings, string viewType)
        => (viewType ?? "Top").Trim().ToLowerInvariant() switch
        {
            "side" => settings.SideProgram,
            "bottom" => settings.BottomProgram,
            _ => settings.TopProgram,
        };
}

public sealed class SimulatedLightingController : ILightingController
{
    private readonly LightingSettings _settings;
    private readonly Dictionary<string, string> _selectedPrograms = new(StringComparer.OrdinalIgnoreCase);

    public SimulatedLightingController(LightingSettings settings)
    {
        _settings = settings;
    }

    public string Name => "Simulated Lighting Controller";
    public IntegrationConnectionStatus Status => IntegrationConnectionStatus.Simulated;
    public string StatusMessage => "Lighting simulation only. No real lighting controller is connected.";
    public IReadOnlyDictionary<string, string> SelectedPrograms => _selectedPrograms;
    public string LastCommand { get; private set; } = string.Empty;

    public Task<IntegrationCommandResult> SetProgramAsync(
        string viewType,
        string programName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var view = string.IsNullOrWhiteSpace(viewType) ? "Top" : viewType.Trim();
        var program = string.IsNullOrWhiteSpace(programName) ? LightingCommandFormatter.ProgramForView(_settings, view) : programName.Trim();
        _selectedPrograms[view] = program;
        LastCommand = LightingCommandFormatter.BuildCommand(_settings.CommandTemplate, view, program);

        return Task.FromResult(new IntegrationCommandResult(
            true,
            IntegrationConnectionStatus.Simulated,
            $"Simulated lighting program selected: {view} -> {program}. No real lighting command was sent."));
    }
}

public sealed class TcpTextLightingController : ILightingController
{
    private readonly LightingSettings _settings;

    public TcpTextLightingController(LightingSettings settings)
    {
        _settings = settings;
    }

    public string Name => "TCP Text Lighting Controller";
    public IntegrationConnectionStatus Status => LightingSettingsService.Validate(_settings).Count == 0
        ? IntegrationConnectionStatus.Ready
        : IntegrationConnectionStatus.Error;
    public string StatusMessage => Status == IntegrationConnectionStatus.Ready
        ? $"TCP text lighting configured for {_settings.TcpHost}:{_settings.TcpPort}. Commands are sent only during explicit lighting sync."
        : string.Join(" ", LightingSettingsService.Validate(_settings));

    public async Task<IntegrationCommandResult> SetProgramAsync(
        string viewType,
        string programName,
        CancellationToken cancellationToken = default)
    {
        var errors = LightingSettingsService.Validate(_settings);
        if (errors.Count > 0)
            return new IntegrationCommandResult(false, IntegrationConnectionStatus.Error, string.Join(" ", errors));

        var command = LightingCommandFormatter.BuildCommand(_settings.CommandTemplate, viewType, programName);
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(_settings.ResponseTimeoutMs));
            using var client = new TcpClient();
            await client.ConnectAsync(_settings.TcpHost, _settings.TcpPort, timeoutCts.Token).ConfigureAwait(false);
            await using var stream = client.GetStream();
            var bytes = Encoding.ASCII.GetBytes(command);
            await stream.WriteAsync(bytes, timeoutCts.Token).ConfigureAwait(false);
            await stream.FlushAsync(timeoutCts.Token).ConfigureAwait(false);

            return new IntegrationCommandResult(
                true,
                IntegrationConnectionStatus.Ready,
                $"TCP lighting command sent: {viewType} -> {programName}.");
        }
        catch (OperationCanceledException)
        {
            return new IntegrationCommandResult(false, IntegrationConnectionStatus.Error, $"TCP lighting command timed out after {_settings.ResponseTimeoutMs} ms.");
        }
        catch (Exception ex) when (ex is SocketException or IOException or InvalidOperationException)
        {
            return new IntegrationCommandResult(false, IntegrationConnectionStatus.Error, $"TCP lighting command failed: {ex.Message}");
        }
    }
}

public sealed class SerialTextLightingController : ILightingController
{
    private readonly LightingSettings _settings;

    public SerialTextLightingController(LightingSettings settings)
    {
        _settings = settings;
    }

    public string Name => "Serial Text Lighting Controller";
    public IntegrationConnectionStatus Status => LightingSettingsService.Validate(_settings).Count == 0
        ? IntegrationConnectionStatus.Ready
        : IntegrationConnectionStatus.Error;
    public string StatusMessage => Status == IntegrationConnectionStatus.Ready
        ? $"Serial text lighting configured for {_settings.SerialPortName} at {_settings.BaudRate.ToString(CultureInfo.InvariantCulture)} baud. Commands are sent only during explicit lighting sync."
        : string.Join(" ", LightingSettingsService.Validate(_settings));

    public Task<IntegrationCommandResult> SetProgramAsync(
        string viewType,
        string programName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var errors = LightingSettingsService.Validate(_settings);
        if (errors.Count > 0)
            return Task.FromResult(new IntegrationCommandResult(false, IntegrationConnectionStatus.Error, string.Join(" ", errors)));

        var serialPortType = Type.GetType("System.IO.Ports.SerialPort, System.IO.Ports");
        if (serialPortType is null)
        {
            return Task.FromResult(new IntegrationCommandResult(
                false,
                IntegrationConnectionStatus.Error,
                "Serial lighting mode requires System.IO.Ports at runtime. The app did not add a serial dependency because no hardware adapter is bundled."));
        }

        var command = LightingCommandFormatter.BuildCommand(_settings.CommandTemplate, viewType, programName);
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(_settings.ResponseTimeoutMs));
            timeoutCts.Token.ThrowIfCancellationRequested();

            using var serialPort = (IDisposable?)Activator.CreateInstance(serialPortType, _settings.SerialPortName, _settings.BaudRate);
            if (serialPort is null)
                return Task.FromResult(new IntegrationCommandResult(false, IntegrationConnectionStatus.Error, "Serial lighting controller could not create a SerialPort instance."));

            SetSerialProperty(serialPortType, serialPort, "ReadTimeout", _settings.ResponseTimeoutMs);
            SetSerialProperty(serialPortType, serialPort, "WriteTimeout", _settings.ResponseTimeoutMs);
            serialPortType.GetMethod("Open", Type.EmptyTypes)?.Invoke(serialPort, null);
            timeoutCts.Token.ThrowIfCancellationRequested();
            serialPortType.GetMethod("Write", new[] { typeof(string) })?.Invoke(serialPort, new object[] { command });
            serialPortType.GetMethod("Close", Type.EmptyTypes)?.Invoke(serialPort, null);

            return Task.FromResult(new IntegrationCommandResult(
                true,
                IntegrationConnectionStatus.Ready,
                $"Serial lighting command sent: {viewType} -> {programName}."));
        }
        catch (OperationCanceledException)
        {
            return Task.FromResult(new IntegrationCommandResult(false, IntegrationConnectionStatus.Error, $"Serial lighting command timed out after {_settings.ResponseTimeoutMs} ms."));
        }
        catch (Exception ex) when (ex is TargetInvocationException or IOException or InvalidOperationException or ArgumentException)
        {
            var message = ex is TargetInvocationException { InnerException: not null } target
                ? target.InnerException!.Message
                : ex.Message;
            return Task.FromResult(new IntegrationCommandResult(false, IntegrationConnectionStatus.Error, $"Serial lighting command failed: {message}"));
        }
    }

    private static void SetSerialProperty(Type serialPortType, object serialPort, string propertyName, int value)
        => serialPortType.GetProperty(propertyName)?.SetValue(serialPort, value);
}
