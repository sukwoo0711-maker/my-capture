using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Capture;
using MyCapture.App.Pinning;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Pin;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Platform.Display;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PinClickThroughHandleTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HandleHasItsOwnInputAndExclusionAndDisablesOnlyItsPin(bool layered) => StaTestHost.Run(() =>
    {
        using var scope = new HandleScope(layered);
        using var language = UiText.UseLanguage("ko-KR");
        var settings = new PinSettings();
        PinWindow first = Pin(settings), second = Pin(settings);
        try
        {
            first.Show(); second.Show();
            Assert.Null(first.ClickThroughHandleForTest);
            first.ApplyClickThrough(true); second.ApplyClickThrough(true);
            PinClickThroughHandle one = Assert.IsType<PinClickThroughHandle>(first.ClickThroughHandleForTest);
            PinClickThroughHandle two = Assert.IsType<PinClickThroughHandle>(second.ClickThroughHandleForTest);
            Assert.NotSame(one, two);
            Assert.Same(first, one.Owner);
            Assert.Equal(layered, one.AllowsTransparency);
            Assert.True(one.IsVisible);
            Assert.True(two.IsVisible);
            Assert.Equal(24, one.Width);
            Assert.Equal(24, one.Height);
            Assert.Equal(.7, layered ? one.Opacity : one.RecoveryButton.Opacity, 3);
            Assert.False(one.ShowActivated);
            Assert.False(one.ShowInTaskbar);
            Assert.False(one.RecoveryButton.Focusable);
            Assert.Equal("클릭 허용 끄기", AutomationProperties.GetName(one.RecoveryButton));
            Assert.True(WindowStyleFacade.IsClickThrough(new WindowInteropHelper(first).Handle));
            Assert.False(WindowStyleFacade.IsClickThrough(new WindowInteropHelper(one).Handle));
            Assert.True(one.IsCaptureExcluded);
            Assert.True(one.HintForTest.IsCaptureExcluded);
            Assert.Contains(one, scope.Excluded);
            Assert.Contains(one.HintForTest, scope.Excluded);
            // The existing bottom message still carries the keyboard and tray way back.
            Assert.True(first.IsFeedbackVisible);

            one.RecoveryButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.False(first.State.IsClickThrough);
            Assert.False(WindowStyleFacade.IsClickThrough(new WindowInteropHelper(first).Handle));
            Assert.False(one.IsVisible);
            Assert.False(one.HintForTest.IsVisible);
            Assert.True(second.State.IsClickThrough);
            Assert.True(two.IsVisible);
        }
        finally { first.Close(); second.Close(); }
    });

    [Fact]
    public void HandleFollowsMovesResizesVisibilityAndClosesWithItsPin() => StaTestHost.Run(() =>
    {
        using var scope = new HandleScope();
        PinWindow pin = Pin(new PinSettings());
        try
        {
            pin.ApplyClickThrough(true);
            Assert.Null(pin.ClickThroughHandleForTest);
            pin.Show(); Drain();
            PinClickThroughHandle handle = Assert.IsType<PinClickThroughHandle>(pin.ClickThroughHandleForTest);
            AssertPosition(pin, handle);
            pin.Left += 84; pin.Top += 41; Drain();
            AssertPosition(pin, handle);
            pin.Width += 160; pin.Height += 80; Drain();
            AssertPosition(pin, handle);
            pin.HidePin();
            Assert.False(handle.IsVisible);
            Assert.False(handle.HintForTest.IsVisible);
            pin.ShowPin(); Drain();
            Assert.True(handle.IsVisible);
            AssertPosition(pin, handle);
            pin.Hide();
            Assert.False(handle.IsVisible);
            pin.Show(); Drain();
            Assert.True(handle.IsVisible);
            bool closed = false;
            handle.Closed += (_, _) => closed = true;
            pin.Close();
            Assert.True(closed);
            Assert.False(handle.IsVisible);
            Assert.Null(pin.ClickThroughHandleForTest);
        }
        finally { pin.Close(); }
    });

    [Theory]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void PlacementUsesPhysicalMonitorCoordinatesAndSixDipInset(double scale)
    {
        var pin = new RectD(-1750, -400, 720 * scale, 360 * scale);
        RectD handle = PinClickThroughHandle.Placement(pin, scale);
        Assert.Equal(24 * scale, handle.Width);
        Assert.Equal(24 * scale, handle.Height);
        Assert.Equal(6 * scale, pin.Right - handle.Right);
        Assert.Equal(6 * scale, handle.Top - pin.Top);
    }

    [Fact]
    public void IntroductionIsSharedAcrossPinsOnlyThreeTimesAndDoesNotRepeatOnShow() => StaTestHost.Run(() =>
    {
        using var scope = new HandleScope();
        var settings = new PinSettings();
        var manager = new PinManager(() => settings, NullLogger.Instance)
        {
            // A shared desktop must never see a test pin take focus.
            ActivatePinForTest = _ => { },
            BeforeShowForTest = pin =>
            {
                pin.Left = pin.Top = -10000;
                pin.ReadWorkArea = _ => new RectD(-12000, -12000, 4000, 4000);
            },
        };
        int saved = 0;
        manager.ClickThroughHintShown += () => saved++;
        try
        {
            for (int i = 0; i < 4; i++)
            {
                PinWindow pin = manager.PinImage(Image());
                pin.ApplyClickThrough(true);
                Assert.Equal(Math.Min(i + 1, 3), settings.ClickThroughHintShownCount);
                Assert.Equal(i < 3, pin.ClickThroughHandleForTest!.HintForTest.IsVisible);
                pin.ApplyClickThrough(true);
                pin.HidePin(); pin.ShowPin();
                Assert.False(pin.ClickThroughHandleForTest.HintForTest.IsVisible);
                Assert.Equal(Math.Min(i + 1, 3), saved);
            }
            IReadOnlyList<PinWindow> open = manager.SnapshotOpenPinWindows();
            Assert.Equal(4, open.Count);
            Assert.All(open, pin => Assert.True(pin.ClickThroughHandleForTest!.IsVisible));
            Assert.Equal(4, manager.ClearClickThroughAll());
            Assert.All(open, pin =>
            {
                Assert.False(pin.State.IsClickThrough);
                Assert.False(pin.ClickThroughHandleForTest!.IsVisible);
            });
        }
        finally { manager.CloseAllForExit(); }
    });

    [Fact]
    public void TooltipReadsTheConfiguredShortcutAndOmitsEmptyParentheses() => StaTestHost.Run(() =>
    {
        using var scope = new HandleScope();
        using var language = UiText.UseLanguage("en-US");
        Func<HotkeySettings> previous = ShortcutHelpWindow.ReadHotkeys;
        var keys = new HotkeySettings { ToggleClickThrough = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x54) };
        ShortcutHelpWindow.ReadHotkeys = () => keys;
        PinWindow pin = Pin(new PinSettings());
        try
        {
            pin.Show(); pin.ApplyClickThrough(true);
            PinClickThroughHandle handle = pin.ClickThroughHandleForTest!;
            Assert.Equal("Turn off click-through (Ctrl+Alt+T)", handle.TooltipText);
            Assert.Equal("Ctrl+Alt+T", AutomationProperties.GetAcceleratorKey(handle.RecoveryButton));
            keys.ToggleClickThrough = Hotkey.None;
            handle.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0)
                { RoutedEvent = System.Windows.Input.Mouse.MouseEnterEvent });
            Assert.Equal("Turn off click-through", handle.TooltipText);
            Assert.Equal(1, handle.Opacity);
        }
        finally { pin.Close(); ShortcutHelpWindow.ReadHotkeys = previous; }
    });

    [Fact]
    public void ExclusionFailureDoesNotExposeTheControlsOrConsumeAnIntroduction() => StaTestHost.Run(() =>
    {
        using var scope = new HandleScope();
        PinClickThroughHandle.ApplyCaptureExclusion = _ => false;
        var settings = new PinSettings();
        PinWindow pin = Pin(settings);
        try
        {
            pin.Show();
            Assert.False(pin.ToggleClickThrough());
            Assert.False(pin.State.IsClickThrough);
            Assert.False(pin.ClickThroughHandleForTest!.IsVisible);
            Assert.Equal(0, settings.ClickThroughHintShownCount);
        }
        finally { pin.Close(); }
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HandleIsTheHitTargetOverItsPinOnlyWhileClickThroughIsOn(bool layered) => StaTestHost.Run(() =>
    {
        using var scope = new HandleScope(layered);
        PinWindow pin = Pin(new PinSettings());
        try
        {
            pin.Show(); Drain();
            IntPtr pinHandle = new WindowInteropHelper(pin).Handle;
            pin.ApplyClickThrough(true); Drain();
            PinClickThroughHandle handle = pin.ClickThroughHandleForTest!;
            IntPtr handleWindow = new WindowInteropHelper(handle).Handle;
            var (left, top, right, bottom) = WindowStyleFacade.GetWindowBounds(handleWindow);
            var center = new NativePoint((left + right) / 2, (top + bottom) / 2);
            // The click-through pin is skipped by hit testing, so the handle is the only thing a click can land on.
            Assert.Equal(handleWindow, RootWindowAt(center));
            var (pinLeft, pinTop, pinRight, pinBottom) = pin.PhysicalBounds;
            var pinCenter = new NativePoint((pinLeft + pinRight) / 2, (pinTop + pinBottom) / 2);
            Assert.NotEqual(pinHandle, RootWindowAt(pinCenter));

            pin.ApplyClickThrough(false); Drain();
            // Back to an ordinary pin: the handle is gone and the pin takes the click again.
            Assert.Equal(pinHandle, RootWindowAt(center));
        }
        finally { pin.Close(); }
    });

    [Fact]
    public void ShortcutToggleAndAutomationInvokeUseTheSameHandleWithoutRepeatingTheIntroduction() => StaTestHost.Run(() =>
    {
        using var scope = new HandleScope();
        var settings = new PinSettings();
        PinWindow pin = Pin(settings);
        try
        {
            pin.Show(); Drain();
            Assert.True(pin.ToggleClickThrough());
            PinClickThroughHandle handle = pin.ClickThroughHandleForTest!;
            Assert.True(handle.IsVisible);
            Assert.Equal(1, settings.ClickThroughHintShownCount);
            Assert.False(pin.ToggleClickThrough());
            Assert.False(handle.IsVisible);
            Assert.False(handle.HintForTest.IsVisible);
            Assert.True(pin.ToggleClickThrough());
            Assert.Same(handle, pin.ClickThroughHandleForTest);
            Assert.True(handle.IsVisible);
            Assert.Equal(2, settings.ClickThroughHintShownCount);

            // Assistive technology reaches the same command without the pointer.
            var peer = Assert.IsType<System.Windows.Automation.Peers.ButtonAutomationPeer>(
                System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(handle.RecoveryButton));
            Assert.Equal("클릭 허용 끄기", peer.GetName());
            var invoke = (System.Windows.Automation.Provider.IInvokeProvider)peer.GetPattern(
                System.Windows.Automation.Peers.PatternInterface.Invoke)!;
            invoke.Invoke();
            // UI Automation queues the click on the dispatcher.
            Drain();
            Assert.False(pin.State.IsClickThrough);
            Assert.False(handle.IsVisible);
        }
        finally { pin.Close(); }
    });

    private static IntPtr RootWindowAt(NativePoint point)
    {
        IntPtr hit = WindowFromPoint(point);
        return hit == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hit, 2);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private readonly record struct NativePoint(int X, int Y);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    private static void AssertPosition(PinWindow pin, PinClickThroughHandle handle)
    {
        var (left, top, right, bottom) = pin.PhysicalBounds;
        RectD expected = PinClickThroughHandle.Placement(new RectD(left, top, right - left, bottom - top), VisualTreeHelper.GetDpi(pin).DpiScaleX);
        var actual = WindowStyleFacade.GetWindowBounds(new WindowInteropHelper(handle).Handle);
        Assert.InRange(Math.Abs(actual.Left - expected.Left), 0, 1);
        Assert.InRange(Math.Abs(actual.Top - expected.Top), 0, 1);
    }

    internal static PinWindow Pin(PinSettings settings) => new(PinContent.FromImage(Image()), new PinViewState(480, 280, 1, 1, .1), -10000, -10000, () => settings)
    {
        ShowActivated = false,
        ReadWorkArea = _ => new RectD(-12000, -12000, 4000, 4000),
    };

    internal static BitmapSource Image()
    {
        byte[] pixels = new byte[480 * 280 * 4];
        Array.Fill(pixels, (byte)0xEE);
        var image = BitmapSource.Create(480, 280, 96, 96, PixelFormats.Bgra32, null, pixels, 480 * 4);
        image.Freeze();
        return image;
    }

    private static void Drain() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    internal sealed class HandleScope : IDisposable
    {
        private readonly Func<Window, bool> _exclusion = PinClickThroughHandle.ApplyCaptureExclusion;
        private readonly Func<bool> _probe = CaptureWindowExclusion.LayeredWindowProbe;
        private readonly bool? _motion = FluidMotion.AnimationsEnabledOverrideForTest;
        internal List<Window> Excluded { get; } = [];
        internal HandleScope(bool layered = true)
        {
            CaptureWindowExclusion.LayeredWindowProbe = () => layered;
            PinClickThroughHandle.ApplyCaptureExclusion = window => { Excluded.Add(window); return true; };
            FluidMotion.AnimationsEnabledOverrideForTest = false;
        }
        public void Dispose()
        {
            PinClickThroughHandle.ApplyCaptureExclusion = _exclusion;
            CaptureWindowExclusion.LayeredWindowProbe = _probe;
            FluidMotion.AnimationsEnabledOverrideForTest = _motion;
        }
    }
}
