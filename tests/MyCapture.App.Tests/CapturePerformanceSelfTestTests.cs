using MyCapture.App.Diagnostics;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class CapturePerformanceSelfTestTests
{
    [Theory]
    [InlineData(3840, 2160, 3840, 2160)]
    [InlineData(4208, 2806, 3840, 2160)]
    // A single 1920×1080 monitor: Windows would cap a 4K top-level window at about 1942×1102.
    [InlineData(1920, 1080, 1920, 1080)]
    [InlineData(4208, 1440, 3840, 1440)]
    [InlineData(0, 0, 3840, 2160)]
    public void SyntheticSize_FitsTheVirtualScreenAndPrefers4K(int virtualWidth, int virtualHeight, int width, int height)
    {
        Assert.Equal((width, height), CapturePerformanceSelfTest.SyntheticSize(virtualWidth, virtualHeight));
    }
}
