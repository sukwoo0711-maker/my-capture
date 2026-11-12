using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using MyCapture.App.Capture;
using MyCapture.App.Pinning;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Pin;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Ocr;
using MyCapture.Platform.Display;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Lightweight picture pins, increment 2: the passive commands, the context menu and the windows around a
/// native pin, on real HWNDs kept off every screen. Input is posted to the test's own windows; the real
/// foreground, pointer, keyboard and clipboard are never used, and no popup is opened on the screen.
/// </summary>
public sealed class PinLightweightSurfaceWindowTests
{
    private static readonly RectD OffscreenArea = new(-12000, -12000, 1600, 1000);
    private static readonly MonitorInfo Offscreen = new("PINLIGHT-OFFSCREEN", OffscreenArea, OffscreenArea, 96, false);

    [Fact]
    public void KeysRunTheSameCommandsOnTheNativeHostWithoutSwitching() => Run(false, () =>
    {
        PinWindow pin = NativePin(new PinSettings { LightweightImagePins = true });
        try
        {
            NativePinWindow native = pin.NativeWindowForTest!;
            NativePinHost host = pin.NativeHostForTest!;
            var copies = new List<PinImageCopyRequest>();
            var saves = new List<PinSaveMode>();
            pin.CopyRequested += (_, request) => copies.Add(request);
            pin.SaveRequested += (_, args) => { saves.Add(args.Mode); pin.ReportSaveResult(new PinSaveResult(PinSaveStatus.Cancelled)); };

            host.ReadModifiers = static () => ModifierKeys.Control;
            Post(native.Handle, 0x0100, 0x43); // C
            PinNativeSpeedTestWait.Until(() => copies.Count == 1);
            Post(native.Handle, 0x0100, 0x53); // S
            PinNativeSpeedTestWait.Until(() => saves.Count == 1);
            host.ReadModifiers = static () => ModifierKeys.Control | ModifierKeys.Shift;
            Post(native.Handle, 0x0100, 0x53);
            PinNativeSpeedTestWait.Until(() => saves.Count == 2);
            Assert.Equal([PinSaveMode.QuickSave, PinSaveMode.SaveAs], saves);
            Assert.Same(pin.PinnedContent.Image, copies[0].Image);

            // Arrows move by the nudge in physical pixels; + and 0 zoom about the centre with the readout.
            host.ReadModifiers = static () => ModifierKeys.None;
            RectD before = native.Bounds;
            Post(native.Handle, 0x0100, 0x27); // Right
            PinNativeSpeedTestWait.Until(() => native.Bounds.Left != before.Left);
            Assert.Equal(before.Left + 1, native.Bounds.Left);
            Post(native.Handle, 0x0100, 0xBB); // OemPlus
            PinNativeSpeedTestWait.Until(() => pin.State.Zoom > 1);
            PinNativeSpeedTestWait.Until(() => pin.FeedbackWindowForTest is { IsVisible: true });
            Assert.Equal($"{pin.State.Zoom * 100:0}%", FeedbackText(pin));
            Post(native.Handle, 0x0100, 0x30); // 0
            PinNativeSpeedTestWait.Until(() => Math.Abs(pin.State.Zoom - 1) < 1e-9);
            PinNativeSpeedTestWait.Until(() => FeedbackText(pin) == "100%");
            Assert.True(pin.IsNativeHosted);
            Assert.False(pin.IsHostSwitchPending);
        }
        finally { Close(pin); }
    });

