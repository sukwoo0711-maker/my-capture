using System.Windows.Media.Imaging;

namespace MyCapture.App.Pinning;

/// <summary>The semantic kind retained behind a visually rendered pinned bitmap.</summary>
internal enum PinContentKind
{
    Image,
    Text,
    Table,
}

/// <summary>
/// A pin's immutable visual and, for clipboard text, the exact source string that produced it.
/// </summary>
/// <remarks>
/// Text and spreadsheet selections are rendered to a bitmap so they behave like every other
/// pin, but the original Unicode payload stays attached to the pin. This keeps source copying
/// independent of whatever replaces the system clipboard after the pin opens.
/// </remarks>
internal sealed record PinContent
{
    private PinContent(BitmapSource image, PinContentKind kind, string? originalText)
    {
        Image = image ?? throw new ArgumentNullException(nameof(image));
        Kind = kind;
        OriginalText = originalText;
    }

    internal BitmapSource Image { get; }

    internal PinContentKind Kind { get; }

    internal string? OriginalText { get; }

    internal bool HasOriginalText => OriginalText is not null;

    /// <summary>
    /// Rendered text is authored at 96 DPI and therefore uses one bitmap pixel per WPF DIP.
    /// Screen captures instead represent physical display pixels and use the target monitor's
    /// scale factor when their initial window size is calculated.
    /// </summary>
    internal bool UsesDeviceIndependentPixels => Kind is PinContentKind.Text or PinContentKind.Table;

    internal static PinContent FromImage(BitmapSource image) =>
        new(image, PinContentKind.Image, originalText: null);

    internal static PinContent FromText(BitmapSource image, string originalText, bool isTable)
    {
        ArgumentNullException.ThrowIfNull(originalText);
        return new(
            image,
            isTable ? PinContentKind.Table : PinContentKind.Text,
            originalText);
    }
}
