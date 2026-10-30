using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Gallery;
using MyCapture.App.Themes;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

public sealed class GalleryImagePreviewTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(400, 260, 1)]
    [InlineData(400, 260, 1.25)]
    [InlineData(400, 260, 1.5)]
    [InlineData(400, 260, 1.75)]
    [InlineData(401, 261, 1)]
    [InlineData(401, 261, 1.25)]
    [InlineData(401, 261, 1.5)]
    [InlineData(401, 261, 1.75)]
    public void StyledWindowMapsTheLaidOutImageToPhysicalPixels(int width, int height, double pixelsPerDip) => StaTestHost.Run(() =>
    {
        var bitmap = BitmapSource.Create(width, height, 144, 144, PixelFormats.Bgr32, null,
            new byte[width * height * 4], width * 4);
        var window = ShowStyledPreview(bitmap, pixelsPerDip);
        try
        {
            var viewport = Assert.IsType<Canvas>(window.Content);
            var image = Assert.IsType<Image>(viewport.Children[0]);
            Assert.True(window.UseLayoutRounding);
            Assert.True(viewport.UseLayoutRounding);
            Assert.Same(window.Resources["Window.Standard"], window.Style);
            Assert.Equal(pixelsPerDip, VisualTreeHelper.GetDpi(image).PixelsPerDip);
            Assert.True(image.IsMeasureValid && image.IsArrangeValid);
            Assert.True(window.HandleZoomKey(Key.D1, ModifierKeys.Control));
            Settle(window);

            // Include the actual layout result and the visual transform, not the requested
            // Width/Height or the viewport model's scale alone.
            Rect finalBounds = image.TransformToAncestor(viewport).TransformBounds(new Rect(image.RenderSize));
            output.WriteLine($"{width}x{height} at {pixelsPerDip}: RenderSize={image.RenderSize}, physical={finalBounds.Width * pixelsPerDip:R}x{finalBounds.Height * pixelsPerDip:R}");
            Assert.Equal(width, finalBounds.Width * pixelsPerDip, 7);
            Assert.Equal(height, finalBounds.Height * pixelsPerDip, 7);
            Assert.Equal(100, window.ViewportModel.Percentage, 8);
            Assert.EndsWith("100%", window.Title);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(400, 260)]
    [InlineData(401, 261)]
    public void StyledWindowRendersEveryCheckerboardPixelOneToOneAt96Dpi(int width, int height) => StaTestHost.Run(() =>
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            byte value = (byte)(((x + y) % 2) * 255);
            int offset = (y * width + x) * 4;
            pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = value;
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * 4);
        var window = ShowStyledPreview(bitmap, 1);
        try
        {
            Assert.True(window.HandleZoomKey(Key.D1, ModifierKeys.Control));
            Settle(window);
            var viewport = Assert.IsType<Canvas>(window.Content);
            var image = Assert.IsType<Image>(viewport.Children[0]);
            Rect bounds = image.TransformToAncestor(window).TransformBounds(new Rect(image.RenderSize));
            Assert.Equal(width, bounds.Width, 7);
            Assert.Equal(height, bounds.Height, 7);
            Assert.Equal(Math.Round(bounds.X), bounds.X, 7);
            Assert.Equal(Math.Round(bounds.Y), bounds.Y, 7);

            // Render while still attached to the real styled window. Detaching the content
            // would lose the inherited layout-rounding conditions this test protects.
            int renderWidth = (int)Math.Ceiling(window.RenderSize.Width);
            int renderHeight = (int)Math.Ceiling(window.RenderSize.Height);
            var rendered = new RenderTargetBitmap(renderWidth, renderHeight, 96, 96, PixelFormats.Pbgra32);
            rendered.Render(window);
            byte[] actual = new byte[width * height * 4];
            rendered.CopyPixels(new Int32Rect((int)bounds.X, (int)bounds.Y, width, height), actual, width * 4, 0);
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int offset = (y * width + x) * 4;
                byte expected = pixels[offset];
                Assert.True(actual[offset] == expected && actual[offset + 1] == expected
                    && actual[offset + 2] == expected && actual[offset + 3] == 255,
                    $"Checkerboard pixel ({x}, {y}): expected {expected}, actual BGRA {actual[offset]},{actual[offset + 1]},{actual[offset + 2]},{actual[offset + 3]}.");
            }
        }
        finally { window.Close(); }
    });

    private static GalleryImagePreviewWindow ShowStyledPreview(BitmapSource bitmap, double pixelsPerDip)
    {
        var window = new GalleryImagePreviewWindow(bitmap, "Preview", "Capture")
        {
            Resources = SystemHighContrastThemeTests.LoadTheme(),
            Width = 720, Height = 540,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false,
        };
        StandardWindowTheme.Apply(window);
        FluidMotion.SetWindowEntrance(window, false);
        try
        {
            window.Show();
            VisualTreeHelper.SetRootDpi(window, new DpiScale(pixelsPerDip, pixelsPerDip));
            InvalidateLayout(window);
            Settle(window);
            var viewport = (Canvas)window.Content;
            window.UpdateViewport(viewport.RenderSize, pixelsPerDip);
            Settle(window);
            return window;
        }
        catch { window.Close(); throw; }
    }

    private static void InvalidateLayout(DependencyObject element)
    {
        if (element is UIElement ui) { ui.InvalidateMeasure(); ui.InvalidateArrange(); }
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            InvalidateLayout(VisualTreeHelper.GetChild(element, i));
    }

    private static void Settle(Window window) =>
        window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));

    [Theory]
    [InlineData(1.25, 96)]
    [InlineData(1.5, 144)]
    [InlineData(1.75, 192)]
    public void SmallImageOpensAtPhysicalPixelsIndependentOfImageMetadataDpi(double monitorDpi, double imageDpi) => StaTestHost.Run(() =>
    {
        BitmapSource bitmap = BitmapSource.Create(400, 260, imageDpi, imageDpi, PixelFormats.Bgr32, null, new byte[400 * 260 * 4], 400 * 4);
        var window = Assert.IsType<GalleryImagePreviewWindow>(GalleryWindow.CreateImagePreviewWindow(bitmap, "Preview", "Capture"));
        try
        {
            window.UpdateViewport(new Size(608, 388), monitorDpi);
            var image = Assert.IsType<Image>(Assert.IsType<Canvas>(window.Content).Children[0]);
            Assert.Equal(400, image.Width);
            Assert.Equal(260, image.Height);
            Assert.Equal(1 / monitorDpi, window.ViewportModel.Scale, 10);
            Assert.Equal(400, image.Width * window.ViewportModel.Scale * monitorDpi, 8);
            Assert.Equal(100, window.ViewportModel.Percentage, 8);
            Assert.EndsWith("100%", window.Title);
            Assert.False(window.IsVisible);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(1.75)]
    public void LargeImageStartsFittedAndActualSizeAndFitAreDistinct(double dpi)
    {
        var viewport = new GalleryImagePreviewViewport(2400, 1600);
        viewport.Resize(new Size(600, 400), dpi);
        Assert.Equal(.25, viewport.Scale, 10);
        Assert.Equal(new Vector(0, 0), viewport.Offset);
        viewport.ActualSize();
        Assert.Equal(1 / dpi, viewport.Scale, 10);
        Assert.True(viewport.CanPan);
        viewport.Fit();
        Assert.Equal(.25, viewport.Scale, 10);
        Assert.False(viewport.CanPan);
    }

    [Fact]
    public void PointerZoomPreservesTheImagePointUntilAnEdgeMustBeClamped()
    {
        var viewport = new GalleryImagePreviewViewport(1200, 800);
        viewport.Resize(new Size(600, 400), 1.5);
        Point pointer = new(400, 250);
        Point original = viewport.ImagePointAt(pointer);
        viewport.Zoom(2, pointer);
        Assert.Equal(original.X, viewport.ImagePointAt(pointer).X, 8);
        Assert.Equal(original.Y, viewport.ImagePointAt(pointer).Y, 8);
        Assert.Equal(new Vector(-400, -250), viewport.Offset);
        viewport.Pan(new Vector(10000, -10000));
        Assert.Equal(new Vector(0, -400), viewport.Offset);
        viewport.Zoom(.1, new Point(0, 400));
        Assert.Equal((600 - 1200 * viewport.Scale) / 2, viewport.Offset.X, 8);
        Assert.Equal((400 - 800 * viewport.Scale) / 2, viewport.Offset.Y, 8);
    }

    [Fact]
    public void FitNeverEnlargesAndResizesWithoutDestroyingExplicitZoom()
    {
        var viewport = new GalleryImagePreviewViewport(400, 260);
        viewport.Resize(new Size(600, 400), 1.5);
        viewport.Fit();
        Assert.Equal(100, viewport.Percentage, 8);
        viewport.Zoom(3, new Point(300, 200));
        Assert.Equal(300, viewport.Percentage, 8);
        viewport.Resize(new Size(700, 500), 1.75);
        Assert.Equal(300, viewport.Percentage, 8);
        viewport.ActualSize();
        viewport.Resize(new Size(800, 600), 1.25);
        Assert.Equal(.8, viewport.Scale, 8);
        Assert.Equal(100, viewport.Percentage, 8);
        viewport.Fit();
        viewport.Resize(new Size(100, 65), 1.25);
        Assert.Equal(.25, viewport.Scale, 8);
    }

    [Fact]
    public void WheelKeyboardAndPanUseTheWindowViewportWithoutRealInput() => StaTestHost.Run(() =>
    {
        var window = new GalleryImagePreviewWindow(BitmapSource.Create(1200, 800, 192, 192,
            PixelFormats.Bgr32, null, new byte[1200 * 800 * 4], 1200 * 4), "Capture", "Capture");
        try
        {
            window.UpdateViewport(new Size(600, 400), 1.5);
            Assert.False(window.HandleWheel(120, ModifierKeys.None, new Point(300, 200)));
            Assert.Equal(.5, window.ViewportModel.Scale, 10);
            Point anchor = new(360, 240);
            Point original = window.ViewportModel.ImagePointAt(anchor);
            Assert.True(window.HandleWheel(120, ModifierKeys.Control, anchor));
            Assert.Equal(.6, window.ViewportModel.Scale, 10);
            Assert.Equal(original, window.ViewportModel.ImagePointAt(anchor));
            Assert.EndsWith("90%", window.Title);
            foreach (Key plus in new[] { Key.Add, Key.OemPlus })
            {
                double scale = window.ViewportModel.Scale;
                Assert.True(window.HandleZoomKey(plus, ModifierKeys.Control | ModifierKeys.Shift));
                Assert.Equal(scale * 1.2, window.ViewportModel.Scale, 10);
            }
            foreach (Key minus in new[] { Key.Subtract, Key.OemMinus })
                Assert.True(window.HandleZoomKey(minus, ModifierKeys.Control));
            Assert.Equal(.6, window.ViewportModel.Scale, 10);
            Assert.True(window.HandleZoomKey(Key.D1, ModifierKeys.Control));
            Assert.Equal(100, window.ViewportModel.Percentage, 8);
            Assert.True(window.BeginPan(new Point(250, 200)));
            window.PanTo(new Point(10000, -10000));
            Assert.Equal(0, window.ViewportModel.Offset.X, 8);
            Assert.Equal(400 - 800 / 1.5, window.ViewportModel.Offset.Y, 8);
            window.EndPan();
            Vector offset = window.ViewportModel.Offset;
            window.PanTo(new Point(-10000, 10000));
            Assert.Equal(offset, window.ViewportModel.Offset);
            foreach (Key fit in new[] { Key.D0, Key.NumPad0 })
            {
                Assert.True(window.HandleZoomKey(fit, ModifierKeys.Control));
                Assert.Equal(.5, window.ViewportModel.Scale, 10);
                Assert.False(window.BeginPan(new Point(0, 0)));
                Assert.True(window.HandleZoomKey(Key.NumPad1, ModifierKeys.Control));
                Assert.Equal(100, window.ViewportModel.Percentage, 8);
            }
            Assert.False(window.HandleZoomKey(Key.OemPlus, ModifierKeys.None));
            Assert.False(window.HandleZoomKey(Key.A, ModifierKeys.Control));
        }
        finally { window.Close(); }
    });
}
