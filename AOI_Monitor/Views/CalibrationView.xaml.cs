using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Microsoft.Win32;

namespace AOI_Monitor.Views;

public partial class CalibrationView : UserControl
{
    private readonly ObservableCollection<CalibrationPointRow> _points = new();
    private bool _loadingProfile;
    private BitmapSource? _sampleBitmap;

    public CalibrationView()
    {
        InitializeComponent();
        PointsGrid.ItemsSource = _points;
        ProfileNameText.Text = $"{WorkflowState.Instance.BoardProgram} 2D Top Calibration";
        BoardModelText.Text = WorkflowState.Instance.BoardProgram;
        ReloadProfiles();
        UpdateTransformStatus();
    }

    public void RefreshFromState()
    {
        if (string.IsNullOrWhiteSpace(BoardModelText.Text))
            BoardModelText.Text = WorkflowState.Instance.BoardProgram;

        ReloadProfiles();
    }

    private void OnReloadProfilesClick(object sender, RoutedEventArgs e) => ReloadProfiles();

    private void OnBrowseSampleImageClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select sample calibration image",
            Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp|All files|*.*",
        };

        if (dialog.ShowDialog() != true)
            return;

        SampleImagePathText.Text = dialog.FileName;
        LoadCalibrationImage(dialog.FileName);
    }

    private void OnCalibrationImageClick(object sender, MouseButtonEventArgs e)
    {
        if (_sampleBitmap is null || CalibrationImage.ActualWidth <= 0 || CalibrationImage.ActualHeight <= 0)
            return;

        var position = e.GetPosition(CalibrationImage);
        var scale = Math.Min(CalibrationImage.ActualWidth / _sampleBitmap.PixelWidth, CalibrationImage.ActualHeight / _sampleBitmap.PixelHeight);
        var renderedWidth = _sampleBitmap.PixelWidth * scale;
        var renderedHeight = _sampleBitmap.PixelHeight * scale;
        var offsetX = (CalibrationImage.ActualWidth - renderedWidth) / 2.0;
        var offsetY = (CalibrationImage.ActualHeight - renderedHeight) / 2.0;
        var imageX = (position.X - offsetX) / scale;
        var imageY = (position.Y - offsetY) / scale;

        if (imageX < 0 || imageY < 0 || imageX > _sampleBitmap.PixelWidth || imageY > _sampleBitmap.PixelHeight)
            return;

        ImageXText.Text = imageX.ToString("F1", CultureInfo.InvariantCulture);
        ImageYText.Text = imageY.ToString("F1", CultureInfo.InvariantCulture);
        TransformStatusText.Text = "Image X/Y filled from preview click. Enter matching board-mm coordinates, then Add Point.";
    }

    private void OnAddPointClick(object sender, RoutedEventArgs e)
    {
        if (!TryParsePoint(out var point))
            return;

        _points.Add(new CalibrationPointRow(
            _points.Count + 1,
            point.ImageX,
            point.ImageY,
            point.BoardXMillimeters,
            point.BoardYMillimeters));

        ImageXText.Clear();
        ImageYText.Clear();
        BoardXText.Clear();
        BoardYText.Clear();
        UpdateTransformStatus();
    }

    private void OnRemoveSelectedPointClick(object sender, RoutedEventArgs e)
    {
        if (PointsGrid.SelectedItem is not CalibrationPointRow row)
            return;

        _points.Remove(row);
        RenumberPoints();
        UpdateTransformStatus();
    }

    private void OnSaveProfileClick(object sender, RoutedEventArgs e)
    {
        var state = WorkflowState.Instance;
        if (!state.TryAuthorize(RoleAuthorization.CanEditCalibration, "Saving calibration profile", out var message))
        {
            MessageBox.Show(message, "Permission Denied", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_points.Count == 0)
        {
            MessageBox.Show("Add at least one calibration point before saving a profile.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var profileId = AoiDatabase.SaveCalibrationProfile(
                ProfileNameText.Text,
                BoardModelText.Text,
                SelectedViewType(),
                SampleImagePathText.Text,
                state.OperatorWithRole,
                _points.Select(point => new CalibrationPointInput(
                    point.ImageX,
                    point.ImageY,
                    point.BoardXMillimeters,
                    point.BoardYMillimeters)).ToArray());

            state.AddEvent("CALIBRATION", $"2D calibration profile saved: {ProfileNameText.Text}, id {profileId}, points {_points.Count}. Stage 2 preparation only.");
            ReloadProfiles(profileId);
            MessageBox.Show("2D calibration profile saved to SQLite.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            state.AddEvent("CALIBRATION_ERROR", $"Calibration profile save failed: {ex.Message}");
            MessageBox.Show($"Calibration profile save failed:\n{ex.Message}", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnProfileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingProfile || ProfileCombo.SelectedItem is not CalibrationProfileRecord profile)
            return;

        LoadProfile(profile);
    }

    private void ReloadProfiles(long? selectedProfileId = null)
    {
        try
        {
            _loadingProfile = true;
            var profiles = AoiDatabase.GetCalibrationProfiles();
            ProfileCombo.ItemsSource = profiles;
            ProfileCombo.SelectedItem = selectedProfileId is null
                ? profiles.FirstOrDefault()
                : profiles.FirstOrDefault(profile => profile.Id == selectedProfileId.Value) ?? profiles.FirstOrDefault();
        }
        catch (Exception ex)
        {
            TransformStatusText.Text = $"Calibration profiles could not be loaded: {ex.Message}";
            WorkflowState.Instance.AddEvent("CALIBRATION_ERROR", TransformStatusText.Text);
        }
        finally
        {
            _loadingProfile = false;
        }

        if (ProfileCombo.SelectedItem is CalibrationProfileRecord selected)
            LoadProfile(selected);
    }

    private void LoadProfile(CalibrationProfileRecord profile)
    {
        _loadingProfile = true;
        try
        {
            ProfileNameText.Text = profile.ProfileName;
            BoardModelText.Text = profile.BoardModel;
            SetSelectedViewType(profile.ViewType);
            SampleImagePathText.Text = profile.SampleImagePath;
            _points.Clear();
            var index = 1;
            foreach (var point in profile.Points)
            {
                _points.Add(new CalibrationPointRow(
                    index++,
                    point.ImageX,
                    point.ImageY,
                    point.BoardXMillimeters,
                    point.BoardYMillimeters));
            }

            if (File.Exists(profile.SampleImagePath))
                LoadCalibrationImage(profile.SampleImagePath);
            else
                ClearCalibrationImage(profile.SampleImagePath.Length == 0 ? "No sample image path saved with this profile." : "Saved calibration image is missing.");

            SavedProfileText.Text = string.Join(Environment.NewLine,
                $"Profile: {profile.DisplayName}",
                $"Saved UTC: {profile.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture)}",
                $"Operator: {profile.OperatorId}",
                profile.TransformSummary);
            UpdateTransformStatus();
        }
        finally
        {
            _loadingProfile = false;
        }
    }

    private bool TryParsePoint(out CalibrationPointInput point)
    {
        point = new CalibrationPointInput(0, 0, 0, 0);
        if (!TryParseDouble(ImageXText.Text, out var imageX) ||
            !TryParseDouble(ImageYText.Text, out var imageY) ||
            !TryParseDouble(BoardXText.Text, out var boardX) ||
            !TryParseDouble(BoardYText.Text, out var boardY))
        {
            MessageBox.Show("Enter numeric image X/Y and board X/Y values. Board coordinates are in millimeters.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        point = new CalibrationPointInput(imageX, imageY, boardX, boardY);
        return true;
    }

    private void UpdateTransformStatus()
    {
        var transform = CalibrationTransformService.Calculate(_points.Select(point => new CalibrationPointInput(
            point.ImageX,
            point.ImageY,
            point.BoardXMillimeters,
            point.BoardYMillimeters)).ToArray());

        TransformStatusText.Text = transform.Summary;
        TransformStatusText.Foreground = transform.IsAvailable
            ? System.Windows.Media.Brushes.LightGreen
            : System.Windows.Media.Brushes.Orange;
    }

    private void LoadCalibrationImage(string imagePath)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(imagePath, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();

            _sampleBitmap = bitmap;
            CalibrationImage.Source = bitmap;
            EmptyImageText.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            ClearCalibrationImage($"Calibration image could not be loaded: {ex.Message}");
        }
    }

    private void ClearCalibrationImage(string message)
    {
        _sampleBitmap = null;
        CalibrationImage.Source = null;
        EmptyImageText.Text = message;
        EmptyImageText.Visibility = Visibility.Visible;
    }

    private void RenumberPoints()
    {
        for (var i = 0; i < _points.Count; i++)
            _points[i].No = i + 1;

        PointsGrid.Items.Refresh();
    }

    private string SelectedViewType()
        => (ViewTypeCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Top";

    private void SetSelectedViewType(string viewType)
    {
        for (var i = 0; i < ViewTypeCombo.Items.Count; i++)
        {
            if (ViewTypeCombo.Items[i] is ComboBoxItem item &&
                string.Equals(item.Content?.ToString(), viewType, StringComparison.OrdinalIgnoreCase))
            {
                ViewTypeCombo.SelectedIndex = i;
                return;
            }
        }

        ViewTypeCombo.SelectedIndex = 0;
    }

    private static bool TryParseDouble(string text, out double value)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    public sealed class CalibrationPointRow
    {
        public CalibrationPointRow(int no, double imageX, double imageY, double boardXMillimeters, double boardYMillimeters)
        {
            No = no;
            ImageX = imageX;
            ImageY = imageY;
            BoardXMillimeters = boardXMillimeters;
            BoardYMillimeters = boardYMillimeters;
        }

        public int No { get; set; }
        public double ImageX { get; }
        public double ImageY { get; }
        public double BoardXMillimeters { get; }
        public double BoardYMillimeters { get; }
        public string ImageXDisplay => ImageX.ToString("F2", CultureInfo.InvariantCulture);
        public string ImageYDisplay => ImageY.ToString("F2", CultureInfo.InvariantCulture);
        public string BoardXDisplay => BoardXMillimeters.ToString("F3", CultureInfo.InvariantCulture);
        public string BoardYDisplay => BoardYMillimeters.ToString("F3", CultureInfo.InvariantCulture);
    }
}
