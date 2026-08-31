using System.Diagnostics;
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
        coordinator.SelectionPersistRequested = _ =>
        {
            persistenceCalls++;
            return releasePersistence.Task;
        };
        coordinator.OverlayClosed += (_, _) => closedEvents++;

        BitmapSource bitmap = Solid(32, 20);
        var frame = new FrozenFrame(bitmap, new RectD(0, 0, 32, 20), null, 0);

        Assert.True(coordinator.StartWithSelection(frame, new RectD(2, 3, 18, 11)));
        Assert.True(coordinator.IsActive);
        Assert.Equal(1, persistenceCalls);

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
