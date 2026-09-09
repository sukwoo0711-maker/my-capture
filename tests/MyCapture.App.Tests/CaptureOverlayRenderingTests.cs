using System.Windows;
using System.Diagnostics;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Capture;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class CaptureOverlayRenderingTests
{
    [Fact]
    public void PointerBurst_CoalescesMagnifierWork_WithoutRebuildingDesktop() => StaTestHost.Run(() =>
    {
        var view = new CaptureOverlayView(Frame(PixelFormats.Bgr32));
        var window = Host(view);
        PointD pointer = new(20, 30);
        view.ReadCursor = () => pointer;
        try
        {
            window.Show();
            window.UpdateLayout();
            view.UpdatePointer(false, false);
            view.FlushFeedbackForTest();
            int staticBefore = view.StaticRenderCount;
            int updatesBefore = view.MagnifierUpdateCount;
            int renderedBefore = view.FeedbackRenderCount;
            BitmapSource? magnifier = view.MagnifierBitmapForTest;
            Assert.NotNull(magnifier);

            for (int i = 0; i < 200; i++)
            {
                pointer = new PointD(30 + i, 40 + i / 2);
                view.UpdatePointer(false, false);
            }
            Assert.True(view.HasPendingFeedback);
            Assert.Equal(updatesBefore, view.MagnifierUpdateCount);
            view.FlushFeedbackForTest();
            Assert.Equal(renderedBefore + 1, view.FeedbackRenderCount);
            Assert.Equal(updatesBefore + 1, view.MagnifierUpdateCount);
            Assert.Equal(staticBefore, view.StaticRenderCount);
            DrawingGroup desktopDrawing = ((DrawingVisual)VisualTreeHelper.GetChild(view, 0)).Drawing;
            Assert.Same(view.Frame!.Bitmap, Assert.Single(desktopDrawing.Children.OfType<ImageDrawing>()).ImageSource);
            Assert.Same(magnifier, view.MagnifierBitmapForTest);
            Assert.Equal("#604020", view.SampleLabelForTest);
            Assert.False(view.HasPendingFeedback);
        }
        finally { view.ReleaseResources(); window.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MagnifierCopiesCorrectSmallRegion_ForNativeAnd24BitSources(bool use24Bit) => StaTestHost.Run(() =>
    {
        var view = new CaptureOverlayView(Frame(use24Bit ? PixelFormats.Bgr24 : PixelFormats.Bgra32));
        var window = Host(view);
        view.ReadCursor = () => new PointD(1, 1);
        try
        {
            window.Show();
            window.UpdateLayout();
            view.UpdatePointer(false, false);
            view.FlushFeedbackForTest();
            BitmapSource magnifier = Assert.IsAssignableFrom<BitmapSource>(view.MagnifierBitmapForTest);
            Assert.Equal(15, magnifier.PixelWidth);
            Assert.Equal(15, magnifier.PixelHeight);
            Assert.Equal("#604020", view.SampleLabelForTest);
            byte[] pixels = new byte[15 * 15 * 4];
            magnifier.CopyPixels(pixels, 15 * 4, 0);
            Assert.Equal(new byte[] { 0x20, 0x40, 0x60, 0xFF }, pixels.Skip((15 + 1) * 4).Take(4));
        }
        finally { view.ReleaseResources(); window.Close(); }
    });

    [Fact]
    public void CloseClearsFrameMagnifierVisualSources_AndPendingStaticEvent() => StaTestHost.Run(() =>
    {
        var window = new CaptureOverlayWindow(Frame(PixelFormats.Bgr32), false, true);
        var view = Assert.IsType<CaptureOverlayView>(window.Content);
        PointD pointer = new(20, 30);
        view.ReadCursor = () => pointer;
        try
        {
            window.Show();
            // Show/UpdateLayout do not guarantee WPF's deferred Loaded/render callbacks
            // have run (physical placement can also queue another layout). Unloaded views
            // intentionally cannot subscribe to the static composition event.
            PumpUntil(() => view.IsLoaded && view.StaticRenderCount > 0);
            view.UpdatePointer(false, false);
            view.FlushFeedbackForTest();
            Assert.NotNull(view.MagnifierBitmapForTest);
            pointer = new(60, 70);
            view.UpdatePointer(false, false);
            Assert.True(view.HasPendingFeedback);
            window.Close();
            Assert.Null(window.Content);
            Assert.Null(view.Frame);
            Assert.Null(view.MagnifierBitmapForTest);
            Assert.False(view.HasPendingFeedback);
            Assert.Equal(0, VisualTreeHelper.GetChildrenCount(view));
            view.UpdatePointer(false, false);
            Assert.False(view.HasPendingFeedback);
        }
        finally { window.Close(); }
    });

    private static Window Host(CaptureOverlayView view) => new()
    {
        Content = view, Width = 320, Height = 240,
        WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
        ShowInTaskbar = false, ShowActivated = false,
    };

    private static void PumpUntil(Func<bool> ready)
    {
        var timeout = Stopwatch.StartNew();
        while (!ready() && timeout.Elapsed < TimeSpan.FromSeconds(5))
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
        Assert.True(ready(), "The capture overlay did not finish its Loaded/first-render lifecycle.");
    }

    private static FrozenFrame Frame(PixelFormat format)
    {
        byte[] pixels = new byte[320 * 240 * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 0x20;
            pixels[i + 1] = 0x40;
            pixels[i + 2] = 0x60;
            pixels[i + 3] = 0xFF;
        }
        BitmapSource source = BitmapSource.Create(320, 240, 96, 96, PixelFormats.Bgra32, null, pixels, 320 * 4);
        if (format != source.Format) source = new FormatConvertedBitmap(source, format, null, 0);
        source.Freeze();
        return new FrozenFrame(source, new RectD(0, 0, 320, 240), null, 0);
    }
}