    [Fact]
    public void TheWheelReadoutAndAFailureMessageShowOnTheMessageWindowAndEscDismissesFirst() => Run(false, () =>
    {
        PinWindow pin = NativePin(new PinSettings { LightweightImagePins = true });
        try
        {
            NativePinWindow native = pin.NativeWindowForTest!;
            pin.NativeHostForTest!.ReadModifiers = static () => ModifierKeys.None;
            RectD bounds = native.Bounds;
            int centre = PointParam(bounds.Left + (bounds.Width / 2), bounds.Top + (bounds.Height / 2));
            Assert.True(PostMessageW(native.Handle, 0x020A, new IntPtr(120 << 16), new IntPtr(centre)));
            PinNativeSpeedTestWait.Until(() => pin.FeedbackWindowForTest is { IsVisible: true });
            PinFeedbackSurface surface = pin.FeedbackWindowForTest!;
            Assert.Equal($"{pin.State.Zoom * 100:0}%", FeedbackText(pin));
            // The message window is the pin's own: owned by the native window and shown in captures.
            Assert.Equal(native.Handle, NativePinWindowInterop.GetWindow(new WindowInteropHelper(surface).Handle, 4));
            Assert.False(CaptureWindowExclusion.WasApplied(surface));
            PinNativeSpeedTestWait.Until(() => SurfaceRect(surface).Bottom == native.Bounds.Bottom);
            RectD placed = SurfaceRect(surface);
            Assert.InRange(placed.Left + (placed.Width / 2) - (native.Bounds.Left + (native.Bounds.Width / 2)), -1, 1);

            // Ctrl+wheel: the opacity readout.
            Assert.True(PostMessageW(native.Handle, 0x020A, new IntPtr((-120 << 16) | 0x0008), new IntPtr(centre)));
            PinNativeSpeedTestWait.Until(() => pin.State.Opacity < 1);
            Assert.Equal(UiText.Format("Text_C740003A2150", pin.State.Opacity * 100), FeedbackText(pin));

            pin.ReportSaveResult(new PinSaveResult(PinSaveStatus.Failed, ErrorMessage: "x"));
            Assert.True(pin.IsFeedbackVisible);
            Post(native.Handle, 0x0100, 0x1B); // Esc dismisses the message.
            PinNativeSpeedTestWait.Until(() => !pin.IsFeedbackVisible);
            PinNativeSpeedTestWait.Until(() => pin.FeedbackWindowForTest is null);
            Assert.False(pin.IsClosed);
            Post(native.Handle, 0x0100, 0x1B); // The next Esc closes the pin.
            PinNativeSpeedTestWait.Until(() => pin.IsClosed);
        }
        finally { Close(pin); }
    });

    [Fact]
    public void DoubleClicksCloseOrRecognizeOnTheNativeHost() => Run(false, () =>
    {
        PinWindow closing = NativePin(new PinSettings { LightweightImagePins = true, CloseOnDoubleClick = true });
        PinWindow reading = NativePin(new PinSettings { LightweightImagePins = true, CloseOnDoubleClick = true });
        try
        {
            var recognized = new List<BitmapSource>();
            reading.OcrRequested += (_, image) => recognized.Add(image);
            Assert.True(PostMessageW(reading.NativeWindowForTest!.Handle, 0x0203, new IntPtr(0x0001 | 0x0008), new IntPtr(PointParam(5, 5))));
            PinNativeSpeedTestWait.Until(() => recognized.Count == 1);
            Assert.True(reading.IsNativeHosted);
            Assert.False(reading.IsClosed);

            Assert.True(PostMessageW(closing.NativeWindowForTest!.Handle, 0x0203, new IntPtr(0x0001), new IntPtr(PointParam(5, 5))));
            PinNativeSpeedTestWait.Until(() => closing.IsClosed);
        }
        finally { Close(closing); Close(reading); }
    });

    [Fact]
    public void TheContextMenuOpensOnTheNativePinWithTheSameItemsAndOnlyTextSelectionSwitches() => Run(false, () =>
    {
        var settings = new PinSettings { LightweightImagePins = true };
        Func<BitmapSource, CancellationToken, Task<OcrResult>> recognize = static (_, token) =>
            new TaskCompletionSource<OcrResult>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        PinWindow pin = NativePin(settings, recognize);
        BitmapSource picture = PinNativeTestImages.Picture(160, 100);
        var wpf = new PinWindow(PinContent.FromImage(picture), new PinViewState(160, 100, 1, 1, .1), -12000, -12000,
            () => new PinSettings(), recognizeSelection: recognize);
        var opened = new List<ContextMenu>();
        Action<ContextMenu>? previous = PinWindow.PresentNativeContextMenuForTest;
        PinWindow.PresentNativeContextMenuForTest = opened.Add;
        try
        {
            NativePinWindow native = pin.NativeWindowForTest!;
            Assert.True(PostMessageW(native.Handle, 0x007B, native.Handle, new IntPtr(PointParam(-11990, -11990))));
            PinNativeSpeedTestWait.Until(() => opened.Count == 1);
            Assert.Same(pin.ContextMenu, opened[0]);
            Assert.True(pin.IsNativeHosted);
            Assert.Equal(Headers(wpf.ContextMenu!), Headers(opened[0]));

            // A passive item runs natively.
            Click(opened[0], UiText.Get("Onboard_PinZoomIn"));
            Assert.True(pin.State.Zoom > 1);
            Assert.True(pin.IsNativeHosted);
            Click(opened[0], "100%");
            Assert.Equal(1, pin.State.Zoom, 9);
            Click(opened[0], UiText.Get("Onboard_PinMoreTransparent"));
            Assert.True(pin.State.Opacity < 1);
            Assert.True(pin.IsNativeHosted);

            // Text selection is drawn by the WPF window: the pin switches when it is chosen, not before.
            pin.ActivateAfterSwitchForTest = static _ => { };
            Click(opened[0], UiText.Get("Pin_SelectText"));
            PinNativeSpeedTestWait.Until(() => !pin.IsNativeHosted && !pin.IsHostSwitchPending);
            Assert.True(pin.IsRecognizingSelection);
        }
        finally
        {
            PinWindow.PresentNativeContextMenuForTest = previous;
            wpf.Close();
            Close(pin);
        }
    });

