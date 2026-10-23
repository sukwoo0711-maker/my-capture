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
    private static Func<bool>? _testOverride;

    internal static bool IsTestOverrideInstalled => Volatile.Read(ref _testOverride) is not null;

    // Installed before product code runs. Tests cannot reset this and accidentally resume
    // native clipboard queries; the delegate overload below remains available for fixtures.
    internal static void InstallTestOverride(Func<bool> hasPinnableContent)
    {
        ArgumentNullException.ThrowIfNull(hasPinnableContent);
        if (Interlocked.CompareExchange(ref _testOverride, hasPinnableContent, null) is not null)
            throw new InvalidOperationException("The clipboard availability test override is already installed for this process.");
    }

    /// <summary>True when the clipboard announces an image (PNG or bitmap) or text.</summary>
    internal static bool HasPinnableContent() =>
        Volatile.Read(ref _testOverride)?.Invoke()
        ?? HasPinnableContent(IsClipboardFormatAvailable, static () => PngFormat.Value);

    internal static bool HasPinnableContent(Func<uint, bool> hasFormat, Func<uint> getPngFormat)
    {
        try
        {
            if (hasFormat(CfUnicodeText) || hasFormat(CfText)
                || hasFormat(CfDibV5) || hasFormat(CfDib)
                || hasFormat(CfBitmap)) return true;
            uint png = getPngFormat();
            return png != 0 && hasFormat(png);
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
