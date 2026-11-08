using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Capture;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using Xunit;
using static MyCapture.App.Tests.WindowDetectOverlayTests;

namespace MyCapture.App.Tests;

public sealed class NativeSelectorFocusTests
{
    // A shown native selector window must fit on the CI desktop (CaptureCueEvidenceTests.ShownWidth).
    private static readonly RectD Bounds = new(0, 0, CaptureCueEvidenceTests.ShownWidth, 720);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TabVisitsTheBarInBothDirectionsThenReturnsToBackground(bool reverse) => StaTestHost.Run(() =>
    {
        using var owner = Owner();
        try
        {
            Show(owner);
            NativeSelectorChrome chrome = owner.Chrome!;
            CaptureModeBar bar = owner.ModeBar;
            ButtonBase[] order = [bar.CaptureButton, bar.WindowButton, bar.FullScreenButton,
                bar.RatioButton, bar.TextButton, bar.ColorButton, bar.HelpButton, bar.CancelButton];
            if (reverse) Array.Reverse(order);
            Assert.True(chrome.Windows.Count >= 5, "The passive key panel and magnifier must be present during the focus walk.");
            AssertBackgroundFocus(owner);
            Assert.True(owner.HandleKey(Key.Tab, reverse ? ModifierKeys.Shift : ModifierKeys.None));
            foreach (ButtonBase expected in order)
            {
                AssertBarFocus(expected);
                if (reverse) chrome.NavigateTab(true);
                else Press(expected, Key.Tab);
            }
            AssertBackgroundFocus(owner);
            Assert.False(owner.IsClosed);
        }
        finally { owner.Close(); NativeSelectorChromeCache.Trim(); }
    });

    [Fact]
    public void TabSkipsDisabledCollapsedAndNonTabStopButtons() => StaTestHost.Run(() =>
    {
        using var owner = Owner();
        try
        {
            Show(owner);
            CaptureModeBar bar = owner.ModeBar;
            bar.WindowButton.IsEnabled = false;
            bar.FullScreenButton.Visibility = Visibility.Collapsed;
            KeyboardNavigation.SetIsTabStop(bar.RatioButton, false);
            bar.TextButton.Visibility = Visibility.Collapsed;
            owner.HandleKey(Key.Tab, ModifierKeys.None);
            AssertBarFocus(bar.CaptureButton);
            Press(bar.CaptureButton, Key.Tab);
            AssertBarFocus(bar.ColorButton);
            Press(bar.ColorButton, Key.Tab);
            AssertBarFocus(bar.HelpButton);
            Press(bar.HelpButton, Key.Tab);
            AssertBarFocus(bar.CancelButton);
            Press(bar.CancelButton, Key.Tab);
            AssertBackgroundFocus(owner);
        }
        finally { owner.Close(); NativeSelectorChromeCache.Trim(); }
    });

