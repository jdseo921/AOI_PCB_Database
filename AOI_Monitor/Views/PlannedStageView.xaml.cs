using System.Windows.Controls;

namespace AOI_Monitor.Views;

public partial class PlannedStageView : UserControl
{
    public PlannedStageView(string title, string description)
    {
        InitializeComponent();
        TitleText.Text = title;
        DescriptionText.Text = description;
    }
}
