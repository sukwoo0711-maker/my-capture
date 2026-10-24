using MyCapture.App.Recording;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Telling Windows Media Player's media-ended report from a seek's landing. In that state
/// MediaElement.Position reads the player's whole-second length for every seek.
/// </summary>
public sealed class MediaElementPreviewEngineTests
{
    private const double Tolerance = MediaElementPreviewEngine.PresentationToleranceMs;

    [Theory]
    // The reported self-test failure: 2533 ms clip, the player's length is 2000 ms.
    [InlineData(2000, 2499.933 + Tolerance, 2000)]
    [InlineData(2000, 1500 + Tolerance, 2000)]
    [InlineData(2000, 2533.267, 2000)]
    // A sub-second clip, whose whole-second length is zero and has no reported duration.
    [InlineData(0, 400 + Tolerance, 0)]
    public void AReadPinnedAtThePlayerLength_ForASeekElsewhere_IsTheEndedReport(
        double observed,
        double element,
        double playerLength) =>
        Assert.True(MediaElementPreviewEngine.IsMediaEndedReport(observed, element, playerLength));

    [Theory]
    // A seek still pending or landed reads the element position, including a 100 ns tick short.
    [InlineData(2499.933 + Tolerance, 2499.933 + Tolerance, 2000)]
    [InlineData(1500.0099, 1500 + Tolerance, 2000)]
    // A seek to the player's length itself legitimately reads it.
    [InlineData(2000, 2000, 2000)]
    [InlineData(2000.01, 2000 + Tolerance, 2000)]
    [InlineData(Tolerance, Tolerance, 0)]
    // The clip end past the player's length, as the Last command and playback end request it.
    [InlineData(2533.267, 2533.267, 2000)]
    public void AReadOfTheRequestedPosition_IsNotTheEndedReport(
        double observed,
        double element,
        double playerLength) =>
        Assert.False(MediaElementPreviewEngine.IsMediaEndedReport(observed, element, playerLength));

    [Theory]
    // The element read as positioned, and one 100 ns tick short of it.
    [InlineData(600 + Tolerance, 600 + Tolerance)]
    [InlineData(1500.0099, 1500 + Tolerance)]
    [InlineData(2533.267, 2533.267)]
    public void AReadAtTheElementPosition_HasLanded(double observed, double element) =>
        Assert.True(MediaElementPreviewEngine.HasLanded(observed, element));

    [Theory]
    // Seeks that stopped past their position after the stream ran to its end, under load.
    [InlineData(600.51, 600 + Tolerance)]
    [InlineData(602.536, 600 + Tolerance)]
    [InlineData(688.141, 600 + Tolerance)]
    [InlineData(1499.9, 1500 + Tolerance)]
    public void AReadAwayFromTheElementPosition_HasNotLanded(double observed, double element) =>
        Assert.False(MediaElementPreviewEngine.HasLanded(observed, element));
}
