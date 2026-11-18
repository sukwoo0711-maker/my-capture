using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Pinning;
using MyCapture.App.Themes;
using MyCapture.Core.Pin;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Design section 8: a native pin across monitors of different scales. This PC has two 100% monitors, so the
/// scale reaches the windows only through the scale hook and through WM_DPICHANGED and WM_GETDPISCALEDSIZE sent
/// or posted to the test's own windows; the display settings are never changed. The windows stay off every
/// screen, so Windows itself never sends a change of its own while a test runs.
/// </summary>
public sealed class PinNativeDisplayScaleTests
{
    private static readonly RectD OffscreenArea = new(-12000, -12000, 1600, 1000);

    [Fact]
    public void AScaleChangeDuringAMoveKeepsThePictureSizeInDipAndThePointUnderThePointer() => Run(() =>
    {
        using var pin = new NativePinWindow(PinNativeTestImages.Picture(96, 64), -20000, -20000);
        pin.Show();
        // Press on picture point (40,30), then move ten pixels right.
        Press(pin, -19960, -19970);
        Drag(pin, -19950, -19970);
        Assert.Equal(new RectD(-19990, -20000, 96, 64), pin.Bounds);

        // 96 to 144 under a suggestion that keeps the top-left, as Windows suggests for a move.
        SendDpiChanged(pin.Handle, 144, pin.DisplayedBounds);
        Assert.Equal(1.5, pin.DpiScale);
        Assert.Equal(1, pin.DpiChangeCount);
        // Same DIP size; picture point (40,30) is still under the pointer: (-19950 - 60, -19970 - 45).
        Assert.Equal(new RectD(-20010, -20015, 144, 96), pin.Bounds);
        Assert.Equal(pin.DisplayedBounds, PinNativeScreenBoundedTests.WindowRectangle(pin.Handle));
        Assert.True(pin.IsDragging);

        // The move continues from the new place, not from where it started.
        Drag(pin, -19943, -19967);
        Assert.Equal(new RectD(-20003, -20012, 144, 96), pin.Bounds);

        // And back to 96 after that move: the same point again, then no drift once the pointer returns.
        SendDpiChanged(pin.Handle, 96, pin.DisplayedBounds);
        Assert.Equal(1, pin.DpiScale);
        Assert.Equal(new RectD(-19983, -19997, 96, 64), pin.Bounds);
        Drag(pin, -19960, -19970);
        Assert.Equal(new RectD(-20000, -20000, 96, 64), pin.Bounds);
        Release(pin);
        Assert.Equal(2, pin.DpiChangeCount);
        Assert.Equal(0, pin.IgnoredDpiChangeCount);
        Assert.Null(pin.HeldDpiScale);
        Assert.Equal(pin.DisplayedBounds, PinNativeScreenBoundedTests.WindowRectangle(pin.Handle));
        PinNativeSpeedTestWait.Complete(pin.WaitForSettledAsync());
        Assert.Equal(PinNativeTestImages.Native(PinNativeTestImages.Picture(96, 64), 1, 1, AppTheme.Glass).Pixels, pin.CopyDisplayPixels());
        Close(pin);
    });

