using System.Windows.Controls;

namespace AOI_Monitor.Views;

public partial class GuideView : UserControl
{
    private static readonly object[] Steps =
    {
        new { Num = "01", Priority = "MANDATORY", Step = "Confirm that the background inspection service is active before opening review work." },
        new { Num = "02", Priority = "MANDATORY", Step = "Confirm active recipe, model version, lot ID, and image-vault link state." },
        new { Num = "03", Priority = "CHECK",     Step = "Review Possible Escape cases before false calls. Default policy is AI OK / GT NG first." },
        new { Num = "04", Priority = "CHECK",     Step = "Open the current sample, compare AI overlay against ground-truth overlay, then verify RefDes and FOV." },
        new { Num = "05", Priority = "CHECK",     Step = "Check closest matching historical defect images before disposition." },
        new { Num = "06", Priority = "CHECK",     Step = "Record final disposition: Confirm NG, False Call, Possible Escape, Training Candidate, or Hold." },
        new { Num = "07", Priority = "CHECK",     Step = "Review SPC / database health before exporting customer validation evidence." },
        new { Num = "08", Priority = "CHECK",     Step = "Lock recipe and export the audit trail after review completion." },
    };

    public GuideView()
    {
        InitializeComponent();
        StepsGrid.ItemsSource = Steps;
    }
}
