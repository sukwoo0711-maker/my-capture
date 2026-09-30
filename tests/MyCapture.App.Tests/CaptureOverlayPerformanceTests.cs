using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Capture;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class CaptureOverlayPerformanceTests
{
    [Fact]
    public void RepeatedSamePositionMouseMoves_DoNotScheduleFeedbackOrResample() => StaTestHost.Run(() =>
    {
        var view = new CaptureOverlayView(Frame());
        var host = Host(view);
        PointD pointer = new(120, 100);
        view.ReadCursor = () => pointer;
        try
        {
            host.Show();
            PumpUntil(() => view.IsLoaded && view.StaticRenderCount > 0);
            SetMouseOver(view, true);
            view.UpdatePointer(false, false);
            view.FlushFeedbackForTest();
            int feedback = view.FeedbackRenderCount;
            int samples = view.MagnifierUpdateCount;
            int desktop = view.StaticRenderCount;
            for (int i = 0; i < 200; i++)
                Invoke(view, "OnMouseMove", MoveArgs());
            Assert.False(view.HasPendingFeedback);
            Assert.Equal(feedback, view.FeedbackRenderCount);
            Assert.Equal(samples, view.MagnifierUpdateCount);
            Assert.Equal(desktop, view.StaticRenderCount);
            // A burst still renders its latest position once on the next frame.
            for (int i = 0; i < 100; i++)
            {
                pointer = new PointD(150 + i, 100 + i);
                Invoke(view, "OnMouseMove", MoveArgs());
            }
            Assert.True(view.HasPendingFeedback);
            view.FlushFeedbackForTest();
            Assert.Equal(feedback + 1, view.FeedbackRenderCount);
            Assert.Equal(desktop, view.StaticRenderCount);
            Assert.Equal(new Vector(0, 199), Visual(view, "_horizontalGuideVisual").Offset);
            Assert.Equal(new Vector(249, 0), Visual(view, "_verticalGuideVisual").Offset);
        }
        finally { view.ReleaseResources(); host.Close(); }
    });

    [Fact]
    public void PrecisionResetAtSamePosition_RefreshesInstructionWithoutDesktopRebuild() => StaTestHost.Run(() =>
    {
        var view = new CaptureOverlayView(Frame());
        var host = Host(view);
        view.ReadCursor = () => new PointD(120, 100);
        try
        {
            host.Show();
            PumpUntil(() => view.IsLoaded && view.StaticRenderCount > 0);
            view.UpdatePointer(true, true);
            view.FlushFeedbackForTest();
            Assert.True(Field<bool>(view, "_drawnPrecision"));
            int desktop = view.StaticRenderCount;
            view.InitializePointer();
            Assert.True(view.HasPendingFeedback);
            view.FlushFeedbackForTest();
            Assert.False(Field<bool>(view, "_drawnPrecision"));
            Assert.Equal(desktop, view.StaticRenderCount);
        }
        finally { view.ReleaseResources(); host.Close(); }
    });

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void HiddenGuides_RebuildAfterResizeAndDpi_WhenZeroAreaDragReturnsToReady(double scale) => StaTestHost.Run(() =>
    {
        var view = new CaptureOverlayView(Frame(), showMagnifier: false);
        view.ReadCursor = () => new PointD(120, 100);
        try
        {
            view.InitializePointer();
            Render(view, 640, 360);
            Invoke(view, "OnMouseLeftButtonDown", ButtonArgs(UIElement.MouseLeftButtonDownEvent));
            Render(view, 640, 360);
            AssertHidden(view);
            Invoke(view, "OnDpiChanged", new DpiScale(1, 1), new DpiScale(scale, scale));
            Render(view, 800, 450, scale);
            AssertHidden(view);
            Invoke(view, "OnMouseLeftButtonUp", ButtonArgs(UIElement.MouseLeftButtonUpEvent));
            Render(view, 800, 450, scale);
            Assert.Null(view.Selection);
            DrawingVisual horizontal = Visual(view, "_horizontalGuideVisual");
            DrawingVisual vertical = Visual(view, "_verticalGuideVisual");
            Assert.Equal(1, horizontal.Opacity);
            Assert.Equal(1, vertical.Opacity);
            Assert.Equal(view.ActualWidth, horizontal.ContentBounds.Width, 5);
            Assert.Equal(view.ActualHeight, vertical.ContentBounds.Height, 5);
            Assert.InRange(horizontal.ContentBounds.Height, 0, 3.001);
            Assert.InRange(vertical.ContentBounds.Width, 0, 3.001);
        }
        finally { view.ReleaseResources(); }
    });

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void RetainedStripPixels_MatchOriginalGuidesAtEdgesAndFractionalCoordinates(double scale) => StaTestHost.Run(() =>
    {
        foreach (PointD cursor in new[] { new PointD(0, 0), new PointD(639, 0), new PointD(0, 359), new PointD(639, 359), new PointD(123.25, 79.5) })
        {
            var optimized = new CaptureOverlayView(Frame(), showMagnifier: false);
            var reference = new CaptureOverlayView(Frame(), showMagnifier: false, showSelectionGrid: false);
            try
            {
                optimized.ReadCursor = reference.ReadCursor = () => cursor;
                optimized.InitializePointer();
                reference.InitializePointer();
                Render(reference, 640, 360, scale);
                var original = new DrawingVisual();
                // The v3.1.4 full-viewport guide drawing, preserved as a pixel reference.
                using (DrawingContext dc = original.RenderOpen())
                {
                    var shadow = new Pen(new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)), 3);
                    var line = new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x47)), 1.25);
                    Point p = new(cursor.X * reference.ActualWidth / 640, cursor.Y * reference.ActualHeight / 360);
                    dc.PushClip(new RectangleGeometry(new Rect(reference.RenderSize)));
                    dc.DrawLine(shadow, new Point(0, p.Y), new Point(reference.ActualWidth, p.Y));
                    dc.DrawLine(line, new Point(0, p.Y), new Point(reference.ActualWidth, p.Y));
                    dc.DrawLine(shadow, new Point(p.X, 0), new Point(p.X, reference.ActualHeight));
                    dc.DrawLine(line, new Point(p.X, 0), new Point(p.X, reference.ActualHeight));
                    dc.Pop();
                }
                Field<VisualCollection>(reference, "_visuals").Insert(3, original);
                byte[] expected = Pixels(Render(reference, 640, 360, scale));
                byte[] actual = Pixels(Render(optimized, 640, 360, scale));
                int changed = 0;
                int first = -1;
                for (int i = 0; i < actual.Length; i += 4)
                {
                    if (actual.AsSpan(i, 4).SequenceEqual(expected.AsSpan(i, 4))) continue;
                    if (first < 0) first = i / 4;
                    changed++;
                }
                Assert.True(changed == 0, $"{changed} changed pixels at scale {scale}, cursor {cursor}; first ({first % 640}, {first / 640}).");
            }
            finally { optimized.ReleaseResources(); reference.ReleaseResources(); }
        }
    });

    [Fact]
    public void LostCaptureAndCancel_ClearRetainedGuidesAndFeedback() => StaTestHost.Run(() =>
    {
        var view = new CaptureOverlayView(Frame());
        view.ReadCursor = () => new PointD(120, 100);
        try
        {
            SetMouseOver(view, true);
            view.InitializePointer();
            Render(view, 640, 360);
            Invoke(view, "OnMouseLeftButtonDown", ButtonArgs(UIElement.MouseLeftButtonDownEvent));
            Render(view, 640, 360);
            AssertHidden(view);
            Invoke(view, "OnLostMouseCapture", new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.LostMouseCaptureEvent });
            Render(view, 640, 360);
            Assert.Equal(1, Visual(view, "_horizontalGuideVisual").Opacity);
            Invoke(view, "RequestCancel");
            Render(view, 640, 360);
            AssertHidden(view);
            Assert.True(Visual(view, "_pointerVisual").ContentBounds.IsEmpty);
            Assert.True(Visual(view, "_magnifierVisual").ContentBounds.IsEmpty);
        }
        finally { view.ReleaseResources(); }
    });

    [Fact]
    public void SamePositionMouseLeaveReentryAndSourceAttach_RefreshMagnifierCorrectly() => StaTestHost.Run(() =>
    {
        var view = new CaptureOverlayView(Frame());
        view.ReadCursor = () => new PointD(120, 100);
        try
        {
            SetMouseOver(view, true);
            view.InitializePointer();
            Render(view, 640, 360);
            Assert.False(Visual(view, "_magnifierVisual").ContentBounds.IsEmpty);
            Assert.Equal("#604020", view.SampleLabelForTest);
            SetMouseOver(view, false);
            Invoke(view, "OnMouseLeave", new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseLeaveEvent });
            Render(view, 640, 360);
            Assert.True(Visual(view, "_magnifierVisual").ContentBounds.IsEmpty);
            SetMouseOver(view, true);
            Invoke(view, "OnMouseEnter", new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
            Render(view, 640, 360);
            Assert.False(Visual(view, "_magnifierVisual").ContentBounds.IsEmpty);
            view.AttachFrame(Frame(0xA0));
            Render(view, 640, 360);
            Assert.Equal("#6040A0", view.SampleLabelForTest);
            byte[] pixel = new byte[4];
            view.MagnifierBitmapForTest!.CopyPixels(new Int32Rect(0, 0, 1, 1), pixel, 4, 0);
            Assert.Equal(new byte[] { 0xA0, 0x40, 0x60, 0xFF }, pixel);
        }
        finally { view.ReleaseResources(); }
    });

    private static void AssertHidden(CaptureOverlayView view)
    {
        Assert.Equal(0, Visual(view, "_horizontalGuideVisual").Opacity);
        Assert.Equal(0, Visual(view, "_verticalGuideVisual").Opacity);
    }

    private static T Field<T>(CaptureOverlayView view, string field) =>
        (T)typeof(CaptureOverlayView).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
    private static DrawingVisual Visual(CaptureOverlayView view, string field) => Field<DrawingVisual>(view, field);
    private static void Invoke(CaptureOverlayView view, string method, params object[] arguments) =>
        typeof(CaptureOverlayView).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, arguments);
    private static MouseEventArgs MoveArgs() => new(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseMoveEvent };
    private static MouseButtonEventArgs ButtonArgs(RoutedEvent routed) => new(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = routed };

    private static void SetMouseOver(CaptureOverlayView view, bool value)
    {
        var key = (DependencyPropertyKey)typeof(UIElement).GetField("IsMouseOverPropertyKey", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        view.SetValue(key, value);
        MethodInfo writeFlag = typeof(UIElement).GetMethod("WriteFlag", BindingFlags.Instance | BindingFlags.NonPublic)!;
        writeFlag.Invoke(view, [Enum.Parse(writeFlag.GetParameters()[0].ParameterType, "IsMouseOverCache"), value]);
    }

    private static RenderTargetBitmap Render(CaptureOverlayView view, int width, int height, double scale = 1)
    {
        view.InvalidateVisual();
        view.Measure(new Size(width / scale, height / scale));
        view.Arrange(new Rect(0, 0, width / scale, height / scale));
        view.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(view);
        return bitmap;
    }

    private static byte[] Pixels(BitmapSource bitmap)
    {
        byte[] pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }

    private static Window Host(CaptureOverlayView view) => new()
    {
        Content = view, Width = 640, Height = 360, WindowStyle = WindowStyle.None,
        ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false
    };

    private static void PumpUntil(Func<bool> predicate)
    {
        var timeout = Stopwatch.StartNew();
        while (!predicate() && timeout.Elapsed < TimeSpan.FromSeconds(3))
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
        Assert.True(predicate());
    }

    private static FrozenFrame Frame(byte blue = 0x20)
    {
        byte[] pixels = new byte[640 * 360 * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = blue;
            pixels[i + 1] = 0x40;
            pixels[i + 2] = 0x60;
            pixels[i + 3] = 255;
        }
        BitmapSource bitmap = BitmapSource.Create(640, 360, 96, 96, PixelFormats.Bgra32, null, pixels, 640 * 4);
        bitmap.Freeze();
        return new FrozenFrame(bitmap, new RectD(0, 0, 640, 360), null, 0);
    }
}
