using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using MyCapture.App.Settings;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Settings choices adopted from the 2026-11-06 aesthetic review: a shortcut problem reads as an
/// error, not as the next row's name; a section header shows the shared buttons' focus ring; the
/// first-use guide has its own help section and the privacy statements read as the tab's other
/// descriptions; and a search without matches says so in the content area as well.
/// </summary>
/// <remarks>
/// With <c>MYCAPTURE_AES_SO_FIX_EVIDENCE</c> set to a folder, the checks write the area they
/// measure as PNG at 96 and 144 dpi (see <c>SaveAlignmentEvidence</c>).
/// </remarks>
public sealed partial class SettingsThemePreviewTests
{
    [Fact]
    public Task AestheticSettings_ShortcutProblemsTakeTheDangerState() =>
        RunIsolated(nameof(AestheticSettings_ShortcutProblemsTakeTheDangerState), VerifyShortcutProblemState);

    [Fact]
    public Task AestheticSettings_SectionHeadersTakeTheSharedFocusRing() =>
        RunIsolated(nameof(AestheticSettings_SectionHeadersTakeTheSharedFocusRing), () => InSettingsWindows(VerifySectionHeaderFocusRing));

    [Fact]
    public Task AestheticSettings_AboutTabHasAHelpSectionAndDescriptionPrivacyText() =>
        RunIsolated(nameof(AestheticSettings_AboutTabHasAHelpSectionAndDescriptionPrivacyText), () => InSettingsWindows(VerifyAboutSections));

    [Fact]
    public Task AestheticSettings_SearchWithoutMatchesSaysSoInTheContentArea() =>
        RunIsolated(nameof(AestheticSettings_SearchWithoutMatchesSaysSoInTheContentArea), () => InSettingsWindows(VerifySearchNoMatch));

