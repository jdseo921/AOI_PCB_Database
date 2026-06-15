using System.Windows.Controls;

namespace AOI_Monitor.Views;

public partial class InstallView : UserControl
{
    private static readonly object[] Runtime =
    {
        new { Element = "Local WPF process",             Status = "READY",  Purpose = "Runs review, disposition, recipe, logs, and settings workflows on this workstation." },
        new { Element = "SQLite / image vault",          Status = "LOCAL",  Purpose = "Stores imported images, inspection history, review events, recipe revisions, and exports." },
        new { Element = "Inspection engine",             Status = "LOCAL",  Purpose = "Uses deterministic pixel difference against selected local images." },
        new { Element = "Service deployment",            Status = "PLANNED",Purpose = "Windows service, camera, robot, PLC, and MES links are outside this prototype." },
    };

    private static readonly object[] Boundary =
    {
        new { Topic = "Implemented now",        Desc = "Local review console, image-vault persistence, recipe revision saves, and local export packages." },
        new { Topic = "Operator role",          Desc = "Operators use the GUI for review, disposition, recipe editing, and evidence export." },
        new { Topic = "Planned integration",    Desc = "Final software may add hardware links, service hosting, MES writeback, and 3D profile input." },
        new { Topic = "Not implemented here",   Desc = "No camera, PLC, robot, conveyor, Windows service, or production training pipeline is controlled by this app." },
    };

    public InstallView()
    {
        InitializeComponent();
        RuntimeGrid.ItemsSource  = Runtime;
        BoundaryGrid.ItemsSource = Boundary;
    }
}
