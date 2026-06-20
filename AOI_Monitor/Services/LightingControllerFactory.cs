using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public static class LightingControllerFactory
{
    public static ILightingController Create(LightingSettings settings)
        => LightingSettingsService.NormalizeMode(settings.Mode) switch
        {
            LightingModes.Simulated => new SimulatedLightingController(settings),
            LightingModes.TcpText => new TcpTextLightingController(settings),
            LightingModes.SerialText => new SerialTextLightingController(settings),
            _ => new NullLightingController(),
        };

    public static void ApplyActiveController()
        => IntegrationBoundaryRegistry.LightingController = Create(LightingSettingsService.Load());
}

public static class LightingSynchronizationService
{
    public static async Task<IntegrationCommandResult> SynchronizeAsync(
        ILightingController controller,
        LightingSettings settings,
        string viewType,
        CancellationToken cancellationToken = default)
    {
        var program = LightingCommandFormatter.ProgramForView(settings, viewType);
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(settings.ResponseTimeoutMs));
            return await controller.SetProgramAsync(viewType, program, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new IntegrationCommandResult(false, IntegrationConnectionStatus.Error, $"Lighting sync timed out after {settings.ResponseTimeoutMs} ms.");
        }
        catch (Exception ex)
        {
            return new IntegrationCommandResult(false, IntegrationConnectionStatus.Error, $"Lighting sync failed safely: {ex.Message}");
        }
    }
}
