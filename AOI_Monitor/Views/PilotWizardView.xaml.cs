using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using Microsoft.Win32;

namespace AOI_Monitor.Views;

public partial class PilotWizardView : UserControl
{
    private readonly ObservableCollection<PilotStepRow> _steps = new();
    private CustomerPilotSnapshot? _snapshot;

    public PilotWizardView()
    {
        InitializeComponent();
        StepsGrid.ItemsSource = _steps;
        PopulateProfiles();
        ResumeLatest(showMessage: false);
    }

    public void RefreshFromState()
    {
        if (_snapshot is not null)
            LoadSnapshot(CustomerPilotWizardService.Load(_snapshot.Session.Id));
        else
            ResumeLatest(showMessage: false);
    }

    private void PopulateProfiles()
    {
        ProfileCombo.Items.Clear();
        foreach (var profile in new[] { DeploymentProfile.Stage1ImageValidation, DeploymentProfile.Stage2CameraPilot })
        {
            ProfileCombo.Items.Add(new ComboBoxItem
            {
                Content = FactoryReadinessService.DisplayName(profile),
                Tag = profile,
            });
        }

        ProfileCombo.SelectedIndex = 0;
    }

    private DeploymentProfile SelectedProfile()
        => (ProfileCombo.SelectedItem as ComboBoxItem)?.Tag is DeploymentProfile profile
            ? profile
            : DeploymentProfile.Stage1ImageValidation;

    private void OnStartNewClick(object sender, RoutedEventArgs e)
    {
        try
        {
            LoadSnapshot(CustomerPilotWizardService.StartNew(SelectedProfile(), WorkflowState.Instance.OperatorWithRole));
            StatusText.Text = "Started a new customer pilot session.";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowError("Could not start pilot", ex);
        }
    }

    private void OnResumeClick(object sender, RoutedEventArgs e) => ResumeLatest(showMessage: true);

    private void ResumeLatest(bool showMessage)
    {
        try
        {
            var latest = AoiDatabase.GetLatestIncompleteCustomerPilotSession();
            if (latest is null)
            {
                if (showMessage)
                    StatusText.Text = "No incomplete customer pilot session exists.";
                return;
            }

            LoadSnapshot(CustomerPilotWizardService.Load(latest.Id));
            StatusText.Text = $"Resumed pilot session {latest.SessionId}.";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowError("Could not resume pilot", ex);
        }
    }

    private void OnConfirmProfileClick(object sender, RoutedEventArgs e)
        => RunStep(() => CustomerPilotWizardService.ConfirmDeploymentProfile(RequireSession().Session.Id, SelectedProfile(), WorkflowState.Instance.OperatorWithRole), "Deployment profile confirmed.");

    private void OnDiagnosticsClick(object sender, RoutedEventArgs e)
        => RunStep(() => CustomerPilotWizardService.RunSystemDiagnostics(RequireSession().Session.Id), "Diagnostics completed.");

    private void OnSelectDatasetClick(object sender, RoutedEventArgs e)
    {
        var folderDialog = new OpenFolderDialog
        {
            Title = "Select customer dataset folder",
            Multiselect = false,
        };
        if (folderDialog.ShowDialog() != true || string.IsNullOrWhiteSpace(folderDialog.FolderName))
            return;

        var fileDialog = new OpenFileDialog
        {
            Title = "Select customer validation manifest CSV",
            Filter = "CSV manifest (*.csv)|*.csv|All files (*.*)|*.*",
            InitialDirectory = folderDialog.FolderName,
        };
        if (fileDialog.ShowDialog() != true || string.IsNullOrWhiteSpace(fileDialog.FileName))
            return;

        RunStep(() => CustomerPilotWizardService.RecordDatasetSelection(RequireSession().Session.Id, folderDialog.FolderName, fileDialog.FileName), "Dataset selection recorded.");
    }

    private void OnPreflightClick(object sender, RoutedEventArgs e)
        => RunStep(() => CustomerPilotWizardService.RunDatasetPreflight(RequireSession().Session.Id), "Dataset preflight completed.");

