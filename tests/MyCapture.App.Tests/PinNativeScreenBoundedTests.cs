using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Pinning;
using MyCapture.App.Themes;
using MyCapture.Core.Platform;
using MyCapture.Core.Primitives;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Design section 7: at rest a native pin holds only what the screens around it can show. The settled part
/// must be the same pixels as the same part of the whole settled picture, the held part must stay inside the
/// screens however far the zoom goes, and a move must bring in the newly visible part with exactly those pixels.
/// </summary>
public sealed class PinNativeScreenBoundedTests
{
    private static double Radius => WindowsFeatureSupport.WindowCornerRadiusDip(Environment.OSVersion.Version.Build);

    public static TheoryData<double, double, bool> Zooms()
    {
        var data = new TheoryData<double, double, bool>();
        foreach (double zoom in new[] { .5, 1.1, 1.5, 2.5937424601, 8 })
            foreach (double scale in new[] { 1d, 1.5 })
                foreach (bool transparent in new[] { false, true })
                    data.Add(zoom, scale, transparent);
        return data;
    }

    [Theory]
    [MemberData(nameof(Zooms))]
    public void ASettledPartIsByteIdenticalToTheSamePartOfTheWholeSettledPicture(double zoom, double scale, bool transparent)
    {
        BitmapSource picture = PinNativeTestImages.Picture(48, 34, transparent);
        using var source = NativePinPixelSource.FromBitmap(picture);
        using NativePinPixelSource.Lease lease = source.AcquireLease();
        int width = (int)Math.Round(48 * zoom * scale), height = (int)Math.Round(34 * zoom * scale);
        Color outline = PinNativeTestImages.Outline(AppTheme.Glass);
        using var whole = new NativePinWindowDib(width, height);
        _ = NativePinRaster.RenderCached(lease, whole.Bits, width, height, whole.Stride, scale, outline, Radius, true, out _);
        byte[] all = whole.CopyPixels();
        foreach ((int l, int t, int w, int h) in new[] { (0, 0, 55, 60), (30, 25, 40, 45), (45, 40, 55, 60), (0, 97, 100, 3), (0, 0, 100, 100) })
        {
            var view = new NativePinRaster.View(width * l / 100, height * t / 100,
                Math.Max(1, width * w / 100), Math.Max(1, height * h / 100), width, height);
            using var part = new NativePinWindowDib(view.Width, view.Height);
            byte[] stale = Enumerable.Repeat((byte)0x5A, part.ByteLength).ToArray();
            Marshal.Copy(stale, 0, part.Bits, stale.Length);
            _ = NativePinRaster.RenderCached(lease, part.Bits, view, part.Stride, scale, outline, Radius, true, out var edges);
            AssertSamePart(all, width, part.CopyPixels(), view, $"view {view}");
            Assert.Equal(view, edges.Area);

            // The worker path: coverage prepared for the view, rendered into a fresh buffer.
            var coverage = new NativePinRaster.Chrome(view, view.Width * 4, scale, Radius, 1);
            using var buffer = new NativePinSettleBuffer(view.Width, view.Height);
            _ = NativePinRaster.RenderPrepared(lease, buffer.Pointer, view, buffer.Stride, outline, true, coverage, out _);
            AssertSamePart(all, width, PinNativeTestMemory.Copy(buffer.Pointer, buffer.ByteLength), view, $"prepared {view}");
            if (!view.IsWhole)
                Assert.Throws<ArgumentException>(() => NativePinRaster.RenderPrepared(lease, buffer.Pointer,
                    NativePinRaster.View.Whole(view.Width, view.Height), buffer.Stride, outline, true, coverage, out _));
        }
    }

