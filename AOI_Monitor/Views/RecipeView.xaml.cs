using System.Windows.Controls;

namespace AOI_Monitor.Views;

public partial class RecipeView : UserControl
{
    private static readonly object[] Thresholds =
    {
        new { Type = "Solder Bridge",    Threshold = "0.42", FnMode = "LOW", FpMode = "0.62", Status = "LOCK" },
        new { Type = "Missing Component",Threshold = "0.38", FnMode = "LOW", FpMode = "0.58", Status = "LOCK" },
        new { Type = "Polarity Error",   Threshold = "0.44", FnMode = "LOW", FpMode = "0.66", Status = "LOCK" },
        new { Type = "Pin Height",       Threshold = "0.48", FnMode = "LOW", FpMode = "0.70", Status = "REVIEW" },
    };

    private static readonly object[] Matrix =
    {
        new { Item = "U107 BGA",      Presence = "ON",  Polarity = "ON",  Bridge = "ON",  Coplanarity = "ON",  Volume = "ON",  Action = "Review bridge ROI" },
        new { Item = "C684/C688",     Presence = "ON",  Polarity = "N/A", Bridge = "ON",  Coplanarity = "N/A", Volume = "ON",  Action = "Locked" },
        new { Item = "CN8 Connector", Presence = "ON",  Polarity = "ON",  Bridge = "OFF", Coplanarity = "ON",  Volume = "OFF", Action = "Locked" },
        new { Item = "Shield Can",    Presence = "ON",  Polarity = "ON",  Bridge = "OFF", Coplanarity = "OFF", Volume = "ON",  Action = "Locked" },
        new { Item = "R Network",     Presence = "ON",  Polarity = "OFF", Bridge = "ON",  Coplanarity = "OFF", Volume = "ON",  Action = "Locked" },
        new { Item = "Test Pads",     Presence = "N/A", Polarity = "N/A", Bridge = "OFF", Coplanarity = "OFF", Volume = "OFF", Action = "Locked" },
    };

    public RecipeView()
    {
        InitializeComponent();
        ThresholdGrid.ItemsSource = Thresholds;
        MatrixGrid.ItemsSource    = Matrix;
    }
}
