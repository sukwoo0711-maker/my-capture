using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Ocr;
using MyCapture.App.Pinning;
using MyCapture.Ocr;
using MyCapture.Platform.Imaging;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// A pin restored at launch, reopened from the closed list, made from a clipboard PNG or floated from the
/// text recognition window holds a frame that one thread decoded and others encode (copy, quick save, the
/// session, the closed list). A frame reads its metadata from its decoder when first asked, the decoder
/// belongs to the decoding thread, and an encoder asks, so such an encode threw InvalidOperationException
/// unless it happened to run on the decoding thread. Every thread here is a new one, so none of them is a
/// decoding thread again.
/// </summary>
public sealed class DecodedPngEncodesOnAnyThreadTests
{
    private const string SourceUrl = "https://example.invalid/page";

    [Theory]
    [InlineData("saved", false)]
    [InlineData("saved", true)]
    [InlineData("clipboard", false)]
    [InlineData("clipboard", true)]
    [InlineData("recognition", false)]
    [InlineData("recognition", true)]
    public void ADecodedPng_EncodesOnThreadsThatDidNotDecodeIt(string decoder, bool withTextChunk) => StaTestHost.Run(() =>
    {
        byte[] png = withTextChunk ? ImageCodec.EncodePng(Picture(), SourceUrl) : ImageCodec.EncodePng(Picture());
        // The reference is what WPF gives when the thread that decoded a frame encodes it.
        byte[] expectedPng = OnNewThread(() => ImageCodec.EncodePng(Unread(png)));
        byte[] expectedJpeg = OnNewThread(() => ImageCodec.EncodeJpeg(Unread(png), 90));

        // The product's decoders run on a thread that never encodes the picture, as for a pin.
        BitmapSource decoded = OnNewThread(() => Decode(decoder, png));

        Assert.True(decoded.IsFrozen);
        Assert.Equal(expectedPng, ImageCodec.EncodePng(decoded));
        Assert.Equal(expectedPng, OnNewThread(() => ImageCodec.EncodePng(decoded)));
        Assert.Equal(expectedJpeg, OnNewThread(() => ImageCodec.EncodeJpeg(decoded, 90)));
    });

    [Fact]
    public void AFrameLeftUnreadOnItsDecodingThread_DoesNotEncodeElsewhere() => StaTestHost.Run(() =>
    {
        // The WPF behaviour that ImageCodec.KeepFrameMetadata works around. If this starts to fail, the
        // platform no longer needs the read and the decoders can drop it.
        byte[] png = ImageCodec.EncodePng(Picture());
        BitmapSource unread = OnNewThread(() => Unread(png));

        Assert.Throws<InvalidOperationException>(() => ImageCodec.EncodePng(unread));
        Assert.Throws<InvalidOperationException>(() => OnNewThread(() => ImageCodec.EncodePng(unread)));
    });

    /// <summary>Runs <paramref name="work"/> on a thread of its own and gives back its result or its exception.</summary>
    internal static T OnNewThread<T>(Func<T> work)
    {
        T result = default!;
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); }
        });
        thread.Start();
        thread.Join();
        failure?.Throw();
        return result;
    }

    private static BitmapSource Decode(string decoder, byte[] png) => Assert.IsAssignableFrom<BitmapSource>(decoder switch
    {
        "saved" => ImageCodec.TryDecodePng(png),
        "clipboard" => ClipboardImageReader.TryDecodePngPayload(png),
        _ => OcrSourceSnapshot.LoadAsync(OcrRequest.FromBytes(png), CancellationToken.None).GetAwaiter().GetResult(),
    });

    // A frame as WPF's decoder leaves it: nothing has asked for its metadata yet.
    private static BitmapSource Unread(byte[] png)
    {
        using var stream = new MemoryStream(png, writable: false);
        BitmapSource frame = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad).Frames[0];
        frame.Freeze();
        return frame;
    }

    private static BitmapSource Picture()
    {
        const int width = 24, height = 10;
        byte[] pixels = new byte[width * height * 4];
        for (int index = 0; index < pixels.Length; index++) pixels[index] = (byte)(index * 7 + 3);
        BitmapSource picture = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * 4);
        picture.Freeze();
        return picture;
    }
}