    private void OnBatchClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var latest = AoiDatabase.GetLatestBatchTestRun();
            var status = latest is null ? CustomerPilotStepStatus.Failed : CustomerPilotStepStatus.Passed;
            var evidence = latest is null ? string.Empty : $"SQLite:BatchTestRuns/{latest.Id}";
            var message = latest is null ? "No batch validation run has been recorded." : $"Latest batch validation run {latest.Id}; rows={latest.TotalImages}; falseCall={latest.FalseCallRate:P1}.";
            LoadSnapshot(CustomerPilotWizardService.RecordBatchValidationEvidence(RequireSession().Session.Id, evidence, status, message));
            StatusText.Text = "Batch validation evidence evaluated.";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowError("Batch validation blocked", ex);
        }
    }

    private void OnFalseCallClick(object sender, RoutedEventArgs e)
    {
        var latest = AoiDatabase.GetLatestFalseCallReductionRun();
        var status = latest?.Recommendation.Point is null ? CustomerPilotStepStatus.Failed : CustomerPilotStepStatus.Passed;
        var evidence = latest is null ? string.Empty : $"SQLite:FalseCallReductionRuns/{latest.Id}";
        var message = latest is null ? "No false-call reduction run has been recorded." : $"False-call reduction run {latest.Id}; recommendation={latest.Recommendation.Status}.";
        RunStep(() => CustomerPilotWizardService.RecordFalseCallReductionEvidence(RequireSession().Session.Id, evidence, status, message), "False-call evidence evaluated.");
    }

    private void OnModelClick(object sender, RoutedEventArgs e)
        => RunStep(() => CustomerPilotWizardService.EvaluateModelAcceptance(RequireSession().Session.Id), "Model acceptance evidence evaluated.");

    private void OnCustomerPackageClick(object sender, RoutedEventArgs e)
        => RunStep(() => CustomerPilotWizardService.EvaluateCustomerValidationPackage(RequireSession().Session.Id), "Customer package evidence evaluated.");

    private void OnStage2Click(object sender, RoutedEventArgs e)
        => RunStep(() => CustomerPilotWizardService.EvaluateStage2Acceptance(RequireSession().Session.Id), "Stage 2 acceptance evidence evaluated.");

    private void OnReadinessClick(object sender, RoutedEventArgs e)
        => RunStep(() => CustomerPilotWizardService.ExportReadinessAndChecklist(RequireSession().Session.Id), "Readiness package and checklist exported.");

    private void OnWaiveSelectedClick(object sender, RoutedEventArgs e)
    {
        if (StepsGrid.SelectedItem is not PilotStepRow row)
        {
            StatusText.Text = "Select a step to waive.";
            return;
        }

        if (WorkflowState.Instance.CurrentRole < UserRole.Engineer)
        {
            MessageBox.Show(RoleAuthorization.DeniedMessage(WorkflowState.Instance.CurrentRole, "Recording a customer pilot waiver"), "Permission Denied", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var reason = Microsoft.VisualBasic.Interaction.InputBox("Enter waiver reason. This will be audited.", "Customer Pilot Waiver", "Engineer-reviewed exception for customer pilot evidence.");
        if (string.IsNullOrWhiteSpace(reason))
            return;

        RunStep(
            () => CustomerPilotWizardService.RecordWaiver(RequireSession().Session.Id, row.StepKey, reason, WorkflowState.Instance.CurrentRole, WorkflowState.Instance.OperatorWithRole),
            "Waiver recorded.");
    }

    private void OnExportBundleClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select pilot evidence bundle output folder",
            Multiselect = false,
        };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FolderName))
            return;

        try
        {
            var result = CustomerPilotWizardService.ExportPilotEvidenceBundle(RequireSession().Session.Id, dialog.FolderName);
            RefreshFromState();
            StatusText.Text = $"Pilot evidence bundle exported. JSON: {result.JsonPath}; HTML: {result.HtmlPath}; PDF: {result.PdfPath}.";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowError("Pilot bundle export failed", ex);
        }
    }

    private void RunStep(Func<CustomerPilotSnapshot> action, string successMessage)
    {
        try
        {
            LoadSnapshot(action());
            StatusText.Text = successMessage;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            ShowError("Pilot step failed", ex);
        }
    }

    private CustomerPilotSnapshot RequireSession()
    {
        if (_snapshot is not null)
            return _snapshot;

        _snapshot = CustomerPilotWizardService.StartNew(SelectedProfile(), WorkflowState.Instance.OperatorWithRole);
        LoadSnapshot(_snapshot);
        return _snapshot;
    }

    private void LoadSnapshot(CustomerPilotSnapshot snapshot)
    {
        _snapshot = snapshot;
        var profileIndex = snapshot.Session.DeploymentProfile == DeploymentProfile.Stage2CameraPilot ? 1 : 0;
        if (ProfileCombo.SelectedIndex != profileIndex)
            ProfileCombo.SelectedIndex = profileIndex;
        SessionSummaryText.Text = $"Session {snapshot.Session.SessionId} / {snapshot.Session.DeploymentProfile} / {snapshot.Session.Status} / Dataset: {ShortPath(snapshot.Session.DatasetFolder)}";
        _steps.Clear();
        foreach (var step in snapshot.Steps.OrderBy(item => item.StepOrder))
            _steps.Add(PilotStepRow.FromRecord(step));
    }

    private static string ShortPath(string path)
        => string.IsNullOrWhiteSpace(path) ? "not selected" : Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    private static void ShowError(string title, Exception ex)
    {
        MessageBox.Show(ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    public sealed class PilotStepRow
    {
        public CustomerPilotStepKind StepKey { get; init; }
        public int StepOrder { get; init; }
        public string StepName { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string EvidencePath { get; init; } = string.Empty;
        public string MessagesDisplay { get; init; } = string.Empty;
        public bool Waived { get; init; }
        public Brush StatusBrush { get; init; } = Brushes.Transparent;

        public static PilotStepRow FromRecord(CustomerPilotStepRecord step)
            => new()
            {
                StepKey = step.StepKey,
                StepOrder = step.StepOrder,
                StepName = CustomerPilotWizardService.DisplayName(step.StepKey),
                Status = step.Status.ToString(),
                EvidencePath = step.EvidencePath,
                MessagesDisplay = string.Join(" | ", step.Messages),
                Waived = step.Waived,
                StatusBrush = step.Status switch
                {
                    CustomerPilotStepStatus.Passed => new SolidColorBrush(Color.FromRgb(20, 78, 45)),
                    CustomerPilotStepStatus.Conditional => new SolidColorBrush(Color.FromRgb(98, 72, 22)),
                    CustomerPilotStepStatus.Failed => new SolidColorBrush(Color.FromRgb(96, 36, 36)),
                    CustomerPilotStepStatus.Running => new SolidColorBrush(Color.FromRgb(36, 65, 96)),
                    CustomerPilotStepStatus.Skipped => new SolidColorBrush(Color.FromRgb(56, 58, 62)),
                    _ => new SolidColorBrush(Color.FromRgb(24, 29, 33)),
                },
            };
    }
}
