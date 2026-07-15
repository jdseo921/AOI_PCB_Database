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
    private void SaveCameraSourceSettings(CameraSourceSettings? preparedSettings = null)
    {
        var settings = preparedSettings ?? BuildCameraSourceSettingsFromUi();
        CameraSourceSettingsService.Save(settings);
        CameraSourceSettingsService.ApplyActiveSource();

        WorkflowState.Instance.AddEvent(
            "CAMERA_CONFIG",
            settings.SourceKey switch
            {
                CameraSourceFactory.FolderSimulationSourceKey => $"Camera source set to Folder Simulation; status {CameraSourceFactory.ActiveSource.ConnectionStatus}.",
                CameraSourceFactory.GenericVisionAdapterSourceKey => $"Camera source set to Generic Vision Adapter; status {CameraSourceFactory.ActiveSource.ConnectionStatus}. Adapter configured does not imply camera connected.",
                _ => "Camera source set to No Camera / Not Connected.",
            });
    }

    private void RefreshCameraSourceUi()
        => RefreshCameraSourceUi(CameraSourceSettingsService.Load());

    private void RefreshCameraSourceUi(CameraSourceSettings settings)
    {
        CameraSourceCombo.SelectedIndex = settings.SourceKey switch
        {
            CameraSourceFactory.FolderSimulationSourceKey => 1,
            CameraSourceFactory.GenericVisionAdapterSourceKey => 2,
            _ => 0,
        };
        CameraTopFolderText.Text = settings.TopFolder;
        CameraSideFolderText.Text = settings.SideFolder;
        CameraBottomFolderText.Text = settings.BottomFolder;
        CameraTopDeviceIdText.Text = settings.TopDeviceId;
        CameraSideDeviceIdText.Text = settings.SideDeviceId;
        CameraBottomDeviceIdText.Text = settings.BottomDeviceId;
        CameraAdapterFolderText.Text = settings.AdapterFolder;
        CameraAcquisitionModeCombo.SelectedIndex = settings.AcquisitionMode switch
        {
            CameraAcquisitionMode.SoftwareTrigger => 1,
            CameraAcquisitionMode.HardwareTrigger => 2,
            _ => 0,
        };
        CameraExposureMsText.Text = settings.ExposureMs.ToString("0.###", CultureInfo.InvariantCulture);
        CameraGainText.Text = settings.Gain.ToString("0.###", CultureInfo.InvariantCulture);
        CameraTriggerTimeoutMsText.Text = settings.TriggerTimeoutMs.ToString(CultureInfo.InvariantCulture);
        CameraFrameTimeoutMsText.Text = settings.FrameTimeoutMs.ToString(CultureInfo.InvariantCulture);
        CameraBoardModelText.Text = settings.BoardModel;
        CameraLotIdText.Text = settings.LotId;

        var source = CameraSourceFactory.Create(settings);
        CameraSourceStatusText.Text = $"Camera: {CameraStatusDisplay(source.ConnectionStatus)}";
        CameraSourceStatusText.Foreground = StatusBrush(source.ConnectionStatus);
        var latest = _lastCameraAcceptanceRun ?? AoiDatabase.GetLatestCameraAcceptanceRun();
        CameraAcceptanceStatusText.Text = latest is null
            ? "Acceptance: Not validated"
            : $"Acceptance: {latest.Status} / {latest.FactoryReadinessStatus}";
        CameraAcceptanceStatusText.Foreground = latest?.Status switch
        {
            "PASS" => Brushes.LightGreen,
            "WARN" => Brushes.Gold,
            "FAIL" => Brushes.IndianRed,
            _ => Brushes.Gold,
        };
        CameraDiagnosticsText.Text = latest is null
            ? source.StatusMessage
            : $"{source.StatusMessage} Latest acceptance: {latest.Status}; readiness {latest.FactoryReadinessStatus}; frames {latest.TotalReceivedFrames}/{latest.TotalRequestedFrames}.";
    }

    private CameraSourceSettings BuildCameraSourceSettingsFromUi()
        => new()
        {
            SourceKey = CameraSourceCombo.SelectedIndex switch
            {
                1 => CameraSourceFactory.FolderSimulationSourceKey,
                2 => CameraSourceFactory.GenericVisionAdapterSourceKey,
                _ => CameraSourceFactory.NullSourceKey,
            },
            TopFolder = CameraTopFolderText.Text.Trim(),
            SideFolder = CameraSideFolderText.Text.Trim(),
            BottomFolder = CameraBottomFolderText.Text.Trim(),
            TopDeviceId = CameraTopDeviceIdText.Text.Trim(),
            SideDeviceId = CameraSideDeviceIdText.Text.Trim(),
            BottomDeviceId = CameraBottomDeviceIdText.Text.Trim(),
            AdapterFolder = CameraAdapterFolderText.Text.Trim(),
            AcquisitionMode = CameraAcquisitionModeCombo.SelectedIndex switch
            {
                1 => CameraAcquisitionMode.SoftwareTrigger,
                2 => CameraAcquisitionMode.HardwareTrigger,
                _ => CameraAcquisitionMode.Continuous,
            },
            ExposureMs = double.TryParse(CameraExposureMsText.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var exposureMs) ? exposureMs : 5.0,
            Gain = double.TryParse(CameraGainText.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var gain) ? gain : 1.0,
            TriggerTimeoutMs = int.TryParse(CameraTriggerTimeoutMsText.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var triggerTimeoutMs) ? triggerTimeoutMs : 250,
            FrameTimeoutMs = int.TryParse(CameraFrameTimeoutMsText.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var frameTimeoutMs) ? frameTimeoutMs : 1000,
            BoardModel = CameraBoardModelText.Text.Trim(),
            LotId = CameraLotIdText.Text.Trim(),
        };

    private void SaveLightingSettings(LightingSettings? preparedSettings = null)
    {
        var settings = preparedSettings ?? BuildLightingSettingsFromUi();
        var validation = LightingSettingsService.Validate(settings);
        if (validation.Count > 0)
        {
            LightingDiagnosticsText.Text = string.Join(" ", validation);
            WorkflowState.Instance.AddEvent("LIGHTING_CONFIG_WARNING", LightingDiagnosticsText.Text);
            return;
        }

        LightingSettingsService.Save(settings);
        LightingSettingsService.ApplyIntegrationBoundary();

        WorkflowState.Instance.AddEvent(
            "LIGHTING_CONFIG",
            settings.Mode switch
            {
                LightingModes.Simulated => "Lighting set to Simulated. No real lighting command will be sent.",
                LightingModes.TcpText => $"Lighting set to TCP text protocol endpoint {settings.TcpHost}:{settings.TcpPort}.",
                LightingModes.SerialText => $"Lighting set to Serial text protocol endpoint {settings.SerialPortName}.",
                _ => "Lighting set to None / Not Connected.",
            });
    }

    private void RefreshLightingUi()
        => RefreshLightingUi(LightingSettingsService.Load());

    private void RefreshLightingUi(LightingSettings settings)
    {
        LightingModeCombo.SelectedIndex = settings.Mode switch
        {
            LightingModes.Simulated => 1,
            LightingModes.TcpText => 2,
            LightingModes.SerialText => 3,
            _ => 0,
        };
        LightingTcpHostText.Text = settings.TcpHost;
        LightingTcpPortText.Text = settings.TcpPort.ToString(CultureInfo.InvariantCulture);
        LightingSerialPortText.Text = settings.SerialPortName;
        LightingBaudRateText.Text = settings.BaudRate.ToString(CultureInfo.InvariantCulture);
        LightingTopProgramText.Text = settings.TopProgram;
        LightingSideProgramText.Text = settings.SideProgram;
        LightingBottomProgramText.Text = settings.BottomProgram;
        LightingCommandTemplateText.Text = settings.CommandTemplate;
        LightingTimeoutMsText.Text = settings.ResponseTimeoutMs.ToString(CultureInfo.InvariantCulture);

        var controller = LightingControllerFactory.Create(settings);
        LightingSettingsStatusText.Text = $"Lighting: {IntegrationStatusDisplay(controller.Status)}";
        LightingSettingsStatusText.Foreground = IntegrationStatusBrush(controller.Status);
        var validation = LightingSettingsService.Validate(settings);
        var latest = _lastLightingAcceptanceRun ?? AoiDatabase.GetLatestLightingAcceptanceRun();
        LightingAcceptanceStatusText.Text = latest is null
            ? "Sync: Not validated"
            : $"Sync: {latest.Status} ({(latest.IsSimulated ? "simulated" : "configured")})";
        LightingAcceptanceStatusText.Foreground = latest?.Status switch
        {
            "PASS" => Brushes.LightGreen,
            "WARN" => Brushes.Gold,
            "FAIL" => Brushes.IndianRed,
            _ => Brushes.Gold,
        };
        var baseMessage = validation.Count == 0
            ? controller.StatusMessage
            : string.Join(" ", validation);
        LightingDiagnosticsText.Text = latest is null
            ? baseMessage
            : $"{baseMessage} Latest sync acceptance: {latest.Status}; steps {latest.PassedStepCount}/{latest.StepCount}; simulated={latest.IsSimulated}.";
    }

    private async void OnTestLightingClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Testing lighting synchronization"))
            return;

        var settings = BuildLightingSettingsFromUi();
        var validation = LightingSettingsService.Validate(settings);
        if (validation.Count > 0)
        {
            LightingDiagnosticsText.Text = string.Join(" ", validation);
            MessageBox.Show(LightingDiagnosticsText.Text, "Lighting Test", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var button = sender as Button;
        await ErrorBoundaryService.SafeAsyncCommand(
            "Test lighting synchronization",
            "Settings",
            async token =>
            {
                var controller = LightingControllerFactory.Create(settings);
                var result = await LightingSynchronizationService.SynchronizeAsync(controller, settings, "Top", token);
                LightingDiagnosticsText.Text = $"{result.Message} Status={result.Status}.";
                LightingSettingsStatusText.Text = $"Lighting: {IntegrationStatusDisplay(result.Status)}";
                LightingSettingsStatusText.Foreground = IntegrationStatusBrush(result.Status);
                WorkflowState.Instance.AddEvent("LIGHTING_TEST", LightingDiagnosticsText.Text);
                MessageBox.Show(
                    LightingDiagnosticsText.Text,
                    "Lighting Test",
                    MessageBoxButton.OK,
                    result.Status is IntegrationConnectionStatus.Ready or IntegrationConnectionStatus.Simulated ? MessageBoxImage.Information : MessageBoxImage.Warning);
            },
            running =>
            {
                if (button is not null)
                    button.IsEnabled = !running;
            },
            message =>
            {
                LightingDiagnosticsText.Text = message;
                LightingSettingsStatusText.Text = "Lighting: Error";
                LightingSettingsStatusText.Foreground = Brushes.IndianRed;
            });
    }

    private async void OnRunLightingAcceptanceClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Running lighting sync acceptance test"))
            return;

        if (_lightingAcceptanceCancellation is not null)
            return;

        var settings = BuildLightingSettingsFromUi();
        var validation = LightingSettingsService.Validate(settings);
        if (validation.Count > 0)
        {
            LightingDiagnosticsText.Text = string.Join(" ", validation);
            MessageBox.Show(LightingDiagnosticsText.Text, "Lighting Sync Acceptance", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _lightingAcceptanceCancellation = new CancellationTokenSource();
        RefreshRoleControls();
        LightingAcceptanceStatusText.Text = "Sync: RUNNING";
        LightingAcceptanceStatusText.Foreground = Brushes.Gold;
        LightingDiagnosticsText.Text = "Lighting sync acceptance test running...";

        var progress = new Progress<string>(message => LightingDiagnosticsText.Text = message);
        try
        {
            var token = _lightingAcceptanceCancellation.Token;
            var run = await LightingAcceptanceTestService.RunAsync(
                settings,
                new LightingAcceptanceCriteria { RequiredViews = new() { "Top", "Side", "Bottom" } },
                cameraSource: CameraSourceFactory.ActiveSource,
                progress: progress,
                cancellationToken: token);
            AoiDatabase.RecordLightingAcceptanceRun(run, WorkflowState.Instance.OperatorWithRole);
            _lastLightingAcceptanceRun = run;
            WorkflowState.Instance.AddEvent("LIGHTING_ACCEPTANCE", $"Lighting sync acceptance: {run.Status}; steps {run.PassedStepCount}/{run.StepCount}; simulated={run.IsSimulated}.");
            LightingAcceptanceStatusText.Text = $"Sync: {run.Status} ({(run.IsSimulated ? "simulated" : "configured")})";
            LightingAcceptanceStatusText.Foreground = run.Status switch
            {
                "PASS" => Brushes.LightGreen,
                "WARN" => Brushes.Gold,
                _ => Brushes.IndianRed,
            };
            LightingDiagnosticsText.Text = BuildLightingAcceptanceUiSummary(run);
            MessageBox.Show(
                LightingDiagnosticsText.Text,
                "Lighting Sync Acceptance",
                MessageBoxButton.OK,
                run.Status == "FAIL" ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            LightingAcceptanceStatusText.Text = "Sync: CANCELED";
            LightingAcceptanceStatusText.Foreground = Brushes.Gold;
            LightingDiagnosticsText.Text = "Lighting sync acceptance test canceled.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            LightingAcceptanceStatusText.Text = "Sync: ERROR";
            LightingAcceptanceStatusText.Foreground = Brushes.IndianRed;
            LightingDiagnosticsText.Text = $"Lighting sync acceptance failed: {ex.Message}";
            MessageBox.Show(LightingDiagnosticsText.Text, "Lighting Sync Acceptance", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _lightingAcceptanceCancellation?.Dispose();
            _lightingAcceptanceCancellation = null;
            RefreshRoleControls();
        }
    }

    private void OnCancelLightingAcceptanceClick(object sender, RoutedEventArgs e)
    {
        _lightingAcceptanceCancellation?.Cancel();
    }

    private void OnExportLightingAcceptanceClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Exporting lighting sync acceptance report"))
            return;

        var run = _lastLightingAcceptanceRun ?? AoiDatabase.GetLatestLightingAcceptanceRun();
        if (run is null)
        {
            MessageBox.Show("No lighting sync acceptance run is available to export.", "Lighting Sync Acceptance", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var export = LightingAcceptanceTestService.ExportReport(run);
            WorkflowState.Instance.AddEvent("LIGHTING_ACCEPTANCE_EXPORT", $"Lighting sync acceptance report exported: {Path.GetFileName(export.JsonPath)}.");
            MessageBox.Show(
                $"Lighting sync acceptance report exported.\n\nJSON: {export.JsonPath}\nHTML: {export.HtmlPath}",
                "Lighting Sync Acceptance",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show($"Lighting sync acceptance export failed:\n{ex.Message}", "Lighting Sync Acceptance", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string BuildLightingAcceptanceUiSummary(LightingAcceptanceRun run)
    {
        var firstMessage = run.Failures.Concat(run.Warnings).FirstOrDefault();
        var boundary = run.IsSimulated
            ? "Simulated result; real lighting controller readiness is not claimed."
            : "Configured controller result; verify physical wiring and light output externally.";
        return $"Lighting sync acceptance {run.Status}; steps {run.PassedStepCount}/{run.StepCount}; max command {run.MaxCommandLatencyMs:F1} ms; max trigger-to-frame {run.MaxTriggerToFrameLatencyMs:F1} ms. {boundary} {firstMessage}";
    }

    private async void OnRunRobotCellAcceptanceClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Running robot cell acceptance"))
            return;

        if (_robotAcceptanceCancellation is not null)
            return;

        _robotAcceptanceCancellation = new CancellationTokenSource();
        RefreshRoleControls();
        RobotAcceptanceStatusText.Text = "Acceptance: RUNNING";
        RobotAcceptanceStatusText.Foreground = Brushes.Gold;
        RobotAcceptanceDiagnosticsText.Text = "Robot cell acceptance running...";
        var progress = new Progress<string>(message => RobotAcceptanceDiagnosticsText.Text = message);

        try
        {
            var token = _robotAcceptanceCancellation.Token;
            var run = await RobotCellAcceptanceTestService.RunAsync(progress: progress, cancellationToken: token);
            AoiDatabase.RecordRobotAcceptanceRun(run, WorkflowState.Instance.OperatorWithRole);
            _lastRobotAcceptanceRun = run;
            WorkflowState.Instance.AddEvent("ROBOT_CELL_ACCEPTANCE", $"Robot cell acceptance {run.Status}; source={run.SourceKind}; safety={run.SafetySourceKind}; eStopBlocked={run.EmergencyStopBlocked}; safetyFaultBlocked={run.SafetyFaultBlocked}.");
            RobotAcceptanceStatusText.Text = $"Acceptance: {run.Status} ({run.SourceKind}/{run.SafetySourceKind})";
            RobotAcceptanceStatusText.Foreground = run.Status == "PASS" ? Brushes.LightGreen : Brushes.IndianRed;
            RobotAcceptanceDiagnosticsText.Text = BuildRobotAcceptanceUiSummary(run);
            MessageBox.Show(
                RobotAcceptanceDiagnosticsText.Text,
                "Robot Cell Acceptance",
                MessageBoxButton.OK,
                run.Status == "PASS" ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (OperationCanceledException)
        {
            RobotAcceptanceStatusText.Text = "Acceptance: CANCELED";
            RobotAcceptanceStatusText.Foreground = Brushes.Gold;
            RobotAcceptanceDiagnosticsText.Text = "Robot cell acceptance canceled.";
            WorkflowState.Instance.AddEvent("ROBOT_CELL_ACCEPTANCE", "Robot cell acceptance canceled by user.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            RobotAcceptanceStatusText.Text = "Acceptance: ERROR";
            RobotAcceptanceStatusText.Foreground = Brushes.IndianRed;
            RobotAcceptanceDiagnosticsText.Text = $"Robot cell acceptance failed: {ex.Message}";
            WorkflowState.Instance.AddEvent("ROBOT_CELL_ACCEPTANCE_ERROR", RobotAcceptanceDiagnosticsText.Text);
            MessageBox.Show(RobotAcceptanceDiagnosticsText.Text, "Robot Cell Acceptance", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _robotAcceptanceCancellation?.Dispose();
            _robotAcceptanceCancellation = null;
            RefreshRoleControls();
        }
    }

    private void OnCancelRobotCellAcceptanceClick(object sender, RoutedEventArgs e)
    {
        _robotAcceptanceCancellation?.Cancel();
    }

    private void OnExportRobotCellAcceptanceClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Exporting robot cell acceptance report"))
            return;

        var run = _lastRobotAcceptanceRun ?? AoiDatabase.GetLatestRobotAcceptanceRun();
        if (run is null)
        {
            MessageBox.Show("No robot cell acceptance run is available to export.", "Robot Cell Acceptance", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var export = RobotCellAcceptanceTestService.ExportReport(run);
            ExportVerificationService.RecordVerifiedExport("RobotCellAcceptanceJson", export.JsonPath, run.Status == "PASS" ? "OK" : "WARN");
            ExportVerificationService.RecordVerifiedExport("RobotCellAcceptanceHtml", export.HtmlPath, run.Status == "PASS" ? "OK" : "WARN");
            WorkflowState.Instance.AddEvent("ROBOT_CELL_ACCEPTANCE_EXPORT", $"Robot cell acceptance report exported: {Path.GetFileName(export.JsonPath)}.");
            MessageBox.Show(
                $"Robot cell acceptance report exported.\n\nJSON: {export.JsonPath}\nHTML: {export.HtmlPath}",
                "Robot Cell Acceptance",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show($"Robot cell acceptance export failed:\n{ex.Message}", "Robot Cell Acceptance", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string BuildRobotAcceptanceUiSummary(RobotAcceptanceRun run)
    {
        var firstMessage = run.Failures.Concat(run.Warnings).FirstOrDefault() ?? string.Empty;
        var boundary = run.SourceKind == "Real" && run.SafetySourceKind == "Real"
            ? "Configured real robot/PLC boundary evidence recorded; safety certification remains external."
            : "Simulation/not-connected evidence only; no real production robot movement or safety certification was validated.";
        return $"Robot cell acceptance {run.Status}; source={run.SourceKind}; safety={run.SafetySourceKind}; fullCycle={run.FullCycleMs:F1} ms; emergencyStopBlocked={run.EmergencyStopBlocked}; safetyFaultBlocked={run.SafetyFaultBlocked}. {boundary} {firstMessage}";
    }

    private LightingSettings BuildLightingSettingsFromUi()
        => new()
        {
            Mode = LightingModeCombo.SelectedIndex switch
            {
                1 => LightingModes.Simulated,
                2 => LightingModes.TcpText,
                3 => LightingModes.SerialText,
                _ => LightingModes.None,
            },
            TcpHost = LightingTcpHostText.Text.Trim(),
            TcpPort = int.TryParse(LightingTcpPortText.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tcpPort) ? tcpPort : 0,
            SerialPortName = LightingSerialPortText.Text.Trim(),
            BaudRate = int.TryParse(LightingBaudRateText.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var baudRate) ? baudRate : 0,
            TopProgram = LightingTopProgramText.Text.Trim(),
            SideProgram = LightingSideProgramText.Text.Trim(),
            BottomProgram = LightingBottomProgramText.Text.Trim(),
            CommandTemplate = LightingCommandTemplateText.Text,
            ResponseTimeoutMs = int.TryParse(LightingTimeoutMsText.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var timeout) ? timeout : 0,
        };

    private void SaveMesIntegrationSettings(MesIntegrationSettings? preparedSettings = null)
    {
        var settings = preparedSettings ?? BuildMesIntegrationSettingsFromUi();
        var validation = MesIntegrationSettingsService.Validate(settings);
        if (validation.Count > 0)
        {
            MesDiagnosticsText.Text = string.Join(" ", validation);
            WorkflowState.Instance.AddEvent("MES_CONFIG_WARNING", MesDiagnosticsText.Text);
            return;
        }

        MesIntegrationSettingsService.Save(settings);

        WorkflowState.Instance.AddEvent(
            "MES_CONFIG",
            $"MES/ERP settings updated: {MesIntegrationSettingsService.RedactedSummary(settings)}");
    }

    private void RefreshMesIntegrationUi()
        => RefreshMesIntegrationUi(MesIntegrationSettingsService.Load());

    private void RefreshMesIntegrationUi(MesIntegrationSettings settings)
    {
        MesModeCombo.SelectedIndex = settings.Mode switch
        {
            MesIntegrationMode.MockRest => 1,
            MesIntegrationMode.Rest => 2,
            _ => 0,
        };
        MesEndpointText.Text = settings.MockEndpointUrl;
        MesRestBaseUrlText.Text = settings.BaseUrl;
        MesResultPathText.Text = settings.UploadResultPath;
        MesImagePathText.Text = settings.UploadImagePath;
        MesAuthModeCombo.SelectedIndex = settings.AuthMode switch
        {
            MesRestAuthMode.ApiKey => 1,
            MesRestAuthMode.Bearer => 2,
            MesRestAuthMode.Basic => 3,
            _ => 0,
        };
        MesApiKeyHeaderText.Text = settings.ApiKeyHeaderName;
        MesApiKeyBox.Password = settings.ApiKey;
        MesBearerTokenBox.Password = settings.BearerToken;
        MesUsernameText.Text = settings.Username;
        MesPasswordBox.Password = settings.Password;
        MesTimeoutText.Text = settings.UploadTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        MesMaxRetryText.Text = settings.MaxRetryCount.ToString(CultureInfo.InvariantCulture);
        MesRetryBackoffText.Text = settings.RetryBackoffMs.ToString(CultureInfo.InvariantCulture);
        MesAutoUploadCheck.IsChecked = settings.AutoUploadEnabled;

        MesIntegrationSettingsService.ApplyIntegrationBoundary();
        var status = IntegrationBoundaryRegistry.MesClient.Status;
        MesMockStatusText.Text = settings.Mode switch
        {
            MesIntegrationMode.MockRest => string.IsNullOrWhiteSpace(settings.MockEndpointUrl)
                ? "Mock MES: Local JSON"
                : "Mock MES: REST Configured",
            MesIntegrationMode.Rest => status == IntegrationConnectionStatus.Ready ? "MES REST: Ready" : "MES REST: Config Error",
            _ => "MES: Not Connected",
        };
        MesMockStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(status switch
        {
            IntegrationConnectionStatus.Simulated => "#E1A334",
            IntegrationConnectionStatus.Ready => "#50F56E",
            IntegrationConnectionStatus.Error => "#F27777",
            _ => "#F27777",
        }));
        MesDiagnosticsText.Text = MesIntegrationSettingsService.RedactedSummary(settings);
    }

    private void SaveCentralSyncSettings(CentralSyncSettings? preparedSettings = null)
    {
        var settings = preparedSettings ?? BuildCentralSyncSettingsFromUi();
        var validation = CentralSyncSettingsService.Validate(settings);
        if (validation.Count > 0)
        {
            CentralSyncStatusText.Text = string.Join(" ", validation);
            WorkflowState.Instance.AddEvent("CENTRAL_SYNC_CONFIG_WARNING", CentralSyncStatusText.Text);
            return;
        }

        CentralSyncSettingsService.Save(settings);
        WorkflowState.Instance.AddEvent(
            "CENTRAL_SYNC_CONFIG",
            $"Central sync settings updated: {CentralSyncSettingsService.RedactedSummary(settings)}");
    }

    private void RefreshCentralSyncUi()
        => RefreshCentralSyncUi(CentralSyncSettingsService.Load());

    private void RefreshCentralSyncUi(CentralSyncSettings settings)
    {
        CentralSyncModeCombo.SelectedIndex = settings.Mode switch
        {
            CentralSyncMode.FileDrop => 1,
            CentralSyncMode.RestApi => 2,
            CentralSyncMode.ProductionDatabaseBoundary or CentralSyncMode.PostgreSqlBoundary => 3,
            _ => 0,
        };
        CentralSyncEndpointText.Text = settings.EndpointOrFolder;
        CentralSyncStationIdText.Text = settings.StationId;
        CentralSyncIntervalText.Text = settings.SyncIntervalSeconds.ToString(CultureInfo.InvariantCulture);
        CentralSyncMaxRetryText.Text = settings.MaxRetryCount.ToString(CultureInfo.InvariantCulture);
        CentralSyncSecretBox.Password = settings.SharedSecret;
        CentralSyncIncludeImagesCheck.IsChecked = settings.IncludeImages;
        CentralSyncRedactOperatorCheck.IsChecked = settings.RedactOperatorId;
        CentralSyncRedactImagePathsCheck.IsChecked = settings.RedactImagePaths;
        CentralSyncRedactEndpointCheck.IsChecked = settings.RedactEndpointInExports;
        CentralSyncStatusText.Text = CentralSyncSettingsService.RedactedSummary(settings);
    }

    private async void OnTestMesRestClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Testing MES integration"))
            return;

        var settings = BuildMesIntegrationSettingsFromUi();
        var validation = MesIntegrationSettingsService.Validate(settings);
        if (validation.Count > 0)
        {
            MesDiagnosticsText.Text = string.Join(" ", validation);
            MessageBox.Show(MesDiagnosticsText.Text, "MES Integration Test", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var payload = new TraceabilityPayload
        {
            IntegrationMode = TraceabilityUploadService.ToDisplay(settings.Mode),
            LotId = "TEST-LOT",
            BoardModel = WorkflowState.Instance.BoardProgram,
            SerialNumber = "TEST-BOARD",
            StationId = WorkflowState.Instance.StationId,
            OperatorId = WorkflowState.Instance.OperatorWithRole,
            Result = "REVIEW",
            TimestampUtc = DateTime.UtcNow,
            DefectSummary = "Settings test payload; no production result.",
            SourceNotice = "Settings test payload generated by AOI Monitor. No production inspection result.",
        };

        var button = sender as Button;
        await ErrorBoundaryService.SafeAsyncCommand(
            "Test MES integration",
            "Settings",
            async token =>
            {
                MesIntegrationSettingsService.Save(settings);
                var outcome = await TraceabilityUploadService.UploadAsync(payload, token);
                MesDiagnosticsText.Text = $"{outcome.Result.Message} Payload={outcome.PayloadPath}";
                WorkflowState.Instance.AddEvent("MES_TEST", MesDiagnosticsText.Text);
                MessageBox.Show(
                    MesDiagnosticsText.Text,
                    "MES Integration Test",
                    MessageBoxButton.OK,
                    outcome.Result.Accepted ? MessageBoxImage.Information : MessageBoxImage.Warning);
            },
            running =>
            {
                if (button is not null)
                    button.IsEnabled = !running;
            },
            message => MesDiagnosticsText.Text = message);
    }

    private MesIntegrationSettings BuildMesIntegrationSettingsFromUi()
        => new()
        {
            Mode = MesModeCombo.SelectedIndex switch
            {
                1 => MesIntegrationMode.MockRest,
                2 => MesIntegrationMode.Rest,
                _ => MesIntegrationMode.NotConnected,
            },
            MockEndpointUrl = MesEndpointText.Text.Trim(),
            AutoUploadEnabled = MesAutoUploadCheck.IsChecked == true,
            BaseUrl = MesRestBaseUrlText.Text.Trim(),
            UploadResultPath = MesResultPathText.Text.Trim(),
            UploadImagePath = MesImagePathText.Text.Trim(),
            AuthMode = MesAuthModeCombo.SelectedIndex switch
            {
                1 => MesRestAuthMode.ApiKey,
                2 => MesRestAuthMode.Bearer,
                3 => MesRestAuthMode.Basic,
                _ => MesRestAuthMode.None,
            },
            ApiKeyHeaderName = MesApiKeyHeaderText.Text.Trim(),
            ApiKey = MesApiKeyBox.Password,
            BearerToken = MesBearerTokenBox.Password,
            Username = MesUsernameText.Text.Trim(),
            Password = MesPasswordBox.Password,
            UploadTimeoutSeconds = int.TryParse(
                MesTimeoutText.Text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var timeout)
                ? Math.Clamp(timeout, 1, 300)
                : 10,
            TimeoutSeconds = int.TryParse(
                MesTimeoutText.Text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var restTimeout)
                ? restTimeout
                : 10,
            MaxRetryCount = int.TryParse(
                MesMaxRetryText.Text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var retries)
                ? retries
                : 2,
            RetryBackoffMs = int.TryParse(
                MesRetryBackoffText.Text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var backoff)
                ? backoff
                : 500,
        };

    private CentralSyncSettings BuildCentralSyncSettingsFromUi()
        => new()
        {
            Mode = CentralSyncModeCombo.SelectedIndex switch
            {
                1 => CentralSyncMode.FileDrop,
                2 => CentralSyncMode.RestApi,
                3 => CentralSyncMode.ProductionDatabaseBoundary,
                _ => CentralSyncMode.Disabled,
            },
            EndpointOrFolder = CentralSyncEndpointText.Text.Trim(),
            EndpointUrl = CentralSyncModeCombo.SelectedIndex == 1 ? string.Empty : CentralSyncEndpointText.Text.Trim(),
            FileDropFolder = CentralSyncModeCombo.SelectedIndex == 1 ? CentralSyncEndpointText.Text.Trim() : string.Empty,
            StationId = CentralSyncStationIdText.Text.Trim(),
            SyncIntervalSeconds = int.TryParse(
                CentralSyncIntervalText.Text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var interval)
                ? interval
                : 300,
            MaxRetryCount = int.TryParse(
                CentralSyncMaxRetryText.Text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var maxRetry)
                ? maxRetry
                : 5,
            IncludeImages = CentralSyncIncludeImagesCheck.IsChecked == true,
            RedactOperatorId = CentralSyncRedactOperatorCheck.IsChecked == true,
            RedactImagePaths = CentralSyncRedactImagePathsCheck.IsChecked == true,
            RedactEndpointInExports = CentralSyncRedactEndpointCheck.IsChecked == true,
            SharedSecret = CentralSyncSecretBox.Password,
        };

}
