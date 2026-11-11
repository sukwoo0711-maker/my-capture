using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
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
/// Lightweight picture pins, increment 1, through the real PinManager and real HWNDs on an off-screen
/// stand-in monitor. Input is posted to the test's own windows; the real foreground, pointer and
/// keyboard are never used, and activation uses SetActiveWindow on this thread only.
/// </summary>
public sealed class PinLightweightHostWindowTests
{
    private static readonly RectD OffscreenArea = new(-12000, -12000, 1600, 1000);
    private static readonly MonitorInfo Offscreen = new("PINLIGHT-OFFSCREEN", OffscreenArea, OffscreenArea, 96, false);

    [Fact]
    public void ManagerOpensAPictureOnTheNativeHostAndItsListsAndCommandsWork() => Run(animations: false, () =>
    {
        var settings = new PinSettings { LightweightImagePins = true };
        var manager = Manager(settings);
        try
        {
            PinWindow first = manager.PinImage(Picture(120, 80));
            PinWindow second = manager.PinImage(Picture(90, 60));
            foreach (PinWindow pin in new[] { first, second })
            {
                Assert.True(pin.IsNativeHosted);
                NativePinWindow native = Assert.IsType<NativePinWindow>(pin.NativeWindowForTest);
                Assert.True(native.IsVisible);
                Assert.True(pin.IsShown);
                // The WPF window is never shown and has no HWND.
                Assert.False(pin.IsVisible);
                Assert.Equal(IntPtr.Zero, new WindowInteropHelper(pin).Handle);
                Assert.Equal(native.Handle, pin.Handle);
                Assert.False(native.IsCaptureExcluded);
                (int left, int top, int right, int bottom) = pin.PhysicalBounds;
                Assert.Equal(native.Bounds, new RectD(left, top, right - left, bottom - top));
                Assert.True(OffscreenArea.Contains(new PointD(native.Bounds.Left, native.Bounds.Top)));
            }
            Assert.Equal(2, manager.Count);
            Assert.Equal([second, first], manager.SnapshotOpenPinWindows());

            // Session snapshot: bottom-to-top from the native handles, whole-surface placement.
            IReadOnlyList<PinManager.ClosedPin> session = manager.SnapshotOpenPins();
            Assert.Equal(2, session.Count);
            Assert.Same(first.PinnedContent, session[0].Content);
            Assert.Same(second.PinnedContent, session[1].Content);
            Assert.Equal(first.NativeWindowForTest!.Bounds, session[0].Placement!.Value.Bounds);

            manager.HideOrShowAll();
            Assert.All(new[] { first, second }, pin =>
            {
                Assert.True(pin.State.IsHidden);
                Assert.False(pin.IsShown);
                Assert.True(pin.IsNativeHosted);
            });
            manager.HideOrShowAll();
            Assert.All(new[] { first, second }, pin => Assert.True(pin.IsShown));

            var activated = new List<PinWindow>();
            manager.ActivatePinForTest = activated.Add;
            first.HidePin();
            manager.ActivateOpenPin(first);
            Assert.Equal([first], activated);
            Assert.True(first.IsShown);
            Assert.True(first.IsNativeHosted);

            manager.CloseAll();
            Assert.Equal(0, manager.Count);
            Assert.True(first.IsClosed);
            Assert.True(second.IsClosed);
            PinNativeSpeedTestWait.Until(() => NativePinWindow.LiveWindowCount == 0);
        }
        finally { manager.CloseAllForExit(); }
    });

