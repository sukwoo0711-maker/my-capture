using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Searching the shortcut help lists matches from every tab in one list, tagged with their
/// category, says how many there are, offers to clear an empty search, and returns to the tab
/// that was open. Esc clears a search before it closes the window.
/// </summary>
public sealed class ShortcutHelpSearchTests
{
    [Fact]
    public void SearchListsMatchesFromEveryTab_TaggedWithTheirCategory_AndCountsThem() => WithHelp((help, view) =>
    {
        view.Tabs.SelectedIndex = 0;

        // F2 renames a folder; the capture tab is open, but the match is still shown.
        view.Search.Text = "F2";
        help.UpdateLayout();
        Assert.Equal(Visibility.Collapsed, view.Tabs.Visibility);
        Assert.Equal(Visibility.Visible, view.ResultsView.Visibility);
        Border folder = Assert.Single(view.VisibleResults());
        Assert.Equal(UiText.Get("Folder410.Rename"), Texts(folder)[0].Text);
        TextBlock category = Texts(folder)[1];
        Assert.Equal(UiText.Get("Folder410.Title"), category.Text);
        Assert.Equal(12, category.FontSize);
        Assert.Equal(new Thickness(12, 4, 12, 4), folder.Padding);
        Assert.True(folder.ActualHeight >= 36, $"A result row is {folder.ActualHeight} high.");
        Assert.Equal(Visibility.Visible, view.Count.Visibility);
        Assert.Equal(UiText.Format("Help.SearchCountOne", 1), view.Count.Text);
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(view.Count));

        // The same command on two tabs is listed twice, each under its own category.
        view.Search.Text = "Ctrl+C";
        Border[] copies = view.VisibleResults();
        Assert.Equal(2, copies.Length);
        Assert.Equal(new[] { UiText.Get("Help.Annotation"), UiText.Get("Text_33EFD44366A7") }, copies.Select(row => Texts(row)[1].Text));
        Assert.All(copies, row => Assert.Contains(FirstRunWelcomeTests.Descendants(row).OfType<WrapPanel>(),
            keys => AutomationProperties.GetName(keys) == "Ctrl+C"));
        Assert.Equal(UiText.Format("Help.SearchCount", 2), view.Count.Text);

        // A category name finds its commands too.
        view.Search.Text = UiText.Get("Help.Video");
        Assert.Equal(5, view.VisibleResults().Length);

        // Only spaces is no search: the tabs stay.
        view.Search.Text = "   ";
        Assert.Equal(Visibility.Visible, view.Tabs.Visibility);
        Assert.Equal(Visibility.Collapsed, view.ResultsView.Visibility);
        Assert.Equal(Visibility.Collapsed, view.Count.Visibility);
        Assert.Equal(0, view.Tabs.SelectedIndex);
    });

    [Fact]
    public void NoResults_SaysSo_AndClearingReturnsToTheTabThatWasOpen() => WithHelp((help, view) =>
    {
        view.Tabs.SelectedIndex = 3;
        view.Search.Text = "no such shortcut";

        Assert.Equal(Visibility.Visible, view.Empty.Visibility);
        Assert.Equal(Visibility.Collapsed, view.ResultsView.Visibility);
        Assert.Equal(Visibility.Collapsed, view.Count.Visibility);
        Assert.Equal(Visibility.Collapsed, view.Tabs.Visibility);
        TextBlock title = view.Empty.Children.OfType<TextBlock>().Single();
        Assert.Equal(UiText.Get("Help.SearchEmpty"), title.Text);
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(title));
        Button clear = view.Empty.Children.OfType<Button>().Single();
        Assert.Equal(UiText.Get("Help.SearchClear"), clear.Content);
        Assert.Equal(UiText.Get("Help.SearchClear"), AutomationProperties.GetName(clear));
        Assert.True(clear.Focusable && clear.IsTabStop);

        clear.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.Equal(string.Empty, view.Search.Text);
        Assert.Equal(Visibility.Visible, view.Tabs.Visibility);
        Assert.Equal(3, view.Tabs.SelectedIndex);
        Assert.Equal(Visibility.Collapsed, view.Empty.Visibility);
        Assert.Same(view.Search, FocusManager.GetFocusedElement(help));
    });

    [Fact]
    public void Esc_ClearsTheSearchFirst_ThenCloses() => WithHelp((help, view) =>
    {
        bool closed = false;
        help.Closed += (_, _) => closed = true;
        view.Tabs.SelectedIndex = 2;
        view.Search.Text = "Del";

        PressEscape(view.Search);
        Assert.False(closed);
        Assert.Equal(string.Empty, view.Search.Text);
        Assert.Equal(Visibility.Visible, view.Tabs.Visibility);
        Assert.Equal(2, view.Tabs.SelectedIndex);

        PressEscape(view.Search);
        Assert.True(closed);
    });

    [Fact]
    public void TabsKeepTheirRowsAndNamesWhileTheSearchIsEmpty() => WithHelp((help, view) =>
    {
        Assert.Equal(UiText.Get("Help.Search"), AutomationProperties.GetName(view.Search));
        Assert.Equal(5, view.Tabs.Items.Count);
        Assert.All(view.Tabs.Items.OfType<TabItem>(), tab => Assert.True(tab.Focusable));
        Assert.Equal(Visibility.Collapsed, view.ResultsView.Visibility);
        Assert.Empty(view.VisibleResults());
        // Searching and clearing leaves every tab row in place.
        int rows = TabRows(view.Tabs).Count();
        view.Search.Text = "Ctrl";
        view.Search.Text = string.Empty;
        Assert.Equal(rows, TabRows(view.Tabs).Count(row => row.Visibility == Visibility.Visible));

        static IEnumerable<UniformGrid> TabRows(TabControl tabs) => tabs.Items.OfType<TabItem>()
            .SelectMany(tab => ((StackPanel)((ScrollViewer)tab.Content).Content).Children.OfType<UniformGrid>());
    });

    internal sealed record HelpView(TextBox Search, TabControl Tabs, ScrollViewer ResultsView, TextBlock Count, StackPanel Empty)
    {
        internal Border[] VisibleResults() =>
            ((StackPanel)ResultsView.Content).Children.OfType<Border>().Where(row => row.Visibility == Visibility.Visible).ToArray();
    }

    internal static HelpView View(ShortcutHelpWindow help)
    {
        var root = Assert.IsType<DockPanel>(help.Content);
        return new HelpView(
            root.Children.OfType<Grid>().Single().Children.OfType<TextBox>().Single(),
            root.Children.OfType<TabControl>().Single(),
            root.Children.OfType<ScrollViewer>().Single(),
            root.Children.OfType<TextBlock>().Single(),
            root.Children.OfType<StackPanel>().Single());
    }

    private static TextBlock[] Texts(Border row) => FirstRunWelcomeTests.Descendants(row).OfType<TextBlock>().ToArray();

    private static void PressEscape(UIElement target) =>
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target), 0, Key.Escape)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        });

    private static void WithHelp(Action<ShortcutHelpWindow, HelpView> verify) => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        Func<HotkeySettings> previous = ShortcutHelpWindow.ReadHotkeys;
        ShortcutHelpWindow.ReadHotkeys = () => new HotkeySettings();
        var help = new ShortcutHelpWindow
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false,
        };
        bool closed = false;
        help.Closed += (_, _) => closed = true;
        try
        {
            help.Show();
            help.UpdateLayout();
            verify(help, View(help));
        }
        finally
        {
            ShortcutHelpWindow.ReadHotkeys = previous;
            if (!closed) help.Close();
        }
    });
}
