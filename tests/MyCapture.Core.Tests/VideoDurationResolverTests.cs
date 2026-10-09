using MyCapture.Core.Recording;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class VideoDurationResolverTests
{
    [Theory]
    [InlineData(2133.267, 2000.0, 2300.0)] // WPF truncation and a longer wall clock are both ignored
    [InlineData(666.6, null, 816.0)]       // sub-second: WPF has no duration at all
    [InlineData(1133.267, 1000.0, 0.0)]
    public void ContainerDuration_IsAuthoritative(double container, double? player, double recorded) =>
        Assert.Equal(container, VideoDurationResolver.Resolve(container, player, recorded));

    [Theory]
    [InlineData(2000.0, 2133.267, 2133.267)] // recorded agrees with the whole-second player value
    [InlineData(2000.0, 2999.9, 2999.9)]
    [InlineData(2000.0, 3000.0, 2000.0)]     // outside [player, player + 1 s): contradicts the file
    [InlineData(2000.0, 1999.0, 2000.0)]
    [InlineData(2000.0, 0.0, 2000.0)]
    public void WithoutContainer_PlayerValueBoundsTheRecordedDuration(double player, double recorded, double expected) =>
        Assert.Equal(expected, VideoDurationResolver.Resolve(0, player, recorded));

    [Theory]
    [InlineData(double.NaN, 750.0)]
    [InlineData(-1.0, 750.0)]
    [InlineData(double.PositiveInfinity, 750.0)]
    public void WithoutContainerOrPlayer_FallsBackToRecorded(double container, double recorded) =>
        Assert.Equal(recorded, VideoDurationResolver.Resolve(container, null, recorded));

    [Fact]
    public void NothingKnown_ReturnsTheMinimumEditableLength() =>
        Assert.Equal(1, VideoDurationResolver.Resolve(0, null, double.NaN));
}
