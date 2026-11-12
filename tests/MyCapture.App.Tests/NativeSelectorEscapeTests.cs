using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Capture;
using MyCapture.App.Recording;
using MyCapture.Core.Primitives;
using MyCapture.Core.Queue;
using MyCapture.Core.Recording;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using Xunit;
using static MyCapture.App.Tests.WindowDetectOverlayTests;

namespace MyCapture.App.Tests;

/// <summary>
/// Windows refuses SetForegroundWindow once the user's key-up or pointer input has gone to another
/// process. The selector is then shown topmost but every key, Esc included, still reaches that other
/// application until the selector is clicked. The hotkey registration is injected: these tests never
/// claim the real Esc key.
/// </summary>
public sealed class NativeSelectorEscapeTests
{
    private static readonly RectD Bounds = new(0, 0, 640, 360);
    private static readonly MonitorInfo Monitor = new("escape-test", Bounds, Bounds, 96, true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EscCancelsASelectorShownWithoutTheForeground(bool recording) => StaTestHost.Run(() =>
    {
        var hotkeys = new HotkeyLog();
        using NativeCaptureOverlay overlay = Create(recording, foreground: false, hotkeys);
        int completed = 0, closed = 0;
        overlay.SelectionCompleted += (_, _) => completed++;
        overlay.GeometrySelectionCompleted = _ => completed++;
        overlay.Closed += (_, _) => closed++;
        try
        {
            overlay.Show();
            PumpUntil(() => overlay.IsReady);
            IntPtr hwnd = overlay.Handle;
            Assert.Equal([hwnd], hotkeys.Registered);
            _ = SendMessage(hwnd, 0x0312, new IntPtr(NativeCaptureOverlay.EscapeHotkeyId), new IntPtr(0x1B << 16));
            Assert.True(overlay.IsClosed);
            Assert.Equal(1, closed);
            Assert.Equal(0, completed);
            Assert.Equal([hwnd], hotkeys.Unregistered);
        }
        finally { overlay.Close(); NativeSelectorChromeCache.Trim(); }
    });

    [Fact]
    public void AForegroundSelectorNeverClaimsEscAndActivationReleasesTheClaim() => StaTestHost.Run(() =>
    {
        var hotkeys = new HotkeyLog();
        using (NativeCaptureOverlay focused = Create(recording: true, foreground: true, hotkeys))
        {
            try
            {
                focused.Show();
                PumpUntil(() => focused.IsReady);
                Assert.Empty(hotkeys.Registered);
            }
            finally { focused.Close(); NativeSelectorChromeCache.Trim(); }
        }
        Assert.Empty(hotkeys.Unregistered);

        using NativeCaptureOverlay overlay = Create(recording: true, foreground: false, hotkeys);
        try
        {
            overlay.Show();
            PumpUntil(() => overlay.IsReady);
            IntPtr hwnd = overlay.Handle;
            Assert.Equal([hwnd], hotkeys.Registered);
            // A click activates the selector; from then on Esc arrives as an ordinary key message.
            _ = SendMessage(hwnd, 0x0006, new IntPtr(2), IntPtr.Zero);
            Assert.Equal([hwnd], hotkeys.Unregistered);
            Assert.False(overlay.IsClosed);
            _ = SendMessage(hwnd, 0x0100, new IntPtr(0x1B), IntPtr.Zero);
            Assert.True(overlay.IsClosed);
            Assert.Equal([hwnd], hotkeys.Unregistered);
        }
        finally { overlay.Close(); NativeSelectorChromeCache.Trim(); }
    });

    [Fact]
    public void RecordingSelectionWithoutTheForegroundEndsTheSessionOnEsc() => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-record-escape-");
        var bounds = new RectD(100, 100, 64, 48);
        var monitor = new MonitorInfo("recording-escape", bounds, bounds, 96, true);
        BitmapSource bitmap = BitmapSource.Create(64, 48, 96, 96, PixelFormats.Bgra32, null, new byte[64 * 48 * 4], 64 * 4);
        bitmap.Freeze();
        var frame = new FrozenFrame(bitmap, bounds, monitor, 0);
        AppPaths paths = AppPaths.CreateForRoot(root);
        var queue = new CaptureQueue(paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
        var library = new VideoLibraryService(queue, paths, NullLogger<VideoLibraryService>.Instance);
        var hotkeys = new HotkeyLog();
        var coordinator = new RegionRecordingCoordinator(new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance),
            paths, () => new RecordingSettings(), NullLoggerFactory.Instance, library)
        {
            UseNativeDibSelector = true,
            AcquireSelectionFrame = () => frame,
            SelectionDesktopBounds = () => bounds,
        };
        coordinator.ExcludeNativeSelectionWindow = _ =>
        {
            NativeCaptureOverlay selector = Assert.IsType<NativeCaptureOverlay>(coordinator.NativeOverlayForProbe);
            selector.ReadCursor = () => new PointD(110, 110);
            selector.ReadMonitors = () => [monitor];
            selector.PlaceCursor = _ => false;
            hotkeys.Attach(selector, foreground: false);
            return true;
        };
        int ended = 0, opened = 0;
        coordinator.SessionEnded += (_, _) => ended++;
        coordinator.OpenControlsForTest = _ => opened++;
        try
        {
            coordinator.Toggle();
            PinHistoryTestSupport.Await(coordinator.LastSelectionPreparation);
            NativeCaptureOverlay selector = Assert.IsType<NativeCaptureOverlay>(coordinator.NativeOverlayForProbe);
            Assert.True(selector.IsVisible);
            Assert.True(coordinator.IsSelecting);
            _ = SendMessage(selector.Handle, 0x0312, new IntPtr(NativeCaptureOverlay.EscapeHotkeyId), new IntPtr(0x1B << 16));
            Assert.False(coordinator.IsSelecting);
            Assert.False(coordinator.HasRecordingSession);
            Assert.Equal(1, ended);
            Assert.Equal(0, opened);
            Assert.Single(hotkeys.Registered);
            Assert.Equal(hotkeys.Registered, hotkeys.Unregistered);
        }
        finally
        {
            coordinator.CancelRegionSelection();
            PinHistoryTestSupport.Await(coordinator.LastSelectionPreparation);
            NativeSelectorChromeCache.Trim();
            OwnedTestDirectory.Delete(root);
        }
    });

    private sealed class HotkeyLog
    {
        internal List<IntPtr> Registered { get; } = [];
        internal List<IntPtr> Unregistered { get; } = [];

        internal void Attach(NativeCaptureOverlay overlay, bool foreground)
        {
            overlay.ReadForegroundWindow = () => foreground ? overlay.Handle : IntPtr.Zero;
            overlay.RegisterEscapeHotkey = hwnd => { Registered.Add(hwnd); return true; };
            overlay.UnregisterEscapeHotkey = Unregistered.Add;
        }
    }

    private static NativeCaptureOverlay Create(bool recording, bool foreground, HotkeyLog hotkeys)
    {
        NativeSelectorChromeCache.Trim();
        BitmapSource bitmap = BitmapSource.Create(640, 360, 96, 96, PixelFormats.Bgr32, null, new byte[640 * 360 * 4], 640 * 4);
        bitmap.Freeze();
        var overlay = new NativeCaptureOverlay(Bounds, false, true)
        {
            PlaceCursor = _ => false,
            ReadCursor = () => new PointD(320, 180),
            ReadMonitors = () => [Monitor],
            RecordingSelection = recording,
        };
        hotkeys.Attach(overlay, foreground);
        overlay.AttachFrame(new FrozenFrame(bitmap, Bounds, null, 0));
        return overlay;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
}
