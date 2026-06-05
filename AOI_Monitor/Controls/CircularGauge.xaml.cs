using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace AOI_Monitor.Controls;

public partial class CircularGauge : UserControl
{
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(double), typeof(CircularGauge),
            new PropertyMetadata(0.0, OnValueChanged));

    public static readonly DependencyProperty GaugeColorProperty =
        DependencyProperty.Register(nameof(GaugeColor), typeof(Brush), typeof(CircularGauge),
            new PropertyMetadata(null, OnValueChanged));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public Brush GaugeColor
    {
        get => (Brush)GetValue(GaugeColorProperty);
        set => SetValue(GaugeColorProperty, value);
    }

    public CircularGauge()
    {
        InitializeComponent();
        GaugeColor = new SolidColorBrush(Color.FromRgb(0x50, 0xF5, 0x6E));
        Loaded += (_, _) => UpdateArc();
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((CircularGauge)d).UpdateArc();

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        UpdateArc();
    }

    private void UpdateArc()
    {
        double w = ActualWidth;
        double h = ActualHeight;
        if (double.IsNaN(w) || double.IsNaN(h) || w <= 1 || h <= 1)
        {
            return;
        }

        double size = Math.Min(w, h);
        double stroke = Math.Clamp(size * 0.16, 8.0, 12.0);
        double cx = w / 2.0;
        double cy = h / 2.0;
        double r = Math.Max(0.0, (size - stroke) / 2.0 - 0.5);

        TrackPath.StrokeThickness = stroke;
        ArcPath.StrokeThickness = stroke;

        // Keep center mask and text proportional to control size.
        double inner = Math.Max(0.0, size - stroke * 2.0);
        InnerMask.Width = inner;
        InnerMask.Height = inner;
        ValueText.FontSize = Math.Clamp(size * 0.145, 9.0, 12.0);

        // Full background track circle
        TrackPath.Data = new EllipseGeometry(new Point(cx, cy), r, r);

        double value = Math.Clamp(Value, 0, 100);
        ValueText.Text = $"{value:F0}%";

        var color = GaugeColor ?? new SolidColorBrush(Color.FromRgb(0x50, 0xF5, 0x6E));

        if (value < 0.5) { ArcPath.Data = null; return; }

        if (value >= 99.9)
        {
            ArcPath.Data   = new EllipseGeometry(new Point(cx, cy), r, r);
            ArcPath.Stroke = color;
            return;
        }

        double angleDeg  = value / 100.0 * 360.0;
        double startRad  = -Math.PI / 2;
        double endRad    = startRad + angleDeg * Math.PI / 180.0;

        var start = new Point(cx + r * Math.Cos(startRad), cy + r * Math.Sin(startRad));
        var end   = new Point(cx + r * Math.Cos(endRad),   cy + r * Math.Sin(endRad));

        var fig = new PathFigure { StartPoint = start };
        fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0,
                                        angleDeg > 180, SweepDirection.Clockwise, true));

        ArcPath.Data   = new PathGeometry(new[] { fig });
        ArcPath.Stroke = color;
    }
}