    [Fact]
    public void TheHeldPartIsTheSurfaceCutToTheScreensItTouches()
    {
        // One screen.
        RectD[] one = [new(0, 0, 1920, 1080)];
        Assert.True(NativePinRaster.View.Held(100, 100, 800, 600, one).IsWhole);
        Assert.Equal(new NativePinRaster.View(200, 100, 1920, 1080, 4000, 3000), NativePinRaster.View.Held(-200, -100, 4000, 3000, one));

        // Two side by side, the second left of the primary with a negative origin and a different height.
        RectD[] two = [new(0, 0, 1920, 1080), new(-2560, -360, 2560, 1440)];
        // Touching only the primary: the secondary is not part of what it holds.
        Assert.Equal(new NativePinRaster.View(0, 0, 1820, 980, 3000, 3000), NativePinRaster.View.Held(100, 100, 3000, 3000, two));
        // Across both: the box of both, so the part on either monitor stays.
        Assert.Equal(new NativePinRaster.View(2560, 1360, 4480, 1440, 9000, 5000), NativePinRaster.View.Held(-5120, -1720, 9000, 5000, two));
        // Inside the secondary and smaller than it: all of it.
        Assert.True(NativePinRaster.View.Held(-2000, -300, 1000, 700, two).IsWhole);

        // Three in an L: the third under the primary; a surface touching the primary and the third.
        RectD[] three = [new(0, 0, 1920, 1080), new(-2560, -360, 2560, 1440), new(0, 1080, 1920, 1200)];
        // Ten pixels over the secondary's edge as well: all three, cut to the surface.
        Assert.Equal(new NativePinRaster.View(0, 0, 1930, 2300, 5000, 5000), NativePinRaster.View.Held(-10, -20, 5000, 5000, three));
        Assert.Equal(new NativePinRaster.View(0, 0, 1920, 2280, 5000, 5000), NativePinRaster.View.Held(0, 0, 5000, 5000, three));

        // Off every screen: a small surface is held whole; a huge one no more than all the screens together.
        Assert.True(NativePinRaster.View.Held(-20000, -20000, 200, 100, three).IsWhole);
        NativePinRaster.View off = NativePinRaster.View.Held(-90000, -90000, 30720, 17280, three);
        Assert.True(off.Pixels <= 4480L * 2640, $"{off} holds more than the screens show.");
        Assert.True(NativePinRaster.View.Held(5, 5, 50, 50, []).IsWhole);
    }

    [Fact]
    public void ContainsAndIntersectCompareOnlyViewsOfTheSameSurface()
    {
        var a = new NativePinRaster.View(10, 10, 100, 50, 400, 300);
        Assert.True(a.Contains(a));
        Assert.True(a.Contains(new NativePinRaster.View(20, 20, 90, 40, 400, 300)));
        Assert.False(a.Contains(new NativePinRaster.View(20, 20, 91, 40, 400, 300)));
        Assert.False(a.Contains(new NativePinRaster.View(10, 10, 100, 50, 401, 300)));
        Assert.Equal(new NativePinRaster.View(60, 30, 50, 30, 400, 300), a.Intersect(new NativePinRaster.View(60, 30, 200, 200, 400, 300)));
        Assert.Null(a.Intersect(new NativePinRaster.View(110, 10, 5, 5, 400, 300)));
        Assert.Null(a.Intersect(new NativePinRaster.View(10, 10, 5, 5, 400, 301)));
        Assert.Equal(5000L, a.Pixels);
    }

