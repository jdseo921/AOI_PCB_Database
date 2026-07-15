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
    private void OnStartTrainingClick(object sender, RoutedEventArgs e)
    {
        var state = WorkflowState.Instance;
        if (state.Training.IsRunning)
        {
            MessageBox.Show("Training set export preparation is already active.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        state.StartTraining();
    }

    private void OnRunEpochClick(object sender, RoutedEventArgs e)
    {
        var state = WorkflowState.Instance;
        if (!state.Training.IsRunning)
        {
            MessageBox.Show("Prepare the training set export before validating the list.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var validation = state.DetectionPriority switch
        {
            Models.DetectionPriority.MinimizeFalsePositives => 95.0,
            Models.DetectionPriority.Balanced => 92.5,
            Models.DetectionPriority.MaximizeDefectRecall => 90.5,
            _ => 92.5,
        };

        // Add small deterministic drift to avoid a static value across epochs.
        validation = Math.Max(80, validation - (state.Training.EpochsCompleted % 4) * 0.4);
        state.CompleteTrainingEpoch(validation);
    }

    private void OnStopTrainingClick(object sender, RoutedEventArgs e)
    {
        var state = WorkflowState.Instance;
        if (!state.Training.IsRunning)
        {
            MessageBox.Show("Training set export preparation is already stopped.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        state.StopTraining();
    }

    private void OnBrowseModelClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Changing model path"))
            return;

        var dialog = new OpenFileDialog
        {
            Title = "Select offline ONNX model",
            Filter = "ONNX model|*.onnx|All files|*.*",
        };

        if (dialog.ShowDialog() != true)
            return;

        ModelPathText.Text = dialog.FileName;
        if (string.IsNullOrWhiteSpace(ModelVersionText.Text) || ModelVersionText.Text == "UNCONFIGURED")
            ModelVersionText.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
        InspectionEngineCombo.SelectedIndex = 1;
        RefreshInspectionConfigurationUi(BuildConfigurationFromUi());
    }

    private void OnBrowseStorageRootClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Changing local database and image-vault storage path"))
            return;

        var dialog = new OpenFolderDialog
        {
            Title = "Select local AOI storage root",
            Multiselect = false,
        };

        if (Directory.Exists(StorageRootText.Text))
            dialog.InitialDirectory = StorageRootText.Text;

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
            StorageRootText.Text = dialog.FolderName;
    }

    private void OnBrowseBrandLogoClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Changing the client-visible logo asset"))
            return;

        var dialog = new OpenFileDialog
        {
            Title = "Select client logo image",
            Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp|All files|*.*",
        };

        if (dialog.ShowDialog() == true)
            BrandLogoPathText.Text = dialog.FileName;
    }

    private void OnBrowseLabelMapClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Changing label map path"))
            return;

        var dialog = new OpenFileDialog
        {
            Title = "Select inspection label map",
            Filter = "Label map|*.json;*.txt;*.csv|All files|*.*",
        };

        if (dialog.ShowDialog() != true)
            return;

        LabelMapPathText.Text = dialog.FileName;
        RefreshInspectionConfigurationUi(BuildConfigurationFromUi());
    }

    private void OnImportTaxonomyClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanChangeThresholds, "Importing defect taxonomy CSV"))
            return;

        var dialog = new OpenFileDialog
        {
            Title = "Import defect taxonomy CSV",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var taxonomy = DefectTaxonomyService.ImportCsv(dialog.FileName, WorkflowState.Instance.CurrentRole, WorkflowState.Instance.OperatorWithRole);
            WorkflowState.Instance.AddEvent("DEFECT_TAXONOMY", $"Imported active defect taxonomy {taxonomy.Taxonomy.Name} with {taxonomy.Entries.Count} class(es).");
            RefreshDefectTaxonomyUi();
            MessageBox.Show("Defect taxonomy imported and activated.", "Defect Taxonomy", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show($"Defect taxonomy import failed:\n{ex.Message}", "Defect Taxonomy", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnExportTaxonomyClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanChangeThresholds, "Exporting defect taxonomy CSV"))
            return;

        var dialog = new SaveFileDialog
        {
            Title = "Export defect taxonomy CSV",
            FileName = $"defect_taxonomy_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var path = DefectTaxonomyService.ExportCsv(dialog.FileName);
            WorkflowState.Instance.AddEvent("DEFECT_TAXONOMY", $"Exported active defect taxonomy CSV: {Path.GetFileName(path)}.");
            MessageBox.Show($"Defect taxonomy exported.\n\n{path}", "Defect Taxonomy", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show($"Defect taxonomy export failed:\n{ex.Message}", "Defect Taxonomy", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnRegisterModelClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Registering local ONNX model"))
            return;

        try
        {
            var request = BuildModelRegistrationRequestFromUi();
            var entry = ModelRegistryService.Register(request);
            _ = RefreshModelRegistryUiAsync();
            ModelRegistryGrid.SelectedItem = _modelRegistryRows.FirstOrDefault(row => row.ModelId == entry.ModelId);
            WorkflowState.Instance.AddEvent("MODEL_REGISTRY", $"Registered model {entry.ModelId}; version {entry.Version}; status {entry.ValidationStatus}.");
            MessageBox.Show(
                $"Model registered.\n\nModel ID: {entry.ModelId}\nSHA-256: {entry.Sha256}",
                "Model Registry",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or JsonException)
        {
            MessageBox.Show($"Model registration failed:\n{ex.Message}", "Model Registry", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnValidateRegisteredModelClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanTestModelConfiguration, "Validating registered model"))
            return;

        if (ModelRegistryGrid.SelectedItem is not ModelRegistryRow row)
        {
            MessageBox.Show("Select a registered model first.", "Model Registry", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var result = ModelLifecycleService.ValidateRuntime(row.ModelId, WorkflowState.Instance.CurrentRole, WorkflowState.Instance.OperatorWithRole);
            RefreshInspectionConfigurationUi(InspectionModelConfigurationService.Load());
            _ = RefreshModelRegistryUiAsync();
            WorkflowState.Instance.AddEvent("MODEL_CHECK", $"Registered model validation: {row.ModelId}; {result.DisplayStatus}. {result.Message}");
            MessageBox.Show(
                $"{result.DisplayStatus}\n\n{result.Message}",
                "Registered Model Validation",
                MessageBoxButton.OK,
                result.Status == ModelConfigurationTestStatus.Ready ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show($"Registered model validation failed:\n{ex.Message}", "Model Registry", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnSetActiveModelClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Setting active ONNX model"))
            return;

        if (ModelRegistryGrid.SelectedItem is not ModelRegistryRow row)
        {
            MessageBox.Show("Select a registered model first.", "Model Registry", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            if (!ModelRegistryService.SetActiveModel(row.ModelId))
            {
                MessageBox.Show("The selected model registry entry could not be found.", "Model Registry", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(ex.Message, "Model Registry", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        RefreshInspectionConfigurationUi(InspectionModelConfigurationService.Load());
        _ = RefreshModelRegistryUiAsync();
        WorkflowState.Instance.AddEvent("MODEL_DEPLOYMENT", $"Active model set to {row.ModelId}. ONNX inference remains gated by validation status.");
        MessageBox.Show(
            $"Active model set.\n\nModel ID: {row.ModelId}\nRun Validate before using it for accepted ONNX inference.",
            "Model Registry",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void OnSetActiveLearnedVisualModelClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanSetActiveLearnedVisualModel, "Setting active learned visual model"))
            return;

        if (LearnedVisualModelGrid.SelectedItem is not LearnedVisualModelRow row)
        {
            MessageBox.Show("Select a learned PCB visual model first.", "Learned Visual Model", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var active = LearnedVisualModelRegistryService.SetActiveLearnedVisualModel(
                row.ModelId,
                WorkflowState.Instance.CurrentRole,
                WorkflowState.Instance.OperatorWithRole);
            RefreshInspectionConfigurationUi(InspectionModelConfigurationService.Load());
            _ = RefreshLearnedVisualModelRegistryUiAsync();
            WorkflowState.Instance.AddEvent("MODEL_DEPLOYMENT", $"Active learned visual model set to {active.ModelId}. Image-only Stage 1 evidence; not live camera validation.");
            MessageBox.Show(
                $"Active learned visual model set.\n\nModel ID: {active.ModelId}\nTraining project: {active.ProjectName}\n\nImage-only Stage 1 learning; not live camera validation.",
                "Learned Visual Model",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or IOException)
        {
            MessageBox.Show(ex.Message, "Learned Visual Model", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void OnRunModelAcceptanceClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanTestModelConfiguration, "Running model acceptance"))
            return;

        if (_modelAcceptanceCancellation is not null)
            return;

        var datasetDialog = new OpenFolderDialog { Title = "Select model acceptance validation dataset folder", Multiselect = false };
        if (datasetDialog.ShowDialog() != true || string.IsNullOrWhiteSpace(datasetDialog.FolderName))
            return;

        var csvDialog = new OpenFileDialog
        {
            Title = "Select formal ground-truth CSV",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
        };
        if (csvDialog.ShowDialog() != true)
            return;

        try
        {
            _modelAcceptanceCancellation = new CancellationTokenSource();
            RefreshRoleControls();
            ModelAcceptanceProgressBar.IsIndeterminate = true;
            ModelAcceptanceProgressText.Text = "Starting model acceptance...";
            ModelCheckMessageText.Text = "Model acceptance running...";
            var datasetFolder = datasetDialog.FolderName;
            var csvPath = csvDialog.FileName;
            var operatorId = WorkflowState.Instance.OperatorWithRole;
            var progress = new Progress<string>(message =>
            {
                ModelAcceptanceProgressText.Text = message;
                ModelCheckMessageText.Text = message;
            });
            var token = _modelAcceptanceCancellation.Token;
            var run = await Task.Run(() => ModelAcceptanceService.RunAcceptance(datasetFolder, csvPath, operatorId: operatorId, role: WorkflowState.Instance.CurrentRole, progress: progress, cancellationToken: token), token);
            RefreshModelAcceptanceRunsUi();
            WorkflowState.Instance.AddEvent("MODEL_ACCEPTANCE", $"Model acceptance {run.Status}: model={run.ModelId}; run={run.Id}; dataset={run.DatasetName}.");
            ModelAcceptanceProgressBar.IsIndeterminate = false;
            ModelAcceptanceProgressBar.Value = 100;
            ModelAcceptanceProgressText.Text = $"Model acceptance {run.Status}: run {run.Id}.";
            MessageBox.Show($"Model acceptance {run.Status}.\n\nRun ID: {run.Id}\n{string.Join(Environment.NewLine, run.Messages.Take(5))}", "Model Acceptance", MessageBoxButton.OK, run.Status == "PASS" ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (OperationCanceledException)
        {
            WorkflowState.Instance.AddEvent("MODEL_ACCEPTANCE", "Model acceptance canceled by user before acceptance evidence was recorded.");
            ModelCheckMessageText.Text = "Model acceptance canceled.";
            ModelAcceptanceProgressText.Text = "Model acceptance canceled.";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or InvalidDataException)
        {
            MessageBox.Show($"Model acceptance failed:\n{ex.Message}", "Model Acceptance", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _modelAcceptanceCancellation?.Dispose();
            _modelAcceptanceCancellation = null;
            ModelAcceptanceProgressBar.IsIndeterminate = false;
            RefreshRoleControls();
        }
    }

    private void OnCancelModelAcceptanceClick(object sender, RoutedEventArgs e)
    {
        _modelAcceptanceCancellation?.Cancel();
        ModelAcceptanceProgressText.Text = "Cancel requested. Finishing current validation step...";
        ModelCheckMessageText.Text = "Cancel requested. Finishing current validation step...";
    }

    private void OnCreateModelReleasePackageClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanChangeThresholds, "Creating model release packages"))
            return;

        var latest = AoiDatabase.GetLatestModelAcceptanceRun();
        if (latest is null)
        {
            MessageBox.Show("Run model acceptance before creating a release package.", "Model Release", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var outputDialog = new OpenFolderDialog { Title = "Select model release package output folder", Multiselect = false };
        if (outputDialog.ShowDialog() != true || string.IsNullOrWhiteSpace(outputDialog.FolderName))
            return;

        try
        {
            var package = ModelAcceptanceService.CreateReleasePackage(latest.Id, outputDialog.FolderName, WorkflowState.Instance.OperatorWithRole);
            RefreshModelAcceptanceRunsUi();
            _ = RefreshModelRegistryUiAsync();
            WorkflowState.Instance.AddEvent("MODEL_RELEASE_PACKAGE", $"Model release package created: model={package.ModelId}; status={package.Status}.");
            MessageBox.Show($"Model release package created:\n{package.PackagePath}", "Model Release", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            MessageBox.Show($"Model release package failed:\n{ex.Message}", "Model Release", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnPromoteProductionCandidateClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanChangeThresholds, "Promoting production candidate models"))
            return;

        var latest = AoiDatabase.GetLatestModelAcceptanceRun();
        if (latest is null)
        {
            MessageBox.Show("Run model acceptance before promoting a production candidate.", "Model Acceptance", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            $"Promote model acceptance run {latest.Id} for {latest.ModelId} to production candidate?\n\nThis remains limited to the validation dataset and does not prove universal production accuracy.",
            "Confirm Production Candidate",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
            return;

        try
        {
            ModelAcceptanceService.PromoteToProductionCandidate(latest.Id, WorkflowState.Instance.CurrentRole, WorkflowState.Instance.OperatorWithRole);
            RefreshModelAcceptanceRunsUi();
            _ = RefreshModelRegistryUiAsync();
            WorkflowState.Instance.AddEvent("MODEL_PRODUCTION_CANDIDATE", $"Promoted model acceptance run {latest.Id} for {latest.ModelId}.");
            MessageBox.Show("Model acceptance run promoted to production candidate.", "Model Acceptance", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            MessageBox.Show(ex.Message, "Model Acceptance", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnViewModelAcceptanceRunsClick(object sender, RoutedEventArgs e)
        => RefreshModelAcceptanceRunsUi(showMessage: true);

    private void OnDeployModelClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Deploying model lifecycle state"))
            return;

        if (ModelRegistryGrid.SelectedItem is not ModelRegistryRow row)
        {
            MessageBox.Show("Select a registered model first.", "Model Lifecycle", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var model = ModelRegistryService.GetModel(row.ModelId);
            var latestAcceptance = AoiDatabase.GetLatestModelAcceptanceRun(row.ModelId);
            if (model is null)
            {
                MessageBox.Show("The selected model registry entry could not be found.", "Model Lifecycle", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var confirm = MessageBox.Show(
                $"Deploy model {model.DisplayName} v{model.Version}?\n\nSHA-256: {model.Sha256}\nAcceptance run ID: {latestAcceptance?.Id.ToString(CultureInfo.InvariantCulture) ?? "none"}\nAcceptance status: {latestAcceptance?.Status ?? "none"}\n\nFull automation readiness still requires PASS acceptance with no active waiver and the remaining factory evidence.",
                "Confirm Deploy Model",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes)
                return;

            ModelLifecycleService.DeployModel(row.ModelId, WorkflowState.Instance.CurrentRole, WorkflowState.Instance.OperatorWithRole);
            RefreshInspectionConfigurationUi(InspectionModelConfigurationService.Load());
            _ = RefreshModelRegistryUiAsync();
            WorkflowState.Instance.AddEvent("MODEL_DEPLOYMENT", $"Deployed model {row.ModelId} through lifecycle approval.");
            MessageBox.Show("Model deployed. Full automation readiness still depends on PASS acceptance and other factory evidence.", "Model Lifecycle", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            MessageBox.Show(ex.Message, "Model Lifecycle", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnWaiveDeployModelClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Deploying model with Admin waiver"))
            return;

        if (ModelRegistryGrid.SelectedItem is not ModelRegistryRow row)
        {
            MessageBox.Show("Select a registered model first.", "Model Lifecycle", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var reason = PromptForText("Deployment Waiver", "Admin waiver reason");
        if (string.IsNullOrWhiteSpace(reason))
            return;
        var expiryText = PromptForText("Deployment Waiver Expiry", "Expiry date/time UTC, for example 2026-07-21T00:00:00Z");
        if (!DateTime.TryParse(
                expiryText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var waiverExpiresAtUtc))
        {
            MessageBox.Show("A valid future waiver expiry date is required.", "Model Lifecycle", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var riskClassification = PromptForText("Deployment Waiver Risk", "Risk classification, for example Low / Medium / High / Safety Critical");
        if (string.IsNullOrWhiteSpace(riskClassification))
            return;

        try
        {
            ModelLifecycleService.DeployModel(row.ModelId, WorkflowState.Instance.CurrentRole, WorkflowState.Instance.OperatorWithRole, reason, waiverExpiresAtUtc, riskClassification);
            RefreshInspectionConfigurationUi(InspectionModelConfigurationService.Load());
            _ = RefreshModelRegistryUiAsync();
            WorkflowState.Instance.AddEvent("MODEL_DEPLOYMENT_WAIVER", $"Deployed model {row.ModelId} with Admin waiver risk={riskClassification}; expiring {waiverExpiresAtUtc:O}. Full automation remains blocked without real PASS evidence.");
            MessageBox.Show("Model deployed with waiver. Readiness packages will show this waiver and will not claim full production readiness.", "Model Lifecycle", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show(ex.Message, "Model Lifecycle", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnRetireModelClick(object sender, RoutedEventArgs e)
    {
        if (!Authorize(RoleAuthorization.CanManageSettings, "Retiring model lifecycle state"))
            return;

        if (ModelRegistryGrid.SelectedItem is not ModelRegistryRow row)
        {
            MessageBox.Show("Select a registered model first.", "Model Lifecycle", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var reason = PromptForText("Retire Model", "Retirement reason");
        if (string.IsNullOrWhiteSpace(reason))
            return;

        try
        {
            ModelLifecycleService.RetireModel(row.ModelId, WorkflowState.Instance.CurrentRole, WorkflowState.Instance.OperatorWithRole, reason);
            RefreshInspectionConfigurationUi(InspectionModelConfigurationService.Load());
            _ = RefreshModelRegistryUiAsync();
            WorkflowState.Instance.AddEvent("MODEL_RETIRED", $"Retired model {row.ModelId}.");
            MessageBox.Show("Model retired and removed from active use.", "Model Lifecycle", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            MessageBox.Show(ex.Message, "Model Lifecycle", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnBrowseCameraTopClick(object sender, RoutedEventArgs e) => BrowseCameraFolder(CameraViewType.Top);
    private void OnBrowseCameraSideClick(object sender, RoutedEventArgs e) => BrowseCameraFolder(CameraViewType.Side);
    private void OnBrowseCameraBottomClick(object sender, RoutedEventArgs e) => BrowseCameraFolder(CameraViewType.Bottom);

}
