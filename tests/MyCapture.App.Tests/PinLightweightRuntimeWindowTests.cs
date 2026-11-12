using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Accessibility;
using MyCapture.App.Diagnostics;
using MyCapture.App.Pinning;
using MyCapture.App.Themes;
using MyCapture.Core.Pin;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Platform.Display;
using MyCapture.Platform.Shell;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Lightweight picture pins, increment 4, through the real PinManager and real HWNDs on an off-screen
/// stand-in monitor: the screen reader flag, the setting turned off while pins are open, and snapping
/// while a pin is dragged. Messages go to the test's own windows; the real foreground, pointer and
/// keyboard are never used. Every pin that switches to WPF stays at or below 720x480 physical pixels,
/// which a 1024x768 desktop can hold.
/// </summary>
public sealed class PinLightweightRuntimeWindowTests
{
    private static readonly RectD OffscreenArea = new(-12000, -12000, 1600, 1000);
    private static readonly MonitorInfo Offscreen = new("PINLIGHT-OFFSCREEN", OffscreenArea, OffscreenArea, 96, false);
    private const uint SettingChange = 0x001A;
    private const int SetScreenReader = 0x0047;

    [Fact]
    public void AScreenReaderStartingSwitchesEveryNativePinAndNewPinsOpenAsWpf() => Run(() =>
    {
        bool flag = false;
        var settings = new PinSettings { LightweightImagePins = true };
        var manager = Manager(settings);
        using var window = new NativeMessageWindow();
        using var reader = new ScreenReaderState(window, () => flag);
        manager.AttachScreenReader(reader);
        try
        {
            PinWindow first = manager.PinImage(Picture(120, 80));
            PinWindow second = manager.PinImage(Picture(90, 60));
            Assert.True(first.IsNativeHosted && second.IsNativeHosted);
            var reasons = new List<PinSwitchReason>();
            foreach (PinWindow pin in new[] { first, second })
            {
                pin.ActivateAfterSwitchForTest = static _ => { };
                pin.HostSwitched += reasons.Add;
            }

            // The flag changes, but nothing told the app which setting changed: no read, no switch.
            flag = true;
            SendSettingChange(window, 0x002F);
            Assert.False(reader.Active);
            Assert.True(first.IsNativeHosted && second.IsNativeHosted);

            SendSettingChange(window, SetScreenReader);
            PinNativeSpeedTestWait.Until(() => !first.IsNativeHosted && !second.IsNativeHosted
                && !first.IsHostSwitchPending && !second.IsHostSwitchPending);
            Assert.True(reader.Active);
            Assert.Equal([PinSwitchReason.ScreenReader, PinSwitchReason.ScreenReader], reasons);
            Assert.True(first.IsShown && second.IsShown);
            Assert.Equal(0, NativePinWindow.LiveWindowCount);

            PinWindow opened = manager.PinImage(Picture(80, 50));
            Assert.False(opened.IsNativeHosted);
            Assert.True(opened.IsVisible);

            // The reader stops: the open pins stay as they are, the next pin may be native again.
            flag = false;
            SendSettingChange(window, SetScreenReader);
            PinNativeSpeedTestWait.Until(() => !reader.Active);
            Assert.False(first.IsNativeHosted);
            PinWindow later = manager.PinImage(Picture(80, 50));
            Assert.True(later.IsNativeHosted);
        }
        finally { manager.CloseAllForExit(); }
    });

