using System.Windows;
using System.Windows.Input;
using MyCapture.App.Pinning;
using MyCapture.App.Themes;
using MyCapture.Core.Pin;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Platform.Display;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// A wheel or key zoom moves the pin on the screen to the zoom its state takes at once (PinZoomMotion), on
/// both hosts. The motion clock is injected and stepped by hand; no DWM tick, timer or real input is used.
/// </summary>
public sealed class PinZoomMotionTests
{
    private static readonly RectD OffscreenArea = new(-12000, -12000, 1600, 1000);
    private static readonly MonitorInfo Offscreen = new("PINZOOM-OFFSCREEN", OffscreenArea, OffscreenArea, 96, false);

    // ----- WPF host -----

    [Fact]
    public void WheelTakesTheNewZoomAtOnceAndMovesTheWindowToItWithTheAnchorStill() => Run(animations: true, () =>
    {
        double now = 1000;
        var (pin, zooms) = NewWpfPin(() => now);
        try
        {
            var before = pin.PhysicalBounds;
            var anchor = new Point(before.Left + 40, before.Top + 30);
            pin.ResidentProbeWheelAt(120, anchor);

            // The meaning changes now: state, practice signal, readout.
            Assert.Equal(1.1, pin.State.Zoom, 9);
            Assert.Single(zooms);
            Assert.Equal("110%", pin.FeedbackTextForTest);
            Assert.True(pin.IsZoomMoving);
            // The first step is on the screen in the input's own turn, short of the target.
            int width = Width(pin.PhysicalBounds);
            Assert.InRange(width, 161, 175);
            AssertAnchorStill(pin, before, anchor);

            var widths = new List<int> { 160, width };
            for (now = 1010; pin.IsZoomMoving; now += 10)
            {
                Assert.True(now <= 1000 + PinZoomMotionTiming.DurationMs, "the motion outlived its duration");
                pin.TickZoomMotionForTest();
                widths.Add(Width(pin.PhysicalBounds));
                AssertAnchorStill(pin, before, anchor);
            }

            Assert.Equal(widths.Order(), widths);
            Assert.Equal(176, widths[^1]);
            Assert.True(widths.Distinct().Count() >= 5, string.Join(",", widths));
            Assert.Equal(pin.State.WidthDip, pin.Width, 6);
            Assert.Equal(pin.State.HeightDip, pin.Height, 6);
            Assert.Single(zooms);
            // The crisp picture is drawn once, after the motion.
            Assert.True(pin.IsLiveZoomingForTest);
            pin.ForceZoomSettleForTest();
            Assert.False(pin.IsLiveZoomingForTest);
        }
        finally { pin.Close(); }
    });

    [Fact]
    public void WithoutAnimationsTheWheelZoomsAtOnceAsBefore() => Run(animations: false, () =>
    {
        double now = 0;
        var (pin, zooms) = NewWpfPin(() => now);
        try
        {
            var before = pin.PhysicalBounds;
            var anchor = new Point(before.Left + 40, before.Top + 30);
            pin.ResidentProbeWheelAt(120, anchor);

            Assert.False(pin.IsZoomMoving);
            Assert.Equal(176, Width(pin.PhysicalBounds));
            Assert.Equal(pin.State.WidthDip, pin.Width, 6);
            AssertAnchorStill(pin, before, anchor);
            Assert.Single(zooms);
            Assert.True(pin.IsLiveZoomingForTest);
        }
        finally { pin.Close(); }
    });

    [Fact]
    public void QuickNotchesTurnTheRunningMotionAndEndOneDurationAfterTheLast() => Run(animations: true, () =>
    {
        double now = 0;
        var (pin, zooms) = NewWpfPin(() => now);
        try
        {
            var before = pin.PhysicalBounds;
            var anchor = new Point(before.Left + 120, before.Top + 70);
            var widths = new List<int> { Width(before) };
            for (int notch = 0; notch < 5; notch++)
            {
                now = notch * 30;
                pin.ResidentProbeWheelAt(120, anchor);
                widths.Add(Width(pin.PhysicalBounds));
                AssertAnchorStill(pin, before, anchor);
                now += 15;
                pin.TickZoomMotionForTest();
                widths.Add(Width(pin.PhysicalBounds));
            }
            double last = 4 * 30;
            Assert.Equal(Math.Pow(1.1, 5), pin.State.Zoom, 9);
            Assert.Equal(5, zooms.Count);
            for (now = last + 20; pin.IsZoomMoving; now += 20)
            {
                Assert.True(now <= last + PinZoomMotionTiming.DurationMs, "the motion outlived the last notch by more than its duration");
                pin.TickZoomMotionForTest();
                widths.Add(Width(pin.PhysicalBounds));
                AssertAnchorStill(pin, before, anchor);
            }
            Assert.Equal(widths.Order(), widths);
            Assert.Equal((int)Math.Round(160 * Math.Pow(1.1, 5)), widths[^1]);
        }
        finally { pin.Close(); }
    });

