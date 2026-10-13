using System.Runtime.InteropServices;

namespace MyCapture.App.Pinning;

/// <summary>
/// Whether the clipboard holds something "Pin clipboard" can show, asked without opening the
/// clipboard: the tray menu checks it every time it opens.
/// </summary>
/// <remarks>
/// <c>IsClipboardFormatAvailable</c> reads the format list the clipboard owner announced; it does
/// not open the clipboard, render delayed data or block on another process, so it costs a few
/// microseconds. It can only say "maybe": a format that is announced but fails to render is still
/// reported by the pin command when it runs.
/// </remarks>
internal static class ClipboardAvailability
{
    private const uint CfText = 1, CfBitmap = 2, CfDib = 8, CfUnicodeText = 13, CfDibV5 = 17;
    private static readonly Lazy<uint> PngFormat = new(() => RegisterClipboardFormat("PNG"));

    /// <summary>Replaces the native check in tests.</summary>
    internal static Func<bool>? OverrideForTest { get; set; }

    /// <summary>True when the clipboard announces an image (PNG or bitmap) or text.</summary>
    internal static bool HasPinnableContent()
    {
        if (OverrideForTest is { } overridden) return overridden();
        try
        {
            if (IsClipboardFormatAvailable(CfUnicodeText) || IsClipboardFormatAvailable(CfText)
                || IsClipboardFormatAvailable(CfDibV5) || IsClipboardFormatAvailable(CfDib)
                || IsClipboardFormatAvailable(CfBitmap)) return true;
            uint png = PngFormat.Value;
            return png != 0 && IsClipboardFormatAvailable(png);
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            // Without the check the command stays available; running it reports an empty clipboard.
            return true;
        }
    }

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern uint RegisterClipboardFormat(string format);
}
