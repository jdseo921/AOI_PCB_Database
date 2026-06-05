using System.Windows.Controls;

namespace AOI_Monitor.Views;

public partial class InstallView : UserControl
{
    private static readonly object[] Runtime =
    {
        new { Element = "Background inspection service", Status = "RUN",    Purpose = "Executes inspection and result logging without requiring the GUI to stay foregrounded." },
        new { Element = "GUI monitor / review console",  Status = "READY",  Purpose = "Used for station visibility, disposition, database access, recipe lock, and export." },
        new { Element = "Shared local storage",          Status = "READY",  Purpose = "SQLite records and image-vault links remain accessible to the monitor." },
        new { Element = "Service restart",               Status = "MANUAL", Purpose = "Maintenance action only; not part of normal operator review." },
    };

    private static readonly object[] Boundary =
    {
        new { Topic = "Prototype purpose",      Desc = "Visual direction and workflow confirmation for installed GUI." },
        new { Topic = "Installation implication",Desc = "Final software may run as a background service with a separate monitoring console." },
        new { Topic = "Reference usage",        Desc = "Factory-style monitor layout is used as visual direction only; AOI-specific data is retained." },
        new { Topic = "Not implemented here",   Desc = "Hardware control and final service daemon are outside this HTML prototype." },
    };

    public InstallView()
    {
        InitializeComponent();
        RuntimeGrid.ItemsSource  = Runtime;
        BoundaryGrid.ItemsSource = Boundary;
    }
}
