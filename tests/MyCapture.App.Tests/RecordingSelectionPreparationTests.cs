using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Recording;
using MyCapture.App.Gallery;
using MyCapture.Core.Primitives;
using MyCapture.Core.Queue;
using MyCapture.Core.Recording;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Platform.Capture;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class RecordingSelectionPreparationTests
{
    [Fact]
    public void ACompletedVideoEditorKeepsUnsavedEditsWhileAnotherRecordingStarts() => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-record-over-editor-");
        RegionRecordingCoordinator recorder = Create(root);
        VideoEditorWindow? editor = null;
        Window? selector = null;
        int acquired = 0;
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        try
        {
            editor = VideoEditorUxTrimTests.OpenReadyEditor(root, 10000, show: true);
            Assert.True(editor.TimelineForTest.SetTrimRange(1000, 9000));
            Assert.True(editor.HasUnsavedEditsForTest);
            editor.UnsavedEditsPromptForTest = () => throw new InvalidOperationException("Recording must keep the old editor.");
            typeof(RegionRecordingCoordinator).GetField("_editor", flags)!.SetValue(recorder, editor);
            recorder.ExcludeSelectionWindow = window => { selector = window; return true; };
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
        }
        finally
        {
            recorder.CancelRegionSelection();
            PumpUntil(() => recorder.LastSelectionPreparation.IsCompleted);
            editor?.Close();
            OwnedTestDirectory.Delete(root);
        }
    });

    [Fact]
    public void LibraryHideConfirmationPrecedesOverlayCreationAndFrozenFrameAcquisition() => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-library-record-selection-");
        RegionRecordingCoordinator recorder = Create(root);
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
        recorder.ExcludeSelectionWindow = window =>
        {
            Assert.True(hidden && hideConfirmed);
            Assert.False(window.IsVisible);
            overlays++;
            return false;
        };
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
        }
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
    [InlineData(true)]
    [InlineData(false)]
    public void DelayedAcquisition_LeavesDispatcherResponsive_AndCancelDiscardsLateFrame(bool excluded) => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-record-selection-");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        RegionRecordingCoordinator coordinator = Create(root);
        Window? overlay = null;
        int calls = 0;
        int ended = 0;
        int ownerThread = Environment.CurrentManagedThreadId;
        coordinator.ExcludeSelectionWindow = window => { overlay = window; Assert.False(window.IsVisible); return excluded; };
        coordinator.AcquireSelectionFrame = () =>
        {
            Assert.NotEqual(ownerThread, Environment.CurrentManagedThreadId);
            Interlocked.Increment(ref calls);
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            return Frame();
        };
        coordinator.SessionEnded += (_, _) => ended++;
        try
        {
            coordinator.Toggle();
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
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
            release.Set();
            PumpUntil(() => coordinator.LastSelectionPreparation.IsCompleted);
            Assert.False(coordinator.IsActive);
            Assert.False(overlay.IsVisible);
            Assert.Equal(1, ended);
        }
        finally
        {
            release.Set();
            coordinator.CancelRegionSelection();
            PumpUntil(() => coordinator.LastSelectionPreparation.IsCompleted);
            OwnedTestDirectory.Delete(root);
        }
    });

    [Fact]
    public void AcquisitionFailure_IsReportedOnOwner_AndNextSelectionCanOpen() => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-record-selection-");
        RegionRecordingCoordinator coordinator = Create(root);
        int attempts = 0;
        int failures = 0;
        int ownerThread = Environment.CurrentManagedThreadId;
        Window? overlay = null;
        coordinator.ExcludeSelectionWindow = window => { overlay = window; return false; };
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
            PumpUntil(() => coordinator.LastSelectionPreparation.IsCompleted);
            Assert.Equal(1, failures);
            Assert.False(coordinator.IsActive);
            coordinator.Toggle();
            PumpUntil(() => coordinator.LastSelectionPreparation.IsCompleted);
            Assert.NotNull(overlay);
            Assert.True(overlay.IsVisible);
            Assert.True(coordinator.IsActive);
        }
        finally { coordinator.CancelRegionSelection(); OwnedTestDirectory.Delete(root); }
    });

    [Fact]
    public void DisplayOriginChangedDuringAcquisition_RejectsStaleCoordinates_AndCanRetry() => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-record-selection-");
        RegionRecordingCoordinator coordinator = Create(root);
        int failures = 0;
        Window? overlay = null;
        coordinator.ExcludeSelectionWindow = window => { overlay = window; return true; };
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
        }
        finally { coordinator.CancelRegionSelection(); OwnedTestDirectory.Delete(root); }
    });

    private static RegionRecordingCoordinator Create(string root)
    {
        AppPaths paths = AppPaths.CreateForRoot(root);
        var queue = new CaptureQueue(paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
        var library = new VideoLibraryService(queue, paths, NullLogger<VideoLibraryService>.Instance);
        return new RegionRecordingCoordinator(new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance),
            paths, () => new RecordingSettings(), NullLoggerFactory.Instance, library)
        { SelectionDesktopBounds = () => new RectD(0, 0, 32, 20) };
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
