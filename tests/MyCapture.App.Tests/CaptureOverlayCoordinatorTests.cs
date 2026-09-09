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
using Xunit;

namespace MyCapture.App.Tests;

public sealed class CaptureOverlayCoordinatorTests
{
    [Fact]
    public void PreparingFrame_LeavesDispatcherResponsiveAndReservesOneSessionUntilCancelledCaptureDrains() => RunSta(() =>
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int ownerThread = Environment.CurrentManagedThreadId;
        int calls = 0;
        int closed = 0;
        int windows = 0;
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
        coordinator.ApplyCaptureExclusion = _ => { windows++; return true; };
        try
        {
            // Native WM_HOTKEY does not guarantee a WPF synchronization context.
            SynchronizationContext.SetSynchronizationContext(null);
            coordinator.Start(true, false, false);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(coordinator.IsActive);
            Assert.Equal(1, windows);
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
        Assert.False(persistedSelection!.CopyToClipboardImmediately);

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
    public void ManualRegionSelection_DefaultsToImmediateClipboardCopy()
    {
        BitmapSource bitmap = Solid(30, 18);
        var frame = new FrozenFrame(bitmap, new RectD(0, 0, 30, 18), null, 0);
        var selection = new CaptureSelectionCompletedEventArgs(
            frame,
            new RectD(3, 2, 15, 9),
            bitmap);

        Assert.True(selection.RecordForRepeat);
        Assert.True(selection.CopyToClipboardImmediately);
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
        var thread = new Thread(() =>
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
