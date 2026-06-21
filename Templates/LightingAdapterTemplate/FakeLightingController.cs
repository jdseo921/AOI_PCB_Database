using AOI_Monitor.Models;
using AOI_Monitor.Services;

namespace LightingAdapterTemplate;

public sealed class FakeLightingControllerFactory : ILightingControllerFactory
{
    public string DriverId => "template.fake-lighting";
    public string DisplayName => "Template Fake Lighting Controller";
    public string Version => "0.1.0";
    public IReadOnlyList<string> SupportedModes => new[] { "template-fake", LightingModes.Simulated };

    public ILightingController Create(LightingSettings settings)
        => new FakeLightingController(settings);
}

public sealed class FakeLightingController : ILightingController
{
    private readonly LightingSettings _settings;
    private readonly Dictionary<string, string> _selectedPrograms = new(StringComparer.OrdinalIgnoreCase);

    public FakeLightingController(LightingSettings settings)
    {
        _settings = settings;
    }

    public string Name => "Template Fake Lighting Controller";
    public IntegrationConnectionStatus Status => IntegrationConnectionStatus.Simulated;
    public string StatusMessage => "Template fake lighting controller. No real strobe or illumination controller is connected.";

    public Task<IntegrationCommandResult> SetProgramAsync(string viewType, string programName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var view = string.IsNullOrWhiteSpace(viewType) ? "Top" : viewType.Trim();
        var program = string.IsNullOrWhiteSpace(programName) ? LightingCommandFormatter.ProgramForView(_settings, view) : programName.Trim();

        // Vendor SDK calls belong here, for example:
        // send TCP/serial command, call Cognex/Keyence/Basler lighting API,
        // wait for acknowledgement, and surface timeout/error details.
        _selectedPrograms[view] = program;
        return Task.FromResult(new IntegrationCommandResult(
            true,
            IntegrationConnectionStatus.Simulated,
            $"Template fake lighting selected {view} -> {program}. No real hardware command was sent."));
    }
}
