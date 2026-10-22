using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using MyCapture.App.Threading;
using MyCapture.App.Pinning;
using MyCapture.Core.Capture;
using MyCapture.Platform.Imaging;

namespace MyCapture.App.Editing;

/// <summary>
/// Places an image on the Windows clipboard as exact PNG plus a legacy Bitmap fallback.
/// </summary>
/// <remarks>
/// PNG encoding runs on a worker and the WPF/OLE clipboard operation runs on an isolated STA
/// worker. Copying a large scrolling capture therefore never blocks every WPF window while
/// another application briefly holds <c>OpenClipboard</c>.
/// </remarks>
internal static class ClipboardImageService
{
    private const uint ClipboardCantOpen = 0x800401D0;
    private static readonly SemaphoreSlim CopyGate = new(1, 1);

    /// <summary>Copies <paramref name="bitmap"/> without blocking the caller's dispatcher.</summary>
    internal static Task<bool> CopyImageAsync(BitmapSource bitmap) => CopyImageAsync(bitmap, sourceUrl: null);

    /// <summary>
    /// Copies <paramref name="bitmap"/> and embeds the capture's source page URL in the PNG
    /// payload, so a paste — or a later F9 upload — keeps the capture's origin.
    /// </summary>
    internal static Task<bool> CopyImageAsync(BitmapSource bitmap, string? sourceUrl) => CopyImageAsync(bitmap, sourceUrl, null);

    internal static Task<bool> CopyImageAsync(BitmapSource bitmap, string? sourceUrl, Action<IDataObject>? writeData) =>
        CopyImageWithTextAsync(bitmap, sourceUrl, originalText: null, writeData);

    internal static Task<bool> CopyImageWithProvenanceAsync(BitmapSource bitmap, string? sourceUrl,
        CaptureProvenance? provenance, Action<IDataObject>? writeData = null,
        Task<CaptureProvenance>? pendingProvenance = null) =>
        CopyImageWithTextAsync(bitmap, sourceUrl, originalText: null, writeData, provenance, pendingProvenance);

    /// <summary>Copies the image and any exact retained source in one clipboard data object.</summary>
    internal static Task<bool> CopyImageWithTextAsync(BitmapSource bitmap, string? sourceUrl, string? originalText) =>
        CopyImageWithTextAsync(bitmap, sourceUrl, originalText, writeData: null);

    internal static async Task<bool> CopyImageWithTextAsync(BitmapSource bitmap, string? sourceUrl, string? originalText, Action<IDataObject>? writeData,
        CaptureProvenance? provenance = null, Task<CaptureProvenance>? pendingProvenance = null)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        BitmapSource frozen = Freeze(bitmap);
        return await RunCopySerializedAsync(() => CopyFrozenAsync(frozen, sourceUrl, originalText, writeData, provenance, pendingProvenance)).ConfigureAwait(false);
    }

    /// <summary>
    /// Copies an exact Unicode source string retained by a rendered text or table pin.
    /// </summary>
    internal static async Task<bool> CopyTextAsync(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return await RunCopySerializedAsync(() => CopyTextCoreAsync(text)).ConfigureAwait(false);
    }

    /// <summary>
    /// Preserves user invocation order across independent encodes and OLE retries, so a slow
    /// earlier copy can never overwrite a newer clipboard selection after it completes.
    /// </summary>
    internal static async Task<bool> RunCopySerializedAsync(Func<Task<bool>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await CopyGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await operation().ConfigureAwait(false);
        }
        finally
        {
            CopyGate.Release();
        }
    }

    private static async Task<bool> CopyFrozenAsync(BitmapSource frozen, string? sourceUrl, string? originalText, Action<IDataObject>? writeData,
        CaptureProvenance? provenance, Task<CaptureProvenance>? pendingProvenance)
    {
        byte[] pngBytes;
        try
        {
            pngBytes = await Task.Run(() => ImageCodec.EncodePng(frozen, sourceUrl)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException
                                   or InvalidOperationException
                                   or NotSupportedException
                                   or ArgumentException)
        {
            return false;
        }

        try
        {
            // WPF's ClipboardCore already performs its bounded native OLE retry. That retry
            // can synchronously sleep, so run the single operation on an isolated STA instead
            // of stacking another retry loop on the UI dispatcher.
            return await StaThreadTask.RunAsync(
                () => TrySetOnce(frozen, pngBytes, originalText, writeData, provenance, pendingProvenance),
                "MyCapture clipboard writer").ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is COMException or ExternalException)
        {
            return false;
        }
    }

    private static async Task<bool> CopyTextCoreAsync(string text)
    {
        try
        {
            return await StaThreadTask.RunAsync(
                () => TrySetTextOnce(text),
                "MyCapture clipboard text writer").ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is COMException or ExternalException)
        {
            return false;
        }
    }

    private static bool TrySetOnce(BitmapSource frozen, byte[] pngBytes, string? originalText, Action<IDataObject>? writeData,
        CaptureProvenance? provenance, Task<CaptureProvenance>? pendingProvenance)
    {
        try
        {
            var data = new DataObject();
            using var pngStream = new MemoryStream(pngBytes, writable: false);
            data.SetData("PNG", pngStream);
            data.SetImage(frozen);
            if (originalText is not null) data.SetText(originalText, TextDataFormat.UnicodeText);
            // Only an opaque process-local reference travels with the clipboard. File exports
            // and PNG metadata retain their existing privacy rules.
            string? sourceToken = provenance is { HasSource: true } || pendingProvenance is not null
                ? Guid.NewGuid().ToString("N") : null;
            if (sourceToken is not null)
                data.SetData(ClipboardProvenance.Format, sourceToken, autoConvert: false);

            // copy:true materialises the delayed formats before this method and the stream
            // return, so clipboard content remains after MyCapture exits.
            if (writeData is null) Clipboard.SetDataObject(data, copy: true);
            else writeData(data);
            if (sourceToken is not null)
                ClipboardProvenance.Publish(sourceToken, provenance ?? CaptureProvenance.None, pendingProvenance);
            return true;
        }
        catch (COMException ex) when ((uint)ex.HResult == ClipboardCantOpen)
        {
            return false;
        }
        catch (COMException)
        {
            return false;
        }
        catch (ExternalException)
        {
            return false;
        }
    }

    private static bool TrySetTextOnce(string text)
    {
        try
        {
            var data = new DataObject();
            data.SetText(text, TextDataFormat.UnicodeText);
            Clipboard.SetDataObject(data, copy: true);
            return true;
        }
        catch (COMException ex) when ((uint)ex.HResult == ClipboardCantOpen)
        {
            return false;
        }
        catch (COMException)
        {
            return false;
        }
        catch (ExternalException)
        {
            return false;
        }
    }

    private static BitmapSource Freeze(BitmapSource bitmap)
    {
        if (bitmap.IsFrozen)
        {
            return bitmap;
        }

        BitmapSource copy = bitmap.Clone();
        copy.Freeze();
        return copy;
    }
}
