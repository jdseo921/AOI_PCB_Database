using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using AOI_Monitor.ViewModels;
using AOI_Monitor.Views;

namespace AOI_Monitor.Services;

public sealed class HmiLayoutAuditReport
{
    public string SchemaVersion { get; init; } = "hmi-layout-audit/v1";
    public DateTime GeneratedAtUtc { get; init; } = DateTime.UtcNow;
    public string AppVersion { get; init; } = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
    public double MinimumWindowWidth { get; init; } = 1920;
    public double MinimumWindowHeight { get; init; } = 1080;
    public double MinimumReadableTextPt { get; init; } = 14;
    public double MinimumPrimaryButtonWidth { get; init; } = 120;
    public double MinimumPrimaryButtonHeight { get; init; } = 40;
    public List<HmiLayoutViewResult> Views { get; init; } = new();
    public List<HmiLayoutIssue> Issues { get; init; } = new();
    public List<string> ApprovedExceptions { get; init; } = new();
    public bool Passed => Issues.All(issue => issue.Approved || issue.Severity != HmiLayoutIssueSeverity.Fail);
}

public sealed class HmiLayoutViewResult
{
    public string Key { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public double DpiScale { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
    public bool DensePage { get; init; }
    public bool HasScrollViewer { get; init; }
    public int VisualElementCount { get; init; }
    public int IssueCount { get; init; }
    public int FailureCount { get; init; }
    public int WarningCount { get; init; }
}

public sealed class HmiLayoutIssue
{
    public string Id { get; init; } = string.Empty;
    public string ViewKey { get; init; } = string.Empty;
    public string ViewName { get; init; } = string.Empty;
    public double DpiScale { get; init; }
    public string IssueType { get; init; } = string.Empty;
    public string Target { get; init; } = string.Empty;
    public HmiLayoutIssueSeverity Severity { get; init; }
    public string Message { get; init; } = string.Empty;
    public bool Approved { get; set; }
}

public enum HmiLayoutIssueSeverity
{
    Info,
    Warn,
    Fail,
}

public sealed class HmiLayoutAuditOptions
{
    public double Width { get; init; } = 1920;
    public double Height { get; init; } = 1080;
    public IReadOnlyList<double> DpiScales { get; init; } = new[] { 1.0, 1.25, 1.5 };
    public string? OutputRoot { get; init; }
    public string? ApprovedExceptionsPath { get; init; }
}

public static class HmiLayoutAuditService
{
    private const double ClipTolerance = 3.0;
    private const double MinimumReadableFontDip = 18.6667;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static HmiLayoutAuditReport RunAudit(HmiLayoutAuditOptions? options = null)
    {
        options ??= new HmiLayoutAuditOptions();
        EnsureApplicationResources();
        var exceptions = LoadApprovedExceptions(options.ApprovedExceptionsPath);
        var report = new HmiLayoutAuditReport
        {
            MinimumWindowWidth = options.Width,
            MinimumWindowHeight = options.Height,
        };

        foreach (var definition in CreateViewDefinitions())
        {
            foreach (var dpiScale in options.DpiScales)
            {
                var issues = new List<HmiLayoutIssue>();
                FrameworkElement? element = null;
                var hasScrollViewer = false;
                var elementCount = 0;

                try
                {
                    element = definition.Factory();
                    MeasureElement(element, options.Width, options.Height);
                    var descendants = Descendants(element).ToArray();
                    elementCount = descendants.Length + 1;
                    hasScrollViewer = descendants.OfType<ScrollViewer>().Any();

                    if (definition.RequiresScrollViewer && !hasScrollViewer)
                    {
                        issues.Add(CreateIssue(definition, dpiScale, "MissingScrollViewer", definition.Key, HmiLayoutIssueSeverity.Fail,
                            "Dense operator page does not contain a ScrollViewer or approved exception."));
                    }

                    AddRequiredControlIssues(definition, element, dpiScale, issues);
                    AddZeroSizeIssues(definition, element, dpiScale, issues);
                    AddTextIssues(definition, element, dpiScale, issues);
                    AddButtonIssues(definition, element, dpiScale, issues);
                    AddLongTextIssues(definition, element, dpiScale, issues);
                }
                catch (Exception ex)
                {
                    issues.Add(CreateIssue(definition, dpiScale, "ViewConstruction", definition.Key, HmiLayoutIssueSeverity.Fail,
                        $"{ex.GetType().Name}: {ex.Message}"));
                }
                finally
                {
                    if (element is Window window)
                        window.Close();
                }

                foreach (var issue in issues)
                {
                    issue.Approved = exceptions.IsApproved(issue);
                    if (issue.Approved)
                        report.ApprovedExceptions.Add(issue.Id);
                }

                report.Issues.AddRange(issues);
                report.Views.Add(new HmiLayoutViewResult
                {
                    Key = definition.Key,
                    Name = definition.Name,
                    DpiScale = dpiScale,
                    Width = options.Width,
                    Height = options.Height,
                    DensePage = definition.RequiresScrollViewer,
                    HasScrollViewer = hasScrollViewer,
                    VisualElementCount = elementCount,
                    IssueCount = issues.Count,
                    FailureCount = issues.Count(issue => issue.Severity == HmiLayoutIssueSeverity.Fail && !issue.Approved),
                    WarningCount = issues.Count(issue => issue.Severity == HmiLayoutIssueSeverity.Warn && !issue.Approved),
                });
            }
        }

        return report;
    }

    public static HmiLayoutAuditArtifacts WriteAuditArtifacts(HmiLayoutAuditReport report, string? outputRoot = null)
    {
        var root = string.IsNullOrWhiteSpace(outputRoot)
            ? Path.Combine(GetRepositoryRoot(), "TestResults")
            : outputRoot.Trim();
        Directory.CreateDirectory(root);

        var jsonPath = Path.Combine(root, "hmi_layout_audit.json");
        var htmlPath = Path.Combine(root, "hmi_layout_audit.html");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, JsonOptions), Encoding.UTF8);
        File.WriteAllText(htmlPath, BuildHtml(report), Encoding.UTF8);
        return new HmiLayoutAuditArtifacts(jsonPath, htmlPath);
    }

