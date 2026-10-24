using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Capture;
using MyCapture.App.Themes;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Platform.Display;
using MyCapture.Platform.Shell;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// The captured region lifts off the screen exactly where it was taken and flies into the quick
/// access card's preview (feel bench 1-C), never delaying the card, and steps aside whenever it
/// cannot be drawn correctly.
/// </summary>
public sealed class CaptureFlightTests
{
    private static readonly Rect Source = new(100, 200, 400, 300);
    private static readonly Rect Target = new(700, 650, 160, 120);

    [Fact]
    public void FlightStartsOnTheSourceLandsOnThePreviewAndLiftsMidway()
    {
        CaptureFlightFrame start = CaptureFlight.FlightFrame(Source, Target, 0, 1.5);
        Assert.Equal(Source, start.Image);
        Assert.Equal(CaptureFlight.LiftBlurDip * 1.5, start.ShadowBlur, 6);
        Assert.Equal(CaptureFlight.LiftShadowOpacity, start.ShadowOpacity, 6);

        CaptureFlightFrame end = CaptureFlight.FlightFrame(Source, Target, 1, 1.5);
        Assert.Equal(Target, end.Image);
        // The image has settled into the card: no shadow is left to vanish on arrival.
        Assert.Equal(0, end.ShadowOpacity, 6);

        // Cubic ease-out: halfway in distance about a fifth of the way through in time.
        double midway = 1 - Math.Cbrt(0.5);
        CaptureFlightFrame peak = CaptureFlight.FlightFrame(Source, Target, midway, 1.5);
        Assert.Equal((Source.Left + Target.Left) / 2, peak.Image.Left, 6);
        Assert.Equal(CaptureFlight.PeakBlurDip * 1.5, peak.ShadowBlur, 6);
        Assert.Equal(CaptureFlight.PeakOffsetDip * 1.5, peak.ShadowOffset, 6);
        Assert.Equal(CaptureFlight.PeakShadowOpacity, peak.ShadowOpacity, 6);
        foreach (double progress in new[] { 0.1, 0.3, 0.6, 0.9 })
        {
            CaptureFlightFrame frame = CaptureFlight.FlightFrame(Source, Target, progress, 1);
            Assert.InRange(frame.ShadowBlur, start.ShadowBlur / 1.5, CaptureFlight.PeakBlurDip);
            // One uniform scale: the image keeps its aspect ratio all the way.
            Assert.Equal(Source.Width / Source.Height, frame.Image.Width / frame.Image.Height, 6);
            Assert.Equal(1, frame.Opacity);
        }
    }

    [Fact]
    public void LiftFadesItsShadowInOnTheSourceAndTheFlightTakesOffFromIt()
    {
        // At release the image covers its source exactly and has no shadow yet.
        CaptureFlightFrame released = CaptureFlight.LiftFrame(Source, 0, 1.5);
        Assert.Equal(Source, released.Image);
        Assert.Equal(0, released.ShadowOpacity, 6);
        Assert.Equal(CaptureFlight.LiftBlurDip * 1.5, released.ShadowBlur, 6);
        Assert.Equal(CaptureFlight.LiftOffsetDip * 1.5, released.ShadowOffset, 6);
        Assert.Equal(1, released.Opacity);

        // The shadow fades in over 167 ms, quadratic ease-out, and then rests.
        Assert.Equal(1 - Math.Pow(1 - 0.5, 2), CaptureFlight.LiftLevel(167 / 2.0), 6);
        Assert.Equal(1, CaptureFlight.LiftLevel(167), 6);
        Assert.Equal(1, CaptureFlight.LiftLevel(400), 6);
        Assert.Equal(CaptureFlight.LiftShadowOpacity, CaptureFlight.LiftFrame(Source, 400, 1.5).ShadowOpacity, 6);
        Assert.Equal(0.5, CaptureFlight.LiftFrame(Source, 400, 1.5, opacity: 0.5).Opacity, 6);

        // A flight that starts mid-lift starts from the lift's own picture.
        foreach (double ms in new[] { 0d, 40, 120, 400 })
        {
            CaptureFlightFrame lift = CaptureFlight.LiftFrame(Source, ms, 1.5);
            CaptureFlightFrame takeOff = CaptureFlight.FlightFrame(Source, Target, 0, 1.5, CaptureFlight.LiftLevel(ms));
            Assert.Equal(lift, takeOff);
        }

        // The card has faded in completely before the image lands in it.
        Assert.True(QuickAccessWindow.LandingFadeDuration.TimeSpan < FluidMotion.DeliberateDuration.TimeSpan);
    }

