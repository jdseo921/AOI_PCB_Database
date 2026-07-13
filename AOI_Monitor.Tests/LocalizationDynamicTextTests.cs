using System.Windows.Controls;
using AOI_Monitor.Services;
using Xunit;

namespace AOI_Monitor.Tests;

/// <summary>
/// Regression tests for the localization walker's non-destructive re-apply contract.
/// The shell re-localizes on preference changes; TextBlocks whose text was rewritten at
/// runtime (alarm counts, footer status, banners) must keep that runtime text instead of
/// being reset to their first-seen XAML literal, while static literals must still switch
/// cleanly between English and Korean.
/// </summary>
public sealed class LocalizationDynamicTextTests
{
    [Fact]
    public void ReapplyKeepsRuntimeUpdatedTextAndStillTranslatesStaticLiterals()
    {
        UiNavigationSmokeTests.RunOnStaForTests(() =>
        {
            var panel = new StackPanel();
            var staticBlock = new TextBlock { Text = "Demo Mode" };
            var dynamicBlock = new TextBlock { Text = "none" };
            panel.Children.Add(staticBlock);
            panel.Children.Add(dynamicBlock);

            UiPreferencesService.ApplyLocalization(panel, UiLanguage.English);
            dynamicBlock.Text = "35 alarm / 1 warning";

            // Regression: a re-apply used to reset runtime text to the first-seen literal ("none").
            UiPreferencesService.ApplyLocalization(panel, UiLanguage.English);
            Assert.Equal("35 alarm / 1 warning", dynamicBlock.Text);
            Assert.Equal("Demo Mode", staticBlock.Text);

            UiPreferencesService.ApplyLocalization(panel, UiLanguage.Korean);
            Assert.Equal("데모 모드", staticBlock.Text);
            Assert.Equal("35 alarm / 1 warning", dynamicBlock.Text);

            UiPreferencesService.ApplyLocalization(panel, UiLanguage.English);
            Assert.Equal("Demo Mode", staticBlock.Text);
            Assert.Equal("35 alarm / 1 warning", dynamicBlock.Text);
        });
    }

    [Fact]
    public void ReapplyKeepsRuntimeUpdatedToolTipText()
    {
        UiNavigationSmokeTests.RunOnStaForTests(() =>
        {
            var block = new TextBlock { Text = "Status", ToolTip = "No active alarms." };

            UiPreferencesService.ApplyLocalization(block, UiLanguage.English);
            block.ToolTip = "3 Critical / 2 Alarm / 1 Warning active. Open active alarm list.";

            UiPreferencesService.ApplyLocalization(block, UiLanguage.English);
            Assert.Equal("3 Critical / 2 Alarm / 1 Warning active. Open active alarm list.", block.ToolTip);
        });
    }
}