    [Fact]
    public void ClickThroughChangesInPlaceAndItsHandleAnchorsToTheNativeWindowThenFollowsTheSwitch() => Run(false, () =>
    {
        using var scope = new PinClickThroughHandleTests.HandleScope();
        PinWindow pin = NativePin(new PinSettings { LightweightImagePins = true, ClickThroughHintShownCount = 3 });
        try
        {
            NativePinWindow native = pin.NativeWindowForTest!;
            Assert.True(pin.ToggleClickThrough());
            Assert.True(pin.IsNativeHosted);
            Assert.False(pin.IsHostSwitchPending);
            Assert.True(WindowStyleFacade.IsClickThrough(native.Handle));
            PinClickThroughHandle handle = Assert.IsType<PinClickThroughHandle>(pin.ClickThroughHandleForTest);
            Assert.True(handle.IsVisible);
            IntPtr handleWindow = new WindowInteropHelper(handle).Handle;
            Assert.Equal(native.Handle, NativePinWindowInterop.GetWindow(handleWindow, 4)); // GW_OWNER
            RectD expected = PinClickThroughHandle.Placement(native.Bounds, OffscreenArea, 1);
            Assert.Equal(expected, WindowRect(handleWindow));

            // The handle follows the native window.
            pin.MovePhysicalForTest(native.Bounds.Left + 40, native.Bounds.Top);
            Assert.Equal(PinClickThroughHandle.Placement(native.Bounds, OffscreenArea, 1), WindowRect(handleWindow));

            // A switch hands the handle to the WPF window before the native one goes.
            pin.ActivateAfterSwitchForTest = static _ => { };
            Assert.True(pin.SwitchToWpf(PinSwitchReason.Command));
            PinNativeSpeedTestWait.Until(() => !pin.IsHostSwitchPending);
            Assert.True(handle.IsVisible);
            Assert.Equal(new WindowInteropHelper(pin).Handle, NativePinWindowInterop.GetWindow(handleWindow, 4));
            Assert.True(WindowStyleFacade.IsClickThrough(new WindowInteropHelper(pin).Handle));

            handle.RecoveryButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.False(pin.State.IsClickThrough);
        }
        finally { Close(pin); }
    });

