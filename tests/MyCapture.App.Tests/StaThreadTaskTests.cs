using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Threading;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class StaThreadTaskTests
{
    // Hang guard only: every wait below ends on a completion or handshake signal. The first
    // WPF dispatcher/imaging use in a cold test process loads native rendering components, and
    // on loaded hosted runners that startup has exceeded a 5 s budget without any hang.
    private static readonly TimeSpan HangCeiling = TimeSpan.FromMinutes(2);

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
                if (!allowShutdown.Wait(HangCeiling)) throw new TimeoutException();
            };
            dispatcher.ShutdownFinished += (_, _) => finished = true;
            return 42;
        });
        try
        {
            Assert.True(shuttingDown.Wait(HangCeiling));
            Assert.False(operation.IsCompleted);
        }
        finally { allowShutdown.Set(); }
        Assert.Equal(42, await operation.WaitAsync(HangCeiling));
        Assert.True(finished);
    }

    [Fact]
    public void FrozenRenderedBitmapRemainsReadable_AndCallerDispatcherIsUntouched() => StaTestHost.Run(() =>
    {
        Dispatcher caller = Dispatcher.CurrentDispatcher;
        var probe = new WorkerProbe();
        BitmapSource bitmap = probe.Await(StaThreadTask.RunAsync(() =>
        {
            Dispatcher worker = probe.Enter();
            Assert.NotSame(caller, worker);
            var visual = new DrawingVisual();
            using (DrawingContext drawing = visual.RenderOpen())
                drawing.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 8, 6));
            var image = new RenderTargetBitmap(8, 6, 96, 96, PixelFormats.Pbgra32);
            image.Render(visual);
            image.Freeze();
            return probe.Return(image);
        }));
        Assert.True(probe.ShutdownFinished);
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
            operation.WaitAsync(HangCeiling).GetAwaiter().GetResult());
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
        Thread? worker = null;
        Task<int> operation = StaThreadTask.RunAsync(() =>
        {
            worker = Thread.CurrentThread;
            Dispatcher.CurrentDispatcher.ShutdownStarted += (_, _) => throw cleanupFailure;
            if (actionFails) throw actionFailure;
            return 42;
        });
        if (actionFails)
        {
            AggregateException failure = Assert.Throws<AggregateException>(() =>
                operation.WaitAsync(HangCeiling).GetAwaiter().GetResult());
            Assert.Equal(new Exception[] { actionFailure, cleanupFailure }, failure.InnerExceptions);
        }
        else
        {
            Assert.Same(cleanupFailure, Assert.Throws<InvalidOperationException>(() =>
                operation.WaitAsync(HangCeiling).GetAwaiter().GetResult()));
        }

        // The failed shutdown left the dispatcher's windows alive, so the worker keeps pumping
        // them instead of exiting and letting its teardown call their managed window procedures.
        Assert.NotNull(worker);
        Assert.False(worker!.Join(TimeSpan.FromMilliseconds(300)), "the worker exited with a live dispatcher");
        Assert.False(Dispatcher.FromThread(worker)!.HasShutdownFinished);
    }

    [Fact]
    public void NonFrozenBitmapIsRejectedBeforeLeavingWorker()
    {
        var probe = new WorkerProbe();
        Task<WriteableBitmap> operation = StaThreadTask.RunAsync(() =>
        {
            probe.Enter();
            return probe.Return(new WriteableBitmap(2, 2, 96, 96, PixelFormats.Bgra32, null));
        });
        Assert.Throws<InvalidOperationException>(() => probe.Await(operation));
        Assert.True(probe.ShutdownFinished);
    }

    [Fact]
    public async Task NonWpfOperationDoesNotCreateDispatcher()
    {
        Thread worker = await StaThreadTask.RunAsync(() =>
        {
            Assert.Null(Dispatcher.FromThread(Thread.CurrentThread));
            return Thread.CurrentThread;
        }).WaitAsync(HangCeiling);
        Assert.Null(Dispatcher.FromThread(worker));
    }

    /// <summary>
    /// Records how far an imaging worker progressed so a ceiling hit reports whether the
    /// worker never started, stalled inside WPF imaging, or stalled in dispatcher teardown.
    /// </summary>
    private sealed class WorkerProbe
    {
        private volatile string _phase = "queued; worker thread has not entered the action";
        private volatile bool _shutdownFinished;

        public bool ShutdownFinished => _shutdownFinished;

        public Dispatcher Enter()
        {
            _phase = "entered the action; WPF imaging has not returned";
            Dispatcher worker = Dispatcher.CurrentDispatcher;
            worker.ShutdownFinished += (_, _) => _shutdownFinished = true;
            return worker;
        }

        public T Return<T>(T value)
        {
            _phase = "action returned; dispatcher shutdown has not completed";
            return value;
        }

        public T Await<T>(Task<T> operation)
        {
            try
            {
                return operation.WaitAsync(HangCeiling).GetAwaiter().GetResult();
            }
            catch (TimeoutException) when (!operation.IsCompleted)
            {
                throw new TimeoutException(
                    $"STA worker did not complete within {HangCeiling}. Last phase: {_phase}.");
            }
        }
    }
}
