using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MyCapture.App.Gallery;
using MyCapture.App.Settings;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using Xunit;
using static MyCapture.App.Tests.TextBoxCaptureGuardTests;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    [Fact]
    public Task TextBoxCaptureGuard_SettingsSearchReleasesBeforeTheNextClick() =>
        RunIsolated(nameof(TextBoxCaptureGuard_SettingsSearchReleasesBeforeTheNextClick),
            VerifyTextBoxCaptureGuardSettingsSearch);

    [Fact]
    public Task TextBoxCaptureGuard_LibraryRenameReleasesBeforeSearchClick() =>
        RunIsolated(nameof(TextBoxCaptureGuard_LibraryRenameReleasesBeforeSearchClick),
            VerifyTextBoxCaptureGuardLibraryRename);

    private static void VerifyTextBoxCaptureGuardSettingsSearch()
    {
        using var language = UiText.UseLanguage("ko-KR");
        Application application = CreateThemedApplication();
        SettingsWindow window = CreateSettingsUxWindow(new AppSettings()).Window;
        try
        {
            window.Show();
            Drain(window);
            var tabs = Assert.IsType<TabControl>(window.FindName("CategoryTabs"));
            var search = Assert.IsType<TextBox>(tabs.Template.FindName("SettingsSearchBox", tabs));
            search.Text = "단축키";
            Assert.True(window.HandleSearchShortcut(Key.F, ModifierKeys.Control));
            Drain(window);
            using var mouse = new QueuedMouse(window, search);
            Assert.NotEmpty(search.Text);
            Assert.Equal(search.Text.Length, search.SelectionLength);
            // Eight pixels past the query's final character, at line center, inside PART_ContentHost.
            Point point = PastEndPoint(search);
            mouse.Click(search, point);
            Assert.Equal(0, search.SelectionLength);
            Assert.False(mouse.ViewValidAtRelease);
            AssertReleasedAndFocused(mouse, search);

            // The content card's margin is a painted Border with no click action.
            var content = Assert.IsType<ContentPresenter>(tabs.Template.FindName("PART_SelectedContentHost", tabs));
            var target = Assert.IsType<Border>(VisualTreeHelper.GetParent(VisualTreeHelper.GetParent(content)));
            AssertNextClickReaches(mouse, window, search, target, new Point(8, 8));
        }
        finally { window.CloseForExit(); application.Shutdown(); }
    }

    private static void VerifyTextBoxCaptureGuardLibraryRename()
    {
        Application application = CreateGalleryTestApplication();
        SynchronizationContext? previous = SynchronizationContext.Current;
        using var language = UiText.UseLanguage("en-US");
        ThemeService.Apply(AppTheme.Midnight);
        LibraryUxFixture fixture = CreateLibraryUxFixture("mc-textbox-capture-guard-");
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(application.Dispatcher));
        GalleryWindow window = fixture.Window;
        try
        {
            window.Show();
            SettleGalleryLayout(window);
            var rows = (ListBox)window.FindName("RowsList");
            GalleryItemViewModel tile = fixture.ViewModel.FindTile(fixture.Records[0].Id)!;
            ListBoxItem Item() => Descendants(rows).OfType<ListBoxItem>().Single(item => item.DataContext == tile);
            TextBox Input() => Descendants(Item()).OfType<TextBox>().Single(input => input.Name == "TileRenameInput");
            KeyBinding f2 = window.InputBindings.OfType<KeyBinding>().Single(binding => binding.Key == Key.F2);
            fixture.ViewModel.Select(tile.Id);
            SettleGalleryLayout(window);
            Item().Focus();
            f2.Command.Execute(null);
            PumpLibraryUntil(window, () => tile.IsRenaming && Input().IsKeyboardFocused, "inline title focused");
            TextBox input = Input();
            using var mouse = new QueuedMouse(window, input);
            Assert.NotEmpty(input.Text);
            Assert.Equal(input.Text.Length, input.SelectionLength);
            // Eight pixels past the title's final character, at line center, inside PART_ContentHost.
            Point point = PastEndPoint(input);
            mouse.Click(input, point);
            Assert.Equal(0, input.SelectionLength);
            Assert.False(mouse.ViewValidAtRelease);
            AssertReleasedAndFocused(mouse, input);
            Assert.True(tile.IsRenaming);

            input.Text = "Saved on blur";
            var search = (TextBox)window.FindName("SearchBox");
            // Stay in the search text host, away from any search or clear icon.
            AssertNextClickReaches(mouse, window, input, search, PastEndPoint(search));
            Assert.Same(search, Keyboard.FocusedElement);
            PumpLibraryUntil(window, () => !tile.IsRenaming, "blur saved");
            Assert.Equal("Saved on blur", fixture.Records[0].Title);
        }
        finally
        {
            window.CloseForExit();
            SynchronizationContext.SetSynchronizationContext(previous);
            application.Shutdown();
        }
    }
}
