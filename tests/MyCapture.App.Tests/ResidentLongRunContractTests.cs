using MyCapture.App.Diagnostics;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class ResidentLongRunContractTests
{
    [Fact]
    public void ThousandSuccessfulActionsWithoutEightHoursAreIncomplete()
    {
        var report = ResidentLongRunContract.Evaluate(Samples(), 28_799.99);
        Assert.False(report.EvidenceValid);
        Assert.Null(report.BudgetPassed);
        Assert.Contains("DURATION_INCOMPLETE", report.Issues);
    }

    [Fact]
    public void MissingOrFailedActionsCannotBeRemovedFromTheMemoryWindow()
    {
        var samples = Samples();
        Assert.False(ResidentLongRunContract.Evaluate(samples[..999], 28_800).EvidenceValid);
        samples[32] = samples[32] with { OraclePassed = false };
        Assert.False(ResidentLongRunContract.Evaluate(samples, 28_800).EvidenceValid);
    }

    [Fact]
    public void CacheOccupancyMustMatchBeforeComparingPrivateBytes()
    {
        var samples = Samples();
        samples[950] = samples[950] with { State = samples[950].State with { ThumbnailBytes = 1024 } };
        var report = ResidentLongRunContract.Evaluate(samples, 28_800);
        Assert.False(report.EvidenceValid);
        Assert.Null(report.BudgetPassed);
        Assert.Contains("COMPARISON_STATE_DIFFERS", report.Issues);
    }

    [Theory]
    [InlineData(100, 32, true)]
    [InlineData(100, 33, false)]
    [InlineData(1024, 100, true)]
    [InlineData(1024, 103, false)]
    public void OriginalMemoryBudgetUsesTheLargerOfTenPercentAnd32MiB(int baselineMb, int increaseMb, bool passed)
    {
        var samples = Samples(baselineMb * 1024L * 1024);
        for (int i = 900; i < 1000; i++) samples[i] = samples[i] with
            { PrivateBytes = samples[i].PrivateBytes + increaseMb * 1024L * 1024 };
        var report = ResidentLongRunContract.Evaluate(samples, 28_800);
        Assert.True(report.EvidenceValid);
        Assert.Equal(passed, report.BudgetPassed);
    }

    [Fact]
    public void WrongOrderOrEarlyBurstCannotCompleteTheSchedule()
    {
        var samples = Samples();
        samples[10] = samples[10] with { StartedSeconds = 0 };
        Assert.False(ResidentLongRunContract.Evaluate(samples, 28_800).EvidenceValid);
        samples = Samples();
        samples[10] = samples[10] with { Kind = "recording" };
        Assert.False(ResidentLongRunContract.Evaluate(samples, 28_800).EvidenceValid);
    }

    [Fact]
    public void LongRunAcceptsOnlyUnmodifiedProductionOptions()
    {
        Assert.True(ResidentLongRunContract.FullWorkloadImplemented);
        if (Environment.Is64BitProcess)
            ResidentLongRunContract.ValidateOptions(
                [ResidentLongRunContract.FullScenario], false, false, new HashSet<string>(), false, false);
        else
            Assert.Throws<PlatformNotSupportedException>(() => ResidentLongRunContract.ValidateOptions(
                [ResidentLongRunContract.FullScenario], false, false, new HashSet<string>(), false, false));
        Assert.Throws<ArgumentException>(() => ResidentLongRunContract.ValidateOptions(
            [ResidentLongRunContract.PreflightScenario], false, false, new HashSet<string> { "no-idle-gc" }, false, false));
        Assert.Throws<ArgumentException>(() => ResidentLongRunContract.ValidateOptions(
            [ResidentLongRunContract.FullScenario], false, false, new HashSet<string>(), true, false));
    }

    private static ResidentLongRunContract.ActionSample[] Samples(long bytes = 100 * 1024 * 1024) =>
        Enumerable.Range(1, 1000).Select(index => new ResidentLongRunContract.ActionSample(index,
            ResidentLongRunContract.Kind(index), ResidentLongRunContract.ScheduledSeconds(index),
            ResidentLongRunContract.ScheduledSeconds(index) + 0.1, true, bytes,
            new("hidden", 1000, 12, 12 * 1024, 10, 1024, "ordinary-fixed-state", 0))).ToArray();

    [Theory]
    [InlineData(28800, true, 0, 0, true)]
    [InlineData(28799, true, 0, 0, false)]
    [InlineData(28800, false, 0, 0, false)]
    [InlineData(28800, true, 1, 0, false)]
    [InlineData(28800, true, 0, 1, false)]
    public void FullIntervalRequiresActualAwakeTimeAndNoSuspendResume(int awakeSeconds, bool clockSucceeded,
        int suspends, int resumes, bool accepted)
    {
        // Only the pure acceptance boundary is synthetic; no Windows power call or actual sleep.
        var sample = new ResidentLongRunContract.AwakeSample(clockSucceeded, true, 100,
            100 + (ulong)awakeSeconds * 10_000_000, awakeSeconds, suspends, resumes, 0, 0,
            1, DateTimeOffset.UnixEpoch);
        Assert.Equal(accepted, ResidentLongRunContract.IsAwakeIntervalValid(sample));
    }
}