    [Fact]
    public void ThePointerOverANativePinShowsTheSharedHoverBarWhichFollowsTheMoveWithEveryNameOrNone() => Run(false, () =>
    {
        Func<Window, bool> exclusion = PinHoverBar.ApplyCaptureExclusion;
        PinHoverBar.ApplyCaptureExclusion = static _ => true;
        Action<ToolTip>? tip = PinWindow.PresentNativeToolTipForTest;
        PinWindow.PresentNativeToolTipForTest = static _ => { };
        PinWindow first = NativePin(new PinSettings { LightweightImagePins = true }, width: 600, height: 300);
        PinWindow second = NativePin(new PinSettings { LightweightImagePins = true }, width: 120, height: 80);
        try
        {
            NativePinWindow native = first.NativeWindowForTest!;
            Assert.True(PostMessageW(native.Handle, 0x0200, IntPtr.Zero, new IntPtr(PointParam(10, 10))));
            PinNativeSpeedTestWait.Until(() => first.IsHoverBarShown);
            PinHoverBar bar = first.HoverBarForTest!;
            Assert.Same(PinWindow.SharedNativeHoverBarForTest, bar);
            Assert.Equal(native.Handle, NativePinWindowInterop.GetWindow(new WindowInteropHelper(bar).Handle, 4));
            Assert.False(bar.Compact);
            RectD placed = WindowRect(new WindowInteropHelper(bar).Handle);
            Assert.Equal(PinHoverBar.Place(native.Bounds, placed.Width, placed.Height, OffscreenArea, 1), placed);

            first.MovePhysicalForTest(native.Bounds.Left + 50, native.Bounds.Top + 20);
            placed = WindowRect(new WindowInteropHelper(bar).Handle);
            Assert.Equal(PinHoverBar.Place(native.Bounds, placed.Width, placed.Height, OffscreenArea, 1), placed);

            // The narrow pin borrows the same bar; it drops every name at once.
            NativePinWindow small = second.NativeWindowForTest!;
            Assert.True(PostMessageW(small.Handle, 0x0200, IntPtr.Zero, new IntPtr(PointParam(5, 5))));
            PinNativeSpeedTestWait.Until(() => second.IsHoverBarShown);
            Assert.Same(bar, second.HoverBarForTest);
            Assert.Null(first.HoverBarForTest);
            Assert.True(bar.Compact);
            Assert.Equal(small.Handle, NativePinWindowInterop.GetWindow(new WindowInteropHelper(bar).Handle, 4));

            // The bar outlives the pin that borrowed it, hidden and no longer owned by its window
            // (WPF gives an ownerless tool window its own hidden owner).
            IntPtr smallHandle = small.Handle;
            second.CloseWithExit();
            PinNativeSpeedTestWait.Until(() => !bar.IsVisible);
            Assert.NotEqual(smallHandle, NativePinWindowInterop.GetWindow(new WindowInteropHelper(bar).Handle, 4));
        }
        finally
        {
            Close(first); Close(second);
            PinWindow.SharedNativeHoverBarForTest?.Close();
            PinHoverBar.ApplyCaptureExclusion = exclusion;
            PinWindow.PresentNativeToolTipForTest = tip;
        }
    });

    [Fact]
    public void TheLandingOrnamentAnchorsToTheNativeWindowAndClosesWhenItMoves() => Run(true, () =>
    {
        Func<Window, bool> exclusion = PinLandingDecoration.ApplyCaptureExclusion;
        Func<bool> remote = PinLandingDecoration.ReadRemoteSession, layered = PinLandingDecoration.ReadLayeredSupport;
        PinLandingThrottle throttle = PinLandingDecoration.Throttle;
        PinLandingDecoration.ApplyCaptureExclusion = static _ => true;
        PinLandingDecoration.ReadRemoteSession = static () => false;
        PinLandingDecoration.ReadLayeredSupport = static () => true;
        PinLandingDecoration.Throttle = new PinLandingThrottle();
        PinWindow pin = NativePin(new PinSettings { LightweightImagePins = true });
        try
        {
            NativePinWindow native = pin.NativeWindowForTest!;
            int startedAtTick = -1;
            bool rendered = false;
            native.MotionStarted += () => { startedAtTick = native.MotionTickCount; rendered = pin.LandingDecoration?.HasRendered == true; };
            pin.StartLandingEffect();
            // Not in the opening turn: the ornament comes at the first motion tick, and the landing starts
            // once its first frame is drawn, with the ornament's clock starting at the same moment.
            Assert.Null(pin.LandingDecoration);
            PinNativeSpeedTestWait.Until(() => pin.LandingDecoration is not null);
            PinLandingDecoration decoration = pin.LandingDecoration!;
            Assert.False(decoration.IsClosed);
            Assert.Equal(native.Handle, NativePinWindowInterop.GetWindow(new WindowInteropHelper(decoration).Handle, 4));
            Assert.True(native.IsLanding);
            PinNativeSpeedTestWait.Until(() => startedAtTick > 0);
            Assert.True(rendered);
            pin.MovePhysicalForTest(native.Bounds.Left + 30, native.Bounds.Top);
            Assert.True(decoration.IsClosed);
        }
        finally
        {
            Close(pin);
            PinLandingDecoration.ApplyCaptureExclusion = exclusion;
            PinLandingDecoration.ReadRemoteSession = remote;
            PinLandingDecoration.ReadLayeredSupport = layered;
            PinLandingDecoration.Throttle = throttle;
        }
    });

