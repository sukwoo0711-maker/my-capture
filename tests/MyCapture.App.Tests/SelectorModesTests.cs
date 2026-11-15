using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Capture;
using MyCapture.App.Themes;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// The selector's scroll mode, armed delay and colour mode: what each input does, in the native
/// selector and (where it applies) the WPF fallback.
/// </summary>
public sealed class SelectorModesTests
{
    private static readonly RectD Bounds = new(0, 0, 640, 360);
    private static readonly MonitorInfo Monitor = new("test", Bounds, Bounds, 96, true);

    [Fact]
    public void ScrollModeHighlightsWindowAndClickRequestsScrollAtThatPoint() => StaTestHost.Run(() =>
    {
        var window = new WindowCandidate(new IntPtr(77), new RectD(100, 80, 300, 200), "Editor");
        using NativeCaptureOverlay overlay = Create(window);
        var requests = new List<CaptureSelectorRequest>();
        int completed = 0;
        overlay.ActionRequested += (_, request) => requests.Add(request);
        overlay.SelectionCompleted += (_, _) => completed++;
        Assert.True(overlay.HandleKey(Key.S, ModifierKeys.None));
        Assert.Equal(CaptureSelectorMode.Scroll, overlay.Mode);
        overlay.ReadCursor = () => new PointD(200, 150);
        overlay.RefreshHover();
        Assert.NotNull(overlay.HoverRegion);

        // A drag selects nothing and asks for nothing.
        overlay.BeginSelection(new PointD(150, 120));
        overlay.MoveSelection(new PointD(260, 220));
        overlay.EndSelection(new PointD(260, 220));
        Assert.False(overlay.IsClosed);
        Assert.Empty(requests);
        Assert.Equal(0, completed);

        // A click asks to scroll the window at the click point.
        overlay.BeginSelection(new PointD(200, 150));
        overlay.EndSelection(new PointD(200, 150));
        CaptureSelectorRequest request = Assert.Single(requests);
        Assert.Equal(CaptureSelectorAction.ScrollingCapture, request.Action);
        Assert.Equal(new PointD(200, 150), request.ScreenPoint);
        Assert.Equal(0, completed);
    });

    [Fact]
    public void ScrollKeyTogglesBackAndEscCloses() => StaTestHost.Run(() =>
    {
        using NativeCaptureOverlay overlay = Create();
        Assert.True(overlay.HandleKey(Key.S, ModifierKeys.None));
        Assert.Equal(CaptureSelectorMode.Scroll, overlay.Mode);
        Assert.True(overlay.HandleKey(Key.S, ModifierKeys.Shift));
        Assert.Equal(CaptureSelectorMode.Capture, overlay.Mode);
        overlay.HandleKey(Key.S, ModifierKeys.None);
        overlay.HandleKey(Key.Escape, ModifierKeys.None);
        Assert.True(overlay.IsClosed);
    });

    [Fact]
    public void DelayKeyCyclesAndAppliesOnlyToImageModes() => StaTestHost.Run(() =>
    {
        using NativeCaptureOverlay overlay = Create();
        Assert.Equal(0, overlay.ArmedDelaySeconds);
        int[] seen = new int[5];
        for (int index = 0; index < seen.Length; index++)
        {
            Assert.True(overlay.HandleKey(Key.D, ModifierKeys.None));
            seen[index] = overlay.ArmedDelaySeconds;
        }
        Assert.Equal([3, 5, 10, 0, 3], seen);
        Assert.Equal(3, overlay.DelaySeconds);
        overlay.HandleKey(Key.T, ModifierKeys.None);
        Assert.Equal(0, overlay.DelaySeconds);
        Assert.False(overlay.ModeBar.DelayButton.IsEnabled);
        Assert.False(overlay.HandleKey(Key.D, ModifierKeys.None));
        overlay.HandleKey(Key.T, ModifierKeys.None);
        Assert.Equal(3, overlay.DelaySeconds);
        Assert.True(overlay.ModeBar.DelayButton.IsEnabled);
        Assert.Equal(true, overlay.ModeBar.DelayButton.IsChecked);
    });

