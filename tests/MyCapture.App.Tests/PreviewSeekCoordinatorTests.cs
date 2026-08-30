using System.Collections.Concurrent;
using System.Diagnostics;
using MyCapture.App.Recording;
using MyCapture.Core.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PreviewSeekCoordinatorTests
{
    private sealed class PendingCall
    {
        private readonly TaskCompletionSource<bool> _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal PendingCall(PreviewSeekRequest request) => Request = request;

        internal PreviewSeekRequest Request { get; }

        internal Task Release => _release.Task;

        internal void Complete() => _release.TrySetResult(true);

        internal void Fail(Exception exception) => _release.TrySetException(exception);

        internal void Cancel(CancellationToken cancellationToken) =>
            _release.TrySetCanceled(cancellationToken);
    }

    private sealed class ControlledPreviewEngine : IVideoPreviewEngine
    {
        private readonly ConcurrentQueue<PendingCall> _calls = new();
        private readonly SemaphoreSlim _available = new(0);
        private int _active;
        private int _maxActive;
        private int _callCount;

        internal int CallCount => Volatile.Read(ref _callCount);

        internal int MaxActive => Volatile.Read(ref _maxActive);

        public async ValueTask<PresentedPreviewFrame> SeekAsync(
            PreviewSeekRequest request,
            CancellationToken cancellationToken)
        {
            int active = Interlocked.Increment(ref _active);
            Interlocked.Increment(ref _callCount);
            UpdateMaximum(ref _maxActive, active);

            var call = new PendingCall(request);
            _calls.Enqueue(call);
            _available.Release();
            using CancellationTokenRegistration registration = cancellationToken.Register(
                () => call.Cancel(cancellationToken));
            try
            {
                await call.Release.ConfigureAwait(false);
                return new PresentedPreviewFrame(
                    request.Generation,
                    request.TargetPositionMs,
                    request.TargetPositionMs,
                    request.TargetFrameIndex,
                    request.Mode);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        internal async Task<PendingCall> NextCallAsync()
        {
            bool signaled = await _available.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(signaled, "preview engine call was not issued within timeout");
            Assert.True(_calls.TryDequeue(out PendingCall? call));
            return call!;
        }

        public void Dispose() => _available.Dispose();

        private static void UpdateMaximum(ref int location, int candidate)
        {
            int current;
            do
            {
                current = Volatile.Read(ref location);
                if (candidate <= current)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(ref location, candidate, current) != current);
        }
    }

    private sealed class ImmediatePreviewEngine : IVideoPreviewEngine
    {
        internal ConcurrentQueue<long> Timestamps { get; } = new();

        public ValueTask<PresentedPreviewFrame> SeekAsync(
            PreviewSeekRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Timestamps.Enqueue(Stopwatch.GetTimestamp());
            return ValueTask.FromResult(new PresentedPreviewFrame(
                request.Generation,
                request.TargetPositionMs,
                request.TargetPositionMs,
                request.TargetFrameIndex,
                request.Mode));
        }

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task PreviewBurst_KeepsOneInFlightAndOnlyLatestPending()
    {
        using var engine = new ControlledPreviewEngine();
        await using var coordinator = new PreviewSeekCoordinator(engine, 15, TimeSpan.Zero);

        coordinator.RequestPreview(70);
        PendingCall first = await engine.NextCallAsync();
        coordinator.RequestPreview(140);
        coordinator.RequestPreview(210);
        coordinator.RequestPreview(300);

        Assert.True(coordinator.IsInFlightForTest);
        Assert.Equal(1, coordinator.PendingCountForTest);

        first.Complete();
        PendingCall latest = await engine.NextCallAsync();
        Assert.Equal(300, latest.Request.TargetPositionMs, precision: 3);
        latest.Complete();
        await coordinator.WaitForIdleForTestAsync();

        Assert.Equal(2, engine.CallCount);
        Assert.Equal(1, engine.MaxActive);
        Assert.Equal(1, coordinator.MaxObservedInFlightForTest);
        Assert.Equal(300, coordinator.PresentedPositionMs, precision: 3);
        Assert.True(coordinator.DroppedRequestCountForTest >= 2);
        Assert.Equal(1, coordinator.StaleResultCountForTest);
        Assert.False(coordinator.HasPendingForTest);
    }

    [Fact]
    public async Task ExactRequest_ReplacesPreviewAndSuppressesStaleInFlightResult()
    {
        using var engine = new ControlledPreviewEngine();
        await using var coordinator = new PreviewSeekCoordinator(engine, 15, TimeSpan.Zero);

        coordinator.RequestPreview(100);
        PendingCall first = await engine.NextCallAsync();
        coordinator.RequestPreview(300);
        Task<PresentedPreviewFrame> exactTask = coordinator.RequestExactAsync(900);

        first.Complete();
        PendingCall exact = await engine.NextCallAsync();
        Assert.Equal(PreviewSeekMode.Exact, exact.Request.Mode);
        Assert.Equal(900, exact.Request.TargetPositionMs, precision: 3);
        exact.Complete();

        PresentedPreviewFrame presented = await exactTask;
        await coordinator.WaitForIdleForTestAsync();
        Assert.Equal(PreviewSeekMode.Exact, presented.Mode);
        Assert.Equal(900, coordinator.PresentedPositionMs, precision: 3);
        Assert.True(coordinator.StaleResultCountForTest >= 1);
        Assert.Equal(1, engine.MaxActive);
    }

    [Fact]
    public async Task SameTargetFrame_IsDeduplicatedWhileInFlightAndWhenIdle()
    {
        using var engine = new ControlledPreviewEngine();
        await using var coordinator = new PreviewSeekCoordinator(engine, 15, TimeSpan.Zero);

        long firstGeneration = coordinator.RequestPreview(100);
        PendingCall first = await engine.NextCallAsync();
        long duplicateGeneration = coordinator.RequestPreview(110); // both are frame 1 at 15fps

        Assert.Equal(firstGeneration, duplicateGeneration);
        Assert.False(coordinator.HasPendingForTest);
        first.Complete();
        await coordinator.WaitForIdleForTestAsync();

        long idleDuplicate = coordinator.RequestPreview(105);
        await coordinator.WaitForIdleForTestAsync();
        Assert.Equal(firstGeneration, idleDuplicate);
        Assert.Equal(1, engine.CallCount);
        Assert.Equal(2, coordinator.DeduplicatedRequestCountForTest);
    }

    [Fact]
    public async Task PreviewSampling_WaitsApproximatelyOneIntervalBetweenDispatches()
    {
        using var engine = new ImmediatePreviewEngine();
        await using var coordinator = new PreviewSeekCoordinator(
            engine,
            15,
            PreviewSeekCoordinator.DefaultPreviewInterval);

        coordinator.RequestPreview(0);
        await coordinator.WaitForIdleForTestAsync();
        var completionWait = Stopwatch.StartNew();
        coordinator.RequestPreview(1000);
        await coordinator.WaitForIdleForTestAsync();
        completionWait.Stop();

        Assert.Equal(2, engine.Timestamps.Count);
        Assert.True(engine.Timestamps.TryDequeue(out long firstDispatch));
        Assert.True(engine.Timestamps.TryDequeue(out long secondDispatch));
        TimeSpan dispatchInterval = Stopwatch.GetElapsedTime(firstDispatch, secondDispatch);
        Assert.True(
            dispatchInterval >= TimeSpan.FromMilliseconds(25),
            $"preview dispatch interval was too short: {dispatchInterval.TotalMilliseconds:0.0}ms");
        Assert.True(completionWait.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task EngineFailure_DoesNotStrandWorkerAndNextRequestCanSucceed()
    {
        using var engine = new ControlledPreviewEngine();
        await using var coordinator = new PreviewSeekCoordinator(engine, 15, TimeSpan.Zero);

        coordinator.RequestPreview(100);
        PendingCall failed = await engine.NextCallAsync();
        failed.Fail(new InvalidOperationException("synthetic seek failure"));
        await coordinator.WaitForIdleForTestAsync();
        Assert.IsType<InvalidOperationException>(coordinator.LastError);

        coordinator.RequestPreview(500);
        PendingCall recovered = await engine.NextCallAsync();
        recovered.Complete();
        await coordinator.WaitForIdleForTestAsync();

        Assert.Equal(500, coordinator.PresentedPositionMs, precision: 3);
        Assert.Equal(2, engine.CallCount);
        Assert.Equal(1, engine.MaxActive);
    }

    [Fact]
    public async Task Dispose_CancelsInFlightAndNeverPumpsPendingRequest()
    {
        using var engine = new ControlledPreviewEngine();
        var coordinator = new PreviewSeekCoordinator(engine, 15, TimeSpan.Zero);

        coordinator.RequestPreview(100);
        _ = await engine.NextCallAsync();
        coordinator.RequestPreview(500);
        await coordinator.DisposeAsync();

        Assert.Equal(1, engine.CallCount);
        Assert.False(coordinator.HasPendingForTest);
        Assert.False(coordinator.IsInFlightForTest);
        Assert.Equal(0, coordinator.RequestPreview(900));
    }
}
