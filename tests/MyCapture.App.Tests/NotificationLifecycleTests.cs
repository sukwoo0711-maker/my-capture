using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using MyCapture.App.Themes;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class NotificationLifecycleTests
{
    [Fact]
    public void ClosingAnUnshownTrackedNotificationClearsItAndStopsItsCountdownOnce() => StaTestHost.Run(() =>
    {
        using var presenter = new ThemedShellPresenter(() => false);
        var window = new Window();
        int dismissed = 0, expired = 0;
        presenter.NotificationDismissed += () => dismissed++;
        var countdown = AttachCountdown(presenter, () => expired++);
        try
        {
            presenter.TrackNotification(window);
            countdown.Start(TimeSpan.FromSeconds(5));
            Assert.True(presenter.HasNotification);
            Assert.False(window.IsVisible);
            Assert.True(countdown.IsRunning);

            window.Close();

            Assert.False(presenter.HasNotification);
            Assert.Null(presenter.NotificationWindowForTest);
            Assert.False(countdown.IsRunning);
            Assert.Null(countdown.Remaining);
            Assert.Equal(1, dismissed);
            countdown.Advance(TimeSpan.FromMinutes(1));
            presenter.Dismiss();
            Assert.Equal(0, expired);
            Assert.Equal(1, dismissed);
        }
        finally
        {
            countdown.Stop();
            presenter.Dismiss();
        }
    });

    [Fact]
    public void RepeatedDismissRaisesTheNotificationClosedEventOnlyOnce() => StaTestHost.Run(() =>
    {
        using var presenter = new ThemedShellPresenter(() => false);
        int dismissed = 0, windowClosed = 0;
        presenter.NotificationDismissed += () => dismissed++;
        var window = new Window();
        window.Closed += (_, _) => windowClosed++;
        presenter.TrackNotification(window);

        presenter.Dismiss();
        presenter.Dismiss();

        Assert.False(presenter.HasNotification);
        Assert.Equal(1, windowClosed);
        Assert.Equal(1, dismissed);
    });

    [Fact]
    public void AStaleWindowClosingCannotClearItsReplacementOrStopTheNewCountdown() => StaTestHost.Run(() =>
    {
        using var presenter = new ThemedShellPresenter(() => false);
        var previous = new Window();
        var replacement = new Window();
        int dismissed = 0;
        presenter.NotificationDismissed += () => dismissed++;
        var countdown = AttachCountdown(presenter, () => { });
        try
        {
            presenter.TrackNotification(previous);
            presenter.TrackNotification(replacement);
            countdown.Start(TimeSpan.FromSeconds(5));

            previous.Close();

            Assert.True(presenter.HasNotification);
            Assert.Same(replacement, presenter.NotificationWindowForTest);
            Assert.True(countdown.IsRunning);
            Assert.Equal(0, dismissed);
            replacement.Close();
            Assert.False(presenter.HasNotification);
            Assert.False(countdown.IsRunning);
            Assert.Equal(1, dismissed);
        }
        finally
        {
            countdown.Stop();
            previous.Close();
            presenter.Dismiss();
        }
    });

    [Fact]
    public void QueuedResidentRetryWaitsForAReplacementThenRunsOnceAfterItsDirectClose() => StaTestHost.Run(() =>
    {
        using var presenter = new ThemedShellPresenter(() => false);
        var notice = new ResidentReadyNotification();
        notice.RequestResident(WelcomeOutcome.TryCapture);
        notice.CompleteCapture();
        int shown = 0;
        presenter.NotificationDismissed += () => Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle, new Action(() =>
                notice.TryNotifyResident(true, false, presenter.HasNotification, (_, _) => shown++)));
        presenter.TrackNotification(new Window());

        presenter.Dismiss();
        var replacement = new Window();
        presenter.TrackNotification(replacement);
        PumpAfterResidentRetry();

        Assert.Equal(0, shown);
        Assert.True(notice.HasPendingResident);
        Assert.Same(replacement, presenter.NotificationWindowForTest);

        replacement.Close();
        PumpAfterResidentRetry();
        presenter.Dismiss();
        PumpAfterResidentRetry();

        Assert.Equal(1, shown);
        Assert.False(notice.HasPendingResident);
    });

    private static FeedbackCountdown AttachCountdown(ThemedShellPresenter presenter, Action expired)
    {
        var countdown = new FeedbackCountdown(Dispatcher.CurrentDispatcher, () => false, expired);
        typeof(ThemedShellPresenter).GetField("_countdown", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(presenter, countdown);
        return countdown;
    }

    private static void PumpAfterResidentRetry()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.SystemIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}