using MyCapture.App.Recording;
using MyCapture.Core.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class VideoMulticutPlaybackTests
{
    private static VideoCutTimeline Timeline() => VideoCutTimeline.Create(0, 10000,
        [new(2000, 4000), new(6000, 8000), new(9000, 10000)]);

    [Fact]
    public void RemovedRangeIssuesOneSeekWhileStaleDecoderPositionsWait()
    {
        var playback = new PreviewCutPlayback();
        VideoCutDecisionCheck(playback.Observe(Timeline(), 1999, TimeSpan.Zero), PreviewCutAction.Present, 1999);
        VideoCutDecisionCheck(playback.Observe(Timeline(), 2050, TimeSpan.Zero), PreviewCutAction.Seek, 4000);
        for (int tick = 1; tick < 30; tick++)
        {
            VideoCutDecisionCheck(playback.Observe(Timeline(), 2050, TimeSpan.FromMilliseconds(tick * 20)), PreviewCutAction.Wait, 4000);
        }
        VideoCutDecisionCheck(playback.Observe(Timeline(), 4000, TimeSpan.FromMilliseconds(600)), PreviewCutAction.Present, 4000);
        VideoCutDecisionCheck(playback.Observe(Timeline(), 6050, TimeSpan.FromMilliseconds(800)), PreviewCutAction.Seek, 8000);
    }

    [Fact]
    public void UnobservedLandingTimesOutInsteadOfSeekingForever()
    {
        var playback = new PreviewCutPlayback();
        playback.Observe(Timeline(), 2050, TimeSpan.Zero);
        Assert.Equal(PreviewCutAction.Wait, playback.Observe(Timeline(), 2050, TimeSpan.FromMilliseconds(999)).Action);
        Assert.Equal(PreviewCutAction.TimedOut, playback.Observe(Timeline(), 2050, PreviewCutPlayback.SettleTimeout).Action);
        Assert.Equal(PreviewCutAction.TimedOut, playback.Observe(Timeline(), 2050, TimeSpan.FromSeconds(2)).Action);
        playback.Reset();
        Assert.Equal(PreviewCutAction.Seek, playback.Observe(Timeline(), 2050, TimeSpan.FromSeconds(3)).Action);
    }

    [Fact]
    public void DecoderToleranceAndLateRegressionCannotReissueTheSameCut()
    {
        var playback = new PreviewCutPlayback();
        playback.Observe(Timeline(), 2000, TimeSpan.Zero);
        VideoCutDecisionCheck(playback.Observe(Timeline(), 3999.995, TimeSpan.FromMilliseconds(20)), PreviewCutAction.Present, 4000);
        Assert.Equal(PreviewCutAction.Wait, playback.Observe(Timeline(), 2000, TimeSpan.FromMilliseconds(40)).Action);
        Assert.Equal(PreviewCutAction.Present, playback.Observe(Timeline(), 4050, TimeSpan.FromMilliseconds(60)).Action);
    }

    [Fact]
    public void TrailingCutStopsAtTheLastKeptEnd()
    {
        var playback = new PreviewCutPlayback();
        VideoCutDecisionCheck(playback.Observe(Timeline(), 9100, TimeSpan.Zero), PreviewCutAction.Stop, 9000);
    }

    [Fact]
    public void StoppingSeeksAFrameInsideTheLastKeptSegment()
    {
        Assert.Equal(8900, PreviewCutPlayback.LastKeptFrame(Timeline(), 10), 6);
        VideoCutTimeline fractional = VideoCutTimeline.Create(0, 10000, [new(9005, 10000)]);
        Assert.Equal(9000, PreviewCutPlayback.LastKeptFrame(fractional, 10), 6);
        VideoCutTimeline shortLast = VideoCutTimeline.Create(0, 1000, [new(0, 899)]);
        Assert.InRange(PreviewCutPlayback.LastKeptFrame(shortLast, 1), 899, 999.999);
    }

    [Fact]
    public void LeadingCutAndNoCutTrimUseTheCorrectSourceStart()
    {
        var playback = new PreviewCutPlayback();
        VideoCutTimeline leading = VideoCutTimeline.Create(1000, 10000, [new(1000, 3000)]);
        VideoCutDecisionCheck(playback.Observe(leading, 1000, TimeSpan.Zero), PreviewCutAction.Seek, 3000);
        playback.Reset();
        VideoCutDecisionCheck(playback.Observe(VideoCutTimeline.Create(1000, 10000), 2000, TimeSpan.Zero), PreviewCutAction.Present, 2000);
    }

    private static void VideoCutDecisionCheck(PreviewCutDecision decision, PreviewCutAction action, double sourceMs)
    {
        Assert.Equal(action, decision.Action);
        Assert.Equal(sourceMs, decision.SourceTimeMs);
    }
}