    [Fact]
    public void ShowOnlyPinHidesTheOtherNativePins() => Run(animations: false, () =>
    {
        var manager = Manager(new PinSettings { LightweightImagePins = true });
        try
        {
            PinWindow first = manager.PinImage(Picture(60, 40));
            PinWindow second = manager.PinImage(Picture(60, 40));
            first.HidePin();
            // The menu command the WPF pin offers (a native pin reaches its menu after a switch).
            first.RefreshShowOnlyMenuItem();
            first.ContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, UiText.Get("Pin_ShowOnly")))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.True(first.IsShown);
            Assert.False(second.IsShown);
            Assert.True(second.State.IsHidden);
            Assert.True(first.IsNativeHosted && second.IsNativeHosted);
        }
        finally { manager.CloseAllForExit(); }
    });

    [Fact]
    public void ClosingANeverShownPinWindowRaisesClosingAndClosedAndReleasesTheNativeWindow() => Run(animations: false, () =>
    {
        var manager = Manager(new PinSettings { LightweightImagePins = true });
        try
        {
            PinWindow pin = manager.PinImage(Picture(60, 40));
            NativePinWindow native = pin.NativeWindowForTest!;
            IntPtr handle = native.Handle;
            int closing = 0, closed = 0;
            pin.Closing += (_, _) => closing++;
            pin.Closed += (_, _) => closed++;
            pin.Close();
            Assert.Equal(1, closing);
            Assert.Equal(1, closed);
            Assert.True(native.IsClosed);
            Assert.False(IsWindow(handle));
            Assert.Equal(0, manager.Count);
            Assert.Equal(0, NativePinWindow.LiveWindowCount);
        }
        finally { manager.CloseAllForExit(); }
    });

    [Fact]
    public void ExitThenRestoreReopensTheSamePictureOnTheNativeHostAtItsPlace() => Run(animations: false, () =>
    {
        var manager = Manager(new PinSettings { LightweightImagePins = true });
        try
        {
            BitmapSource picture = Picture(120, 80);
            PinWindow pin = manager.PinImage(picture);
            RectD before = pin.NativeWindowForTest!.Bounds;
            Guid logical = pin.LogicalPracticeId;
            pin.CloseWithExit();
            Assert.True(pin.IsClosed);
            PinHistoryTestSupport.Await(manager.LastClosedEncodingForTest);
            Assert.Equal(1, manager.ClosedCount);

            PinWindow reopened = Assert.IsType<PinWindow>(PinHistoryTestSupport.Restore(manager));
            Assert.NotSame(pin, reopened);
            Assert.True(reopened.IsNativeHosted);
            Assert.Equal(before, reopened.NativeWindowForTest!.Bounds);
            Assert.Equal(logical, reopened.LogicalPracticeId);
            PinHistoryTestSupport.EqualPixels(picture, reopened.PinnedContent.Image);

            // An opt-in session restore goes through the same host decision.
            PinManager.ClosedPin snapshot = manager.SnapshotOpenPins().Single();
            reopened.Close();
            PinWindow session = manager.RestoreSessionPin(snapshot);
            Assert.True(session.IsNativeHosted);
            Assert.Equal(before, session.NativeWindowForTest!.Bounds);
        }
        finally { manager.CloseAllForExit(); }
    });

    [Fact]
    public void ThePinIsVisibleToScreenCapturesUntilItsExitStarts() => Run(animations: true, () =>
    {
        Func<IntPtr, bool> previous = PinWindow.ExcludeLeavingPinFromCapture;
        var excluded = new List<IntPtr>();
        PinWindow.ExcludeLeavingPinFromCapture = handle => { excluded.Add(handle); return true; };
        var manager = Manager(new PinSettings { LightweightImagePins = true });
        try
        {
            PinWindow pin = manager.PinImage(Picture(80, 60));
            NativePinWindow native = pin.NativeWindowForTest!;
            PinNativeSpeedTestWait.Until(() => !native.IsLanding);
            Assert.Empty(excluded);
            Assert.False(native.IsCaptureExcluded);

            pin.CloseWithExit();
            Assert.Equal([native.Handle], excluded);
            Assert.True(native.IsCaptureExcluded);
            Assert.True(native.IsExiting);
            Assert.True(pin.IsLeaving);
            PinNativeSpeedTestWait.Until(() => native.IsClosed);
            PinNativeSpeedTestWait.Until(() => NativePinWindow.LiveWindowCount == 0);
            PinNativeSpeedTestWait.Complete(native.DrainAsync());
        }
        finally
        {
            manager.CloseAllForExit();
            PinWindow.ExcludeLeavingPinFromCapture = previous;
        }
    });

    [Fact]
    public void TextSelectionMovesThePinToWpfInPlaceDirectlyBelowAndRunsTheKeyThere() => Run(animations: false, () =>
    {
        var manager = Manager(new PinSettings { LightweightImagePins = true });
        int recognitions = 0;
        // Recognition that never finishes: the pin stays in its recognizing state until it closes.
        manager.RecognizeSelection = (_, token) =>
        {
            Interlocked.Increment(ref recognitions);
            var pending = new TaskCompletionSource<OcrResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            token.Register(() => pending.TrySetCanceled(token));
            return pending.Task;
        };
        try
        {
            PinWindow pin = manager.PinImage(Picture(120, 80));
            NativePinWindow native = pin.NativeWindowForTest!;
            NativePinHost host = pin.NativeHostForTest!;
            host.ReadModifiers = static () => ModifierKeys.None;
            Guid practiceId = pin.PracticeId;
            Assert.True(NativePinWindowInterop.GetWindowRect(native.Handle, out var nativeRect));
            IntPtr nativeHandle = native.Handle;
            IntPtr below = IntPtr.Zero, wpfAtRetire = IntPtr.Zero;
            NativePinWindowInterop.Rect wpfRect = default;
            pin.BeforeNativeRetiredForTest = (nativeWindow, wpf) =>
            {
                below = NativePinWindowInterop.GetWindow(nativeWindow, 2); // GW_HWNDNEXT
                wpfAtRetire = wpf;
                Assert.True(NativePinWindowInterop.GetWindowRect(wpf, out wpfRect));
            };
            var activated = new List<PinWindow>();
            pin.ActivateAfterSwitchForTest = activated.Add;
            var reasons = new List<PinSwitchReason>();
            pin.HostSwitched += reasons.Add;
            int stateChanges = 0;
            pin.ViewStateChanged += () => stateChanges++;
            _ = NativePinWindowInterop.SetActiveWindow(nativeHandle);
            Assert.Equal(nativeHandle, NativePinWindowInterop.GetActiveWindow());

            Assert.True(PostMessageW(nativeHandle, 0x0100, new IntPtr(0x71), new IntPtr(1))); // WM_KEYDOWN VK_F2
            PinNativeSpeedTestWait.Until(() => !pin.IsNativeHosted && !pin.IsHostSwitchPending);

            Assert.Equal([PinSwitchReason.Key], reasons);
            Assert.Equal(practiceId, pin.PracticeId);
            Assert.NotEqual(IntPtr.Zero, wpfAtRetire);
            Assert.Equal(wpfAtRetire, below);
            Assert.Equal(wpfAtRetire, pin.Handle);
            // Same physical rectangle while both windows exist.
            Assert.Equal(nativeRect.Left, wpfRect.Left);
            Assert.Equal(nativeRect.Top, wpfRect.Top);
            Assert.Equal(nativeRect.Right, wpfRect.Right);
            Assert.Equal(nativeRect.Bottom, wpfRect.Bottom);
            Assert.Equal([pin], activated);
            Assert.True(stateChanges > 0);
            Assert.True(native.IsClosed);
            Assert.False(IsWindow(nativeHandle));
            Assert.True(pin.IsVisible);
            Assert.True(pin.IsShown);
            Assert.Null(pin.NativeWindowForTest);
            // The replayed F2 started text selection on the WPF pin, in place. The pin is in its recognizing
            // state at once; the recognizer itself is called from a pool thread a moment later.
            Assert.True(pin.IsRecognizingSelection);
            PinNativeSpeedTestWait.Until(() => Volatile.Read(ref recognitions) == 1);
            (int left, int top, int right, int bottom) = pin.PhysicalBounds;
            Assert.Equal(nativeRect.Left, left);
            Assert.Equal(nativeRect.Top, top);
            Assert.Equal(nativeRect.Right, right);
            Assert.Equal(nativeRect.Bottom, bottom);
            Assert.Same(pin, Assert.Single(manager.SnapshotOpenPinWindows()));
            Assert.Equal(0, NativePinWindow.LiveWindowCount);
            PinNativeSpeedTestWait.Complete(native.DrainAsync());

            // Never back: the pin stays WPF.
            Assert.False(pin.SwitchToWpf(PinSwitchReason.Command));
            Assert.False(pin.IsNativeHosted);
        }
        finally { manager.CloseAllForExit(); }
    });

    [Fact]
    public void AHiddenPinSwitchesWithoutShowingAndRevealsOnItsWpfWindow() => Run(animations: false, () =>
    {
        var manager = Manager(new PinSettings { LightweightImagePins = true });
        try
        {
            PinWindow pin = manager.PinImage(Picture(80, 60));
            RectD bounds = pin.NativeWindowForTest!.Bounds;
            pin.HidePin();
            Assert.True(pin.SwitchToWpf(PinSwitchReason.Command));
            Assert.False(pin.IsNativeHosted);
            Assert.False(pin.IsHostSwitchPending);
            Assert.False(pin.IsVisible);
            Assert.True(pin.State.IsHidden);
            Assert.Equal(0, NativePinWindow.LiveWindowCount);

            manager.HideOrShowAll();
            Assert.True(pin.IsVisible);
            (int left, int top, int right, int bottom) = pin.PhysicalBounds;
            Assert.Equal(bounds, new RectD(left, top, right - left, bottom - top));
        }
        finally { manager.CloseAllForExit(); }
    });

    [Fact]
    public void ASwitchDuringLandingAndLiveZoomEndsEveryTransitionAtItsFinalValue() => Run(animations: true, () =>
    {
        var manager = Manager(new PinSettings { LightweightImagePins = true, InitialOpacity = .8 });
        // Sources of earlier closed dropped pins go with their finalizers; let them go before the count.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        int workers = NativePinWindow.LiveWorkerBufferCount, sources = NativePinPixelSource.LiveAllocationCount;
        try
        {
            PinWindow pin = manager.PinImage(Picture(120, 80));
            NativePinWindow native = pin.NativeWindowForTest!;
            native.LiveClipForTest = OffscreenArea;
            Assert.True(native.IsLanding || native.IsRevealing);
            native.StepZoom(1);
            Assert.True(native.IsLiveZooming);
            double zoom = pin.State.Zoom;
            pin.ActivateAfterSwitchForTest = static _ => { };

            Assert.True(pin.SwitchToWpf(PinSwitchReason.Command));
            Assert.False(native.IsLanding);
            Assert.False(native.IsLiveZooming);
            PinNativeSpeedTestWait.Until(() => !pin.IsHostSwitchPending);

            Assert.Equal(zoom, pin.State.Zoom);
            Assert.Equal(pin.State.WidthDip, pin.Width, 6);
            Assert.Equal(pin.State.HeightDip, pin.Height, 6);
            Assert.Equal(.8, pin.Opacity, 6);
            Assert.True(native.IsClosed);
            PinNativeSpeedTestWait.Complete(native.DrainAsync());
            Assert.Equal(0, NativePinWindow.LiveWindowCount);
            Assert.Equal(workers, NativePinWindow.LiveWorkerBufferCount);
            Assert.Equal(sources, NativePinPixelSource.LiveAllocationCount);
        }
        finally { manager.CloseAllForExit(); }
    });

    [Fact]
    public void WhenTheNativeHostCannotBeCreatedThePinIsStandardAndOneLineIsLogged() => Run(animations: false, () =>
    {
        Func<Exception>? previous = NativePinHost.CreateFailureForTest;
        NativePinHost.CreateFailureForTest = static () => new InvalidOperationException("injected");
        PinManager.ResetNativeFailureLogForTest();
        var log = new RecordingLog();
        var manager = Manager(new PinSettings { LightweightImagePins = true }, log);
        try
        {
            PinWindow first = manager.PinImage(Picture(60, 40));
            PinWindow second = manager.PinImage(Picture(70, 50));
            Assert.False(first.IsNativeHosted);
            Assert.False(second.IsNativeHosted);
            Assert.True(first.IsVisible);
            Assert.True(second.IsVisible);
            Assert.Equal(0, NativePinWindow.LiveWindowCount);
            string line = Assert.Single(log.Warnings);
            Assert.Contains("InvalidOperationException", line);
            Assert.Contains("60x40", line);
        }
        finally
        {
            manager.CloseAllForExit();
            NativePinHost.CreateFailureForTest = previous;
            PinManager.ResetNativeFailureLogForTest();
        }
    });

    [Fact]
    public void WithTheSettingOffNoNativeWindowIsCreated() => Run(animations: false, () =>
    {
        var manager = Manager(new PinSettings());
        try
        {
            PinWindow pin = manager.PinImage(Picture(60, 40));
            Assert.False(pin.IsNativeHosted);
            Assert.Null(pin.NativeWindowForTest);
            Assert.True(pin.IsVisible);
            Assert.Equal(0, NativePinWindow.LiveWindowCount);
        }
        finally { manager.CloseAllForExit(); }
    });

    private static void Run(bool animations, Action test) => StaTestHost.Run(() =>
    {
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        Func<IntPtr, double?>? scale = NativePinWindow.ReadWindowScaleForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = animations;
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

    private static PinManager Manager(PinSettings settings, ILogger? log = null) =>
        new(() => settings, log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)
        {
            ReadPlacementMonitorForTest = static () => Offscreen,
            ReadRestoreMonitorsForTest = static () => [Offscreen],
            ActivatePinForTest = static _ => { },
        };

    private static BitmapSource Picture(int width, int height) => PinNativeTestImages.Picture(width, height);

    private sealed class RecordingLog : ILogger
    {
        internal readonly List<string> Warnings = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (level == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);
}
