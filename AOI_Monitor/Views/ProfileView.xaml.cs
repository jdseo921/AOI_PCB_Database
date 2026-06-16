using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AOI_Monitor.Services;
using Microsoft.Win32;

namespace AOI_Monitor.Views;

public partial class ProfileView : UserControl
{
    private readonly Dictionary<(int X, int Y), double> _points = new();
    private int _minX;
    private int _maxX;
    private int _minY;
    private int _maxY;
    private double _minHeight;
    private double _maxHeight;
    private string _currentCsvPath = string.Empty;
    private (int X, int Y, double Height)? _selectedPoint;

    public ProfileView()
    {
        InitializeComponent();
    }

    private void OnLoadCsvClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Load sample 3D height-map CSV",
            Filter = "CSV height map|*.csv|All files|*.*",
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            LoadHeightMap(dialog.FileName);
            WorkflowState.Instance.AddEvent("PROFILE_3D", $"Loaded sample height-map CSV: {Path.GetFileName(dialog.FileName)}.");
        }
        catch (Exception ex)
        {
            StatusText.Text = $"CSV load failed: {ex.Message}";
            WorkflowState.Instance.AddEvent("PROFILE_3D_ERROR", $"Height-map CSV load failed: {ex.Message}");
            MessageBox.Show($"Could not load height-map CSV:\n{ex.Message}", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void LoadHeightMap(string path)
    {
        var loaded = ParseHeightMap(path);
        if (loaded.Count == 0)
            throw new InvalidOperationException("No valid x,y,height rows were found.");

        _points.Clear();
        foreach (var point in loaded)
            _points[(point.X, point.Y)] = point.Height;

        _currentCsvPath = path;
        _minX = _points.Keys.Min(p => p.X);
        _maxX = _points.Keys.Max(p => p.X);
        _minY = _points.Keys.Min(p => p.Y);
        _maxY = _points.Keys.Max(p => p.Y);
        _minHeight = _points.Values.Min();
        _maxHeight = _points.Values.Max();
        _selectedPoint = loaded.OrderByDescending(p => p.Height).First();

        CsvNameText.Text = Path.GetFileName(path);
        HeightRangeText.Text = $"{_minHeight:F3} / {_maxHeight:F3}";
        MapStatusText.Text = $"{_points.Count:N0} height samples";
        EmptyMapText.Visibility = Visibility.Collapsed;
        HeightMapImage.Source = BuildHeatMapBitmap();
        RefreshSelectionUi();
        DrawProfileLine();
        StatusText.Text = "Sample height-map loaded. This is not live 3D camera inspection.";
    }

    private static List<(int X, int Y, double Height)> ParseHeightMap(string path)
    {
        var rows = new List<(int X, int Y, double Height)>();
        var lines = File.ReadAllLines(path);
        if (lines.Length == 0)
            return rows;

        var start = 0;
        var header = SplitCsvLine(lines[0]).Select(h => h.Trim().ToLowerInvariant()).ToArray();
        var hasHeader = header.Contains("x") && header.Contains("y") && header.Contains("height");
        var xIndex = hasHeader ? Array.IndexOf(header, "x") : 0;
        var yIndex = hasHeader ? Array.IndexOf(header, "y") : 1;
        var heightIndex = hasHeader ? Array.IndexOf(header, "height") : 2;
        if (hasHeader)
            start = 1;

        foreach (var line in lines.Skip(start))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var cells = SplitCsvLine(line);
            if (cells.Count <= Math.Max(heightIndex, Math.Max(xIndex, yIndex)))
                continue;

            if (int.TryParse(cells[xIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) &&
                int.TryParse(cells[yIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) &&
                double.TryParse(cells[heightIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out var height))
            {
                rows.Add((x, y, height));
            }
        }

        return rows;
    }

    private BitmapSource BuildHeatMapBitmap()
    {
        var width = _maxX - _minX + 1;
        var height = _maxY - _minY + 1;
        var stride = width * 4;
        var pixels = new byte[stride * height];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var key = (X: _minX + x, Y: _minY + y);
                var color = _points.TryGetValue(key, out var value)
                    ? ColorForHeight(value)
                    : Color.FromRgb(18, 22, 25);
                var offset = y * stride + x * 4;
                pixels[offset] = color.B;
                pixels[offset + 1] = color.G;
                pixels[offset + 2] = color.R;
                pixels[offset + 3] = 255;
            }
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }

    private Color ColorForHeight(double height)
    {
        var range = Math.Max(0.000001, _maxHeight - _minHeight);
        var t = Math.Clamp((height - _minHeight) / range, 0, 1);
        var r = (byte)(40 + 215 * t);
        var g = (byte)(80 + 120 * (1.0 - Math.Abs(t - 0.5) * 2.0));
        var b = (byte)(220 * (1.0 - t));
        return Color.FromRgb(r, g, b);
    }

    private void OnHeightMapClick(object sender, MouseButtonEventArgs e)
    {
        if (HeightMapImage.Source is not BitmapSource bitmap || _points.Count == 0)
            return;

        var position = e.GetPosition(HeightMapImage);
        var scaleX = bitmap.PixelWidth / Math.Max(1.0, HeightMapImage.ActualWidth);
        var scaleY = bitmap.PixelHeight / Math.Max(1.0, HeightMapImage.ActualHeight);
        var x = _minX + (int)Math.Clamp(Math.Floor(position.X * scaleX), 0, bitmap.PixelWidth - 1);
        var y = _minY + (int)Math.Clamp(Math.Floor(position.Y * scaleY), 0, bitmap.PixelHeight - 1);

        if (_points.TryGetValue((x, y), out var value))
        {
            _selectedPoint = (x, y, value);
            RefreshSelectionUi();
            DrawProfileLine();
        }
    }

    private void RefreshSelectionUi()
    {
        if (_selectedPoint is not { } point)
        {
            SelectedPointText.Text = "--";
            SelectedHeightText.Text = "--";
            return;
        }

        SelectedPointText.Text = $"x={point.X}, y={point.Y}";
        SelectedHeightText.Text = point.Height.ToString("F3", CultureInfo.InvariantCulture);
    }

    private void DrawProfileLine()
    {
        ProfileCanvas.Children.Clear();
        if (_selectedPoint is not { } point || _points.Count == 0)
            return;

        var row = Enumerable.Range(_minX, _maxX - _minX + 1)
            .Select(x => (X: x, Height: _points.TryGetValue((x, point.Y), out var value) ? value : double.NaN))
            .Where(p => !double.IsNaN(p.Height))
            .ToArray();

        if (row.Length < 2)
            return;

        var width = Math.Max(1, ProfileCanvas.ActualWidth);
        var height = Math.Max(1, ProfileCanvas.ActualHeight);
        if (width <= 1 || height <= 1)
        {
            width = 420;
            height = 240;
        }

        var polyline = new System.Windows.Shapes.Polyline
        {
            Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#5CA0D3")),
            StrokeThickness = 2,
        };

        foreach (var sample in row)
        {
            var x = (sample.X - _minX) / Math.Max(1.0, _maxX - _minX) * width;
            var y = height - ((sample.Height - _minHeight) / Math.Max(0.000001, _maxHeight - _minHeight) * height);
            polyline.Points.Add(new Point(x, y));
        }

        ProfileCanvas.Children.Add(polyline);
    }

    private void OnAcceptDefectClick(object sender, RoutedEventArgs e) => RecordDisposition("Accept Defect");
    private void OnRejectDefectClick(object sender, RoutedEventArgs e) => RecordDisposition("Reject Defect");

    private void RecordDisposition(string action)
    {
        if (_selectedPoint is not { } point)
        {
            MessageBox.Show("Load a sample height-map CSV and select a point first.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var message = $"{action}: 3D sample-data point x={point.X}, y={point.Y}, height={point.Height:F3}, file={Path.GetFileName(_currentCsvPath)}. 3D camera not connected.";
        WorkflowState.Instance.AddDisposition(message);
        StatusText.Text = $"{action} recorded in SQLite review events.";
    }

    private static List<string> SplitCsvLine(string line)
    {
        var cells = new List<string>();
        var sb = new System.Text.StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                cells.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }

        cells.Add(sb.ToString());
        return cells;
    }
}