    [Fact]
    public void AChangeBackCausedByThePinsOwnNewSizeIsNotAppliedUntilTheNextMoveEnds() => Run(() =>
    {
        using var pin = new NativePinWindow(PinNativeTestImages.Picture(96, 64), -20000, -20000);
        pin.Show();
        // While 144 is being applied, the larger window's centre crosses back: Windows sends 96 at once.
        bool bounced = false;
        void Bounce()
        {
            if (bounced) return;
            bounced = true;
            SendDpiChanged(pin.Handle, 96, pin.DisplayedBounds);
        }
        pin.Presented += Bounce;
        SendDpiChanged(pin.Handle, 144, pin.DisplayedBounds);
        pin.Presented -= Bounce;
        Assert.True(bounced);
        Assert.Equal(1.5, pin.DpiScale);
        Assert.Equal(1, pin.DpiChangeCount);
        Assert.Equal(1, pin.IgnoredDpiChangeCount);
        Assert.Equal(1, pin.HeldDpiScale);
        Assert.Equal(1, pin.WindowDpiScale);

        // The same change back arriving later, from the dispatcher, before the pin has moved: still the flicker.
        // (Windows refuses to post WM_DPICHANGED, whose lParam is a pointer: ERROR_MESSAGE_SYNC_ONLY.)
        Assert.False(PostMessageW(pin.Handle, 0x02E0, new IntPtr((96 << 16) | 96), IntPtr.Zero));
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background, () => SendDpiChanged(pin.Handle, 96, pin.DisplayedBounds));
        PinNativeSpeedTestWait.Until(() => pin.IgnoredDpiChangeCount == 2);
        Assert.Equal(1.5, pin.DpiScale);

        // A move that ends applies the scale Windows gave, keeping the point under the pointer.
        Press(pin, -19940, -19955);
        Drag(pin, -19930, -19955);
        Assert.Equal(1.5, pin.DpiScale);
        Release(pin);
        Assert.Equal(1, pin.DpiScale);
        Assert.Equal(2, pin.DpiChangeCount);
        Assert.Null(pin.HeldDpiScale);
        // Point (40,30) in DIP was under (-19930, -19955): the 96 DPI pin starts at (-19970, -19985).
        Assert.Equal(new RectD(-19970, -19985, 96, 64), pin.Bounds);

        // Its own shrink bounces once more before any move: not applied either.
        SendDpiChanged(pin.Handle, 144, pin.DisplayedBounds);
        Assert.Equal(1, pin.DpiScale);
        Assert.Equal(3, pin.IgnoredDpiChangeCount);

        // After the pointer has moved, the same change is a real crossing and applies at once.
        Press(pin, -19950, -19970);
        Drag(pin, -19949, -19970);
        SendDpiChanged(pin.Handle, 144, pin.DisplayedBounds);
        Assert.Equal(1.5, pin.DpiScale);
        Assert.Equal(3, pin.DpiChangeCount);
        Release(pin);
        Assert.Equal(1.5, pin.DpiScale);
        Close(pin);
    });

    [Fact]
    public void GetDpiScaledSizeAnswersTheSizeThePinWillHoldAtTheNewScale() => Run(() =>
    {
        using var pin = new NativePinWindow(PinNativeTestImages.Picture(96, 64), -20000, -20000);
        pin.LiveClipForTest = new RectD(-20010, -20010, 130, 90);
        pin.Show();
        Assert.Equal(new RectD(-20000, -20000, 96, 64), pin.DisplayedBounds);
        IntPtr size = Marshal.AllocHGlobal(8);
        try
        {
            Marshal.WriteInt64(size, 0);
            // At 150% the surface is 144 x 96, of which the stand-in screen shows 120 x 80.
            Assert.Equal(new IntPtr(1), SendMessageW(pin.Handle, 0x02E4, new IntPtr((144 << 16) | 144), size));
            Assert.Equal(120, Marshal.ReadInt32(size, 0));
            Assert.Equal(80, Marshal.ReadInt32(size, 4));
            // At 125% the whole 120 x 80 surface fits.
            Assert.Equal(new IntPtr(1), SendMessageW(pin.Handle, 0x02E4, new IntPtr((120 << 16) | 120), size));
            Assert.Equal(120, Marshal.ReadInt32(size, 0));
            Assert.Equal(80, Marshal.ReadInt32(size, 4));
            Assert.Equal(new IntPtr(1), SendMessageW(pin.Handle, 0x02E4, new IntPtr((96 << 16) | 96), size));
            Assert.Equal(96, Marshal.ReadInt32(size, 0));
            Assert.Equal(64, Marshal.ReadInt32(size, 4));
        }
        finally { Marshal.FreeHGlobal(size); }
        // Answering changes nothing.
        Assert.Equal(1, pin.DpiScale);
        Assert.Equal(0, pin.DpiChangeCount);
        Close(pin);
    });

    [Fact]
    public void APinCreatedOnAMonitorOfAnotherScaleRendersAtThatScaleAndHoldsOnlyWhatTheScreenShows() => Run(() =>
    {
        // Session restore onto a monitor whose scale changed since: the saved scale was 100%, the monitor is 150%.
        NativePinWindow.ReadWindowScaleForTest = static _ => 1.5;
        BitmapSource picture = PinNativeTestImages.Picture(96, 64, transparent: true);
        using var pin = new NativePinWindow(NativePinPixelSource.FromBitmap(picture), new PinViewState(96, 64, 2, 1, .1),
            -11950, -11980, 1, AppTheme.Glass, null, null, "test");
        pin.LiveClipForTest = OffscreenArea;
        Assert.Equal(1.5, pin.DpiScale);
        Assert.Equal(new RectD(-11950, -11980, 288, 192), pin.Bounds);
        pin.Show();
        PinNativeSpeedTestWait.Complete(pin.WaitForSettledAsync());
        // The 288 x 192 surface lies inside the stand-in screen: all of it, at 150% and 200% zoom.
        Assert.Equal(pin.Bounds, pin.DisplayedBounds);
        Assert.Equal(PinNativeTestImages.Native(picture, 2, 1.5, AppTheme.Glass).Pixels.Length, pin.CopyDisplayPixels().Length);
        Close(pin);
    });

    [Fact]
    public void TheMessageWindowFollowsThePinsScaleChangeAndItsOwn() => RunHosted(() =>
    {
        PinWindow pin = HostedPin();
        try
        {
            NativePinWindow native = pin.NativeWindowForTest!;
            RectD bounds = native.Bounds;
            int centre = PointParam(bounds.Left + (bounds.Width / 2), bounds.Top + (bounds.Height / 2));
            Assert.True(PostMessageW(native.Handle, 0x020A, new IntPtr(120 << 16), new IntPtr(centre)));
            PinNativeSpeedTestWait.Until(() => pin.FeedbackWindowForTest is { IsVisible: true });
            PinFeedbackSurface surface = pin.FeedbackWindowForTest!;
            IntPtr handle = new WindowInteropHelper(surface).Handle;
            PinNativeSpeedTestWait.Until(() => WindowRect(handle).Bottom == native.Bounds.Bottom);

            // The pin goes to 150%; the message window still lays out at its own 100%.
            SendDpiChanged(native.Handle, 144, native.DisplayedBounds);
            Assert.Equal(1.5, native.DpiScale);
            PinNativeSpeedTestWait.Until(() => Placed(surface, native));
            Assert.Equal(1, VisualTreeHelper.GetDpi(surface).DpiScaleX);

            // Then it crosses too: Windows moves it to its suggestion, and it comes back under the pin at 150%.
            RectD away = new(-11000, -11000, WindowRect(handle).Width * 1.5, WindowRect(handle).Height * 1.5);
            SendDpiChanged(handle, 144, away);
            PinNativeSpeedTestWait.Until(() => VisualTreeHelper.GetDpi(surface).DpiScaleX == 1.5);
            PinNativeSpeedTestWait.Until(() => Placed(surface, native));
        }
        finally { Close(pin); }
    });

    [Fact]
    public void TheSharedHoverBarFollowsThePinsScaleChangeAndItsOwn() => RunHosted(() =>
    {
        Func<Window, bool> exclusion = PinHoverBar.ApplyCaptureExclusion;
        PinHoverBar.ApplyCaptureExclusion = static _ => true;
        Action<System.Windows.Controls.ToolTip>? tip = PinWindow.PresentNativeToolTipForTest;
        PinWindow.PresentNativeToolTipForTest = static _ => { };
        PinWindow pin = HostedPin(width: 600, height: 300);
        try
        {
            NativePinWindow native = pin.NativeWindowForTest!;
            Assert.True(PostMessageW(native.Handle, 0x0200, IntPtr.Zero, new IntPtr(PointParam(10, 10))));
            PinNativeSpeedTestWait.Until(() => pin.IsHoverBarShown);
            PinHoverBar bar = pin.HoverBarForTest!;
            IntPtr handle = new WindowInteropHelper(bar).Handle;

            SendDpiChanged(native.Handle, 144, native.DisplayedBounds);
            Assert.Equal(1.5, native.DpiScale);
            PinNativeSpeedTestWait.Until(() => BarPlaced(bar, native));

            RectD away = new(-11000, -11000, WindowRect(handle).Width * 1.5, WindowRect(handle).Height * 1.5);
            SendDpiChanged(handle, 144, away);
            PinNativeSpeedTestWait.Until(() => VisualTreeHelper.GetDpi(bar).DpiScaleX == 1.5);
            PinNativeSpeedTestWait.Until(() => BarPlaced(bar, native));
        }
        finally
        {
            Close(pin);
            PinHoverBar.ApplyCaptureExclusion = exclusion;
            PinWindow.PresentNativeToolTipForTest = tip;
            PinWindow.SharedNativeHoverBarForTest?.Close();
        }
    });

    [Theory]
    [InlineData(1d)]
    [InlineData(1.5)]
    public void TheSwitchOfAPinLargerThanTheScreenTakesTheWholeSurface(double scale) => RunHosted(() =>
    {
        // The pin starts 300 x 200 away from the right-bottom corner of the 1600 x 1000 stand-in screen, so 200%
        // already overflows it on the right and bottom. The surface stays at most 720 x 480: Windows does not let
        // a WPF window grow past the real virtual screen, and the CI desktop is 1024 x 768.
        PinWindow pin = HostedPin(240, 160, left: -10700, top: -11200);
        try
        {
            NativePinWindow native = pin.NativeWindowForTest!;
            if (scale != 1) SendDpiChanged(native.Handle, (int)(96 * scale), native.DisplayedBounds);
            Assert.Equal(scale, native.DpiScale);
            native.SetZoom(2);
            PinNativeSpeedTestWait.Complete(native.WaitForSettledAsync());
            Assert.True(native.IsDisplayClipped);
            RectD whole = native.Bounds;
            Assert.Equal(Math.Round(240 * 2 * scale), whole.Width);
            Assert.True(native.DisplayedBounds.Width < whole.Width);

            Rect expected = PinWindow.SwitchGeometry(whole, scale);
            pin.ActivateAfterSwitchForTest = static _ => { };
            Assert.True(pin.SwitchToWpf(PinSwitchReason.Command));
            PinNativeSpeedTestWait.Until(() => !pin.IsHostSwitchPending);
            Assert.Equal(pin.State.WidthDip, expected.Width, 6);
            Assert.Equal(pin.State.HeightDip, expected.Height, 6);
            Assert.Equal(pin.State.WidthDip, pin.Width, 6);
            (int left, int top, int right, int bottom) = pin.PhysicalBounds;
            // The WPF window starts where the whole surface starts, not where the held part does.
            Assert.Equal(whole.Left, left);
            Assert.Equal(whole.Top, top);
            // At this PC's own scale it takes exactly the surface's rectangle. At 150% its size follows the
            // monitor it is really on (100% here), which only a 150% display can show.
            if (scale == 1) Assert.Equal(whole, new RectD(left, top, right - left, bottom - top));
        }
        finally { Close(pin); }
    });

    // ----- Harness -----

    private static bool Placed(PinFeedbackSurface surface, NativePinWindow native)
    {
        double own = VisualTreeHelper.GetDpi(surface).DpiScaleX;
        RectD placed = WindowRect(new WindowInteropHelper(surface).Handle);
        RectD target = PinFeedbackSurface.Place(native.Bounds, surface.ActualWidth * own, surface.ActualHeight * own);
        return Math.Abs(placed.Left - target.Left) <= 1 && Math.Abs(placed.Top - target.Top) <= 1
            && Math.Abs(placed.Width - target.Width) <= 1 && Math.Abs(placed.Height - target.Height) <= 1;
    }

    private static bool BarPlaced(PinHoverBar bar, NativePinWindow native)
    {
        double own = VisualTreeHelper.GetDpi(bar).DpiScaleX;
        RectD placed = WindowRect(new WindowInteropHelper(bar).Handle);
        RectD target = PinHoverBar.Place(native.Bounds, bar.ActualWidth * own, bar.ActualHeight * own, OffscreenArea, native.DpiScale);
        return Math.Abs(placed.Left - target.Left) <= 1 && Math.Abs(placed.Top - target.Top) <= 1
            && Math.Abs(placed.Width - target.Width) <= 1 && Math.Abs(placed.Height - target.Height) <= 1;
    }

    private static void Press(NativePinWindow pin, int x, int y)
    {
        _ = SendMessageW(pin.Handle, 0x0201, new IntPtr(1), Client(pin, x, y));
        Assert.True(pin.IsDragging, $"The real HWND must hold the mouse capture for this gesture (capture {GetCapture()}, "
            + $"window {pin.Handle}, at {pin.DisplayedBounds}, visible {pin.IsVisible}, hiding {pin.IsHiding}).");
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
        return new IntPtr(PointParam(x - at.Left, y - at.Top));
    }

    private static int PointParam(double x, double y) => (((int)y & 0xFFFF) << 16) | ((int)x & 0xFFFF);

    private static IntPtr DpiRect(RectD suggested)
    {
        IntPtr rect = Marshal.AllocHGlobal(16);
        Marshal.WriteInt32(rect, 0, (int)suggested.Left);
        Marshal.WriteInt32(rect, 4, (int)suggested.Top);
        Marshal.WriteInt32(rect, 8, (int)(suggested.Left + suggested.Width));
        Marshal.WriteInt32(rect, 12, (int)(suggested.Top + suggested.Height));
        return rect;
    }

    private static void SendDpiChanged(IntPtr window, int dpi, RectD suggested)
    {
        IntPtr rect = DpiRect(suggested);
        try { _ = SendMessageW(window, 0x02E0, new IntPtr((dpi << 16) | dpi), rect); }
        finally { Marshal.FreeHGlobal(rect); }
    }

    private static RectD WindowRect(IntPtr window)
    {
        Assert.True(NativePinWindowInterop.GetWindowRect(window, out var rect));
        return new RectD(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    private static void Close(NativePinWindow pin)
    {
        if (!pin.IsClosed) pin.Close();
        PinNativeSpeedTestWait.Complete(pin.DrainAsync());
    }

    private static PinWindow HostedPin(int width = 160, int height = 100, int left = -11900, int top = -11900)
    {
        BitmapSource picture = PinNativeTestImages.Picture(width, height);
        PinContent content = PinContent.FromImage(picture);
        var settings = new PinSettings { LightweightImagePins = true };
        var pin = new PinWindow(content, new PinViewState(width, height, 1, 1, .1), left, top, () => settings)
        {
            ReadWorkArea = static _ => OffscreenArea,
        };
        NativePinHost host = NativePinHost.Create(pin, content, left, top, 1);
        pin.UseNativeHost(host);
        host.Window.LiveClipForTest = OffscreenArea;
        pin.Present();
        return pin;
    }

    private static void Close(PinWindow pin)
    {
        if (!pin.IsClosed) pin.Close();
        else if (pin.IsLeaving) pin.CompleteExit(releaseBuffers: false);
        PinNativeSpeedTestWait.Until(() => pin.NativeWindowForTest is null || pin.NativeWindowForTest.IsClosed);
    }

    private static void Run(Action test) => StaTestHost.Run(() =>
    {
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        Func<IntPtr, double?>? scale = NativePinWindow.ReadWindowScaleForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        try { test(); }
        finally
        {
            FluidMotion.AnimationsEnabledOverrideForTest = motion;
            NativePinWindow.ReadWindowScaleForTest = scale;
        }
    });

    private static void RunHosted(Action test) => StaTestHost.Run(() =>
    {
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        Func<IntPtr, double?>? scale = NativePinWindow.ReadWindowScaleForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        NativePinWindow.ReadWindowScaleForTest = static _ => null;
        try
        {
            test();
            PinNativeSpeedTestWait.Until(() => NativePinWindow.LiveWindowCount == 0);
        }
        finally
        {
            FluidMotion.AnimationsEnabledOverrideForTest = motion;
            NativePinWindow.ReadWindowScaleForTest = scale;
        }
    });

    [DllImport("user32.dll")]
    private static extern IntPtr GetCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageW(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
}