    public static IReadOnlyList<HmiViewAuditDefinition> CreateViewDefinitions()
    {
        return new HmiViewAuditDefinition[]
        {
            new("home", "AOI Defect Console Home", () => new HomeView { DataContext = new MainViewModel() }, true, new[] { "HomeModuleItems" }),
            new("board-image-library", "Board & Image Library", () => new LibraryView(), false, new[] { "RecordsGrid", "SchemaGrid", "ImportStatusText", "CancelImportButton" }),
            new("main-inspection", "Main Inspection", () => new MonitorView(), false, new[] { "StartInspectionButton", "StopInspectionButton", "NextBoardButton", "SaveResultButton" }),
            new("golden-compare", "Golden Template Compare", () => new CompareView(), false, new[] { "FindingsGrid", "DiffScoreText", "DefectCanvasViewbox", "GoldenCanvasViewbox" }),
            new("defect-review", "Defect Review", () => new ReviewView(), false, new[] { "QueueGrid", "ReviewCanvasViewbox" }),
            new("recipe-editor", "Recipe Editor", () => new RecipeView(), false, new[] { "RecipeNameText", "RoiGrid", "ViewportCanvas" }),
            new("ai-model-test", "AI Model Test", () => new AIModelTestView(), true, new[] { "CancelWorkButton", "ResultsGrid", "FolderPathText", "GroundTruthPathText" }),
            new("yield-analytics", "Yield Analytics", () => new SpcView(), false, new[] { "InspectionCountText", "VerdictBreakdownText", "YieldText", "DbHealthGrid" }),
            new("export-trace", "Export & Trace", () => SelectReportsTab(new ReportsView(), "Export History"), true, new[] { "ExportGrid", "CancelWorkButton" }),
            new("settings-basics", "Settings - Basics", () => SelectSettingsTab(new SettingsView(new MainViewModel()), "Basics"), true, new[] { "ApplyBtn", "CancelBtn", "ResolutionCombo", "ThemeCombo", "LangCombo", "StorageRootText" }),
            new("settings-qol", "Settings - QOL", () => SelectSettingsTab(new SettingsView(new MainViewModel()), "QOL"), true, new[] { "ApplyBtn", "CancelBtn", "ReviewDefaultText", "DetectionPriorityCombo" }),
            new("settings-ai", "Settings - AI", () => SelectSettingsTab(new SettingsView(new MainViewModel()), "AI"), true, new[] { "InspectionEngineCombo", "ModelRegistryGrid", "ThresholdProfilesGrid", "TestModelBtn" }),
            new("settings-hardware", "Settings - Hardware", () => SelectSettingsTab(new SettingsView(new MainViewModel()), "Hardware"), true, new[] { "CameraSourceCombo", "LightingModeCombo", "RunCameraAcceptanceBtn", "RunLightingAcceptanceBtn", "RunRobotCellAcceptanceBtn" }),
            new("settings-traceability", "Settings - Traceability", () => SelectSettingsTab(new SettingsView(new MainViewModel()), "Traceability"), true, new[] { "MesModeCombo", "CentralSyncModeCombo", "TestMesRestBtn" }),
            new("settings-evidence", "Settings - Evidence", () => SelectSettingsTab(new SettingsView(new MainViewModel()), "Evidence"), true, new[] { "OpenInstallNotesBtn", "OpenGuideBtn", "ExportDiagnosticsBtn", "BackupConfigurationBtn", "ExportSupportBundleBtn", "TrainingStatusText" }),
            new("calibration", "Calibration", () => new CalibrationView(), false, new[] { "PointsGrid", "ProfileCombo", "SampleImagePathText" }),
            new("profile-3d", "3D Profile Viewer", () => new ProfileView(), false, new[] { "RunAcceptanceButton", "CancelAcceptanceButton", "ProfileSourceCombo" }),
            new("hardware-readiness", "Hardware Readiness", () => new PilotWizardView(), false, new[] { "ProfileCombo", "StepsGrid", "StatusText" }),
            new("factory-readiness", "Factory Readiness", () => SelectReportsTab(new ReportsView(), "Factory Readiness"), true, new[] { "FactoryReadinessGrid" }),
            new("standards-quality-checklist", "Standards & Quality Checklist", () => SelectReportsTab(new ReportsView(), "Standards & Quality Checklist"), true, new[] { "StandardsTraceabilityGrid", "StandardsTraceabilitySummaryText" }),
            new("management-dashboard", "Management Dashboard", () => SelectReportsTab(new ReportsView(), "Management Dashboard"), true, new[] { "ManagementDefectGrid", "ManagementRoiGrid", "ManagementModelTrendGrid" }),
            new("model-registry-acceptance", "Model Registry / Model Acceptance", () => SelectSettingsTab(new SettingsView(new MainViewModel()), "AI"), true, new[] { "ModelRegistryGrid", "ModelAcceptanceRunsGrid" }),
            new("mes-queue", "MES Queue", () => SelectReportsTab(new ReportsView(), "MES Upload Queue"), true, new[] { "MesSpoolGrid", "MesQueueStatusFilter" }),
            new("central-sync", "Central Sync", () => SelectReportsTab(new ReportsView(), "Central Evidence Sync"), true, new[] { "CentralSyncGrid" }),
            new("factory-acceptance", "Factory Acceptance Checklist", () => SelectReportsTab(new ReportsView(), "Factory Acceptance"), true, new[] { "FactoryAcceptanceGrid", "FactoryAcceptanceProfileCombo" }),
            new("completion-matrix", "Completion Matrix", () => SelectReportsTab(new ReportsView(), "Evidence Completion"), true, new[] { "CompletionMatrixGrid", "CompletionMatrixSummaryText" }),
            new("installation-notes", "Installation Notes", () => new InstallView(), true, new[] { "RuntimeGrid", "BoundaryGrid" }),
            new("guide", "Guide", () => new GuideView(), true, new[] { "StepsGrid" }),
        };
    }

