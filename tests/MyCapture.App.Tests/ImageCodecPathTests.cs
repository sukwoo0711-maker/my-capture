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
    public void TryLoad_MissingOrBadPath_ReturnsNull_NeverThrows() => RunSta(() =>
    {
        Assert.Null(ImageCodec.TryLoad("does-not-exist-\u0001.png"));
        Assert.Null(ImageCodec.TryLoad(""));
        Assert.Null(ImageCodec.TryLoad("   "));
        Assert.Null(ImageCodec.TryLoadScaled("nope-missing.png", 100));
    });
}
