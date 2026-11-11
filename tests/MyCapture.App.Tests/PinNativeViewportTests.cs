using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Pinning;
using MyCapture.App.Themes;
using MyCapture.Core.Platform;
using MyCapture.Core.Primitives;
using MyCapture.Core.Themes;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

/// <summary>
/// A live frame draws only the part of the surface that can be on a screen. These tests hold that
/// part to the pixels the whole surface would have at the same place, and the window to its
/// whole-surface geometry everywhere outside the live frame.
/// </summary>
public sealed class PinNativeViewportTests(ITestOutputHelper output)
{
    private static double Radius => WindowsFeatureSupport.WindowCornerRadiusDip(Environment.OSVersion.Version.Build);

    [Fact]
    public void ClipKeepsTheVisiblePartAndFallsBackToTheWholeSurfaceWhenNothingIsVisible()
    {
        Assert.Equal(new NativePinRaster.View(20, 0, 80, 50, 200, 100),
            NativePinRaster.View.Clip(left: -20, top: 10, 200, 100, clipLeft: 0, clipTop: 0, clipRight: 80, clipBottom: 60));
        Assert.Equal(new NativePinRaster.View(0, 30, 100, 70, 200, 100),
            NativePinRaster.View.Clip(left: 500, top: -30, 200, 100, clipLeft: 0, clipTop: 0, clipRight: 600, clipBottom: 400));
        // A screen that contains the surface, and one that does not touch it, both keep all of it.
        Assert.True(NativePinRaster.View.Clip(10, 10, 200, 100, 0, 0, 1000, 1000).IsWhole);
        Assert.True(NativePinRaster.View.Clip(-20000, -20000, 200, 100, 0, 0, 1000, 1000).IsWhole);
        Assert.True(NativePinRaster.View.Clip(1000, 0, 200, 100, 0, 0, 1000, 1000).IsWhole);
        Assert.Throws<ArgumentOutOfRangeException>(() => new NativePinRaster.View(150, 0, 80, 50, 200, 100).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new NativePinRaster.View(0, 0, 0, 50, 200, 100).Validate());
    }

    public static TheoryData<int, double, double, double, int, int, int, int> Views()
    {
        var data = new TheoryData<int, double, double, double, int, int, int, int>();
        foreach (int mode in new[] { (int)NativePinLiveMode.GdiNearest, (int)NativePinLiveMode.IntegerBilinear })
            foreach ((double zoom, double dpiScale, double contentScale) in new[]
                { (1.1, 1d, 1d), (2.5937424601, 1d, 1d), (1.65, 1.5, 1d), (.62, 1d, 1d), (1.5, 1d, .97), (1.5, 1.5, .9) })
            {
                // Fractions of the surface: a corner, an inner block, the far edges, one thin strip, all of it.
                data.Add(mode, zoom, dpiScale, contentScale, 0, 0, 55, 60);
                data.Add(mode, zoom, dpiScale, contentScale, 30, 25, 40, 45);
                data.Add(mode, zoom, dpiScale, contentScale, 45, 40, 55, 60);
                data.Add(mode, zoom, dpiScale, contentScale, 0, 97, 100, 3);
                data.Add(mode, zoom, dpiScale, contentScale, 0, 0, 100, 100);
            }
        return data;
    }

