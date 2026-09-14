using System;
using System.IO;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.Platform.Imaging;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Regression guard for a real-device defect found by the recording/OCR self-tests:
/// <see cref="ImageCodec.TryLoad"/> threw an unhandled <see cref="UriFormatException"/> when
/// given a RELATIVE path (which passes <c>File.Exists</c> resolved against the CWD but is not
/// a valid absolute URI), breaking the method's documented null-on-failure contract and
/// crashing the OCR path. It must return a bitmap for a relative path that exists, and null
/// (never throw) for a bad one.
/// </summary>
public sealed class ImageCodecPathTests
{
    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException($"STA body threw: {failure}");
        }
    }

    private static string WriteTempPng(string dir, string name, int w = 8, int h = 8)
    {
        Directory.CreateDirectory(dir);
        var bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        byte[] px = new byte[w * h * 4];
        Array.Fill(px, (byte)0x7F);
        bmp.WritePixels(new System.Windows.Int32Rect(0, 0, w, h), px, w * 4, 0);
        bmp.Freeze();
        string path = Path.Combine(dir, name);
        ImageCodec.SavePng(bmp, path);
        return path;
    }

    [Fact]
    public void TryLoad_RelativePath_LoadsInsteadOfThrowing() => RunSta(() =>
    {
        string dir = Path.Combine(Path.GetTempPath(), "mc-imgcodec-" + Guid.NewGuid().ToString("N"));
        string abs = WriteTempPng(dir, "img.png");
        string originalCwd = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(dir);
            // A bare relative filename: previously threw UriFormatException.
            BitmapSource? loaded = ImageCodec.TryLoad("img.png");
            Assert.NotNull(loaded);
            Assert.Equal(8, loaded!.PixelWidth);

            BitmapSource? scaled = ImageCodec.TryLoadScaled("img.png", 4);
            Assert.NotNull(scaled);
        }
        finally
        {
            Directory.SetCurrentDirectory(originalCwd);
            try { Directory.Delete(dir, true); } catch (IOException) { }
            _ = abs;
        }
    });

    [Fact]
    public void StretchContrast_SpreadsFaintGreyPrintTowardBlackAndWhite() => RunSta(() =>
    {
        const int width = 8;
        const int height = 2;
        var source = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        byte[] pixels = new byte[width * height * 4];
        for (int x = 0; x < width; x++)
        {
            WritePixel(pixels, x, 0, width, 110);
            WritePixel(pixels, x, 1, width, 170);
        }

        source.WritePixels(new System.Windows.Int32Rect(0, 0, width, height), pixels, width * 4, 0);
        source.Freeze();

        BitmapSource stretched = ImageCodec.StretchContrastForRecognition(source);
        Assert.True(stretched.IsFrozen);
        byte[] result = new byte[pixels.Length];
        stretched.CopyPixels(result, width * 4, 0);
        Assert.True(result[0] < 40, "Dark paper print should move toward black.");
        Assert.True(result[(width * 4) + 0] > 210, "Light paper should move toward white.");
    });

    private static void WritePixel(byte[] pixels, int x, int y, int width, byte gray)
    {
        int offset = ((y * width) + x) * 4;
        pixels[offset] = gray;
        pixels[offset + 1] = gray;
        pixels[offset + 2] = gray;
        pixels[offset + 3] = 255;
    }

    [Fact]
    public void TryLoad_MissingOrBadPath_ReturnsNull_NeverThrows() => RunSta(() =>
    {
        Assert.Null(ImageCodec.TryLoad("does-not-exist-\u0001.png"));
        Assert.Null(ImageCodec.TryLoad(""));
        Assert.Null(ImageCodec.TryLoad("   "));
        Assert.Null(ImageCodec.TryLoadScaled("nope-missing.png", 100));
    });

    [Theory]
    [InlineData(16, 320, 320, 16, 320)]
    [InlineData(40, 800, 320, 16, 320)]
    [InlineData(800, 40, 320, 320, 16)]
    [InlineData(8, 8, 320, 8, 8)]
    public void ScaledLoad_BoundsLongEdgeWithoutUpscaling(int width, int height, int bound,
        int expectedWidth, int expectedHeight) => RunSta(() =>
    {
        string dir = OwnedTestDirectory.Create("mc-scaled-");
        try
        {
            string path = WriteTempPng(dir, "portrait.png", width, height);
            BitmapSource loaded = Assert.IsAssignableFrom<BitmapSource>(ImageCodec.TryLoadScaled(path, bound));
            Assert.Equal(expectedWidth, loaded.PixelWidth);
            Assert.Equal(expectedHeight, loaded.PixelHeight);
            Assert.True(loaded.IsFrozen);
            // Exclusive reopen proves that the decoder released its input handle.
            using var unlocked = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
        }
        finally { OwnedTestDirectory.Delete(dir); }
    });
}
