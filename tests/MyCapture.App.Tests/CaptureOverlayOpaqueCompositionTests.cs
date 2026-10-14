using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Capture;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class CaptureOverlayOpaqueCompositionTests
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void RegionTransitions_MoveOnlyTheRevealClip_AndMatchTheOriginalComposition(double scale) => StaTestHost.Run(() =>
    {
        FrozenFrame frame = Frame(PixelFormats.Bgr32);
        var actual = new CaptureOverlayView(frame, showMagnifier: false, showSelectionGrid: false);
        var reference = new CaptureOverlayView(frame, showMagnifier: false, showSelectionGrid: false);
        DrawingVisual[] legacy = InstallOriginalComposition(reference);
        RectD?[] regions = [null, new(0, 0, 64, 40), new(0, 0, 1, 40), new(63, 0, 1, 40),
            new(0, 0, 64, 1), new(0, 39, 64, 1), new(5, 7, 38, 24), new(-5, -4, 20, 19),
            new(60, 37, 20, 10), new(7, 9, 0, 0), null];
        try
        {
            SetDpi(actual, scale); SetDpi(reference, scale);
            int dimmerRenders = -1;
            Geometry? clip = null;
            for (int i = 0; i < regions.Length; i++)
            {
                // The same region may be a detected window or a drag. Selection wins when both exist.
                Set(actual, "_selection", i % 2 == 0 ? regions[i] : null);
                Set(reference, "_selection", i % 2 == 0 ? regions[i] : null);
                Set(actual, "_hoverRegion", regions[i]); Set(reference, "_hoverRegion", regions[i]);
                Compare(actual, reference, legacy, frame, 64, 40, scale);
                // The viewport-sized dimmer is drawn once; a new region only moves one retained clip,
                // so WPF redraws the old and new region instead of the whole desktop.
                if (dimmerRenders < 0) { dimmerRenders = actual.DimmerRenderCount; clip = Visual(actual, "_revealVisual").Clip; }
                Assert.Equal(dimmerRenders, actual.DimmerRenderCount);
                Assert.Same(clip, Visual(actual, "_revealVisual").Clip);
                Assert.Equal(1, Visual(actual, "_revealVisual").Opacity);
            }
            Set(actual, "_selection", new RectD(5, 7, 20, 15));
            Set(reference, "_selection", new RectD(5, 7, 20, 15));
            Set(actual, "_hoverRegion", new RectD(0, 0, 64, 40)); Set(reference, "_hoverRegion", new RectD(0, 0, 64, 40));
            Compare(actual, reference, legacy, frame, 64, 40, scale);
            // Resizing to a different pixel ratio uses the original path when edges are fractional.
            Compare(actual, reference, legacy, frame, 80, 50, scale);
            Set(actual, "_staticDirty", true); Set(reference, "_staticDirty", true);
            var dimmer = new SolidColorBrush(Color.FromArgb(127, 42, 81, 113)); dimmer.Freeze();
            Set(actual, "_dimmerBrush", dimmer); Set(reference, "_dimmerBrush", dimmer);
            Compare(actual, reference, legacy, frame, 64, 40, scale);
        }
        finally { actual.ReleaseResources(); reference.ReleaseResources(); }
    });

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void FractionalEdges_MatchTheOriginalComposition_WithoutRedrawingTheDimmer(double scale) => StaTestHost.Run(() =>
    {
        FrozenFrame frame = Frame(PixelFormats.Bgr32);
        var actual = new CaptureOverlayView(frame, false, false);
        var reference = new CaptureOverlayView(frame, false, false);
        DrawingVisual[] legacy = InstallOriginalComposition(reference);
        try
        {
            SetDpi(actual, scale); SetDpi(reference, scale);
            int dimmerRenders = -1;
            foreach (RectD region in new[] { new RectD(5, 7, 20, 15), new RectD(5.25, 7.5, 20.75, 15.25),
                new RectD(0.5, 0.25, 1.5, 2.75), new RectD(5, 7, 20, 15) })
            {
                Set(actual, "_selection", region); Set(reference, "_selection", region);
                Compare(actual, reference, legacy, frame, 64, 40, scale);
                if (dimmerRenders < 0) dimmerRenders = actual.DimmerRenderCount;
                Assert.Equal(dimmerRenders, actual.DimmerRenderCount);
            }
        }
        finally { actual.ReleaseResources(); reference.ReleaseResources(); }
    });

    [Theory]
    [InlineData("Bgra32")]
    [InlineData("Pbgra32")]
    [InlineData("Bgr24")]
    public void OtherPixelFormatsRetainOriginalAlphaAndConversionComposition(string formatName) => StaTestHost.Run(() =>
    {
        PixelFormat format = formatName switch { "Bgra32" => PixelFormats.Bgra32, "Pbgra32" => PixelFormats.Pbgra32, _ => PixelFormats.Bgr24 };
        FrozenFrame frame = Frame(format);
        var actual = new CaptureOverlayView(frame, false, false);
        var reference = new CaptureOverlayView(frame, false, false);
        DrawingVisual[] legacy = InstallOriginalComposition(reference);
        try
        {
            foreach (RectD? region in new RectD?[] { null, new(0, 0, 64, 40), new(5, 7, 20, 15), new(5.25, 7.5, 20, 15), null })
            {
                Set(actual, "_selection", region); Set(reference, "_selection", region);
                Compare(actual, reference, legacy, frame, 64, 40, 1);
                Assert.Equal(1, Visual(actual, "_revealVisual").Opacity);
            }
        }
        finally { actual.ReleaseResources(); reference.ReleaseResources(); }
    });

    [Fact]
    public void MissingFrameThenOpaqueAndAlphaAttachment_KeepTheRevealClipOnTheSelection() => StaTestHost.Run(() =>
    {
        var actual = new CaptureOverlayView(new RectD(0, 0, 64, 40), showMagnifier: false, showSelectionGrid: false);
        try
        {
            Set(actual, "_selection", new RectD(5, 7, 20, 15)); Layout(actual, 64, 40, 1);
            Geometry clip = Visual(actual, "_revealVisual").Clip;
            Assert.Equal(new Rect(5, 7, 20, 15), ((RectangleGeometry)clip).Rect);
            foreach (PixelFormat format in new[] { PixelFormats.Bgr32, PixelFormats.Bgra32 })
            {
                int dimmerRenders = actual.DimmerRenderCount;
                actual.AttachFrame(Frame(format)); RenderLayers(actual);
                // A new frame rebuilds the static layers once; the clip stays on the selection.
                Assert.Equal(dimmerRenders + 1, actual.DimmerRenderCount);
                Assert.Same(clip, Visual(actual, "_revealVisual").Clip);
                Assert.Equal(new Rect(5, 7, 20, 15), ((RectangleGeometry)clip).Rect);
                Assert.Equal(1, Visual(actual, "_revealVisual").Opacity);
            }
        }
        finally { actual.ReleaseResources(); }
    });

    private static DrawingVisual[] InstallOriginalComposition(CaptureOverlayView view)
    {
        var children = (VisualCollection)typeof(CaptureOverlayView).GetField("_visuals", Instance)!.GetValue(view)!;
        children.RemoveRange(0, 3);
        DrawingVisual[] legacy = [new(), new(), new()];
        for (int i = 0; i < legacy.Length; i++) children.Insert(i, legacy[i]);
        return legacy;
    }

    private static void Compare(CaptureOverlayView actual, CaptureOverlayView reference, DrawingVisual[] legacy,
        FrozenFrame frame, int width, int height, double scale)
    {
        Layout(actual, width, height, scale); Layout(reference, width, height, scale);
        // Independent recipe from the unchanged renderer: desktop, full dimmer, clipped desktop.
        using (DrawingContext dc = legacy[0].RenderOpen()) dc.DrawImage(frame.Bitmap, new Rect(reference.RenderSize));
        using (DrawingContext dc = legacy[1].RenderOpen())
        {
            Rect viewport = new(reference.RenderSize);
            Brush dimmer = (Brush)typeof(CaptureOverlayView).GetField("_dimmerBrush", Instance)!.GetValue(reference)!;
            dc.DrawRectangle(dimmer, null, viewport);
        }
        using (DrawingContext dc = legacy[2].RenderOpen())
        {
            RectD? selection = (RectD?)typeof(CaptureOverlayView).GetField("_selection", Instance)!.GetValue(reference);
            RectD? hover = selection.HasValue ? null : (RectD?)typeof(CaptureOverlayView).GetField("_hoverRegion", Instance)!.GetValue(reference);
            RectD? region = selection ?? hover;
            Rect clip = region.HasValue ? (Rect)typeof(CaptureOverlayView).GetMethod("ToDipRect", Instance)!
                .Invoke(reference, [region.Value.ClampTo(new RectD(0, 0, frame.PixelWidth, frame.PixelHeight))])! : new Rect(0, 0, 0, 0);
            legacy[2].Clip = new RectangleGeometry(clip); dc.DrawImage(frame.Bitmap, new Rect(reference.RenderSize));
        }
        byte[] expected = Pixels(reference, width, height, scale), obtained = Pixels(actual, width, height, scale);
        Assert.True(expected.AsSpan().SequenceEqual(obtained), $"Pixels differ at scale {scale}, region {actual.Selection}.");
    }

    private static byte[] Pixels(FrameworkElement view, int width, int height, double scale)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(view); byte[] pixels = new byte[width * height * 4]; bitmap.CopyPixels(pixels, width * 4, 0); return pixels;
    }
    private static void Layout(CaptureOverlayView view, int width, int height, double scale)
    {
        // Offscreen roots report 96DPI; avoid rounding DIPs as 96DPI after simulating a different
        // physical scale. Real per-monitor roots round at their actual DPI instead.
        view.UseLayoutRounding = false;
        view.Measure(new Size(width / scale, height / scale)); view.Arrange(new Rect(0, 0, width / scale, height / scale));
        view.UpdateLayout(); RenderLayers(view);
    }
    private static void SetDpi(CaptureOverlayView view, double scale) => typeof(CaptureOverlayView).GetMethod("OnDpiChanged", Instance)!
        .Invoke(view, [new DpiScale(1, 1), new DpiScale(scale, scale)]);
    private static void RenderLayers(CaptureOverlayView view) => typeof(CaptureOverlayView).GetMethod("RenderLayers", Instance)!.Invoke(view, null);
    private static void Set(CaptureOverlayView view, string field, object? value) => typeof(CaptureOverlayView).GetField(field, Instance)!.SetValue(view, value);
    private static DrawingVisual Visual(CaptureOverlayView view, string field) => (DrawingVisual)typeof(CaptureOverlayView).GetField(field, Instance)!.GetValue(view)!;
    private static FrozenFrame Frame(PixelFormat format)
    {
        byte[] pixels = new byte[64 * 40 * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        { pixels[i] = (byte)(i % 211); pixels[i + 1] = (byte)((i / 4) % 199); pixels[i + 2] = (byte)((i / 64) % 173); pixels[i + 3] = (byte)(23 + (i / 4) % 233); }
        BitmapSource bitmap = BitmapSource.Create(64, 40, 96, 96, PixelFormats.Bgra32, null, pixels, 64 * 4);
        if (format != PixelFormats.Bgra32) bitmap = new FormatConvertedBitmap(bitmap, format, null, 0);
        bitmap.Freeze(); return new FrozenFrame(bitmap, new RectD(0, 0, 64, 40), null, 0);
    }
}
