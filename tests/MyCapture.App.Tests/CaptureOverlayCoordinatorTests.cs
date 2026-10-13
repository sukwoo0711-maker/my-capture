using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Capture;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class CaptureOverlayCoordinatorTests
{
    [Theory]
    [InlineData(0L, long.MinValue, false)]
    [InlineData(12345678L, long.MinValue, false)]
    [InlineData(long.MaxValue, long.MinValue, false)]
    [InlineData(10000L, 10000L, true)]
    [InlineData(10000L, 9251L, true)]
    [InlineData(10000L, 9250L, false)]
    [InlineData(10000L, 9249L, false)]
    [InlineData(10000L, 10001L, false)]
    public void PresentationSettle_OnlyWaitsForAnActualRecentDismissal(
        long currentTick, long lastDismissedTick, bool expected)
    {
        Assert.Equal(expected, CaptureOverlayCoordinator.NeedsPresentationSettle(currentTick, lastDismissedTick));
    }

    [Fact]
    public void PreparingFrame_LeavesDispatcherResponsiveAndReservesOneSessionUntilCancelledCaptureDrains() => RunSta(() =>
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int ownerThread = Environment.CurrentManagedThreadId;
        int calls = 0;
        int closed = 0;
        int windows = 0;
        System.Windows.Window? pendingOverlay = null;
        using var coordinator = Coordinator(includeCursor =>
        {
            Assert.NotEqual(ownerThread, Environment.CurrentManagedThreadId);
            Assert.True(includeCursor);
            Interlocked.Increment(ref calls);
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            return Frame();
        });
        coordinator.OverlayClosed += (_, _) => { Assert.Equal(ownerThread, Environment.CurrentManagedThreadId); closed++; };
        coordinator.RequiresCaptureExclusion = () => true;
        coordinator.ApplyCaptureExclusion = window => { windows++; pendingOverlay = window; return true; };
        try
        {
            // Native WM_HOTKEY does not guarantee a WPF synchronization context.
            SynchronizationContext.SetSynchronizationContext(null);
            coordinator.Start(true, false, false);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(coordinator.IsActive);
            Assert.Equal(1, windows);
            Assert.NotNull(pendingOverlay);
            Assert.False(pendingOverlay.IsVisible); // No black placeholder while acquisition is pending.
            bool dispatched = false;
            _ = Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => dispatched = true));
            PumpUntil(() => dispatched);
            Assert.False(coordinator.LastPreparationForTest.IsCompleted);
            coordinator.Start(false, true, true);
            Assert.False(coordinator.StartWithSelection(Frame(), new RectD(0, 0, 8, 8)));
            coordinator.Cancel();
            Assert.True(coordinator.IsActive); // Native work still owns the only acquisition slot.
            coordinator.Start(false, true, true);
            Assert.Equal(1, calls);
            Assert.Equal(0, closed);
        }
        finally
        {
            release.Set();
            PumpUntil(() => coordinator.LastPreparationForTest.IsCompleted);
        }
        Assert.True(coordinator.LastPreparationForTest.IsCompletedSuccessfully);
        Assert.False(coordinator.IsActive);
        Assert.Equal(1, windows);
        Assert.Equal(1, closed);
    });

    [Fact]
    public void AcquisitionFailure_IsReportedOnOwnerDispatcherAndNextHotkeyCanShowFreshFrame() => RunSta(() =>
    {
        int ownerThread = Environment.CurrentManagedThreadId;
        int calls = 0;
        int failures = 0;
        int closed = 0;
        System.Windows.Window? shown = null;
        using var coordinator = Coordinator(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new IOException("synthetic acquisition failure");
            return Frame();
        });
        coordinator.TransitionFailed += error =>
        {
            Assert.Equal(ownerThread, Environment.CurrentManagedThreadId);
            Assert.IsType<IOException>(error);
            failures++;
        };
        coordinator.OverlayClosed += (_, _) => closed++;
        coordinator.RequiresCaptureExclusion = () => true;
        coordinator.ApplyCaptureExclusion = window =>
        {
            Assert.Equal(ownerThread, Environment.CurrentManagedThreadId);
            Assert.False(window.IsVisible);
            shown = window;
            return true;
        };
        SynchronizationContext.SetSynchronizationContext(null);
        coordinator.Start(false, false, false);
        PumpUntil(() => coordinator.LastPreparationForTest.IsCompleted);
        Assert.True(coordinator.LastPreparationForTest.IsCompletedSuccessfully);
        Assert.Equal(1, failures);
        Assert.Equal(1, closed);
        Assert.False(coordinator.IsActive);
        coordinator.Start(false, false, false);
        PumpUntil(() => coordinator.LastPreparationForTest.IsCompleted);
        Assert.True(coordinator.LastPreparationForTest.IsCompletedSuccessfully);
        Assert.Equal(2, calls);
        Assert.True(coordinator.IsActive);
        Assert.NotNull(shown);
        Assert.True(shown.IsVisible);
        coordinator.Cancel();
        Assert.False(coordinator.IsActive);
        Assert.Equal(2, closed);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisposeOrDispatcherShutdown_DiscardsLateFrameWithoutWindowOrFailure(bool shutdown) => RunSta(() =>
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int windows = 0;
        int failures = 0;
        using var coordinator = Coordinator(_ =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            return Frame();
        });
        coordinator.RequiresCaptureExclusion = () => true;
        coordinator.ApplyCaptureExclusion = _ => { windows++; return true; };
        coordinator.TransitionFailed += _ => failures++;
        coordinator.Start(false, false, false);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        if (shutdown) Dispatcher.CurrentDispatcher.InvokeShutdown();
        else coordinator.Dispose();
        Assert.False(coordinator.IsActive);
        Assert.Throws<ObjectDisposedException>(() => coordinator.Start(false, false, false));
        release.Set();
        if (shutdown) Assert.True(coordinator.LastPreparationForTest.Wait(TimeSpan.FromSeconds(5)));
        else PumpUntil(() => coordinator.LastPreparationForTest.IsCompleted);
        Assert.True(coordinator.LastPreparationForTest.IsCompletedSuccessfully);
        Assert.Equal(1, windows);
        Assert.Equal(0, failures);
    });

    [Fact]
    public void PreparationRechecksRecordingExclusionBeforeShowingOverlay() => RunSta(() =>
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        bool requiresExclusion = false;
        System.Windows.Window? rejected = null;
        int failures = 0;
        int closed = 0;
        using var coordinator = Coordinator(_ =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            return Frame();
        });
        coordinator.RequiresCaptureExclusion = () => requiresExclusion;
        coordinator.ApplyCaptureExclusion = window => { rejected = window; Assert.False(window.IsVisible); return false; };
        coordinator.TransitionFailed += _ => failures++;
        coordinator.OverlayClosed += (_, _) => closed++;
        coordinator.Start(false, false, false);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        requiresExclusion = true;
        release.Set();
        PumpUntil(() => coordinator.LastPreparationForTest.IsCompleted);
        Assert.True(coordinator.LastPreparationForTest.IsCompletedSuccessfully);
        Assert.NotNull(rejected);
        Assert.False(rejected.IsVisible);
        Assert.False(coordinator.IsActive);
        Assert.Equal(1, failures);
        Assert.Equal(1, closed);
    });

    [Fact]
    public void ReadsStartWithTheGrabAndNeitherTheSelectorNorTheSaveWaitsForThem() => RunSta(() =>
    {
        const string url = "https://example.com/confirmed";
        var first = new TaskCompletionSource<IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var confirming = new TaskCompletionSource<IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
        int reads = 0;
        IReadOnlyList<WindowCandidate> asked = [];
        CaptureOverlayWindow? overlay = null;
        CaptureSelectionCompletedEventArgs? persisted = null;
        using var coordinator = Coordinator(_ => { events.Enqueue("pixels"); return Frame(); });
        coordinator.ReadWindowSources = null;
        coordinator.ApplyCaptureExclusion = window => { events.Enqueue("selector built"); overlay = (CaptureOverlayWindow)window; return true; };
        coordinator.OpenEditorAfterCapture = () => false;
        coordinator.SelectionPersistRequested = selection => { persisted = selection; return Task.CompletedTask; };
        coordinator.ReadPageUrls = candidates =>
        {
            events.Enqueue("read");
            if (Interlocked.Increment(ref reads) > 1) return confirming.Task;
            asked = candidates;
            return first.Task;
        };

        coordinator.Start(false, false, false);
        // The first read is asked at the hotkey, before the grab and before the selector window is built.
        Assert.Equal("read", events.First());
        Assert.True(events.ToList().IndexOf("read") < events.ToList().IndexOf("selector built"));
        PumpUntil(() => overlay?.IsVisible == true);
        Assert.Equal(1, reads); // Shown while the first read is still walking.
        Assert.True(coordinator.LastPreparationForTest.IsCompletedSuccessfully);
        Assert.True(overlay!.SourceDetailsPendingForTest);
        Assert.False(coordinator.LastSourceDetailsForTest.IsCompleted);

        overlay.CompleteSelection(new RectD(4, 4, 16, 10));
        PumpUntil(() => coordinator.LastTransitionForTest.IsCompleted && persisted is not null);
        Assert.Equal(new RectD(4, 4, 16, 10), persisted!.BitmapRegion);
        Assert.Empty(persisted.SourcePageUrl);
        Task<CaptureSelectionSource> late = Assert.IsAssignableFrom<Task<CaptureSelectionSource>>(persisted.PendingSource);
        Assert.False(overlay.IsVisible);
        Assert.False(coordinator.IsActive); // The session ends with the selector, not with the reads.

        // Every exposed window reports one stable page: the confirming read runs after the first one.
        first.SetResult(asked.ToDictionary(candidate => candidate.Handle,
            candidate => new BrowserDocumentSnapshot(candidate.ProcessId, candidate.Title, "document", url)));
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref reads) == 2, TimeSpan.FromSeconds(5)));
        Assert.False(late.IsCompleted);
        confirming.SetResult(first.Task.Result);
        Assert.True(late.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(coordinator.LastSourceDetailsForTest.IsCompletedSuccessfully);
        Assert.Contains(coordinator.LastSourceDetailsForTest.Result, window => window.SourcePageUrl == url);
        // The late source is the selection's window resolved from that confirmed list.
        Assert.Equal(CaptureOverlayWindow.ResolveSelectionSource(coordinator.LastSourceDetailsForTest.Result,
            new RectD(4, 4, 16, 10), null, wholeMonitor: false), late.Result);
    });

    [Fact]
    public void HotkeysOverAShownSelectorActAsOpenWhileThePageReadIsConfirming() => RunSta(() =>
    {
        var confirming = new TaskCompletionSource<IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int reads = 0, acquisitions = 0;
        CaptureOverlayWindow? overlay = null;
        using var coordinator = Coordinator(_ => { Interlocked.Increment(ref acquisitions); return Frame(); });
        coordinator.ReadWindowSources = null;
        coordinator.TextCaptureRequested = _ => { };
        coordinator.ApplyCaptureExclusion = window => { overlay = (CaptureOverlayWindow)window; return true; };
        coordinator.ReadPageUrls = candidates => Interlocked.Increment(ref reads) > 1
            ? confirming.Task
            : Task.FromResult<IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot>>(candidates.ToDictionary(
                candidate => candidate.Handle,
                candidate => new BrowserDocumentSnapshot(candidate.ProcessId, candidate.Title, "document", "https://example.com/")));

        coordinator.Start(false, false, false);
        PumpUntil(() => overlay?.IsVisible == true);
        Assert.False(overlay!.TextCaptureMode);
        // The text capture command over the shown selector switches it, exactly as once it is ready.
        coordinator.Start(false, false, false, textCapture: true);
        Assert.True(overlay.TextCaptureMode);
        Assert.False(coordinator.StartWithSelection(Frame(), new RectD(0, 0, 8, 8)));
        Assert.Equal(1, acquisitions);
        Assert.Equal(2, reads);

        confirming.SetResult(new Dictionary<IntPtr, BrowserDocumentSnapshot>());
        PumpUntil(() => coordinator.LastPreparationForTest.IsCompleted);
        Assert.True(coordinator.LastPreparationForTest.IsCompletedSuccessfully);
        Assert.True(overlay.IsVisible);
        coordinator.Cancel();
        Assert.False(coordinator.IsActive);
    });

    [Fact]
    public void SelectorClosedWhileThePageReadIsConfirmingEndsTheSessionAndTheNextCaptureStarts() => RunSta(() =>
    {
        var confirming = new TaskCompletionSource<IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int reads = 0;
        int closed = 0;
        int acquisitions = 0;
        CaptureOverlayWindow? overlay = null;
        using var coordinator = Coordinator(_ => { Interlocked.Increment(ref acquisitions); return Frame(); });
        coordinator.ReadWindowSources = null;
        coordinator.ApplyCaptureExclusion = window => { overlay = (CaptureOverlayWindow)window; return true; };
        coordinator.OverlayClosed += (_, _) => closed++;
        coordinator.ReadPageUrls = candidates => Interlocked.Increment(ref reads) == 2
            ? confirming.Task
            : Task.FromResult<IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot>>(candidates.ToDictionary(
                candidate => candidate.Handle,
                candidate => new BrowserDocumentSnapshot(candidate.ProcessId, candidate.Title, "document", "https://example.com/")));

        coordinator.Start(false, false, false);
        PumpUntil(() => overlay?.IsVisible == true);
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref reads) == 2, TimeSpan.FromSeconds(5)));
        overlay!.Close();
        Assert.Equal(1, closed);
        Assert.False(coordinator.IsActive);

        // The reader itself serializes native probes (waiting within its deadline); the capture does not.
        CaptureOverlayWindow first = overlay;
        coordinator.Start(false, false, false);
        PumpUntil(() => overlay is { IsVisible: true } next && !ReferenceEquals(next, first));
        Assert.Equal(2, acquisitions);
        Assert.True(Volatile.Read(ref reads) >= 3);
        confirming.SetResult(new Dictionary<IntPtr, BrowserDocumentSnapshot>());
        coordinator.Cancel();
        Assert.False(coordinator.IsActive);
        Assert.Equal(2, closed);
    });

    [Fact]
    public void EditorOpensWholeOnThePortraitSecondaryTheSelectionWasTakenOn() => RunSta(() =>
    {
        // The reported layout, moved far off the real desktop so the window never shows on screen:
        // an ultrawide primary with a 768 x 1366 portrait display on its right.
        const double x = -30000, y = -20000;
        var primary = new MonitorInfo(@"\\.\DISPLAY1", new RectD(x, y, 3440, 1440), new RectD(x, y, 3440, 1392), 96, true);
        var portrait = new MonitorInfo(@"\\.\DISPLAY2", new RectD(x + 3440, y, 768, 1366), new RectD(x + 3440, y, 768, 1318), 96, false);
        using var coordinator = Coordinator(_ => Frame());
        coordinator.ReadMonitors = () => [primary, portrait];
        var frame = new FrozenFrame(Solid(32, 20), new RectD(x + 3500, y + 100, 32, 20), null, 0);

        Assert.True(coordinator.StartWithSelection(frame, new RectD(0, 0, 32, 20)));
        PumpUntil(() => coordinator.LastTransitionForTest.IsCompleted && coordinator.CurrentEditor?.IsLoaded == true);
        System.Windows.Window editor = System.Windows.Window.GetWindow(coordinator.CurrentEditor!)!;
        Assert.True(GetWindowRect(new System.Windows.Interop.WindowInteropHelper(editor).Handle, out NativeRect rect));
        try
        {
            // Before: 980 wide from the primary work area, spilling onto the primary and off the right edge.
            Assert.Equal((x + 3472, y + 349, x + 4176, y + 969), ((double)rect.Left, (double)rect.Top, (double)rect.Right, (double)rect.Bottom));
        }
        finally
        {
            coordinator.Cancel();
        }
    });

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    private static CaptureOverlayCoordinator Coordinator(Func<bool, FrozenFrame> acquire) => new(
        new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance),
        new WindowCandidateService(NullLogger<WindowCandidateService>.Instance),
        NullLogger<CaptureOverlayCoordinator>.Instance,
        acquire,
        static () => new RectD(0, 0, 32, 20));

    private static FrozenFrame Frame() => new(Solid(32, 20), new RectD(0, 0, 32, 20), null, 0);

    [Fact]
    public void PendingPersistence_RejectsSecondCaptureAndCanBeCancelled() => RunSta(() =>
    {
        var coordinator = new CaptureOverlayCoordinator(
            new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance),
            new WindowCandidateService(NullLogger<WindowCandidateService>.Instance),
            NullLogger<CaptureOverlayCoordinator>.Instance);
        var releasePersistence = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int persistenceCalls = 0;
        int closedEvents = 0;
        CaptureSelectionCompletedEventArgs? persistedSelection = null;
        coordinator.SelectionPersistRequested = selection =>
        {
            persistenceCalls++;
            persistedSelection = selection;
            return releasePersistence.Task;
        };
        coordinator.OverlayClosed += (_, _) => closedEvents++;

        BitmapSource bitmap = Solid(32, 20);
        var frame = new FrozenFrame(bitmap, new RectD(0, 0, 32, 20), null, 0);

        Assert.True(coordinator.StartWithSelection(frame, new RectD(2, 3, 18, 11)));
        Assert.True(coordinator.IsActive);
        Assert.Equal(1, persistenceCalls);
        Assert.NotNull(persistedSelection);
        Assert.False(persistedSelection!.RecordForRepeat);

        // No overlay/editor window exists during the awaited durable write. This second call is
        // the exact race that previously started another session and overwrote App._currentRecord.
        Assert.False(coordinator.StartWithSelection(frame, new RectD(1, 1, 8, 8)));
        Assert.Equal(1, persistenceCalls);

        coordinator.Cancel();
        releasePersistence.SetResult(null);
        PumpUntil(() => coordinator.LastTransitionForTest.IsCompleted);

        Assert.True(coordinator.LastTransitionForTest.IsCompletedSuccessfully);
        Assert.False(coordinator.IsActive);
        Assert.Equal(1, closedEvents);
    });

    [Fact]
    public void ManualRegionSelection_RecordsManualRepeatMetadata()
    {
        BitmapSource bitmap = Solid(30, 18);
        var frame = new FrozenFrame(bitmap, new RectD(0, 0, 30, 18), null, 0);
        var selection = new CaptureSelectionCompletedEventArgs(
            frame,
            new RectD(3, 2, 15, 9),
            bitmap);

        Assert.True(selection.RecordForRepeat);
        Assert.Equal(new RectD(3, 2, 15, 9), selection.BitmapRegion);
    }

    private static BitmapSource Solid(int width, int height)
    {
        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        byte[] pixels = Enumerable.Repeat((byte)0x7A, width * height * 4).ToArray();
        for (int offset = 3; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = 0xFF;
        }

        bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, width, height), pixels, width * 4, 0);
        bitmap.Freeze();
        return bitmap;
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition())
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(5))
            {
                throw new TimeoutException("The capture transition did not complete.");
            }

            var frame = new DispatcherFrame();
            _ = Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = WpfTestThread.Create(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException($"STA body threw: {failure}");
        }
    }
}
