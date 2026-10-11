using MyCapture.Core.Recording;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class FilmstripLayoutTests
{
    [Fact]
    public void LandscapeSource_TilesAreAspectCorrectAndFillTheStrip()
    {
        // 16:9 at 54 tall is 96 wide; 1100 / 96 = 11.46 -> 12 tiles stretched to 91.67 each.
        var layout = FilmstripLayout.Create(10_000, 1920, 1080, 1100, 54, 30);

        Assert.Equal(96, layout.TileWidth, 3);
        Assert.Equal(12, layout.TileCount);
        Assert.Equal(1100.0 / 12, layout.SlotWidth, 6);
        Assert.Equal(0, layout.TileLeft(0));
        Assert.Equal(1100, layout.TileRight(11));
        for (int index = 1; index < layout.TileCount; index++)
        {
            Assert.Equal(layout.TileRight(index - 1), layout.TileLeft(index), 9);
        }
    }

    [Fact]
    public void SampleTimes_AreTileCentresAndNonDecreasing()
    {
        var layout = FilmstripLayout.Create(12_000, 1280, 720, 960, 54, 30);

        Assert.Equal(10, layout.TileCount);
        Assert.Equal(600, layout.SampleTimeMs(0), 6);
        Assert.Equal(1_800, layout.SampleTimeMs(1), 6);
        Assert.Equal(11_400, layout.SampleTimeMs(9), 6);
        for (int index = 1; index < layout.TileCount; index++)
        {
            Assert.True(layout.SampleTimeMs(index) >= layout.SampleTimeMs(index - 1));
        }
    }

    [Fact]
    public void SampleTimes_StayOneFrameBeforeTheEnd()
    {
        // Two tiles over a 200 ms clip: centres 50 and 150 ms. At 10 fps the last frame starts
        // at 100 ms, so the second tile is pulled back to it; at 30 fps (166.7 ms) it is kept.
        var tenFps = FilmstripLayout.Create(200, 100, 100, 100, 50, 10);
        Assert.Equal(2, tenFps.TileCount);
        Assert.Equal(50, tenFps.SampleTimeMs(0), 6);
        Assert.Equal(100, tenFps.LastSampleTimeMs, 6);
        Assert.Equal(100, tenFps.SampleTimeMs(1), 6);

        var thirtyFps = FilmstripLayout.Create(200, 100, 100, 100, 50, 30);
        Assert.Equal(200 - (1000.0 / 30), thirtyFps.LastSampleTimeMs, 6);
        Assert.Equal(150, thirtyFps.SampleTimeMs(1), 6);
    }

    [Fact]
    public void OneFrameClip_SamplesEveryTileAtZero()
    {
        var layout = FilmstripLayout.Create(1000.0 / 30, 640, 360, 500, 54, 30);

        Assert.Equal(0, layout.LastSampleTimeMs);
        for (int index = 0; index < layout.TileCount; index++)
        {
            Assert.Equal(0, layout.SampleTimeMs(index));
        }
    }

    [Fact]
    public void TinyStrip_StillHasOneTileThatFillsIt()
    {
        var layout = FilmstripLayout.Create(5_000, 1920, 1080, 1, 54, 30);

        Assert.Equal(1, layout.TileCount);
        Assert.Equal(0, layout.TileLeft(0));
        Assert.Equal(1, layout.TileRight(0));
        Assert.Equal(2_500, layout.SampleTimeMs(0), 6);
    }

    [Fact]
    public void HugeStrip_IsCappedAtFortyEightTilesThatStillFillIt()
    {
        var layout = FilmstripLayout.Create(60_000, 1920, 1080, 100_000, 54, 30);

        Assert.Equal(FilmstripLayout.MaxTileCount, layout.TileCount);
        Assert.Equal(100_000.0 / 48, layout.SlotWidth, 6);
        Assert.Equal(100_000, layout.TileRight(47));
        Assert.True(layout.SlotWidth > layout.TileWidth);
    }

    [Fact]
    public void PortraitSource_NarrowTilesAreClampedToTheMinimumWidth()
    {
        // 9:16 at 54 tall is 30.4 wide (kept); an extreme 1:10 strip is held at 24.
        var portrait = FilmstripLayout.Create(10_000, 1080, 1920, 600, 54, 30);
        Assert.Equal(54.0 * 1080 / 1920, portrait.TileWidth, 6);
        Assert.Equal(20, portrait.TileCount);

        var sliver = FilmstripLayout.Create(10_000, 100, 1000, 600, 54, 30);
        Assert.Equal(FilmstripLayout.MinTileWidth, sliver.TileWidth);
        Assert.Equal(25, sliver.TileCount);
    }

    [Fact]
    public void UltraWideSource_TileWidthIsClampedToTheMaximum()
    {
        var layout = FilmstripLayout.Create(10_000, 5000, 500, 800, 54, 30);

        Assert.Equal(FilmstripLayout.MaxTileWidth, layout.TileWidth);
        Assert.Equal(5, layout.TileCount);
    }

    [Theory]
    [InlineData(0, 1920, 1080, 800, 54, 30)]
    [InlineData(-1, 1920, 1080, 800, 54, 30)]
    [InlineData(double.NaN, 1920, 1080, 800, 54, 30)]
    [InlineData(double.PositiveInfinity, 1920, 1080, 800, 54, 30)]
    [InlineData(1000, 0, 1080, 800, 54, 30)]
    [InlineData(1000, 1920, -1, 800, 54, 30)]
    [InlineData(1000, 1920, 1080, 0, 54, 30)]
    [InlineData(1000, 1920, 1080, -800, 54, 30)]
    [InlineData(1000, 1920, 1080, 800, 0, 30)]
    [InlineData(1000, 1920, 1080, 800, double.NaN, 30)]
    [InlineData(1000, 1920, 1080, 800, 54, 0)]
    [InlineData(1000, 1920, 1080, 800, 54, -30)]
    public void ZeroOrNegativeInputs_AreRejected(
        double durationMs, int sourceWidth, int sourceHeight, double stripWidth, double tileHeight, int fps) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FilmstripLayout.Create(durationMs, sourceWidth, sourceHeight, stripWidth, tileHeight, fps));

    [Fact]
    public void TileIndexOutsideTheStrip_IsRejected()
    {
        var layout = FilmstripLayout.Create(1000, 640, 360, 300, 54, 30);

        Assert.Throws<ArgumentOutOfRangeException>(() => layout.SampleTimeMs(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => layout.TileLeft(layout.TileCount));
        Assert.Throws<ArgumentOutOfRangeException>(() => layout.TileRight(layout.TileCount));
    }

    [Fact]
    public void SamplesSameFramesAs_IgnoresStripWidthWhileTheCountHolds()
    {
        var narrow = FilmstripLayout.Create(10_000, 1920, 1080, 1100, 54, 30);
        var slightlyWider = FilmstripLayout.Create(10_000, 1920, 1080, 1140, 54, 30);
        var muchWider = FilmstripLayout.Create(10_000, 1920, 1080, 1300, 54, 30);
        var otherClip = FilmstripLayout.Create(12_000, 1920, 1080, 1100, 54, 30);

        Assert.Equal(narrow.TileCount, slightlyWider.TileCount);
        Assert.True(narrow.SamplesSameFramesAs(slightlyWider));
        Assert.False(narrow.SamplesSameFramesAs(muchWider));
        Assert.False(narrow.SamplesSameFramesAs(otherClip));
        Assert.False(narrow.SamplesSameFramesAs(null));
    }
}
