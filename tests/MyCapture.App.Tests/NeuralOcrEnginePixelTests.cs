using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.Ocr;
using SkiaSharp;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class NeuralOcrEnginePixelTests
{
    [Fact]
    public void ACaptureReachesSkiaOpaqueWithAlpha255WhateverItsFourthByte() => StaTestHost.Run(() =>
    {
        // Desktop captures leave the fourth byte undefined; Skia read zeros there as transparency.
        BitmapSource capture = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgr32, null,
            new byte[] { 10, 20, 30, 0, 40, 50, 60, 7 }, 8);
        capture.Freeze();

        using SKBitmap bitmap = Copy(capture, new Int32Rect(0, 0, 2, 1));

        Assert.Equal(SKAlphaType.Opaque, bitmap.AlphaType);
        Assert.Equal(new byte[] { 10, 20, 30, 255, 40, 50, 60, 255 }, bitmap.GetPixelSpan().ToArray());
    });

    [Fact]
    public void AnImageWithAlphaReachesSkiaPremultiplied() => StaTestHost.Run(() =>
    {
        BitmapSource image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { 200, 100, 50, 128 }, 4);
        image.Freeze();

        using SKBitmap bitmap = Copy(image, new Int32Rect(0, 0, 1, 1));

        Assert.Equal(SKAlphaType.Premul, bitmap.AlphaType);
        byte[] pixel = bitmap.GetPixelSpan().ToArray();
        Assert.InRange(pixel[0], 99, 101);
        Assert.InRange(pixel[1], 49, 51);
        Assert.Equal(128, pixel[3]);
    });

    [Fact]
    public void ARowCropCopiesOnlyItsRow() => StaTestHost.Run(() =>
    {
        BitmapSource image = BitmapSource.Create(1, 2, 96, 96, PixelFormats.Bgr24, null,
            new byte[] { 1, 2, 3, 0, 4, 5, 6, 0 }, 4);
        image.Freeze();

        using SKBitmap row = Copy(image, new Int32Rect(0, 1, 1, 1));

        Assert.Equal(new byte[] { 4, 5, 6, 255 }, row.GetPixelSpan().ToArray());
    });

    private static SKBitmap Copy(BitmapSource source, Int32Rect crop)
    {
        (BitmapSource pixels, SKAlphaType alpha) = NeuralOcrEngine.ForSkia(source);
        return NeuralOcrEngine.CopyToSkia(pixels, alpha, crop);
    }
}
