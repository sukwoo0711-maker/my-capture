using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Threading;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class StaThreadTaskTests
{
    [Fact]
    public async Task CompletionWaitsForWorkerDispatcherShutdown()
    {
        using var shuttingDown = new ManualResetEventSlim();
        using var allowShutdown = new ManualResetEventSlim();
        bool finished = false;
        Task<int> operation = StaThreadTask.RunAsync(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.ShutdownStarted += (_, _) =>
            {
                shuttingDown.Set();
                if (!allowShutdown.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            };
            dispatcher.ShutdownFinished += (_, _) => finished = true;
            return 42;
        });
        try
        {
            Assert.True(shuttingDown.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(operation.IsCompleted);
        }
        finally { allowShutdown.Set(); }
        Assert.Equal(42, await operation.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(finished);
    }

    [Fact]
    public void FrozenRenderedBitmapRemainsReadable_AndCallerDispatcherIsUntouched() => StaTestHost.Run(() =>
    {
        Dispatcher caller = Dispatcher.CurrentDispatcher;
        bool workerShutdown = false;
        BitmapSource bitmap = StaThreadTask.RunAsync(() =>
        {
            Dispatcher worker = Dispatcher.CurrentDispatcher;
            Assert.NotSame(caller, worker);
            worker.ShutdownFinished += (_, _) => workerShutdown = true;
            var visual = new DrawingVisual();
            using (DrawingContext drawing = visual.RenderOpen())
                drawing.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 8, 6));
            var image = new RenderTargetBitmap(8, 6, 96, 96, PixelFormats.Pbgra32);
            image.Render(visual);
            image.Freeze();
            return image;
        }).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        Assert.True(workerShutdown);
        Assert.False(caller.HasShutdownStarted);
        Assert.True(bitmap.IsFrozen);
        var pixels = new byte[8 * 6 * 4];
        bitmap.CopyPixels(pixels, 8 * 4, 0);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, pixels.Take(4));
    });

    [Fact]
    public void FailureStillShutsDownWorker_AndPreservesOriginalException()
    {
        var expected = new InvalidOperationException("action failed");
        bool finished = false;
        Task<int> operation = StaThreadTask.RunAsync<int>(() =>
        {
            Dispatcher.CurrentDispatcher.ShutdownFinished += (_, _) => finished = true;
            throw expected;
        });
        Exception actual = Assert.Throws<InvalidOperationException>(() =>
            operation.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult());
        Assert.Same(expected, actual);
        Assert.True(finished);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CleanupFailureCannotBecomeSuccess_AndKeepsActionFailure(bool actionFails)
    {
        var actionFailure = new ArgumentException("action failed");
        var cleanupFailure = new InvalidOperationException("cleanup failed");
        Task<int> operation = StaThreadTask.RunAsync(() =>
        {
            Dispatcher.CurrentDispatcher.ShutdownStarted += (_, _) => throw cleanupFailure;
            if (actionFails) throw actionFailure;
            return 42;
        });
        if (actionFails)
        {
            AggregateException failure = Assert.Throws<AggregateException>(() =>
                operation.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult());
            Assert.Equal(new Exception[] { actionFailure, cleanupFailure }, failure.InnerExceptions);
        }
        else
        {
            Assert.Same(cleanupFailure, Assert.Throws<InvalidOperationException>(() =>
                operation.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult()));
        }
    }

    [Fact]
    public void NonFrozenBitmapIsRejectedBeforeLeavingWorker()
    {
        bool shutdown = false;
        Task<WriteableBitmap> operation = StaThreadTask.RunAsync(() =>
        {
            Dispatcher.CurrentDispatcher.ShutdownFinished += (_, _) => shutdown = true;
            return new WriteableBitmap(2, 2, 96, 96, PixelFormats.Bgra32, null);
        });
        Assert.Throws<InvalidOperationException>(() =>
            operation.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult());
        Assert.True(shutdown);
    }

    [Fact]
    public async Task NonWpfOperationDoesNotCreateDispatcher()
    {
        Thread worker = await StaThreadTask.RunAsync(() =>
        {
            Assert.Null(Dispatcher.FromThread(Thread.CurrentThread));
            return Thread.CurrentThread;
        }).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(Dispatcher.FromThread(worker));
    }
}
