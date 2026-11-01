using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class CropPixelSizingTests
{
    [Fact]
    public void FullRangeInteriorWaitsForMovementThenStartsNewRange()
    {
        var crop = new CropSelection(640, 360);
        crop.Begin(new PointD(100, 80), 7, 4, 4);
        Assert.Equal(CropGrip.New, crop.Grip);
        crop.Drag(new PointD(102, 82), false);
        Assert.Equal(crop.Bounds, crop.Region);
        crop.Drag(new PointD(200, 160), false);
        crop.End();
        Assert.Equal(new RectD(100, 80, 100, 80), crop.Region);
        Assert.Equal(CropGrip.Move, crop.HitTest(new PointD(140, 120), 7));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TypedOddSizeSnapsOriginAndKeepsExactOutputWithRatio(bool width)
    {
        var crop = new CropSelection(640, 360);
        crop.SetRatio(16.0 / 9);
        if (width) crop.SetWidth(101); else crop.SetHeight(101);
        Assert.Equal(16.0 / 9, crop.Region.Width / crop.Region.Height, 10);
        Assert.Equal(Math.Floor(crop.Region.X), crop.Region.X);
        Assert.Equal(Math.Floor(crop.Region.Y), crop.Region.Y);
        Assert.Equal(101, width ? crop.Region.ToPixelBounds().Width : crop.Region.ToPixelBounds().Height);
        Assert.True(crop.Region.Right <= crop.Bounds.Right && crop.Region.Bottom <= crop.Bounds.Bottom);
    }
}
