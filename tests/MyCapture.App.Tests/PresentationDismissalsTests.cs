using MyCapture.App.Capture;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PresentationDismissalsTests
{
    [Fact]
    public void LegacySettleProbeWaitsAfterExcludedSurfacesButStillExpires()
    {
        bool previous = PresentationDismissals.UseLegacySettleForProbe;
        try
        {
            PresentationDismissals.UseLegacySettleForProbe = true;
            var dismissals = new PresentationDismissals();
            dismissals.Record(1000, captureExcluded: true);
            Assert.True(dismissals.NeedsSettle(1749));
            Assert.False(dismissals.NeedsSettle(1750));
        }
        finally { PresentationDismissals.UseLegacySettleForProbe = previous; }
    }

    [Fact]
    public void ShellReportsEachNotificationActualExclusionAtDismissal() => StaTestHost.Run(() =>
    {
        using var shell = new ThemedShellPresenter(() => false);
        var results = new List<bool>();
        shell.CaptureSurfaceDismissed += results.Add;
        var excluded = new System.Windows.Window();
        var failed = new System.Windows.Window();
        shell.TrackNotification(excluded);
        CaptureWindowExclusion.RecordResult(excluded, succeeded: true);
        excluded.Close();
        shell.TrackNotification(failed);
        CaptureWindowExclusion.RecordResult(failed, succeeded: false);
        failed.Close();
        Assert.Equal(new[] { true, false }, results);
    });

    [Fact]
    public void UnknownWindowAndFailedReapplicationAreNotTreatedAsExcluded() => StaTestHost.Run(() =>
    {
        var first = new System.Windows.Window();
        var other = new System.Windows.Window();
        try
        {
            Assert.False(CaptureWindowExclusion.WasApplied(first));
            CaptureWindowExclusion.RecordResult(first, succeeded: true);
            Assert.True(CaptureWindowExclusion.WasApplied(first));
            Assert.False(CaptureWindowExclusion.WasApplied(other));
            CaptureWindowExclusion.RecordResult(first, succeeded: false);
            Assert.False(CaptureWindowExclusion.WasApplied(first));
        }
        finally { first.Close(); other.Close(); }
    });

    [Fact]
    public void NoDismissalAndExcludedDismissalsDoNotWait()
    {
        var dismissals = new PresentationDismissals();
        Assert.False(dismissals.NeedsSettle(1000));
        dismissals.Record(1000, captureExcluded: true);
        dismissals.Record(1100, captureExcluded: true);
        Assert.False(dismissals.NeedsSettle(1100));
    }

    [Fact]
    public void ExcludedDismissalCannotMaskAnEarlierFailedExclusionOrEditor()
    {
        var dismissals = new PresentationDismissals();
        dismissals.Record(1000, captureExcluded: false);
        dismissals.Record(1700, captureExcluded: true);
        Assert.Equal(1700, dismissals.LastDismissedTick);
        Assert.True(dismissals.NeedsSettle(1749));
        Assert.False(dismissals.NeedsSettle(1750));
    }

    [Fact]
    public void MostRecentUnexcludedDismissalExtendsItsOwnWindow()
    {
        var dismissals = new PresentationDismissals();
        dismissals.Record(1000, captureExcluded: false);
        dismissals.Record(1400, captureExcluded: false);
        dismissals.Record(1700, captureExcluded: true);
        Assert.True(dismissals.NeedsSettle(2149));
        Assert.False(dismissals.NeedsSettle(2150));
    }

    [Theory]
    [InlineData(0L, long.MinValue, false)]
    [InlineData(long.MaxValue, long.MinValue, false)]
    [InlineData(1000L, 1001L, false)]
    [InlineData(long.MinValue + 5, long.MaxValue - 5, true)]
    public void SentinelAndTickWrapAreHandled(long now, long dismissed, bool expected)
    {
        var dismissals = new PresentationDismissals();
        dismissals.Record(dismissed, captureExcluded: false);
        Assert.Equal(expected, dismissals.NeedsSettle(now));
    }
}
