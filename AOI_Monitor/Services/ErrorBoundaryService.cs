namespace AOI_Monitor.Services;

public static class ErrorBoundaryService
{
    public static Task<UiErrorBoundaryResult> RunAsync(
        string operationName,
        string currentPage,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
        => UiErrorBoundaryService.RunAsync(operationName, currentPage, operation, cancellationToken);

    public static async Task<UiErrorBoundaryResult> RunRecoverableAsync(
        string operationName,
        string currentPage,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken,
        Action<string>? showWarning = null)
    {
        var result = await RunAsync(operationName, currentPage, operation, cancellationToken).ConfigureAwait(true);
        if (!result.Success)
            showWarning?.Invoke(result.OperatorMessage);

        return result;
    }

    public static async Task<UiErrorBoundaryResult> SafeAsyncCommand(
        string operationName,
        string currentPage,
        Func<CancellationToken, Task> operation,
        Action<bool>? setRunning = null,
        Action<string>? showWarning = null,
        CancellationToken cancellationToken = default)
    {
        setRunning?.Invoke(true);
        try
        {
            var result = await RunRecoverableAsync(
                operationName,
                currentPage,
                operation,
                cancellationToken,
                showWarning).ConfigureAwait(true);

            WorkflowState.Instance.AddEvent(
                result.Success ? "ASYNC_COMMAND" : "RECOVERABLE_ERROR",
                result.Success
                    ? $"{operationName} completed."
                    : $"{operationName} failed safely. Crash report: {result.ReportPath}");

            return result;
        }
        finally
        {
            setRunning?.Invoke(false);
        }
    }
}