    [Fact]
    public void KeysAndResetMoveAroundTheCentreAndTheWheelStopsAtOneHundredPercent() => Run(animations: true, () =>
    {
        double now = 0;
        var (pin, zooms) = NewWpfPin(() => now);
        try
        {
            var before = pin.PhysicalBounds;
            Assert.True(pin.HandleKey(Key.OemPlus, ModifierKeys.None));
            Assert.True(pin.IsZoomMoving);
            Finish(pin, ref now);
            var plus = pin.PhysicalBounds;
            Assert.Equal(Center(before).X, Center(plus).X, 1.0);
            Assert.Equal(Center(before).Y, Center(plus).Y, 1.0);

            Assert.True(pin.HandleKey(Key.D0, ModifierKeys.None));
            Assert.Equal(1, pin.State.Zoom, 9);
            Assert.Equal("100%", pin.FeedbackTextForTest);
            Finish(pin, ref now);
            Assert.Equal(160, Width(pin.PhysicalBounds));
            Assert.Equal(Center(before).X, Center(pin.PhysicalBounds).X, 1.0);

            // From 95% one notch up stops at 100%, and the motion ends there.
            pin.State.SetZoom(.95);
            pin.ResidentProbeSetZoomAt(.95, new Point(before.Left, before.Top));
            pin.ResidentProbeWheelAt(120, new Point(before.Left + 10, before.Top + 10));
            Assert.Equal(1, pin.State.Zoom, 9);
            Finish(pin, ref now);
            Assert.Equal(160, Width(pin.PhysicalBounds));
            Assert.Equal(3, zooms.Count);
        }
        finally { pin.Close(); }
    });

