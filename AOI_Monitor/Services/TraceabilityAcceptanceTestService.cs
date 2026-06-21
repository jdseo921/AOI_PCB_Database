using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

/// <summary>
/// Stage 4 MES/ERP acceptance harness entry point.
/// This wraps the existing traceability signoff implementation so future REST,
/// OPC UA, or customer-specific adapters can hang off one production-review name.
/// </summary>
public static class TraceabilityAcceptanceTestService
{
    public static async Task<TraceabilityTestReport> RunAsync(
        string? testImagePath = null,
        bool productionModeConfirmed = false,
        string? operatorId = null,
        CancellationToken cancellationToken = default)
        => await TraceabilitySignoffService.RunAsync(
            testImagePath,
            productionModeConfirmed,
            operatorId,
            cancellationToken).ConfigureAwait(false);

    public static Task<TraceabilityTestReport> RunResultOnlyAsync(
        string? operatorId = null,
        CancellationToken cancellationToken = default)
        => RunAsync(testImagePath: null, productionModeConfirmed: false, operatorId, cancellationToken);
}
