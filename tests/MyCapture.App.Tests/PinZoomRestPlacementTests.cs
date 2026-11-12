using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using MyCapture.App.Pinning;
using MyCapture.App.Themes;
using MyCapture.Core.Pin;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// A WPF pin's zoom ends on the pixels WPF itself gives its final Left, Top, Width and Height, so syncing those
/// properties moves the window no further: one placement without animations, none after a motion's last tick.
/// The pin starts on a fractional Top so a relative rounding would land a pixel off WPF's.
/// </summary>
public sealed class PinZoomRestPlacementTests
{
    [Fact]
    public void WithoutAnimationsAWheelZoomIsExactlyOnePlacement() => Run(animations: false, () =>
    {
        var (pin, moves) = NewPin(clock: null);
        try
        {
            var before = pin.PhysicalBounds;
            var anchor = new Point(before.Left + 100, before.Top + 84);
            moves.Clear();
            pin.ResidentProbeWheelAt(120, anchor);
            Flush(pin);

            Assert.False(pin.IsZoomMoving);
            Assert.Single(moves);
            AssertOnWpfPixels(pin);
        }
        finally { pin.Close(); }
    });

    [Fact]
    public void AMotionMovesNothingAfterItsLastTick() => Run(animations: true, () =>
    {
        double now = 0;
        var (pin, moves) = NewPin(() => now);
        try
        {
            var before = pin.PhysicalBounds;
            var anchor = new Point(before.Left + 100, before.Top + 84);
            pin.ResidentProbeWheelAt(120, anchor);
            for (now = 30; now <= 60; now += 30) pin.TickZoomMotionForTest();
            Assert.True(pin.IsZoomMoving);
            // The last tick (a large step, so it surely moves the window) is one placement, its property sync none.
            now = 1000;
            moves.Clear();
            pin.TickZoomMotionForTest();
            Assert.False(pin.IsZoomMoving);
            Assert.Single(moves);
            moves.Clear();
            Flush(pin);
            pin.ForceZoomSettleForTest();
            Flush(pin);

            Assert.Empty(moves);
            AssertOnWpfPixels(pin);
        }
        finally { pin.Close(); }
    });

    // A 400 x 300 picture on Top 119.79: its physical top is 120 and a notch at 84 px below it puts the new Top on
    // 111.37, which WPF rounds to 111 and a placement relative to the window's own 120 rounds to 112.
    private static (PinWindow Pin, List<(int Left, int Top, int Right, int Bottom)> Moves) NewPin(Func<double>? clock)
    {
        PinContent content = PinContent.FromImage(PinNativeTestImages.Picture(400, 300));
        var pin = new PinWindow(content, new PinViewState(content.LayoutWidthDip, content.LayoutHeightDip, 1, 1, 0.1),
            120, 120, () => new PinSettings { CloseOnDoubleClick = false }) { ShowActivated = false };
        pin.ZoomMotionClockForTest = clock;
        pin.Show();
        pin.UpdateLayout();
        // A Top between pixels, as a zoom about a point leaves it.
        pin.Top = 119.79;
        Flush(pin);
        Assert.Equal(119.79, pin.Top, 6);
        Assert.Equal(120, pin.PhysicalBounds.Top);
        var moves = new List<(int Left, int Top, int Right, int Bottom)>();
        (int Left, int Top, int Right, int Bottom) last = pin.PhysicalBounds;
        HwndSource.FromHwnd(pin.Handle)!.AddHook((IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            const int WindowPosChanged = 0x0047;
            if (msg == WindowPosChanged)
            {
                var rect = pin.PhysicalBounds;
                if (rect != last) moves.Add(rect);
                last = rect;
            }
            return IntPtr.Zero;
        });
        return (pin, moves);
    }

    // The window sits on the pixels WPF's own conversion gives its properties (round half away from zero).
    private static void AssertOnWpfPixels(PinWindow pin)
    {
        double scale = System.Windows.Media.VisualTreeHelper.GetDpi(pin).DpiScaleX;
        static int Device(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);
        var rect = pin.PhysicalBounds;
        Assert.Equal(Device(pin.Left * scale), rect.Left);
        Assert.Equal(Device(pin.Top * scale), rect.Top);
        Assert.Equal(Device(pin.Width * scale), rect.Right - rect.Left);
        Assert.Equal(Device(pin.Height * scale), rect.Bottom - rect.Top);
    }

    private static void Flush(PinWindow pin) => pin.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, static () => { });

    private static void Run(bool animations, Action test) => StaTestHost.Run(() =>
    {
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = animations;
        try { test(); }
        finally { FluidMotion.AnimationsEnabledOverrideForTest = motion; }
    });
}
