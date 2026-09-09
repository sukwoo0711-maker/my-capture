using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Recording;
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
