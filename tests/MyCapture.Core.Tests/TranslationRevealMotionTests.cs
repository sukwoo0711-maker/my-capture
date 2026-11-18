using MyCapture.Core.Pin;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class TranslationRevealMotionTests
{
    [Fact]
    public void TheSweepCrossesThePinOnceInHalfASecondWithEasedEnds()
    {
        Assert.Null(TranslationRevealMotion.Sweep(0, 0.44));
        Assert.Null(TranslationRevealMotion.Sweep(500, 0.44));
        Assert.Null(TranslationRevealMotion.Sweep(-1, 0.44));
        TranslationSweepFrame quarter = TranslationRevealMotion.Sweep(125, 0.44)!.Value;
        TranslationSweepFrame half = TranslationRevealMotion.Sweep(250, 0.44)!.Value;
        TranslationSweepFrame late = TranslationRevealMotion.Sweep(499, 0.44)!.Value;
        // Ease-in-out: a quarter of the time covers less than a quarter of the way; half the time, half the way.
        Assert.Equal(0.28, half.Width, 6);
        Assert.Equal(-0.28 + (1.28 * 0.5), half.Left, 6);
        Assert.True(quarter.Left < -0.28 + (1.28 * 0.25));
        Assert.True(late.Left > 0.99);
        Assert.Equal(0.44, half.Opacity);
    }

    [Theory]
    [InlineData(1.0, 0.44)]
    [InlineData(0.85, 0.44)]
    [InlineData(0.0, 0.11)]
    [InlineData(0.15, 0.11)]
    [InlineData(0.5, 0.275)]
    [InlineData(double.NaN, 0.11)]
    public void TheSweepFollowsThePictureBrightness(double luminance, double opacity) =>
        Assert.Equal(opacity, TranslationRevealMotion.SweepOpacity(luminance), 6);

    [Fact]
    public void UntranslatedTextDimsToAboutSevenTenthsOver120Milliseconds()
    {
        Assert.Equal(0, TranslationRevealMotion.PendingCoverAt(-1));
        Assert.Equal(0, TranslationRevealMotion.PendingCoverAt(0));
        Assert.Equal(0.3, TranslationRevealMotion.PendingCoverAt(120), 9);
        Assert.Equal(0.3, TranslationRevealMotion.PendingCoverAt(5000), 9);
        Assert.Equal(0.3 * (1 - Math.Pow(0.5, 3)), TranslationRevealMotion.PendingCoverAt(60), 9);
    }

    [Fact]
    public void ABlockChangesLeftToRightIn200MillisecondsWhileItsTextRises()
    {
        TranslationBlockFrame before = TranslationRevealMotion.Block(-5, 0.18);
        Assert.Equal((0d, 0d, 2d, false), (before.Progress, before.Boundary, before.Rise, before.Done));
        TranslationBlockFrame half = TranslationRevealMotion.Block(100, 0.18);
        Assert.Equal(0.5, half.Progress, 9);
        Assert.Equal(0.5 * 1.18, half.Boundary, 9);
        Assert.Equal(0.5 * 1.18 - 0.18, half.SolidEnd, 9);
        Assert.Equal(2 * Math.Pow(0.5, 3), half.Rise, 9);
        TranslationBlockFrame done = TranslationRevealMotion.Block(200, 0.07);
        Assert.True(done.Done);
        Assert.Equal(1.07, done.Boundary, 9);
        Assert.Equal(0, done.Rise);
    }

    [Theory]
    [InlineData(1, 100, 1000, 0.18)]
    [InlineData(2, 100, 1000, 0.07)]
    [InlineData(1, 601, 1000, 0.07)]
    [InlineData(1, 600, 1000, 0.18)]
    public void WideOrSeveralLineBlocksUseTheNarrowEdge(int lines, double width, double picture, double band) =>
        Assert.Equal(band, TranslationRevealMotion.BandFor(lines, width, picture));

    [Fact]
    public void TheOutlineGlowsOnceOver250Milliseconds()
    {
        Assert.Equal(0, TranslationRevealMotion.Glow(-1));
        Assert.Equal(0, TranslationRevealMotion.Glow(0), 9);
        Assert.Equal(1, TranslationRevealMotion.Glow(125), 9);
        Assert.Equal(0, TranslationRevealMotion.Glow(250));
    }

    [Theory]
    [InlineData(1, 45)]
    [InlineData(11, 45)]
    [InlineData(12, 500.0 / 12)]
    [InlineData(20, 25)]
    [InlineData(41, 12.195121951219512)]
    [InlineData(42, 12)]
    [InlineData(200, 12)]
    public void TheStaggerShrinksWithTheBacklogButNotBelow12Milliseconds(int backlog, double gap) =>
        Assert.Equal(gap, TranslationRevealMotion.StaggerGap(backlog), 9);

    [Fact]
    public void BlocksReadyTogetherStartInReadingOrderOneGapApart()
    {
        var schedule = new TranslationRevealSchedule();
        schedule.Ready([4, 1, 2], 1000);
        Assert.Equal([1000d, 1045d, 1090d], new[] { schedule.Starts[1], schedule.Starts[2], schedule.Starts[4] });
        Assert.Equal(1290, schedule.LastEnd);
    }

    [Fact]
    public void ALongBacklogStartsWithinHalfASecond()
    {
        var schedule = new TranslationRevealSchedule();
        schedule.Ready(Enumerable.Range(0, 60), 0);
        Assert.Equal(0, schedule.Starts[0]);
        Assert.Equal(12, schedule.Starts[1]);
        Assert.Equal(59 * 12, schedule.Starts[59]);
    }

    [Fact]
    public void BlocksReadyLaterQueueBehindTheStartedOnesAndTakeTheirPlaceInReadingOrder()
    {
        var schedule = new TranslationRevealSchedule();
        schedule.Ready([5, 6, 7], 0);
        // At 50 ms blocks 5 and 6 have started; 7 (planned at 90) waits with the new 2 and 9.
        schedule.Ready([9, 2], 50);
        Assert.Equal(0, schedule.Starts[5]);
        Assert.Equal(45, schedule.Starts[6]);
        Assert.Equal(90, schedule.Starts[2]);
        Assert.Equal(135, schedule.Starts[7]);
        Assert.Equal(180, schedule.Starts[9]);
        // A block handed over again keeps its plan.
        schedule.Ready([5], 300);
        Assert.Equal(0, schedule.Starts[5]);
    }

    [Fact]
    public void ABlockReadyAfterAQuietSpellStartsAtOnce()
    {
        var schedule = new TranslationRevealSchedule();
        schedule.Ready([0], 0);
        schedule.Ready([1], 1000);
        Assert.Equal(1000, schedule.Starts[1]);
    }
}
