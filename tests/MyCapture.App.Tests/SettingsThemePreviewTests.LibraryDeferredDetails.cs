using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using MyCapture.App.Gallery;
using MyCapture.Core.Localization;
using MyCapture.Core.Queue;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    [Fact]
    public Task LibraryDetails_NoSelectionKeepsShellWithoutCreatingSelectedControls() =>
        RunIsolated(nameof(LibraryDetails_NoSelectionKeepsShellWithoutCreatingSelectedControls), () =>
        {
            Application application = CreateLibraryMotionApplication();
            LibraryUxFixture fixture = CreateLibraryUxFixture("mc-details-unselected-");
            try
            {
                GalleryWindow window = fixture.Window;
                var host = Assert.IsType<Border>(window.FindName("GalleryDetailsHost"));
                Assert.Null(host.Child);
                Assert.Equal(0, window.DetailsContentCreationCount);
                window.Show();
                window.SetDetailsPaneOpen(true);
                SettleGalleryLayout(window);
                Assert.Null(fixture.ViewModel.SingleSelectedTile);
                Assert.Null(host.Child);
                Assert.Null(window.FindName("GalleryDetailsScroll"));
                Assert.Equal(0, window.DetailsContentCreationCount);
                Assert.True(Assert.IsType<TextBlock>(window.FindName("GalleryDetailsHeading")).IsVisible);
                Assert.True(Assert.IsType<Button>(window.FindName("HideDetailsButton")).IsVisible);
                var panel = Assert.IsType<Border>(window.FindName("GalleryDetailsPanel"));
                Assert.Contains(Descendants(panel).OfType<TextBlock>(),
                    text => text.IsVisible && text.Text == UiText.Get("Workspace.SelectHint"));
            }
            finally { fixture.Window.CloseForExit(); application.Shutdown(); }
        });

    [Fact]
    public Task LibraryDetails_FirstSelectionCreatesOnceAndTracksImageVideoClearDeleteAndReopen_Korean() =>
        VerifyLibraryDetailsSelectionLifecycle(nameof(LibraryDetails_FirstSelectionCreatesOnceAndTracksImageVideoClearDeleteAndReopen_Korean), "ko-KR");

    [Fact]
    public Task LibraryDetails_FirstSelectionCreatesOnceAndTracksImageVideoClearDeleteAndReopen_English() =>
        VerifyLibraryDetailsSelectionLifecycle(nameof(LibraryDetails_FirstSelectionCreatesOnceAndTracksImageVideoClearDeleteAndReopen_English), "en-US");

    private Task VerifyLibraryDetailsSelectionLifecycle(string testName, string language) =>
        RunIsolated(testName, () =>
        {
            using var culture = UiText.UseLanguage(language);
            Application application = CreateLibraryMotionApplication();
            LibraryUxFixture fixture = CreateLibraryUxFixture("mc-details-reuse-");
            try
            {
                GalleryWindow window = fixture.Window;
                GalleryViewModel vm = fixture.ViewModel;
                CaptureRecord image = fixture.Records[0], video = fixture.Records[2];
                video.MediaKind = CaptureMediaKind.Video;
                image.SourcePageUrl = "https://example.com/synthetic/details";
                vm.Refresh();
                window.Show();
                window.SetDetailsPaneOpen(true);
                SettleGalleryLayout(window);
                Assert.Equal(0, window.DetailsContentCreationCount);
                vm.Select(image.Id);
                SettleGalleryLayout(window);
                var content = Assert.IsType<Grid>(window.FindName("GalleryDetailsContent"));
                var picture = Assert.IsType<Image>(window.FindName("SelectedDetailThumbnail"));
                var edit = Assert.IsType<Button>(window.FindName("DetailsEditButton"));
                var pin = Assert.IsType<Button>(window.FindName("DetailsFloatButton"));
                var copy = Assert.IsType<Button>(window.FindName("DetailsCopyButton"));
                var scroll = Assert.IsType<ScrollViewer>(window.FindName("GalleryDetailsScroll"));
                var toggle = Assert.IsType<ToggleButton>(window.FindName("SourcePageToggle"));
                Assert.Same(window.FindName("SourcePageValue"), GalleryTextClamp.GetToggleTarget(toggle));
                Assert.Same(window.FindName("SourcePathValue"), GalleryTextClamp.GetToggleTarget(
                    Assert.IsType<ToggleButton>(window.FindName("SourcePathToggle"))));
                Assert.True(edit.Focusable && edit.IsTabStop && pin.Focusable && pin.IsTabStop && copy.Focusable && copy.IsTabStop);
                Assert.False(scroll.Focusable);
                Assert.True(pin.IsVisible && copy.IsVisible);
                AssertSelection(image.Id);

                vm.Select(video.Id);
                SettleGalleryLayout(window);
                AssertSelection(video.Id);
                Assert.False(pin.IsVisible || copy.IsVisible);
                vm.ClearSelection();
                SettleGalleryLayout(window);
                Assert.Null(content.DataContext);
                Assert.Equal(Visibility.Collapsed, content.Visibility);
                vm.Select(image.Id);
                vm.Select(video.Id, control: true);
                SettleGalleryLayout(window);
                Assert.Null(vm.SingleSelectedTile);
                Assert.Equal(Visibility.Collapsed, content.Visibility);
                vm.Select(image.Id);
                SettleGalleryLayout(window);
                // This fixture has no removal callback: remove only the in-memory record.
                // Existing LibraryFeel tests exercise actual recoverable deletion and commands.
                Assert.Null(fixture.Queue.PrepareRemoval);
                Assert.True(fixture.Queue.Remove(image.Id));
                vm.Refresh();
                SettleGalleryLayout(window);
                Assert.Null(vm.SingleSelectedTile);
                Assert.Null(content.DataContext);
                Assert.Equal(Visibility.Collapsed, content.Visibility);
                vm.Select(video.Id);
                SettleGalleryLayout(window);
                AssertSelection(video.Id);
                window.Close();
                Assert.False(window.IsVisible);
                window.ShowGallery();
                SettleGalleryLayout(window);
                AssertSelection(video.Id);
                Assert.Same(scroll, window.FindName("GalleryDetailsScroll"));
                Assert.Equal(1, window.DetailsContentCreationCount);

                void AssertSelection(Guid id)
                {
                    GalleryItemViewModel tile = Assert.IsType<GalleryItemViewModel>(vm.SingleSelectedTile);
                    Assert.Equal(id, tile.Id);
                    Assert.Same(tile, content.DataContext);
                    Assert.Same(tile, edit.DataContext);
                    Assert.Same(content, Assert.IsType<Border>(window.FindName("GalleryDetailsHost")).Child);
                    Assert.True(content.IsVisible);
                    Assert.Equal(tile.ActionLabel, edit.Content);
                    Assert.Equal(tile.AccessibleName, AutomationProperties.GetName(picture));
                    Assert.Equal(1, window.DetailsContentCreationCount);
                }
            }
            finally { fixture.Window.CloseForExit(); application.Shutdown(); }
        });

    [Fact]
    public Task LibraryDetails_PreselectedConstructorCreatesVisibleContentImmediately() =>
        RunIsolated(nameof(LibraryDetails_PreselectedConstructorCreatesVisibleContentImmediately), () =>
        {
            Application application = CreateLibraryMotionApplication();
            LibraryUxFixture fixture = CreateLibraryUxFixture("mc-details-preselected-", vm =>
            {
                vm.SelectAllVisible();
                vm.Select(vm.SelectedTiles.First().Id);
            });
            try
            {
                Assert.NotNull(fixture.ViewModel.SingleSelectedTile);
                Assert.Equal(1, fixture.Window.DetailsContentCreationCount);
                var content = Assert.IsType<Grid>(fixture.Window.FindName("GalleryDetailsContent"));
                fixture.Window.Show();
                fixture.Window.SetDetailsPaneOpen(true);
                SettleGalleryLayout(fixture.Window);
                Assert.Same(fixture.ViewModel.SingleSelectedTile, content.DataContext);
                Assert.True(content.IsVisible);
                Assert.Equal(1, fixture.Window.DetailsContentCreationCount);
            }
            finally { fixture.Window.CloseForExit(); application.Shutdown(); }
        });
}
