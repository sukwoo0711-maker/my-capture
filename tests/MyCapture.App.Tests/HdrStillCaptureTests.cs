using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Diagnostics;
using MyCapture.Core.Capture;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using MyCapture.Platform.Interop;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

/// <summary>
/// The HDR correction of still captures on a real desktop: display detection, the forced scRGB
/// read of an owned window (one display, two displays, a rotated display where the PC has one),
/// the untouched GDI copy without HDR, the kept D3D device, and the fallback when a read is late.
/// </summary>
/// <remarks>
/// The read needs a desktop that DXGI can duplicate. Where it cannot (a session without a GPU
/// output), the engine keeps the GDI copy and these tests check that instead; set
/// <c>MYCAPTURE_REQUIRE_HDR_READ=1</c> to make a missing read a failure.
/// </remarks>
public sealed class HdrStillCaptureTests(ITestOutputHelper output)
{
    private const string RequireReadVariable = "MYCAPTURE_REQUIRE_HDR_READ";
    private const int RegionWidth = 320, RegionHeight = 240, Margin = 10;

    private static ScreenCaptureEngine Engine(HdrCaptureMode mode) =>
        new(NullLogger<ScreenCaptureEngine>.Instance) { HdrMode = mode };

    /// <summary>
    /// A forced capture whose read was given the time it needs. The first read of a process loads
    /// DXGI and the display driver and creates the D3D device, which can outlast one capture's
    /// budget on a busy PC; that capture keeps the GDI copy, the read goes on and leaves the device,
    /// and the capture is taken again. What a late read does to its own capture has its own test.
    /// </summary>
    private (BitmapSource Bitmap, HdrCaptureReport Report) CaptureSettled(
        ScreenCaptureEngine engine, RectD region, bool includeCursor = false)
    {
        for (int attempt = 1; ; attempt++)
        {
            BitmapSource bitmap = engine.CaptureRegion(region, includeCursor, out HdrCaptureReport? report);
            Assert.NotNull(report);
            foreach (HdrOutputRead read in report.Outputs)
            {
                output.WriteLine(
                    $"attempt {attempt}: {read.DeviceName} {read.Outcome} {read.Detail}; waited {report.ReadMs:0.0}ms; device {read.DeviceMs:0.0}ms " +
                    $"({(read.DeviceReused ? "kept" : "created")}), duplicate {read.DuplicateMs:0.0}ms, frame {read.AcquireMs:0.0}ms, copy {read.CopyMs:0.0}ms, convert {read.ConvertMs:0.0}ms");
            }

            if (attempt == 5 || report.Outputs.All(read => read.Outcome != HdrReadOutcome.TimedOut))
            {
                return (bitmap, report);
            }

            _ = WaitUntil(() =>
            {
                if (ScreenCaptureEngine.TakeLateHdrRead() is not { } late) return false;
                foreach (HdrOutputRead read in late)
                {
                    output.WriteLine(
                        $"the late read ended: {read.DeviceName} {read.Outcome}; device {read.DeviceMs:0.0}ms, duplicate {read.DuplicateMs:0.0}ms, " +
                        $"frame {read.AcquireMs:0.0}ms, copy {read.CopyMs:0.0}ms, convert {read.ConvertMs:0.0}ms");
                }

                return true;
            }, TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void DisplayStates_NameEachSourceOnce_AndCarryAUsableWhiteLevel()
    {
        IReadOnlyList<HdrDisplayState> displays = HdrDisplayDetector.Detect();

        Assert.Equal(displays.Count, displays.Select(display => display.DeviceName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(displays, display =>
        {
            Assert.StartsWith(@"\\.\", display.DeviceName, StringComparison.Ordinal);
            Assert.InRange(display.SdrWhiteNits, 1.0, 100000.0);
            if (display.ActiveColorMode is { } mode) Assert.Equal(mode == 2, display.IsHdrActive);
            else Assert.Equal(display.AdvancedColorEnabled, display.IsHdrActive);
        });
        // The same answer twice: nothing is cached, and nothing is left behind by a pass.
        Assert.Equal(displays, HdrDisplayDetector.Detect());
    }

    [Theory]
    [InlineData(false, null, false)]
    [InlineData(true, null, true)]
    [InlineData(true, 0, false)]
    [InlineData(true, 1, false)]
    [InlineData(true, 2, true)]
    [InlineData(false, 2, true)]
    public void HdrIsTheActiveColourModeWhereItIsReported_AndAdvancedColourElsewhere(bool enabled, int? activeMode, bool expected)
    {
        var state = new HdrDisplayState(@"\\.\DISPLAY1", true, enabled, false, false, activeMode, 0, 10, 2500);

        Assert.Equal(expected, state.IsHdrActive);
        Assert.Equal(200.0, state.SdrWhiteNits);
        Assert.Equal(80.0, (state with { SdrWhiteLevel = null }).SdrWhiteNits);
        Assert.Equal(80.0, (state with { SdrWhiteLevel = 1000 }).SdrWhiteNits);
        Assert.Equal(480.0, (state with { SdrWhiteLevel = 6000 }).SdrWhiteNits);
    }

    [Fact]
    public void ForcedFloatRead_OfAKnownWindow_ReturnsItsPixels() => StaTestHost.Run(() =>
    {
        using var fixture = PatternWindow.OnPrimaryDisplay();
        fixture.WaitUntilShown();
        ScreenCaptureEngine engine = Engine(HdrCaptureMode.ForceFloatRead);

        (BitmapSource bitmap, HdrCaptureReport report) = CaptureSettled(engine, fixture.Region);

        Assert.True(bitmap.IsFrozen);
        Assert.Equal(HdrCaptureMode.ForceFloatRead, report.Mode);
        HdrOutputRead read = Assert.Single(report.Outputs);
        if (!ReadApplied(read))
        {
            fixture.AssertShows(bitmap, tolerance: 0);
            return;
        }

        Assert.Equal(10u, read.SurfaceFormat);
        Assert.Equal(new RectD(fixture.Region.Left, fixture.Region.Top, RegionWidth, RegionHeight), read.ScreenBounds);
        Assert.Equal(0, read.PixelsAboveSdrWhite);
        Assert.True(report.AnyApplied);
        // An SDR desktop read in scRGB and converted is the desktop GDI copies, within one code value.
        fixture.AssertShows(bitmap, tolerance: 1);
    });

    [Fact]
    public void AutomaticAndOffModes_OnADisplayWithoutHdr_ReturnTheGdiCopyAndReportNothing() => StaTestHost.Run(() =>
    {
        if (HdrDisplayDetector.Detect().Any(display => display.IsHdrActive))
        {
            // On a PC with HDR on, the automatic mode corrects; that is the owner's check with the self-test.
            return;
        }

        using var fixture = PatternWindow.OnPrimaryDisplay();
        fixture.WaitUntilShown();

        BitmapSource off = Engine(HdrCaptureMode.Off).CaptureRegion(fixture.Region, includeCursor: false, out HdrCaptureReport? offReport);
        BitmapSource auto = Engine(HdrCaptureMode.Auto).CaptureRegion(fixture.Region, includeCursor: false, out HdrCaptureReport? autoReport);

        Assert.Null(offReport);
        Assert.Null(autoReport);
        fixture.AssertShows(off, tolerance: 0);
        Assert.Equal(PatternWindow.Pixels(off), PatternWindow.Pixels(auto));
    });

    [Fact]
    public void ForcedFloatRead_AcrossTwoDisplays_TakesEachPartFromItsOwnDisplay() => StaTestHost.Run(() =>
    {
        RectD? shared = HdrCaptureSelfTest.FindSpanningRectangle(MonitorEnumerator.GetAll());
        if (shared is not { Width: RegionWidth, Height: RegionHeight } rectangle)
        {
            // One display, or no edge with room for the fixture.
            return;
        }

        using var fixture = new PatternWindow((int)rectangle.Left - Margin, (int)rectangle.Top - Margin);
        fixture.WaitUntilShown();

        (BitmapSource bitmap, HdrCaptureReport report) = CaptureSettled(Engine(HdrCaptureMode.ForceFloatRead), fixture.Region);

        Assert.Equal(2, report.Outputs.Count);
        if (!report.Outputs.All(ReadApplied))
        {
            fixture.AssertShows(bitmap, tolerance: report.AnyApplied ? 1 : 0);
            return;
        }

        // The two parts tile the rectangle; a rotated display delivers a rotated surface, which
        // the asymmetric pattern would expose as a mismatch.
        Assert.Equal(RegionWidth * RegionHeight, report.Outputs.Sum(read => (int)(read.ScreenBounds.Width * read.ScreenBounds.Height)));
        Assert.Equal(2, report.Outputs.Select(read => read.DeviceName).Distinct().Count());
        fixture.AssertShows(bitmap, tolerance: 1);
    });

    [Fact]
    public void ForcedFloatRead_LeavesOutAWindowThatIsExcludedFromCapture() => StaTestHost.Run(() =>
    {
        using var fixture = PatternWindow.OnPrimaryDisplay();
        fixture.WaitUntilShown();
        if (!fixture.ExcludeFromCapture())
        {
            // Windows before 10 version 2004 has no capture exclusion.
            return;
        }

        ScreenCaptureEngine gdi = Engine(HdrCaptureMode.Off);
        const int pixels = RegionWidth * RegionHeight;
        // The app's own recording controls, cards and selector rely on this for the GDI copy. A
        // desktop whose GDI copy keeps showing the window has nothing to hold the scRGB read against.
        if (!PumpUntil(() => PatternWindow.Mismatches(gdi.CaptureRegion(fixture.Region, includeCursor: false), tolerance: 1) > pixels * 9 / 10,
            TimeSpan.FromSeconds(5)))
        {
            Assert.False(Environment.GetEnvironmentVariable(RequireReadVariable) == "1", "The GDI copy still shows the excluded window.");
            return;
        }

        byte[] before = PatternWindow.Pixels(gdi.CaptureRegion(fixture.Region, includeCursor: false));
        (BitmapSource bitmap, HdrCaptureReport report) = CaptureSettled(Engine(HdrCaptureMode.ForceFloatRead), fixture.Region);
        byte[] after = PatternWindow.Pixels(gdi.CaptureRegion(fixture.Region, includeCursor: false));

        bool applied = ReadApplied(Assert.Single(report.Outputs));
        // The scRGB read leaves the window out as well: nothing of its pattern, and what lies behind
        // it is what GDI copied, wherever that held still between the two GDI copies.
        Assert.True(PatternWindow.Mismatches(bitmap, tolerance: 1) > pixels * 9 / 10, "The scRGB read shows the excluded window.");
        HdrPixelComparison comparison = HdrCaptureSelfTest.Compare(before, after, PatternWindow.Pixels(bitmap), RegionWidth, RegionHeight);
        output.WriteLine($"applied {applied}; {comparison}");
        if (comparison.StablePixels >= pixels / 2)
        {
            Assert.True(comparison.ShareWithinOne >= HdrCaptureSelfTest.RequiredShareWithinOne,
                $"Only {comparison.ShareWithinOne:P3} of the stable samples behind the excluded window match the GDI copy.");
        }
    });

    [Fact]
    public void ForcedFloatRead_KeepsItsDeviceForTheNextCapture_AndReleasesItWhenIdle() => StaTestHost.Run(() =>
    {
        using var fixture = PatternWindow.OnPrimaryDisplay();
        fixture.WaitUntilShown();
        ScreenCaptureEngine engine = Engine(HdrCaptureMode.ForceFloatRead);
        TimeSpan idleRelease = HdrDesktopReader.DeviceIdleRelease;
        try
        {
            Assert.True(WaitUntil(ScreenCaptureEngine.ReleaseHdrDevice, TimeSpan.FromSeconds(15)));
            Assert.False(ScreenCaptureEngine.HoldsHdrDevice);

            (_, HdrCaptureReport first) = CaptureSettled(engine, fixture.Region);
            if (!ReadApplied(Assert.Single(first.Outputs)))
            {
                return;
            }

            Assert.True(ScreenCaptureEngine.HoldsHdrDevice);
            BitmapSource bitmap = engine.CaptureRegion(fixture.Region, includeCursor: false, out HdrCaptureReport? second);

            Assert.True(Assert.Single(second!.Outputs).DeviceReused);
            Assert.True(ScreenCaptureEngine.HoldsHdrDevice);
            fixture.AssertShows(bitmap, tolerance: 1);

            HdrDesktopReader.DeviceIdleRelease = TimeSpan.FromMilliseconds(250);
            _ = engine.CaptureRegion(fixture.Region, includeCursor: false, out _);
            Assert.True(WaitUntil(() => !ScreenCaptureEngine.HoldsHdrDevice, TimeSpan.FromSeconds(10)),
                "The idle D3D device was not released.");

            // A released device is no failure: the next capture creates one.
            (bitmap, HdrCaptureReport afterRelease) = CaptureSettled(engine, fixture.Region);
            Assert.True(Assert.Single(afterRelease.Outputs).Applied);
            Assert.True(ScreenCaptureEngine.HoldsHdrDevice);
            fixture.AssertShows(bitmap, tolerance: 1);
        }
        finally
        {
            HdrDesktopReader.DeviceIdleRelease = idleRelease;
        }
    });

    [Fact]
    public void AReadThatOutlastsItsBudget_LeavesTheGdiCopy_AndLaterCapturesReadAgain() => StaTestHost.Run(() =>
    {
        using var fixture = PatternWindow.OnPrimaryDisplay();
        fixture.WaitUntilShown();
        ScreenCaptureEngine engine = Engine(HdrCaptureMode.ForceFloatRead);
        (_, HdrCaptureReport baseline) = CaptureSettled(engine, fixture.Region);
        bool canRead = ReadApplied(Assert.Single(baseline.Outputs));
        int reads = 0;
        // Only the first read stalls, far longer than any capture may wait.
        HdrDesktopReader.BeforeReadForTest = () =>
        {
            if (Interlocked.Increment(ref reads) == 1) Thread.Sleep(2500);
        };
        try
        {
            long started = Stopwatch.GetTimestamp();
            BitmapSource late = engine.CaptureRegion(fixture.Region, includeCursor: false, out HdrCaptureReport? lateReport);
            double waited = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            Assert.Equal(HdrReadOutcome.TimedOut, Assert.Single(lateReport!.Outputs).Outcome);
            Assert.False(lateReport.AnyApplied);
            // With the device from the baseline read the capture waits the ordinary budget; where
            // this desktop cannot be read there is no device, and it waits the first-read budget.
            TimeSpan budget = canRead ? HdrStillCapture.ReadBudget : HdrStillCapture.FirstReadBudget;
            Assert.InRange(waited, budget.TotalMilliseconds - 50, 2400);
            fixture.AssertShows(late, tolerance: 0);

            // The stalled read still owns the device: this capture gives up after its own budget
            // instead of queueing behind it or starting a second read.
            BitmapSource busy = engine.CaptureRegion(fixture.Region, includeCursor: false, out HdrCaptureReport? busyReport);
            Assert.Equal(HdrReadOutcome.TimedOut, Assert.Single(busyReport!.Outputs).Outcome);
            fixture.AssertShows(busy, tolerance: 0);
            Assert.Equal(1, Volatile.Read(ref reads));

            Assert.True(WaitUntil(ScreenCaptureEngine.ReleaseHdrDevice, TimeSpan.FromSeconds(15)), "The stalled read never ended.");
            // What the stalled read reported when it ended is kept for whoever diagnoses it.
            IReadOnlyList<HdrOutputRead>? stalled = ScreenCaptureEngine.TakeLateHdrRead();
            Assert.Equal(HdrReadOutcome.TimedOut, Assert.Single(stalled!).Outcome);
            Assert.Null(ScreenCaptureEngine.TakeLateHdrRead());

            (BitmapSource again, HdrCaptureReport againReport) = CaptureSettled(engine, fixture.Region);
            Assert.Equal(canRead, Assert.Single(againReport.Outputs).Applied);
            fixture.AssertShows(again, tolerance: canRead ? 1 : 0);
        }
        finally
        {
            HdrDesktopReader.BeforeReadForTest = null;
        }
    });

    [Fact]
    public void PrewarmAndCursorCaptures_WorkWithTheFloatReadForced() => StaTestHost.Run(() =>
    {
        using var fixture = PatternWindow.OnPrimaryDisplay();
        fixture.WaitUntilShown();
        ScreenCaptureEngine engine = Engine(HdrCaptureMode.ForceFloatRead);

        Assert.True(engine.Prewarm() >= 0);
        BitmapSource withCursor = engine.CaptureRegion(fixture.Region, includeCursor: true, out HdrCaptureReport? report);
        FrozenFrame monitor = engine.CaptureMonitor(MonitorEnumerator.GetPrimary(), includeCursor: false);

        Assert.NotNull(report);
        Assert.True(withCursor.IsFrozen);
        Assert.Equal(RegionWidth, withCursor.PixelWidth);
        Assert.Equal(RegionHeight, withCursor.PixelHeight);
        Assert.Equal(MonitorEnumerator.GetPrimary().PixelWidth, monitor.PixelWidth);
        Assert.Equal(MonitorEnumerator.GetPrimary().PixelHeight, monitor.PixelHeight);
    });

    [Fact]
    public void SelfTestComparison_LeavesOutPixelsThatChangedBetweenTheTwoGdiPictures()
    {
        // Four pixels in a row: equal, one off in one channel, six off, and one that moved.
        byte[] before = [10, 20, 30, 0, 10, 20, 30, 0, 10, 20, 30, 0, 10, 20, 30, 0];
        byte[] after = [10, 20, 30, 9, 10, 20, 30, 0, 10, 20, 30, 0, 11, 20, 30, 0];
        byte[] converted = [10, 20, 30, 255, 10, 21, 30, 255, 16, 20, 28, 255, 200, 200, 200, 255];

        HdrPixelComparison comparison = HdrCaptureSelfTest.Compare(before, after, converted, 4, 1);

        Assert.Equal(3, comparison.StablePixels);
        Assert.Equal(1, comparison.UnstablePixels);
        Assert.Equal(6, comparison.Equal);
        Assert.Equal(1, comparison.OffByOne);
        Assert.Equal(1, comparison.OffByTwo);
        Assert.Equal(0, comparison.OffBy3To5);
        Assert.Equal(1, comparison.OffBy6To15);
        Assert.Equal(0, comparison.OffBy16OrMore);
        Assert.Equal(6, comparison.LargestDifference);
        Assert.Equal(7.0 / 9.0, comparison.ShareWithinOne, 9);
        Assert.Equal("(2,0)-(2,0)", comparison.OutlierBounds);
        Assert.Throws<ArgumentException>(() => HdrCaptureSelfTest.Compare(before, after, converted, 5, 1));

        // A picture in which everything moved has nothing to compare, which is not a mismatch.
        byte[] moved = [.. before.Select(value => (byte)(value + 1))];
        HdrPixelComparison nothing = HdrCaptureSelfTest.Compare(before, moved, converted, 4, 1);
        Assert.Equal(0, nothing.StablePixels);
        Assert.Equal(4, nothing.UnstablePixels);
        Assert.Equal(1.0, nothing.ShareWithinOne);
        Assert.Null(nothing.OutlierBounds);
    }

    [Fact]
    public void SpanningRectangle_IsCentredOnTheFirstSharedEdge()
    {
        static MonitorInfo Monitor(string name, double x, double y, double width, double height) =>
            new(name, new RectD(x, y, width, height), new RectD(x, y, width, height), 96, x == 0 && y == 0);

        Assert.Null(HdrCaptureSelfTest.FindSpanningRectangle([Monitor("A", 0, 0, 1920, 1080)]));
        Assert.Equal(new RectD(1760, 420, 320, 240), HdrCaptureSelfTest.FindSpanningRectangle(
            [Monitor("A", 0, 0, 1920, 1080), Monitor("B", 1920, -200, 1080, 1920)]));
        Assert.Equal(new RectD(800, 960, 320, 240), HdrCaptureSelfTest.FindSpanningRectangle(
            [Monitor("A", 0, 0, 1920, 1080), Monitor("B", 0, 1080, 1920, 1080)]));
        // Displays that only touch at a corner share no edge to span.
        Assert.Null(HdrCaptureSelfTest.FindSpanningRectangle(
            [Monitor("A", 0, 0, 1920, 1080), Monitor("B", 1920, 1080, 1920, 1080)]));
    }

    /// <summary>
    /// True when the display was read in scRGB. A desktop that cannot be duplicated keeps the GDI
    /// copy, which the caller then checks; with <see cref="RequireReadVariable"/> set that is a failure.
    /// </summary>
    private static bool ReadApplied(HdrOutputRead read)
    {
        if (read.Applied)
        {
            Assert.Null(read.Detail);
            return true;
        }

        Assert.False(string.IsNullOrWhiteSpace(read.Detail));
        if (Environment.GetEnvironmentVariable(RequireReadVariable) == "1")
        {
            Assert.Fail($"The scRGB read of {read.DeviceName} was required but ended as {read.Outcome}: {read.Detail}");
        }

        return false;
    }

    /// <summary>Like <see cref="WaitUntil"/>, but lets the fixture window's thread process its messages.</summary>
    private static bool PumpUntil(Func<bool> condition, TimeSpan timeout)
    {
        long deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (!condition())
        {
            if (Stopwatch.GetTimestamp() >= deadline)
            {
                return false;
            }

            SyntheticCaptureFixture.Pump(20);
        }

        return true;
    }

    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        long deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (!condition())
        {
            if (Stopwatch.GetTimestamp() >= deadline)
            {
                return false;
            }

            Thread.Sleep(50);
        }

        return true;
    }

    /// <summary>
    /// A topmost, never-activated window showing a bitmap whose every pixel depends on both of its
    /// coordinates, so a shifted, mirrored or rotated read cannot pass for the right one.
    /// </summary>
    private sealed class PatternWindow : IDisposable
    {
        private const int WindowWidth = RegionWidth + (2 * Margin), WindowHeight = RegionHeight + (2 * Margin);
        private readonly IntPtr _window;
        private readonly IntPtr _bitmap;
        private readonly int _left, _top;

        internal PatternWindow(int left, int top)
        {
            _left = left;
            _top = top;
            byte[] pixels = new byte[WindowWidth * WindowHeight * 4];
            for (int y = 0; y < WindowHeight; y++)
            for (int x = 0; x < WindowWidth; x++)
            {
                (byte blue, byte green, byte red) = Colour(x, y);
                int offset = ((y * WindowWidth) + x) * 4;
                pixels[offset] = blue;
                pixels[offset + 1] = green;
                pixels[offset + 2] = red;
            }

            IntPtr desktop = NativeMethods.GetDesktopWindow();
            IntPtr screen = NativeMethods.GetWindowDC(desktop);
            try
            {
                _bitmap = NativeMethods.CreateCompatibleBitmap(screen, WindowWidth, WindowHeight);
                Assert.NotEqual(IntPtr.Zero, _bitmap);
                var info = new NativeMethods.BITMAPINFO
                {
                    bmiHeader = new NativeMethods.BITMAPINFOHEADER
                    {
                        biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                        biWidth = WindowWidth,
                        biHeight = -WindowHeight,
                        biPlanes = 1,
                        biBitCount = 32,
                    },
                };
                Assert.Equal(WindowHeight, SetDIBits(screen, _bitmap, 0, WindowHeight, pixels, ref info, 0));
            }
            finally
            {
                NativeMethods.ReleaseDC(desktop, screen);
            }

            // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST; WS_POPUP | WS_VISIBLE | SS_BITMAP.
            _window = CreateWindowEx(0x08000088, "STATIC", "HDR capture fixture", 0x9000000E,
                left, top, WindowWidth, WindowHeight, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (_window == IntPtr.Zero)
            {
                NativeMethods.DeleteObject(_bitmap);
                Assert.Fail("The fixture window could not be created.");
            }

            SendMessage(_window, 0x0172, IntPtr.Zero, _bitmap); // STM_SETIMAGE, IMAGE_BITMAP
            UpdateWindow(_window);
        }

        /// <summary>The window's interior, in virtual-desktop pixels.</summary>
        internal RectD Region => new(_left + Margin, _top + Margin, RegionWidth, RegionHeight);

        internal static PatternWindow OnPrimaryDisplay()
        {
            RectD bounds = MonitorEnumerator.GetPrimary().Bounds;
            return new PatternWindow((int)bounds.Left + 100, (int)bounds.Top + 100);
        }

        internal static (byte Blue, byte Green, byte Red) Colour(int x, int y) =>
            ((byte)((x * 7) + (y * 3)), (byte)((y * 5) + 40), (byte)((x * 2) + 90));

        internal static byte[] Pixels(BitmapSource bitmap)
        {
            byte[] pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
            bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            for (int offset = 3; offset < pixels.Length; offset += 4) pixels[offset] = 0;
            return pixels;
        }

        /// <summary>Waits until the plain GDI copy of the region is the pattern.</summary>
        internal void WaitUntilShown()
        {
            ScreenCaptureEngine gdi = Engine(HdrCaptureMode.Off);
            long deadline = Stopwatch.GetTimestamp() + (5 * Stopwatch.Frequency);
            int mismatches;
            do
            {
                mismatches = Mismatches(gdi.CaptureRegion(Region, includeCursor: false), tolerance: 0);
                if (mismatches == 0)
                {
                    return;
                }

                // Creating a window does not synchronise with composition.
                SyntheticCaptureFixture.Pump(10);
            }
            while (Stopwatch.GetTimestamp() < deadline);

            Assert.Fail($"The fixture window did not reach the screen within five seconds ({mismatches} pixels differ).");
        }

        internal void AssertShows(BitmapSource bitmap, int tolerance)
        {
            Assert.Equal(RegionWidth, bitmap.PixelWidth);
            Assert.Equal(RegionHeight, bitmap.PixelHeight);
            int mismatches = Mismatches(bitmap, tolerance);
            Assert.True(mismatches == 0, $"{mismatches} of {RegionWidth * RegionHeight} pixels differ from the fixture by more than {tolerance}.");
        }

        /// <summary>Asks DWM to leave the window out of every screen capture (WDA_EXCLUDEFROMCAPTURE).</summary>
        internal bool ExcludeFromCapture() => SetWindowDisplayAffinity(_window, 0x11);

        /// <summary>Pixels of a capture of the region that are not the fixture's pattern.</summary>
        internal static int Mismatches(BitmapSource bitmap, int tolerance)
        {
            if (bitmap.PixelWidth != RegionWidth || bitmap.PixelHeight != RegionHeight)
            {
                return RegionWidth * RegionHeight;
            }

            byte[] pixels = Pixels(bitmap);
            int mismatches = 0;
            for (int y = 0; y < RegionHeight; y++)
            for (int x = 0; x < RegionWidth; x++)
            {
                (byte blue, byte green, byte red) = Colour(x + Margin, y + Margin);
                int offset = ((y * RegionWidth) + x) * 4;
                if (Math.Abs(pixels[offset] - blue) > tolerance || Math.Abs(pixels[offset + 1] - green) > tolerance
                    || Math.Abs(pixels[offset + 2] - red) > tolerance)
                {
                    mismatches++;
                }
            }

            return mismatches;
        }

        public void Dispose()
        {
            try
            {
                Assert.True(DestroyWindow(_window));
            }
            finally
            {
                NativeMethods.DeleteObject(_bitmap);
            }
        }
    }

    [DllImport("gdi32.dll")]
    private static extern int SetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, byte[] bits, ref NativeMethods.BITMAPINFO info, uint usage);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string title, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);
}
