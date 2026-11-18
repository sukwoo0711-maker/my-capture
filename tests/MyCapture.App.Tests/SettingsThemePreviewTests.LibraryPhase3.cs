using System.Windows;
using System.Windows.Controls;
using MyCapture.App.Gallery;
using MyCapture.Core.Localization;
using MyCapture.Core.Queue;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    [Fact]
    public Task LibraryPhase3_ContextRowKeepsHeightAndSavedDetailsChoice() =>
        RunIsolated(nameof(LibraryPhase3_ContextRowKeepsHeightAndSavedDetailsChoice), () =>
        {
            Application application = CreateLibraryMotionApplication();
            using var language = UiText.UseLanguage("en-US");
            LibraryUxFixture fixture = CreateLibraryUxFixture("mc-library-context-row-");
            GalleryWindow window = fixture.Window;
            GalleryViewModel model = fixture.ViewModel;
            try
            {
                var saved = new List<bool>();
                window.Show();
                var bar = (Grid)window.FindName("LibraryContentCommandBar");
                var rows = (ListBox)window.FindName("RowsList");
                var commands = (StackPanel)window.FindName("SelectionCommands");
                var scope = (TextBlock)window.FindName("BrowseScopeLabel");
                var sort = (ComboBox)window.FindName("SortBox");
                var more = (Button)window.FindName("MoreCommandsButton");
                foreach (bool collapsed in new[] { true, false })
                {
                    window.ConfigureDetailsPanePersistence(collapsed, saved.Add);
                    model.ClearSelection();
                    sort.SelectedIndex = 2;
                    SettleGalleryLayout(window);
                    double top = rows.TranslatePoint(new Point(), window).Y;
                    foreach (int count in new[] { 0, 1, 2, 0 })
                    {
                        model.ClearSelection();
                        for (int index = 0; index < count; index++) model.Select(fixture.Records[index].Id, control: index > 0);
                        SettleGalleryLayout(window);
                        Assert.Equal(44, bar.ActualHeight, 1);
                        Assert.Equal(top, rows.TranslatePoint(new Point(), window).Y);
                        Assert.Equal(!collapsed, window.IsDetailsPaneOpen);
                        Assert.Empty(saved);
                        Assert.Equal(count > 0, commands.IsVisible);
                        Assert.Equal(count == 0, sort.IsVisible);
                        Assert.Equal(count == 0, scope.IsVisible);
                        Assert.Equal(2, sort.SelectedIndex);
                        if (count > 0)
                            Assert.Equal(new[] { "ClearSelectionButton", "FloatSelectedButton", "CopySelectedButton", "EditSelectedButton" },
                                commands.Children.OfType<Button>().Where(button => button.IsVisible).Select(button => button.Name));
                        foreach (string name in new[] { "PreviewSelectedButton", "KeepSelectedButton", "ContactSheetMenu" })
                            Assert.Contains(window.FindName(name), more.ContextMenu.Items.Cast<object>());
                    }
                    model.SearchQuery = "no matching capture";
                    SettleGalleryLayout(window);
                    Assert.Equal(!collapsed, window.IsDetailsPaneOpen);
                    Assert.Equal(44, bar.ActualHeight, 1);
                    model.SearchQuery = string.Empty;
                }

                model.ShowView(GalleryFilter.Images);
                SettleGalleryLayout(window);
                Assert.Equal(UiText.Get("LibraryUx_NavImages"), MyCapture.App.Themes.KoreanWordWrap.Strip(scope.Text));
                model.NavigateTo(CaptureFolderScope.ForFolder(fixture.Design.Id));
                SettleGalleryLayout(window);
                Assert.Equal(fixture.Design.Name, MyCapture.App.Themes.KoreanWordWrap.Strip(scope.Text));
                Assert.Equal(fixture.Design.Name, System.Windows.Automation.AutomationProperties.GetName(scope));
                Assert.True(window.IsDetailsPaneOpen);
                Assert.Empty(saved);
            }
            finally { window.CloseForExit(); application.Shutdown(); }
        });
}
