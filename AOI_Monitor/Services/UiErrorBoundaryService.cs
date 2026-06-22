namespace AOI_Monitor.Services;

public sealed record UiErrorBoundaryResult(bool Success, string ReportPath, string OperatorMessage);

public static class UiErrorBoundaryService
{
    public static async Task<UiErrorBoundaryResult> RunAsync(
        string operationName,
        string currentPage,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            await operation(cancellationToken).ConfigureAwait(true);
            return new UiErrorBoundaryResult(true, string.Empty, string.Empty);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var report = CrashReportService.WriteReport(new CrashReportRequest
            {
                Exception = ex,
                OperationName = operationName,
                CurrentPage = currentPage,
                IsFatal = false,
                IsUiThread = true,
            });
            return new UiErrorBoundaryResult(false, report.ReportPath, report.OperatorMessage);
        }
    }
}
