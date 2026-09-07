using MyCapture.Core.Recording;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class TextLayerTimingTests
{
    [Theory]
    [InlineData(true, -500, 0, 800)]
    [InlineData(true, 900, 790, 800)]
    [InlineData(false, 0, 200, 210)]
    [InlineData(false, 5000, 200, 1000)]
    [InlineData(true, 350, 350, 800)]
    [InlineData(false, 650, 200, 650)]
    public void Resize_ClampsMovingEndpointAndPreservesOtherEndpoint(
        bool startHandle, double target, double expectedStart, double expectedEnd)
    {
        Assert.Equal((expectedStart, expectedEnd), TextLayerTiming.Resize(200, 800, 1000, startHandle, target));
    }

    [Fact]
    public void Resize_HandlesSubFrameClipAndRejectsNonFiniteInput()
    {
        Assert.Equal((0d, 5d), TextLayerTiming.Resize(0, 5, 5, true, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => TextLayerTiming.Resize(0, 100, 100, false, double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => TextLayerTiming.Resize(0, 100, 0, false, 50));
    }
}
