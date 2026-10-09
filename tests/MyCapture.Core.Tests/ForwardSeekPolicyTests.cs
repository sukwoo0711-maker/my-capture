using MyCapture.Core.Recording;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class ForwardSeekPolicyTests
{
    [Theory]
    [InlineData(33.3, 66.7, false)]       // next frame of playback / MP4 export
    [InlineData(1000, 1800, false)]       // GIF export at 5 fps and 4x speed
    [InlineData(1000, 2999.99, false)]    // just under the threshold
    [InlineData(1000, 3000, true)]        // exactly the threshold
    [InlineData(33.3, 9_999, true)]       // probe: first frame, then the final frame
    public void ShouldSeek_OnlyForGapsAtLeastTheThreshold(double newest, double target, bool expected)
    {
        var policy = new ForwardSeekPolicy();

        Assert.Equal(2000, policy.ThresholdMs);
        Assert.Equal(expected, policy.ShouldSeek(newest, target, endOfStream: false));
    }

    [Fact]
    public void ShouldSeek_NeverWithoutAKnownPositionOrAfterTheStreamEnded()
    {
        var policy = new ForwardSeekPolicy();

        Assert.False(policy.ShouldSeek(null, 60_000, endOfStream: false));
        Assert.False(policy.ShouldSeek(0, 60_000, endOfStream: true));
        Assert.False(policy.ShouldSeek(double.NaN, 60_000, endOfStream: false));
        Assert.False(policy.ShouldSeek(0, double.PositiveInfinity, endOfStream: false));
    }

    [Fact]
    public void Evaluate_KeyFrameAfterThePreviousPositionKeepsTheThreshold()
    {
        var policy = new ForwardSeekPolicy();

        Assert.Equal(ForwardSeekLanding.Advanced, policy.Evaluate(originMs: 33.3, targetMs: 9_999, landingMs: 9_000));
        Assert.Equal(ForwardSeekLanding.Advanced, policy.Evaluate(originMs: 33.3, targetMs: 9_000, landingMs: 9_000));
        Assert.Equal(2000, policy.ThresholdMs);
    }

    [Fact]
    public void Evaluate_LandingAtOrBeforeThePreviousPositionGrowsBeyondTheObservedKeyFrameDistance()
    {
        var policy = new ForwardSeekPolicy();

        // Key frames every 10 s: the seek from 33 ms to 2.5 s re-decoded from 0.
        Assert.Equal(ForwardSeekLanding.NoProgress, policy.Evaluate(originMs: 33.3, targetMs: 2_500, landingMs: 0));
        Assert.Equal(5_000, policy.ThresholdMs);
        Assert.False(policy.ShouldSeek(2_500, 7_499, endOfStream: false));
        Assert.True(policy.ShouldSeek(2_500, 7_500, endOfStream: false));

        // Landing exactly on the previous position (within timestamp rounding) saved nothing either.
        Assert.Equal(ForwardSeekLanding.NoProgress, policy.Evaluate(originMs: 2_500, targetMs: 7_500, landingMs: 2_500.005));
        Assert.Equal(10_000, policy.ThresholdMs, 3);
    }

    [Theory]
    [InlineData(9_999.02)]
    [InlineData(10_500)]
    [InlineData(double.NaN)]
    public void Evaluate_LandingAfterTheTargetOrNowhereDisablesForwardSeeking(double landing)
    {
        var policy = new ForwardSeekPolicy();

        Assert.Equal(ForwardSeekLanding.Overshot, policy.Evaluate(originMs: 33.3, targetMs: 9_999, landingMs: landing));
        Assert.Equal(double.PositiveInfinity, policy.ThresholdMs);
        Assert.False(policy.ShouldSeek(0, 1_000_000, endOfStream: false));
    }

    [Fact]
    public void Evaluate_TimestampRoundingAtTheTargetIsNotAnOvershoot()
    {
        var policy = new ForwardSeekPolicy();

        Assert.Equal(ForwardSeekLanding.Advanced, policy.Evaluate(originMs: 33.3, targetMs: 9_999.995, landingMs: 10_000));
    }

    [Fact]
    public void Disable_AndInfiniteThreshold_KeepEveryRequestSequential()
    {
        var disabled = new ForwardSeekPolicy();
        disabled.Disable();
        var sequential = new ForwardSeekPolicy(double.PositiveInfinity);

        Assert.False(disabled.ShouldSeek(0, 3_600_000, endOfStream: false));
        Assert.False(sequential.ShouldSeek(0, 3_600_000, endOfStream: false));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void Constructor_RejectsNonPositiveThresholds(double threshold) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ForwardSeekPolicy(threshold));
}
