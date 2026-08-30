using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using MyCapture.Platform.Imaging;

namespace MyCapture.App.Editing;

/// <summary>
/// Places an annotated capture on the Windows clipboard.
/// </summary>
/// <remarks>
/// <para>
/// Two formats are set on one data object. <c>PNG</c> carries the exact bytes with the
/// alpha channel intact for applications that understand it (chat apps, modern editors),
/// while a <see cref="System.Windows.Media.Imaging.BitmapSource"/> under the standard
/// <c>Bitmap</c> format is what legacy consumers such as older Office paste. Setting only
/// one loses either transparency or half the target applications.
/// </para>
/// <para>
/// The clipboard is a single shared OS resource, so <c>OpenClipboard</c> fails with
/// <c>CLIPBRD_E_CANT_OPEN</c> (HRESULT 0x800401D0) whenever another process is mid-paste.
/// This is transient and common; the copy is retried a handful of times with a short
/// back-off rather than surfaced as a failure the first time it races.
/// </para>
/// </remarks>
internal static class ClipboardImageService
{
    /// <summary>HRESULT returned by the shell when the clipboard cannot be opened.</summary>
    private const uint ClipboardCantOpen = 0x800401D0;

    private const int MaxAttempts = 10;
    private const int RetryDelayMs = 60;

    /// <summary>
    /// Copies <paramref name="bitmap"/> to the clipboard as PNG and Bitmap.
    /// </summary>
    /// <returns><see langword="true"/> when the clipboard was updated.</returns>
    internal static bool CopyImage(BitmapSource bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        BitmapSource frozen = Freeze(bitmap);
        byte[] pngBytes = ImageCodec.EncodePng(frozen);

        var data = new DataObject();

        // PNG first: lossless, alpha-preserving, understood by modern consumers.
        using (var pngStream = new MemoryStream(pngBytes, writable: false))
        {
            data.SetData("PNG", pngStream);

            // A DIB/Bitmap fallback for consumers that do not read the PNG format. WPF flattens
            // the BitmapSource into a device-independent bitmap for this standard format.
            data.SetImage(frozen);

            return TrySetWithRetry(data);
        }
    }

    private static bool TrySetWithRetry(DataObject data)
    {
        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                // copy: true leaves the data on the clipboard after this process exits, which is
                // the behaviour a user expects from "copy to clipboard".
                Clipboard.SetDataObject(data, copy: true);
                return true;
            }
            catch (COMException ex) when ((uint)ex.HResult == ClipboardCantOpen && attempt < MaxAttempts)
            {
                // Another process holds the clipboard open; back off briefly and retry.
                Thread.Sleep(RetryDelayMs);
            }
            catch (COMException)
            {
                // A non-transient clipboard failure. Report it as an unsuccessful copy rather
                // than throwing into the commit path.
                return false;
            }
            catch (ExternalException)
            {
                return false;
            }
        }

        return false;
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
