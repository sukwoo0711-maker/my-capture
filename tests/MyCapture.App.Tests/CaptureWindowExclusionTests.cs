using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Capture;
using MyCapture.App.Editing;
using MyCapture.App.Gallery;
using MyCapture.App.Pinning;
using MyCapture.App.Recording;
using MyCapture.App.Themes;
using MyCapture.Core.Primitives;
using MyCapture.Core.Recording;
using MyCapture.Core.Settings;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using MyCapture.Platform.Shell;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class CaptureWindowExclusionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProbeIsLazyCachedAndReplacementInvalidatesCache(bool supported) => StaTestHost.Run(() =>
    {
        Func<bool> original = CaptureWindowExclusion.LayeredWindowProbe;
        ILogger previousLog = CaptureWindowExclusion.Logger;
        var log = new ModeLog();
        int calls = 0;
        try
        {
            CaptureWindowExclusion.Logger = log;
            CaptureWindowExclusion.LayeredWindowProbe = () => { calls++; return supported; };
            Assert.Equal(0, calls);
            for (int i = 0; i < 3; i++) Assert.Equal(supported, CaptureWindowExclusion.SupportsLayeredWindows);
            Assert.Equal(1, calls);
            Assert.Equal(LogLevel.Information, Assert.Single(log.Entries).Level);
            Assert.Contains(supported ? "layered windows" : "opaque windows", log.Entries[0].Text);
            CaptureWindowExclusion.LayeredWindowProbe = () => { calls++; return !supported; };
            Assert.Equal(!supported, CaptureWindowExclusion.SupportsLayeredWindows);
            Assert.Equal(!supported, CaptureWindowExclusion.SupportsLayeredWindows);
            Assert.Equal(2, calls);
            Assert.Equal(2, log.Entries.Count);
        }
        finally
        {
            CaptureWindowExclusion.LayeredWindowProbe = original;
            CaptureWindowExclusion.Logger = previousLog;
        }
    });

    [Fact]
    public void NativeProbeLeavesNoWindowAndDoesNotChangeForeground() => StaTestHost.Run(() =>
    {
        var anchor = new Window { ShowActivated = false, ShowInTaskbar = false };
        try
        {
            _ = new WindowInteropHelper(anchor).EnsureHandle();
            IntPtr foreground = NativeExclusion.GetForegroundWindow();
            IntPtr[] before = NativeExclusion.ThreadWindows();
            bool probed = NativeExclusion.SupportsLayeredWindows;
            Assert.Equal(before, NativeExclusion.ThreadWindows());
            Assert.Equal(foreground, NativeExclusion.GetForegroundWindow());
            var layered = new Window { AllowsTransparency = true, WindowStyle = WindowStyle.None,
                ShowInTaskbar = false, ShowActivated = false, Width = 1, Height = 1, Left = -32000, Top = -32000 };
            try { Assert.Equal(probed, CaptureWindowExclusion.TryApply(layered)); }
            finally { layered.Close(); }
        }
        finally { anchor.Close(); }
    });

    [Fact]
    public void TryApplyStillRejectsZeroHandle() => Assert.False(CaptureWindowExclusion.TryApply(IntPtr.Zero));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CountdownSelectsModeBeforeHandleAndAcceptsExclusion(bool supported) => StaTestHost.Run(() =>
    {
        using var mode = new ExclusionMode(supported);
        var window = new CountdownWindow(3);
        try
        {
            Assert.Equal(NativeExclusion.ExpectsSuccess(supported),
                CaptureWindowExclusion.TryApply(window)); // App's recording countdown gate.
            NativeExclusion.AssertWindow(window, supported);
            Border surface = Assert.IsType<Border>(window.Content);
            Assert.Equal(new CornerRadius(supported ? 18 : 0), surface.CornerRadius);
            if (!supported) Assert.Null(surface.Effect);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QuickAccessSelectsModeAndKeepsItsContentWidth(bool supported) => StaTestHost.Run(() =>
    {
        using var mode = new ExclusionMode(supported);
        QuickAccessWindow window = QuickAccess();
        try
        {
            _ = new WindowInteropHelper(window).EnsureHandle();
            NativeExclusion.AssertWindow(window, supported);
            Border surface = Assert.IsType<Border>(window.Content);
            Assert.Equal(new Thickness(supported ? 8 : 0), surface.Margin);
            Assert.Equal(new CornerRadius(supported ? 12 : 0), surface.CornerRadius);
            Assert.Equal(328, window.Width - surface.Margin.Left - surface.Margin.Right);
            if (!supported) Assert.Null(surface.Effect);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void IntentionallyCapturableQuickAccessAndWallClockStayLayered() => StaTestHost.Run(() =>
    {
        using var mode = new ExclusionMode(false);
        QuickAccessWindow quick = QuickAccess(exclude: false);
        var clock = new RecordingWallClockWindow(new RectD(100, 100, 640, 480), 1, 30);
        try
        {
            foreach (Window window in new Window[] { quick, clock })
            {
                IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();
                Assert.True(window.AllowsTransparency);
                Assert.True(NativeExclusion.IsLayered(hwnd));
                Assert.Equal(0u, NativeExclusion.Affinity(hwnd));
            }
        }
        finally { quick.Close(); clock.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PinHoverBarSelectsModeAndRevealKeepsExclusion(bool supported) => StaTestHost.Run(() =>
    {
        using var mode = new ExclusionMode(supported);
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = true;
        var window = new PinHoverBar();
        try
        {
            window.Show();
            window.Reveal();
            NativeExclusion.AssertWindow(window, supported);
            Assert.Equal(NativeExclusion.ExpectsSuccess(supported), window.IsCaptureExcluded);
            Assert.Equal(new CornerRadius(supported ? 8 : 0), Assert.IsType<Border>(window.Content).CornerRadius);
            if (!supported) { Assert.Equal(1, window.Opacity); Assert.False(window.HasAnimatedProperties); }
        }
        finally { window.Close(); FluidMotion.AnimationsEnabledOverrideForTest = motion; }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryShellNotificationSelectsModeAndExcludesItsActualHwnd(bool supported) => StaTestHost.Run(() =>
    {
        using var mode = new ExclusionMode(supported);
        using var shell = new ThemedShellPresenter(() => false);
        Action[] show =
        [
            () => shell.ShowNotification("Title", "Message", TrayBalloonKind.Information),
            () => shell.ShowColorCopy(new ColorCopyFeedback("#123456", 0x12, 0x34, 0x56)),
            () => shell.ShowCaptureReceipt("Captured", () => { }),
            () => shell.ShowActionNotification("Title", "Message", "Open", () => { }),
        ];
        for (int i = 0; i < show.Length; i++)
        {
            show[i]();
            Window window = Assert.IsType<Window>(shell.NotificationWindowForTest);
            NativeExclusion.AssertWindow(window, supported);
            Grid surface = Assert.IsType<Grid>(window.Content);
            Assert.Equal(new Thickness(supported ? ThemedShellPresenter.ShadowMargin : 0), surface.Margin);
            Assert.Equal(i == 2 ? 320 : ThemedShellPresenter.CardWidth,
                window.Width - surface.Margin.Left - surface.Margin.Right);
            Border card = Assert.IsType<Border>(surface.Children[1]);
            Assert.Equal(supported ? ThemedShellPresenter.CardCorner : new CornerRadius(0), card.CornerRadius);
            if (!supported) Assert.All(surface.Children.OfType<Border>(), border => Assert.Null(border.Effect));
            shell.Dismiss();
        }
    });

    [Fact]
    public void OpaqueNotificationPlacementKeepsTheVisibleCardEdgeGap()
    {
        var work = new RectD(-1600, 100, 1600, 900);
        Rect bounds = ThemedShellPresenter.NotificationBounds(work, 1, 360, 120, shadowMargin: 0);
        Assert.Equal(work.Right - ThemedShellPresenter.EdgeGap, bounds.Right);
        Assert.Equal(work.Bottom - ThemedShellPresenter.EdgeGap, bounds.Bottom);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RecordingAndPinMenusSelectPopupModeBeforeOpeningAndReopening(bool supported, bool pinMenu) => StaTestHost.Run(() =>
    {
        using var mode = new ExclusionMode(supported);
        Window owner;
        ContextMenu menu;
        if (pinMenu)
        {
            var bar = new PinHoverBar();
            owner = bar;
            menu = bar.CreateOpacityMenu();
        }
        else
        {
            var controls = new RecordingControlWindow(new RectD(100, 100, 160, 100), new RecordingSettings(),
                () => throw new InvalidOperationException(), () => "unused.mp4", NullLogger.Instance);
            owner = controls;
            menu = controls.CreateMoreMenu();
        }
        try
        {
            owner.ShowActivated = false;
            owner.Show();
            owner.UpdateLayout();
            _ = menu.ApplyTemplate(); // Initial template application must not create a transparent HWND.
            int opened = 0;
            menu.Opened += (_, _) =>
            {
                // Inspect the actual popup before WPF destroys a rejected layered HWND.
                opened++;
                var source = Assert.IsType<HwndSource>(PresentationSource.FromVisual(menu));
                var popup = Assert.IsType<Popup>(menu.Parent);
                Assert.Equal(supported, popup.AllowsTransparency);
                Assert.Equal(supported, NativeExclusion.IsLayered(source.Handle));
                NativeExclusion.AssertAffinity(source.Handle, supported);
                if (!supported)
                {
                    Assert.False(menu.HasDropShadow);
                    Assert.Equal(PopupAnimation.None, popup.PopupAnimation);
                    menu.ApplyTemplate();
                    Border surface = Assert.IsType<Border>(VisualTreeHelper.GetChild(menu, 0));
                    Assert.Equal(new CornerRadius(0), surface.CornerRadius);
                    Assert.Null(surface.Effect);
                }
            };
            for (int attempt = 0; attempt < 2; attempt++)
            {
                menu.IsOpen = true;
                if (!supported && attempt == 0)
                {
                    // The first open is still waiting for HWND creation: no layered flash.
                    Assert.Null(PresentationSource.FromVisual(menu));
                    Assert.False(Assert.IsType<Popup>(menu.Parent).AllowsTransparency);
                }
                NativeExclusion.Drain();
                Assert.Equal(attempt + 1, opened);
                Assert.Equal(NativeExclusion.ExpectsSuccess(supported), menu.IsOpen);
                menu.IsOpen = false;
                NativeExclusion.Drain();
            }
        }
        finally { menu.IsOpen = false; owner.Close(); }
    });

    [Fact]
    public Task ExistingOpaqueCaptureSurfacesStillAcceptExclusion() => SettingsThemePreviewTests.RunIsolated(
        nameof(ExistingOpaqueCaptureSurfacesStillAcceptExclusion), () =>
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
                application.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml"),
                });
            using var mode = new ExclusionMode(false);
            var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(32, 32, 96, 96,
                PixelFormats.Bgr32, null, new byte[32 * 32 * 4], 128);
            bitmap.Freeze();
            var frame = new FrozenFrame(bitmap, new RectD(0, 0, 32, 32), null, 0);
            Window[] windows =
            [
                new CaptureOverlayWindow(frame, abortOnFocusLoss: false, showMagnifier: false),
                new AnnotationEditorWindow(frame, new RectD(0, 0, 32, 32), bitmap),
                new GalleryVideoPlayerWindow(),
            ];
            try
            {
                foreach (Window window in windows)
                {
                    Assert.False(window.AllowsTransparency);
                    Assert.True(CaptureWindowExclusion.TryApply(window));
                    IntPtr hwnd = new WindowInteropHelper(window).Handle;
                    Assert.False(NativeExclusion.IsLayered(hwnd));
                    Assert.Equal(0x11u, NativeExclusion.Affinity(hwnd));
                }
            }
            finally { foreach (Window window in windows) window.Close(); }
        }, typeof(CaptureWindowExclusionTests));

    [Fact]
    public Task OpaqueBackingTracksThemeColorWithFullAlpha() => SettingsThemePreviewTests.RunIsolated(
        nameof(OpaqueBackingTracksThemeColorWithFullAlpha), () =>
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.Resources["Surface.Floating"] = new SolidColorBrush(Color.FromArgb(128, 30, 40, 50));
            SolidColorBrush brush = Assert.IsType<SolidColorBrush>(CaptureWindowExclusion.OpaqueBackground());
            Assert.Equal(Color.FromRgb(30, 40, 50), brush.Color);
            Assert.Equal(1, brush.Opacity);
            application.Resources["Surface.Floating"] = new SolidColorBrush(Color.FromArgb(64, 220, 230, 240)) { Opacity = 0.5 };
            ThemeBrushes.Refresh(application.Resources);
            NativeExclusion.Drain();
            Assert.Equal(Color.FromRgb(220, 230, 240), brush.Color);
            Assert.Equal(1, brush.Opacity);
        }, typeof(CaptureWindowExclusionTests));

    private static QuickAccessWindow QuickAccess(bool exclude = true)
    {
        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8);
        return new QuickAccessWindow(bitmap, () => Task.FromResult(true), () => { }, () => Task.CompletedTask,
            () => Task.FromResult(true), () => { }, () => Task.FromResult(string.Empty), excludeFromCapture: exclude);
    }

    private sealed class ModeLog : ILogger
    {
        internal List<(LogLevel Level, string Text)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }
}

internal sealed class ExclusionMode : IDisposable
{
    private readonly Func<bool> _previous = CaptureWindowExclusion.LayeredWindowProbe;
    internal ExclusionMode(bool supported) => CaptureWindowExclusion.LayeredWindowProbe = () => supported;
    public void Dispose() => CaptureWindowExclusion.LayeredWindowProbe = _previous;
}

internal static class NativeExclusion
{
    // Independent of ExclusionMode, shared by both test classes for this test process.
    private static readonly Lazy<bool> RealLayeredSupport = new(() =>
    {
        Func<bool> previous = CaptureWindowExclusion.LayeredWindowProbe;
        try
        {
            CaptureWindowExclusion.LayeredWindowProbe = typeof(CaptureWindowExclusion)
                .GetMethod("ProbeLayeredWindow", BindingFlags.Static | BindingFlags.NonPublic)!
                .CreateDelegate<Func<bool>>();
            IntPtr foreground = GetForegroundWindow();
            IntPtr[] before = ThreadWindows();
            bool supported = CaptureWindowExclusion.SupportsLayeredWindows;
            Assert.Equal(before, ThreadWindows());
            Assert.Equal(foreground, GetForegroundWindow());
            return supported;
        }
        finally { CaptureWindowExclusion.LayeredWindowProbe = previous; }
    });

    internal static bool SupportsLayeredWindows => RealLayeredSupport.Value;
    internal static bool ExpectsSuccess(bool layered) => !layered || SupportsLayeredWindows;

    internal static void AssertAffinity(IntPtr hwnd, bool layered)
    {
        bool expected = ExpectsSuccess(layered);
        // Verify the product already applied affinity before exercising the native call again.
        if (expected) Assert.Equal(0x11u, Affinity(hwnd));
        Assert.Equal(expected, CaptureWindowExclusion.TryApply(hwnd));
    }

    internal static void AssertWindow(Window window, bool layered)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        Assert.NotEqual(IntPtr.Zero, hwnd);
        Assert.Equal(layered, window.AllowsTransparency);
        Assert.Equal(layered, IsLayered(hwnd));
        AssertAffinity(hwnd, layered);
        if (!layered)
        {
            var background = Assert.IsType<SolidColorBrush>(window.Background);
            Assert.Equal(255, background.Color.A);
            Assert.Equal(1, background.Opacity);
            Assert.False(ModernWindowChrome.GetEnabled(window));
        }
    }

    internal static bool IsLayered(IntPtr hwnd) => (GetWindowLongPtr(hwnd, -20).ToInt64() & 0x80000) != 0;
    internal static uint Affinity(IntPtr hwnd)
    {
        Assert.True(GetWindowDisplayAffinity(hwnd, out uint value));
        return value;
    }
    internal static IntPtr[] ThreadWindows()
    {
        var windows = new List<IntPtr>();
        Assert.True(EnumThreadWindows(GetCurrentThreadId(), (hwnd, _) => { windows.Add(hwnd); return true; }, IntPtr.Zero));
        return windows.OrderBy(hwnd => hwnd.ToInt64()).ToArray();
    }
    internal static T Field<T>(object owner, string name) => (T)owner.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    internal static void Drain()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        _ = System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);
    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    private delegate bool EnumWindow(IntPtr hwnd, IntPtr parameter);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumThreadWindows(uint thread, EnumWindow callback, IntPtr parameter);
}
