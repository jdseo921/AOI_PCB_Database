using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using AOI_Monitor.ViewModels;
using Microsoft.Win32;

namespace AOI_Monitor.Views;

public partial class AiTrainingSetupView : UserControl
{
    private readonly AiTrainingSetupViewModel _viewModel = new();

    public AiTrainingSetupView()
    {
        InitializeComponent();
        DataContext = _viewModel;
        Loaded += (_, _) => RefreshFromState();
    }

    public void RefreshFromState()
        => _viewModel.Refresh();

    private void OnCreateProjectClick(object sender, RoutedEventArgs e)
        => RunUiAction(() => _viewModel.CreateProject(), "Create Training Project");

    private async void OnImportProjectFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Select image learning project folder",
            };

            if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FolderName))
                return;

            ImageLearningFolderImportResult? result = null;
            var currentRole = WorkflowState.Instance.CurrentRole;
            var operatorId = WorkflowState.Instance.OperatorWithRole;
            await RunLongActionAsync(
                () => result = AiTrainingSetupService.ImportProjectFolder(
                    dialog.FolderName,
                    currentRole,
                    operatorId,
                    ImageLearningEvidenceMode.CustomerData),
                "Import Convention Folder");
            ShowFolderImportWarnings(result);
        }
        catch (Exception ex)
        {
            ShowOperationFailure("Import Convention Folder", ex);
        }
    }

    private async void OnAddImagesClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!TryGetRole(sender, out var role))
                return;

            var dialog = new OpenFileDialog
            {
                Title = $"Add {DisplayRole(role)} images",
                Filter = "PCB images|*.png;*.jpg;*.jpeg|All files|*.*",
                Multiselect = true,
            };

            if (dialog.ShowDialog() != true || dialog.FileNames.Length == 0)
                return;

            await ImportRolePathsAsync(role, dialog.FileNames, $"Import {DisplayRole(role)} Images");
        }
        catch (Exception ex)
        {
            ShowOperationFailure("Import Images", ex);
        }
    }

    private void OnRoleCardDragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryGetRole(sender, out _) && HasDroppedPaths(e)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnRoleCardDrop(object sender, DragEventArgs e)
    {
        try
        {
            if (!TryGetRole(sender, out var role) || !TryGetDroppedPaths(e, out var paths))
                return;

            await ImportRolePathsAsync(role, paths, $"Import Dropped {DisplayRole(role)} Images");
        }
        catch (Exception ex)
        {
            ShowOperationFailure("Import Dropped Images", ex);
        }
        finally
        {
            e.Handled = true;
        }
    }

    private async void OnImportRoleFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!TryGetRole(sender, out var role))
                return;

            var dialog = new OpenFolderDialog
            {
                Title = $"Import {DisplayRole(role)} image folder",
                Multiselect = false,
            };

            if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FolderName))
                return;

            await ImportRolePathsAsync(role, [dialog.FolderName], $"Import {DisplayRole(role)} Folder");
        }
        catch (Exception ex)
        {
            ShowOperationFailure("Import Folder", ex);
        }
    }

    private void OnPreviewRoleClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetRole(sender, out var role))
            return;

        var path = AiTrainingSetupViewModel.PreviewImagePath(role);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            MessageBox.Show("No preview image is available for this image group yet.", "AI Training Setup", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        OpenPath(path, "Preview Image");
    }

    private async void OnLearnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var currentRole = WorkflowState.Instance.CurrentRole;
            var operatorId = WorkflowState.Instance.OperatorWithRole;
            AiTrainingSetupLearningOutcome? outcome = null;
            await RunLongActionAsync(
                () => outcome = AiTrainingSetupService.RunLearning(currentRole, createdBy: operatorId),
                "Learn Normal PCB Appearance");
            ShowLearningWarnings(outcome);
        }
        catch (Exception ex)
        {
            ShowOperationFailure("Learn Normal PCB Appearance", ex);
        }
    }

    private async void OnCalibrateClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var currentRole = WorkflowState.Instance.CurrentRole;
            var operatorId = WorkflowState.Instance.OperatorWithRole;
            AiTrainingSetupLearningOutcome? outcome = null;
            await RunLongActionAsync(
                () => outcome = AiTrainingSetupService.RunLearning(currentRole, createdBy: operatorId),
                "Calibrate False Calls");
            ShowLearningWarnings(outcome);
        }
        catch (Exception ex)
        {
            ShowOperationFailure("Calibrate False Calls", ex);
        }
    }

    private async void OnInspectClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var currentRole = WorkflowState.Instance.CurrentRole;
            var operatorId = WorkflowState.Instance.OperatorWithRole;
            await RunLongActionAsync(
                () => AiTrainingSetupService.InspectSamples(currentRole, operatorId: operatorId),
                "Inspect Samples");
        }
        catch (Exception ex)
        {
            ShowOperationFailure("Inspect Samples", ex);
        }
    }

    private async void OnExportReportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            string? report = null;
            await RunLongActionAsync(
                () => report = _viewModel.ExportReport(),
                "Export Client Learning Report");
            if (!string.IsNullOrWhiteSpace(report))
                OpenPath(Path.GetDirectoryName(report) ?? report, "Open Report Folder");
        }
        catch (Exception ex)
        {
            ShowOperationFailure("Export Client Learning Report", ex);
        }
    }

    private async void OnExportVisualEvidenceClick(object sender, RoutedEventArgs e)
    {
        try
        {
            ImageLearningOverlayExportResult? result = null;
            await RunLongActionAsync(
                () => result = _viewModel.ExportVisualEvidence(),
                "Export Visual Evidence");
            if (result is not null)
                OpenPath(result.OutputFolder, "Open Visual Evidence Folder");
        }
        catch (Exception ex)
        {
            ShowOperationFailure("Export Visual Evidence", ex);
        }
    }

    private void OnSetActiveInspectionModelClick(object sender, RoutedEventArgs e)
        => RunUiAction(() =>
        {
            var active = _viewModel.SetCurrentModelAsActive();
            MessageBox.Show(
                $"Active inspection model set.\n\nModel ID: {active.ModelId}\nVersion: {active.ModelVersion}\n\nImage-only Stage 1 learning; not live camera validation.",
                "AI Training Setup",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }, "Set Active Inspection Model");

    private void OnManageOpenProjectClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetStringTag(sender, out var projectId))
            return;

        RunUiAction(() => _viewModel.OpenManagedProject(projectId), "Open Managed Training Project");
    }

    private void OnManageRenameProjectClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: TrainedDataProjectRow row })
            return;

        RunUiAction(() => _viewModel.RenameManagedProject(row.ProjectId, row.RenameText), "Rename Training Project");
    }

    private void OnManageArchiveProjectClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetStringTag(sender, out var projectId))
            return;

        var confirmed = MessageBox.Show(
            "Archive this image-only learning project metadata? Original customer image files are not deleted by this action.",
            "Manage Trained Data",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirmed != MessageBoxResult.Yes)
            return;

        RunUiAction(() => _viewModel.ArchiveManagedProject(projectId), "Archive Training Project");
    }

    private void OnManageDuplicateProjectClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetStringTag(sender, out var projectId))
            return;

        RunUiAction(() => _viewModel.DuplicateManagedProject(projectId), "Duplicate Training Project");
    }

    private async void OnManageRebuildModelClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!TryGetStringTag(sender, out var projectId))
                return;

            await RunLongActionAsync(
                () => _viewModel.RebuildManagedModel(projectId),
                "Rebuild Learned Model");
        }
        catch (Exception ex)
        {
            ShowOperationFailure("Rebuild Learned Model", ex);
        }
    }

    private void OnManageSetActiveModelClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetStringTag(sender, out var modelId))
            return;

        RunUiAction(() =>
        {
            var active = _viewModel.SetManagedModelAsActive(modelId);
            MessageBox.Show(
                $"Active learned model set.\n\nModel ID: {active.ModelId}\nVersion: {active.ModelVersion}\n\nImage-only Stage 1 learning; not live camera validation.",
                "Manage Trained Data",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }, "Set Active Learned Model");
    }

    private async void OnManageExportArtifactsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!TryGetStringTag(sender, out var modelId))
                return;

            TrainedDataManagementExportResult? result = null;
            await RunLongActionAsync(
                () => result = _viewModel.ExportManagedModelArtifacts(modelId),
                "Export Model Artifacts");
            if (result is not null)
                OpenPath(result.OutputFolder, "Open Model Artifact Export Folder");
        }
        catch (Exception ex)
        {
            ShowOperationFailure("Export Model Artifacts", ex);
        }
    }

    private async void OnManageExportLearningReportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not FrameworkElement { Tag: TrainedDataModelVersionRow row })
                return;

            ImageOnlyLearningReportResult? result = null;
            await RunLongActionAsync(
                () => result = _viewModel.ExportManagedLearningReport(row.ProjectId, row.ModelId),
                "Export Learning Report");
            if (result is not null)
                OpenPath(result.OutputFolder, "Open Learning Report Folder");
        }
        catch (Exception ex)
        {
            ShowOperationFailure("Export Learning Report", ex);
        }
    }

    private void OnManageOpenArtifactFolderClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetStringTag(sender, out var modelId))
            return;

        RunUiAction(() => OpenPath(_viewModel.ManagedArtifactFolder(modelId), "Open Artifact Folder"), "Open Artifact Folder");
    }

    private async void OnManageDeleteGeneratedArtifactsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!TryGetStringTag(sender, out var modelId))
                return;

            var confirmed = MessageBox.Show(
                "Delete generated learned-model artifacts for this version? Original customer image files and imported image records are not deleted.",
                "Manage Trained Data",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirmed != MessageBoxResult.Yes)
                return;

            ImageLearningArtifactDeletionResult? result = null;
            await RunLongActionAsync(
                () => result = _viewModel.DeleteManagedGeneratedArtifacts(modelId, explicitConfirmation: true),
                "Delete Generated Artifacts");
            if (result is not null)
            {
                MessageBox.Show(
                    $"Generated artifacts deleted.\n\nFiles deleted: {result.FilesDeleted}\nMissing files: {result.MissingFiles}\n\nOriginal customer images were not deleted.",
                    "Manage Trained Data",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            ShowOperationFailure("Delete Generated Artifacts", ex);
        }
    }

    private async Task RunLongActionAsync(Action action, string title)
    {
        if (_viewModel.IsBusy)
            return;

        _viewModel.IsBusy = true;
        _viewModel.StatusText = $"{title} running...";
        try
        {
            await Task.Run(action).ConfigureAwait(true);
            _viewModel.Refresh();
            _viewModel.StatusText = $"{title} complete.";
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or IOException or NotSupportedException)
        {
            _viewModel.StatusText = $"{title} failed: {ex.Message}";
            MessageBox.Show(_viewModel.StatusText, "AI Training Setup", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _viewModel.IsBusy = false;
        }
    }

    private async Task ImportRolePathsAsync(ImageLearningImageRole role, IReadOnlyList<string> paths, string title)
    {
        IReadOnlyList<ImageLearningImportResult>? results = null;
        var selectedPaths = paths.ToArray();
        var currentRole = WorkflowState.Instance.CurrentRole;
        var operatorId = WorkflowState.Instance.OperatorWithRole;
        await RunLongActionAsync(
            () => results = AiTrainingSetupService.ImportImages(role, selectedPaths, currentRole, operatorId),
            title);
        ShowImageImportWarnings(results);
    }

    private void RunUiAction(Action action, string title)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or IOException or NotSupportedException)
        {
            _viewModel.StatusText = $"{title} failed: {ex.Message}";
            MessageBox.Show(_viewModel.StatusText, "AI Training Setup", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ShowOperationFailure(string title, Exception ex)
    {
        _viewModel.StatusText = $"{title} failed: {ex.Message}";
        MessageBox.Show(_viewModel.StatusText, "AI Training Setup", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void ShowImageImportWarnings(IReadOnlyList<ImageLearningImportResult>? results)
    {
        if (results is null)
            return;

        var warnings = results
            .Where(result => !result.Imported)
            .Select(result => result.Message)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (warnings.Length == 0)
            return;

        _viewModel.StatusText = $"Import completed with {warnings.Length} warning(s). {string.Join(" ", warnings.Take(3))}";
        MessageBox.Show(string.Join(Environment.NewLine, warnings.Take(12)), "AI Training Setup Import Warnings", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void ShowFolderImportWarnings(ImageLearningFolderImportResult? result)
    {
        if (result is null || result.Warnings.Count == 0)
            return;

        _viewModel.StatusText = $"Folder import completed with {result.Warnings.Count} warning(s). {string.Join(" ", result.Warnings.Take(3))}";
        MessageBox.Show(string.Join(Environment.NewLine, result.Warnings.Take(12)), "AI Training Setup Import Warnings", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void ShowLearningWarnings(AiTrainingSetupLearningOutcome? outcome)
    {
        var warnings = outcome?.LearningResult.Warnings;
        if (warnings is null || warnings.Count == 0)
            return;

        // Learning warnings can hide real evidence gaps (e.g. every NG validation image was
        // skipped as unreadable, making the possible-escape rate trivially 0%). They must be
        // shown, not just written into learning_summary.json.
        _viewModel.StatusText = $"Learning completed with {warnings.Count} warning(s). {string.Join(" ", warnings.Take(3))}";
        MessageBox.Show(string.Join(Environment.NewLine, warnings.Take(12)), "AI Training Warnings", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static void OpenPath(string path, string title)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            MessageBox.Show($"Could not open path for {title}: {ex.Message}", "AI Training Setup", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static bool TryGetRole(object sender, out ImageLearningImageRole role)
    {
        role = ImageLearningImageRole.Inspection;
        if (sender is FrameworkElement { Tag: ImageLearningImageRole value })
        {
            role = value;
            return true;
        }

        if (sender is FrameworkElement { Tag: string text } &&
            Enum.TryParse(text, ignoreCase: true, out ImageLearningImageRole parsed))
        {
            role = parsed;
            return true;
        }

        return false;
    }

    private static bool TryGetStringTag(object sender, out string value)
    {
        value = string.Empty;
        if (sender is FrameworkElement { Tag: string text } && !string.IsNullOrWhiteSpace(text))
        {
            value = text;
            return true;
        }

        return false;
    }

    private static bool HasDroppedPaths(DragEventArgs e)
        => e.Data.GetDataPresent(DataFormats.FileDrop);

    private static bool TryGetDroppedPaths(DragEventArgs e, out string[] paths)
    {
        paths = [];
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return false;

        paths = e.Data.GetData(DataFormats.FileDrop) as string[] ?? [];
        return paths.Length > 0;
    }

    private static string DisplayRole(ImageLearningImageRole role)
        => role switch
        {
            ImageLearningImageRole.GoldenReference => "Golden / Reference",
            ImageLearningImageRole.OkLearning => "OK Learning",
            ImageLearningImageRole.OkValidation => "OK Validation",
            ImageLearningImageRole.Inspection => "Inspection",
            ImageLearningImageRole.NgValidation => "Optional NG Validation",
            _ => role.ToString(),
        };
}
