using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AOI_Monitor.Data;
using AOI_Monitor.Models;
using AOI_Monitor.Services;
using AOI_Monitor.ViewModels;
using Microsoft.Win32;

namespace AOI_Monitor.Views;

public partial class ReportsView : UserControl, IAsyncNavigationPage, IDisposable
{
    private static readonly Encoding CsvEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff",
    };

    private readonly ObservableCollection<InspectionLogRow> _inspectionRows = new();
    private readonly ObservableCollection<ReviewLogRow> _reviewRows = new();
    private readonly ObservableCollection<ExportHistoryRow> _exportRows = new();
    private readonly ObservableCollection<AuditLogRow> _auditRows = new();
    private readonly ObservableCollection<MesSpoolQueueRow> _mesSpoolRows = new();
    private readonly ObservableCollection<CentralSyncQueueRow> _centralSyncRows = new();
    private readonly ObservableCollection<PilotIssueRow> _pilotIssueRows = new();
    private readonly ObservableCollection<FactoryReadinessRow> _factoryReadinessRows = new();
    private readonly ObservableCollection<Stage1ReadinessRow> _stage1ReadinessRows = new();
    private readonly ObservableCollection<StandardsTraceabilityMatrix> _standardsTraceabilityRows = new();
    private readonly ObservableCollection<CompletionMatrixRow> _completionMatrixRows = new();
    private readonly ObservableCollection<FactoryAcceptanceChecklistItem> _factoryAcceptanceRows = new();
    private readonly ObservableCollection<UiNavigationSoakEvent> _uiStabilityEvents = new();
    private readonly ObservableCollection<ManagementDashboardContributor> _managementDefectRows = new();
    private readonly ObservableCollection<ManagementDashboardContributor> _managementRoiRows = new();
    private readonly ObservableCollection<ManagementDashboardTrendPoint> _managementTrendRows = new();
    private readonly ObservableCollection<ManagementDashboardBreakdown> _managementBreakdownRows = new();
    private ManagementDashboardReport? _managementDashboardReport;
    private Stage1ReadinessReport? _latestStage1ReadinessReport;
    private CancellationTokenSource? _workCts;
    private CancellationTokenSource? _refreshCts;

    public ReportsView()
    {
        InitializeComponent();
        InspectionGrid.ItemsSource = _inspectionRows;
        ReviewGrid.ItemsSource = _reviewRows;
        ExportGrid.ItemsSource = _exportRows;
        AuditGrid.ItemsSource = _auditRows;
        MesSpoolGrid.ItemsSource = _mesSpoolRows;
        CentralSyncGrid.ItemsSource = _centralSyncRows;
        PilotIssuesGrid.ItemsSource = _pilotIssueRows;
        FactoryReadinessGrid.ItemsSource = _factoryReadinessRows;
        Stage1ReadinessGrid.ItemsSource = _stage1ReadinessRows;
        StandardsTraceabilityGrid.ItemsSource = _standardsTraceabilityRows;
        CompletionMatrixGrid.ItemsSource = _completionMatrixRows;
        FactoryAcceptanceGrid.ItemsSource = _factoryAcceptanceRows;
        UiStabilityEventsGrid.ItemsSource = _uiStabilityEvents;
        ManagementDefectGrid.ItemsSource = _managementDefectRows;
        ManagementRoiGrid.ItemsSource = _managementRoiRows;
        ManagementModelTrendGrid.ItemsSource = _managementTrendRows;
        ManagementLotModelGrid.ItemsSource = _managementBreakdownRows;
        PopulateFactoryAcceptanceProfiles();
        PopulateManagementProfiles();
        PopulatePilotIssueFilters();
        FromDatePicker.SelectedDate = DateTime.Today.AddDays(-30);
        ToDatePicker.SelectedDate = DateTime.Today;
        StatusText.Text = "Ready. Select Refresh or open this page to load logs.";
    }

    public Task OnNavigatedToAsync(CancellationToken cancellationToken) => RefreshAsync(cancellationToken);

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await LoadLogsAsync(_refreshCts.Token);
        await RefreshRetentionWarningAsync(_refreshCts.Token);
    }

    public void RefreshFromState() => _ = RefreshAsync(CancellationToken.None);

    private static string DescribeRetentionPolicy()
    {
        var settings = LogRetentionSettingsService.LoadSettings();
        return settings.Enabled
            ? $"Archive-then-purge: logs older than {settings.RetentionDays} day(s) are copied to the recoverable LogArchive and removed from the live tables at startup."
            : "Automatic purge disabled: logs are retained indefinitely in the live tables.";
    }

    private async Task RefreshRetentionWarningAsync(CancellationToken cancellationToken)
    {
        LogRetentionSettings settings;
        try
        {
            settings = LogRetentionSettingsService.LoadSettings();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"Retention warning skipped (settings load): {ex.Message}");
            RetentionWarningText.Visibility = Visibility.Collapsed;
            return;
        }

        if (!settings.Enabled || !settings.WarningEnabled)
        {
            RetentionWarningText.Visibility = Visibility.Collapsed;
            return;
        }

        int nearing;
        try
        {
            nearing = await Task.Run(
                () => AoiDatabase.CountRowsNearingPurge(settings.RetentionDays, settings.WarningLeadDays),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"Retention warning skipped (count): {ex.Message}");
            RetentionWarningText.Visibility = Visibility.Collapsed;
            return;
        }

        if (nearing > 0)
        {
            RetentionWarningText.Text =
                $"Retention notice: {nearing} log row(s) will be archived and purged within {settings.WarningLeadDays} day(s) (retention {settings.RetentionDays}d). Configure in System Settings.";
            RetentionWarningText.Visibility = Visibility.Visible;
        }
        else
        {
            RetentionWarningText.Visibility = Visibility.Collapsed;
        }
    }

    public void CancelWork()
    {
        _refreshCts?.Cancel();
        _workCts?.Cancel();
        StatusText.Text = "Cancel requested. Finishing current operation...";
    }

    private async void OnApplyFiltersClick(object sender, RoutedEventArgs e)
    {
        var button = sender as Button;
        await ErrorBoundaryService.SafeAsyncCommand(
            "Apply report filters",
            "Log & Export",
            RefreshAsync,
            running =>
            {
                if (button is not null)
                    button.IsEnabled = !running;
            },
            message => StatusText.Text = message);
    }

    private static IEnumerable<MesSpoolQueueRow> ApplyMesQueueFilter(IEnumerable<MesSpoolQueueRow> rows, string selected)
    {
        return string.Equals(selected, "All", StringComparison.OrdinalIgnoreCase)
            ? rows
            : rows.Where(row => row.Status.Equals(selected, StringComparison.OrdinalIgnoreCase));
    }

    private void PopulateFactoryAcceptanceProfiles()
    {
        FactoryAcceptanceProfileCombo.Items.Clear();
        foreach (DeploymentProfile profile in Enum.GetValues<DeploymentProfile>())
        {
            FactoryAcceptanceProfileCombo.Items.Add(new ComboBoxItem
            {
                Content = FactoryReadinessService.DisplayName(profile),
                Tag = profile,
            });
        }

        FactoryAcceptanceProfileCombo.SelectedIndex = Math.Max(0, (int)DeploymentProfileSettingsService.Load());
    }

    private DeploymentProfile SelectedFactoryAcceptanceProfile()
        => (FactoryAcceptanceProfileCombo.SelectedItem as ComboBoxItem)?.Tag is DeploymentProfile profile
            ? profile
            : DeploymentProfile.Stage1ImageValidation;

    private void PopulateManagementProfiles()
    {
        ManagementDeploymentProfileCombo.Items.Clear();
        ManagementDeploymentProfileCombo.Items.Add(new ComboBoxItem { Content = "Active deployment profile", Tag = null });
        foreach (DeploymentProfile profile in Enum.GetValues<DeploymentProfile>())
        {
            ManagementDeploymentProfileCombo.Items.Add(new ComboBoxItem
            {
                Content = FactoryReadinessService.DisplayName(profile),
                Tag = profile,
            });
        }

        ManagementDeploymentProfileCombo.SelectedIndex = 0;
    }

    private DeploymentProfile? SelectedManagementProfile()
        => (ManagementDeploymentProfileCombo.SelectedItem as ComboBoxItem)?.Tag is DeploymentProfile profile
            ? profile
            : null;

    private void PopulatePilotIssueFilters()
    {
        PilotIssueCategoryFilterCombo.Items.Clear();
        PilotIssueCategoryFilterCombo.Items.Add(new ComboBoxItem { Content = "All", Tag = null });
        foreach (PilotIssueCategory category in Enum.GetValues<PilotIssueCategory>())
            PilotIssueCategoryFilterCombo.Items.Add(new ComboBoxItem { Content = category.ToString(), Tag = category });
        PilotIssueCategoryFilterCombo.SelectedIndex = 0;

        PilotIssueStatusFilterCombo.Items.Clear();
        PilotIssueStatusFilterCombo.Items.Add(new ComboBoxItem { Content = "All", Tag = null });
        foreach (PilotIssueStatus status in Enum.GetValues<PilotIssueStatus>())
            PilotIssueStatusFilterCombo.Items.Add(new ComboBoxItem { Content = status.ToString(), Tag = status });
        PilotIssueStatusFilterCombo.SelectedIndex = 0;
    }

    private PilotIssueFilter BuildPilotIssueFilter()
        => new()
        {
            Category = (PilotIssueCategoryFilterCombo?.SelectedItem as ComboBoxItem)?.Tag is PilotIssueCategory category ? category : null,
            Status = (PilotIssueStatusFilterCombo?.SelectedItem as ComboBoxItem)?.Tag is PilotIssueStatus status ? status : null,
            Severity = ComboBoxTokens.SelectedToken(PilotIssueSeverityFilterCombo, "All") == "All"
                ? string.Empty
                : ComboBoxTokens.SelectedToken(PilotIssueSeverityFilterCombo, string.Empty),
            BoardModel = BoardFilterText.Text.Trim(),
            LotId = ManagementLotFilterText?.Text.Trim() ?? string.Empty,
        };

}