    [Theory]
    [InlineData(true, false, true, true)]
    [InlineData(false, false, true, false)]
    [InlineData(true, true, true, false)]
    [InlineData(true, false, false, false)]
    public void FliesOnlyWithMotionLocallyAndOnLayeredWindows(bool animations, bool remote, bool layered, bool expected) =>
        Assert.Equal(expected, CaptureFlight.IsEligible(animations, remote, layered));

    [Fact]
    public void WindowCoversThePathAndItsShadowWithinTheDisplays()
    {
        MonitorInfo display = Monitor(0, 0, 1920, 1080, 144);
        (RectD bounds, double scale) = CaptureFlight.PlanBounds(Rect.Union(Source, Target), [display])!.Value;
        Assert.Equal(1.5, scale);
        Assert.Equal(new RectD(100 - 72, 200 - 72, 760 + 144, 570 + 144), bounds);

        // A full-screen capture needs no room beyond its display.
        (RectD full, _) = CaptureFlight.PlanBounds(new Rect(0, 0, 1920, 1080), [display])!.Value;
        Assert.Equal(new RectD(0, 0, 1920, 1080), full);
    }

    [Fact]
    public void MixedScalesAndOversizedWindowsDoNotFly()
    {
        MonitorInfo left = Monitor(0, 0, 1920, 1080, 96);
        MonitorInfo right = Monitor(1920, 0, 2560, 1440, 144);
        // A path across displays of different scale cannot be one window's physical pixels.
        Assert.Null(CaptureFlight.PlanBounds(new Rect(1800, 100, 300, 200), [left, right]));
        // Ending exactly at the edge does not cross the next display; the shadow stops at the edge.
        (RectD edge, _) = CaptureFlight.PlanBounds(new Rect(1700, 100, 220, 200), [left, right])!.Value;
        Assert.Equal(1920, edge.Right);

        // A full-screen flight on one 4K display is the largest window; wider paths do not fly.
        MonitorInfo first = Monitor(0, 0, 3840, 2160, 144);
        MonitorInfo second = Monitor(3840, 0, 3840, 2160, 144);
        (RectD whole, _) = CaptureFlight.PlanBounds(new Rect(0, 0, 3840, 2160), [first, second])!.Value;
        Assert.Equal(new RectD(0, 0, 3840, 2160), whole);
        Assert.NotNull(CaptureFlight.PlanBounds(new Rect(3000, 0, 1680, 2160), [first, second]));
        Assert.Null(CaptureFlight.PlanBounds(new Rect(1000, 0, 4000, 2160), [first, second]));
    }

    [Fact]
    public void NoLiftWithoutMotionOrForAnImageThatIsNotTheSourcePixels() => RunFlight(motion: false, (_, _, image, _) =>
    {
        Assert.Null(CaptureFlight.TryLift(image, SourceOn(), () => 0));
        FluidMotion.AnimationsEnabledOverrideForTest = true;
        // A stitched or rescaled image does not look like the screen it came from.
        RectD wrongSize = SourceOn() with { Width = image.PixelWidth + 10 };
        Assert.Null(CaptureFlight.TryLift(image, wrongSize, () => 0));
    });

