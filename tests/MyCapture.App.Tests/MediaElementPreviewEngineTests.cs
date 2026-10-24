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
    // Where paused seeks stopped, before and after playback: up to a millisecond past, which a
    // seek again does not move.
    [InlineData(600.51, 600 + Tolerance)]
    [InlineData(600.856, 600 + Tolerance)]
    [InlineData(1235.462, 1234.5 + Tolerance)]
    [InlineData(1499.9, 1500 + Tolerance)]
    public void AReadAtTheElementPosition_HasLanded(double observed, double element) =>
        Assert.True(MediaElementPreviewEngine.HasLanded(observed, element));

    [Theory]
    // Seeks that stopped past their position after the stream ran to its end, under load.
    [InlineData(602.536, 600 + Tolerance)]
    [InlineData(603.01, 600 + Tolerance)]
    [InlineData(688.141, 600 + Tolerance)]
    [InlineData(1497.9, 1500 + Tolerance)]
    public void AReadAwayFromTheElementPosition_HasNotLanded(double observed, double element) =>
        Assert.False(MediaElementPreviewEngine.HasLanded(observed, element));

    private const double FrameMs = 1000.0 / 15;
    private const double ClipMs = 2533.267;

    [Theory]
    // Far from where the player stands and from its last position.
    [InlineData(1234.51, 600.01, 600.01, 1234.51)]
    // The same position again while the player stands there (Play reconciling the playhead).
    [InlineData(666.677, 667.2, 666.677, 666.677)]
    // Within the frame on screen, the player stopped past its last position.
    [InlineData(666.51, 666.977, 666.377, 666.51)]
    // The frame start again after it was shown from a position moved within the frame.
    [InlineData(666.677, 668.98, 668.677, 666.677)]
    // The clip end, asked for again.
    [InlineData(ClipMs, ClipMs, ClipMs, ClipMs)]
    // Nothing positioned yet, far from the player.
    [InlineData(1500.01, 0, double.NaN, 1500.01)]
    public void ASeekThePlayerActsOn_KeepsItsPosition(double position, double current, double last, double expected) =>
        Assert.Equal(expected, MediaElementPreviewEngine.SeparatedPositionMs(position, current, last, FrameMs, ClipMs), 6);

    [Theory]
    // The next frame from 0.3 ms before it: the player stands 0.3 ms away.
    [InlineData(666.677, 666.377, 666.377, 668.677)]
    // It stopped 0.6 ms past its last position, already past the start of the next frame.
    [InlineData(666.677, 666.977, 666.377, 670.677)]
    // Back across the boundary to 0.7 ms before the last position, whose frame the player kept.
    [InlineData(666.277, 667.665, 666.977, 664.277)]
    // Nothing positioned yet, the player standing just before the frame.
    [InlineData(666.677, 666.377, double.NaN, 668.677)]
    // A millisecond before the clip end, where playback stopped after starting a frame earlier.
    [InlineData(2532.267, ClipMs, 2466.0, 2530.267)]
    public void ASeekThePlayerWouldIgnore_MovesWithinTheFrame(double position, double current, double last, double expected)
    {
        double separated = MediaElementPreviewEngine.SeparatedPositionMs(position, current, last, FrameMs, ClipMs);
        Assert.Equal(expected, separated, 6);
        Assert.Equal(Math.Floor(position / FrameMs), Math.Floor(separated / FrameMs));
        Assert.True(Math.Abs(separated - current) >= MediaElementPreviewEngine.SeekResolutionMs);
    }

    [Fact]
    public void WithoutAFrameGridOrRoomInTheFrame_TheSeekKeepsItsPosition()
    {
        Assert.Equal(666.677, MediaElementPreviewEngine.SeparatedPositionMs(666.677, 666.377, 666.377, 0, ClipMs));
        // 300 fps: no position in the 3.3 ms frame is 2 ms from both.
        Assert.Equal(1.01, MediaElementPreviewEngine.SeparatedPositionMs(1.01, 1.5, 0.5, 1000.0 / 300, ClipMs));
    }
}
