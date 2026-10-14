using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.Platform.Imaging;

namespace MyCapture.App.Tests;

internal static class ExportPngFixture
{
    internal static byte[] Create(Guid id = default)
    {
        byte[] pixels = id.ToByteArray();
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        BitmapSource bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
        bitmap.Freeze();
        return ImageCodec.EncodePng(bitmap);
    }
}