    [Fact]
    public void DialogsOfANativePinAreOwnedByItsWindowAndTheMessageReturnsToTheWpfWindowAtTheSwitch() => Run(false, () =>
    {
        PinWindow pin = NativePin(new PinSettings { LightweightImagePins = true });
        try
        {
            NativePinWindow native = pin.NativeWindowForTest!;
            Assert.Null(pin.DialogOwner);
            Assert.Equal(native.Handle, pin.DialogOwnerHandle);
            pin.ReportSaveResult(new PinSaveResult(PinSaveStatus.Failed, ErrorMessage: "x"));
            PinNativeSpeedTestWait.Until(() => pin.FeedbackWindowForTest is { IsVisible: true });
            var shown = (FrameworkElement)pin.FeedbackSurfaceForTest;
            PinNativeSpeedTestWait.Until(() => SurfaceRect(pin.FeedbackWindowForTest!).Bottom == native.Bounds.Bottom);
            pin.FeedbackWindowForTest!.UpdateLayout();
            Point onNative = shown.PointToScreen(new Point(0, 0));
            Size sizeOnNative = new(shown.ActualWidth, shown.ActualHeight);

            pin.ActivateAfterSwitchForTest = static _ => { };
            // Until the native window goes, the message is already inside the WPF window and the message
            // window still shows it, so neither window's frame lacks it.
            bool? inChromeAtRetire = null, windowShownAtRetire = null;
            pin.BeforeNativeRetiredForTest = (_, _) =>
            {
                inChromeAtRetire = ((FrameworkElement)pin.FeedbackSurfaceForTest).Parent is Grid;
                windowShownAtRetire = pin.FeedbackWindowForTest is { IsVisible: true };
            };
            Assert.True(pin.SwitchToWpf(PinSwitchReason.Command));
            PinNativeSpeedTestWait.Until(() => !pin.IsHostSwitchPending);
            pin.BeforeNativeRetiredForTest = null;
            Assert.True(inChromeAtRetire);
            Assert.True(windowShownAtRetire);
            Assert.Null(pin.FeedbackWindowForTest);
            Assert.True(pin.IsFeedbackVisible);
            var message = (FrameworkElement)pin.FeedbackSurfaceForTest;
            Assert.IsType<Grid>(message.Parent);
            // The message is on the WPF window's screen, not only in its tree.
            pin.UpdateLayout();
            Assert.True(message.IsVisible);
            Assert.True(message.ActualWidth > 0 && message.ActualHeight > 0);
            Assert.True(message.Opacity > 0);
            // Same place and size on the screen as on the message window.
            Point onWpf = message.PointToScreen(new Point(0, 0));
            Assert.Equal(sizeOnNative, new Size(message.ActualWidth, message.ActualHeight));
            Assert.Equal(onNative, onWpf);
            Assert.Same(pin, pin.DialogOwner);
            Assert.Equal(IntPtr.Zero, pin.DialogOwnerHandle);
        }
        finally { Close(pin); }
    });

    // ----- Harness -----

    private static PinWindow NativePin(PinSettings settings,
        Func<BitmapSource, CancellationToken, Task<OcrResult>>? recognize = null, int width = 160, int height = 100)
    {
        BitmapSource picture = PinNativeTestImages.Picture(width, height);
        PinContent content = PinContent.FromImage(picture);
        var pin = new PinWindow(content, new PinViewState(width, height, 1, 1, .1), -11900, -11900, () => settings,
            recognizeSelection: recognize)
        {
            ReadWorkArea = static _ => OffscreenArea,
        };
        NativePinHost host = NativePinHost.Create(pin, content, -11900, -11900, 1);
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

    private static string FeedbackText(PinWindow pin) => pin.FeedbackTextForTest;

    private static string[] Headers(ContextMenu menu) =>
        [.. menu.Items.OfType<object>().Select(item => item is MenuItem menuItem ? menuItem.Header?.ToString() ?? "" : "-")];

    private static void Click(ContextMenu menu, string header) =>
        menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, header)).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    private static void Post(IntPtr window, uint message, int key) =>
        Assert.True(PostMessageW(window, message, new IntPtr(key), new IntPtr(1)));

    private static int PointParam(double x, double y) => (((int)y & 0xFFFF) << 16) | ((int)x & 0xFFFF);

    private static RectD WindowRect(IntPtr window)
    {
        Assert.True(NativePinWindowInterop.GetWindowRect(window, out var rect));
        return new RectD(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    private static RectD SurfaceRect(Window window) => WindowRect(new WindowInteropHelper(window).Handle);

    private static void Run(bool animations, Action test) => StaTestHost.Run(() =>
    {
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        Func<IntPtr, double?>? scale = NativePinWindow.ReadWindowScaleForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = animations;
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

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
}