    private static FrameworkElement SelectSettingsTab(SettingsView view, string header)
    {
        view.Measure(new Size(1920, 1080));
        view.Arrange(new Rect(0, 0, 1920, 1080));
        view.UpdateLayout();
        foreach (var tabControl in Descendants(view).OfType<TabControl>())
        {
            foreach (var item in tabControl.Items.OfType<TabItem>())
            {
                if ((item.Header?.ToString() ?? string.Empty).Equals(header, StringComparison.OrdinalIgnoreCase))
                {
                    tabControl.SelectedItem = item;
                    tabControl.UpdateLayout();
                    view.UpdateLayout();
                    return view;
                }
            }
        }

        return view;
    }

    private static FrameworkElement SelectReportsTab(ReportsView view, string header)
    {
        view.Measure(new Size(1920, 1080));
        view.Arrange(new Rect(0, 0, 1920, 1080));
        view.UpdateLayout();
        foreach (var tabControl in Descendants(view).OfType<TabControl>())
        {
            foreach (var item in tabControl.Items.OfType<TabItem>())
            {
                if ((item.Header?.ToString() ?? string.Empty).Equals(header, StringComparison.OrdinalIgnoreCase))
                {
                    tabControl.SelectedItem = item;
                    tabControl.UpdateLayout();
                    return view;
                }
            }
        }

        return view;
    }

