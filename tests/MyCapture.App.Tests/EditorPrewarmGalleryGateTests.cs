using System.Windows;
using MyCapture.App.Editing;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class EditorPrewarmGalleryGateTests
{
    [Fact]
    public void ShowingTheObservedWindowPausesQueuedWorkUntilActualHide() => StaTestHost.Run(() =>
    {
        var host = new Host();
        var window = CreateWindow();
        try
        {
            host.Gate.Observe(window);
            host.Scheduler.Request();
            Assert.Single(host.Idle);
            window.Show();
            Assert.True(window.IsVisible);
            Assert.Equal(1, host.StoppedRetries);
            host.Idle.Dequeue()();
            for (int i = 0; i < 10; i++) host.Scheduler.Request();
            Assert.Empty(host.Idle);
            Assert.Equal(0, host.Retries);
            Assert.Equal(0, host.Runs);
            window.Hide();
            Assert.False(window.IsVisible);
            Assert.Single(host.Idle);
            host.Idle.Dequeue()();
            Assert.Equal(1, host.Runs);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void RepeatedObservationAndHideThenReopenDoNotDuplicateOrLoseWork() => StaTestHost.Run(() =>
    {
        var host = new Host();
        var window = CreateWindow();
        try
        {
            window.Show();
            host.Gate.Observe(window);
            host.Gate.Observe(window);
            Assert.Equal(1, host.StoppedRetries);
            window.Hide();
            Assert.Single(host.Idle);
            window.Show();
            host.Idle.Dequeue()();
            Assert.Equal(0, host.Runs);
            Assert.Equal(0, host.Retries);
            Assert.Empty(host.Idle);
            window.Hide();
            Assert.Single(host.Idle);
            host.Idle.Dequeue()();
            host.Scheduler.Request();
            Assert.Empty(host.Idle);
            Assert.Equal(1, host.Runs);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void ReplacedWindowEventsCannotReleaseTheCurrentVisibleWindow() => StaTestHost.Run(() =>
    {
        var host = new Host();
        var first = CreateWindow();
        var second = CreateWindow();
        try
        {
            first.Show();
            host.Gate.Observe(first);
            second.Show();
            host.Gate.Observe(second);
            first.Hide();
            first.Close();
            Assert.True(second.IsVisible);
            Assert.Empty(host.Idle);
            second.Hide();
            Assert.Single(host.Idle);
            host.Idle.Dequeue()();
            Assert.Equal(1, host.Runs);
        }
        finally { first.Close(); second.Close(); }
    });

    [Fact]
    public void RealCloseAfterEditorOpenedCannotRevivePrewarm() => StaTestHost.Run(() =>
    {
        var host = new Host();
        var window = CreateWindow();
        try
        {
            window.Show();
            host.Gate.Observe(window);
            host.Scheduler.EditorOpened();
            window.Close();
            Assert.False(window.IsVisible);
            Assert.Empty(host.Idle);
            Assert.Equal(0, host.Runs);
            Assert.Equal(0, host.Retries);
        }
        finally { window.Close(); }
    });

    private static Window CreateWindow() => new()
    {
        Width = 200, Height = 100, Left = -20000, Top = -20000,
        ShowActivated = false, ShowInTaskbar = false,
    };

    private sealed class Host
    {
        internal readonly Queue<Action> Idle = new();
        internal readonly EditorPrewarmScheduler Scheduler;
        internal readonly EditorPrewarmGalleryGate Gate;
        internal int Runs, Retries, StoppedRetries;

        internal Host()
        {
            Scheduler = new EditorPrewarmScheduler(Idle.Enqueue, () => false, () => false,
                () => Retries++, () => Runs++);
            Gate = new EditorPrewarmGalleryGate(Scheduler.SetGalleryVisible,
                () => StoppedRetries++, Scheduler.Request);
        }
    }
}
