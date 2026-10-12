using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Gallery;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// A Windows contrast theme draws Surface.Hover in the Window colour, so library hover must show
/// as a Highlight edge (Border.Hover). Renders the real GalleryWindow BAML and compares each
/// target before and after its own IsMouseOver trigger; in an app palette the edge stays clear.
/// </summary>
public sealed partial class SettingsThemePreviewTests
{
    [Fact]
    public Task LibraryHover_WindowsContrastShowsAHighlightEdge() =>
        RunIsolated(nameof(LibraryHover_WindowsContrastShowsAHighlightEdge), () =>
        {
            var application = CreateLibraryMotionApplication();
            bool? previous = FluidMotion.AnimationsEnabledOverrideForTest;
            FluidMotion.AnimationsEnabledOverrideForTest = false;
            try
            {
                using var scope = UiText.UseLanguage("ko-KR");
                ThemeService.SystemHighContrastOverrideForTest = true;
                ThemeService.SystemColorsOverrideForTest = role => SystemHighContrastThemeTests.NightSky[role];
                VerifyContrastHover(SystemHighContrastThemeTests.NightSky[SystemColorRole.Highlight]);

                // An app palette keeps the edge transparent: hover stays the wash alone.
                ThemeService.SystemHighContrastOverrideForTest = false;
                ThemeService.SystemColorsOverrideForTest = null;
                ThemeService.Apply(AppTheme.Glass);
                Assert.Equal(0, ((SolidColorBrush)application.FindResource("Border.Hover")).Color.A);
            }
            finally
            {
                ThemeService.SystemHighContrastOverrideForTest = null;
                ThemeService.SystemColorsOverrideForTest = null;
                FluidMotion.AnimationsEnabledOverrideForTest = previous;
                ThemeService.Apply(AppTheme.Midnight);
                application.Shutdown();
            }
        });

    private static void VerifyContrastHover(Color highlight)
    {
        ThemeService.Apply(AppTheme.Glass);
        LibraryUxFixture f = CreateLibraryUxFixture("mc-library-contrast-hover-");
        GalleryWindow window = f.Window;
        try
        {
            window.Show();
            var rowsList = (ListBox)window.FindName("RowsList");
            PumpLibraryUntil(window, () => FolderCardButtons(rowsList).Length == 2
                && Descendants(rowsList).OfType<ListBoxItem>().Any(item => item.DataContext is GalleryItemViewModel),
                "contrast hover: cards");
            var dates = (Expander)window.FindName("DateSection");
            dates.IsExpanded = true;
            SettleGalleryLayout(window);

            ListBoxItem card = Descendants(rowsList).OfType<ListBoxItem>()
                .First(item => item.DataContext is GalleryItemViewModel tile && tile.Id == f.Records[0].Id);
            Assert.False(((GalleryItemViewModel)card.DataContext).IsSelected);
            Button folderCard = FolderCardButtons(rowsList).Single(button => ((GalleryFolderCardViewModel)button.DataContext).FolderId == f.Design.Id);
            RadioButton view = Descendants(window).OfType<RadioButton>().Single(button => (string)button.Tag == "Images");
            Assert.False(view.IsChecked);
            TreeViewItem folderRow = Descendants((TreeView)window.FindName("FolderTree")).OfType<TreeViewItem>()
                .First(item => item.DataContext is GalleryFolderNode node && node.FolderId == f.Empty.Id);
            Assert.False(folderRow.IsSelected);
            var dateHeader = (ToggleButton)dates.Template.FindName("HeaderSite", dates);
            var dateList = (ListBox)window.FindName("DateList");
            var dateRow = (ListBoxItem)dateList.ItemContainerGenerator.ContainerFromIndex(0);
            Assert.False(dateRow.IsSelected);

            foreach ((string name, Control target) in new (string, Control)[]
                     {
                         ("capture card", card), ("folder card", folderCard), ("view row", view),
                         ("folder row", folderRow), ("date header", dateHeader), ("date row", dateRow),
                     })
            {
                byte[] before = RenderPixels(window, target, out int width, out int height);
                SimulateTemplateTrigger(target, "IsMouseOver");
                SettleGalleryLayout(window);
                byte[] after = RenderPixels(window, target, out int afterWidth, out int afterHeight);
                Assert.Equal((width, height), (afterWidth, afterHeight));
                // Pixels the hover moved toward Highlight, antialiased curves included; a one-DIP
                // edge must cover at least half of the target's perimeter.
                int towardHighlight = 0;
                for (int offset = 0; offset < after.Length; offset += 4)
                {
                    if (Distance(after, offset, highlight) + 24 < Distance(before, offset, highlight)) towardHighlight++;
                }
                Assert.True(towardHighlight >= width + height,
                    $"{name}: hover drew {towardHighlight} Highlight-edge pixels on a {width}x{height} target");
            }
        }
        finally
        {
            window.CloseForExit();
        }
    }

    private static int Distance(byte[] bgra, int offset, Color color) =>
        Math.Abs(bgra[offset] - color.B) + Math.Abs(bgra[offset + 1] - color.G) + Math.Abs(bgra[offset + 2] - color.R);

    /// <summary>Renders the window's client area and crops <paramref name="element"/>'s own bounds.</summary>
    private static byte[] RenderPixels(Window window, FrameworkElement element, out int width, out int height)
    {
        var clientRoot = (FrameworkElement)VisualTreeHelper.GetChild(window, 0);
        int clientWidth = (int)Math.Ceiling(clientRoot.ActualWidth), clientHeight = (int)Math.Ceiling(clientRoot.ActualHeight);
        var content = new DrawingVisual();
        using (DrawingContext context = content.RenderOpen())
        {
            context.DrawRectangle(Brushes.Black, null, new Rect(0, 0, clientWidth, clientHeight));
            context.DrawRectangle(new VisualBrush(clientRoot)
            {
                ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, clientWidth, clientHeight), Stretch = Stretch.Fill,
            }, null, new Rect(0, 0, clientWidth, clientHeight));
        }
        var bitmap = new RenderTargetBitmap(clientWidth, clientHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        Rect bounds = element.TransformToAncestor(clientRoot).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        var crop = new Int32Rect(Math.Max(0, (int)Math.Floor(bounds.X)), Math.Max(0, (int)Math.Floor(bounds.Y)), 0, 0);
        crop.Width = Math.Min(clientWidth - crop.X, (int)Math.Ceiling(bounds.Right) - crop.X);
        crop.Height = Math.Min(clientHeight - crop.Y, (int)Math.Ceiling(bounds.Bottom) - crop.Y);
        Assert.True(crop.Width > 0 && crop.Height > 0, "The hover target must lie inside the rendered client area.");
        width = crop.Width;
        height = crop.Height;
        var converted = new FormatConvertedBitmap(new CroppedBitmap(bitmap, crop), PixelFormats.Bgra32, null, 0);
        byte[] pixels = new byte[width * height * 4];
        converted.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }
}
