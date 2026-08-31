using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using MyCapture.App.Threading;
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
    internal static async Task<bool> CopyImageAsync(BitmapSource bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        BitmapSource frozen = Freeze(bitmap);
        return await RunCopySerializedAsync(() => CopyFrozenAsync(frozen)).ConfigureAwait(false);
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

    private static async Task<bool> CopyFrozenAsync(BitmapSource frozen)
    {
        byte[] pngBytes;
        try
        {
            pngBytes = await Task.Run(() => ImageCodec.EncodePng(frozen)).ConfigureAwait(false);
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
                () => TrySetOnce(frozen, pngBytes),
                "MyCapture clipboard writer").ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is COMException or ExternalException)
        {
            return false;
        }
    }

    private static bool TrySetOnce(BitmapSource frozen, byte[] pngBytes)
    {
        try
        {
            var data = new DataObject();
            using var pngStream = new MemoryStream(pngBytes, writable: false);
            data.SetData("PNG", pngStream);
            data.SetImage(frozen);

            // copy:true materialises the delayed formats before this method and the stream
            // return, so clipboard content remains after MyCapture exits.
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
