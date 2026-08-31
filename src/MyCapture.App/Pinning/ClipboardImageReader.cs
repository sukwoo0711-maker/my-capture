using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using MyCapture.App.Threading;
using MyCapture.Core.Pin;

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
    /// Immutable clipboard data captured on the WPF dispatcher. Only detached PNG bytes and a
    /// frozen legacy image are allowed to cross the dispatcher boundary.
    /// </summary>
    internal readonly record struct CapturedAttempt(
        ClipboardImageOutcome Outcome,
        byte[]? PngBytes,
        BitmapSource? FallbackImage,
        bool ShouldRetry,
        uint ClipboardSequence)
    {
        internal static CapturedAttempt Retry() =>
            new(ClipboardImageOutcome.Busy(), null, null, ShouldRetry: true, ClipboardSequence: 0);

        internal static CapturedAttempt Busy() =>
            new(ClipboardImageOutcome.Busy(), null, null, ShouldRetry: false, ClipboardSequence: 0);

        internal static CapturedAttempt NoImage() =>
            new(ClipboardImageOutcome.NoImage(), null, null, ShouldRetry: false, ClipboardSequence: 0);

        internal static CapturedAttempt Content(
            byte[]? pngBytes,
            BitmapSource? fallbackImage,
            uint clipboardSequence = 0) =>
            new(
                ClipboardImageOutcome.NoImage(),
                pngBytes,
                fallbackImage,
                ShouldRetry: false,
                clipboardSequence);
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

    private static CapturedAttempt CaptureOnce()
    {
        try
        {
            uint clipboardSequence = GetClipboardSequenceNumber();
            IDataObject? data = Clipboard.GetDataObject();
            if (data is null)
            {
                return CapturedAttempt.NoImage();
            }

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

            return pngBytes is not null
                ? CapturedAttempt.Content(pngBytes, fallbackImage: null, clipboardSequence)
                : CaptureFallback(data);
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

    private static CapturedAttempt CaptureFallbackOnce(uint expectedClipboardSequence)
    {
        try
        {
            if (expectedClipboardSequence == 0
                || GetClipboardSequenceNumber() != expectedClipboardSequence)
            {
                return CapturedAttempt.NoImage();
            }

            CapturedAttempt captured = CaptureFallback(Clipboard.GetDataObject());
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

    private static CapturedAttempt CaptureFallback(IDataObject? data)
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
            && Clipboard.ContainsImage()
            && Clipboard.GetImage() is BitmapSource fallback)
        {
            fallbackImage = Freeze(fallback);
        }

        return fallbackImage is null
            ? CapturedAttempt.NoImage()
            : CapturedAttempt.Content(pngBytes: null, fallbackImage);
    }

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
