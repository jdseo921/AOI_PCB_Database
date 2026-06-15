using System.Collections.ObjectModel;
using System.Windows.Threading;
using AOI_Monitor.Models;

namespace AOI_Monitor.ViewModels;

public class NavPage : ViewModelBase
{
    private bool _isActive;
    public string Key      { get; set; } = "";
    public string Number   { get; set; } = "";
    public string Title    { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public bool IsEnabled  { get; set; } = true;
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
        new NavPage { Key="profile",  Number="05", Title="3D Profile Viewer", Subtitle="planned stage 2", IsEnabled=false },
        new NavPage { Key="guide",    Number="06", Title="Settings / Guide",  Subtitle="setup/docs" },
    };

    public IEnumerable<StatusCell> StatusCells { get; } = new[]
    {
        new StatusCell("Console Scope",    "Local Review Workstation",      ""),
        new StatusCell("Runtime Model",    "Local WPF Prototype",           ""),
        new StatusCell("Board / Program",  "TBOX-MAIN / TBOX_TOP_V1.2",    "green"),
        new StatusCell("Review Policy",    "False Negative Priority",       "amber"),
        new StatusCell("Inspection Engine","Pixel Difference 0.1",          ""),
        new StatusCell("Storage",          "See readiness panel",           ""),
    };

    public IEnumerable<DefectRecord> DefectRecords { get; } = new[]
    {
        new DefectRecord("IMG_0241","TBOX-MAIN","U107","Solder Bridge",  "Critical","OK","NG","Possible Escape","LINK","01-28 18:04"),
        new DefectRecord("IMG_0188","TBOX-MAIN","C684","Insuff. Solder", "Major",   "NG","NG","Verified NG",    "LINK","01-28 17:52"),
        new DefectRecord("IMG_0182","TBOX-MAIN","CN8", "Pin Height Err.","Major",   "NG","OK","False Call",     "LINK","01-28 17:44"),
        new DefectRecord("IMG_0177","TBOX-MAIN","D12", "Polarity Error", "Critical","OK","NG","Possible Escape","LINK","01-28 17:39"),
        new DefectRecord("IMG_0164","TBOX-MAIN","R88", "Tombstone",      "Major",   "NG","NG","Verified NG",    "LINK","01-28 17:20"),
        new DefectRecord("IMG_0153","TBOX-MAIN","SH1", "Shield Can Gap", "Major",   "NG","OK","False Call",     "LINK","01-28 17:11"),
    };

    public IEnumerable<StationInfo> Stations { get; } = new[]
    {
        new StationInfo("FOV01","Review Set",1930,  3,0, 81.0,81,2, "TOP FOV",      "green"),
        new StationInfo("FOV02","Review Set",2060,  14,0,67.0,67,3, "BGA Area",     "green"),
        new StationInfo("FOV03","Review Set",1840,  25,1,44.0,44,5, "Passive",      "amber"),
        new StationInfo("FOV04","Review Set",1970,  36,0,73.0,73,4, "Connector",    "green"),
        new StationInfo("FOV05","Hold",       19,    6,0, 19.0, 6,62,"Review Hold",  "red"),
        new StationInfo("FOV06","Review Set",2100,  42,0,42.0,42,5, "Shield",       "green"),
        new StationInfo("FOV07","Review Set",1800,  38,2,38.0,38,6, "Side View",    "green"),
        new StationInfo("FOV08","Planned Input",0,   0,0,  0.0, 0,0, "Not Connected","red"),
    };

    public IEnumerable<SpcStat> SpcStats { get; } = new[]
    {
        new SpcStat("First Pass Yield",     "94.8%", false),
        new SpcStat("False Call Ratio",     "2.7%",  false),
        new SpcStat("Escape-Risk Rate",     "0.42%", true),
        new SpcStat("Annotation Coverage",  "91.6%", false),
        new SpcStat("Broken Image Links",   "17",    true),
        new SpcStat("Missing RefDes",       "84",    false),
    };

    public IEnumerable<DbHealthRow> DbRows { get; } = new[]
    {
        new DbHealthRow("Samples",             "23,932", "OK"),
        new DbHealthRow("Annotations",         "21,927", "OK"),
        new DbHealthRow("ROI Crops",           "18,404", "WARN"),
        new DbHealthRow("Similar-Image Index", "23,711", "OK"),
        new DbHealthRow("Audit Trail",         "83,112", "OK"),
        new DbHealthRow("Unresolved Conflicts","36",     "NG"),
    };

    public RelayCommand NavigateCommand { get; }

    public MainViewModel()
    {
        NavPages[0].IsActive = true;
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
