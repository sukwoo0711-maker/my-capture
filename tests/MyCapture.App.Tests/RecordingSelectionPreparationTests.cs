using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Capture;
using MyCapture.App.Recording;
using MyCapture.App.Gallery;
using MyCapture.Core.Primitives;
using MyCapture.Core.Queue;
using MyCapture.Core.Recording;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Platform.Capture;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

public sealed class RecordingSelectionPreparationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecordingSelectorDefersAPreviousGuideWriteUntilSelectionCloses(bool native) => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-record-guide-write-");
        RegionRecordingCoordinator recorder = Create(root, native);
        using var releaseFrame = new ManualResetEventSlim();
        var pending = new Queue<Action>();
        int saves = 0;
        var scheduler = new CaptureGuideWriteScheduler(() => recorder.IsSelecting, pending.Enqueue);
        recorder.SelectorClosed += scheduler.NotifySelectorClosed;
        recorder.AcquireSelectionFrame = () => { releaseFrame.Wait(TimeSpan.FromSeconds(8)); return Frame(); };
        recorder.OpenControlsForTest = _ => { };
        ExcludeSelection(recorder, native, _ => true);
        try
        {
            scheduler.Schedule(() => saves++); // Previous screenshot closed before this recording started.
            Assert.Single(pending);
            recorder.Toggle();
            Assert.True(recorder.IsSelecting);
            pending.Dequeue()();
            Assert.Equal(0, saves);
            Assert.Empty(pending);
            releaseFrame.Set();
            PumpUntil(() => recorder.LastSelectionPreparation.IsCompleted);
            Assert.True(recorder.IsSelecting);
            Assert.Empty(pending);
            new RecordingSelector(recorder, native).CompleteSelection(new RectD(1, 1, 20, 12));
            Assert.False(recorder.IsSelecting);
            Assert.Single(pending);
            pending.Dequeue()();
            Assert.Equal(1, saves);
            output.WriteLine($"Body assertions completed before fixture cleanup: {nameof(RecordingSelectorDefersAPreviousGuideWriteUntilSelectionCloses)} native={native}");
        }
        catch (Exception error) { output.WriteLine($"Body failure before fixture cleanup: {error}"); throw; }
        finally
        {
            releaseFrame.Set();
            recorder.CancelRegionSelection();
            PumpUntil(() => recorder.LastSelectionPreparation.IsCompleted);
            OwnedTestDirectory.Delete(root);
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ACompletedVideoEditorKeepsUnsavedEditsWhileAnotherRecordingStarts(bool native) => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-record-over-editor-");
        RegionRecordingCoordinator recorder = Create(root, native);
        VideoEditorWindow? editor = null;
        RecordingSelector? selector = null;
        int acquired = 0;
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        try
        {
            editor = VideoEditorUxTrimTests.OpenReadyEditor(root, 10000, show: true);
            Assert.True(editor.TimelineForTest.SetTrimRange(1000, 9000));
            Assert.True(editor.HasUnsavedEditsForTest);
            editor.UnsavedEditsPromptForTest = () => throw new InvalidOperationException("Recording must keep the old editor.");
            typeof(RegionRecordingCoordinator).GetField("_editor", flags)!.SetValue(recorder, editor);
            ExcludeSelection(recorder, native, window => { selector = window; return true; });
            recorder.AcquireSelectionFrame = () => { Interlocked.Increment(ref acquired); return Frame(); };
            Assert.True(recorder.IsActive);
            Assert.False(recorder.HasRecordingSession);

            // A busy completed editor still blocks the recording command.
            typeof(VideoEditorWindow).GetField("_operationRunning", flags)!.SetValue(editor, true);
            recorder.Toggle();
            Assert.Equal(0, acquired);
            Assert.False(recorder.HasRecordingSession);
            typeof(VideoEditorWindow).GetField("_operationRunning", flags)!.SetValue(editor, false);

            recorder.Toggle();
            PumpUntil(() => recorder.LastSelectionPreparation.IsCompleted);
            Assert.Equal(1, acquired);
            Assert.True(recorder.HasRecordingSession);
            Assert.True(selector!.IsVisible);
            Assert.True(editor.IsVisible);
            Assert.True(editor.HasUnsavedEditsForTest);
            Assert.Equal(1000, editor.TimelineForTest.InMs, precision: 3);
            Assert.Equal(9000, editor.TimelineForTest.OutMs, precision: 3);
            editor.Close();
            Assert.True(recorder.HasRecordingSession);
            Assert.True(selector.IsVisible);
            output.WriteLine($"Body assertions completed before fixture cleanup: {nameof(ACompletedVideoEditorKeepsUnsavedEditsWhileAnotherRecordingStarts)} native={native}");
        }
        catch (Exception error) { output.WriteLine($"Body failure before fixture cleanup: {error}"); throw; }
        finally
        {
            recorder.CancelRegionSelection();
            PumpUntil(() => recorder.LastSelectionPreparation.IsCompleted);
            editor?.Close();
            OwnedTestDirectory.Delete(root);
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LibraryHideConfirmationPrecedesOverlayCreationAndFrozenFrameAcquisition(bool native) => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-library-record-selection-");
        RegionRecordingCoordinator recorder = Create(root, native);
        var confirmed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool hidden = false;
        bool hideConfirmed = false;
        int overlays = 0;
        int acquired = 0;
        var start = new LibraryCaptureStartCoordinator(async () =>
        {
            hidden = true;
            await confirmed.Task;
            hideConfirmed = true;
        }, () => hidden = false);
        ExcludeSelection(recorder, native, window =>
        {
            Assert.True(hidden && hideConfirmed);
            Assert.False(window.IsVisible);
            overlays++;
            return false;
        });
        recorder.AcquireSelectionFrame = () =>
        {
            Assert.True(hidden && hideConfirmed);
            Interlocked.Increment(ref acquired);
            return Frame();
        };
        Task? pending = null;
        try
        {
            _ = Dispatcher.CurrentDispatcher.InvokeAsync(() => pending = start.StartAsync(recorder.Toggle));
            PumpUntil(() => pending is not null);
            Assert.True(hidden);
            Assert.False(recorder.IsActive);
            Assert.Equal(0, overlays);
            Assert.Equal(0, acquired);
            confirmed.SetResult();
            PumpUntil(() => pending!.IsCompleted);
            pending!.GetAwaiter().GetResult();
            PumpUntil(() => recorder.LastSelectionPreparation.IsCompleted);
            Assert.Equal(1, overlays);
            Assert.Equal(1, acquired);
            Assert.True(recorder.IsActive);
            Assert.True(hidden);
            output.WriteLine($"Body assertions completed before fixture cleanup: {nameof(LibraryHideConfirmationPrecedesOverlayCreationAndFrozenFrameAcquisition)} native={native}");
        }
        catch (Exception error) { output.WriteLine($"Body failure before fixture cleanup: {error}"); throw; }
        finally
        {
            confirmed.TrySetResult();
            if (pending is not null) PumpUntil(() => pending.IsCompleted);
            recorder.CancelRegionSelection();
            PumpUntil(() => recorder.LastSelectionPreparation.IsCompleted);
            OwnedTestDirectory.Delete(root);
        }
    });

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void DelayedAcquisition_LeavesDispatcherResponsive_AndCancelDiscardsLateFrame(bool excluded, bool native) => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-record-selection-");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        RegionRecordingCoordinator coordinator = Create(root, native);
        RecordingSelector? overlay = null;
        int calls = 0;
        int ended = 0;
        int ownerThread = Environment.CurrentManagedThreadId;
        ExcludeSelection(coordinator, native, window => { overlay = window; Assert.False(window.IsVisible); return excluded; });
        coordinator.AcquireSelectionFrame = () =>
        {
            Assert.NotEqual(ownerThread, Environment.CurrentManagedThreadId);
            Interlocked.Increment(ref calls);
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            return Frame();
        };
        var pendingGuideWrites = new Queue<Action>();
        int guideSaves = 0;
        var guideWrites = new CaptureGuideWriteScheduler(() => coordinator.IsSelecting, pendingGuideWrites.Enqueue);
        coordinator.SelectorClosed += guideWrites.NotifySelectorClosed;
        coordinator.SessionEnded += (_, _) => ended++;
        try
        {
            coordinator.Toggle();
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            guideWrites.Schedule(() => guideSaves++);
            Assert.Empty(pendingGuideWrites);
            Assert.NotNull(overlay);
            Assert.False(overlay.IsVisible);
            bool responsive = false;
            _ = Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => responsive = true));
            PumpUntil(() => responsive);
            coordinator.Toggle();
            Assert.Equal(1, calls);
            coordinator.CancelRegionSelection();
            Assert.True(coordinator.IsActive); // reserve until native acquisition drains
            Assert.Equal(0, ended);
            Assert.True(coordinator.IsSelecting);
            Assert.Empty(pendingGuideWrites);
            release.Set();
            PumpUntil(() => coordinator.LastSelectionPreparation.IsCompleted);
            Assert.False(coordinator.IsActive);
            Assert.False(overlay.IsVisible);
            Assert.Equal(1, ended);
            Assert.False(coordinator.IsSelecting);
            Assert.Equal(0, guideSaves);
            Assert.Single(pendingGuideWrites).Invoke();
            Assert.Equal(1, guideSaves);
            output.WriteLine($"Body assertions completed before fixture cleanup: {nameof(DelayedAcquisition_LeavesDispatcherResponsive_AndCancelDiscardsLateFrame)} excluded={excluded} native={native}");
        }
        catch (Exception error) { output.WriteLine($"Body failure before fixture cleanup: {error}"); throw; }
        finally
        {
            release.Set();
            coordinator.CancelRegionSelection();
            PumpUntil(() => coordinator.LastSelectionPreparation.IsCompleted);
            OwnedTestDirectory.Delete(root);
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AcquisitionFailure_IsReportedOnOwner_AndNextSelectionCanOpen(bool native) => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-record-selection-");
        RegionRecordingCoordinator coordinator = Create(root, native);
        int attempts = 0;
        int failures = 0;
        int ownerThread = Environment.CurrentManagedThreadId;
        var pendingGuideWrites = new Queue<Action>();
        int guideSaves = 0;
        var guideWrites = new CaptureGuideWriteScheduler(() => coordinator.IsSelecting, pendingGuideWrites.Enqueue);
        coordinator.SelectorClosed += guideWrites.NotifySelectorClosed;
        RecordingSelector? overlay = null;
        ExcludeSelection(coordinator, native, window => { overlay = window; return false; });
        coordinator.AcquireSelectionFrame = () => ++attempts == 1 ? throw new InvalidOperationException("capture failed") : Frame();
        coordinator.SelectionPreparationFailed += error =>
        {
            Assert.Equal(ownerThread, Environment.CurrentManagedThreadId);
            Assert.Equal("capture failed", error.Message);
            failures++;
        };
        try
        {
            coordinator.Toggle();
            guideWrites.Schedule(() => guideSaves++);
            Assert.True(coordinator.IsSelecting);
            Assert.Empty(pendingGuideWrites);
            PumpUntil(() => coordinator.LastSelectionPreparation.IsCompleted);
            Assert.Equal(1, failures);
            Assert.False(coordinator.IsActive);
            Assert.False(coordinator.IsSelecting);
            Assert.Equal(0, guideSaves);
            Assert.Single(pendingGuideWrites).Invoke();
            pendingGuideWrites.Clear();
            Assert.Equal(1, guideSaves);
            coordinator.Toggle();
            PumpUntil(() => coordinator.LastSelectionPreparation.IsCompleted);
            Assert.NotNull(overlay);
            Assert.True(overlay.IsVisible);
            Assert.True(coordinator.IsActive);
            Assert.Empty(pendingGuideWrites);
            Assert.Equal(1, guideSaves);
            output.WriteLine($"Body assertions completed before fixture cleanup: {nameof(AcquisitionFailure_IsReportedOnOwner_AndNextSelectionCanOpen)} native={native}");
        }
        catch (Exception error) { output.WriteLine($"Body failure before fixture cleanup: {error}"); throw; }
        finally { coordinator.CancelRegionSelection(); OwnedTestDirectory.Delete(root); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisplayOriginChangedDuringAcquisition_RejectsStaleCoordinates_AndCanRetry(bool native) => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-record-selection-");
        RegionRecordingCoordinator coordinator = Create(root, native);
        int failures = 0;
        RecordingSelector? overlay = null;
        ExcludeSelection(coordinator, native, window => { overlay = window; return true; });
        coordinator.SelectionPreparationFailed += _ => failures++;
        coordinator.SelectionDesktopBounds = () => new RectD(-32, 0, 32, 20);
        coordinator.AcquireSelectionFrame = Frame;
        try
        {
            coordinator.Toggle();
            PumpUntil(() => coordinator.LastSelectionPreparation.IsCompleted);
            Assert.Equal(1, failures);
            Assert.False(coordinator.IsActive);
            Assert.False(overlay!.IsVisible);
            coordinator.SelectionDesktopBounds = () => new RectD(0, 0, 32, 20);
            coordinator.Toggle();
            PumpUntil(() => coordinator.LastSelectionPreparation.IsCompleted);
            Assert.True(overlay!.IsVisible);
            Assert.True(coordinator.IsActive);
            output.WriteLine($"Body assertions completed before fixture cleanup: {nameof(DisplayOriginChangedDuringAcquisition_RejectsStaleCoordinates_AndCanRetry)} native={native}");
        }
        catch (Exception error) { output.WriteLine($"Body failure before fixture cleanup: {error}"); throw; }
        finally { coordinator.CancelRegionSelection(); OwnedTestDirectory.Delete(root); }
    });

    private static void ExcludeSelection(RegionRecordingCoordinator coordinator, bool native, Func<RecordingSelector, bool> exclude)
    {
        coordinator.ExcludeSelectionWindow = _ => exclude(new RecordingSelector(coordinator, native));
        coordinator.ExcludeNativeSelectionWindow = _ => exclude(new RecordingSelector(coordinator, native));
    }

    private static RegionRecordingCoordinator Create(string root, bool native)
    {
        AppPaths paths = AppPaths.CreateForRoot(root);
        var queue = new CaptureQueue(paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
        var library = new VideoLibraryService(queue, paths, NullLogger<VideoLibraryService>.Instance);
        return new RegionRecordingCoordinator(new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance),
            paths, () => new RecordingSettings(), NullLoggerFactory.Instance, library)
        { UseNativeDibSelector = native, SelectionDesktopBounds = () => new RectD(0, 0, 32, 20) };
    }

    private static FrozenFrame Frame()
    {
        BitmapSource bitmap = BitmapSource.Create(32, 20, 96, 96, PixelFormats.Bgra32, null, new byte[32 * 20 * 4], 32 * 4);
        bitmap.Freeze();
        return new(bitmap, new RectD(0, 0, 32, 20), null, 0);
    }

    private static void PumpUntil(Func<bool> done)
    {
        var timeout = Stopwatch.StartNew();
        while (!done() && timeout.Elapsed < TimeSpan.FromSeconds(8))
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
        Assert.True(done(), "Selection preparation did not complete before the deadline.");
    }
}
