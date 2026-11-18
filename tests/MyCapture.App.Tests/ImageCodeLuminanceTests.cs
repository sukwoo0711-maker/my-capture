using System.Windows.Media.Imaging;
using MyCapture.Ocr;
using ZXing;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

/// <summary>The in-place luminance sources behind <see cref="ImageCodeDecoder"/> match ZXing's copying ones.</summary>
public sealed class ImageCodeLuminanceTests(ITestOutputHelper output)
{
    private const int Width = 37, Height = 23;

    private static byte[] Pixels(int seed)
    {
        var pixels = new byte[Width * Height];
        new Random(seed).NextBytes(pixels);
        return pixels;
    }

    private static byte[][] Rows(LuminanceSource source) =>
        Enumerable.Range(0, source.Height).Select(y => source.getRow(y, null)[..source.Width]).ToArray();

    [Fact]
    public void Source_ReadsTheArrayInPlace()
    {
        byte[] pixels = Pixels(1);
        var source = new ImageCodeDecoder.DeadlineLuminance(pixels, Width, Height, CancellationToken.None);
        Assert.Same(pixels, source.Matrix);
        Assert.Equal(Rows(new ReferenceLuminance(pixels, Width, Height)), Rows(source));
        // A preallocated row that is long enough is filled and returned, as ZXing's own sources do.
        byte[] row = new byte[Width + 5];
        Assert.Same(row, source.getRow(3, row));
    }

    [Fact]
    public void RotatedView_MatchesZXingCounterClockwiseRotation()
    {
        byte[] pixels = Pixels(2);
        LuminanceSource expected = new ReferenceLuminance(pixels, Width, Height).rotateCounterClockwise();
        LuminanceSource rotated = new ImageCodeDecoder.DeadlineLuminance(pixels, Width, Height, CancellationToken.None).rotateCounterClockwise();
        Assert.Equal((Height, Width), (rotated.Width, rotated.Height));
        Assert.Equal(Rows(expected), Rows(rotated));
        Assert.Equal(expected.Matrix[..(Width * Height)], rotated.Matrix[..(Width * Height)]);
    }

    [Fact]
    public void Crops_MatchZXingAndReuseOneBuffer()
    {
        byte[] pixels = Pixels(3);
        var reference = new ReferenceLuminance(pixels, Width, Height);
        var source = new ImageCodeDecoder.DeadlineLuminance(pixels, Width, Height, CancellationToken.None);
        LuminanceSource first = source.crop(4, 5, 20, 11);
        Assert.Equal(Rows(reference.crop(4, 5, 20, 11)), Rows(first));
        byte[] buffer = first.Matrix;
        LuminanceSource second = source.crop(0, 9, 13, 14);
        Assert.Equal(Rows(reference.crop(0, 9, 13, 14)), Rows(second));
        Assert.Same(buffer, second.Matrix);
        Assert.Throws<ArgumentException>(() => source.crop(30, 0, 8, 1));
    }

    [Fact]
    public void InPlaceInversion_MatchesZXingInvertedSourceAndRestores()
    {
        // Odd length exercises both the word loop and the byte tail; every byte value appears.
        byte[] pixels = Enumerable.Range(0, 256 * 3 + 5).Select(i => (byte)(i * 7)).ToArray();
        byte[] original = pixels.ToArray();
        LuminanceSource inverted = new ReferenceLuminance(original, pixels.Length, 1).invert();
        ImageCodeDecoder.Invert(pixels);
        Assert.Equal(inverted.Matrix[..pixels.Length], pixels);
        Assert.All(Enumerable.Range(0, pixels.Length), i => Assert.Equal(255 - original[i], pixels[i]));
        // The darkness tests in Confirmed/BarExtent use p < 128; on the negative they flip exactly.
        Assert.All(Enumerable.Range(0, pixels.Length), i => Assert.NotEqual(original[i] < 128, pixels[i] < 128));
        ImageCodeDecoder.Invert(pixels);
        Assert.Equal(original, pixels);
    }

    [Fact]
    public void Decode_AllocatesLittleMoreThanItsLuminanceImage() => Ocr410Fixture.RunSta(() =>
    {
        // 1920x1080 is sampled to 1600x900 (1,440,000 luminance bytes). With ZXing's copying sources
        // this decode allocated about 13.0 MB (copies per source, rotation, inversion and crop); in place it
        // is about 4.9 MB, mostly ZXing's own binarized matrices. The limit fails one more full copy.
        BitmapSource image = BarcodeFixture.Screenshot(1920, 1080, 6);
        Assert.Single(ImageCodeDecoder.Decode(image, CancellationToken.None));
        const long limit = 6_000_000;
        long[] windows = ThreadAllocation.Measure(() => _ = ImageCodeDecoder.Decode(image, CancellationToken.None), limit);
        output.WriteLine("Decode allocated " + ThreadAllocation.Describe(windows));
        Assert.True(ThreadAllocation.AnyWithin(windows, limit), "One decode allocated " + ThreadAllocation.Describe(windows));
    });

    private sealed class ReferenceLuminance(byte[] pixels, int width, int height) : BaseLuminanceSource(pixels, width, height)
    {
        protected override LuminanceSource CreateLuminanceSource(byte[] newLuminances, int newWidth, int newHeight) =>
            new ReferenceLuminance(newLuminances, newWidth, newHeight);
    }
}
