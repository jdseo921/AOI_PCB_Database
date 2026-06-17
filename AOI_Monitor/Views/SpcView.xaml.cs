using System.Windows.Controls;
using System.Windows.Media;
using System.IO;
using AOI_Monitor.Data;
using AOI_Monitor.Models;

namespace AOI_Monitor.Views;

public partial class SpcView : UserControl
{
    public SpcView()
    {
        InitializeComponent();
        RefreshFromState();
    }

    public void RefreshFromState()
    {
        var healthRows = AoiDatabase.GetDatabaseHealthRows();
        DbHealthGrid.ItemsSource = healthRows;

        var inspections = AoiDatabase.GetInspectionHistory(new LogFilter());
        var reviews = AoiDatabase.GetReviewEvents(new LogFilter());
        var images = AoiDatabase.GetImportedImages();
        var ok = inspections.Count(row => string.Equals(row.Verdict, "OK", StringComparison.OrdinalIgnoreCase));
        var ng = inspections.Count(row => string.Equals(row.Verdict, "NG", StringComparison.OrdinalIgnoreCase));
        var review = inspections.Count(row => string.Equals(row.Verdict, "REVIEW", StringComparison.OrdinalIgnoreCase));
        var brokenLinks = images.Count(image => string.IsNullOrWhiteSpace(image.VaultPath) || !File.Exists(image.VaultPath));
        var yield = inspections.Count == 0
            ? "--"
            : (ok / (double)inspections.Count).ToString("P1", System.Globalization.CultureInfo.InvariantCulture);

        InspectionCountText.Text = inspections.Count.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
        VerdictBreakdownText.Text = $"{ok:N0} / {ng:N0} / {review:N0}";
        YieldText.Text = yield;
        ReviewEventCountText.Text = reviews.Count.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
        ImportedImageCountText.Text = images.Count.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
        BrokenImageLinksText.Text = brokenLinks.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
        BrokenImageLinksText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(brokenLinks > 0 ? "#F13B3F" : "#DCE5EB"));

        DatabaseWarningText.Text = brokenLinks > 0
            ? $"SQLite warning: {brokenLinks:N0} imported image vault link(s) are missing. Run Verify Paths in Log & Export."
            : "SQLite summary uses local database counts. Trend chart is prototype data, not live factory SPC.";
        DatabaseWarningText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(brokenLinks > 0 ? "#F27777" : "#E1A334"));
    }
}
