using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using AOI_Monitor.Data;
using AOI_Monitor.Models;

namespace AOI_Monitor.Services;

public sealed class CrashReportRequest
{
    public Exception Exception { get; init; } = new InvalidOperationException("Unknown error.");
    public string OperationName { get; init; } = "UNKNOWN";
    public string CurrentPage { get; init; } = "UNKNOWN";
    public bool IsFatal { get; init; }
    public bool IsUiThread { get; init; }
}

public sealed class CrashReportResult
{
    public string ReportPath { get; init; } = string.Empty;
    public string OperatorMessage { get; init; } = string.Empty;
}

public static class CrashReportService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string LastCrashReportPath { get; private set; } = string.Empty;

    public static CrashReportResult WriteReport(CrashReportRequest request)
    {
        try
        {
            var root = Path.Combine(AppContext.BaseDirectory, "exports", "crash_reports");
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, $"crash_report_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}.json");
            var report = BuildReport(request);
            File.WriteAllText(path, JsonSerializer.Serialize(report, JsonOptions));
            LastCrashReportPath = path;

            TryRecordWorkflowEvent(request, path);
            AlarmEventService.RaiseFromException(
                request.OperationName,
                request.CurrentPage,
                request.Exception,
                request.IsFatal ? AlarmSeverity.Critical : AlarmSeverity.Alarm,
                reportPath: path);
            PilotIssueService.CreateCrashIssue(
                request.CurrentPage,
                request.OperationName,
                request.Exception,
                path,
                WorkflowState.Instance.OperatorWithRole);

            return new CrashReportResult
            {
                ReportPath = path,
                OperatorMessage = PlainLanguageGlossaryService.SafeOperatorError(request.OperationName, path),
            };
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Crash report write failed; returning safe operator fallback: {ex.Message}");
            return new CrashReportResult
            {
                ReportPath = string.Empty,
                OperatorMessage = PlainLanguageGlossaryService.SafeOperatorError(request.OperationName),
            };
        }
    }

    public static string RedactDiagnosticText(string? value, bool redactCustomerImagePaths = true)
        => Redact(value, redactCustomerImagePaths);

    public static string RedactForTests(string value, bool redactCustomerImagePaths = true)
        => RedactDiagnosticText(value, redactCustomerImagePaths);

    public static void ClearForTests()
        => LastCrashReportPath = string.Empty;

    private static object BuildReport(CrashReportRequest request)
    {
        var state = WorkflowState.Instance;
        var exception = request.Exception;
        var deploymentProfile = Safe(() => DeploymentProfileSettingsService.Load().ToString(), "UNKNOWN");
        var memory = MemoryDiagnosticsService.Capture();

        return new
        {
            SchemaVersion = "aoi-monitor-crash/v1",
            GeneratedAtUtc = DateTime.UtcNow,
            AppVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0",
            CurrentPage = request.CurrentPage,
            OperationName = request.OperationName,
            IsFatal = request.IsFatal,
            Operator = state.OperatorWithRole,
            User = state.OperatorId,
            Role = state.CurrentRole.ToString(),
            DeploymentProfile = deploymentProfile,
            Exception = new
            {
                Type = exception.GetType().FullName,
                Message = Redact(exception.Message),
                StackTrace = Redact(exception.ToString()),
                InnerType = exception.InnerException?.GetType().FullName,
                InnerMessage = Redact(exception.InnerException?.Message),
            },
            WorkflowEvents = state.History.TakeLast(100).Select(entry => new
            {
                entry.Timestamp,
                entry.Category,
                Message = Redact(entry.Message),
            }),
            DatabasePath = Redact(AoiDatabase.DatabasePath),
            StorageRoot = Redact(AoiDatabase.StorageRoot),
            Memory = memory,
            Thread = new
            {
                ManagedThreadId = Environment.CurrentManagedThreadId,
                request.IsUiThread,
                ApartmentState = Safe(() => Thread.CurrentThread.GetApartmentState().ToString(), "UNKNOWN"),
            },
            RecentUiPerformance = UiPerformanceMonitorService.RecentEvents.TakeLast(25),
            RedactionStatus = new
            {
                SecretsRedacted = true,
                CustomerImagePathsRedacted = true,
                StorageRootRedacted = true,
                DatabasePathRedacted = true,
                MesSecretsRedacted = true,
                CentralSyncSecretsRedacted = true,
            },
        };
    }

    private static void TryRecordWorkflowEvent(CrashReportRequest request, string path)
    {
        try
        {
            WorkflowState.Instance.AddEvent(
                request.IsFatal ? "UNHANDLED_EXCEPTION" : "RECOVERABLE_ERROR",
                $"{request.OperationName} failed: {request.Exception.GetType().Name}: {RedactDiagnosticText(request.Exception.Message)}",
                relatedEntityType: "CrashReport",
                relatedPath: path);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Crash workflow event could not be recorded: {ex.Message}");
        }
    }

    private static string Redact(string? value, bool redactCustomerImagePaths = true)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var result = value;
        result = MesIntegrationSettingsService.RedactSecrets(result);
        result = CentralSyncSettingsService.RedactSecrets(result);

        foreach (var path in new[] { AoiDatabase.TrainingVaultPath, AoiDatabase.ImageVaultPath, AoiDatabase.StorageRoot }
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .OrderByDescending(path => path.Length))
        {
            result = result.Replace(path, path == AoiDatabase.StorageRoot ? "[STORAGE_ROOT]" : "[IMAGE_PATH]", StringComparison.OrdinalIgnoreCase);
        }

        if (redactCustomerImagePaths)
        {
            result = result.Replace(".png", ".[IMAGE]", StringComparison.OrdinalIgnoreCase)
                .Replace(".jpg", ".[IMAGE]", StringComparison.OrdinalIgnoreCase)
                .Replace(".jpeg", ".[IMAGE]", StringComparison.OrdinalIgnoreCase)
                .Replace(".bmp", ".[IMAGE]", StringComparison.OrdinalIgnoreCase)
                .Replace(".tif", ".[IMAGE]", StringComparison.OrdinalIgnoreCase)
                .Replace(".tiff", ".[IMAGE]", StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }

    private static T Safe<T>(Func<T> action, T fallback)
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Crash report safe fallback used: {ex.Message}");
            return fallback;
        }
    }
}