    private static void AddRequiredControlIssues(HmiViewAuditDefinition definition, FrameworkElement root, double dpiScale, ICollection<HmiLayoutIssue> issues)
    {
        foreach (var name in definition.RequiredControlNames)
        {
            if (FindByName(root, name) is not FrameworkElement control)
            {
                issues.Add(CreateIssue(definition, dpiScale, "MissingRequiredControl", name, HmiLayoutIssueSeverity.Fail,
                    $"Important named control '{name}' was not found."));
                continue;
            }

            if (!IsEffectivelyVisible(control) || control.ActualWidth <= 0 || control.ActualHeight <= 0)
            {
                issues.Add(CreateIssue(definition, dpiScale, "UnreachableRequiredControl", name, HmiLayoutIssueSeverity.Fail,
                    $"Important control '{name}' is not visible/reachable. Actual={control.ActualWidth:N0}x{control.ActualHeight:N0}."));
            }
        }
    }

    private static void AddZeroSizeIssues(HmiViewAuditDefinition definition, FrameworkElement root, double dpiScale, ICollection<HmiLayoutIssue> issues)
    {
        foreach (var element in Descendants(root).OfType<FrameworkElement>())
        {
            if (IsTemplateInternal(element))
                continue;
            if (!IsEffectivelyVisible(element) || element is not (TextBlock or Button or Label or TextBox or ComboBox or DataGrid))
                continue;
            if (element.ActualWidth > 0 && element.ActualHeight > 0)
                continue;

            issues.Add(CreateIssue(definition, dpiScale, "ZeroSizeControl", Describe(element), HmiLayoutIssueSeverity.Fail,
                $"Visible control has non-positive rendered size. Actual={element.ActualWidth:N0}x{element.ActualHeight:N0}."));
        }
    }

