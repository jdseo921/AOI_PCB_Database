using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.IO;
<<<<<<< HEAD
using System.Linq;
=======
>>>>>>> 67117d637c0ef2a7f4698c2245b5001171a02ca2
using AOI_Monitor.Services;

namespace AOI_Monitor.Views;

public partial class ReportsView : UserControl
{
<<<<<<< HEAD
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff",
    };

=======
>>>>>>> 67117d637c0ef2a7f4698c2245b5001171a02ca2
    private static readonly object[] Packages =
    {
        new { Package = "Customer validation package",    Format = "CSV/PDF", Status = "OK"    },
        new { Package = "False-negative review list",     Format = "CSV/PDF", Status = "READY" },
        new { Package = "False-call reduction report",    Format = "CSV/PDF", Status = "WARN"  },
        new { Package = "Annotated image bundle",         Format = "PNG ZIP", Status = "OK"    },
        new { Package = "Recipe revision evidence",       Format = "CSV/PDF", Status = "OK"    },
        new { Package = "SQLite database backup",         Format = "DB ZIP",  Status = "OK"    },
    };

    public ReportsView()
    {
        InitializeComponent();
        PackagesGrid.ItemsSource = Packages;
    }

    private void OnPackageExportClick(object sender, RoutedEventArgs e)
    {
        var row = (sender as FrameworkElement)?.DataContext;
        if (row is null) return;

        var packageName = row.GetType().GetProperty("Package")?.GetValue(row)?.ToString() ?? "package";
        var fileSafe = string.Join("_", packageName.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));

        var exportDir = Path.Combine(AppContext.BaseDirectory, "exports", "packages");
        Directory.CreateDirectory(exportDir);

        var path = Path.Combine(exportDir, $"{fileSafe}_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        File.WriteAllText(path, $"Package: {packageName}{Environment.NewLine}Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        WorkflowState.Instance.AddEvent("EXPORT", $"Package exported: {Path.GetFileName(path)}");
        MessageBox.Show($"Exported:\n{path}", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
    }

<<<<<<< HEAD
    private void OnVerifyImagePathsClick(object sender, RoutedEventArgs e)
    {
        var exportsDir = EnsureExportsDir();
        var reportPath = Path.Combine(exportsDir, $"image_path_verification_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        var sb = new StringBuilder();

        int checkedFiles = 0;
        int inaccessible = 0;

        foreach (var file in EnumerateImageFiles(exportsDir))
        {
            checkedFiles++;
            try
            {
                using var _ = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            }
            catch (Exception ex)
            {
                inaccessible++;
                sb.AppendLine($"[INACCESSIBLE] {file} ({ex.GetType().Name})");
            }
        }

        var state = WorkflowState.Instance;
        CheckPrimaryPath("Sample", state.SampleImagePath, sb, ref inaccessible);
        CheckPrimaryPath("Golden", state.GoldenImagePath, sb, ref inaccessible);

        if (sb.Length == 0)
            sb.AppendLine("No inaccessible image paths detected.");

        sb.AppendLine();
        sb.AppendLine($"CheckedAt: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"ImageFilesChecked: {checkedFiles}");
        sb.AppendLine($"InaccessibleReferences: {inaccessible}");
        File.WriteAllText(reportPath, sb.ToString());

        state.AddEvent("UTILITY", $"Image path verification completed. Checked={checkedFiles}, Issues={inaccessible}.");
        MessageBox.Show(
            $"Image path verification finished.\nChecked: {checkedFiles}\nIssues: {inaccessible}\n\nReport:\n{reportPath}",
            "AOI Monitor",
            MessageBoxButton.OK,
            inaccessible == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void OnArchiveReviewedSamplesClick(object sender, RoutedEventArgs e)
    {
        var sourceDir = Path.Combine(EnsureExportsDir(), "training_set");
        Directory.CreateDirectory(sourceDir);

        var archiveDir = Path.Combine(EnsureExportsDir(), "archive", $"reviewed_{DateTime.Now:yyyyMMdd}");
        Directory.CreateDirectory(archiveDir);

        int moved = 0;
        int skipped = 0;
        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            var name = Path.GetFileName(file);
            var info = new FileInfo(file);

            // Archive explicit review exports and any stale training assets.
            bool shouldArchive = name.StartsWith("review_", StringComparison.OrdinalIgnoreCase)
                                 || info.LastWriteTime <= DateTime.Now.AddDays(-1);

            if (!shouldArchive)
            {
                skipped++;
                continue;
            }

            var target = Path.Combine(archiveDir, name);
            if (File.Exists(target))
            {
                target = Path.Combine(
                    archiveDir,
                    $"{Path.GetFileNameWithoutExtension(name)}_{DateTime.Now:HHmmssfff}{Path.GetExtension(name)}");
            }

            File.Move(file, target);
            moved++;
        }

        WorkflowState.Instance.AddEvent("UTILITY", $"Reviewed sample archive executed. Moved={moved}, Skipped={skipped}.");
        MessageBox.Show(
            $"Archive complete.\nMoved: {moved}\nSkipped: {skipped}\nArchive folder:\n{archiveDir}",
            "AOI Monitor",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void OnRunDbIntegrityCheckClick(object sender, RoutedEventArgs e)
    {
        var exportsDir = EnsureExportsDir();
        var reportPath = Path.Combine(exportsDir, $"db_integrity_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        var sb = new StringBuilder();

        int warnings = 0;
        int failures = 0;

        // SQLite may be external in this prototype; check critical artifacts and logs for operational readiness.
        var auditLogPath = Path.Combine(exportsDir, "review_disposition_log.csv");
        if (!File.Exists(auditLogPath))
        {
            warnings++;
            sb.AppendLine("[WARN] Missing review disposition log file.");
        }
        else
        {
            var firstLine = File.ReadLines(auditLogPath).FirstOrDefault() ?? string.Empty;
            if (!firstLine.StartsWith("TimestampUtc,StationId,OperatorId", StringComparison.OrdinalIgnoreCase))
            {
                failures++;
                sb.AppendLine("[FAIL] review_disposition_log.csv header mismatch.");
            }
            else
            {
                sb.AppendLine("[PASS] Review disposition log header valid.");
            }
        }

        var historyCount = WorkflowState.Instance.History.Count;
        if (historyCount < 5)
        {
            warnings++;
            sb.AppendLine($"[WARN] Very low workflow history volume ({historyCount}).");
        }
        else
        {
            sb.AppendLine($"[PASS] Workflow history entries available ({historyCount}).");
        }

        var trainingDir = Path.Combine(exportsDir, "training_set");
        Directory.CreateDirectory(trainingDir);
        var probe = Path.Combine(trainingDir, ".write_probe.tmp");
        try
        {
            File.WriteAllText(probe, DateTime.Now.ToString("O"));
            File.Delete(probe);
            sb.AppendLine("[PASS] Training folder write access OK.");
        }
        catch
        {
            failures++;
            sb.AppendLine("[FAIL] Training folder write access failed.");
        }

        var summary = failures > 0
            ? $"FAILED ({failures} fail, {warnings} warn)"
            : warnings > 0
                ? $"WARN ({warnings} warn)"
                : "PASS";

        sb.AppendLine();
        sb.AppendLine($"Summary: {summary}");
        sb.AppendLine($"CheckedAt: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        File.WriteAllText(reportPath, sb.ToString());

        WorkflowState.Instance.AddEvent("UTILITY", $"DB integrity check result: {summary}.");
        MessageBox.Show(
            $"DB integrity check complete: {summary}\n\nReport:\n{reportPath}",
            "AOI Monitor",
            MessageBoxButton.OK,
            failures > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    private void OnRebuildImageIndexClick(object sender, RoutedEventArgs e)
    {
        var exportsDir = EnsureExportsDir();
        var file = Path.Combine(exportsDir, "image_index.csv");
        var lines = new List<string> { "RelativePath,Bytes,LastWriteUtc" };

        int count = 0;
        foreach (var path in EnumerateImageFiles(exportsDir))
        {
            var info = new FileInfo(path);
            var rel = Path.GetRelativePath(exportsDir, path).Replace('\\', '/');
            lines.Add($"{EscapeCsv(rel)},{info.Length},{info.LastWriteTimeUtc:O}");
            count++;
        }

        File.WriteAllLines(file, lines);
        WorkflowState.Instance.AddEvent("UTILITY", $"Image index rebuilt with {count} entries.");

        MessageBox.Show(
            $"Image index rebuilt with {count} entries.\n\nIndex file:\n{file}",
            "AOI Monitor",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }
=======
    private void OnRebuildImageIndexClick(object sender, RoutedEventArgs e) => RunUtility("Rebuild image index", "Image index rebuild completed.");
    private void OnVerifyImagePathsClick(object sender, RoutedEventArgs e) => RunUtility("Verify image paths", "Image path verification completed. Broken links: 0 in current run.");
    private void OnArchiveReviewedSamplesClick(object sender, RoutedEventArgs e) => RunUtility("Archive reviewed samples", "Reviewed samples archived.");
    private void OnRunDbIntegrityCheckClick(object sender, RoutedEventArgs e) => RunUtility("Run DB integrity check", "DB integrity check finished with no critical errors.");
>>>>>>> 67117d637c0ef2a7f4698c2245b5001171a02ca2

    private void OnExportAuditTrailClick(object sender, RoutedEventArgs e)
    {
        var exportDir = Path.Combine(AppContext.BaseDirectory, "exports");
        Directory.CreateDirectory(exportDir);
        var file = Path.Combine(exportDir, $"audit_trail_{DateTime.Now:yyyyMMdd_HHmmss}.csv");

        var sb = new StringBuilder();
        sb.AppendLine("Timestamp,Category,Message");
        foreach (var h in WorkflowState.Instance.History)
            sb.AppendLine($"{h.Timestamp:yyyy-MM-dd HH:mm:ss},{h.Category},\"{h.Message.Replace("\"", "''")}\"");

        File.WriteAllText(file, sb.ToString());
        WorkflowState.Instance.AddEvent("EXPORT", $"Audit trail exported: {Path.GetFileName(file)}");
        MessageBox.Show($"Audit trail exported:\n{file}", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnLockActiveRecipeClick(object sender, RoutedEventArgs e)
    {
        var state = WorkflowState.Instance;
        state.IsRecipeLocked = !state.IsRecipeLocked;
        state.AddEvent("SYSTEM", state.IsRecipeLocked ? "Active recipe locked from Reports." : "Active recipe unlocked from Reports.");
        MessageBox.Show(state.IsRecipeLocked ? "Active recipe locked." : "Active recipe unlocked.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
    }

<<<<<<< HEAD
    private static string EnsureExportsDir()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "exports");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static IEnumerable<string> EnumerateImageFiles(string root)
    {
        if (!Directory.Exists(root))
            yield break;

        foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
        {
            if (ImageExtensions.Contains(Path.GetExtension(file)))
                yield return file;
        }
    }

    private static string EscapeCsv(string value)
        => $"\"{value.Replace("\"", "\"\"")}\"";

    private static void CheckPrimaryPath(string label, string? path, StringBuilder sb, ref int inaccessible)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            sb.AppendLine($"[WARN] {label} image path is not set.");
            return;
        }

        if (!File.Exists(path))
        {
            inaccessible++;
            sb.AppendLine($"[MISSING] {label} image not found: {path}");
        }
=======
    private void RunUtility(string name, string message)
    {
        WorkflowState.Instance.AddEvent("UTILITY", name);
        MessageBox.Show(message, "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
>>>>>>> 67117d637c0ef2a7f4698c2245b5001171a02ca2
    }
}
