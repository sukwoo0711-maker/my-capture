using MyCapture.Core.Capture;
using MyCapture.Core.Primitives;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class FreeformMaskTests
{
    private static readonly RectD Desktop = new(0, 0, 80, 60);

    [Fact]
    public void ConvexOutlineClosesToItsStartAndBoundsUsePhysicalPixels()
    {
        FreeformMask mask = Mask([new(10, 12), new(30, 12), new(10, 32)]);
        Assert.Equal(new RectD(10, 12, 20, 20), mask.Bounds);
        Assert.Equal(20, mask.Width);
        Assert.Equal(20, mask.Height);
        Assert.Equal(400, mask.Coverage.Length);
        Assert.Equal(255, At(mask, 12, 14));
        Assert.Equal(0, At(mask, 28, 30));
    }

    [Fact]
    public void ConcaveOutlineLeavesItsNotchTransparent()
    {
        FreeformMask mask = Mask([new(4, 4), new(28, 4), new(28, 12),
            new(12, 12), new(12, 28), new(4, 28)]);
        Assert.Equal(new RectD(4, 4, 24, 24), mask.Bounds);
        Assert.Equal(255, At(mask, 20, 8));
        Assert.Equal(255, At(mask, 8, 20));
        Assert.Equal(0, At(mask, 20, 20));
    }

    [Fact]
    public void SelfCrossingBowTieHasTwoFilledLobesDespiteZeroSignedArea()
    {
        FreeformMask mask = Mask([new(4, 4), new(28, 28), new(4, 28), new(28, 4)]);
        Assert.Equal(255, At(mask, 15, 6));
        Assert.Equal(255, At(mask, 15, 25));
        Assert.Equal(0, At(mask, 5, 15));
        Assert.Equal(0, At(mask, 26, 15));
    }

    [Fact]
    public void TwiceWoundOutlineUsesNonZeroRatherThanEvenOddFill()
    {
        PointD[] rectangle = [new(4, 4), new(28, 4), new(28, 28), new(4, 28)];
        FreeformMask once = Mask(rectangle);
        FreeformMask twice = Mask([.. rectangle, .. rectangle]);
        Assert.Equal(once.Bounds, twice.Bounds);
        Assert.Equal(once.Coverage, twice.Coverage);
        Assert.All(twice.Coverage, alpha => Assert.Equal(255, alpha));
    }

    [Fact]
    public void OppositeRetracingCancelsItsWindingAndCapturesNothing()
    {
        Assert.Null(FreeformMask.Create([new(4, 4), new(28, 4), new(28, 28), new(4, 28),
            new(4, 4), new(4, 28), new(28, 28), new(28, 4)], Desktop));
    }

    [Fact]
    public void ClippingIntersectsSegmentsInsteadOfClampingTheirEndpoints()
    {
        // The diagonal from (-20, 0) to (20, 40) enters x=0 at y=20. Clamping
        // its endpoint to (0, 0) would wrongly include (5, 15).
        FreeformMask mask = Mask([new(-20, 0), new(20, 40), new(-20, 40)]);
        Assert.Equal(new RectD(0, 20, 20, 20), mask.Bounds);
        Assert.Equal(0, At(mask, 15, 22));
        Assert.Equal(255, At(mask, 2, 35));
        Assert.Equal(128, At(mask, 10, 30));
    }

    [Fact]
    public void AnOutlineSurroundingTheDesktopClipsToAllFourDesktopEdges()
    {
        FreeformMask mask = Mask([new(-100, -100), new(200, -100), new(200, 200), new(-100, 200)]);
        Assert.Equal(Desktop, mask.Bounds);
        Assert.All(mask.Coverage, alpha => Assert.Equal(255, alpha));
    }

    [Fact]
    public void ClippingAndBoundsAlsoWorkAtANegativeDesktopOrigin()
    {
        RectD clip = new(-60, -40, 80, 60);
        FreeformMask mask = Assert.IsType<FreeformMask>(FreeformMask.Create(
            [new(-80, -50), new(-20, -50), new(-20, 10), new(-80, 10)], clip));
        Assert.Equal(new RectD(-60, -40, 40, 50), mask.Bounds);
        Assert.All(mask.Coverage, alpha => Assert.Equal(255, alpha));
    }

    [Theory]
    [InlineData(7, 20)]
    [InlineData(20, 7)]
    [InlineData(7.99, 20)]
    [InlineData(20, 7.99)]
    public void EitherPhysicalDimensionBelowEightRejectsTheStroke(double width, double height)
    {
        Assert.Null(FreeformMask.Create(Rectangle(0.25, 0.25, width, height), Desktop));
    }

    [Fact]
    public void ExactlyEightByEightIsAcceptedAndClippingCanMakeAStrokeTooSmall()
    {
        FreeformMask mask = Mask(Rectangle(2, 3, 8, 8));
        Assert.Equal(new RectD(2, 3, 8, 8), mask.Bounds);
        Assert.All(mask.Coverage, alpha => Assert.Equal(255, alpha));
        Assert.Null(FreeformMask.Create(Rectangle(-20, 10, 27, 20), Desktop));
    }

    [Fact]
    public void CollinearRepeatedAndFullyClippedOutlinesHaveNoFilledArea()
    {
        Assert.Null(FreeformMask.Create([new(1, 1), new(15, 15), new(30, 30)], Desktop));
        Assert.Null(FreeformMask.Create([new(1, 1), new(1, 1), new(1, 1)], Desktop));
        Assert.Null(FreeformMask.Create(Rectangle(90, 90, 20, 20), Desktop));
        Assert.Null(FreeformMask.Create([], Desktop));
        Assert.Null(FreeformMask.Create([new(1, 1), new(20, 20)], Desktop));
    }

    [Fact]
    public void InvalidCoordinatesProduceNoSelection()
    {
        Assert.Null(FreeformMask.Create([new(0, 0), new(20, 0), new(double.NaN, 20)], Desktop));
        Assert.Null(FreeformMask.Create(Rectangle(0, 0, 20, 20), new RectD(0, 0, double.PositiveInfinity, 60)));
        Assert.Null(FreeformMask.Create(Rectangle(0, 0, 20, 20), RectD.Empty));
    }

    [Fact]
    public void DiagonalAndSubpixelEdgesHaveCoverageAlpha()
    {
        FreeformMask diagonal = Mask([new(0, 0), new(16, 0), new(0, 16)]);
        for (int y = 0; y < 16; y++)
            Assert.Equal(128, At(diagonal, 15 - y, y));
        FreeformMask fractional = Mask(Rectangle(0.25, 0, 16, 16));
        Assert.Equal(new RectD(0, 0, 17, 16), fractional.Bounds);
        Assert.Equal(191, At(fractional, 0, 8));
        Assert.Equal(64, At(fractional, 16, 8));
        Assert.Equal(255, At(fractional, 8, 8));
    }

    [Fact]
    public void ReversingDirectionPreservesTheMask()
    {
        PointD[] stroke = [new(-20, 0), new(40, 50), new(25, 4), new(4, 40)];
        FreeformMask clockwise = Mask(stroke);
        FreeformMask counterclockwise = Mask(stroke.Reverse().ToArray());
        Assert.Equal(clockwise.Bounds, counterclockwise.Bounds);
        Assert.Equal(clockwise.Coverage, counterclockwise.Coverage);
    }

    [Fact]
    public void ApplyingMaskKeepsInteriorExactAndEdgeColoursStraightAndClearsOutside()
    {
        FreeformMask mask = Mask([new(0, 0), new(16, 0), new(0, 16)]);
        int stride = (mask.Width * 4) + 8;
        byte[] source = new byte[stride * mask.Height];
        for (int i = 0; i < source.Length; i++) source[i] = (byte)((i * 37) % 251);
        for (int row = 0; row < mask.Height; row++)
            for (int column = 0; column < mask.Width; column++)
                source[(row * stride) + (column * 4) + 3] = 255;
        byte[] output = (byte[])source.Clone();
        mask.ApplyToBgra32(output, stride);
        for (int row = 0; row < mask.Height; row++)
        {
            for (int column = 0; column < mask.Width; column++)
            {
                int p = (row * stride) + (column * 4);
                byte alpha = mask.Coverage[(row * mask.Width) + column];
                Assert.Equal(alpha, output[p + 3]);
                if (alpha == 0) Assert.Equal(new byte[4], output[p..(p + 4)]);
                else Assert.Equal(source[p..(p + 3)], output[p..(p + 3)]);
                if (alpha == 255) Assert.Equal(source[p..(p + 4)], output[p..(p + 4)]);
            }
            int padding = (row * stride) + (mask.Width * 4);
            Assert.Equal(source[padding..(padding + 8)], output[padding..(padding + 8)]);
        }
    }

    [Fact]
    public void ApplyingMaskMultipliesExistingAlphaAndValidatesBufferBounds()
    {
        FreeformMask mask = Mask([new(0, 0), new(16, 0), new(0, 16)]);
        byte[] pixels = Enumerable.Repeat((byte)80, 16 * 16 * 4).ToArray();
        mask.ApplyToBgra32(pixels, 64);
        Assert.Equal(80, pixels[3]);
        Assert.Equal(40, pixels[(15 * 4) + 3]);
        Assert.Equal(80, pixels[15 * 4]);
        Assert.Throws<ArgumentOutOfRangeException>(() => mask.ApplyToBgra32(pixels, 63));
        Assert.Throws<ArgumentException>(() => mask.ApplyToBgra32(new byte[20], 64));
    }

    private static FreeformMask Mask(IReadOnlyList<PointD> points) =>
        Assert.IsType<FreeformMask>(FreeformMask.Create(points, Desktop));

    private static PointD[] Rectangle(double x, double y, double width, double height) =>
        [new(x, y), new(x + width, y), new(x + width, y + height), new(x, y + height)];

    private static byte At(FreeformMask mask, int x, int y) =>
        mask.Coverage[((y - (int)mask.Bounds.Top) * mask.Width) + x - (int)mask.Bounds.Left];
}