    private static void AddTextIssues(HmiViewAuditDefinition definition, FrameworkElement root, double dpiScale, ICollection<HmiLayoutIssue> issues)
    {
        foreach (var textBlock in Descendants(root).OfType<TextBlock>())
        {
            if (IsTemplateInternal(textBlock))
                continue;
            if (!IsEffectivelyVisible(textBlock) || string.IsNullOrWhiteSpace(textBlock.Text) || textBlock.ActualWidth <= 0 || textBlock.ActualHeight <= 0)
                continue;

            if (IsCriticalText(textBlock) && textBlock.FontSize < MinimumReadableFontDip)
            {
                issues.Add(CreateIssue(definition, dpiScale, "SmallCriticalText", Describe(textBlock), HmiLayoutIssueSeverity.Warn,
                    $"Critical text is below 14 pt readable target. FontSize={textBlock.FontSize:N1} DIP."));
            }

            var desired = MeasureTextBlock(textBlock, textBlock.ActualWidth);
            var tooNarrow = textBlock.TextWrapping == TextWrapping.NoWrap &&
                textBlock.TextTrimming == TextTrimming.None &&
                desired.Width > textBlock.ActualWidth + ClipTolerance;
            var tooShort = desired.Height > textBlock.ActualHeight + ClipTolerance;
            if (!tooNarrow && !tooShort)
                continue;

            var severity = IsCriticalText(textBlock) ? HmiLayoutIssueSeverity.Fail : HmiLayoutIssueSeverity.Warn;
            issues.Add(CreateIssue(definition, dpiScale, "ClippedText", Describe(textBlock), severity,
                $"Text may be clipped. Desired={desired.Width:N0}x{desired.Height:N0}; actual={textBlock.ActualWidth:N0}x{textBlock.ActualHeight:N0}."));
        }
    }

    private static void AddButtonIssues(HmiViewAuditDefinition definition, FrameworkElement root, double dpiScale, ICollection<HmiLayoutIssue> issues)
    {
        foreach (var button in Descendants(root).OfType<Button>())
        {
            if (IsTemplateInternal(button))
                continue;
            if (!IsEffectivelyVisible(button))
                continue;

            var target = Describe(button);
            if (IsPrimaryOperatorButton(button) && (button.ActualWidth < 120 || button.ActualHeight < 40))
            {
                issues.Add(CreateIssue(definition, dpiScale, "SmallPrimaryButton", target, HmiLayoutIssueSeverity.Fail,
                    $"Primary operator button is below 120x40 px. Actual={button.ActualWidth:N0}x{button.ActualHeight:N0}."));
            }

            var desired = MeasureButton(button);
            if (desired.Width > button.ActualWidth + ClipTolerance || desired.Height > button.ActualHeight + ClipTolerance)
            {
                issues.Add(CreateIssue(definition, dpiScale, "ClippedButton", target, HmiLayoutIssueSeverity.Fail,
                    $"Button content may be clipped. Desired={desired.Width:N0}x{desired.Height:N0}; actual={button.ActualWidth:N0}x{button.ActualHeight:N0}."));
            }
        }
    }

    private static void AddLongTextIssues(HmiViewAuditDefinition definition, FrameworkElement root, double dpiScale, ICollection<HmiLayoutIssue> issues)
    {
        foreach (var element in Descendants(root).OfType<FrameworkElement>())
        {
            var text = element switch
            {
                TextBlock textBlock => textBlock.Text,
                TextBox textBox => textBox.Text,
                _ => string.Empty,
            };
            if (string.IsNullOrWhiteSpace(text) || text.Length < 32)
                continue;
            if (!IsEffectivelyVisible(element))
                continue;
            if (!LooksLikePathModelOrMessage(element, text))
                continue;

            var hasTooltip = element.ToolTip is not null;
            var wraps = element is TextBlock { TextWrapping: not TextWrapping.NoWrap } or TextBox { TextWrapping: not TextWrapping.NoWrap };
            var trims = element is TextBlock { TextTrimming: not TextTrimming.None };
            if (hasTooltip || wraps || trims)
                continue;

            issues.Add(CreateIssue(definition, dpiScale, "LongTextNoWrapOrTooltip", Describe(element), HmiLayoutIssueSeverity.Warn,
                "Long path/model/message text should wrap, trim with tooltip, or expose a tooltip."));
        }
    }

