using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using MyCapture.App.Settings;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// The "Recording method" fold on the recording page, as chosen in the 2026-11-06 aesthetic
/// review (P10, option A): the fold's title already names the choice, so its content no longer
/// repeats it as a label. The list and its guide button share one row, as the capture page's
/// "After capture" list does, and the list keeps the name for screen readers and settings search.
/// </summary>
/// <remarks>
/// With <c>MYCAPTURE_AES_SO_FIX_EVIDENCE</c> set to a folder, the check writes the fold as PNG at
/// 96 and 144 dpi before it asserts anything (see <c>SaveAlignmentEvidence</c>).
/// </remarks>
public sealed partial class SettingsThemePreviewTests
{
    [Fact]
    public Task AestheticSettings_RecordingMethodFoldDoesNotRepeatItsTitle() =>
        RunIsolated(nameof(AestheticSettings_RecordingMethodFoldDoesNotRepeatItsTitle), () => InSettingsWindows(VerifyRecordingMethodFold));

    private static void VerifyRecordingMethodFold(SettingsWindow window, TabControl tabs, string locale)
    {
        var fold = Assert.IsType<Expander>(window.FindName("RecordingCompatibilityExpander"));
        tabs.SelectedItem = window.FindName("RecordingTab");
        fold.IsExpanded = true;
        Drain(window);
        fold.BringIntoView();
        Drain(window);
        SaveAlignmentEvidence(window, $"recording-method-fold-{locale}", BoundsIn(fold, window));

        string name = UiText.Get("RecordingBackend_Label");
        Assert.Equal(UiText.Get("SettingsStructure_RecordingCompatibility"), (string)fold.Header);
        var content = Assert.IsAssignableFrom<FrameworkElement>(fold.Content);
        Assert.DoesNotContain(Descendants(content).OfType<TextBlock>(),
            text => text.IsVisible && KoreanWordWrap.Strip(text.Text) == name);
        var selector = Assert.IsType<ComboBox>(window.FindName("RecordingCaptureModeSelector"));
        Assert.Equal(name, AutomationProperties.GetName(selector));
        Button guide = Descendants(content).OfType<Button>().Single(button => Equals(button.Tag, "G06"));
        Assert.Same(VisualTreeHelper.GetParent(selector), VisualTreeHelper.GetParent(guide));
        Rect list = BoundsIn(selector, window), button = BoundsIn(guide, window);
        Assert.True(button.Left >= list.Right, $"guide button {button} beside the list {list}");
        Assert.InRange(Math.Abs(VerticalCentre(list) - VerticalCentre(button)), 0, 1);

        // Searching for the name still opens this fold on the recording page.
        fold.IsExpanded = false;
        tabs.SelectedItem = window.FindName("GeneralTab");
        Drain(window);
        window.FilterSettings(name);
        Drain(window);
        Assert.Same(window.FindName("RecordingTab"), tabs.SelectedItem);
        FrameworkElement? match = window.FirstSearchMatch;
        Assert.NotNull(match);
        DependencyObject? node = match;
        while (node is not null && !ReferenceEquals(node, fold)) node = LogicalTreeHelper.GetParent(node);
        Assert.Same(fold, node);
        Assert.True(fold.IsExpanded);
        Assert.True(selector.IsVisible);
        window.FilterSettings(string.Empty);
    }
}