    // The message under a recorder is State.Danger at normal weight, and the recorder's outline turns
    // State.Danger with it, as an invalid number field in the same window does.
    private static void VerifyShortcutProblemState()
    {
        using var language = UiText.UseLanguage("ko-KR");
        Application application = CreateThemedApplication();
        ThemeService.ApplyFromSettings(AppThemeNames.Glass);
        Func<Hotkey, bool>? canRegister = HotkeyRecorder.CanRegister;
        var recorder = new HotkeyRecorder { HorizontalAlignment = HorizontalAlignment.Left };
        // A selected settings tab passes SemiBold down to its content; the message must not take it.
        var host = new StackPanel { Margin = new Thickness(16), Children = { recorder } };
        TextElement.SetFontWeight(host, FontWeights.SemiBold);
        var window = new Window
        {
            Content = host, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000,
            Width = 520, Height = 220, ShowActivated = false, ShowInTaskbar = false,
        };
        window.SetResourceReference(Control.BackgroundProperty, "Surface.Base");
        try
        {
            window.Show();
            Drain(window);
            TextBlock message = Assert.IsType<TextBlock>(recorder.Children[1]);
            Button record = Assert.IsType<Button>(Assert.IsType<StackPanel>(recorder.Children[0]).Children[0]);
            Brush idle = record.BorderBrush;
            Assert.Equal(Visibility.Collapsed, message.Visibility);

            // A key without a modifier: the invalid-key message.
            Assert.True(recorder.HandleRecordedKey(Key.A, ModifierKeys.None));
            Drain(window);
            AssertProblem(message, record, window, UiText.Get("Hotkey.ModifierRequired"));
            SaveAlignmentEvidence(window, "shortcut-invalid-key", BoundsIn(recorder, window));

            // A valid chord clears both, and the outline is the button style's again.
            recorder.Accept(new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x51));
            Drain(window);
            Assert.Equal(Visibility.Collapsed, message.Visibility);
            Assert.Equal(DependencyProperty.UnsetValue, record.ReadLocalValue(Control.BorderBrushProperty));
            Assert.Same(idle, record.BorderBrush);

            // A chord Windows does not give MyCapture: the conflict message, the same way.
            HotkeyRecorder.CanRegister = _ => false;
            recorder.Accept(new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x57));
            Drain(window);
            AssertProblem(message, record, window, UiText.Get("Hotkey.Unavailable"));
            SaveAlignmentEvidence(window, "shortcut-conflict", BoundsIn(recorder, window));
        }
        finally
        {
            HotkeyRecorder.CanRegister = canRegister;
            window.Close();
            application.Shutdown();
        }
    }

    private static void AssertProblem(TextBlock message, Button record, Window window, string text)
    {
        Assert.Equal(Visibility.Visible, message.Visibility);
        Assert.Equal(text, KoreanWordWrap.Strip(message.Text));
        Assert.Equal(FontWeights.Normal, message.FontWeight);
        AssertSameColour(window.FindResource("State.Danger"), message.Foreground);
        AssertSameColour(window.FindResource("State.Danger"), record.BorderBrush);
    }

    // The header's ring is the shared button's inset ring (2 DIP, toggled by opacity), not the
    // 1 px outline the category tabs keep.
    private static void VerifySectionHeaderFocusRing(SettingsWindow window, TabControl tabs, string locale)
    {
        var expander = Assert.IsType<Expander>(window.FindName("AdditionalShortcutsExpander"));
        DependencyObject? node = expander;
        while (node is not null and not TabItem) node = LogicalTreeHelper.GetParent(node);
        tabs.SelectedItem = Assert.IsType<TabItem>(node);
        Drain(window);
        var toggle = Assert.IsType<ToggleButton>(expander.Template.FindName("HeaderToggle", expander));
        var ring = Assert.IsType<Border>(toggle.Template.FindName("FocusRing", toggle));
        Button shared = Descendants(window).OfType<HotkeyRecorder>().First(recorder => recorder.IsVisible)
            .Children.OfType<StackPanel>().First().Children.OfType<Button>().First();
        var sharedRing = Assert.IsType<Border>(shared.Template.FindName("FocusRing", shared));

        Assert.Equal(sharedRing.Margin, ring.Margin);
        Assert.Equal((Thickness)window.FindResource("Focus.RingThickness"), ring.BorderThickness);
        Assert.Equal(sharedRing.BorderThickness, ring.BorderThickness);
        Assert.Equal(sharedRing.CornerRadius, ring.CornerRadius);
        AssertSameColour(sharedRing.BorderBrush, ring.BorderBrush);
        Assert.False(ring.IsHitTestVisible);
        Assert.Equal(0, ring.Opacity);
        Setter shown = toggle.Template.Triggers.OfType<Trigger>()
            .Single(trigger => trigger.Property == UIElement.IsKeyboardFocusedProperty)
            .Setters.OfType<Setter>().Single(setter => setter.TargetName == "FocusRing");
        Assert.Equal(UIElement.OpacityProperty, shown.Property);
        Assert.Equal(1d, Convert.ToDouble(shown.Value, System.Globalization.CultureInfo.InvariantCulture));

        // An offscreen window does not always take keyboard focus; when it does, the ring shows.
        if (toggle.Focus() && toggle.IsKeyboardFocused)
        {
            Drain(window);
            Assert.Equal(1, ring.Opacity);
            SaveAlignmentEvidence(window, $"section-header-focus-{locale}", BoundsIn(toggle, window));
        }
    }

    private static void VerifyAboutSections(SettingsWindow window, TabControl tabs, string locale)
    {
        tabs.SelectedItem = window.FindName("UpdateTab");
        Drain(window);
        var guide = Assert.IsType<StackPanel>(window.FindName("AboutGuideSection"));
        var feedback = Assert.IsType<StackPanel>(window.FindName("AboutFeedbackSection"));
        var privacy = Assert.IsType<StackPanel>(window.FindName("AboutPrivacySection"));
        var sections = Assert.IsAssignableFrom<Panel>(guide.Parent);

        // The guide is a section of its own, "Help", right above feedback.
        Assert.Equal(sections.Children.IndexOf(guide) + 1, sections.Children.IndexOf(feedback));
        TextBlock heading = Assert.IsType<TextBlock>(guide.Children[0]);
        Assert.Same(window.FindResource("About.Heading"), heading.Style);
        Assert.Equal(UiText.Get("About_HelpHeading"), KoreanWordWrap.Strip(heading.Text));
        Assert.Same(window.FindName("FirstUseGuideButton"), guide.Children[1]);

        // Privacy statements look like the tab's other descriptions.
        TextBlock description = guide.Children.OfType<TextBlock>().Last();
        Assert.Same(window.FindResource("Settings.Description"), description.Style);
        TextBlock[] statements = privacy.Children.OfType<TextBlock>().Skip(1).Where(text => text.IsVisible).ToArray();
        Assert.True(statements.Length >= 3, $"{statements.Length} privacy statements shown");
        foreach (TextBlock statement in statements)
        {
            string name = statement.Name;
            Assert.Equal(description.FontSize, statement.FontSize);
            Assert.True(description.FontWeight == statement.FontWeight, $"{name}: {statement.FontWeight} beside {description.FontWeight}");
            Assert.Equal(description.FontFamily, statement.FontFamily);
            AssertSameColour(description.Foreground, statement.Foreground);
        }
        SaveAlignmentEvidence(window, $"about-tab-{locale}", Rect.Union(BoundsIn(guide, window), BoundsIn(privacy, window)));
    }

    private static void VerifySearchNoMatch(SettingsWindow window, TabControl tabs, string locale)
    {
        const string query = "zzqx-no-setting";
        window.FilterSettings(query);
        Drain(window);
        Assert.Null(tabs.SelectedItem);
        var centred = Assert.IsType<StackPanel>(tabs.Template.FindName("SettingsSearchNoMatch", tabs));
        Assert.True(centred.IsVisible);
        TextBlock[] lines = centred.Children.OfType<TextBlock>().ToArray();
        Assert.Equal(2, lines.Length);
        Assert.Equal(UiText.Get("Settings_SearchNoMatch"), KoreanWordWrap.Strip(lines[0].Text));
        // The query has its own line: nothing in the sentence depends on how it ends.
        Assert.Equal(UiText.Format("Settings_SearchNoMatchQuery", query), lines[1].Text);
        Assert.DoesNotContain("{0}", UiText.Get("Settings_SearchNoMatch"), StringComparison.Ordinal);

        // In the middle of the content card, with the search column's note kept. The card keeps its
        // page layer and content host as the palette check walks them.
        var host = Assert.IsType<ContentPresenter>(tabs.Template.FindName("PART_SelectedContentHost", tabs));
        var card = Assert.IsType<Border>(VisualTreeHelper.GetParent(Assert.IsType<AdornerDecorator>(VisualTreeHelper.GetParent(host))));
        Rect bounds = BoundsIn(centred, window), cardBounds = BoundsIn(card, window);
        Assert.True(cardBounds.Contains(bounds), $"no-match state {bounds} inside the card {cardBounds}");
        Assert.InRange(Math.Abs(bounds.Left + bounds.Width / 2 - (cardBounds.Left + cardBounds.Width / 2)), 0, 1);
        Assert.InRange(Math.Abs(VerticalCentre(bounds) - VerticalCentre(cardBounds)), 0, 1);
        Assert.True(Assert.IsType<TextBlock>(tabs.Template.FindName("SettingsSearchEmpty", tabs)).IsVisible);
        SaveAlignmentEvidence(window, $"search-no-match-{locale}", new Rect(0, 0, window.ActualWidth, window.ActualHeight));

        window.FilterSettings(string.Empty);
        Drain(window);
        Assert.NotNull(tabs.SelectedItem);
        Assert.False(centred.IsVisible);
        Assert.Equal(string.Empty, lines[1].Text);
    }

    private static void AssertSameColour(object expected, Brush actual)
    {
        var brush = Assert.IsAssignableFrom<Brush>(expected);
        if (brush is SolidColorBrush solid && actual is SolidColorBrush other) Assert.Equal(solid.Color, other.Color);
        else Assert.Same(brush, actual);
    }
}
