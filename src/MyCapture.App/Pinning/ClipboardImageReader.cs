using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Threading;
using MyCapture.Core.Capture;
using MyCapture.Core.Pin;
using MyCapture.Platform.Imaging;

namespace MyCapture.App.Pinning;

/// <summary>
/// Reads a lossless, frozen image from the Windows clipboard without blocking the UI while
/// another process temporarily owns the clipboard.
/// </summary>
/// <remarks>
/// MyCapture and many modern editors publish both a custom PNG stream and a legacy Bitmap.
/// PNG is tried first so transparency and exact colour data survive F3; Bitmap remains the
/// interoperability fallback. Transient <c>CLIPBRD_E_CANT_OPEN</c> failures yield through an
/// asynchronous bounded retry instead of sleeping the WPF dispatcher for hundreds of
/// milliseconds.
/// </remarks>
internal static class ClipboardImageReader
{
    private const uint ClipboardCantOpen = 0x800401D0;

    internal readonly record struct ReadAttempt(
        ClipboardImageOutcome Outcome,
        BitmapSource? Image,
        bool ShouldRetry)
    {
        internal static ReadAttempt Retry() =>
            new(ClipboardImageOutcome.Busy(), null, ShouldRetry: true);

        internal static ReadAttempt Busy() =>
            new(ClipboardImageOutcome.Busy(), null, ShouldRetry: false);

        internal static ReadAttempt NoImage() =>
            new(ClipboardImageOutcome.NoImage(), null, ShouldRetry: false);

        internal static ReadAttempt Success(BitmapSource image) =>
            new(
                ClipboardImageOutcome.Success(image.PixelWidth, image.PixelHeight),
                image,
                ShouldRetry: false);
    }

    /// <summary>
    /// A clipboard item ready to become a pin. Text and tables carry both their rendered
    /// bitmap and the exact Unicode source payload.
    /// </summary>
    internal readonly record struct PinReadAttempt(
        ClipboardImageOutcome Outcome,
        PinContent? Content)
    {
        internal static PinReadAttempt Busy() =>
            new(ClipboardImageOutcome.Busy(), null);

        internal static PinReadAttempt NoContent() =>
            new(ClipboardImageOutcome.NoImage(), null);

        internal static PinReadAttempt Success(PinContent content) =>
            new(
                ClipboardImageOutcome.Success(
                    content.Image.PixelWidth,
                    content.Image.PixelHeight),
                content);
    }

    /// <summary>
    /// Immutable clipboard data captured on the WPF dispatcher. Detached payloads, frozen
    /// images and the matching source lookup may cross the dispatcher boundary.
    /// </summary>
    internal readonly record struct CapturedAttempt(
        ClipboardImageOutcome Outcome,
        byte[]? PngBytes,
        BitmapSource? FallbackImage,
        bool ShouldRetry,
        uint ClipboardSequence,
        string? OriginalText,
        bool IsTabularText)
    {
        internal BitmapSource? CodePreview { get; init; }

        internal PinCodeDocument? CodeDocument { get; init; }

        internal bool HasCodeHtml { get; init; }

        internal bool HasExplicitMarkdown { get; init; }

        internal CaptureProvenance? Provenance { get; init; }

        internal Task<CaptureProvenance>? PendingProvenance { get; init; }

        internal static CapturedAttempt Retry() =>
            new(
                ClipboardImageOutcome.Busy(),
                null,
                null,
                ShouldRetry: true,
                ClipboardSequence: 0,
                OriginalText: null,
                IsTabularText: false);

        internal static CapturedAttempt Busy() =>
            new(
                ClipboardImageOutcome.Busy(),
                null,
                null,
                ShouldRetry: false,
                ClipboardSequence: 0,
                OriginalText: null,
                IsTabularText: false);

        internal static CapturedAttempt NoImage() =>
            new(
                ClipboardImageOutcome.NoImage(),
                null,
                null,
                ShouldRetry: false,
                ClipboardSequence: 0,
                OriginalText: null,
                IsTabularText: false);

        internal static CapturedAttempt Content(
            byte[]? pngBytes,
            BitmapSource? fallbackImage,
            uint clipboardSequence = 0,
            string? originalText = null,
            bool isTabularText = false) =>
            new(
                ClipboardImageOutcome.NoImage(),
                pngBytes,
                fallbackImage,
                ShouldRetry: false,
                clipboardSequence,
                originalText,
                isTabularText);
    }

