using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MyCapture.App.Pinning;
using MyCapture.App.Themes;
using MyCapture.Core.Pin;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Platform.Display;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// An arrow key moves a pin by a DIP (ten with Shift). At a fractional display scale that is a fractional number of
/// pixels (1.25 at 125%); the pin moves by whole pixels and keeps its pixel size, on both hosts (#222).
/// </summary>
public sealed class PinKeyboardMoveTests
{
    private static readonly RectD OffscreenArea = new(-12000, -12000, 1600, 1000);
    private static readonly MonitorInfo Offscreen = new("PINMOVE-OFFSCREEN", OffscreenArea, OffscreenArea, 96, false);

    [Theory]
    [InlineData(1.25, Key.Right, false)]
    [InlineData(1.25, Key.Left, false)]
    [InlineData(1.5, Key.Down, false)]
    [InlineData(1.75, Key.Up, true)]
    public void AWpfPinMovedByKeysAtAFractionalScaleKeepsItsPixelSize(double scale, Key key, bool shift) => Run(() =>
    {
        PinContent content = PinContent.FromImage(PinNativeTestImages.Picture(176, 110));
        var pin = new PinWindow(content, new PinViewState(content.LayoutWidthDip, content.LayoutHeightDip, 1, 1, 0.1),
            200, 150, () => new PinSettings { CloseOnDoubleClick = false, SnapWhileDragging = false })
        { ShowActivated = false };
        try
        {
            pin.Show();
            pin.UpdateLayout();
            // The pin reads its scale from WPF, as on a monitor at that scale; the window stays where it is.
            VisualTreeHelper.SetRootDpi(pin, new DpiScale(scale, scale));
            Assert.Equal(scale, VisualTreeHelper.GetDpi(pin).DpiScaleX);
            var start = pin.PhysicalBounds;
            AssertKeysKeepThePixelSize(scale, key, shift, () => pin.PhysicalBounds,
                () => Assert.True(pin.HandleKey(key, shift ? ModifierKeys.Shift : ModifierKeys.None)));
            Assert.Equal((start.Right - start.Left, start.Bottom - start.Top),
                (pin.PhysicalBounds.Right - pin.PhysicalBounds.Left, pin.PhysicalBounds.Bottom - pin.PhysicalBounds.Top));
        }
        finally { pin.Close(); }
    });

    [Theory]
    [InlineData(1.25, Key.Right, false)]
    [InlineData(1.25, Key.Left, false)]
    [InlineData(1.5, Key.Down, false)]
    [InlineData(1.75, Key.Up, true)]
    public void ANativePinMovedByKeysAtAFractionalScaleKeepsItsPixelSize(double scale, Key key, bool shift) => Run(() =>
    {
        Func<IntPtr, double?>? read = NativePinWindow.ReadWindowScaleForTest;
        NativePinWindow.ReadWindowScaleForTest = _ => scale;
        var manager = new PinManager(() => new PinSettings { LightweightImagePins = true, SnapWhileDragging = false },
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)
        {
            ReadPlacementMonitorForTest = static () => Offscreen,
            ReadRestoreMonitorsForTest = static () => [Offscreen],
            ActivatePinForTest = static _ => { },
        };
        try
        {
            PinWindow pin = manager.PinImage(PinNativeTestImages.Picture(176, 110));
            NativePinWindow native = pin.NativeWindowForTest!;
            native.LiveClipForTest = OffscreenArea;
            native.CompleteTransitions();
            Assert.Equal(scale, native.DpiScale);
            RectD start = native.Bounds;
            AssertKeysKeepThePixelSize(scale, key, shift,
                () => ((int)native.Bounds.Left, (int)native.Bounds.Top, (int)native.Bounds.Right, (int)native.Bounds.Bottom),
                () => Assert.True(pin.HandleKey(key, shift ? ModifierKeys.Shift : ModifierKeys.None)));
            Assert.Equal((start.Width, start.Height), (native.Bounds.Width, native.Bounds.Height));
        }
        finally
        {
            manager.CloseAllForExit();
            NativePinWindow.ReadWindowScaleForTest = read;
        }
    });

    // Three presses: each moves the pin the key's way by the whole pixels next to the step (1 or 2 for 1.25 px)
    // and leaves its width and height as they were.
    private static void AssertKeysKeepThePixelSize(double scale, Key key, bool shift,
        Func<(int Left, int Top, int Right, int Bottom)> bounds, Action press)
    {
        double step = (shift ? 10 : 1) * scale;
        (int dx, int dy) = key switch
        {
            Key.Left => (-1, 0),
            Key.Right => (1, 0),
            Key.Up => (0, -1),
            _ => (0, 1),
        };
        var before = bounds();
        for (int i = 0; i < 3; i++)
        {
            press();
            var after = bounds();
            string what = $"press {i + 1} at {scale:0.##}: {before} -> {after}";
            Assert.True(after.Right - after.Left == before.Right - before.Left, "width changed, " + what);
            Assert.True(after.Bottom - after.Top == before.Bottom - before.Top, "height changed, " + what);
            double moved = ((after.Left - before.Left) * dx) + ((after.Top - before.Top) * dy);
            Assert.True(moved >= Math.Floor(step) && moved <= Math.Ceiling(step), "moved " + moved + ", " + what);
            Assert.True(dx == 0 ? after.Left == before.Left : after.Top == before.Top, "moved across, " + what);
            before = after;
        }
    }

    private static void Run(Action test) => StaTestHost.Run(() =>
    {
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        try { test(); }
        finally { FluidMotion.AnimationsEnabledOverrideForTest = motion; }
    });
}
