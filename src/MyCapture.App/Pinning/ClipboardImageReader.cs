using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using MyCapture.Core.Pin;

namespace MyCapture.App.Pinning;

/// <summary>
/// Reads an image off the Windows clipboard, cloning and freezing it so it is safe to
/// hand to a long-lived pinned window on any thread.
/// </summary>
/// <remarks>
/// <para>
/// The clipboard is a single shared OS resource. <c>OpenClipboard</c> fails with
/// <c>CLIPBRD_E_CANT_OPEN</c> (HRESULT 0x800401D0) whenever another process is mid-copy
/// or mid-paste, which is transient and common. The read is retried a bounded number of
/// times with a short back-off; if the whole window elapses the caller is told the
/// clipboard was <see cref="ClipboardImageStatus.Busy"/> rather than shown a hard error.
/// </para>
/// <para>
/// The result is split into a pure <see cref="ClipboardImageOutcome"/> (which drives the
/// caller's branch — pin / no-image balloon / busy balloon) and the decoded
/// <see cref="BitmapSource"/>, so the decision logic stays testable without a clipboard.
/// </para>
/// </remarks>
internal static class ClipboardImageReader
{
    private const uint ClipboardCantOpen = 0x800401D0;
    private const int MaxAttempts = 10;
    private const int RetryDelayMs = 40;

    /// <summary>
    /// Attempts to read a frozen image from the clipboard. Must be called on the STA
    /// dispatcher thread that owns the WPF clipboard.
    /// </summary>
    internal static (ClipboardImageOutcome Outcome, BitmapSource? Image) Read()
    {
        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                if (!Clipboard.ContainsImage())
                {
                    return (ClipboardImageOutcome.NoImage(), null);
                }

                BitmapSource? source = Clipboard.GetImage();
                if (source is null)
                {
                    return (ClipboardImageOutcome.NoImage(), null);
                }

                BitmapSource frozen = Freeze(source);
                return (
                    ClipboardImageOutcome.Success(frozen.PixelWidth, frozen.PixelHeight),
                    frozen);
            }
            catch (COMException ex) when ((uint)ex.HResult == ClipboardCantOpen && attempt < MaxAttempts)
            {
                // Another process holds the clipboard open; back off briefly and retry.
                Thread.Sleep(RetryDelayMs);
            }
            catch (COMException)
            {
                // A non-transient clipboard failure: report busy and let the caller fail soft.
                return (ClipboardImageOutcome.Busy(), null);
            }
            catch (ExternalException)
            {
                return (ClipboardImageOutcome.Busy(), null);
            }
        }

        return (ClipboardImageOutcome.Busy(), null);
    }

    private static BitmapSource Freeze(BitmapSource source)
    {
        // Clone so we own an independent copy: the clipboard's own BitmapSource may be tied
        // to data that changes on the next copy. Freezing makes it cross-thread-safe and
        // cheap to render repeatedly in the pin.
        BitmapSource copy = source.Clone();
        copy.Freeze();
        return copy;
    }
}
