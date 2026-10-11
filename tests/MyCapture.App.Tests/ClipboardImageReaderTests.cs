using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Pinning;
using MyCapture.App.Editing;
using MyCapture.App.Threading;
using MyCapture.Core.Localization;
using MyCapture.Core.Pin;
using MyCapture.Core.Settings;
using MyCapture.Platform.Imaging;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class ClipboardImageReaderTests
{
    [Theory]
    [InlineData("plain text", null, false)]
    [InlineData("if (ready)\r\n\tRun();", null, false)]
    [InlineData("\tfirst();\r\n\tsecond();", null, false)]
    [InlineData("a\tb\nc\td", "<div style=\"white-space: pre;\">code</div>", false)]
    [InlineData("\tvalue\r\n\tother", "<table><tr><td></td><td>value</td></tr></table>", true)]
    [InlineData("A\tB\r\n1\t2", "<table><tr><td style=\"white-space: pre;\">A</td></tr></table>", true)]
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
    public async Task CompletePinAttemptAsync_TablePictureIsShownAndKeepsExactCellText()
    {
        const string source = "이름\t값\r\n첫째\t42\r\n";
        BitmapSource picture = OpaqueImage(3, 2);
        bool textRendered = false;
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
                _ => picture,
                (_, _) =>
                {
                    textRendered = true;
                    return Task.FromResult(AlphaImage());
                },
                _ =>
                {
                    fallbackInvoked = true;
                    return Task.FromResult(ClipboardImageReader.CapturedAttempt.NoImage());
                });

        Assert.False(textRendered);
        Assert.False(fallbackInvoked);
        Assert.NotNull(completed.Content);
        Assert.Same(picture, completed.Content!.Image);
        Assert.Equal(PinContentKind.Table, completed.Content.Kind);
        Assert.Equal(source, completed.Content.OriginalText);
        // The application's picture holds display pixels, like any clipboard image.
        Assert.False(completed.Content.UsesDeviceIndependentPixels);
        Assert.Equal(ClipboardImageStatus.Success, completed.Outcome.Status);
    }

    [Fact]
    public async Task CompletePinAttemptAsync_UndecodableTablePictureFallsBackToTheGrid()
    {
        const string source = "이름\t값\r\n첫째\t42\r\n";
        BitmapSource rendered = AlphaImage();
        uint requestedSequence = 0;
        var captured = ClipboardImageReader.CapturedAttempt.Content(
            [0x89, 0x50, 0x4E, 0x47, 0x00, 0x01],
            fallbackImage: null,
            clipboardSequence: 55,
            originalText: source,
            isTabularText: true);

        ClipboardImageReader.PinReadAttempt completed =
            await ClipboardImageReader.CompletePinAttemptAsync(
                captured,
                _ => null,
                (text, isTable) =>
                {
                    Assert.Equal(source, text);
                    Assert.True(isTable);
                    return Task.FromResult(rendered);
                },
                sequence =>
                {
                    requestedSequence = sequence;
                    return Task.FromResult(ClipboardImageReader.CapturedAttempt.NoImage());
                });

        Assert.Equal(55u, requestedSequence);
        Assert.NotNull(completed.Content);
        Assert.Same(rendered, completed.Content!.Image);
        Assert.Equal(PinContentKind.Table, completed.Content.Kind);
        Assert.Equal(source, completed.Content.OriginalText);
        Assert.True(completed.Content.UsesDeviceIndependentPixels);
    }

    [Fact]
    public async Task ExcelStyleCopyWithPng_PinShowsThePictureAndKeepsTheCellText()
    {
        const string source = "품목\t수량\r\n연필\t3\r\n\t\r\n";
        BitmapSource picture = OpaqueImage(5, 3);
        ClipboardImageReader.PinReadAttempt completed = await ReadPinFromAsync(data =>
        {
            data.SetText(source, TextDataFormat.UnicodeText);
            data.SetData(DataFormats.Html, "Version:1.0\r\n<!--StartFragment--><table><tr><td>품목</td><td>수량</td></tr></table><!--EndFragment-->");
            data.SetData("PNG", new MemoryStream(ImageCodec.EncodePng(picture)));
        });

        PinContent content = Assert.IsType<PinContent>(completed.Content);
        Assert.Equal(PinContentKind.Table, content.Kind);
        Assert.Equal(source, content.OriginalText);
        Assert.False(content.UsesDeviceIndependentPixels);
        Assert.Equal(CopyAsBgra32(picture), CopyAsBgra32(content.Image));
    }

    [Fact]
    public async Task ExcelStyleCopyWithBitmapOnly_PinShowsThePictureAndKeepsTheCellText()
    {
        const string source = "A\tB\r\n1\t2\r\n";
        BitmapSource picture = OpaqueImage(4, 2);
        ClipboardImageReader.PinReadAttempt completed = await ReadPinFromAsync(data =>
        {
            data.SetText(source, TextDataFormat.UnicodeText);
            data.SetData(DataFormats.Bitmap, picture);
        });

        PinContent content = Assert.IsType<PinContent>(completed.Content);
        Assert.Equal(PinContentKind.Table, content.Kind);
        Assert.Equal(source, content.OriginalText);
        Assert.Equal(CopyAsBgra32(picture), CopyAsBgra32(content.Image));
    }

    [Fact]
    public async Task HtmlTableWithoutPicture_PinRendersTheCellGrid()
    {
        const string source = "Name\tScore\r\nKim\t90\r\n";
        BitmapSource grid = OpaqueImage(7, 7);
        bool? renderedAsTable = null;
        ClipboardImageReader.PinReadAttempt completed = await ReadPinFromAsync(
            data =>
            {
                data.SetText(source, TextDataFormat.UnicodeText);
                data.SetData(DataFormats.Html, "<table><tr><td>Name</td><td>Score</td></tr><tr><td>Kim</td><td>90</td></tr></table>");
            },
            (_, isTable) =>
            {
                renderedAsTable = isTable;
                return Task.FromResult(grid);
            });

        PinContent content = Assert.IsType<PinContent>(completed.Content);
        Assert.True(renderedAsTable);
        Assert.Same(grid, content.Image);
        Assert.Equal(PinContentKind.Table, content.Kind);
        Assert.Equal(source, content.OriginalText);
        Assert.True(content.UsesDeviceIndependentPixels);
    }

    [Fact]
    public async Task EditorCodeWithTabs_StaysACodePin()
    {
        const string source = "a\tb\nc\td";
        bool textRendered = false;
        ClipboardImageReader.PinReadAttempt completed = await ReadPinFromAsync(
            data =>
            {
                data.SetText(source, TextDataFormat.UnicodeText);
                data.SetData(DataFormats.Html, "<div style=\"background-color: #1e1e1e; white-space: pre;\"><div>a\tb</div><div>c\td</div></div>");
            },
            (_, _) =>
            {
                textRendered = true;
                return Task.FromResult(AlphaImage());
            });

        PinContent content = Assert.IsType<PinContent>(completed.Content);
        Assert.False(textRendered);
        Assert.Equal(PinContentKind.Text, content.Kind);
        Assert.Equal(source, content.OriginalText);
        // The editor's own colours: the code renderer paints the HTML background.
        Assert.Equal(new byte[] { 0x1E, 0x1E, 0x1E, 0xFF }, CopyAsBgra32(content.Image)[..4]);
    }

    [Fact]
    public void FlattenOntoWhite_CompositesTransparentCellsAndKeepsOpaquePictures()
    {
        BitmapSource translucent = AlphaImage();
        BitmapSource flattened = ClipboardImageReader.FlattenOntoWhite(translucent);

        Assert.True(flattened.IsFrozen);
        byte[] pixels = CopyAsBgra32(flattened);
        // 0x20 alpha over white: colour * 0.125 + 255 * 0.875.
        Assert.InRange(pixels[0], 224, 227);
        Assert.InRange(pixels[1], 226, 229);
        Assert.InRange(pixels[2], 228, 231);
        Assert.Equal(0xFF, pixels[3]);
        Assert.Equal(new byte[] { 0x44, 0x55, 0x66, 0xFF }, pixels[4..8]);

        // A GDI bitmap that never used its alpha byte keeps its colours instead of turning white.
        byte[] unusedAlpha = [0x10, 0x20, 0x30, 0x00, 0x40, 0x50, 0x60, 0x00];
        BitmapSource gdi = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, unusedAlpha, 8);
        gdi.Freeze();
        Assert.Equal(
            new byte[] { 0x10, 0x20, 0x30, 0xFF, 0x40, 0x50, 0x60, 0xFF },
            CopyAsBgra32(ClipboardImageReader.FlattenOntoWhite(gdi)));

        BitmapSource opaque = OpaqueImage(2, 2);
        Assert.Same(opaque, ClipboardImageReader.FlattenOntoWhite(opaque));
        var bgr = new FormatConvertedBitmap(opaque, PixelFormats.Bgr32, null, 0);
        bgr.Freeze();
        Assert.Same(bgr, ClipboardImageReader.FlattenOntoWhite(bgr));
    }

    [Fact]
    public void TablePicturePin_CopiesTheCellTextInsteadOfOfferingOcr() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        const string source = "제품\t수량\r\n연필\t3\r\n";
        BitmapSource picture = OpaqueImage(40, 20);
        var pin = new PinWindow(PinContent.FromTablePicture(picture, source), new PinViewState(40, 20, 1, 1, 0.1), -10000, -10000,
            () => new PinSettings());
        try
        {
            string? copied = null;
            bool ocr = false;
            pin.OriginalTextCopyRequested += (_, text) => copied = text;
            pin.OcrRequested += (_, _) => ocr = true;

            pin.SimulateCtrlDoubleClickForTest();

            Assert.Equal(source, copied);
            Assert.False(ocr);
            Assert.Equal(PinContentKind.Table, pin.ContentKind);
            List<string> headers = Assert.IsType<ContextMenu>(pin.ContextMenu).Items.OfType<MenuItem>()
                .Select(item => item.Header?.ToString() ?? string.Empty).ToList();
            Assert.Contains("원문 텍스트 복사", headers);
            Assert.DoesNotContain("텍스트 복사", headers);
        }
        finally
        {
            pin.Close();
        }
    });

    /// <summary>
    /// Runs the production capture and completion path over an in-memory data object. The
    /// system clipboard is never read or written: its last-resort image reader is replaced.
    /// </summary>
    private static async Task<ClipboardImageReader.PinReadAttempt> ReadPinFromAsync(
        Action<DataObject> fill,
        Func<string, bool, Task<BitmapSource>>? renderText = null)
    {
        ClipboardImageReader.CapturedAttempt captured = await StaThreadTask.RunAsync(
            () =>
            {
                var data = new DataObject();
                fill(data);
                return ClipboardImageReader.CaptureFrom(data, clipboardSequence: 9, () => null);
            },
            "clipboard fixture reader");
        return await ClipboardImageReader.CompletePinAttemptAsync(
            captured,
            ClipboardImageReader.TryDecodePngPayload,
            renderText ?? ((_, _) => throw new InvalidOperationException("The table picture must be used, not the grid.")),
            _ => Task.FromResult(ClipboardImageReader.CapturedAttempt.NoImage()));
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

    private static BitmapSource OpaqueImage(int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = (byte)(index * 7);
            pixels[index + 1] = (byte)(index * 13);
            pixels[index + 2] = (byte)(index * 29);
            pixels[index + 3] = 0xFF;
        }

        BitmapSource image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
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