    [Fact]
    public void ArmedDelayTravelsWithTheConfirmedSelection() => StaTestHost.Run(() =>
    {
        using NativeCaptureOverlay overlay = Create();
        CaptureSelectionCompletedEventArgs? selection = null;
        overlay.SelectionCompleted += (_, args) => selection = args;
        overlay.ArmedDelaySeconds = 5;
        overlay.CompleteSelection(new RectD(10, 10, 100, 80));
        Assert.NotNull(selection);
        Assert.Equal(5, selection.DelaySeconds);
    });

    [Fact]
    public void DelayMenuIsRadioAndChoosingKeepsSelectorOpen() => StaTestHost.Run(() =>
    {
        using NativeCaptureOverlay overlay = Create();
        overlay.Show();
        WindowDetectOverlayTests.PumpUntil(() => overlay.IsChromeReady);
        CaptureModeBar bar = overlay.ModeBar;
        ContextMenu menu = bar.CreateDelayMenu();
        MenuItem[] items = menu.Items.OfType<MenuItem>().ToArray();
        Assert.Equal(4, items.Length);
        Assert.True(items[0].IsChecked);
        items[2].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.False(overlay.IsClosed);
        Assert.Equal(5, overlay.ArmedDelaySeconds);
        Assert.Equal(true, bar.DelayButton.IsChecked);
        MenuItem[] again = bar.CreateDelayMenu().Items.OfType<MenuItem>().ToArray();
        Assert.Equal([false, false, true, false], again.Select(item => item.IsChecked));
        bar.DismissMenus();
    });

    [Fact]
    public void ColorKeyEntersModeWithoutCopyingAndEnterCopiesValueOnly() => StaTestHost.Run(() =>
    {
        using NativeCaptureOverlay overlay = Create();
        var copied = new List<string>();
        overlay.CopyColorText = text => { copied.Add(text); return Task.FromResult(true); };
        ColorCopyFeedback? feedback = null;
        overlay.ColorCopied = result => feedback = result;
        Assert.True(overlay.HandleKey(Key.C, ModifierKeys.None));
        Assert.Equal(CaptureSelectorMode.Color, overlay.Mode);
        Assert.Empty(copied);
        Assert.False(overlay.IsClosed);
        Assert.True(overlay.HandleKey(Key.C, ModifierKeys.None));
        Assert.Equal(CaptureSelectorMode.Capture, overlay.Mode);
        overlay.HandleKey(Key.C, ModifierKeys.None);
        Assert.True(overlay.HandleKey(Key.Enter, ModifierKeys.None));
        Assert.Equal(["#604020"], copied);
        Assert.True(overlay.IsClosed);
        Assert.NotNull(feedback);
        Assert.Equal("#604020", feedback.Value);
        Assert.False(string.IsNullOrEmpty(feedback.NameText));
        Assert.Contains(feedback.NameText, feedback.Announcement);
    });

