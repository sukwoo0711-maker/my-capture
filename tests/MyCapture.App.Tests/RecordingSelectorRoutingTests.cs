using System.IO;
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

namespace MyCapture.App.Tests;

public sealed class RecordingSelectorRoutingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompletedSelectionPassesScreenCoordinatesAndEndsOnce(bool native) => StaTestHost.Run(() =>
    {
        // No recording or library write is needed to verify selector-to-controls routing.
        string root = OwnedTestDirectory.Create("mc-record-routing-");
        var bounds = new RectD(100, 100, 64, 48);
        var monitor = new MonitorInfo("recording-test", bounds, bounds, 96, true);
        BitmapSource bitmap = BitmapSource.Create(64, 48, 96, 96, PixelFormats.Bgra32, null, new byte[64 * 48 * 4], 64 * 4);
        bitmap.Freeze();
        var frame = new FrozenFrame(bitmap, bounds, monitor, 0);
        AppPaths paths = AppPaths.CreateForRoot(root);
        var queue = new CaptureQueue(paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
        var library = new VideoLibraryService(queue, paths, NullLogger<VideoLibraryService>.Instance);
        var coordinator = new RegionRecordingCoordinator(new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance),
            paths, () => new RecordingSettings(), NullLoggerFactory.Instance, library)
        {
            UseNativeDibSelector = native,
            AcquireSelectionFrame = () => frame,
            SelectionDesktopBounds = () => bounds,
        };
        coordinator.ExcludeSelectionWindow = window =>
        {
            var view = Assert.IsType<CaptureOverlayView>(window.Content);
            view.ReadCursor = () => new PointD(110, 110);
            view.ReadMonitors = () => [monitor];
            return true;
        };
        coordinator.ExcludeNativeSelectionWindow = _ =>
        {
            NativeCaptureOverlay selector = Assert.IsType<NativeCaptureOverlay>(coordinator.NativeOverlayForProbe);
            selector.ReadCursor = () => new PointD(110, 110);
            selector.ReadMonitors = () => [monitor];
            return true;
        };
        int ended = 0, selected = 0;
        int ownerThread = Environment.CurrentManagedThreadId;
        RectD? selectedScreenRegion = null;
        RecordingSelector? overlay = null;
        coordinator.SessionEnded += (_, _) => ended++;
        coordinator.OpenControlsForTest = region =>
        {
            selected++;
            selectedScreenRegion = region;
            Assert.Equal(ownerThread, Environment.CurrentManagedThreadId);
            Assert.False(overlay!.IsVisible);
            Assert.True(coordinator.IsSelecting);
        };
        try
        {
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
            coordinator.Toggle();
            PinHistoryTestSupport.Await(coordinator.LastSelectionPreparation);
            overlay = new RecordingSelector(coordinator, native);
            Assert.True(overlay.IsVisible);
            Assert.True(overlay.RecordingSelection);
            overlay.CompleteSelection(new RectD(3, 2, 18, 12));
            Assert.Equal(new RectD(103, 102, 18, 12), selectedScreenRegion);
            Assert.Equal(1, selected);
            Assert.False(overlay.IsVisible);
            Assert.False(coordinator.IsSelecting);
            Assert.False(coordinator.HasRecordingSession);
            Assert.False(coordinator.IsActive);
            Assert.Equal(1, ended);
            coordinator.CancelRegionSelection();
            Assert.Equal(1, ended);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            coordinator.CancelRegionSelection();
            PinHistoryTestSupport.Await(coordinator.LastSelectionPreparation);
            OwnedTestDirectory.Delete(root);
        }
    });
}