    [Fact]
    public void LiftsOnItsSourceAndFliesIntoTheLandingCard() => RunFlight(motion: true, (monitor, clock, image, router) =>
    {
        CaptureFlight flight = Assert.IsType<CaptureFlight>(CaptureFlight.TryLift(image, SourceOn(), () => clock.Now));
        try
        {
            CaptureFlightDecoration lift = flight.WindowForTest!;
            // Drawn one to one over the pixels it was cut from, with room for the shadow.
            Assert.Equal(ToRect(SourceOn()), lift.Frame.Image);
            Assert.Equal(0, lift.Frame.ShadowOpacity);
            Assert.Equal(PhysicalBounds(lift), lift.Bounds);
            Assert.True(lift.Bounds.Left < SourceOn().Left && lift.Bounds.Bottom > SourceOn().Bottom);
            AssertPassive(lift);
            clock.Now = 400;
            flight.Tick();
            Assert.Equal(CaptureFlight.LiftShadowOpacity, lift.Frame.ShadowOpacity, 6);

            QuickAccessWindow card = Card(image, monitor, router);
            int closed = 0;
            card.Closed += (_, _) => closed++;
            try
            {
                Assert.True(flight.Land(card));
                Assert.True(card.IsLanding);
                Assert.False(FluidMotion.GetWindowEntrance(card));
                card.Show();
                Assert.True(PumpUntil(() => flight.IsFlying || flight.IsDone));
                Assert.True(flight.IsFlying);
                // The preview lies outside the lift window: a window over the whole path took over
                // once it had drawn the image, and the lift window is gone.
                Assert.NotSame(lift, flight.WindowForTest);
                Assert.True(lift.IsClosed);
                // The card fades in while the image flies; its own preview waits for the image.
                Assert.Equal(1, (double)card.SurfaceForTest.GetAnimationBaseValue(UIElement.OpacityProperty));
                Assert.Equal(0, card.PreviewForTest.Opacity);
                Rect target = card.PreviewScreenBounds!.Value;
                CaptureFlightDecoration flying = flight.WindowForTest!;
                Assert.True(flying.Bounds.Left <= target.Left && flying.Bounds.Right >= target.Right
                    && flying.Bounds.Top <= target.Top && flying.Bounds.Bottom >= target.Bottom);

                double start = clock.Now;
                clock.Now = start + 60;
                flight.Tick();
                Rect midway = flying.Frame.Image;
                Assert.True(midway.Width < ToRect(SourceOn()).Width && midway.Width > target.Width, $"{midway} -> {target}");

                clock.Now = start + 250;
                flight.Tick();
                // It lands exactly on the preview, which shows in that same frame.
                AssertSame(target, flying.Frame.Image);
                Assert.Equal(1, card.PreviewForTest.Opacity);
                Assert.False(flight.IsDone);
                flight.Tick();
                Assert.True(flight.IsDone);
                Assert.True(flying.IsClosed && lift.IsClosed);
                Assert.Equal(0, closed);
                Assert.False(card.IsLanding);
            }
            finally { card.CloseNow(); }
        }
        finally { flight.Finish(); }
    });

    [Fact]
    public void AFullScreenCaptureFliesInItsOwnWindowAboveTheCard() => RunFlight(motion: true, (_, clock, _, router) =>
    {
        // Smaller than any real desktop, which bounds how large a window can be.
        MonitorInfo monitor = Monitor(-32000, -32000, 1200, 800, MonitorEnumerator.GetPrimary().Dpi);
        CaptureFlight.ReadMonitors = () => [monitor];
        BitmapSource screen = Image((int)monitor.Bounds.Width, (int)monitor.Bounds.Height);
        CaptureFlight flight = CaptureFlight.TryLift(screen, monitor.Bounds, () => clock.Now)!;
        QuickAccessWindow card = new(screen, () => Task.FromResult(true), () => { }, () => Task.CompletedTask,
            () => Task.FromResult(true), () => { }, () => Task.FromResult("unused.png"), capturedRegion: monitor.Bounds,
            targetMonitor: monitor, keyboardRouter: router);
        try
        {
            CaptureFlightDecoration lift = flight.WindowForTest!;
            // The display is the whole window: no room is added beyond it.
            Assert.Equal(monitor.Bounds, lift.Bounds);
            flight.Land(card);
            card.Show();
            Assert.True(PumpUntil(() => flight.IsFlying || flight.IsDone));
            // The card lies inside the image's own window, so the image flies there, raised above it.
            Assert.Same(lift, flight.WindowForTest);
            Assert.True(flight.IsFlying);
            clock.Now += 250;
            flight.Tick();
            AssertSame(card.PreviewScreenBounds!.Value, lift.Frame.Image);
            flight.Tick();
            Assert.True(flight.IsDone && lift.IsClosed);
        }
        finally
        {
            flight.Finish();
            card.CloseNow();
        }
    });

