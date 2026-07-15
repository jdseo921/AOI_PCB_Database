using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using AOI_Monitor.ViewModels;
using Microsoft.Win32;

namespace AOI_Monitor.Views;

public partial class SettingsView
{
    private void BrowseCameraFolder(CameraViewType viewType)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Changing camera simulation folder"))
            return;

        var dialog = new OpenFolderDialog
        {
            Title = $"Select {viewType} camera simulation folder",
            Multiselect = false,
        };

        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FolderName))
            return;

        switch (viewType)
        {
            case CameraViewType.Side:
                CameraSideFolderText.Text = dialog.FolderName;
                break;
            case CameraViewType.Bottom:
                CameraBottomFolderText.Text = dialog.FolderName;
                break;
            default:
                CameraTopFolderText.Text = dialog.FolderName;
                break;
        }

        CameraSourceCombo.SelectedIndex = 1;
        RefreshCameraSourceUi(BuildCameraSourceSettingsFromUi());
    }

    private void OnTestCameraSourceClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Testing camera source"))
            return;

        var settings = BuildCameraSourceSettingsFromUi();
        var source = CameraSourceFactory.Create(settings);
        source.SelectedView = CameraViewType.Top;
        CameraFrame? frame = null;
        try
        {
            source.StartAcquisition();
            frame = source.GetNextFrame();
            source.StopAcquisition();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            CameraDiagnosticsText.Text = $"Camera test failed: {ex.Message}";
            MessageBox.Show(CameraDiagnosticsText.Text, "Camera Source Test", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var diagnostics = source is ICameraStatusDiagnostics statusDiagnostics
            ? string.Join(" ", statusDiagnostics.GetMessages())
            : source.StatusMessage;
        var frameMessage = frame is null
            ? "No frame returned."
            : $"Frame {frame.FrameId}, {frame.Width}x{frame.Height}, {frame.PixelFormat}, {frame.SourceKind}.";
        CameraDiagnosticsText.Text = $"{source.Name}: {CameraStatusDisplay(source.ConnectionStatus)}. {source.StatusMessage} {frameMessage} {diagnostics}";
        MessageBox.Show(
            CameraDiagnosticsText.Text,
            "Camera Source Test",
            MessageBoxButton.OK,
            source.ConnectionStatus is CameraSourceStatus.Ready or CameraSourceStatus.Simulated ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void OnBrowseCameraAdapterClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select external camera adapter plugin folder",
            Multiselect = false,
        };

        if (dialog.ShowDialog() == true)
        {
            CameraAdapterFolderText.Text = dialog.FolderName;
            CameraSourceCombo.SelectedIndex = 2;
            RefreshCameraSourceUi(BuildCameraSourceSettingsFromUi());
        }
    }

    private void OnDiscoverCameraAdaptersClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Discovering camera adapters"))
            return;

        var settings = BuildCameraSourceSettingsFromUi();
        var load = CameraAdapterPluginService.LoadFactory(settings.AdapterFolder);
        if (!load.Success || load.Factory is null)
        {
            CameraDiagnosticsText.Text = load.Message;
            MessageBox.Show(load.Message, "Camera Adapter Discovery", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        CameraSourceCombo.SelectedIndex = 2;
        CameraDiagnosticsText.Text =
            $"Loaded adapter {load.Factory.DisplayName} {load.Factory.Version}. " +
            $"Interfaces: {string.Join(", ", load.Factory.SupportedInterfaces)}. " +
            $"Pixel formats: {string.Join(", ", load.Factory.SupportedPixelFormats)}. " +
            "Adapter loading is opt-in and does not claim real hardware readiness until camera acceptance captures real hardware frames.";
        MessageBox.Show(CameraDiagnosticsText.Text, "Camera Adapter Discovery", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnDiscoverCamerasClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Discovering cameras"))
            return;

        var settings = BuildCameraSourceSettingsFromUi();
        var discovery = VisionCameraPluginLoader.CreateDiscovery(settings, out var loadMessage);
        IReadOnlyList<VisionDeviceInfo> devices;
        try
        {
            devices = discovery.DiscoverDevices();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            CameraDiagnosticsText.Text = $"Camera discovery failed safely: {ex.Message}";
            MessageBox.Show(CameraDiagnosticsText.Text, "Camera Discovery", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        AssignDiscoveredDevices(devices);
        var summary = devices.Count == 0
            ? "No cameras discovered."
            : string.Join(Environment.NewLine, devices.Select(device => $"{device.SuggestedView}: {device.DeviceId} {device.Vendor} {device.Model} {device.InterfaceType} {device.Status} {string.Join("/", device.Capabilities ?? Array.Empty<string>())}"));
        CameraDiagnosticsText.Text = $"{loadMessage} Discovered {devices.Count} device(s). {summary}";
        MessageBox.Show(CameraDiagnosticsText.Text, "Camera Discovery", MessageBoxButton.OK, devices.Any(d => !string.IsNullOrWhiteSpace(d.DeviceId)) ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void OnExportHardwareAcceptanceClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Exporting hardware acceptance package"))
            return;

        try
        {
            var root = Path.Combine(AoiDatabase.StorageRoot, "exports", "hardware_acceptance", DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(root);
            var camera = _lastCameraAcceptanceRun ?? AoiDatabase.GetLatestCameraAcceptanceRun();
            var lighting = _lastLightingAcceptanceRun ?? AoiDatabase.GetLatestLightingAcceptanceRun();
            var manifest = new
            {
                schemaVersion = "hardware-acceptance-package/v1",
                generatedAtUtc = DateTime.UtcNow,
                cameraIncluded = camera is not null,
                lightingIncluded = lighting is not null,
                limitation = "Fake, null, folder, or simulated evidence does not validate real GigE/USB3 cameras or real lighting controllers.",
            };

            File.WriteAllText(Path.Combine(root, "hardware_acceptance_manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            if (camera is not null)
                File.WriteAllText(Path.Combine(root, "latest_camera_acceptance.json"), JsonSerializer.Serialize(camera, new JsonSerializerOptions { WriteIndented = true }));
            if (lighting is not null)
                File.WriteAllText(Path.Combine(root, "latest_lighting_acceptance.json"), JsonSerializer.Serialize(lighting, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(root, "README.txt"), "Hardware acceptance evidence is scoped to the recorded adapters/controllers. Simulation-only evidence must not be presented as real production hardware validation.");

            MessageBox.Show($"Hardware acceptance package exported:\n{root}", "Hardware Acceptance", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show($"Hardware acceptance export failed:\n{ex.Message}", "Hardware Acceptance", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void AssignDiscoveredDevices(IReadOnlyList<VisionDeviceInfo> devices)
    {
        foreach (var device in devices.Where(device => !string.IsNullOrWhiteSpace(device.DeviceId)))
        {
            switch (device.SuggestedView.Trim().ToLowerInvariant())
            {
                case "side":
                    CameraSideDeviceIdText.Text = device.DeviceId;
                    break;
                case "bottom":
                    CameraBottomDeviceIdText.Text = device.DeviceId;
                    break;
                case "top":
                    CameraTopDeviceIdText.Text = device.DeviceId;
                    break;
            }
        }
    }

    private async void OnRunCameraAcceptanceClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Running camera acceptance test"))
            return;

        if (_cameraAcceptanceCancellation is not null)
            return;

        var settings = BuildCameraSourceSettingsFromUi();
        var criteria = new CameraAcceptanceCriteria
        {
            FramesPerView = 5,
            RequiredViews = new() { "Top", "Side", "Bottom" },
        };
        _cameraAcceptanceCancellation = new CancellationTokenSource();
        RefreshRoleControls();
        CameraAcceptanceStatusText.Text = "Acceptance: RUNNING";
        CameraAcceptanceStatusText.Foreground = Brushes.Gold;
        CameraDiagnosticsText.Text = "Camera acceptance test running...";

        var progress = new Progress<string>(message => CameraDiagnosticsText.Text = message);
        try
        {
            var token = _cameraAcceptanceCancellation.Token;
            var run = await Task.Run(() => CameraAcceptanceTestService.Run(settings, criteria, progress: progress, cancellationToken: token), token);
            AoiDatabase.RecordCameraAcceptanceRun(run, WorkflowState.Instance.OperatorWithRole);
            _lastCameraAcceptanceRun = run;
            WorkflowState.Instance.AddEvent("CAMERA_ACCEPTANCE", $"Camera acceptance: {run.Status}; readiness {run.FactoryReadinessStatus}; frames {run.TotalReceivedFrames}/{run.TotalRequestedFrames}.");
            CameraAcceptanceStatusText.Text = $"Acceptance: {run.Status} / {run.FactoryReadinessStatus}";
            CameraAcceptanceStatusText.Foreground = run.Status switch
            {
                "PASS" => Brushes.LightGreen,
                "WARN" => Brushes.Gold,
                _ => Brushes.IndianRed,
            };
            CameraDiagnosticsText.Text = BuildCameraAcceptanceUiSummary(run);
            MessageBox.Show(
                CameraDiagnosticsText.Text,
                "Camera Acceptance Test",
                MessageBoxButton.OK,
                run.Status == "FAIL" ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            CameraAcceptanceStatusText.Text = "Acceptance: CANCELED";
            CameraAcceptanceStatusText.Foreground = Brushes.Gold;
            CameraDiagnosticsText.Text = "Camera acceptance test canceled.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            CameraAcceptanceStatusText.Text = "Acceptance: ERROR";
            CameraAcceptanceStatusText.Foreground = Brushes.IndianRed;
            CameraDiagnosticsText.Text = $"Camera acceptance failed: {ex.Message}";
            MessageBox.Show(CameraDiagnosticsText.Text, "Camera Acceptance Test", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _cameraAcceptanceCancellation?.Dispose();
            _cameraAcceptanceCancellation = null;
            RefreshRoleControls();
        }
    }

    private void OnCancelCameraAcceptanceClick(object sender, RoutedEventArgs e)
    {
        _cameraAcceptanceCancellation?.Cancel();
    }

    private void OnExportCameraAcceptanceClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Exporting camera acceptance report"))
            return;

        var run = _lastCameraAcceptanceRun ?? AoiDatabase.GetLatestCameraAcceptanceRun();
        if (run is null)
        {
            MessageBox.Show("No camera acceptance run is available to export.", "Camera Acceptance", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var export = CameraAcceptanceTestService.ExportReport(run);
            WorkflowState.Instance.AddEvent("CAMERA_ACCEPTANCE_EXPORT", $"Camera acceptance report exported: {Path.GetFileName(export.JsonPath)}.");
            MessageBox.Show(
                $"Camera acceptance report exported.\n\nJSON: {export.JsonPath}\nHTML: {export.HtmlPath}",
                "Camera Acceptance",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show($"Camera acceptance export failed:\n{ex.Message}", "Camera Acceptance", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string BuildCameraAcceptanceUiSummary(CameraAcceptanceRun run)
    {
        var firstMessage = run.Failures.Concat(run.Warnings).FirstOrDefault();
        var evidenceBoundary = run.IsRealHardware
            ? "Real hardware acceptance evidence recorded."
            : "Simulation-only evidence; real GigE/USB3 camera readiness is NOT VALIDATED.";
        return $"Camera acceptance {run.Status}; readiness {run.FactoryReadinessStatus}; frames {run.TotalReceivedFrames}/{run.TotalRequestedFrames}; dropped {run.DroppedFrameCount}; trigger failures {run.TriggerFailureCount}; timeouts {run.TimeoutCount}. {evidenceBoundary} {firstMessage}";
    }

    private void OnOpenTrainingFolderClick(object sender, RoutedEventArgs e)
    {
        var trainingDir = AoiDatabase.TrainingVaultPath;
        Directory.CreateDirectory(trainingDir);

        Process.Start(new ProcessStartInfo
        {
            FileName = trainingDir,
            UseShellExecute = true,
        });
    }

}
