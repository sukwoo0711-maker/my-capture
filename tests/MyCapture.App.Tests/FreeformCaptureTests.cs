using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.Core.Capture;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class FreeformCaptureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CropUsesFrozenPhysicalPixelsAndReturnsDetachedStraightAlpha(bool nativeBgrFrame)
    {
        const int width = 40, height = 32;
        byte[] original = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int p = ((y * width) + x) * 4;
                original[p] = (byte)(x * 5);
                original[p + 1] = (byte)(y * 7);
                original[p + 2] = (byte)(x + y + 60);
                original[p + 3] = nativeBgrFrame ? (byte)0 : (byte)255;
            }
        }
        BitmapSource bitmap = BitmapSource.Create(width, height, 144, 144,
            nativeBgrFrame ? PixelFormats.Bgr32 : PixelFormats.Bgra32, null, original, width * 4);
        bitmap.Freeze();
        var frame = new FrozenFrame(bitmap, new RectD(-1200, -300, width, height), null, 0);
        FreeformMask mask = Assert.IsType<FreeformMask>(FreeformMask.Create(
            [new(6, 4), new(26, 4), new(6, 24)], new RectD(0, 0, width, height)));

        BitmapSource crop = FreeformCapture.Crop(frame, mask);

        Assert.True(crop.IsFrozen);
        Assert.IsNotType<CroppedBitmap>(crop);
        Assert.Equal(PixelFormats.Bgra32, crop.Format);
        Assert.Equal(20, crop.PixelWidth);
        Assert.Equal(20, crop.PixelHeight);
        Assert.Equal(144, crop.DpiX);
        Assert.Equal(144, crop.DpiY);
        byte[] actual = new byte[20 * 20 * 4];
        crop.CopyPixels(actual, 80, 0);
        for (int y = 0; y < 20; y++)
        {
            for (int x = 0; x < 20; x++)
            {
                int p = ((y * 20) + x) * 4;
                int source = (((y + 4) * width) + x + 6) * 4;
                byte alpha = mask.Coverage[(y * 20) + x];
                Assert.Equal(alpha, actual[p + 3]);
                if (alpha == 0) Assert.Equal(new byte[4], actual[p..(p + 4)]);
                else Assert.Equal(original[source..(source + 3)], actual[p..(p + 3)]);
            }
        }
        byte[] unchanged = new byte[original.Length];
        bitmap.CopyPixels(unchanged, width * 4, 0);
        Assert.Equal(original, unchanged);
    }

    [Fact]
    public void CropRejectsAMaskOutsideTheFrameRatherThanMovingTheSelection()
    {
        BitmapSource bitmap = BitmapSource.Create(10, 10, 96, 96, PixelFormats.Bgra32, null, new byte[400], 40);
        bitmap.Freeze();
        var frame = new FrozenFrame(bitmap, new RectD(0, 0, 10, 10), null, 0);
        FreeformMask mask = Assert.IsType<FreeformMask>(FreeformMask.Create(
            [new(5, 5), new(15, 5), new(15, 15), new(5, 15)], new RectD(0, 0, 20, 20)));
        Assert.Throws<ArgumentOutOfRangeException>(() => FreeformCapture.Crop(frame, mask));
    }
}
