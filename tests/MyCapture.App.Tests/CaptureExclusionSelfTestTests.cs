using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Diagnostics;
using MyCapture.Core.Primitives;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class CaptureExclusionSelfTestTests
{
    [Fact]
    public void PatternVerifierChecksAllQuadrantsAndFrameOrigin() => StaTestHost.Run(() =>
    {
        BitmapSource bitmap = Pattern();
        var bounds = new RectD(-200, 100, 80, 80);
        Assert.Equal(0, CaptureExclusionSelfTest.CountMismatches(bitmap, bounds, bounds, expectedCover: false));
        Assert.Equal(16, CaptureExclusionSelfTest.CountMismatches(bitmap, bounds, bounds, expectedCover: true));
        Assert.Equal(16, CaptureExclusionSelfTest.CountMismatches(bitmap, bounds,
            new RectD(1000, 1000, 80, 80), expectedCover: false));
    });

    [Fact]
    public void BlackOrOccludedDesktopCannotPass() => StaTestHost.Run(() =>
    {
        BitmapSource bitmap = BitmapSource.Create(80, 80, 96, 96, PixelFormats.Bgr32, null, new byte[80 * 80 * 4], 80 * 4);
        var bounds = new RectD(0, 0, 80, 80);
        Assert.Equal(16, CaptureExclusionSelfTest.CountMismatches(bitmap, bounds, bounds, expectedCover: false));
    });

    private static BitmapSource Pattern()
    {
        byte[] data = new byte[80 * 80 * 4];
        for (int y = 0; y < 80; y++)
        for (int x = 0; x < 80; x++)
        {
            Color color = CaptureExclusionSelfTest.PatternColors[(y / 40) * 2 + x / 40];
            int offset = (y * 80 + x) * 4;
            data[offset] = color.B; data[offset + 1] = color.G; data[offset + 2] = color.R; data[offset + 3] = 255;
        }
        return BitmapSource.Create(80, 80, 96, 96, PixelFormats.Bgra32, null, data, 80 * 4);
    }
}
