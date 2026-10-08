using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Diagnostics;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class CaptureBlankDetectionTests
{
    [Theory]
    [InlineData(768, 1366, 0)]
    [InlineData(768, 1366, 255)]
    [InlineData(1, 1, 127)]
    [InlineData(7, 3, 250)]
    public void UniformRgb_RemainsSuspect_EvenWithDifferentAlpha(int width, int height, byte value)
    {
        byte[] pixels = Pixels(width, height, value);
        for (int offset = 3; offset < pixels.Length; offset += 4)
            pixels[offset] = (byte)(offset % 256);
        Assert.True(CaptureSelfTest.IsUniformlyBlank(Bitmap(width, height, pixels)));
    }

    [Theory]
    [InlineData(1, 1, 0)]
    [InlineData(767, 1365, 1)]
    [InlineData(19, 106, 2)]
    public void SingleContentPixel_OutsideOldSampleGrid_IsNotBlank(int x, int y, int channel)
    {
        const int width = 768, height = 1366;
        byte[] pixels = Pixels(width, height, 250);
        pixels[(y * width + x) * 4 + channel] = 249;
        Assert.False(CaptureSelfTest.IsUniformlyBlank(Bitmap(width, height, pixels)));
    }

    [Fact]
    public void NonBgraInput_IsComparedByColor()
    {
        BitmapSource bitmap = BitmapSource.Create(3, 2, 96, 96, PixelFormats.Gray8, null,
            new byte[] { 255, 255, 255, 255, 255, 254 }, 3);
        bitmap.Freeze();
        Assert.False(CaptureSelfTest.IsUniformlyBlank(bitmap));
    }

    private static byte[] Pixels(int width, int height, byte value)
    {
        byte[] pixels = new byte[width * height * 4];
        Array.Fill(pixels, value);
        return pixels;
    }

    private static BitmapSource Bitmap(int width, int height, byte[] pixels)
    {
        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96,
            PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }
}
