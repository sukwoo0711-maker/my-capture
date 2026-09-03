using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Pinning;
using MyCapture.App.Editing;
using MyCapture.App.Threading;
using MyCapture.Core.Pin;
using MyCapture.Platform.Imaging;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class ClipboardImageReaderTests
{
    [Theory]
    [InlineData("plain text", null, false)]
    [InlineData("A\tB\r\n1\t2", null, true)]
    [InlineData("row one\r\nrow two", "Version:1.0<!--StartFragment--><TABLE><TR><TD>x</TD></TR></TABLE>", true)]
    [InlineData("single cell", "<table><tr><td>x</td></tr></table>", false)]
    public void IsTabularClipboardText_RecognizesTsvAndMultirowHtmlTables(
        string text,
        string? html,
        bool expected)
    {
        Assert.Equal(
            expected,
            ClipboardImageReader.IsTabularClipboardText(text, html));
    }

    [Fact]
    public void CaptureText_DetachesExactUnicodeAndRecognizesExcelStyleTsv()
    {
        const string source = "열 A\t열 B\r\n한글 😀\t42\r\n";
        var data = new DataObject();
        data.SetText(source, TextDataFormat.UnicodeText);

        (string? text, bool isTabular) = ClipboardImageReader.CaptureText(data);

        Assert.Equal(source, text);
        Assert.True(isTabular);
    }

    [Fact]
    public void TryDecodePngPayload_PreservesDimensionsAlphaAndFreezesTheImage()
    {
        BitmapSource source = AlphaImage();
        byte[] encoded = ImageCodec.EncodePng(source);

        BitmapSource? decoded = ClipboardImageReader.TryDecodePngPayload(encoded);

        Assert.NotNull(decoded);
        Assert.True(decoded!.IsFrozen);
        Assert.Equal(2, decoded.PixelWidth);
        Assert.Equal(1, decoded.PixelHeight);
        byte[] pixels = CopyAsBgra32(decoded);
        Assert.Equal(0x20, pixels[3]);
        Assert.Equal(0xFF, pixels[7]);
    }

    [Fact]
    public void TryDecodePngPayload_StreamIsReadFromStartAndDetachedFromItsLifetime()
    {
        byte[] encoded = ImageCodec.EncodePng(AlphaImage());
        BitmapSource? decoded;
        using (var stream = new MemoryStream(encoded, writable: false))
        {
            stream.Position = stream.Length;
            decoded = ClipboardImageReader.TryDecodePngPayload(stream);
        }

        Assert.NotNull(decoded);
        Assert.True(decoded!.IsFrozen);
        Assert.Equal(2, decoded.PixelWidth);
    }

    [Fact]
    public void TryDecodePngPayload_CorruptCustomFormatFailsSoftForBitmapFallback()
    {
        BitmapSource? decoded = ClipboardImageReader.TryDecodePngPayload(
            new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x00, 0x01 });

        Assert.Null(decoded);
    }

    [Fact]
    public async Task CompleteCapturedAttemptAsync_DecodesCustomPngOnAWorkerThread()
    {
        bool decoderRanOnThreadPool = false;
        BitmapSource expected = AlphaImage();
        var captured = ClipboardImageReader.CapturedAttempt.Content(
            [0x89, 0x50, 0x4E, 0x47],
            fallbackImage: null);

        ClipboardImageReader.ReadAttempt completed =
            await ClipboardImageReader.CompleteCapturedAttemptAsync(
                captured,
                _ =>
                {
                    decoderRanOnThreadPool = Thread.CurrentThread.IsThreadPoolThread;
                    return expected;
                });

        Assert.True(decoderRanOnThreadPool);
        Assert.Same(expected, completed.Image);
        Assert.Equal(ClipboardImageStatus.Success, completed.Outcome.Status);
    }

    [Fact]
    public async Task CompleteCapturedAttemptWithFallbackAsync_MalformedPngReacquiresSameGenerationBitmap()
    {
        BitmapSource fallback = AlphaImage();
        var captured = ClipboardImageReader.CapturedAttempt.Content(
            [0x89, 0x50, 0x4E, 0x47, 0x00, 0x01],
            fallbackImage: null,
            clipboardSequence: 42);
        uint requestedSequence = 0;

        ClipboardImageReader.ReadAttempt completed =
            await ClipboardImageReader.CompleteCapturedAttemptWithFallbackAsync(
                captured,
                _ => null,
                sequence =>
                {
                    requestedSequence = sequence;
                    return Task.FromResult(
                        ClipboardImageReader.CapturedAttempt.Content(
                            pngBytes: null,
                            fallback,
                            clipboardSequence: sequence));
                });

        Assert.Equal(42u, requestedSequence);
        Assert.Same(fallback, completed.Image);
        Assert.True(completed.Image!.IsFrozen);
        Assert.Equal(ClipboardImageStatus.Success, completed.Outcome.Status);
        Assert.Equal(2, completed.Outcome.PixelWidth);
        Assert.Equal(1, completed.Outcome.PixelHeight);
    }

    [Fact]
    public async Task CompleteCapturedAttemptWithFallbackAsync_ChangedGenerationDoesNotMixImages()
    {
        var captured = ClipboardImageReader.CapturedAttempt.Content(
            [0x89, 0x50, 0x4E, 0x47, 0x00, 0x01],
            fallbackImage: null,
            clipboardSequence: 73);

        ClipboardImageReader.ReadAttempt completed =
            await ClipboardImageReader.CompleteCapturedAttemptWithFallbackAsync(
                captured,
                _ => null,
                sequence =>
                {
                    Assert.Equal(73u, sequence);
                    // Production returns NoImage when GetClipboardSequenceNumber changes
                    // before or after the compatibility bitmap is materialised.
                    return Task.FromResult(ClipboardImageReader.CapturedAttempt.NoImage());
                });

        Assert.Null(completed.Image);
        Assert.Equal(ClipboardImageStatus.NoImage, completed.Outcome.Status);
    }

    [Fact]
    public async Task CompleteCapturedAttemptWithFallbackAsync_ValidPngNeverClonesCompatibilityBitmap()
    {
        BitmapSource expected = AlphaImage();
        var captured = ClipboardImageReader.CapturedAttempt.Content(
            [0x89, 0x50, 0x4E, 0x47],
            fallbackImage: null,
            clipboardSequence: 91);
        bool fallbackInvoked = false;

        ClipboardImageReader.ReadAttempt completed =
            await ClipboardImageReader.CompleteCapturedAttemptWithFallbackAsync(
                captured,
                _ => expected,
                _ =>
                {
                    fallbackInvoked = true;
                    return Task.FromResult(ClipboardImageReader.CapturedAttempt.NoImage());
                });

        Assert.Same(expected, completed.Image);
        Assert.False(fallbackInvoked);
    }

    [Fact]
    public async Task CompletePinAttemptAsync_TableTextWinsOverAdvertisedImageAndKeepsExactSource()
    {
        const string source = "이름\t값\r\n첫째\t42\r\n";
        BitmapSource rendered = AlphaImage();
        bool imageDecoded = false;
        bool fallbackInvoked = false;
        var captured = ClipboardImageReader.CapturedAttempt.Content(
            [0x89, 0x50, 0x4E, 0x47],
            fallbackImage: null,
            clipboardSequence: 101,
            originalText: source,
            isTabularText: true);

        ClipboardImageReader.PinReadAttempt completed =
            await ClipboardImageReader.CompletePinAttemptAsync(
                captured,
                _ =>
                {
                    imageDecoded = true;
                    return AlphaImage();
                },
                (text, isTable) =>
                {
                    Assert.Equal(source, text);
                    Assert.True(isTable);
                    return Task.FromResult(rendered);
                },
                _ =>
                {
                    fallbackInvoked = true;
                    return Task.FromResult(ClipboardImageReader.CapturedAttempt.NoImage());
                });

        Assert.False(imageDecoded);
        Assert.False(fallbackInvoked);
        Assert.NotNull(completed.Content);
        Assert.Same(rendered, completed.Content!.Image);
        Assert.Equal(PinContentKind.Table, completed.Content.Kind);
        Assert.Equal(source, completed.Content.OriginalText);
        Assert.Equal(ClipboardImageStatus.Success, completed.Outcome.Status);
    }

    [Fact]
    public async Task CompletePinAttemptAsync_PlainTextRendersWhenNoImageExists()
    {
        const string source = "first line\r\n둘째 줄 😀";
        BitmapSource rendered = AlphaImage();
        var captured = ClipboardImageReader.CapturedAttempt.Content(
            pngBytes: null,
            fallbackImage: null,
            originalText: source);

        ClipboardImageReader.PinReadAttempt completed =
            await ClipboardImageReader.CompletePinAttemptAsync(
                captured,
                _ => null,
                (text, isTable) =>
                {
                    Assert.Equal(source, text);
                    Assert.False(isTable);
                    return Task.FromResult(rendered);
                },
                _ => Task.FromResult(ClipboardImageReader.CapturedAttempt.NoImage()));

        Assert.NotNull(completed.Content);
        Assert.Equal(PinContentKind.Text, completed.Content!.Kind);
        Assert.Equal(source, completed.Content.OriginalText);
        Assert.Same(rendered, completed.Content.Image);
    }

    [Fact]
    public async Task CompletePinAttemptAsync_OrdinaryMixedClipboardPrefersDecodableImage()
    {
        BitmapSource decoded = AlphaImage();
        bool textRendered = false;
        var captured = ClipboardImageReader.CapturedAttempt.Content(
            [0x89, 0x50, 0x4E, 0x47],
            fallbackImage: null,
            clipboardSequence: 7,
            originalText: "image description",
            isTabularText: false);

        ClipboardImageReader.PinReadAttempt completed =
            await ClipboardImageReader.CompletePinAttemptAsync(
                captured,
                _ => decoded,
                (_, _) =>
                {
                    textRendered = true;
                    return Task.FromResult(AlphaImage());
                },
                _ => Task.FromResult(ClipboardImageReader.CapturedAttempt.NoImage()));

        Assert.False(textRendered);
        Assert.NotNull(completed.Content);
        Assert.Equal(PinContentKind.Image, completed.Content!.Kind);
        Assert.Null(completed.Content.OriginalText);
        Assert.Same(decoded, completed.Content.Image);
    }

    [Fact]
    public async Task StaThreadTask_RunsBlockingWorkOffCallerOnAnStaThread()
    {
        int callerThread = Environment.CurrentManagedThreadId;
        using var release = new ManualResetEventSlim(initialState: false);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<(ApartmentState Apartment, int Thread)> work = StaThreadTask.RunAsync(() =>
        {
            started.SetResult();
            release.Wait();
            return (Thread.CurrentThread.GetApartmentState(), Environment.CurrentManagedThreadId);
        });

        await started.Task;
        Assert.False(work.IsCompleted);
        release.Set();
        (ApartmentState apartment, int workerThread) = await work;

        Assert.Equal(ApartmentState.STA, apartment);
        Assert.NotEqual(callerThread, workerThread);
    }

    [Fact]
    public async Task ClipboardCopies_AreSerializedInInvocationOrder()
    {
        var order = new List<string>();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<bool> first = ClipboardImageService.RunCopySerializedAsync(async () =>
        {
            order.Add("first-start");
            firstEntered.SetResult();
            await releaseFirst.Task;
            order.Add("first-end");
            return true;
        });
        await firstEntered.Task;

        Task<bool> second = ClipboardImageService.RunCopySerializedAsync(() =>
        {
            order.Add("second");
            return Task.FromResult(true);
        });
        await Task.Yield();
        Assert.DoesNotContain("second", order);

        releaseFirst.SetResult();
        Assert.True(await first);
        Assert.True(await second);
        Assert.Equal(["first-start", "first-end", "second"], order);
    }

    private static BitmapSource AlphaImage()
    {
        byte[] pixels =
        [
            0x11, 0x22, 0x33, 0x20,
            0x44, 0x55, 0x66, 0xFF,
        ];
        BitmapSource image = BitmapSource.Create(
            2,
            1,
            96,
            96,
            PixelFormats.Bgra32,
            palette: null,
            pixels,
            stride: 8);
        image.Freeze();
        return image;
    }

    private static byte[] CopyAsBgra32(BitmapSource source)
    {
        BitmapSource converted = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int stride = converted.PixelWidth * 4;
        byte[] pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        return pixels;
    }
}