    [Fact]
    public void A4KPictureAt800PercentHoldsNoMoreThanTheScreenAtRest() => StaTestHost.Run(() =>
    {
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        var screen = new RectD(-12000, -12000, 1600, 1000);
        var source = NativePinPixelSource.CreateFilled(3840, 2160, (row, y) =>
        {
            for (int x = 0; x < row.Length / 4; x++)
            {
                row[(x * 4) + 0] = (byte)x; row[(x * 4) + 1] = (byte)y; row[(x * 4) + 2] = (byte)(x ^ y); row[(x * 4) + 3] = 255;
            }
        });
        using var pin = new NativePinWindow(source, -11900, -11900);
        try
        {
            pin.LiveClipForTest = screen;
            pin.Show();
            pin.SetZoom(8);
            PinNativeSpeedTestWait.Complete(pin.WaitForSettledAsync());
            Assert.False(pin.IsLiveZooming);
            Assert.Equal(new RectD(-11900, -11900, 30720, 17280), pin.Bounds);
            Assert.Equal(new RectD(-11900, -11900, 1500, 900), pin.DisplayedBounds);
            Assert.Equal(pin.DisplayedBounds, WindowRectangle(pin.Handle));
            // The DIB, its worker section and the outline strips together stay within the screen's bytes.
            long screenBytes = 1600L * 1000 * 4;
            Assert.True(pin.DisplayBytes <= screenBytes, $"Display {pin.DisplayBytes} B.");
            Assert.Equal(pin.DisplayBytes, pin.DisplaySectionBytes);
            Assert.True(pin.EdgeCacheBytes < screenBytes / 4, $"Edges {pin.EdgeCacheBytes} B.");
            Assert.Equal(0, pin.WorkerBufferCount);
            Assert.Null(pin.LastPresentError);
        }
        finally
        {
            pin.Close();
            PinNativeSpeedTestWait.Complete(pin.DrainAsync());
            FluidMotion.AnimationsEnabledOverrideForTest = motion;
        }
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MovingASettledClippedPinBringsInTheNewPartWithTheSettledPixels(bool strips) => StaTestHost.Run(() =>
    {
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        long limit = NativePinWindow.ReholdPixelLimit;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        NativePinWindow.ReholdPixelLimit = strips ? limit : 0;
        BitmapSource picture = PinNativeTestImages.Picture(96, 64, transparent: true);
        using var pin = new NativePinWindow(picture, -20000, -20000);
        try
        {
            pin.LiveClipForTest = new RectD(-19990, -19995, 100, 70);
            pin.Show();
            pin.SetZoom(2);
            PinNativeSpeedTestWait.Complete(pin.WaitForSettledAsync());
            byte[] settled = PinNativeTestImages.Native(picture, 2, 1, AppTheme.Glass).Pixels;
            var first = new NativePinRaster.View(10, 5, 100, 70, 192, 128);
            Assert.Equal(new RectD(-19990, -19995, 100, 70), pin.DisplayedBounds);
            AssertSamePart(settled, 192, pin.CopyDisplayPixels(), first, "settled");

            // Each step brings in a new part: left and down, then right and up, then one pixel, then nothing.
            NativePinRaster.View previous = first;
            foreach ((int x, int y) in new[] { (-20030, -19988), (-20005, -20020), (-20006, -20020), (-20006, -20020) })
            {
                int reholds = pin.ReholdCount;
                pin.MovePhysical(x, y);
                var held = NativePinRaster.View.Clip(x, y, 192, 128, -19990, -19995, -19890, -19925);
                bool newPart = !previous.Contains(held);
                previous = held;
                Assert.Equal(new RectD(x, y, 192, 128), pin.Bounds);
                Assert.Equal(new RectD(x + held.Left, y + held.Top, held.Width, held.Height), pin.DisplayedBounds);
                Assert.Equal(pin.DisplayedBounds, WindowRectangle(pin.Handle));
                if (newPart && strips)
                {
                    // Drawn on this thread before the window moved: no live frame, no settle to wait for.
                    Assert.False(pin.IsLiveZooming);
                    Assert.False(pin.LastMoveDrewLive);
                    Assert.Equal(reholds + 1, pin.ReholdCount);
                    Assert.True(pin.LastReholdPixels > 0 && pin.LastReholdPixels <= NativePinWindow.ReholdPixelLimit);
                }
                else if (newPart)
                {
                    Assert.True(pin.IsLiveZooming);
                    Assert.True(pin.LastMoveDrewLive);
                    Assert.Equal(reholds, pin.ReholdCount);
                    PinNativeSpeedTestWait.Complete(pin.WaitForSettledAsync());
                }
                else
                {
                    Assert.False(pin.IsLiveZooming);
                    Assert.Equal(reholds, pin.ReholdCount);
                }
                Assert.Equal(new RectD(x + held.Left, y + held.Top, held.Width, held.Height), pin.DisplayedBounds);
                AssertSamePart(settled, 192, pin.CopyDisplayPixels(), held, $"after the move to ({x},{y})");
                // The outline strips are those of the new part: hover redraws them like a fresh settle's.
                pin.SetVisualState(hover: true, active: false);
                AssertSamePart(PinNativeTestImages.Native(picture, 2, 1, AppTheme.Glass, emphasized: true).Pixels, 192,
                    pin.CopyDisplayPixels(), held, $"hover after the move to ({x},{y})");
                pin.SetVisualState(hover: false, active: false);
            }
            Assert.Equal(1, NativePinWindow.LiveDibCount);
            Assert.Null(pin.LastPresentError);
        }
        finally
        {
            pin.Close();
            PinNativeSpeedTestWait.Complete(pin.DrainAsync());
            FluidMotion.AnimationsEnabledOverrideForTest = motion;
            NativePinWindow.ReholdPixelLimit = limit;
        }
    });

    [Fact]
    public void AMoveDuringTheSettleRenderCompletesThePictureForWhereThePinIsNow() => StaTestHost.Run(() =>
    {
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        BitmapSource picture = PinNativeTestImages.Picture(96, 64);
        using var pin = new NativePinWindow(picture, -20000, -20000);
        try
        {
            pin.LiveClipForTest = new RectD(-19990, -19995, 100, 70);
            pin.Show();
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            pin.BeforeSettleRasterForTest = token => { entered.Set(); release.Wait(token); };
            pin.SetZoom(2, live: true);
            pin.ForceSettle();
            PinNativeSpeedTestWait.Until(() => entered.IsSet);
            pin.BeforeSettleRasterForTest = null;
            // The worker draws the part held at the old place; the pin moves twelve pixels meanwhile.
            pin.MovePhysical(-20012, -20000);
            release.Set();
            PinNativeSpeedTestWait.Complete(pin.WaitForSettledAsync());
            var held = NativePinRaster.View.Clip(-20012, -20000, 192, 128, -19990, -19995, -19890, -19925);
            Assert.Equal(new RectD(-20012 + held.Left, -20000 + held.Top, held.Width, held.Height), pin.DisplayedBounds);
            AssertSamePart(PinNativeTestImages.Native(picture, 2, 1, AppTheme.Glass).Pixels, 192, pin.CopyDisplayPixels(), held, "settled");
            Assert.Null(pin.LastPresentError);
        }
        finally
        {
            pin.Close();
            PinNativeSpeedTestWait.Complete(pin.DrainAsync());
            FluidMotion.AnimationsEnabledOverrideForTest = motion;
        }
    });

    internal static void AssertSamePart(byte[] whole, int wholeWidth, byte[] part, NativePinRaster.View view, string state)
    {
        Assert.Equal(view.Width * view.Height * 4, part.Length);
        for (int y = 0; y < view.Height; y++)
        {
            var expected = whole.AsSpan(((y + view.Top) * wholeWidth + view.Left) * 4, view.Width * 4);
            var actual = part.AsSpan(y * view.Width * 4, view.Width * 4);
            int same = expected.CommonPrefixLength(actual);
            Assert.True(same == expected.Length, same == expected.Length ? string.Empty :
                $"{state}: view pixel ({same / 4},{y}) = surface ({same / 4 + view.Left},{y + view.Top}) BGRA[{same % 4}]: whole={expected[same]}, held={actual[same]}.");
        }
    }

    internal static RectD WindowRectangle(IntPtr handle)
    {
        Assert.True(NativePinWindowInterop.GetWindowRect(handle, out var actual));
        return new RectD(actual.Left, actual.Top, actual.Right - actual.Left, actual.Bottom - actual.Top);
    }
}
