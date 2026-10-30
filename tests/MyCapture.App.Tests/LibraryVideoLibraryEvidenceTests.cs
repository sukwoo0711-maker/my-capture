using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Diagnostics;
using MyCapture.App.Gallery;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class LibraryVideoLibraryEvidenceTests
{
    [Fact]
    public Task LibraryCardSelectionAndMenuRenderWithoutShowingWindows() =>
        new SettingsThemePreviewTests().LibraryVideo_RendersLibraryAndMenuWithoutShowingAWindow();
}

public sealed partial class SettingsThemePreviewTests
{
    [Fact]
    public Task LibraryVideo_RendersLibraryAndMenuWithoutShowingAWindow() =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MYCAPTURE_LIBRARY_VIDEO_EVIDENCE"))
            ? Task.CompletedTask
            : RunIsolated(nameof(LibraryVideo_RendersLibraryAndMenuWithoutShowingAWindow), () =>
        {
            Application application = CreateLibraryMotionApplication();
            using var language = UiText.UseLanguage("ko-KR");
            FluidMotion.AnimationsEnabledOverrideForTest = false;
            LibraryUxFixture fixture = CreateLibraryUxFixture("mc-library-video-evidence-");
            GalleryWindow window = fixture.Window;
            try
            {
                fixture.Records[0].Title = "자료 검토";
                fixture.Records[0].HasAnnotations = true;
                fixture.Records[0].SourceAppName = "Microsoft Edge";
                fixture.Records[0].SourceWindowTitle = "라이브러리 기능 검토";
                fixture.ViewModel.Refresh();
                fixture.ViewModel.Select(fixture.Records[0].Id);
                fixture.ViewModel.Select(fixture.Records[1].Id, control: true);
                GalleryItemViewModel tile = fixture.ViewModel.FindTile(fixture.Records[0].Id)!;

                var content = (FrameworkElement)window.Content;
                window.Content = null;
                content.DataContext = fixture.ViewModel;
                var library = new Border
                {
                    Resources = window.Resources, Width = 1440, Height = 900,
                    Background = (Brush)window.FindResource("Surface.Base"), Child = content,
                };
                LayoutLibraryVideo(library);
                window.FitContentToolbar();
                LayoutLibraryVideo(library);
                SaveLibraryVideo(library, "library-selection");

                var template = (DataTemplate)window.FindResource("Gallery.TileTemplate");
                var card = (FrameworkElement)template.LoadContent();
                card.DataContext = tile;
                var frame = new Border
                {
                    Resources = window.Resources, Padding = new Thickness(16), Width = 340,
                    Background = (Brush)window.FindResource("Surface.Base"), Child = card,
                };
                LayoutEvidence(frame);
                SaveLibraryVideo(frame, "library-card");

                ContextMenu menu = window.TileMenu;
                menu.Resources = window.Resources;
                menu.PlacementTarget = new Button { DataContext = tile };
                window.PrepareTileMenu(menu, tile);
                menu.Width = 300;
                LayoutEvidence(menu);
                SaveLibraryVideo(menu, "library-menu");
                Assert.False(menu.IsOpen);
                Assert.False(window.IsVisible);
                Assert.True(menu.ActualHeight > 200);
            }
            finally
            {
                FluidMotion.AnimationsEnabledOverrideForTest = null;
                window.CloseForExit();
                application.Shutdown();
            }
        });

    private static void LayoutLibraryVideo(FrameworkElement element)
    {
        element.Measure(new Size(element.Width, element.Height));
        element.Arrange(new Rect(0, 0, element.Width, element.Height));
        element.UpdateLayout();
    }

    private static void SaveLibraryVideo(FrameworkElement element, string name)
    {
        string root = DiagnosticOutputPaths.Create(Environment.GetEnvironmentVariable("MYCAPTURE_LIBRARY_VIDEO_EVIDENCE")!);
        string phase = Environment.GetEnvironmentVariable("MYCAPTURE_LIBRARY_VIDEO_PHASE") ?? "after";
        Assert.Contains(phase, new[] { "before", "after" });
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * 1.5),
            (int)Math.Ceiling(element.ActualHeight * 1.5), 144, 144, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new FileStream(DiagnosticOutputPaths.Child(root, $"{phase}-{name}.png"),
            phase == "before" ? FileMode.CreateNew : FileMode.Create);
        encoder.Save(output);
    }
}