    [Fact]
    public void ACardTheImageCannotReachEntersNormallyWhileTheLiftFades() => RunFlight(motion: true, (monitor, clock, image, router) =>
    {
        CaptureFlight flight = CaptureFlight.TryLift(image, SourceOn(), () => clock.Now)!;
        QuickAccessWindow card = Card(image, monitor, router);
        try
        {
            CaptureFlightDecoration lift = flight.WindowForTest!;
            // By the time the card shows, its path crosses no display the flight can draw on.
            CaptureFlight.ReadMonitors = () => [];
            flight.Land(card);
            card.Show();
            Assert.True(PumpUntil(() => !card.IsLanding));
            Assert.True(FluidMotion.GetWindowEntrance(card));
            Assert.Equal(1, card.PreviewForTest.Opacity);
            Assert.False(flight.IsFlying);
            clock.Now += FluidMotion.FastDuration.TimeSpan.TotalMilliseconds;
            flight.Tick();
            Assert.True(flight.IsDone && lift.IsClosed);
            Assert.True(card.IsVisible);
        }
        finally
        {
            flight.Finish();
            card.CloseNow();
        }
    });

    [Fact]
    public void WithoutACardTheLiftedImageFadesAwayAndANewCaptureEndsItAtOnce() => RunFlight(motion: true, (_, clock, image, _) =>
    {
        CaptureFlight flight = CaptureFlight.TryLift(image, SourceOn(), () => clock.Now)!;
        CaptureFlightDecoration lift = flight.WindowForTest!;
        clock.Now = 300;
        flight.Cancel();
        Assert.False(flight.IsDone);
        clock.Now = 340;
        flight.Tick();
        Assert.InRange(lift.Frame.Opacity, 0.01, 0.99);
        clock.Now = 300 + FluidMotion.FastDuration.TimeSpan.TotalMilliseconds;
        flight.Tick();
        Assert.True(flight.IsDone);
        Assert.True(lift.IsClosed);

        CaptureFlight next = CaptureFlight.TryLift(image, SourceOn(), () => clock.Now)!;
        CaptureFlightDecoration nextLift = next.WindowForTest!;
        next.Finish();
        Assert.True(next.IsDone && nextLift.IsClosed);
        // A card that arrives after the image is gone shows with its usual entrance.
        QuickAccessWindow late = Card(image, null, null);
        try
        {
            Assert.False(next.Land(late));
            Assert.False(late.IsLanding);
            Assert.True(FluidMotion.GetWindowEntrance(late));
        }
        finally { late.Close(); }
    });

    [Fact]
    public void ACardClosingMidFlightEndsTheFlight() => RunFlight(motion: true, (monitor, clock, image, router) =>
    {
        CaptureFlight flight = CaptureFlight.TryLift(image, SourceOn(), () => clock.Now)!;
        QuickAccessWindow card = Card(image, monitor, router);
        try
        {
            flight.Land(card);
            card.Show();
            Assert.True(PumpUntil(() => flight.IsFlying || flight.IsDone));
            CaptureFlightDecoration flying = flight.WindowForTest!;
            card.RequestClose();
            Assert.True(FluidMotion.IsExiting(card));
            // The card leaves with its preview, and the image does not fly into a closing card.
            Assert.Equal(1, card.PreviewForTest.Opacity);
            flight.Tick();
            Assert.True(flight.IsDone && flying.IsClosed);
        }
        finally
        {
            flight.Finish();
            card.CloseNow();
        }
    });