    [Theory]
    [MemberData(nameof(Views))]
    public void ClippedLiveFrameIsByteIdenticalToTheSamePartOfTheWholeLiveFrame(int modeValue, double zoom,
        double dpiScale, double contentScale, int leftPercent, int topPercent, int widthPercent, int heightPercent)
    {
        var mode = (NativePinLiveMode)modeValue;
        BitmapSource picture = PinNativeTestImages.Picture(160, 110, transparent: true);
        using var source = NativePinPixelSource.FromBitmap(picture);
        using NativePinPixelSource.Lease lease = source.AcquireLease();
        int width = (int)Math.Round(160 * zoom * dpiScale), height = (int)Math.Round(110 * zoom * dpiScale);
        var view = new NativePinRaster.View(width * leftPercent / 100, height * topPercent / 100,
            Math.Max(1, width * widthPercent / 100), Math.Max(1, height * heightPercent / 100), width, height);
        Color rest = PinNativeTestImages.Outline(AppTheme.Glass), emphasized = PinNativeTestImages.Outline(AppTheme.Glass, true);

        using var whole = new NativePinWindowDib(width, height);
        NativePinLiveRaster.Write(mode, lease, whole.Dc, whole.Bits, width, height, whole.Stride, contentScale);
        byte[] wholeRaw = whole.CopyPixels();
        NativePinEdgeCache wholeEdges = NativePinEdgeCache.CaptureAndApply(whole.Bits, width, height, whole.Stride,
            dpiScale, rest, Radius, contentScale);

        using var part = new NativePinWindowDib(view.Width, view.Height);
        // Stale content must not survive: a live frame reuses its DIB from notch to notch.
        byte[] stale = Enumerable.Repeat((byte)0xA5, part.ByteLength).ToArray();
        Marshal.Copy(stale, 0, part.Bits, stale.Length);
        NativePinLiveRaster.Write(mode, lease, part.Dc, part.Bits, view, part.Stride, contentScale);
        AssertSamePart(wholeRaw, width, part.CopyPixels(), view, "picture before the outline");
        NativePinEdgeCache partEdges = NativePinEdgeCache.CaptureAndApply(part.Bits, view, part.Stride,
            dpiScale, rest, Radius, contentScale);
        AssertSamePart(whole.CopyPixels(), width, part.CopyPixels(), view, "rest");
        Assert.True(partEdges.ByteLength <= wholeEdges.ByteLength, "A view never needs more outline than its surface.");

        // The strips of a clipped frame restore and recomposite exactly as the whole frame's do.
        foreach ((string state, Color outline) in new[] { ("hover", emphasized), ("rest again", rest), ("hover again", emphasized) })
        {
            wholeEdges.Redraw(whole.Bits, outline);
            partEdges.Redraw(part.Bits, outline);
            AssertSamePart(whole.CopyPixels(), width, part.CopyPixels(), view, state);
        }
    }

