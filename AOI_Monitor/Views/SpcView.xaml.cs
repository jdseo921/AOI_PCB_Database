using System.Windows.Controls;
using AOI_Monitor.Data;

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
        DbHealthGrid.ItemsSource = AoiDatabase.GetDatabaseHealthRows();
    }
}
