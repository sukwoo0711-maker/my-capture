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
    [Theory]
    [InlineData("ko-KR", "라이브러리")]
    [InlineData("en-US", "Library")]
    public void LibraryHelpIncludesPinAndKeepShortcuts(string language, string category) => WithHelp((help, view) =>
    {
        Assert.Equal(category, UiText.Get("Workspace.Library"));
        TabItem library = Assert.Single(view.Tabs.Items.OfType<TabItem>(), tab => Equals(tab.Header, category));
        Assert.DoesNotContain(view.Tabs.Items.OfType<TabItem>(), tab => Equals(tab.Header, UiText.Get("Folder410.Title")));
        var panel = (StackPanel)((ScrollViewer)library.Content).Content;
        UniformGrid[] rows = panel.Children.OfType<UniformGrid>().ToArray();
        (string Label, string Shortcut)[] expected =
        [
            ("Gallery.Float", "P"), ("Shortcuts.LibraryCopy", "Ctrl+C"),
            ("Shortcuts.LibraryOpen", "Enter"), ("Shortcuts.LibraryPreview", "Space"),
            ("MediaInfo.Title", "Alt+Enter"),
            ("Folder410.Rename", "F2"), ("LibraryUx_KeepToggleName", "K"), ("Folder410.MoveCaptures", "M"),
            ("Shortcuts.LibraryText", "T"), ("Shortcuts.LibraryGif", "G"), ("Folder410.Delete", "Del"),
            ("Shortcuts.ContextMenu", "Menu / Shift+F10"), ("Shortcuts.Undo", "Ctrl+Z"),
            ("Shortcuts.Search", "Ctrl+F"), ("Folder410.NewFolder", "Ctrl+Shift+N"),
            ("Shortcuts.Back", "Backspace / Alt+←"), ("Shortcuts.Forward", "Alt+→"), ("Shortcuts.Parent", "Alt+↑"), ("Shortcuts.SelectAll", "Ctrl+A"),
            ("Shortcuts.MouseNavigation", UiText.Get("Shortcuts.MouseButtons")), ("Shortcuts.LibraryDrag", UiText.Get("Shortcuts.Drag")),
        ];
        Assert.Equal(expected.Select(entry => UiText.Get(entry.Label)), rows.Select(ShortcutHelpRowAssertions.Title));
        Assert.Equal(expected.Select(entry => entry.Shortcut), rows.Select(row =>
            AutomationProperties.GetName(Assert.Single(row.Children.OfType<WrapPanel>()))));

        view.Search.Text = category;
        help.UpdateLayout();
        Border[] results = view.VisibleResults();
        Assert.Equal(23, results.Length); // 21 library commands, Open Library and video Save to Library.
        Border[] libraryResults = results.Where(row => Texts(row)[1].Text == category).ToArray();
        Assert.Equal(21, libraryResults.Length);
        Assert.Equal(expected.Select(entry => UiText.Get(entry.Label)), libraryResults.Select(row => Texts(row)[0].Text));
        Border[] otherResults = results.Where(row => Texts(row)[1].Text != category).ToArray();
        Assert.Equal(2, otherResults.Length);
        Border openLibrary = Assert.Single(otherResults, row => Texts(row)[1].Text == UiText.Get("Help.Capture"));
        Assert.Equal(UiText.Get("Text_66BA5DCDA810"), Texts(openLibrary)[0].Text);
        Assert.Equal(UiText.Get("Help.Capture"), Texts(openLibrary)[1].Text);
        Border saveVideo = Assert.Single(otherResults, row => Texts(row)[1].Text == UiText.Get("Help.Video"));
        Assert.Equal(UiText.Get("MediaExport_SaveEdits"), Texts(saveVideo)[0].Text);
        Assert.Contains(FirstRunWelcomeTests.Descendants(saveVideo).OfType<WrapPanel>(),
            keys => AutomationProperties.GetName(keys) == "Ctrl+S");
        foreach (var (label, shortcut) in expected)
        {
            Border row = Assert.Single(libraryResults, result => Texts(result)[0].Text == UiText.Get(label));
            Assert.Contains(FirstRunWelcomeTests.Descendants(row).OfType<WrapPanel>(),
                keys => AutomationProperties.GetName(keys) == shortcut);
        }
    }, language);

    [Fact]
    public void VideoHelpKeepsStartAndEndShortcutsAndIncludesSaveAndExport() => WithHelp((help, view) =>
    {
        view.Search.Text = UiText.Get("Help.Video");
        help.UpdateLayout();
        foreach (var (label, shortcut) in new[] { ("Video.TrimIn", "I"), ("Video.TrimOut", "O"),
            ("MediaExport_SaveEdits", "Ctrl+S"), ("MediaExport_ExportFormats", "Ctrl+Shift+S"),
            ("VideoFrame_Save", "Shift+S") })
        {
            Border row = Assert.Single(view.VisibleResults(), result => Texts(result)[0].Text == UiText.Get(label));
            Assert.Contains(FirstRunWelcomeTests.Descendants(row).OfType<WrapPanel>(),
                keys => AutomationProperties.GetName(keys) == shortcut);
        }
    });

    [Fact]
    public void SearchListsMatchesFromEveryTab_TaggedWithTheirCategory_AndCountsThem() => WithHelp((help, view) =>
    {
        view.Tabs.SelectedIndex = 0;

        // F2 edits annotation text, renames a library item and selects pin text across tabs.
        view.Search.Text = "F2";
        help.UpdateLayout();
        Assert.Equal(Visibility.Collapsed, view.Tabs.Visibility);
        Assert.Equal(Visibility.Visible, view.ResultsView.Visibility);
        Border[] f2Results = view.VisibleResults();
        Assert.Equal(3, f2Results.Length);
        Assert.Single(f2Results, row => Texts(row)[0].Text == UiText.Get("Shortcuts.EditText"));
        Border folder = Assert.Single(f2Results, row => Texts(row)[0].Text == UiText.Get("Folder410.Rename"));
        Assert.Equal(UiText.Get("Folder410.Rename"), Texts(folder)[0].Text);
        TextBlock category = Texts(folder)[1];
        Assert.Equal(UiText.Get("Workspace.Library"), category.Text);
        Assert.Equal(12, category.FontSize);
        Assert.Equal(new Thickness(12, 4, 12, 4), folder.Padding);
        Assert.True(folder.ActualHeight >= 36, $"A result row is {folder.ActualHeight} high.");
        Assert.Equal(Visibility.Visible, view.Count.Visibility);
        Assert.Equal(UiText.Format("Help.SearchCount", 3), view.Count.Text);
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(view.Count));
        Border selection = Assert.Single(f2Results, row => Texts(row)[0].Text == UiText.Get("Pin_SelectText"));
        Assert.Equal(UiText.Get("Text_33EFD44366A7"), Texts(selection)[1].Text);
        Assert.Contains(FirstRunWelcomeTests.Descendants(selection).OfType<WrapPanel>(),
            keys => AutomationProperties.GetName(keys) == "F2");

        view.Search.Text = UiText.Get("Folder410.Rename");
        Assert.Single(view.VisibleResults());
        Assert.Equal(UiText.Format("Help.SearchCountOne", 1), view.Count.Text);

        // Copy includes the editor, library, both pin routes and the quick-access alias.
        view.Search.Text = "Ctrl+C";
        Border[] copies = view.VisibleResults();
        Assert.Equal(5, copies.Length);
        Assert.Equal(new[] { UiText.Get("Help.Annotation"), UiText.Get("Workspace.Library"), UiText.Get("Text_33EFD44366A7"),
            UiText.Get("Text_33EFD44366A7"), UiText.Get("Help.QuickAccess") }, copies.Select(row => Texts(row)[1].Text));
        Assert.All(copies.Take(3), row => Assert.Contains(FirstRunWelcomeTests.Descendants(row).OfType<WrapPanel>(),
            keys => AutomationProperties.GetName(keys) == "Ctrl+C"));
        Assert.Equal(UiText.Get("Text_976A6C92B870"), Texts(copies[3])[0].Text);
        Assert.Contains(FirstRunWelcomeTests.Descendants(copies[3]).OfType<WrapPanel>(),
            keys => AutomationProperties.GetName(keys) == "Ctrl+C / " + UiText.Get("Text_C7011B8D345C"));
        Assert.Contains(FirstRunWelcomeTests.Descendants(copies[4]).OfType<WrapPanel>(),
            keys => AutomationProperties.GetName(keys) == "C / Ctrl+C");
        Assert.Equal(UiText.Format("Help.SearchCount", 5), view.Count.Text);

        // A category name finds its commands too.
        view.Search.Text = UiText.Get("Help.Video");
        Assert.Equal(8, view.VisibleResults().Length);

        // Only spaces is no search: the tabs stay.
        view.Search.Text = "   ";
        Assert.Equal(Visibility.Visible, view.Tabs.Visibility);
        Assert.Equal(Visibility.Collapsed, view.ResultsView.Visibility);
        Assert.Equal(Visibility.Collapsed, view.Count.Visibility);
        Assert.Equal(0, view.Tabs.SelectedIndex);
    });

    [Fact]
    public void PinSectionOffersBothOriginalTextCopyGesturesAfterCopy() => WithHelp((help, view) =>
    {
        TabItem tab = Assert.Single(view.Tabs.Items.OfType<TabItem>(), item => Equals(item.Header, UiText.Get("Text_33EFD44366A7")));
        var panel = (StackPanel)((ScrollViewer)tab.Content).Content;
        UniformGrid[] rows = panel.Children.OfType<UniformGrid>().ToArray();
        int index = Array.FindIndex(rows, row => FirstRunWelcomeTests.Descendants(row).OfType<TextBlock>()
            .Any(text => text.Text == UiText.Get("Text_976A6C92B870")));
        Assert.True(index > 0);
        Assert.Contains(FirstRunWelcomeTests.Descendants(rows[index - 1]).OfType<TextBlock>(),
            text => text.Text == UiText.Get("QuickAccess.Copy"));
        Assert.Contains(FirstRunWelcomeTests.Descendants(rows[index]).OfType<WrapPanel>(),
            keys => AutomationProperties.GetName(keys) == "Ctrl+C / " + UiText.Get("Text_C7011B8D345C"));
        Assert.Equal("pin-copy", TutorialCatalog.FindHelp("Text_33EFD44366A7", "QuickAccess.Copy")?.Id);
    });

    [Fact]
    public void PinSectionOffersTextSelectionAfterOriginalTextCopy() => WithHelp((help, view) =>
    {
        TabItem tab = Assert.Single(view.Tabs.Items.OfType<TabItem>(), item => Equals(item.Header, UiText.Get("Text_33EFD44366A7")));
        var panel = (StackPanel)((ScrollViewer)tab.Content).Content;
        UniformGrid[] rows = panel.Children.OfType<UniformGrid>().ToArray();
        int index = Array.FindIndex(rows, row => ShortcutHelpRowAssertions.Title(row) == UiText.Get("Text_976A6C92B870"));
        Assert.True(index >= 0 && index + 1 < rows.Length);
        Assert.Equal(UiText.Get("Pin_SelectText"), ShortcutHelpRowAssertions.Title(rows[index + 1]));
        Assert.Equal("F2", ShortcutHelpRowAssertions.Keys(rows[index + 1]));
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
        Assert.Equal(6, view.Tabs.Items.Count);
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

    private static void WithHelp(Action<ShortcutHelpWindow, HelpView> verify, string language = "ko-KR") => StaTestHost.Run(() =>
    {
        using var locale = UiText.UseLanguage(language);
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
