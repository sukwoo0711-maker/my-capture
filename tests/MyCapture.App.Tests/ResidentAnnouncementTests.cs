using MyCapture.Core.Localization;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class ResidentAnnouncementTests
{
    [Theory]
    [InlineData((int)WelcomeOutcome.Closed)]
    [InlineData((int)WelcomeOutcome.OpenSettings)]
    public void ClosingWelcomePreservesAnUnresolvedConflictAndItsRepairAction(int outcome)
    {
        var notice = new ResidentReadyNotification();
        int repairs = 0;
        Action repair = () => repairs++;
        Action? visibleAction = repair;
        string visibleMessage = "Shortcut conflict";
        notice.RequestResident((WelcomeOutcome)outcome);

        void Replace(string title, string message)
        {
            visibleMessage = message;
            visibleAction = null;
        }

        Assert.False(notice.TryNotifyResident(hotkeysReady: false, captureInProgress: false, notificationVisible: true, Replace));
        Assert.Equal("Shortcut conflict", visibleMessage);
        Assert.Same(repair, visibleAction);
        visibleAction!();
        Assert.Equal(1, repairs);
        Assert.True(notice.HasPendingResident);

        // Dismissing the warning does not resolve the registration conflict.
        Assert.False(notice.TryNotifyResident(hotkeysReady: false, captureInProgress: false, notificationVisible: false, Replace));
        // A successful re-registration can release the pending resident explanation.
        Assert.True(notice.TryNotifyResident(hotkeysReady: true, captureInProgress: false, notificationVisible: false, Replace));
        Assert.False(notice.HasPendingResident);
    }

    [Theory]
    [InlineData((int)WelcomeOutcome.TryCapture)]
    [InlineData((int)WelcomeOutcome.DismissedForCapture)]
    public void CaptureWelcomeOutcomesAnnounceOnlyAfterTheCaptureCompletionOrCancellationSignal(int outcome)
    {
        using var language = UiText.UseLanguage("ko");
        var notice = new ResidentReadyNotification();
        var shown = new List<string>();
        notice.RequestResident((WelcomeOutcome)outcome);
        void Show(string title, string message) => shown.Add(message);

        // The request is recorded before the queued capture actually opens its selector.
        Assert.False(notice.TryNotifyResident(true, false, false, Show));
        Assert.True(notice.WaitingForCapture);
        Assert.False(notice.TryNotifyResident(true, true, false, Show));
        Assert.Empty(shown);

        // App completion and cancellation hooks use the same terminal signal.
        Assert.True(notice.CompleteCapture());
        Assert.True(notice.TryNotifyResident(true, false, false, Show));
        Assert.Equal([UiText.Get("Onboard_ResidentTip")], shown);
        Assert.False(notice.CompleteCapture());
        Assert.False(notice.TryNotifyResident(true, false, false, Show));
        notice.RequestResident((WelcomeOutcome)outcome);
        Assert.False(notice.TryNotifyResident(true, false, false, Show));
        Assert.Single(shown);
    }

    [Fact]
    public void FinishingCaptureKeepsConflictPriorityUntilTheHotkeysAreRegistered()
    {
        var notice = new ResidentReadyNotification();
        int shown = 0;
        notice.RequestResident(WelcomeOutcome.TryCapture);
        Assert.True(notice.CompleteCapture());
        Assert.False(notice.TryNotifyResident(false, false, false, (_, _) => shown++));
        Assert.True(notice.HasPendingResident);
        Assert.True(notice.TryNotifyResident(true, false, false, (_, _) => shown++));
        Assert.Equal(1, shown);
    }

    [Fact]
    public void CaptureReceiptStaysVisibleBeforeTheResidentExplanation()
    {
        var notice = new ResidentReadyNotification();
        int shown = 0;
        notice.RequestResident(WelcomeOutcome.TryCapture);
        notice.CompleteCapture();
        Assert.False(notice.TryNotifyResident(true, false, true, (_, _) => shown++));
        Assert.Equal(0, shown);
        // The presenter's dismissal callback retries after any replacement receipt has opened.
        Assert.False(notice.TryNotifyResident(true, false, true, (_, _) => shown++));
        Assert.True(notice.TryNotifyResident(true, false, false, (_, _) => shown++));
        Assert.Equal(1, shown);
    }

    [Fact]
    public void ANewCaptureStillBlocksTheNoticeAfterTheOriginalCaptureEnds()
    {
        var notice = new ResidentReadyNotification();
        notice.RequestResident(WelcomeOutcome.TryCapture);
        notice.CompleteCapture();
        Assert.False(notice.TryNotifyResident(true, true, false, (_, _) => Assert.Fail("A capture must not contain the resident notice.")));
        Assert.True(notice.HasPendingResident);
        Assert.True(notice.TryNotifyResident(true, false, false, (_, _) => { }));
    }

    [Fact]
    public void LaterLaunchesAndOrdinaryCapturesDoNotRequestResidentGuidance()
    {
        var notice = new ResidentReadyNotification();
        Assert.False(notice.CompleteCapture());
        Assert.False(notice.TryNotifyResident(true, false, false, (_, _) => Assert.Fail("No welcome was closed.")));
        Assert.False(notice.HasPendingResident);
    }

    [Fact]
    public void FailedPresentationDoesNotConsumeThePendingResidentNotice()
    {
        var notice = new ResidentReadyNotification();
        notice.RequestResident(WelcomeOutcome.Closed);
        Assert.Throws<InvalidOperationException>(() => notice.TryNotifyResident(true, false, false, (_, _) => throw new InvalidOperationException()));
        Assert.True(notice.HasPendingResident);
        Assert.True(notice.TryNotifyResident(true, false, false, (_, _) => { }));
        Assert.False(notice.HasPendingResident);
    }
}
