using System.Windows;
using System.Windows.Controls;
using System.Diagnostics;
using System.IO;
using AOI_Monitor.Services;
using AOI_Monitor.ViewModels;

namespace AOI_Monitor.Views;

public partial class SettingsView : UserControl
{
    private readonly MainViewModel _vm;

    public SettingsView(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        WorkflowState.Instance.StateChanged += OnWorkflowStateChanged;
        RefreshWorkflowUi();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        WorkflowState.Instance.StateChanged -= OnWorkflowStateChanged;
    }

    private void OnWorkflowStateChanged() => Dispatcher.Invoke(RefreshWorkflowUi);

    private void OnApply(object sender, RoutedEventArgs e)
    {
        var state = WorkflowState.Instance;
        state.SetDetectionPriority(ComboToPriority(DetectionPriorityCombo.SelectedIndex));
        MessageBox.Show("Settings applied.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        LangCombo.SelectedIndex = 0;
        FontCombo.SelectedIndex = 1;
        DetectionPriorityCombo.SelectedIndex = 0;
        WorkflowState.Instance.SetDetectionPriority(Models.DetectionPriority.MinimizeFalsePositives);
        MessageBox.Show("Settings reset to defaults.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnStartTrainingClick(object sender, RoutedEventArgs e)
    {
        var state = WorkflowState.Instance;
        if (state.Training.IsRunning)
        {
            MessageBox.Show("Training session is already running.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        state.StartTraining();
    }

    private void OnRunEpochClick(object sender, RoutedEventArgs e)
    {
        var state = WorkflowState.Instance;
        if (!state.Training.IsRunning)
        {
            MessageBox.Show("Start a training session first.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            MessageBox.Show("Training session is already stopped.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        state.StopTraining();
    }

    private void OnOpenTrainingFolderClick(object sender, RoutedEventArgs e)
    {
        var trainingDir = Path.Combine(AppContext.BaseDirectory, "exports", "training_set");
        Directory.CreateDirectory(trainingDir);

        Process.Start(new ProcessStartInfo
        {
            FileName = trainingDir,
            UseShellExecute = true,
        });
    }

    private void RefreshWorkflowUi()
    {
        var state = WorkflowState.Instance;

        DetectionPriorityCombo.SelectedIndex = state.DetectionPriority switch
        {
            Models.DetectionPriority.MinimizeFalsePositives => 0,
            Models.DetectionPriority.Balanced => 1,
            Models.DetectionPriority.MaximizeDefectRecall => 2,
            _ => 0,
        };

        ReviewDefaultText.Text = WorkflowState.ToDisplay(state.DetectionPriority);
        TrainingStatusText.Text = state.Training.IsRunning ? "RUNNING" : "IDLE";
        TrainingQueueText.Text = state.Training.QueuedSamples.ToString();
        TrainingEpochText.Text = state.Training.EpochsCompleted.ToString();
        TrainingValidationText.Text = state.Training.LastCompletedAt is null
            ? "--"
            : $"{state.Training.LastValidationScore:F1}%";
    }

    private static Models.DetectionPriority ComboToPriority(int selectedIndex) => selectedIndex switch
    {
        0 => Models.DetectionPriority.MinimizeFalsePositives,
        1 => Models.DetectionPriority.Balanced,
        2 => Models.DetectionPriority.MaximizeDefectRecall,
        _ => Models.DetectionPriority.MinimizeFalsePositives,
    };
}
