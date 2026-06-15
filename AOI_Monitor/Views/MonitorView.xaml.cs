using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AOI_Monitor.Controls;
using AOI_Monitor.Models;
using AOI_Monitor.ViewModels;

namespace AOI_Monitor.Views;

public partial class MonitorView : UserControl
{
    private static readonly StationInfo[] Stations =
    {
        new("CAM01","In Process",1930,  3,0, 81.0,81, 2,  "TOP FOV",    "green"),
        new("CAM02","In Process",2060,  14,0,67.0,67, 3,  "BGA Area",   "green"),
        new("CAM03","In Process",1840,  25,1,44.0,44, 5,  "Passive",    "amber"),
        new("CAM04","In Process",1970,  36,0,73.0,73, 4,  "Connector",  "green"),
        new("CAM05","Hold",        19,   6,0,19.0, 6,62,  "Review Hold","red"),
        new("CAM06","In Process",2100,  42,0,42.0,42, 5,  "Shield",     "green"),
        new("CAM07","In Process",1800,  38,2,38.0,38, 6,  "Side View",  "green"),
        new("CAM08","No Link",      0,   0,0, 0.0, 0, 0,  "No Link",    "red"),
    };

    public MonitorView()
    {
        InitializeComponent();
        BuildStationCards();
    }

    private void OnOpenDispositionClick(object sender, RoutedEventArgs e) => Navigate("review");
    private void OnOpenCompareClick(object sender, RoutedEventArgs e) => Navigate("compare");
    private void OnOpenLibraryClick(object sender, RoutedEventArgs e) => Navigate("library");

    private void Navigate(string key)
    {
        if (Window.GetWindow(this)?.DataContext is MainViewModel vm)
            vm.CurrentPage = key;
    }

    private void BuildStationCards()
    {
        foreach (var s in Stations)
            StationsPanel.Children.Add(MakeCard(s));
    }

    private static Border MakeCard(StationInfo s)
    {
        var headBg   = s.IsRed ? "#D6131A" : "#191F23";
        var headFg   = s.IsRed ? "#FFFFFF"  : "#D9E1E6";
        var tagBg    = s.IsRed ? "#581B1D"  : s.IsAmber ? "#8A570F" : "#173A21";
        var tagBdr   = s.IsRed ? "#A83A3E"  : s.IsAmber ? "#CF8D28" : "#3E844A";
        var tagFg    = s.IsRed ? "#FFCECE"  : s.IsAmber ? "#FFF1C5" : "#CFFFCE";
        var gaugeClr = new SolidColorBrush((Color)ColorConverter.ConvertFromString(s.GaugeColor));

        // Card root
        var card = new Border
        {
            BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3D464D")),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#14191D")),
            Margin = new Thickness(0, 0, 8, 8),
        };
        var outer = new Grid();
        outer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(25) });
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        card.Child = outer;

        // Header
        var header = new Border
        {
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(headBg)),
            BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#343B42")),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(6, 3, 6, 3),
        };
        var headerGrid = new Grid();
        headerGrid.Children.Add(new TextBlock { Text = s.Name, FontWeight = FontWeights.ExtraBold, Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(headFg)), FontSize = 12 });
        var statusTb = new TextBlock { Text = s.Status, HorizontalAlignment = HorizontalAlignment.Right, Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(headFg)), FontSize = 10.5 };
        headerGrid.Children.Add(statusTb);
        header.Child = headerGrid;
        Grid.SetRow(header, 0);
        outer.Children.Add(header);

        // Body: metrics + gauge
        var bodyGrid = new Grid { Margin = new Thickness(8) };
        bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        var metrics = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        metrics.Children.Add(MakeMetricLine("Sample", s.SampleCount.ToString()));
        metrics.Children.Add(MakeMetricLine("Review", s.ReviewCount.ToString()));
        metrics.Children.Add(MakeMetricLine("Wait",   s.WaitCount.ToString()));
        bodyGrid.Children.Add(metrics);
        var gauge = new CircularGauge { Value = s.Yield, GaugeColor = gaugeClr, Width = 68, Height = 68, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetColumn(gauge, 1);
        bodyGrid.Children.Add(gauge);
        Grid.SetRow(bodyGrid, 1);
        outer.Children.Add(bodyGrid);

        // Bar list
        var bars = new StackPanel { Margin = new Thickness(8, 0, 8, 7) };
        bars.Children.Add(MakeBar("Detected", s.DetectedPct, false));
        bars.Children.Add(MakeBar("False",    s.FalseCount, s.StatusColor == "red" ? true : false));
        Grid.SetRow(bars, 2);
        outer.Children.Add(bars);

        // Disposition tag
        var tag = new Border
        {
            Margin = new Thickness(8, 0, 8, 8),
            MinHeight = 34,
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(tagBg)),
            BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(tagBdr)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4),
        };
        tag.Child = new TextBlock
        {
            Text = s.Description,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(tagFg)),
            FontSize = 10.5,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
        };
        Grid.SetRow(tag, 3);
        outer.Children.Add(tag);

        return card;
    }

    private static TextBlock MakeMetricLine(string label, string val)
    {
        var tb = new TextBlock { Margin = new Thickness(0, 2, 0, 2), FontSize = 10.5 };
        tb.Inlines.Add(new System.Windows.Documents.Run(label + ": ") { Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#AAB4BB")) });
        tb.Inlines.Add(new System.Windows.Documents.Run(val) { FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EEF3F7")) });
        return tb;
    }

    private static Grid MakeBar(string label, double value, bool isRed)
    {
        var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        g.Children.Add(new TextBlock { Text = label, FontSize = 9.5, Foreground = new SolidColorBrush(Color.FromRgb(0x8C, 0x98, 0xA1)) });
        var bar = new System.Windows.Controls.ProgressBar
        {
            Value = Math.Min(100, value), Maximum = 100,
            Style = (Style)Application.Current.FindResource(isRed ? "BarTrackRed" : "BarTrack"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(5, 0, 5, 0),
        };
        Grid.SetColumn(bar, 1);
        g.Children.Add(bar);
        var num = new TextBlock { Text = value.ToString("F0"), FontSize = 9.5, Foreground = new SolidColorBrush(Color.FromRgb(0x8C, 0x98, 0xA1)), TextAlignment = TextAlignment.Right };
        Grid.SetColumn(num, 2);
        g.Children.Add(num);
        return g;
    }
}