    [Fact]
    public void TurningTheSettingOffSwitchesOpenNativePinsAndTurningItOnLeavesWpfPinsAlone() => Run(() =>
    {
        var settings = new PinSettings();
        var manager = Manager(settings);
        try
        {
            PinWindow standard = manager.PinImage(Picture(100, 70));
            Assert.False(standard.IsNativeHosted);

            settings.LightweightImagePins = true;
            manager.ApplySettings();
            Assert.False(standard.IsNativeHosted);
            Assert.Equal(0, NativePinWindow.LiveWindowCount);
            PinWindow first = manager.PinImage(Picture(120, 80));
            PinWindow second = manager.PinImage(Picture(90, 60));
            Assert.True(first.IsNativeHosted && second.IsNativeHosted);
            var reasons = new List<PinSwitchReason>();
            foreach (PinWindow pin in new[] { first, second })
            {
                pin.ActivateAfterSwitchForTest = static _ => { };
                pin.HostSwitched += reasons.Add;
            }
            RectD bounds = first.NativeWindowForTest!.Bounds;

            // Still on: applying the settings again changes nothing.
            manager.ApplySettings();
            Assert.True(first.IsNativeHosted && second.IsNativeHosted);

            settings.LightweightImagePins = false;
            manager.ApplySettings();
            PinNativeSpeedTestWait.Until(() => !first.IsHostSwitchPending && !second.IsHostSwitchPending
                && !first.IsNativeHosted && !second.IsNativeHosted);
            Assert.Equal([PinSwitchReason.SettingOff, PinSwitchReason.SettingOff], reasons);
            Assert.True(first.IsVisible && second.IsVisible);
            (int left, int top, int right, int bottom) = first.PhysicalBounds;
            Assert.Equal(bounds, new RectD(left, top, right - left, bottom - top));
            Assert.Equal(0, NativePinWindow.LiveWindowCount);
            Assert.Equal(3, manager.Count);

            // Off again: a new pin is a standard pin.
            Assert.False(manager.PinImage(Picture(60, 40)).IsNativeHosted);
        }
        finally { manager.CloseAllForExit(); }
    });

    [Fact]
    public void ANativeDragSnapsToAWpfPinsEdgeAndToTheWorkAreaAndShiftBypasses() => Run(() =>
    {
        var settings = new PinSettings { SnapWhileDragging = true };
        var manager = Manager(settings);
        try
        {
            PinWindow target = manager.PinImage(Picture(120, 80));
            Assert.False(target.IsNativeHosted);
            settings.LightweightImagePins = true;
            PinWindow moving = manager.PinImage(Picture(120, 80));
            Assert.True(moving.IsNativeHosted);
            NativePinWindow native = moving.NativeWindowForTest!;
            native.LiveClipForTest = OffscreenArea;
            moving.ReadSnapDisplays = static () => [new("stand-in", OffscreenArea, OffscreenArea, 1)];
            int reads = 0;
            var source = moving.ReadSnapTargets;
            moving.ReadSnapTargets = () => { reads++; return source(); };
            NativePinHost host = moving.NativeHostForTest!;
            host.ReadModifiers = static () => ModifierKeys.None;

            (int tl, int tt, _, _) = target.PhysicalBounds;
            int width = (int)native.Bounds.Width;
            // Forty pixels left of the WPF pin, on its row: dragging 35 to the right leaves a 5 pixel gap.
            moving.MovePhysicalForTest(tl - width - 40, tt);
            Assert.Equal(new RectD(tl - width - 40, tt, width, native.Bounds.Height), native.Bounds);
            int px = tl - width - 40 + 60, py = tt + 40;
            Press(native, px, py);
            Drag(native, px + 35, py);
            Assert.Equal(tl - width, native.Bounds.Left);
            Assert.Equal(tt, native.Bounds.Top);
            // Shift held: the same pointer position, no snap.
            host.ReadModifiers = static () => ModifierKeys.Shift;
            Drag(native, px + 35, py);
            Assert.Equal(tl - width - 5, native.Bounds.Left);
            host.ReadModifiers = static () => ModifierKeys.None;
            Release(native);
            // The targets were read once for the whole drag.
            Assert.Equal(1, reads);

            // To the work area: the left edge of the stand-in screen, 5 pixels away, snaps to it.
            int wx = (int)OffscreenArea.Left + 300, wy = (int)OffscreenArea.Top + 300;
            moving.MovePhysicalForTest(wx, wy);
            Press(native, wx + 60, wy + 40);
            Drag(native, wx + 60 - 295, wy + 40);
            Assert.Equal(OffscreenArea.Left, native.Bounds.Left);
            Release(native);

            // With the setting off the same drag does not snap and reads no targets.
            settings.SnapWhileDragging = false;
            reads = 0;
            moving.MovePhysicalForTest(wx, wy);
            Press(native, wx + 60, wy + 40);
            Drag(native, wx + 60 - 295, wy + 40);
            Assert.Equal(OffscreenArea.Left + 5, native.Bounds.Left);
            Release(native);
            Assert.Equal(0, reads);
        }
        finally { manager.CloseAllForExit(); }
    });