    [Fact]
    public void PartsOfANotchAddUpAndACollapsedPinIgnoresTheWheel() => Run(animations: true, () =>
    {
        double now = 0;
        var (pin, zooms) = NewWpfPin(() => now);
        try
        {
            var anchor = new Point(pin.PhysicalBounds.Left + 5, pin.PhysicalBounds.Top + 5);
            for (int i = 0; i < 3; i++) pin.ResidentProbeWheelAt(30, anchor);
            Assert.Equal(1, pin.State.Zoom, 9);
            Assert.False(pin.IsZoomMoving);
            pin.ResidentProbeWheelAt(30, anchor);
            Assert.Equal(1.1, pin.State.Zoom, 9);
            Assert.True(pin.IsZoomMoving);
            Finish(pin, ref now);

            pin.ToggleCollapsed();
            Assert.True(pin.State.IsCollapsed);
            double collapsed = pin.Width;
            pin.HandleMouseWheel(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, 120) { RoutedEvent = Mouse.MouseWheelEvent },
                ModifierKeys.None);
            Assert.Equal(1.1, pin.State.Zoom, 9);
            Assert.False(pin.IsZoomMoving);
            Assert.Equal(collapsed, pin.Width);
            Assert.Single(zooms);
        }
        finally { pin.Close(); }
    });

    [Fact]
    public void AnotherGestureEndsTheMotionAtItsZoom() => Run(animations: true, () =>
    {
        double now = 0;
        var (pin, _) = NewWpfPin(() => now);
        try
        {
            var before = pin.PhysicalBounds;
            pin.ResidentProbeWheelAt(120, new Point(before.Left, before.Top));
            Assert.True(pin.IsZoomMoving);
            Assert.True(pin.HandleKey(Key.Right, ModifierKeys.None));
            Assert.False(pin.IsZoomMoving);
            Assert.Equal(176, Width(pin.PhysicalBounds));
            Assert.Equal(pin.State.WidthDip, pin.Width, 6);
        }
        finally { pin.Close(); }
    });

    [Fact]
    public void TheDwmClockRunsOnlyWhileTheMotionRuns() => Run(animations: true, () =>
    {
        var (pin, _) = NewWpfPin(clock: null);
        try
        {
            Assert.False(pin.IsZoomClockRunningForTest);
            pin.ResidentProbeWheelAt(120, new Point(pin.PhysicalBounds.Left, pin.PhysicalBounds.Top));
            Assert.True(pin.IsZoomClockRunningForTest);
            pin.FinishZoomMotion();
            Assert.False(pin.IsZoomMoving);
            Assert.False(pin.IsZoomClockRunningForTest);
            Assert.Equal(176, Width(pin.PhysicalBounds));
        }
        finally { pin.Close(); }
    });

    // ----- Native host -----

    [Fact]
    public void NativeWheelMovesTheSurfaceWithTheAnchorStillAndSettlesOnceAfterwards() => Run(animations: true, () =>
    {
        double now = 0;
        var manager = Manager();
        try
        {
            PinWindow pin = manager.PinImage(PinNativeTestImages.Picture(160, 100));
            NativePinWindow native = pin.NativeWindowForTest!;
            native.LiveClipForTest = OffscreenArea;
            native.CompleteTransitions();
            native.ZoomMotionClockForTest = () => now;
            int zooms = 0;
            pin.UserZoomChanged += _ => zooms++;
            RectD before = native.Bounds;
            var anchor = new Point(before.Left + 40, before.Top + 30);

            native.Wheel(120, anchor);
            Assert.Equal(1.1, pin.State.Zoom, 9);
            Assert.Equal(1, zooms);
            Assert.True(native.IsZoomMoving);
            Assert.True(native.IsLiveZooming);
            var widths = new List<double> { before.Width, native.Bounds.Width };
            Assert.InRange(native.Bounds.Width, 161, 175);
            for (now = 10; native.IsZoomMoving; now += 10)
            {
                Assert.True(now <= PinZoomMotionTiming.DurationMs);
                native.TickMotionForTest();
                widths.Add(native.Bounds.Width);
                RectD bounds = native.Bounds;
                double k = bounds.Width / before.Width;
                Assert.InRange(bounds.Left - (anchor.X - (anchor.X - before.Left) * k), -1, 1);
                Assert.InRange(bounds.Top - (anchor.Y - (anchor.Y - before.Top) * k), -1, 1);
            }
            Assert.Equal(widths.Order(), widths);
            Assert.True(widths.Distinct().Count() >= 6, string.Join(",", widths));
            Assert.Equal(176, native.Bounds.Width);
            Assert.Equal(110, native.Bounds.Height);
            Assert.Equal(1, zooms);
            // The settle waits for the motion; it comes once, after it.
            Assert.True(native.IsLiveZooming);
            native.ForceSettle();
            PinNativeSpeedTestWait.Until(() => !native.IsLiveZooming);
        }
        finally { manager.CloseAllForExit(); }
    });

    [Fact]
    public void NativeKeysMoveAndWithoutAnimationsZoomAtOnce() => Run(animations: true, () =>
    {
        double now = 0;
        var manager = Manager();
        try
        {
            PinWindow pin = manager.PinImage(PinNativeTestImages.Picture(160, 100));
            NativePinWindow native = pin.NativeWindowForTest!;
            native.LiveClipForTest = OffscreenArea;
            native.CompleteTransitions();
            native.ZoomMotionClockForTest = () => now;
            RectD before = native.Bounds;

            Assert.True(pin.HandleKey(Key.OemPlus, ModifierKeys.None));
            Assert.True(native.IsZoomMoving);
            Assert.Equal(1.1, pin.State.Zoom, 9);
            now = 1000;
            native.TickMotionForTest();
            Assert.False(native.IsZoomMoving);
            Assert.Equal(before.Left + (before.Width / 2), native.Bounds.Left + (native.Bounds.Width / 2), 1.0);

            Assert.True(pin.HandleKey(Key.D0, ModifierKeys.None));
            Assert.True(native.IsZoomMoving);
            native.FinishZoomMotion();
            Assert.Equal(before.Width, native.Bounds.Width);

            FluidMotion.AnimationsEnabledOverrideForTest = false;
            native.Wheel(120, new Point(before.Left, before.Top));
            Assert.False(native.IsZoomMoving);
            Assert.Equal(176, native.Bounds.Width);
        }
        finally { manager.CloseAllForExit(); }
    });

    [Fact]
    public void ASwitchToWpfDuringTheMotionEndsItAtTheZoom() => Run(animations: true, () =>
    {
        double now = 0;
        var manager = Manager();
        try
        {
            PinWindow pin = manager.PinImage(PinNativeTestImages.Picture(160, 100));
            NativePinWindow native = pin.NativeWindowForTest!;
            native.LiveClipForTest = OffscreenArea;
            native.ZoomMotionClockForTest = () => now;
            pin.ActivateAfterSwitchForTest = static _ => { };
            native.Wheel(120, new Point(native.Bounds.Left, native.Bounds.Top));
            Assert.True(native.IsZoomMoving);

            Assert.True(pin.SwitchToWpf(PinSwitchReason.Command));
            Assert.False(native.IsZoomMoving);
            Assert.False(native.IsLiveZooming);
            PinNativeSpeedTestWait.Until(() => !pin.IsHostSwitchPending);
            Assert.Equal(pin.State.WidthDip, pin.Width, 6);
            Assert.Equal(176, Width(pin.PhysicalBounds));
        }
        finally { manager.CloseAllForExit(); }
    });

    // ----- Helpers -----

    private static (PinWindow Pin, List<Guid> Zooms) NewWpfPin(Func<double>? clock)
    {
        PinContent content = PinContent.FromImage(PinNativeTestImages.Picture(160, 100));
        var pin = new PinWindow(content, new PinViewState(content.LayoutWidthDip, content.LayoutHeightDip, 1, 1, 0.1),
            200, 150, () => new PinSettings { CloseOnDoubleClick = false }) { ShowActivated = false };
        pin.ZoomMotionClockForTest = clock;
        var zooms = new List<Guid>();
        pin.UserZoomChanged += zooms.Add;
        pin.Show();
        pin.UpdateLayout();
        return (pin, zooms);
    }

    private static void Finish(PinWindow pin, ref double now)
    {
        now += 1000;
        pin.TickZoomMotionForTest();
        Assert.False(pin.IsZoomMoving);
    }

    private static int Width((int Left, int Top, int Right, int Bottom) bounds) => bounds.Right - bounds.Left;

    private static Point Center((int Left, int Top, int Right, int Bottom) bounds) =>
        new((bounds.Left + bounds.Right) / 2.0, (bounds.Top + bounds.Bottom) / 2.0);

    // The picture point under the anchor stays under it: each edge is where the zoom puts it, to a pixel.
    private static void AssertAnchorStill(PinWindow pin, (int Left, int Top, int Right, int Bottom) before, Point anchor)
    {
        var now = pin.PhysicalBounds;
        double k = Width(now) / (double)Width(before);
        Assert.InRange(now.Left - (anchor.X - ((anchor.X - before.Left) * k)), -1, 1);
        Assert.InRange(now.Top - (anchor.Y - ((anchor.Y - before.Top) * k)), -1, 1);
        Assert.InRange(now.Bottom - now.Top - ((before.Bottom - before.Top) * k), -1, 1);
    }

    private static void Run(bool animations, Action test) => StaTestHost.Run(() =>
    {
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        Func<IntPtr, double?>? scale = NativePinWindow.ReadWindowScaleForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = animations;
        NativePinWindow.ReadWindowScaleForTest = static _ => null;
        try { test(); }
        finally
        {
            FluidMotion.AnimationsEnabledOverrideForTest = motion;
            NativePinWindow.ReadWindowScaleForTest = scale;
        }
    });

    private static PinManager Manager() =>
        new(() => new PinSettings { LightweightImagePins = true }, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)
        {
            ReadPlacementMonitorForTest = static () => Offscreen,
            ReadRestoreMonitorsForTest = static () => [Offscreen],
            ActivatePinForTest = static _ => { },
        };
}
