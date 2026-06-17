using System.Collections.ObjectModel;
using System.Windows.Threading;
using AOI_Monitor.Services;

namespace AOI_Monitor.ViewModels;

public class NavPage : ViewModelBase
{
    private bool _isActive;
    private bool _isEnabled = true;
    public string Key      { get; set; } = "";
    public string Number   { get; set; } = "";
    public string Title    { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetField(ref _isEnabled, value);
    }
    public bool IsActive
    {
        get => _isActive;
        set => SetField(ref _isActive, value);
    }
}

public class MainViewModel : ViewModelBase
{
    private string _currentPage = "monitor";
    private string _clockText   = "00:00";

    public string CurrentPage
    {
        get => _currentPage;
        set
        {
            SetField(ref _currentPage, value);
            foreach (var p in NavPages) p.IsActive = IsTopLevelActive(p.Key, value);
        }
    }

    public string ClockText
    {
        get => _clockText;
        set => SetField(ref _clockText, value);
    }

    public ObservableCollection<NavPage> NavPages { get; } = new()
    {
        new NavPage { Key="monitor",  Number="01", Title="Main Inspection",   Subtitle="review workflow" },
        new NavPage { Key="recipe",   Number="02", Title="Recipe Editor",     Subtitle="ROI/rules" },
        new NavPage { Key="modeltest",Number="03", Title="AI Model Test",     Subtitle="stage 1 validation" },
        new NavPage { Key="reports",  Number="04", Title="Log & Export",      Subtitle="history/package" },
        new NavPage { Key="profile",  Number="05", Title="3D Profile Viewer", Subtitle="sample CSV mode" },
        new NavPage { Key="calibration", Number="06", Title="Calibration",     Subtitle="stage 2 prep" },
        new NavPage { Key="guide",    Number="07", Title="Settings / Guide",  Subtitle="setup/docs" },
    };

    public RelayCommand NavigateCommand { get; }

    public MainViewModel()
    {
        NavPages[0].IsActive = true;
        RefreshRolePermissions(WorkflowState.Instance.CurrentRole);
        NavigateCommand = new RelayCommand(key =>
        {
            var pageKey = (string?)key ?? "monitor";
            var navPage = NavPages.FirstOrDefault(p => p.Key == pageKey);
            if (navPage?.IsEnabled == false)
                return;

            CurrentPage = pageKey;
        });

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        timer.Tick += (_, _) => TickClock();
        timer.Start();
        TickClock();
    }

    public void RefreshRolePermissions(UserRole role)
    {
        foreach (var page in NavPages)
            page.IsEnabled = RoleAuthorization.CanAccessPage(role, page.Key);

        if (!RoleAuthorization.CanAccessPage(role, CurrentPage))
            CurrentPage = "monitor";
    }

    private void TickClock()
    {
        var n = DateTime.Now;
        ClockText = $"{n.Hour:D2}:{n.Minute:D2}";
    }

    private static bool IsTopLevelActive(string navKey, string currentPage)
    {
        if (navKey == currentPage)
            return true;

        return navKey switch
        {
            "monitor" => currentPage is "review" or "compare" or "library",
            "reports" => currentPage == "spc",
            "guide" => currentPage is "settings" or "install",
            _ => false,
        };
    }
}
