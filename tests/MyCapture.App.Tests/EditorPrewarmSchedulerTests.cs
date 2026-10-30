using MyCapture.App.Editing;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class EditorPrewarmSchedulerTests
{
    [Fact]
    public void ActiveOverlayDefersUntilTheNextIdleAfterItCloses()
    {
        var host = new FakeHost { OverlayActive = true };
        host.Scheduler.Request();
        host.IdleTurn();
        Assert.Equal(0, host.Runs);
        Assert.Equal(1, host.Retries);
        Assert.Empty(host.Idle);

        host.OverlayActive = false;
        host.Scheduler.Request(); // OverlayClosed schedules, never runs inline.
        Assert.Equal(0, host.Runs);
        host.IdleTurn();
        Assert.Equal(1, host.Runs);
        host.Scheduler.Request();
        Assert.Empty(host.Idle);
    }

    [Fact]
    public void AnEditorOpenedDuringTheCapturePermanentlyCancelsPrewarm()
    {
        var host = new FakeHost { OverlayActive = true };
        host.Scheduler.Request();
        host.IdleTurn();
        host.Scheduler.EditorOpened();
        host.OverlayActive = false; // Includes the editor later closing.
        host.Scheduler.Request();
        Assert.Empty(host.Idle);
        Assert.Equal(0, host.Runs);
    }

    [Fact]
    public void QueuedWorkIsCancelledWhenAnEditorOpensBeforeItsIdleTurn()
    {
        var host = new FakeHost();
        host.Scheduler.Request();
        host.Scheduler.EditorOpened();
        host.IdleTurn();
        Assert.Equal(0, host.Runs);
        Assert.Equal(0, host.Retries);
    }

    [Fact]
    public void RecordingStartingAfterQueueingIsRecheckedAtExecution()
    {
        var host = new FakeHost();
        host.Scheduler.Request();
        host.Recording = true;
        host.IdleTurn();
        Assert.Equal(0, host.Runs);
        Assert.Equal(1, host.Retries);
        host.Recording = false;
        host.Scheduler.Request(); // SessionEnded.
        host.IdleTurn();
        Assert.Equal(1, host.Runs);
    }

    [Fact]
    public void DelayedRetryCoversCaptureCompletionWithoutAFlowEndEvent()
    {
        var host = new FakeHost { OtherCaptureBusy = true };
        host.Scheduler.Request();
        host.IdleTurn();
        Assert.Empty(host.Idle); // No immediate busy loop.
        host.Scheduler.Request(); // The delayed idle timer.
        host.IdleTurn();
        Assert.Equal(0, host.Runs);
        Assert.Equal(2, host.Retries);
        host.OtherCaptureBusy = false;
        host.Scheduler.Request();
        host.IdleTurn();
        Assert.Equal(1, host.Runs);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ProbeSwitchOrShutdownStopsAlreadyQueuedWork(bool disabled, bool shutdown)
    {
        var host = new FakeHost();
        host.Scheduler.Request();
        host.Disabled = disabled;
        host.Shutdown = shutdown;
        host.IdleTurn();
        Assert.Equal(0, host.Runs);
        Assert.Equal(0, host.Retries);
        host.Scheduler.Request();
        Assert.Empty(host.Idle);
    }

    [Fact]
    public void DuplicateAndReentrantRequestsStillRunOnlyOnce()
    {
        var host = new FakeHost();
        host.OnRun = host.Scheduler.Request;
        host.Scheduler.Request();
        host.Scheduler.Request();
        Assert.Single(host.Idle);
        host.IdleTurn();
        Assert.Equal(1, host.Runs);
        Assert.Empty(host.Idle);
    }

    [Fact]
    public void FailedPrewarmIsNotRepeated()
    {
        var host = new FakeHost { OnRun = () => throw new InvalidOperationException("prewarm failed") };
        host.Scheduler.Request();
        Assert.Throws<InvalidOperationException>(host.IdleTurn);
        host.Scheduler.Request();
        Assert.Empty(host.Idle);
        Assert.Equal(1, host.Runs);
    }

    private sealed class FakeHost
    {
        internal readonly Queue<Action> Idle = new();
        internal bool OverlayActive, Recording, OtherCaptureBusy, Disabled, Shutdown;
        internal int Runs, Retries;
        internal Action? OnRun;
        internal readonly EditorPrewarmScheduler Scheduler;

        internal FakeHost() => Scheduler = new EditorPrewarmScheduler(Idle.Enqueue,
            () => OverlayActive || Recording || OtherCaptureBusy,
            () => Disabled || Shutdown,
            () => Retries++,
            () => { Runs++; OnRun?.Invoke(); });

        internal void IdleTurn() => Idle.Dequeue()();
    }
}