    [Fact]
    public void ExplicitTabIndicesPrecedeTheVisualOrder() => StaTestHost.Run(() =>
    {
        using var owner = Owner();
        try
        {
            Show(owner);
            CaptureModeBar bar = owner.ModeBar;
            KeyboardNavigation.SetTabIndex(bar.TextButton, 0);
            KeyboardNavigation.SetTabIndex(bar.CancelButton, 1);
            owner.HandleKey(Key.Tab, ModifierKeys.None);
            AssertBarFocus(bar.TextButton);
            Press(bar.TextButton, Key.Tab);
            AssertBarFocus(bar.CancelButton);
            Press(bar.CancelButton, Key.Tab);
            AssertBarFocus(bar.CaptureButton);
            owner.Chrome!.NavigateTab(true);
            AssertBarFocus(bar.CancelButton);
            owner.Chrome.NavigateTab(true);
            AssertBarFocus(bar.TextButton);
            owner.Chrome.NavigateTab(true);
            AssertBackgroundFocus(owner);
        }
        finally { owner.Close(); NativeSelectorChromeCache.Trim(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TabBeforeChromeReadyIsDeliveredWhenTheBarCanReceiveFocus(bool reverse) => StaTestHost.Run(() =>
    {
        using var owner = Owner();
        try
        {
            owner.Show();
            Assert.False(owner.IsChromeReady);
            Assert.Null(owner.Chrome);
            Assert.True(owner.HandleKey(Key.Tab, reverse ? ModifierKeys.Shift : ModifierKeys.None));
            AssertBackgroundFocus(owner);
            PumpUntil(() => owner.IsChromeReady);
            AssertBarFocus(reverse ? owner.ModeBar.CancelButton : owner.ModeBar.CaptureButton);
        }
        finally { owner.Close(); NativeSelectorChromeCache.Trim(); }
    });

    [Fact]
    public void EnterChangesModeWithoutDroppingKeyboardFocusAndPointerActivationReturnsIt() => StaTestHost.Run(() =>
    {
        using var owner = Owner();
        try
        {
            Show(owner);
            CaptureModeBar bar = owner.ModeBar;
            owner.HandleKey(Key.Tab, ModifierKeys.None);
            for (int i = 0; i < 4; i++) owner.Chrome!.NavigateTab(false);
            AssertBarFocus(bar.TextButton);
            Press(bar.TextButton, Key.Enter);
            Assert.Equal(CaptureSelectorMode.Text, owner.Mode);
            AssertBarFocus(bar.TextButton);

            // A synthetic routed Click also needs the raw input provider's device context.
            // Supply it without moving or clicking the machine's real cursor.
            WithInputDevice(Mouse.PrimaryDevice, () =>
                bar.CaptureButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, bar.CaptureButton)));
            Assert.Equal(CaptureSelectorMode.Capture, owner.Mode);
            AssertBackgroundFocus(owner);
        }
        finally { owner.Close(); NativeSelectorChromeCache.Trim(); }
    });

    private static void Press(ButtonBase source, Key key)
    {
        KeyEventArgs args = KeyArgs(key);
        args.RoutedEvent = Keyboard.PreviewKeyDownEvent;
        WithInputDevice(Keyboard.PrimaryDevice, () => source.RaiseEvent(args));
        Assert.True(args.Handled);
    }

    private static void WithInputDevice(InputDevice device, Action action)
    {
        PropertyInfo recentDevice = typeof(InputManager).GetProperty(nameof(InputManager.MostRecentInputDevice))!;
        InputDevice? previous = InputManager.Current.MostRecentInputDevice;
        recentDevice.SetValue(InputManager.Current, device);
        try { action(); }
        finally { recentDevice.SetValue(InputManager.Current, previous); }
    }

    private static void AssertBarFocus(ButtonBase expected)
    {
        Assert.Same(expected, Keyboard.FocusedElement);
        Assert.True(expected.IsKeyboardFocused);
        Window window = Assert.IsAssignableFrom<Window>(Window.GetWindow(expected));
        Assert.Equal(new WindowInteropHelper(window).Handle, GetFocus());
    }

    private static void AssertBackgroundFocus(NativeCaptureOverlay owner) => Assert.Equal(owner.Handle, GetFocus());

    private static void Show(NativeCaptureOverlay owner)
    {
        owner.Show();
        PumpUntil(() => owner.IsChromeReady && owner.KeyPanelForTest is { Visibility: Visibility.Visible });
    }

    private static NativeCaptureOverlay Owner()
    {
        NativeSelectorChromeCache.Trim();
        int width = (int)Bounds.Width, height = (int)Bounds.Height;
        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null,
            new byte[width * height * 4], width * 4);
        bitmap.Freeze();
        var owner = new NativeCaptureOverlay(Bounds, false, true)
        {
            ReadCursor = () => new PointD(700, 120),
            ReadMonitors = () => [new MonitorInfo("test", Bounds, Bounds, 96, true)],
            DetectWindows = true,
            TextCaptureAvailable = true,
            CopyColorText = _ => Task.FromResult(true),
            ShowKeyPanel = true,
        };
        owner.AttachFrame(new FrozenFrame(bitmap, Bounds, null, 0));
        return owner;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();
}