    /// <summary>
    /// Attempts to read the clipboard on an isolated STA worker. WPF performs its own bounded
    /// OLE retry internally; any synchronous wait therefore blocks only the worker.
    /// </summary>
    internal static async Task<(ClipboardImageOutcome Outcome, BitmapSource? Image)> ReadAsync()
    {
        CapturedAttempt captured = await StaThreadTask.RunAsync(
            CaptureOnce,
            "MyCapture clipboard reader").ConfigureAwait(false);

        ReadAttempt completed = await CompleteCapturedAttemptWithFallbackAsync(
            captured,
            TryDecodePngBytes,
            sequence => StaThreadTask.RunAsync(
                () => CaptureFallbackOnce(sequence),
                "MyCapture clipboard fallback reader")).ConfigureAwait(false);

        return (completed.Outcome, completed.Image);
    }

    /// <summary>
    /// Reads the richest clipboard representation supported by screen pins. A spreadsheet
    /// range is shown with the picture the source application published for the same copy and
    /// keeps its cell text; ordinary text is used when there is no decodable image.
    /// </summary>
    internal static async Task<PinReadAttempt> ReadPinAsync()
    {
        CapturedAttempt captured = await StaThreadTask.RunAsync(
            CaptureOnce,
            "MyCapture clipboard reader").ConfigureAwait(false);

        return await CompletePinAttemptAsync(
            captured,
            TryDecodePngBytes,
            (text, isTabular) => StaThreadTask.RunAsync(
                () => ClipboardTextRenderer.Render(text, isTabular),
                "MyCapture clipboard text renderer"),
            sequence => StaThreadTask.RunAsync(
                () => CaptureFallbackOnce(sequence),
                "MyCapture clipboard fallback reader")).ConfigureAwait(false);
    }

