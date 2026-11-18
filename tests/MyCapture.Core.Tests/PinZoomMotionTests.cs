using MyCapture.Core.Pin;
using Xunit;

namespace MyCapture.Core.Tests;

/// <summary>The zoom a pin shows while it moves to its new zoom, on an injected clock.</summary>
public sealed class PinZoomMotionTests
{
    private const double Duration = 167;
    private const double Lead = 8;

    [Fact]
    public void Retarget_ShowsTheFirstStepInTheInputTurn()
    {
        var motion = new PinZoomMotion(Duration, Lead);

        double first = motion.Retarget(1, 1.1, nowMs: 1000);

        Assert.True(motion.IsActive);
        double expected = Math.Exp(Math.Log(1.1) * PinZoomMotion.Ease(Lead / Duration));
        Assert.Equal(expected, first, 12);
        Assert.True(first > 1 && first < 1.1);
    }

    [Fact]
    public void Sample_ReachesTheTargetExactlyAtTheDurationAndStops()
    {
        var motion = new PinZoomMotion(Duration, Lead);
        motion.Retarget(1, 1.1, nowMs: 0);

        double before = motion.Sample(Duration - Lead - 0.5);
        Assert.True(motion.IsActive);
        Assert.True(before < 1.1);

        double end = motion.Sample(Duration - Lead);
        Assert.False(motion.IsActive);
        Assert.Equal(1.1, end, 12);
        Assert.Equal(1.1, motion.Sample(10_000), 12);
    }

    [Fact]
    public void Sample_RisesMonotonicallyWithoutPassingTheTarget()
    {
        var motion = new PinZoomMotion(Duration, Lead);
        double previous = motion.Retarget(0.5, 2, nowMs: 0);
        for (double t = 1; t <= Duration; t += 1)
        {
            double value = motion.Sample(t);
            Assert.True(value >= previous, $"at {t} ms {value} < {previous}");
            Assert.True(value <= 2 + 1e-12);
            previous = value;
        }
        Assert.Equal(2, previous, 12);
    }

    [Fact]
    public void Sample_InterpolatesTheLogarithmOfTheZoom()
    {
        var motion = new PinZoomMotion(Duration, Lead);
        motion.Retarget(1, 4, nowMs: 0);
        // The eased progress reaches one half at p = 1 - cbrt(0.5); the zoom there is the geometric middle.
        double half = (1 - Math.Cbrt(0.5)) * Duration - Lead;

        Assert.Equal(2, motion.Sample(half), 9);
    }

    [Fact]
    public void Retarget_ContinuesFromTheShownZoomAndEndsOneDurationAfterTheLastInput()
    {
        var motion = new PinZoomMotion(Duration, Lead);
        double shown = motion.Retarget(1, 1.1, nowMs: 0);
        var seen = new List<double> { 1, shown };
        double target = 1.1;
        for (int notch = 1; notch < 5; notch++)
        {
            for (double t = (notch - 1) * 30 + 5; t < notch * 30; t += 5)
            {
                shown = motion.Sample(t);
                seen.Add(shown);
            }
            target *= 1.1;
            shown = motion.Retarget(shown, target, notch * 30);
            seen.Add(shown);
        }
        double last = 4 * 30;
        for (double t = last + 4; motion.IsActive; t += 4)
        {
            Assert.True(t <= last + Duration - Lead + 4, "the motion outlived the last notch by more than its duration");
            seen.Add(motion.Sample(t));
        }

        for (int i = 1; i < seen.Count; i++) Assert.True(seen[i] >= seen[i - 1], $"step {i}: {seen[i]} < {seen[i - 1]}");
        Assert.Equal(target, seen[^1], 12);
    }

    [Fact]
    public void Retarget_BackwardsTurnsAroundFromTheShownZoom()
    {
        var motion = new PinZoomMotion(Duration, Lead);
        motion.Retarget(1, 1.1, nowMs: 0);
        double shown = motion.Sample(40);

        double next = motion.Retarget(shown, 1, nowMs: 40);

        Assert.True(next < shown && next > 1);
        Assert.Equal(1, motion.Sample(40 + Duration), 12);
        Assert.False(motion.IsActive);
    }

    [Fact]
    public void Retarget_ToTheShownZoomEndsTheMotion()
    {
        var motion = new PinZoomMotion(Duration, Lead);
        motion.Retarget(1, 1.1, nowMs: 0);

        Assert.Equal(1.05, motion.Retarget(1.05, 1.05, nowMs: 10), 12);
        Assert.False(motion.IsActive);
    }

    [Fact]
    public void Finish_JumpsToTheTarget()
    {
        var motion = new PinZoomMotion(Duration, Lead);
        motion.Retarget(2, 1, nowMs: 0);

        Assert.Equal(1, motion.Finish(), 12);
        Assert.False(motion.IsActive);
        Assert.Equal(1, motion.Sample(1), 12);
    }

    [Fact]
    public void Ease_StartsFastAndLandsWithoutOvershoot()
    {
        Assert.Equal(0, PinZoomMotion.Ease(0), 12);
        Assert.Equal(1, PinZoomMotion.Ease(1), 12);
        Assert.Equal(1, PinZoomMotion.Ease(2), 12);
        Assert.True(PinZoomMotion.Ease(0.25) > 0.5);
        double previous = 0;
        for (double p = 0.01; p <= 1; p += 0.01)
        {
            double eased = PinZoomMotion.Ease(p);
            Assert.True(eased > previous && eased <= 1);
            previous = eased;
        }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 0)]
    [InlineData(double.NaN, 0)]
    [InlineData(100, 100)]
    [InlineData(100, -1)]
    public void Constructor_RejectsAnUnusableTiming(double duration, double lead) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new PinZoomMotion(duration, lead));

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(double.NaN, 1)]
    [InlineData(1, double.PositiveInfinity)]
    public void Retarget_RejectsAnUnusableZoom(double shown, double target) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new PinZoomMotion(Duration, Lead).Retarget(shown, target, 0));
}