    private static Size MeasureTextBlock(TextBlock source, double width)
    {
        var clone = new TextBlock
        {
            Text = source.Text,
            FontFamily = source.FontFamily,
            FontSize = source.FontSize,
            FontWeight = source.FontWeight,
            FontStyle = source.FontStyle,
            TextWrapping = source.TextWrapping,
            TextTrimming = source.TextTrimming,
            LineHeight = source.LineHeight,
            MaxWidth = source.MaxWidth,
            MinWidth = source.MinWidth,
            Padding = source.Padding,
        };
        clone.Measure(new Size(width, double.PositiveInfinity));
        return clone.DesiredSize;
    }

    private static Size MeasureButton(Button source)
    {
        var clone = new Button
        {
            Content = source.Content,
            FontFamily = source.FontFamily,
            FontSize = source.FontSize,
            FontWeight = source.FontWeight,
            FontStyle = source.FontStyle,
            Padding = source.Padding,
            MinWidth = source.MinWidth,
            MinHeight = source.MinHeight,
        };
        clone.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return clone.DesiredSize;
    }

    private static bool IsPrimaryOperatorButton(Button button)
    {
        var actionSized = button.ActualHeight >= 36 || button.MinHeight >= 36;
        if (!actionSized)
            return false;

        if (!string.IsNullOrWhiteSpace(button.Name) &&
            (button.Name.Contains("Start", StringComparison.OrdinalIgnoreCase) ||
             button.Name.Contains("Stop", StringComparison.OrdinalIgnoreCase) ||
             button.Name.Contains("Save", StringComparison.OrdinalIgnoreCase) ||
             button.Name.Contains("Apply", StringComparison.OrdinalIgnoreCase) ||
             button.Name.Contains("Run", StringComparison.OrdinalIgnoreCase) ||
             button.Name.Contains("Export", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var content = button.Content?.ToString() ?? string.Empty;
        return content.Contains("Start", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("Stop", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("Save", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("Apply", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("Run", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("Export", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("Generate", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTemplateInternal(FrameworkElement element)
        => element.TemplatedParent is not null ||
           (!string.IsNullOrWhiteSpace(element.Name) && element.Name.StartsWith("PART_", StringComparison.Ordinal));

    private static bool IsEffectivelyVisible(FrameworkElement element)
    {
        for (DependencyObject? current = element; current is not null; current = ParentOf(current))
        {
            if (current is UIElement { Visibility: not Visibility.Visible })
                return false;
        }

        return true;
    }

    private static DependencyObject? ParentOf(DependencyObject element)
    {
        DependencyObject? visualParent = null;
        if (element is Visual or Visual3D)
            visualParent = VisualTreeHelper.GetParent(element);

        return visualParent ?? LogicalTreeHelper.GetParent(element);
    }

    private static bool IsCriticalText(TextBlock textBlock)
    {
        var name = textBlock.Name ?? string.Empty;
        var text = textBlock.Text ?? string.Empty;
        return name.Contains("Status", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Alarm", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Warning", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Summary", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("ALARM", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("WARNING", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("NG", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("FAIL", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikePathModelOrMessage(FrameworkElement element, string text)
    {
        var name = element.Name ?? string.Empty;
        return name.Contains("Path", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Model", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Message", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Status", StringComparison.OrdinalIgnoreCase) ||
            text.Contains('\\', StringComparison.Ordinal) ||
            text.Contains('/', StringComparison.Ordinal) ||
            text.Contains("model", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("message", StringComparison.OrdinalIgnoreCase);
    }

    private static void MeasureElement(FrameworkElement element, double width, double height)
    {
        element.Width = width;
        element.Height = height;
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }

    private static DependencyObject? FindByName(FrameworkElement root, string name)
    {
        if (root.Name == name)
            return root;

        if (Descendants(root).OfType<FrameworkElement>().FirstOrDefault(element => element.Name == name) is { } visualMatch)
            return visualMatch;

        return root.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(root) as DependencyObject;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child))
                yield return descendant;
        }
    }

    private static string Describe(FrameworkElement element)
    {
        var name = string.IsNullOrWhiteSpace(element.Name) ? element.GetType().Name : $"{element.GetType().Name}#{element.Name}";
        return element switch
        {
            Button { Content: string content } => $"{name} \"{Trim(content)}\"",
            TextBlock { Text.Length: > 0 } textBlock => $"{name} \"{Trim(textBlock.Text)}\"",
            TextBox { Text.Length: > 0 } textBox => $"{name} \"{Trim(textBox.Text)}\"",
            _ => name,
        };
    }

    private static HmiLayoutIssue CreateIssue(HmiViewAuditDefinition definition, double dpiScale, string issueType, string target, HmiLayoutIssueSeverity severity, string message)
    {
        var normalizedTarget = NormalizeTarget(target);
        return new HmiLayoutIssue
        {
            Id = $"{definition.Key}|{dpiScale.ToString("0.##", CultureInfo.InvariantCulture)}|{issueType}|{normalizedTarget}",
            ViewKey = definition.Key,
            ViewName = definition.Name,
            DpiScale = dpiScale,
            IssueType = issueType,
            Target = target,
            Severity = severity,
            Message = message,
        };
    }

    private static string NormalizeTarget(string target)
        => string.Concat((target ?? string.Empty).Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '#' ? ch : '_'));

    private static string Trim(string value)
        => value.Length <= 72 ? value : value[..69] + "...";

    private static HmiLayoutApprovedExceptions LoadApprovedExceptions(string? path)
    {
        var exceptionPath = string.IsNullOrWhiteSpace(path)
            ? Path.Combine(GetRepositoryRoot(), "Tools", "quality-gates", "hmi_layout_approved_exceptions.json")
            : path.Trim();
        if (!File.Exists(exceptionPath))
            return new HmiLayoutApprovedExceptions();

        var file = JsonSerializer.Deserialize<HmiLayoutApprovedExceptionFile>(File.ReadAllText(exceptionPath), JsonOptions)
            ?? new HmiLayoutApprovedExceptionFile();
        if (!file.SchemaVersion.Equals("hmi-layout-approved-exceptions/v1", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unsupported HMI layout approved exceptions schema.");

        foreach (var exception in file.Exceptions)
        {
            if (HasWildcard(exception.ViewKey) || HasWildcard(exception.IssueType) || HasWildcard(exception.Target))
                throw new InvalidDataException("HMI layout approved exceptions must be exact and cannot contain wildcards.");
            if (string.IsNullOrWhiteSpace(exception.ViewKey) ||
                string.IsNullOrWhiteSpace(exception.IssueType) ||
                string.IsNullOrWhiteSpace(exception.Target) ||
                string.IsNullOrWhiteSpace(exception.Reason))
            {
                throw new InvalidDataException("HMI layout approved exceptions require viewKey, issueType, target, and reason.");
            }
        }

        return new HmiLayoutApprovedExceptions(file.Exceptions);
    }

    private static bool HasWildcard(string value)
        => value.Contains('*', StringComparison.Ordinal) || value.Contains('?', StringComparison.Ordinal);

    private static string BuildHtml(HmiLayoutAuditReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html><html><head><meta charset=\"utf-8\"><title>HMI Layout Audit</title>");
        sb.AppendLine("<style>body{font-family:Segoe UI,Arial,sans-serif;margin:24px;color:#17212b}table{border-collapse:collapse;width:100%;margin:14px 0}td,th{border:1px solid #cfd8df;padding:6px 8px;text-align:left;vertical-align:top}.Pass{color:#176b3a}.Fail{color:#a12626}.Warn{color:#8a5a00}.approved{color:#66727c}</style></head><body>");
        sb.AppendLine($"<h1>HMI Layout Audit <span class=\"{(report.Passed ? "Pass" : "Fail")}\">{(report.Passed ? "PASS" : "FAIL")}</span></h1>");
        sb.AppendLine($"<p>Generated UTC: {report.GeneratedAtUtc:O}<br>Window: {report.MinimumWindowWidth:N0}x{report.MinimumWindowHeight:N0}<br>DPI profiles: 100%, 125%, 150%</p>");
        sb.AppendLine("<h2>Views</h2><table><tr><th>View</th><th>DPI</th><th>Scroll</th><th>Elements</th><th>Failures</th><th>Warnings</th></tr>");
        foreach (var view in report.Views)
            sb.AppendLine($"<tr><td>{Escape(view.Name)}</td><td>{view.DpiScale:P0}</td><td>{(view.HasScrollViewer ? "Yes" : "No")}</td><td>{view.VisualElementCount}</td><td>{view.FailureCount}</td><td>{view.WarningCount}</td></tr>");
        sb.AppendLine("</table><h2>Issues</h2><table><tr><th>Status</th><th>View</th><th>DPI</th><th>Type</th><th>Target</th><th>Message</th></tr>");
        foreach (var issue in report.Issues)
        {
            var status = issue.Approved ? "Approved" : issue.Severity.ToString();
            var css = issue.Approved ? "approved" : issue.Severity.ToString();
            sb.AppendLine($"<tr><td class=\"{css}\">{status}</td><td>{Escape(issue.ViewName)}</td><td>{issue.DpiScale:P0}</td><td>{Escape(issue.IssueType)}</td><td>{Escape(issue.Target)}</td><td>{Escape(issue.Message)}</td></tr>");
        }
        sb.AppendLine("</table></body></html>");
        return sb.ToString();
    }

    private static string Escape(string value)
        => System.Net.WebUtility.HtmlEncode(value ?? string.Empty);

    private static void EnsureApplicationResources()
    {
        if (Application.Current is not null)
            return;

        var app = new App();
        app.InitializeComponent();
    }

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AOI_PCB_Database.slnx")))
            directory = directory.Parent;

        if (directory is null)
            throw new DirectoryNotFoundException("Could not locate repository root.");

        return directory.FullName;
    }
}

public sealed record HmiLayoutAuditArtifacts(string JsonPath, string HtmlPath);

public sealed record HmiViewAuditDefinition(
    string Key,
    string Name,
    Func<FrameworkElement> Factory,
    bool RequiresScrollViewer,
    IReadOnlyList<string> RequiredControlNames);

internal sealed class HmiLayoutApprovedExceptionFile
{
    public string SchemaVersion { get; set; } = "hmi-layout-approved-exceptions/v1";
    public List<HmiLayoutApprovedException> Exceptions { get; set; } = new();
}

internal sealed class HmiLayoutApprovedException
{
    public string ViewKey { get; set; } = string.Empty;
    public double? DpiScale { get; set; }
    public string IssueType { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

internal sealed class HmiLayoutApprovedExceptions
{
    private readonly IReadOnlyList<HmiLayoutApprovedException> _exceptions;

    public HmiLayoutApprovedExceptions()
        : this(Array.Empty<HmiLayoutApprovedException>())
    {
    }

    public HmiLayoutApprovedExceptions(IReadOnlyList<HmiLayoutApprovedException> exceptions)
    {
        _exceptions = exceptions;
    }

    public bool IsApproved(HmiLayoutIssue issue)
        => _exceptions.Any(exception =>
            exception.ViewKey.Equals(issue.ViewKey, StringComparison.OrdinalIgnoreCase) &&
            exception.IssueType.Equals(issue.IssueType, StringComparison.OrdinalIgnoreCase) &&
            exception.Target.Equals(issue.Target, StringComparison.OrdinalIgnoreCase) &&
            (exception.DpiScale is null || Math.Abs(exception.DpiScale.Value - issue.DpiScale) < 0.001));
}
