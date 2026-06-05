using System.Windows.Controls;

namespace AOI_Monitor.Views;

public partial class SpcView : UserControl
{
    private static readonly object[] DbRows =
    {
        new { Table = "Samples",             Count = "23,932", Status = "OK"   },
        new { Table = "Annotations",         Count = "21,927", Status = "OK"   },
        new { Table = "ROI Crops",           Count = "18,404", Status = "WARN" },
        new { Table = "Similar-Image Index", Count = "23,711", Status = "OK"   },
        new { Table = "Audit Trail",         Count = "83,112", Status = "OK"   },
        new { Table = "Unresolved Conflicts",Count = "36",     Status = "NG"   },
    };

    public SpcView()
    {
        InitializeComponent();
        DbHealthGrid.ItemsSource = DbRows;
    }
}
