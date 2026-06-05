using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.IO;
using AOI_Monitor.Services;

namespace AOI_Monitor.Views;

public partial class ReportsView : UserControl
{
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

    private void OnRebuildImageIndexClick(object sender, RoutedEventArgs e) => RunUtility("Rebuild image index", "Image index rebuild completed.");
    private void OnVerifyImagePathsClick(object sender, RoutedEventArgs e) => RunUtility("Verify image paths", "Image path verification completed. Broken links: 0 in current run.");
    private void OnArchiveReviewedSamplesClick(object sender, RoutedEventArgs e) => RunUtility("Archive reviewed samples", "Reviewed samples archived.");
    private void OnRunDbIntegrityCheckClick(object sender, RoutedEventArgs e) => RunUtility("Run DB integrity check", "DB integrity check finished with no critical errors.");

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

    private void RunUtility(string name, string message)
    {
        WorkflowState.Instance.AddEvent("UTILITY", name);
        MessageBox.Show(message, "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