    [Fact]
    public void ColorModeArrowsNudgeThePointer() => StaTestHost.Run(() =>
    {
        using NativeCaptureOverlay overlay = Create();
        var placed = new List<PointD>();
        overlay.PlaceCursor = point => { placed.Add(point); return true; };
        overlay.CopyColorText = _ => Task.FromResult(true);
        overlay.HandleKey(Key.C, ModifierKeys.None);
        Assert.True(overlay.HandleKey(Key.Right, ModifierKeys.None));
        Assert.True(overlay.HandleKey(Key.Down, ModifierKeys.Control));
        Assert.Equal([new PointD(101, 100), new PointD(100, 110)], placed);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DelayedSelectionCountsDownThenRetakesTheRegion(bool native) => RunCoordinator(native, delayElapses: true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EscDuringCountdownCancelsWithoutCapturing(bool native) => RunCoordinator(native, delayElapses: false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScrollEntryOpensTheSelectorInScrollMode(bool native) => StaTestHost.Run(() =>
    {
        using CaptureOverlayCoordinator coordinator = Coordinator(_ => Frame(0x7A));
        coordinator.UseNativeDibSelector = native;
        coordinator.SelectorActionRequested = _ => { };
        coordinator.ReadWindowSources = null;
        coordinator.ApplyCaptureExclusion = _ => true;
        coordinator.Start(false, false, false, mode: CaptureSelectorMode.Scroll);
        WindowDetectOverlayTests.PumpUntil(() => coordinator.LastPreparationForTest.IsCompleted);
        Assert.Equal(CaptureSelectorMode.Scroll, ModeOf(coordinator));
        coordinator.Cancel();
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecordingExclusionFailureShowsNoCountdownAndCapturesNothing(bool native) => StaTestHost.Run(() =>
    {
        int acquired = 0;
        using CaptureOverlayCoordinator coordinator = Coordinator(_ => { acquired++; return Frame(0x7A); });
        coordinator.UseNativeDibSelector = native;
        coordinator.SelectorActionRequested = _ => { };
        coordinator.ReadWindowSources = null;
        coordinator.ReadPageUrls = _ => Task.FromResult<IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot>>(
            new Dictionary<IntPtr, BrowserDocumentSnapshot>());
        // A recording requires exclusion: the selector can be excluded, the countdown cannot.
        coordinator.RequiresCaptureExclusion = () => true;
        var countdowns = new List<CountdownWindow>();
        coordinator.ApplyCaptureExclusion = window =>
        {
            if (window is CountdownWindow countdown) { countdowns.Add(countdown); return false; }
            return true;
        };
        coordinator.ApplyNativeCaptureExclusion = _ => true;
        coordinator.OpenEditorAfterCapture = () => false;
        int failed = 0, cancelled = 0;
        coordinator.DelayExclusionFailed += () => failed++;
        coordinator.DelayCancelled += () => cancelled++;
        CaptureSelectionCompletedEventArgs? persisted = null;
        coordinator.SelectionPersistRequested = selection => { persisted = selection; return Task.CompletedTask; };
        coordinator.Start(false, false, false, delaySeconds: 3);
        WindowDetectOverlayTests.PumpUntil(() => coordinator.LastPreparationForTest.IsCompleted);
        new CoordinatorSelector(coordinator, native).CompleteSelection(new RectD(4, 2, 10, 8));
        WindowDetectOverlayTests.PumpUntil(() => failed == 1 && coordinator.LastTransitionForTest.IsCompleted);
        CountdownWindow refused = Assert.Single(countdowns);
        Assert.False(refused.IsVisible);
        Assert.False(refused.IsLoaded);
        Assert.Null(persisted);
        Assert.Equal(1, acquired);
        Assert.Equal(0, cancelled);
        Assert.False(coordinator.IsCountingDown);
        Assert.False(coordinator.IsActive);
    });

    [Fact]
    public void CountdownIsPlacedClearOfTheRegion()
    {
        var work = new RectD(0, 0, 1920, 1040);
        var size = new Size(200, 100);
        // A central region pushes the countdown beside it; a full-width region leaves only above or below.
        RectD beside = MonitorWindowPlacement.ClearOf(work, 1, size, new RectD(700, 300, 500, 400));
        Assert.Equal(new RectD(850, 184, 200, 100), beside);
        RectD below = MonitorWindowPlacement.ClearOf(work, 1, size, new RectD(0, 0, 1920, 700));
        Assert.Equal(new RectD(860, 716, 200, 100), below);
        // At 150 % the size and the gap scale with the monitor.
        Assert.Equal(new RectD(800, 126, 300, 150), MonitorWindowPlacement.ClearOf(work, 1.5, size, new RectD(700, 300, 500, 400)));
    }

    private static void RunCoordinator(bool native, bool delayElapses) => StaTestHost.Run(() =>
    {
        int acquired = 0;
        using CaptureOverlayCoordinator coordinator = Coordinator(_ => Frame(acquired++ == 0 ? (byte)0x7A : (byte)0x20));
        coordinator.UseNativeDibSelector = native;
        coordinator.SelectorActionRequested = _ => { };
        coordinator.ReadWindowSources = null;
        coordinator.ReadPageUrls = _ => Task.FromResult<IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot>>(
            new Dictionary<IntPtr, BrowserDocumentSnapshot>());
        coordinator.ApplyCaptureExclusion = _ => true;
        coordinator.OpenEditorAfterCapture = () => false;
        var delays = new List<(int Seconds, RectD Region)>();
        coordinator.RunDelay = (seconds, region, _) =>
        {
            delays.Add((seconds, region));
            return Task.FromResult(delayElapses ? CaptureOverlayCoordinator.DelayOutcome.Elapsed : CaptureOverlayCoordinator.DelayOutcome.Cancelled);
        };
        int cancelled = 0;
        coordinator.DelayCancelled += () => cancelled++;
        CaptureSelectionCompletedEventArgs? persisted = null;
        coordinator.SelectionPersistRequested = selection => { persisted = selection; return Task.CompletedTask; };
        coordinator.Start(false, false, false, delaySeconds: 3);
        WindowDetectOverlayTests.PumpUntil(() => coordinator.LastPreparationForTest.IsCompleted);
        new CoordinatorSelector(coordinator, native).CompleteSelection(new RectD(4, 2, 10, 8));
        WindowDetectOverlayTests.PumpUntil(() => coordinator.LastTransitionForTest.IsCompleted && delays.Count == 1);
        WindowDetectOverlayTests.PumpUntil(() => coordinator.LastTransitionForTest.IsCompleted);
        Assert.Equal((3, new RectD(4, 2, 10, 8)), Assert.Single(delays));
        if (delayElapses)
        {
            Assert.NotNull(persisted);
            Assert.Equal(2, acquired);
            Assert.Equal(0x20, FirstBlue(persisted.SelectedBitmap));
            Assert.Equal(0, cancelled);
        }
        else
        {
            Assert.Null(persisted);
            Assert.Equal(1, acquired);
            Assert.Equal(1, cancelled);
            Assert.False(coordinator.IsActive);
        }
    });

    private static CaptureSelectorMode ModeOf(CaptureOverlayCoordinator coordinator)
    {
        if (coordinator.NativeOverlayForProbe is { } native) return native.Mode;
        var window = (CaptureOverlayWindow)typeof(CaptureOverlayCoordinator)
            .GetField("_activeOverlay", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(coordinator)!;
        return window.Mode;
    }

    private static byte FirstBlue(BitmapSource bitmap)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        byte[] pixel = new byte[4];
        converted.CopyPixels(new Int32Rect(0, 0, 1, 1), pixel, 4, 0);
        return pixel[0];
    }

    private static CaptureOverlayCoordinator Coordinator(Func<bool, FrozenFrame> acquire) => new(
        new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance),
        new WindowCandidateService(NullLogger<WindowCandidateService>.Instance),
        NullLogger<CaptureOverlayCoordinator>.Instance,
        acquire,
        static () => new RectD(0, 0, 32, 20),
        static bounds => [new WindowCandidate(new IntPtr(1), bounds, "Synthetic", ProcessId: 42)]);

    private static FrozenFrame Frame(byte value)
    {
        var bitmap = new WriteableBitmap(32, 20, 96, 96, PixelFormats.Bgra32, null);
        byte[] pixels = Enumerable.Repeat(value, 32 * 20 * 4).ToArray();
        for (int offset = 3; offset < pixels.Length; offset += 4) pixels[offset] = 0xFF;
        bitmap.WritePixels(new Int32Rect(0, 0, 32, 20), pixels, 32 * 4, 0);
        bitmap.Freeze();
        return new FrozenFrame(bitmap, new RectD(0, 0, 32, 20), null, 0);
    }

    private static NativeCaptureOverlay Create(params WindowCandidate[] windows)
    {
        byte[] pixels = new byte[640 * 360 * 4];
        for (int offset = 0; offset < pixels.Length; offset += 4)
        { pixels[offset] = 32; pixels[offset + 1] = 64; pixels[offset + 2] = 96; pixels[offset + 3] = 255; }
        BitmapSource bitmap = BitmapSource.Create(640, 360, 96, 96, PixelFormats.Bgr32, null, pixels, 640 * 4);
        bitmap.Freeze();
        var overlay = new NativeCaptureOverlay(Bounds, false, false)
        {
            PlaceCursor = _ => false,
            ReadCursor = () => new PointD(100, 100),
            ReadMonitors = () => [Monitor],
            OffersOtherActions = true,
            TextCaptureAvailable = true,
            DetectWindows = windows.Length > 0,
            SourceWindows = windows,
            CopyColorText = _ => Task.FromResult(true),
        };
        overlay.AttachFrame(new FrozenFrame(bitmap, Bounds, null, 0));
        return overlay;
    }
}
