using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.Platform.Imaging;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class ImageCodecFreshReadTests
{
    [Theory]
    [InlineData(8, 8)]
    [InlineData(16, 12)]
    public void ReloadAfterAtomicReplace_ReadsCurrentDimensionsAndPixels_WhilePreviousBitmapLives(int width, int height) =>
        StaTestHost.Run(() =>
        {
            string root = OwnedTestDirectory.Create("mc-image-fresh-");
            try
            {
                string path = Path.Combine(root, "rendered.png");
                BitmapSource first = Solid(8, 8, 0x21);
                ImageCodec.SavePng(first, path);
                BitmapSource retained = Assert.IsAssignableFrom<BitmapSource>(ImageCodec.TryLoad(path));
                BitmapSource replacement = Solid(width, height, 0xB7);
                ImageCodec.SavePng(replacement, path);

                // Establish the independent disk truth before asking the public loader.
                using (FileStream input = File.OpenRead(path))
                {
                    BitmapFrame onDisk = BitmapFrame.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    Assert.Equal(Pixels(replacement), Pixels(onDisk));
                }
                BitmapSource current = Assert.IsAssignableFrom<BitmapSource>(ImageCodec.TryLoad(path));
                Assert.Equal(width, current.PixelWidth);
                Assert.Equal(height, current.PixelHeight);
                Assert.Equal(Pixels(replacement), Pixels(current));
                Assert.Equal(Pixels(first), Pixels(retained));
                Assert.True(current.IsFrozen);
                using (FileStream unlocked = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    Assert.True(unlocked.Length > 0);
                GC.KeepAlive(retained); // The WPF URI-cache entry cannot disappear before this read.
            }
            finally { OwnedTestDirectory.Delete(root); }
        });

    [Fact]
    public void ReloadAfterFileBecomesCorrupt_ReturnsNull_DespiteRetainedOlderBitmap() => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-image-corrupt-");
        try
        {
            string path = Path.Combine(root, "rendered.png");
            ImageCodec.SavePng(Solid(8, 8, 0x41), path);
            BitmapSource retained = Assert.IsAssignableFrom<BitmapSource>(ImageCodec.TryLoad(path));
            File.WriteAllBytes(path, [1, 2, 3]);
            Assert.Null(ImageCodec.TryLoad(path));
            Assert.Equal(8, retained.PixelWidth);
            GC.KeepAlive(retained);
        }
        finally { OwnedTestDirectory.Delete(root); }
    });

    private static BitmapSource Solid(int width, int height, byte value)
    {
        byte[] pixels = Enumerable.Repeat(value, width * height * 4).ToArray();
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte[] Pixels(BitmapSource bitmap)
    {
        BitmapSource source = bitmap.Format == PixelFormats.Bgra32 ? bitmap
            : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        byte[] pixels = new byte[source.PixelWidth * source.PixelHeight * 4];
        source.CopyPixels(pixels, source.PixelWidth * 4, 0);
        return pixels;
    }
}
