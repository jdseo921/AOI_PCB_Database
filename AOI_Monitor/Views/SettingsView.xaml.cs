using System.Windows;
using System.Windows.Controls;
using System.Diagnostics;
using System.IO;
<<<<<<< HEAD
using System.Globalization;
using System.Windows.Markup;
using System.Windows.Media;
=======
>>>>>>> 67117d637c0ef2a7f4698c2245b5001171a02ca2
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
<<<<<<< HEAD
        ApplyLanguageVisuals();
        ApplyFontPreset();
=======
>>>>>>> 67117d637c0ef2a7f4698c2245b5001171a02ca2
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        WorkflowState.Instance.StateChanged -= OnWorkflowStateChanged;
    }

    private void OnWorkflowStateChanged() => Dispatcher.Invoke(RefreshWorkflowUi);

    private void OnApply(object sender, RoutedEventArgs e)
    {
        var state = WorkflowState.Instance;
<<<<<<< HEAD
        ApplyLanguageVisuals();
        ApplyFontPreset();

        if (!state.TrySetDetectionPriority(ComboToPriority(DetectionPriorityCombo.SelectedIndex), out var message))
        {
            MessageBox.Show($"Display settings applied.\n{message}", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
            RefreshWorkflowUi();
            return;
        }

        MessageBox.Show($"Display settings applied.\n{message}", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
=======
        state.SetDetectionPriority(ComboToPriority(DetectionPriorityCombo.SelectedIndex));
        MessageBox.Show("Settings applied.", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
>>>>>>> 67117d637c0ef2a7f4698c2245b5001171a02ca2
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        LangCombo.SelectedIndex = 0;
        FontCombo.SelectedIndex = 1;
        DetectionPriorityCombo.SelectedIndex = 0;
<<<<<<< HEAD

        ApplyLanguageVisuals();
        ApplyFontPreset();

        var state = WorkflowState.Instance;
        if (!state.TrySetDetectionPriority(Models.DetectionPriority.MinimizeFalsePositives, out var message))
        {
            MessageBox.Show($"Display settings reset.\n{message}", "AOI Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
            RefreshWorkflowUi();
            return;
        }

=======
        WorkflowState.Instance.SetDetectionPriority(Models.DetectionPriority.MinimizeFalsePositives);
>>>>>>> 67117d637c0ef2a7f4698c2245b5001171a02ca2
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

<<<<<<< HEAD
    private void ApplyLanguageVisuals()
    {
        bool isKorean = LangCombo.SelectedIndex == 1;
        var culture = isKorean ? new CultureInfo("ko-KR") : new CultureInfo("en-US");

        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;

        if (Application.Current.MainWindow is Window mainWindow)
        {
            mainWindow.Language = XmlLanguage.GetLanguage(culture.IetfLanguageTag);
            mainWindow.FontFamily = isKorean ? new FontFamily("Malgun Gothic") : new FontFamily("Segoe UI");
        }

        DisplayLanguageHeaderText.Text = isKorean ? "화면 / 언어" : "Display / Language";
        LanguageLabelText.Text = isKorean ? "언어" : "Language";
        FontSizeLabelText.Text = isKorean ? "글자 크기" : "Font Size";
        StoragePathLabelText.Text = isKorean ? "저장 경로" : "Storage Path";
        ReviewDefaultLabelText.Text = isKorean ? "검토 기본값" : "Review Default";
        DetectionPriorityLabelText.Text = isKorean ? "검출 우선순위" : "Detection Priority";
        ApplyBtn.Content = isKorean ? "적용" : "Apply";
        ResetBtn.Content = isKorean ? "초기화" : "Reset";

        SetComboItemText(LangCombo, 0, "English");
        SetComboItemText(LangCombo, 1, "Korean");

        if (isKorean)
        {
            SetComboItemText(FontCombo, 0, "작게");
            SetComboItemText(FontCombo, 1, "기본");
            SetComboItemText(FontCombo, 2, "크게");

            SetComboItemText(DetectionPriorityCombo, 0, "오검출 최소화");
            SetComboItemText(DetectionPriorityCombo, 1, "균형");
            SetComboItemText(DetectionPriorityCombo, 2, "결함 검출 최대화");
        }
        else
        {
            SetComboItemText(FontCombo, 0, "Compact");
            SetComboItemText(FontCombo, 1, "Standard");
            SetComboItemText(FontCombo, 2, "Large");

            SetComboItemText(DetectionPriorityCombo, 0, "Minimize False Positives");
            SetComboItemText(DetectionPriorityCombo, 1, "Balanced");
            SetComboItemText(DetectionPriorityCombo, 2, "Maximize Defect Recall");
        }
    }

    private void ApplyFontPreset()
    {
        if (Application.Current.MainWindow is not Window mainWindow)
            return;

        var scale = FontCombo.SelectedIndex switch
        {
            0 => 0.92,
            2 => 1.08,
            _ => 1.0,
        };

        if (mainWindow.Content is FrameworkElement root)
            root.LayoutTransform = new ScaleTransform(scale, scale);

        mainWindow.FontSize = FontCombo.SelectedIndex switch
        {
            0 => 12,
            2 => 14,
            _ => 13,
        };
    }

    private static void SetComboItemText(ComboBox comboBox, int index, string text)
    {
        if (index < 0 || index >= comboBox.Items.Count)
            return;

        if (comboBox.Items[index] is ComboBoxItem item)
            item.Content = text;
    }

=======
>>>>>>> 67117d637c0ef2a7f4698c2245b5001171a02ca2
    private static Models.DetectionPriority ComboToPriority(int selectedIndex) => selectedIndex switch
    {
        0 => Models.DetectionPriority.MinimizeFalsePositives,
        1 => Models.DetectionPriority.Balanced,
        2 => Models.DetectionPriority.MaximizeDefectRecall,
        _ => Models.DetectionPriority.MinimizeFalsePositives,
    };
}