    private static void AssertPassive(Window window)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        Assert.True(WindowStyleFacade.IsClickThrough(handle));
        Assert.False(window.ShowActivated);
        Assert.False(window.IsHitTestVisible);
        Assert.True(window.Topmost);
    }

    private static RectD PhysicalBounds(Window window)
    {
        (int left, int top, int right, int bottom) = WindowStyleFacade.GetWindowBounds(new WindowInteropHelper(window).Handle);
        return new RectD(left, top, right - left, bottom - top);
    }

    private static Rect ToRect(RectD rect) => new(rect.Left, rect.Top, rect.Width, rect.Height);

    private static void AssertSame(Rect expected, Rect actual)
    {
        Assert.Equal(expected.Left, actual.Left, 6);
        Assert.Equal(expected.Top, actual.Top, 6);
        Assert.Equal(expected.Width, actual.Width, 6);
        Assert.Equal(expected.Height, actual.Height, 6);
    }

    // Off the visible desktop, on a test display the flight treats as real. Larger than the
    // card's preview at any scale, so the image shrinks on its way.
    private static RectD SourceOn() => new(-31600, -31700, 800, 500);

    private static MonitorInfo Monitor(double left, double top, double width, double height, uint dpi) =>
        new("TEST" + left, new RectD(left, top, width, height), new RectD(left, top, width, height - 40), dpi, false);

    private static QuickAccessWindow Card(BitmapSource image, MonitorInfo? monitor, ReceiptKeyboardRouter? router) =>
        new(image, () => Task.FromResult(true), () => { }, () => Task.CompletedTask, () => Task.FromResult(true), () => { },
            () => Task.FromResult("unused.png"), capturedRegion: SourceOn(), targetMonitor: monitor, keyboardRouter: router);

    private static bool PumpUntil(Func<bool> done)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!done() && DateTime.UtcNow < deadline)
        {
            var frame = new DispatcherFrame();
            var tick = new DispatcherTimer(TimeSpan.FromMilliseconds(20), DispatcherPriority.Background,
                (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
            Dispatcher.PushFrame(frame);
            tick.Stop();
        }
        return done();
    }

    private sealed class ManualClock
    {
        internal double Now { get; set; }
    }

    private sealed class NoHotkeys : IHotkeyRegistrar
    {
        public bool TryRegister(int id, Hotkey hotkey, out int errorCode, out string errorMessage)
        {
            errorCode = 0;
            errorMessage = string.Empty;
            return true;
        }

        public void Unregister(int id)
        {
        }
    }

    private static void RunFlight(bool motion, Action<MonitorInfo, ManualClock, BitmapSource, ReceiptKeyboardRouter> body) =>
        StaTestHost.Run(() =>
        {
            bool? previousMotion = FluidMotion.AnimationsEnabledOverrideForTest;
            Func<bool> previousRemote = CaptureFlight.ReadRemoteSession;
            Func<bool> previousLayered = CaptureFlight.ReadLayeredSupport;
            Func<IReadOnlyList<MonitorInfo>> previousMonitors = CaptureFlight.ReadMonitors;
            // A display far off the visible desktop, at the primary display's scale.
            MonitorInfo display = Monitor(-32000, -32000, 2400, 1600, MonitorEnumerator.GetPrimary().Dpi);
            using var router = new ReceiptKeyboardRouter(_ => new NoHotkeys());
            try
            {
                FluidMotion.AnimationsEnabledOverrideForTest = motion;
                CaptureFlight.ReadRemoteSession = () => false;
                CaptureFlight.ReadLayeredSupport = () => true;
                CaptureFlight.ReadMonitors = () => [display];
                body(display, new ManualClock(), Image(800, 500), router);
            }
            finally
            {
                FluidMotion.AnimationsEnabledOverrideForTest = previousMotion;
                CaptureFlight.ReadRemoteSession = previousRemote;
                CaptureFlight.ReadLayeredSupport = previousLayered;
                CaptureFlight.ReadMonitors = previousMonitors;
            }
        });

    private static BitmapSource Image(int width, int height)
    {
        byte[] pixels = Enumerable.Repeat((byte)180, width * height * 4).ToArray();
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        image.Freeze();
        return image;
    }
}