    [Fact]
    public void AWpfDragSnapsToANativePinsEdge() => Run(() =>
    {
        var inputs = new List<SyntheticPinDragInput>();
        var settings = new PinSettings { SnapWhileDragging = true };
        var manager = Manager(settings);
        manager.CreateDragInputForTest = () => { var input = new SyntheticPinDragInput(); inputs.Add(input); return input; };
        manager.BeforeShowForTest = pin => pin.ShowActivated = false;
        try
        {
            PinWindow moving = manager.PinImage(Picture(120, 80));
            Assert.False(moving.IsNativeHosted);
            settings.LightweightImagePins = true;
            PinWindow target = manager.PinImage(Picture(120, 80));
            Assert.True(target.IsNativeHosted);
            NativePinWindow native = target.NativeWindowForTest!;
            native.LiveClipForTest = OffscreenArea;
            moving.ReadSnapDisplays = static () => [new("stand-in", OffscreenArea, OffscreenArea, 1)];
            moving.DragDesktopForTest = static () => OffscreenArea;

            (int ml, int mt, _, _) = moving.PhysicalBounds;
            int width = (int)native.Bounds.Width;
            // The native pin is 40 pixels left of the WPF pin on its row; its shown state counts as a target.
            target.MovePhysicalForTest(ml - width - 40, mt);
            Assert.True(target.IsShown);
            SyntheticPinDragInput input = inputs[0];
            input.Modifiers = ModifierKeys.None;
            input.Down(moving);
            PinSnapTests.MoveTo(moving, input, ml - 35, mt);
            Assert.Equal((int)native.Bounds.Right, moving.PhysicalBounds.Left);
            Assert.Equal(mt, moving.PhysicalBounds.Top);
            input.Up(moving);
        }
        finally { manager.CloseAllForExit(); }
    });

    private static void Press(NativePinWindow pin, int x, int y)
    {
        _ = SendMessageW(pin.Handle, 0x0201, new IntPtr(1), Client(pin, x, y));
        Assert.True(pin.IsDragging, "The real HWND must hold the mouse capture for this gesture.");
    }

    private static void Drag(NativePinWindow pin, int x, int y) => _ = SendMessageW(pin.Handle, 0x0200, new IntPtr(1), Client(pin, x, y));

    private static void Release(NativePinWindow pin)
    {
        RectD at = pin.DisplayedBounds;
        _ = SendMessageW(pin.Handle, 0x0202, IntPtr.Zero, Client(pin, (int)at.Left, (int)at.Top));
        Assert.False(pin.IsDragging);
    }

    // A screen point as the HWND's client coordinates, packed as a mouse message carries them.
    private static IntPtr Client(NativePinWindow pin, int x, int y)
    {
        RectD at = pin.DisplayedBounds;
        return new IntPtr((((int)(y - at.Top) & 0xFFFF) << 16) | ((int)(x - at.Left) & 0xFFFF));
    }

    // WM_SETTINGCHANGE carries a pointer, so Windows refuses to post it; a send to this thread's own window is the same delivery.
    private static void SendSettingChange(NativeMessageWindow window, int action) =>
        _ = SendMessageW(window.Handle, SettingChange, new IntPtr(action), IntPtr.Zero);

    private static void Run(Action test) => StaTestHost.Run(() =>
    {
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        Func<IntPtr, double?>? scale = NativePinWindow.ReadWindowScaleForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        // The stand-in monitor is 100%; the real one under an off-screen HWND is not consulted.
        NativePinWindow.ReadWindowScaleForTest = static _ => null;
        try
        {
            Assert.Equal(0, NativePinWindow.LiveWindowCount);
            test();
            PinNativeSpeedTestWait.Until(() => NativePinWindow.LiveWindowCount == 0);
        }
        finally
        {
            FluidMotion.AnimationsEnabledOverrideForTest = motion;
            NativePinWindow.ReadWindowScaleForTest = scale;
        }
    });

    private static PinManager Manager(PinSettings settings) =>
        new(() => settings, NullLogger.Instance)
        {
            ReadPlacementMonitorForTest = static () => Offscreen,
            ReadRestoreMonitorsForTest = static () => [Offscreen],
            ActivatePinForTest = static _ => { },
        };

    private static BitmapSource Picture(int width, int height) => PinNativeTestImages.Picture(width, height);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageW(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
}