    internal static async Task<PinReadAttempt> CompletePinAttemptAsync(
        CapturedAttempt captured,
        Func<byte[], BitmapSource?> decodePng,
        Func<string, bool, Task<BitmapSource>> renderText,
        Func<uint, Task<CapturedAttempt>> captureSameGenerationFallback,
        Func<string, Task<BitmapSource>>? renderMarkdown = null)
    {
        ArgumentNullException.ThrowIfNull(decodePng);
        ArgumentNullException.ThrowIfNull(renderText);
        ArgumentNullException.ThrowIfNull(captureSameGenerationFallback);

        if (captured.ShouldRetry || captured.Outcome.Status == ClipboardImageStatus.Busy)
        {
            return PinReadAttempt.Busy();
        }

        if (captured.HasExplicitMarkdown && !captured.IsTabularText && HasSourceText(captured))
        {
            PinContent? markdown = await TryRenderTextAsync(captured, renderText, renderMarkdown).ConfigureAwait(false);
            if (markdown is not null)
                return PinReadAttempt.Success(markdown with { PendingProvenance = captured.PendingProvenance });
        }

        if (captured.CodePreview is not null && captured.OriginalText is not null)
        {
            PinContent code = captured.CodeDocument is not null
                ? PinContent.FromCode(captured.CodePreview, captured.OriginalText, captured.CodeDocument, captured.Provenance)
                : PinContent.FromText(captured.CodePreview, captured.OriginalText, false, captured.Provenance);
            return PinReadAttempt.Success(code with { PendingProvenance = captured.PendingProvenance });
        }

        // A Markdown pin publishes its exact source alongside its bitmap. Prefer that source
        // when pinning it again, while retaining Excel pictures and editor-specific styling.
        if (!captured.IsTabularText && !captured.HasCodeHtml && HasSourceText(captured)
            && ClipboardMarkdownRenderer.IsMarkdown(captured.OriginalText!))
        {
            PinContent? markdown = await TryRenderTextAsync(captured, renderText, renderMarkdown).ConfigureAwait(false);
            if (markdown is not null)
                return PinReadAttempt.Success(markdown with { PendingProvenance = captured.PendingProvenance });
        }

        ReadAttempt image = await CompleteCapturedAttemptWithFallbackAsync(
            captured,
            decodePng,
            captureSameGenerationFallback).ConfigureAwait(false);

        if (image.Image is not null && captured.IsTabularText && HasSourceText(captured))
        {
            // Excel publishes a picture of the copied cells (PNG or Bitmap) next to the cell
            // text. The picture shows the real fonts, fills, borders and column widths, so it
            // becomes the visual; the cell text stays attached for exact copying instead of
            // OCR. Tables without a decodable picture (browser or Word HTML, ranges too large
            // for a picture) fall through to the rendered grid. An EnhancedMetafile alone also
            // falls through: Windows does not synthesise a Bitmap from it, and playing a
            // metafile at the right scale is not reliable enough to replace the grid.
            return PinReadAttempt.Success(
                PinContent.FromTablePicture(FlattenOntoWhite(image.Image), captured.OriginalText!, captured.Provenance)
                with { PendingProvenance = captured.PendingProvenance });
        }

        if (image.Image is not null)
        {
            // The clipboard PNG may be one of our exports; its tEXt chunk carries the source
            // page URL, which an F9 upload keeps with the image.
            string sourcePageUrl = captured.PngBytes is { Length: > 0 } png
                ? PngSourceUrlMetadata.TryReadSourceUrl(png)
                : string.Empty;
            return PinReadAttempt.Success(PinContent.FromImage(image.Image, sourcePageUrl, captured.Provenance)
                with { PendingProvenance = captured.PendingProvenance });
        }

        if (HasSourceText(captured))
        {
            PinContent? text = await TryRenderTextAsync(captured, renderText, renderMarkdown).ConfigureAwait(false);
            if (text is not null)
            {
                return PinReadAttempt.Success(text with { PendingProvenance = captured.PendingProvenance });
            }
        }

        return image.Outcome.Status == ClipboardImageStatus.Busy
            ? PinReadAttempt.Busy()
            : PinReadAttempt.NoContent();
    }

    private static CapturedAttempt CaptureOnce()
    {
        try
        {
            uint clipboardSequence = GetClipboardSequenceNumber();
            IDataObject? data = Clipboard.GetDataObject();
            return data is null
                ? CapturedAttempt.NoImage()
                : CaptureFrom(data, clipboardSequence, ReadSystemClipboardImage);
        }
        catch (COMException ex) when ((uint)ex.HResult == ClipboardCantOpen)
        {
            return CapturedAttempt.Retry();
        }
        catch (COMException)
        {
            return CapturedAttempt.Busy();
        }
        catch (ExternalException)
        {
            return CapturedAttempt.Busy();
        }
    }

    /// <summary>
    /// Detaches every representation a pin can use from one clipboard data object: the Unicode
    /// text, PNG bytes or a frozen Bitmap, and a code preview. Must run on an STA thread.
    /// </summary>
    /// <param name="data">The clipboard data object, or an in-memory one in tests.</param>
    /// <param name="clipboardSequence">The generation the data object was read from.</param>
    /// <param name="systemImageFallback">
    /// Last-resort image reader for producers whose delayed Bitmap the data object does not
    /// convert. Production reads the system clipboard; tests pass a fixture.
    /// </param>
    internal static CapturedAttempt CaptureFrom(
        IDataObject data,
        uint clipboardSequence,
        Func<BitmapSource?> systemImageFallback)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(systemImageFallback);
        (string? originalText, bool isTabularText) = CaptureText(data);
        ClipboardProvenance.Snapshot provenance = ClipboardProvenance.Capture(
            TryGetClipboardString(data, ClipboardProvenance.Format, autoConvert: false));