    [Theory]
    [InlineData(9)]
    [InlineData(7)]
    [InlineData(5)]
    [InlineData(3)]
    public void GdiNearestReductionKeepsWholeSourcePixelsNextToTheirIdealPosition(int size)
    {
        // A wheel notch also shrinks the picture. Whatever rule GDI uses to drop pixels, every
        // pixel it keeps must be one complete premultiplied source pixel from the right place.
        const int sourceSize = 10;
        byte[] pixels = new byte[sourceSize * sourceSize * 4];
        for (int pixel = 0; pixel < sourceSize * sourceSize; pixel++)
        {
            int alpha = 40 + pixel * 2;
            pixels[pixel * 4] = (byte)(pixel % 2 == 0 ? alpha : alpha / 3);
            pixels[pixel * 4 + 1] = (byte)(alpha / 2);
            pixels[pixel * 4 + 2] = (byte)(pixel % 3 == 0 ? 0 : alpha);
            pixels[pixel * 4 + 3] = (byte)alpha;
        }
        using var source = NativePinPixelSource.CreateFilled(sourceSize, sourceSize,
            (row, y) => pixels.AsSpan(y * sourceSize * 4, sourceSize * 4).CopyTo(row));
        using NativePinPixelSource.Lease lease = source.AcquireLease();
        using var target = new NativePinWindowDib(size, size);
        NativePinLiveRaster.Write(NativePinLiveMode.GdiNearest, lease, target.Dc, target.Bits, size, size, target.Stride);
        byte[] actual = target.CopyPixels();
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                uint observed = BitConverter.ToUInt32(actual, (y * size + x) * 4);
                double idealX = (x + .5) * sourceSize / size - .5, idealY = (y + .5) * sourceSize / size - .5;
                bool found = false;
                for (int sy = (int)Math.Floor(idealY) - 1; sy <= (int)Math.Ceiling(idealY) + 1 && !found; sy++)
                    for (int sx = (int)Math.Floor(idealX) - 1; sx <= (int)Math.Ceiling(idealX) + 1 && !found; sx++)
                        found = sx >= 0 && sy >= 0 && sx < sourceSize && sy < sourceSize
                            && BitConverter.ToUInt32(pixels, (sy * sourceSize + sx) * 4) == observed;
                Assert.True(found, $"GDI {size * 10}% pixel ({x},{y}) = 0x{observed:X8} is not a whole source pixel near ({idealX:F2},{idealY:F2}).");
            }
    }

    // Design section 7: with the screen-bounded rest on (the default) the settled picture holds the same part
    // of the surface as the live frame, at full quality; off, it is the whole surface again, as before.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LiveZoomDrawsOnlyThePartInsideTheClipAndTheSettledPictureHoldsWhatTheScreenCanShow(bool boundedRest) => StaTestHost.Run(() =>
    {
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        bool previousRest = NativePinWindow.ScreenBoundedRest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        NativePinWindow.ScreenBoundedRest = boundedRest;
        BitmapSource picture = PinNativeTestImages.Picture(96, 64, transparent: true);
        using var pin = new NativePinWindow(picture, -20000, -20000);
        try
        {
            // A stand-in screen that the doubled picture overflows on its right and bottom.
            pin.LiveClipForTest = new RectD(-20010, -20010, 130, 90);
            pin.Show();
            Assert.False(pin.IsDisplayClipped);
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            pin.BeforeSettleRasterForTest = token => { entered.Set(); release.Wait(token); };

            pin.SetZoom(2, live: true);
            Assert.True(pin.IsLiveZooming);
            Assert.True(pin.IsDisplayClipped);
            Assert.Equal(192, pin.PixelWidth);
            Assert.Equal(128, pin.PixelHeight);
            Assert.Equal(new RectD(-20000, -20000, 192, 128), pin.Bounds);
            Assert.Equal(new RectD(-20000, -20000, 120, 80), pin.DisplayedBounds);
            Assert.Equal(120L * 80 * 4, pin.DisplayBytes);
            Assert.Equal(pin.DisplayedBounds, WindowRectangle(pin.Handle));
            Assert.Equal(1, NativePinWindow.LiveDibCount);
            Assert.Equal(LivePart(picture, 192, 128, new NativePinRaster.View(0, 0, 120, 80, 192, 128), 1), pin.CopyDisplayPixels());

            // The worker is held, so the picture on show is still the clipped live frame. An outline
            // change redraws its strips and nothing else.
            pin.ForceSettle();
            PinNativeSpeedTestWait.Until(() => entered.IsSet);
            int rasters = pin.RasterCount;
            pin.SetVisualState(hover: true, active: false);
            Assert.Equal(rasters, pin.RasterCount);
            Assert.Equal(LivePart(picture, 192, 128, new NativePinRaster.View(0, 0, 120, 80, 192, 128), 1, emphasized: true),
                pin.CopyDisplayPixels());
            pin.SetVisualState(hover: false, active: false);

            release.Set();
            PinNativeSpeedTestWait.Complete(pin.WaitForSettledAsync());
            Assert.False(pin.IsLiveZooming);
            Assert.Equal(new RectD(-20000, -20000, 192, 128), pin.Bounds);
            Assert.Equal(pin.DisplayBytes, pin.DisplaySectionBytes);
            Assert.Equal(0, pin.WorkerBufferCount);
            Assert.Equal(1, NativePinWindow.LiveDibCount);
            byte[] settled = PinNativeTestImages.Native(picture, 2, 1, AppTheme.Glass).Pixels;
            if (boundedRest)
            {
                var held = new NativePinRaster.View(0, 0, 120, 80, 192, 128);
                Assert.True(pin.IsDisplayClipped);
                Assert.Equal(new RectD(-20000, -20000, 120, 80), pin.DisplayedBounds);
                Assert.Equal(pin.DisplayedBounds, WindowRectangle(pin.Handle));
                Assert.Equal(120L * 80 * 4, pin.DisplayBytes);
                AssertSamePart(settled, 192, pin.CopyDisplayPixels(), held, "settled part");
            }
            else
            {
                Assert.False(pin.IsDisplayClipped);
                Assert.Equal(pin.Bounds, pin.DisplayedBounds);
                Assert.Equal(pin.Bounds, WindowRectangle(pin.Handle));
                Assert.Equal(192L * 128 * 4, pin.DisplayBytes);
                Assert.Equal(settled, pin.CopyDisplayPixels());
            }
            Assert.Null(pin.LastPresentError);
        }
        finally
        {
            pin.Close();
            PinNativeSpeedTestWait.Complete(pin.DrainAsync());
            FluidMotion.AnimationsEnabledOverrideForTest = motion;
            NativePinWindow.ScreenBoundedRest = previousRest;
        }
    });

    [Fact]
    public void MovingAClippedLiveFrameDrawsThePartVisibleAtTheNewPlace() => StaTestHost.Run(() =>
    {
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        BitmapSource picture = PinNativeTestImages.Picture(96, 64);
        using var pin = new NativePinWindow(picture, -20000, -20000);
        try
        {
            pin.LiveClipForTest = new RectD(-19990, -19995, 100, 70);
            pin.Show();
            pin.SetZoom(2, live: true);
            var first = new NativePinRaster.View(10, 5, 100, 70, 192, 128);
            Assert.Equal(new RectD(-19990, -19995, 100, 70), pin.DisplayedBounds);
            Assert.Equal(LivePart(picture, 192, 128, first, 1), pin.CopyDisplayPixels());

            // Thirty to the left and twelve down: more of the picture's right side comes into the clip.
            pin.MovePhysical(-20030, -19988);
            Assert.True(pin.IsDisplayClipped);
            Assert.Equal(new RectD(-20030, -19988, 192, 128), pin.Bounds);
            var moved = new NativePinRaster.View(40, 0, 100, 63, 192, 128);
            Assert.Equal(new RectD(-19990, -19988, 100, 63), pin.DisplayedBounds);
            Assert.Equal(pin.DisplayedBounds, WindowRectangle(pin.Handle));
            Assert.Equal(LivePart(picture, 192, 128, moved, 1), pin.CopyDisplayPixels());

            // Outside the clip altogether nothing can be seen; the frame is the whole surface, as before.
            pin.MovePhysical(-25000, -25000);
            Assert.False(pin.IsDisplayClipped);
            Assert.Equal(new RectD(-25000, -25000, 192, 128), pin.Bounds);
            Assert.Equal(pin.Bounds, WindowRectangle(pin.Handle));

            PinNativeSpeedTestWait.Complete(pin.WaitForSettledAsync());
            Assert.Equal(new RectD(-25000, -25000, 192, 128), pin.Bounds);
            Assert.Equal(pin.Bounds, WindowRectangle(pin.Handle));
            Assert.Equal(PinNativeTestImages.Native(picture, 2, 1, AppTheme.Glass).Pixels, pin.CopyDisplayPixels());
            Assert.Null(pin.LastPresentError);
        }
        finally
        {
            pin.Close();
            PinNativeSpeedTestWait.Complete(pin.DrainAsync());
            FluidMotion.AnimationsEnabledOverrideForTest = motion;
        }
    });

    [Fact]
    public void PressInsideAClippedFrameIsHitTestedAgainstTheSurfaceNotTheWindow() => StaTestHost.Run(() =>
    {
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        using var pin = new NativePinWindow(PinNativeTestImages.Picture(96, 64), -20000, -20000);
        try
        {
            // The window starts 60 px inside the surface: its own left edge is the picture's middle.
            pin.LiveClipForTest = new RectD(-19940, -19960, 100, 60);
            pin.Show();
            pin.SetZoom(2, live: true);
            Assert.Equal(new RectD(-19940, -19960, 100, 60), pin.DisplayedBounds);
            double zoom = pin.Zoom;

            // Client (1,30) is surface (61,70): the interior, so the press starts a move. Taken as
            // window coordinates it would be the left resize edge and the drag would change the zoom.
            _ = SendMessageW(pin.Handle, 0x0201, new IntPtr(1), new IntPtr((30 << 16) | 1));
            Assert.True(pin.IsDragging, "The real HWND must hold the mouse capture for this gesture.");
            _ = SendMessageW(pin.Handle, 0x0200, new IntPtr(1), new IntPtr((39 << 16) | 9));
            _ = SendMessageW(pin.Handle, 0x0202, IntPtr.Zero, new IntPtr((39 << 16) | 9));
            Assert.False(pin.IsDragging);
            Assert.Equal(zoom, pin.Zoom);
            Assert.Equal(new RectD(-19992, -19991, 192, 128), pin.Bounds);
            Assert.Null(pin.LastPresentError);
        }
        finally
        {
            pin.Close();
            PinNativeSpeedTestWait.Complete(pin.DrainAsync());
            FluidMotion.AnimationsEnabledOverrideForTest = motion;
        }
    });

    [Fact]
    public void ANotchAfterTheWorkerFinishedButBeforeAdoptionDiscardsThatPictureAndItsBuffer() => StaTestHost.Run(() =>
    {
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        BitmapSource picture = PinNativeTestImages.Picture(96, 64, transparent: true);
        using var pin = new NativePinWindow(picture, -20000, -20000);
        try
        {
            pin.Show();
            pin.SetZoom(1.5);
            int stale = pin.RequestedGeneration;
            // Sleeping does not run this thread's dispatcher, so the finished frame stays unadopted.
            var watch = Stopwatch.StartNew();
            while (!pin.IsSettleResultWaitingForTest && watch.Elapsed < TimeSpan.FromSeconds(2)) Thread.Sleep(1);
            Assert.True(pin.IsSettleResultWaitingForTest, "The worker did not finish its picture within two seconds.");
            Assert.Equal(1, pin.WorkerBufferCount);
            Assert.True(pin.SettledGeneration < stale);

            pin.SetZoom(2);
            Assert.True(pin.RequestedGeneration > stale);
            Assert.False(pin.IsSettleResultWaitingForTest);
            Assert.True(pin.SettledGeneration < stale, "The finished 150% picture must not be adopted after the new notch.");
            PinNativeSpeedTestWait.Complete(pin.WaitForSettledAsync());
            Assert.Equal(pin.RequestedGeneration, pin.SettledGeneration);
            Assert.Equal(192, pin.PixelWidth);
            Assert.Equal(PinNativeTestImages.Native(picture, 2, 1, AppTheme.Glass).Pixels, pin.CopyDisplayPixels());
            PinNativeSpeedTestWait.Complete(pin.DrainAsync());
            Assert.Equal(0, pin.WorkerBufferCount);
            Assert.Equal(1, pin.PeakWorkerBufferCount);
            Assert.Equal(0, NativePinSettleBuffer.LiveCount);
            Assert.Equal(0, NativePinSettleBuffer.LiveBytes);
            Assert.Equal(1, NativePinWindow.LiveDisplaySectionCount);
            Assert.Null(pin.LastPresentError);
        }
        finally
        {
            pin.Close();
            PinNativeSpeedTestWait.Complete(pin.DrainAsync());
            FluidMotion.AnimationsEnabledOverrideForTest = motion;
        }
    });

    [Fact]
    public void FiftyClippedZoomBurstCyclesReturnGdiUserAndOwnedResourcesToTheirBaseline() => StaTestHost.Run(() =>
    {
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        try
        {
            _ = RunClippedCycle(settleBeforeClose: true);
            using Process process = Process.GetCurrentProcess();
            for (int attempt = 1; ; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                HashSet<uint> guiThreads = PinNativeSpeedGuiThreads.Capture(process);
                uint gdi = GetGuiResources(process.Handle, 0), user = GetGuiResources(process.Handle, 1);
                int windows = NativePinWindow.LiveWindowCount, dibs = NativePinWindow.LiveDibCount, dcs = NativePinWindow.LiveDcCount;
                int sources = NativePinPixelSource.LiveAllocationCount, sections = NativePinWindow.LiveDisplaySectionCount;
                long sourceBytes = NativePinPixelSource.LiveBytes, sectionBytes = NativePinWindow.LiveDisplaySectionBytes;
                var objects = new List<WeakReference>();
                for (int cycle = 0; cycle < 50; cycle++)
                {
                    objects.AddRange(RunClippedCycle(settleBeforeClose: cycle % 2 == 0));
                    Assert.Equal(windows, NativePinWindow.LiveWindowCount);
                    Assert.Equal(dibs, NativePinWindow.LiveDibCount);
                    Assert.Equal(dcs, NativePinWindow.LiveDcCount);
                    Assert.Equal(sources, NativePinPixelSource.LiveAllocationCount);
                    Assert.Equal(sourceBytes, NativePinPixelSource.LiveBytes);
                    Assert.Equal(0, NativePinSettleBuffer.LiveCount);
                    Assert.Equal(0, NativePinSettleBuffer.LiveBytes);
                    Assert.Equal(sections, NativePinWindow.LiveDisplaySectionCount);
                    Assert.Equal(sectionBytes, NativePinWindow.LiveDisplaySectionBytes);
                }
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                Assert.Equal(100, objects.Count);
                Assert.All(objects, reference => Assert.False(reference.IsAlive));
                // Windows adds a GUI thread of its own to a process the first time it shows windows;
                // fifty cycles that span that moment are measured again from a new baseline.
                if (PinNativeSpeedGuiThreads.ChangedAndMayRepeat(process, guiThreads, attempt, output)) continue;
                Assert.Equal(gdi, GetGuiResources(process.Handle, 0));
                Assert.Equal(user, GetGuiResources(process.Handle, 1));
                break;
            }
        }
        finally { FluidMotion.AnimationsEnabledOverrideForTest = motion; }
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] RunClippedCycle(bool settleBeforeClose)
    {
        BitmapSource picture = PinNativeTestImages.Picture(96, 64, transparent: true);
        using var pin = new NativePinWindow(picture, -20000, -20000);
        WeakReference[] observed = [new(pin), new(pin.Source!)];
        try
        {
            pin.LiveClipForTest = new RectD(-19990, -19990, 110, 75);
            pin.Show();
            // A burst that grows past the clip, shrinks inside it and grows again, as a wheel does.
            foreach (double zoom in new[] { 1.1, 1.5, 2, 2.6, .9, 1.2, 2 })
            {
                pin.SetZoom(zoom, live: true);
                Assert.True(pin.DisplayBytes <= 110L * 75 * 4, "A live frame never holds more than the clip.");
            }
            Assert.True(pin.IsDisplayClipped);
            pin.ForceSettle();
            if (settleBeforeClose)
            {
                PinNativeSpeedTestWait.Complete(pin.WaitForSettledAsync());
                // The settled picture holds what the stand-in screen can show, as the live frame did.
                Assert.Equal(pin.IsDisplayClipped, pin.Bounds != pin.DisplayedBounds);
                Assert.True(pin.DisplayBytes <= 110L * 75 * 4, "A settled picture never holds more than the screen.");
                Assert.Equal(pin.DisplayBytes, pin.DisplaySectionBytes);
                Assert.Equal(0, pin.WorkerBufferCount);
            }
        }
        finally
        {
            pin.Close();
            PinNativeSpeedTestWait.Complete(pin.DrainAsync());
        }
        return observed;
    }

    // The reference: the whole live frame, drawn and outlined as a whole, then cut to the view.
    private static byte[] LivePart(BitmapSource picture, int width, int height, NativePinRaster.View view,
        double dpiScale, bool emphasized = false)
    {
        using var source = NativePinPixelSource.FromBitmap(picture);
        using NativePinPixelSource.Lease lease = source.AcquireLease();
        using var whole = new NativePinWindowDib(width, height);
        NativePinLiveRaster.Write(NativePinWindow.LiveMode, lease, whole.Dc, whole.Bits, width, height, whole.Stride);
        _ = NativePinEdgeCache.CaptureAndApply(whole.Bits, width, height, whole.Stride, dpiScale,
            PinNativeTestImages.Outline(AppTheme.Glass, emphasized), Radius);
        byte[] all = whole.CopyPixels(), part = new byte[view.Width * view.Height * 4];
        for (int y = 0; y < view.Height; y++)
            Array.Copy(all, ((y + view.Top) * width + view.Left) * 4, part, y * view.Width * 4, view.Width * 4);
        return part;
    }

    private static void AssertSamePart(byte[] whole, int wholeWidth, byte[] part, NativePinRaster.View view, string state)
    {
        Assert.Equal(view.Width * view.Height * 4, part.Length);
        for (int y = 0; y < view.Height; y++)
        {
            var expected = whole.AsSpan(((y + view.Top) * wholeWidth + view.Left) * 4, view.Width * 4);
            var actual = part.AsSpan(y * view.Width * 4, view.Width * 4);
            int different = expected.CommonPrefixLength(actual);
            Assert.True(different == expected.Length, different == expected.Length ? string.Empty :
                $"{state}: view pixel ({different / 4},{y}) = surface ({different / 4 + view.Left},{y + view.Top}) BGRA[{different % 4}]: whole={expected[different]}, clipped={actual[different]}.");
        }
    }

    private static RectD WindowRectangle(IntPtr handle)
    {
        Assert.True(NativePinWindowInterop.GetWindowRect(handle, out var actual));
        return new RectD(actual.Left, actual.Top, actual.Right - actual.Left, actual.Bottom - actual.Top);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageW(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr process, uint flags);
}
