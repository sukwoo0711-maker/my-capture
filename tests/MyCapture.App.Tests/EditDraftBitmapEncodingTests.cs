using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Editing;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class EditDraftBitmapEncodingTests
{
    [Theory]
    [InlineData("Bgra32")]
    [InlineData("Pbgra32")]
    [InlineData("Bgr24")]
    [InlineData("Bgr32")]
    [InlineData("Indexed1")]
    [InlineData("Indexed8")]
    public void IndependentPixelsPreservePreviousPngFormatAlphaPaletteAndDpi(string format) => StaTestHost.Run(() =>
    {
        BitmapSource source = Source(format);
        byte[] previous = EncodePrevious(source);
        byte[] current = AnnotationEditorControl.EncodeDraftBitmap(source);
        Assert.Equal(previous, current);
        AssertDecodedEqual(previous, current);
    });

    [Fact]
    public void FrameMetadataAndPreviousColorContextContractArePreserved() => StaTestHost.Run(() =>
    {
        BitmapSource pixels = Source("Bgra32");
        var metadata = new BitmapMetadata("png");
        metadata.SetQuery("/tEXt/{str=Description}", "Owned synthetic recovery draft");
        var contexts = new ReadOnlyCollection<ColorContext>([new ColorContext(PixelFormats.Bgra32)]);
        BitmapFrame source = BitmapFrame.Create(pixels, null, metadata, contexts);
        source.Freeze();
        byte[] previous = EncodePrevious(source);
        byte[] current = AnnotationEditorControl.EncodeDraftBitmap(source);
        Assert.Equal(previous, current);
        AssertDecodedEqual(previous, current);
        using var stream = new MemoryStream(current);
        BitmapFrame decoded = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        Assert.Equal("Owned synthetic recovery draft", ((BitmapMetadata)decoded.Metadata).GetQuery("/tEXt/{str=Description}"));
    });

    [Fact]
    public void FrozenPixelsKeepOneEncodedBufferAcrossRevisions() => StaTestHost.Run(() =>
    {
        BitmapSource source = Source("Bgra32");
        byte[] first = AnnotationEditorControl.EncodeCachedDraftBitmap(source);
        byte[] second = AnnotationEditorControl.EncodeCachedDraftBitmap(source);
        Assert.Same(first, second);
        Assert.Equal(EncodePrevious(source), second);
    });

    [Fact]
    public void FailedEncodeIsNotCached_SoTheNextRevisionRetries() => StaTestHost.Run(() =>
    {
        BitmapSource source = Source("Bgra32");
        int attempts = 0;
        byte[] Encode(BitmapSource bitmap)
        {
            if (++attempts == 1) throw new IOException("Synthetic transient encoder failure");
            return AnnotationEditorControl.EncodeDraftBitmap(bitmap);
        }
        Assert.Throws<IOException>(() => AnnotationEditorControl.EncodeCachedDraftBitmap(source, Encode));
        byte[] retried = AnnotationEditorControl.EncodeCachedDraftBitmap(source, Encode);
        Assert.Equal(EncodePrevious(source), retried);
        Assert.Same(retried, AnnotationEditorControl.EncodeCachedDraftBitmap(source, Encode)); // success is cached
        Assert.Equal(2, attempts);
    });

    [Fact]
    public void UnfrozenLiveBitmap_IsDetachedOnceAndEncodesIdentically() => StaTestHost.Run(() =>
    {
        var live = new WriteableBitmap(Source("Pbgra32"));
        var detached = new WriteableBitmap(live);
        detached.Freeze();
        live.WritePixels(new Int32Rect(0, 0, 1, 1), new byte[] { 9, 9, 9, 255 }, 4, 0); // later edits do not leak in
        Assert.Equal(EncodePrevious(Source("Pbgra32")), AnnotationEditorControl.EncodeDraftBitmap(detached));
    });

    [Fact]
    public void MutablePixelsAreEncodedAgainAfterChanges() => StaTestHost.Run(() =>
    {
        var source = new WriteableBitmap(Source("Bgra32"));
        byte[] first = AnnotationEditorControl.EncodeCachedDraftBitmap(source);
        source.WritePixels(new Int32Rect(0, 0, 1, 1), new byte[] { 3, 7, 19, 255 }, 4, 0);
        byte[] second = AnnotationEditorControl.EncodeCachedDraftBitmap(source);
        Assert.NotSame(first, second);
        Assert.False(first.SequenceEqual(second));
        Assert.Equal(EncodePrevious(source), second);
    });

    private static BitmapSource Source(string name)
    {
        PixelFormat format = name switch
        {
            "Bgra32" => PixelFormats.Bgra32,
            "Pbgra32" => PixelFormats.Pbgra32,
            "Bgr24" => PixelFormats.Bgr24,
            "Bgr32" => PixelFormats.Bgr32,
            "Indexed1" => PixelFormats.Indexed1,
            "Indexed8" => PixelFormats.Indexed8,
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };
        BitmapPalette? palette = name == "Indexed1" ? new BitmapPalette([Colors.Black, Colors.White])
            : name == "Indexed8" ? new BitmapPalette([Colors.Red, Color.FromArgb(128, 0, 255, 0), Colors.Blue, Colors.Transparent]) : null;
        const int width = 17, height = 9;
        int stride = (width * format.BitsPerPixel + 7) / 8;
        byte[] pixels = Enumerable.Range(0, stride * height).Select(index => (byte)(index * 37 + 17)).ToArray();
        if (name == "Indexed8") for (int index = 0; index < pixels.Length; index++) pixels[index] %= 4;
        if (format.BitsPerPixel == 32)
        {
            for (int index = 0; index < pixels.Length; index += 4)
            {
                byte alpha = (byte)((index / 4 % 4) * 64);
                pixels[index + 3] = alpha;
                if (name == "Pbgra32")
                    for (int channel = 0; channel < 3; channel++) pixels[index + channel] = (byte)(pixels[index + channel] * alpha / 255);
            }
        }
        BitmapSource source = BitmapSource.Create(width, height, 120, 144, format, palette, pixels, stride);
        source.Freeze();
        return source;
    }

    private static byte[] EncodePrevious(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static void AssertDecodedEqual(byte[] expected, byte[] actual)
    {
        using var expectedStream = new MemoryStream(expected);
        using var actualStream = new MemoryStream(actual);
        BitmapFrame first = BitmapDecoder.Create(expectedStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        BitmapFrame second = BitmapDecoder.Create(actualStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        Assert.Equal(first.DpiX, second.DpiX);
        Assert.Equal(first.DpiY, second.DpiY);
        Assert.Equal(first.Format, second.Format);
        Assert.Equal(first.Palette?.Colors.ToArray(), second.Palette?.Colors.ToArray());
        var firstPixels = new FormatConvertedBitmap(first, PixelFormats.Bgra32, null, 0);
        var secondPixels = new FormatConvertedBitmap(second, PixelFormats.Bgra32, null, 0);
        int stride = first.PixelWidth * 4;
        byte[] firstBytes = new byte[stride * first.PixelHeight];
        byte[] secondBytes = new byte[stride * second.PixelHeight];
        firstPixels.CopyPixels(firstBytes, stride, 0);
        secondPixels.CopyPixels(secondBytes, stride, 0);
        Assert.Equal(firstBytes, secondBytes);
    }
}
