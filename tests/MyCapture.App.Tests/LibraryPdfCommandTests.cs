using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using MyCapture.App.Gallery;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Queue;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class LibraryPdfCommandTests
{
    [Fact]
    public Task ButtonMoreAndCardMenuShareTargetsAndImageAvailability() =>
        new SettingsThemePreviewTests().LibraryVideo_PdfEntriesShareTargetsAndImageAvailability();

    [Fact]
    public Task PdfKeepsAccessibleNameWhenTheSelectionBarCompacts() =>
        new SettingsThemePreviewTests().LibraryVideo_PdfCompactsWithSelectionCommands();
}

public sealed partial class SettingsThemePreviewTests
{
    [Fact]
    public Task LibraryVideo_PdfEntriesShareTargetsAndImageAvailability() =>
        RunIsolated(nameof(LibraryVideo_PdfEntriesShareTargetsAndImageAvailability), () =>
        {
            Application application = CreateLibraryMotionApplication();
            using var language = UiText.UseLanguage("ko-KR");
            LibraryUxFixture fixture = CreateLibraryUxFixture("mc-library-pdf-targets-");
            GalleryWindow window = fixture.Window;
            try
            {
                fixture.Records[2].MediaKind = CaptureMediaKind.Video;
                fixture.ViewModel.Refresh();
                var content = (FrameworkElement)window.Content;
                window.Content = null;
                content.DataContext = fixture.ViewModel;
                var host = new Border { Resources = window.Resources, Width = 1500, Height = 900, Child = content };
                LayoutLibraryVideo(host);
                Button button = (Button)window.FindName("CreatePdfSelectedButton");
                MenuItem more = (MenuItem)window.FindName("ContactSheetMenu");
                Button moreButton = (Button)window.FindName("MoreCommandsButton");
                ContextMenu moreMenu = moreButton.ContextMenu;
                moreMenu.PlacementTarget = moreButton;
                ContextMenu menu = window.TileMenu;
                MenuItem card = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, "CreatePdf"));
                // Resolve actual entry bindings without opening a popup or a save dialog.
                menu.Resources = window.Resources;
                void RefreshSelectionEntries()
                {
                    LayoutEvidence(moreMenu);
                    window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.DataBind, new Action(() => { }));
                }
                RefreshSelectionEntries();
                void Prepare(GalleryItemViewModel tile)
                {
                    menu.PlacementTarget = new Button { DataContext = tile };
                    window.PrepareTileMenu(menu, tile);
                    LayoutEvidence(menu);
                    RefreshSelectionEntries();
                    Assert.Same(tile, card.CommandParameter);
                }
                GalleryItemViewModel image = fixture.ViewModel.FindTile(fixture.Records[0].Id)!;
                GalleryItemViewModel second = fixture.ViewModel.FindTile(fixture.Records[1].Id)!;
                GalleryItemViewModel video = fixture.ViewModel.FindTile(fixture.Records[2].Id)!;
                GalleryItemViewModel outside = fixture.ViewModel.FindTile(fixture.Records[3].Id)!;

                Assert.False(fixture.ViewModel.CanCreatePdfSelected);
                Assert.False(button.IsEnabled);
                Assert.False(more.IsEnabled);
                fixture.ViewModel.Select(video.Id);
                Prepare(video);
                Assert.False(card.IsEnabled);
                Assert.False(button.IsEnabled);
                Assert.False(more.IsEnabled);

                fixture.ViewModel.Select(second.Id);
                fixture.ViewModel.Select(image.Id, control: true);
                Prepare(image);
                CaptureRecord[] expected = [image.Record, second.Record];
                Assert.Equal(expected, window.ContactSheetTargets(button));
                Assert.Equal(expected, window.ContactSheetTargets(more));
                Assert.Equal(expected, window.ContactSheetTargets(card));
                Assert.True(button.IsEnabled && more.IsEnabled && card.IsEnabled);

                // A menu outside the selection must never export unrelated selected records.
                Prepare(outside);
                Assert.Equal(new[] { outside.Record }, window.ContactSheetTargets(card));
                Assert.Equal(expected, window.ContactSheetTargets(button));
                Assert.Equal(expected, window.ContactSheetTargets(more));
                Prepare(video);
                Assert.Equal(new[] { video.Record }, window.ContactSheetTargets(card));
                Assert.False(card.IsEnabled);

                // Mixed selections retain videos in the shared snapshot for the existing exclusion count and limit.
                fixture.ViewModel.Select(video.Id, control: true);
                Prepare(video);
                Assert.Equal(new[] { image.Record, second.Record, video.Record }, window.ContactSheetTargets(card));
                Assert.True(card.IsEnabled);
                Assert.True(button.IsEnabled && more.IsEnabled);
                Assert.Equal(UiText.Get("ContactSheet.Action"), AutomationProperties.GetName(button));
                Assert.Equal(UiText.Get("ContactSheet.Action"), more.Header);
                Assert.Equal(UiText.Get("ContactSheet.Action"), card.Header);
                Assert.Equal("MediaInfo", ((MenuItem)menu.Items[menu.Items.Count - 1]).Tag);
                int pdfIndex = menu.Items.IndexOf(card);
                int imageExportIndex = menu.Items.IndexOf(menu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, "ExportImage")));
                Assert.Equal(imageExportIndex + 1, pdfIndex);
                Assert.False(window.IsVisible);
                Assert.False(menu.IsOpen);
            }
            finally { window.CloseForExit(); application.Shutdown(); }
        });

    [Fact]
    public Task LibraryVideo_PdfCompactsWithSelectionCommands() =>
        RunIsolated(nameof(LibraryVideo_PdfCompactsWithSelectionCommands), () =>
        {
            Application application = CreateLibraryMotionApplication();
            using var language = UiText.UseLanguage("ko-KR");
            LibraryUxFixture fixture = CreateLibraryUxFixture("mc-library-pdf-fit-");
            GalleryWindow window = fixture.Window;
            try
            {
                var content = (FrameworkElement)window.Content;
                window.Content = null;
                content.DataContext = fixture.ViewModel;
                var host = new Border
                {
                    Resources = window.Resources, Width = 1500, Height = 900,
                    Child = content, Background = (Brush)window.FindResource("Surface.Base"),
                };
                foreach (double width in new[] { 1500d, 980d })
                {
                    host.Width = width;
                    LayoutLibraryVideo(host);
                    window.FitContentToolbar();
                    LayoutLibraryVideo(host);
                    var button = (Button)window.FindName("CreatePdfSelectedButton");
                    var text = (TextBlock)window.FindName("CreatePdfSelectedLabel");
                    var bar = (FrameworkElement)window.FindName("LibraryContentCommandBar");
                    var sort = (FrameworkElement)window.FindName("SortCommands");
                    Assert.Equal(UiText.Get("ContactSheet.Action"), AutomationProperties.GetName(button));
                    Assert.Equal(UiText.Get("ContactSheet.Action"), button.ToolTip);
                    Assert.Equal(window.ContentToolbarDensity == GalleryWindow.ToolbarDensity.Labelled
                        ? Visibility.Visible : Visibility.Collapsed, text.Visibility);
                    Assert.True(button.TranslatePoint(new Point(button.ActualWidth, 0), bar).X
                        <= sort.TranslatePoint(new Point(), bar).X + 0.5);
                    Assert.InRange(bar.ActualHeight, 43.5, 44.5);
                    if (width == 980) Assert.NotEqual(GalleryWindow.ToolbarDensity.Labelled, window.ContentToolbarDensity);
                }
                Assert.False(window.IsVisible);
            }
            finally { window.CloseForExit(); application.Shutdown(); }
        });
}