        byte[]? pngBytes = null;
        if (data.GetDataPresent("PNG", autoConvert: false))
        {
            try
            {
                // IDataObject and its stream remain dispatcher-bound. Copy all bytes now so
                // WPF decoding can safely happen on a worker after this method returns.
                pngBytes = TryMaterializePngPayload(
                    data.GetData("PNG", autoConvert: false));
            }
            catch (Exception ex) when (IsRecoverablePngException(ex))
            {
                // A malformed custom representation must not hide a valid Bitmap from the
                // same IDataObject. Continue collecting the compatibility representation.
            }
        }

        CapturedAttempt result = pngBytes is not null
            ? CapturedAttempt.Content(
                pngBytes,
                fallbackImage: null,
                clipboardSequence: clipboardSequence,
                originalText: originalText,
                isTabularText: isTabularText)
            : CaptureFallback(
                data,
                originalText,
                isTabularText,
                clipboardSequence,
                systemImageFallback);
        string? html = TryGetClipboardString(data, DataFormats.Html, false);
        bool hasExplicitMarkdown = ClipboardMarkdownRenderer.IsExplicitMarkdownHtml(html);
        // A named Markdown/Mermaid container uses the document renderer below. Avoid
        // building a discarded editor bitmap on the clipboard's UI-thread capture path.
        PinCodeDocument? codeDocument = originalText is not null && !isTabularText && !hasExplicitMarkdown
            ? ClipboardCodeRenderer.TryParse(originalText, html)
            : null;
        BitmapSource? codePreview = null;
        if (codeDocument is not null)
        {
            try
            {
                codePreview = ClipboardCodeRenderer.Render(codeDocument);
            }
            catch (Exception ex) when (IsRecoverableTextFormatException(ex))
            {
                // Keep the same image/plain-text fallback when local text formatting fails.
                codeDocument = null;
            }
        }
        return result with
        {
            CodeDocument = codeDocument,
            CodePreview = codePreview,
            HasCodeHtml = ClipboardCodeRenderer.IsCodeHtml(html),
            HasExplicitMarkdown = hasExplicitMarkdown,
            Provenance = provenance.Source,
            PendingProvenance = provenance.Pending,
        };
    }

    private static BitmapSource? ReadSystemClipboardImage() =>
        Clipboard.ContainsImage() ? Clipboard.GetImage() : null;

    private static CapturedAttempt CaptureFallbackOnce(uint expectedClipboardSequence)
    {
        try
        {
            if (expectedClipboardSequence == 0
                || GetClipboardSequenceNumber() != expectedClipboardSequence)
            {
                return CapturedAttempt.NoImage();
            }

            CapturedAttempt captured = CaptureFallback(
                Clipboard.GetDataObject(),
                originalText: null,
                isTabularText: false,
                clipboardSequence: expectedClipboardSequence,
                ReadSystemClipboardImage);
            return GetClipboardSequenceNumber() == expectedClipboardSequence
                ? captured with { ClipboardSequence = expectedClipboardSequence }
                : CapturedAttempt.NoImage();
        }
        catch (COMException ex) when ((uint)ex.HResult == ClipboardCantOpen)
        {
            return CapturedAttempt.Retry();
        }
        catch (COMException)
        {
            return CapturedAttempt.Busy();
        }
        catch (ExternalException)
        {
            return CapturedAttempt.Busy();
        }
    }

    private static CapturedAttempt CaptureFallback(
        IDataObject? data,
        string? originalText,
        bool isTabularText,
        uint clipboardSequence,
        Func<BitmapSource?> systemImageFallback)
    {
        if (data is null)
        {
            return CapturedAttempt.NoImage();
        }

        BitmapSource? fallbackImage = null;
        if (data.GetDataPresent(DataFormats.Bitmap, autoConvert: true)
            && data.GetData(DataFormats.Bitmap, autoConvert: true) is BitmapSource source)
        {
            fallbackImage = Freeze(source);
        }

        // A few native producers advertise an image through delayed rendering that the
        // IDataObject conversion above does not materialise. Keep WPF's standard helper as
        // a final compatibility path.
        if (fallbackImage is null
            && systemImageFallback() is BitmapSource fallback)
        {
            fallbackImage = Freeze(fallback);
        }

        return fallbackImage is null
            ? originalText is null
                ? CapturedAttempt.NoImage()
                : CapturedAttempt.Content(
                    pngBytes: null,
                    fallbackImage: null,
                    clipboardSequence,
                    originalText,
                    isTabularText)
            : CapturedAttempt.Content(
                pngBytes: null,
                fallbackImage,
                clipboardSequence: clipboardSequence,
                originalText: originalText,
                isTabularText: isTabularText);
    }

    private static bool HasSourceText(CapturedAttempt captured) =>
        !string.IsNullOrWhiteSpace(captured.OriginalText);

    private static async Task<PinContent?> TryRenderTextAsync(
        CapturedAttempt captured,
        Func<string, bool, Task<BitmapSource>> renderText,
        Func<string, Task<BitmapSource>>? renderMarkdown = null)
    {
        string? originalText = captured.OriginalText;
        if (string.IsNullOrWhiteSpace(originalText))
        {
            return null;
        }

        try
        {
            if (!captured.IsTabularText && (!captured.HasCodeHtml || captured.HasExplicitMarkdown)
                && (captured.HasExplicitMarkdown || ClipboardMarkdownRenderer.IsMarkdown(originalText)))
            {
                try
                {
                    BitmapSource markdown = await (renderMarkdown is null
                        ? StaThreadTask.RunAsync(() => ClipboardMarkdownRenderer.Render(originalText), "MyCapture clipboard Markdown renderer")
                        : renderMarkdown(originalText)).ConfigureAwait(false);
                    return PinContent.FromMarkdown(markdown, originalText, captured.Provenance);
                }
                catch (Exception ex) when (IsRecoverableTextFormatException(ex))
                {
                    // Keep the exact source and existing plain preview when formatting fails.
                }
            }
            BitmapSource rendered = await renderText(originalText, captured.IsTabularText)
                .ConfigureAwait(false);
            return PinContent.FromText(rendered, originalText, captured.IsTabularText, captured.Provenance);
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or FormatException
                                   or InvalidOperationException
                                   or NotSupportedException)
        {
            // A malformed rich clipboard representation must not hide a valid bitmap from the
            // same logical item. The caller falls through to image decoding or NoContent.
            return null;
        }
    }

    internal static (string? Text, bool IsTabular) CaptureText(IDataObject data)
    {
        string? text = TryGetClipboardString(data, DataFormats.UnicodeText, autoConvert: true)
                       ?? TryGetClipboardString(data, DataFormats.Text, autoConvert: true);
        if (string.IsNullOrWhiteSpace(text))
        {
            return (null, false);
        }

        bool isTabular = IsTabularClipboardText(
            text,
            TryGetClipboardString(data, DataFormats.Html, autoConvert: false));
        return (text, isTabular);
    }

    internal static bool IsTabularClipboardText(
        string text,
        string? clipboardHtml = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (ClipboardCodeRenderer.IsCodeHtml(clipboardHtml))
        {
            return false;
        }
        if (ContainsLineBreak(text)
            && clipboardHtml?.Contains("<table", StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }
        // Tabs are also code indentation. Plain TSV must have consistent columns.
        string[] rows = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .TrimEnd('\r', '\n').Split('\n');
        int columns = rows[0].Count(static character => character == '\t');
        return columns > 0 && rows.All(row => !row.StartsWith('\t')
            && row.Count(static character => character == '\t') == columns);
    }

    private static bool ContainsLineBreak(string text) =>
        text.Contains('\r') || text.Contains('\n');

    private static string? TryGetClipboardString(
        IDataObject data,
        string format,
        bool autoConvert)
    {
        try
        {
            return data.GetDataPresent(format, autoConvert)
                ? data.GetData(format, autoConvert) as string
                : null;
        }
        catch (Exception ex) when (IsRecoverableTextFormatException(ex))
        {
            return null;
        }
    }

    private static bool IsRecoverableTextFormatException(Exception ex) =>
        ex is ArgumentException
            or FormatException
            or InvalidOperationException
            or NotSupportedException;

    /// <summary>
    /// Completes a dispatcher-captured attempt. PNG decoding is intentionally scheduled on the
    /// thread pool; the injected overload keeps the thread boundary and fallback deterministic
    /// in tests.
    /// </summary>
    internal static Task<ReadAttempt> CompleteCapturedAttemptAsync(CapturedAttempt captured) =>
        CompleteCapturedAttemptAsync(captured, TryDecodePngBytes);

    internal static async Task<ReadAttempt> CompleteCapturedAttemptWithFallbackAsync(
        CapturedAttempt captured,
        Func<byte[], BitmapSource?> decodePng,
        Func<uint, Task<CapturedAttempt>> captureSameGenerationFallback)
    {
        ArgumentNullException.ThrowIfNull(captureSameGenerationFallback);
        ReadAttempt completed = await CompleteCapturedAttemptAsync(captured, decodePng).ConfigureAwait(false);
        if (completed.Image is not null || captured.PngBytes is not { Length: > 0 })
        {
            return completed;
        }

        // Avoid cloning a potentially huge compatibility bitmap on the common valid-PNG path.
        // If PNG decode fails, the sequence gate guarantees the lazy fallback still belongs to
        // the exact IDataObject generation from which those PNG bytes were captured.
        CapturedAttempt fallback = await captureSameGenerationFallback(captured.ClipboardSequence)
            .ConfigureAwait(false);
        return await CompleteCapturedAttemptAsync(fallback, decodePng).ConfigureAwait(false);
    }

    internal static async Task<ReadAttempt> CompleteCapturedAttemptAsync(
        CapturedAttempt captured,
        Func<byte[], BitmapSource?> decodePng)
    {
        ArgumentNullException.ThrowIfNull(decodePng);

        if (captured.ShouldRetry)
        {
            // WPF ClipboardCore has already exhausted its own bounded OLE retry on the STA
            // worker. Do not stack a second multi-second retry loop on top.
            return ReadAttempt.Busy();
        }

        if (captured.PngBytes is { Length: > 0 } pngBytes)
        {
            BitmapSource? png = await Task.Run(() => decodePng(pngBytes)).ConfigureAwait(false);
            if (png is not null)
            {
                return ReadAttempt.Success(png);
            }
        }

        if (captured.FallbackImage is not null)
        {
            return ReadAttempt.Success(captured.FallbackImage);
        }

        return new ReadAttempt(captured.Outcome, null, ShouldRetry: false);
    }

    /// <summary>Decodes custom clipboard PNG payloads without retaining the source stream.</summary>
    internal static BitmapSource? TryDecodePngPayload(object? payload)
    {
        byte[]? bytes = TryMaterializePngPayload(payload);
        return bytes is null ? null : TryDecodePngBytes(bytes);
    }

    private static byte[]? TryMaterializePngPayload(object? payload)
    {
        try
        {
            switch (payload)
            {
                case byte[] bytes when bytes.Length > 0:
                    return bytes.ToArray();

                case Stream stream:
                    if (stream.CanSeek)
                    {
                        stream.Position = 0;
                    }

                    using (var detached = new MemoryStream())
                    {
                        stream.CopyTo(detached);
                        return detached.Length == 0 ? null : detached.ToArray();
                    }

                default:
                    return null;
            }
        }
        catch (Exception ex) when (IsRecoverablePngException(ex))
        {
            return null;
        }
    }

    private static BitmapSource? TryDecodePngBytes(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            return DecodePng(stream);
        }
        catch (Exception ex) when (IsRecoverablePngException(ex))
        {
            // A corrupt custom format must not hide a valid legacy Bitmap advertised by the
            // same data object. The caller falls through to that compatibility representation.
            return null;
        }
    }

    private static bool IsRecoverablePngException(Exception ex) =>
        ex is IOException
            or NotSupportedException
            or ArgumentException
            or FormatException
            or InvalidOperationException;

    private static BitmapSource DecodePng(Stream stream)
    {
        BitmapDecoder decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0)
        {
            throw new InvalidDataException("The clipboard PNG contains no frames.");
        }

        return Freeze(decoder.Frames[0]);
    }

    /// <summary>
    /// A table picture stands in for cells the user saw on an opaque sheet. A producer that
    /// leaves unfilled cells transparent would otherwise put bare text over the desktop, so
    /// transparent areas are composited onto white, the default sheet colour. A picture that is
    /// transparent everywhere is a 32-bit GDI bitmap whose alpha byte is unused; its colours are
    /// kept as opaque instead. Opaque pictures are returned unchanged.
    /// </summary>
    internal static BitmapSource FlattenOntoWhite(BitmapSource picture)
    {
        ArgumentNullException.ThrowIfNull(picture);
        if (!CanCarryAlpha(picture.Format))
        {
            return picture;
        }

        BitmapSource straight = picture.Format == PixelFormats.Bgra32
            ? picture
            : new FormatConvertedBitmap(picture, PixelFormats.Bgra32, null, 0);
        int stride = straight.PixelWidth * 4;
        byte[] pixels = new byte[stride * straight.PixelHeight];
        straight.CopyPixels(pixels, stride, 0);
        bool translucent = false;
        bool visible = false;
        for (int index = 3; index < pixels.Length; index += 4)
        {
            translucent |= pixels[index] != 0xFF;
            visible |= pixels[index] != 0;
        }

        if (!translucent)
        {
            return picture;
        }

        for (int index = 3; index < pixels.Length; index += 4)
        {
            int alpha = visible ? pixels[index] : 0xFF;
            int white = (0xFF - alpha) * 0xFF;
            pixels[index - 3] = (byte)(((pixels[index - 3] * alpha) + white + 0x7F) / 0xFF);
            pixels[index - 2] = (byte)(((pixels[index - 2] * alpha) + white + 0x7F) / 0xFF);
            pixels[index - 1] = (byte)(((pixels[index - 1] * alpha) + white + 0x7F) / 0xFF);
            pixels[index] = 0xFF;
        }

        BitmapSource flattened = BitmapSource.Create(
            straight.PixelWidth,
            straight.PixelHeight,
            picture.DpiX,
            picture.DpiY,
            PixelFormats.Bgra32,
            palette: null,
            pixels,
            stride);
        flattened.Freeze();
        return flattened;
    }

    private static bool CanCarryAlpha(PixelFormat format) =>
        format == PixelFormats.Bgra32
        || format == PixelFormats.Pbgra32
        || format == PixelFormats.Rgba64
        || format == PixelFormats.Prgba64
        || format == PixelFormats.Rgba128Float
        || format == PixelFormats.Prgba128Float
        || format == PixelFormats.Indexed1
        || format == PixelFormats.Indexed2
        || format == PixelFormats.Indexed4
        || format == PixelFormats.Indexed8;

    private static BitmapSource Freeze(BitmapSource source)
    {
        // Always clone so the pin owns pixels independent of delayed clipboard data. Freezing
        // makes the result safe for the background PNG encoder used by pin save.
        BitmapSource copy = source.Clone();
        copy.Freeze();
        return copy;
    }

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
}
