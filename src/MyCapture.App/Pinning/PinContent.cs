using System.Windows.Media.Imaging;
using MyCapture.Core.Pin;

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
/// Text and spreadsheet selections are shown as a bitmap so they behave like every other pin,
/// but the original Unicode payload stays attached to the pin. This keeps source copying
/// independent of whatever replaces the system clipboard after the pin opens. A table's bitmap
/// is the picture its source application published with the same copy when there is one (Excel
/// draws the cells with their real formatting); otherwise MyCapture renders the cell grid.
/// </remarks>
internal sealed record PinContent
{
    private PinContent(
        BitmapSource image,
        PinContentKind kind,
        string? originalText,
        string sourcePageUrl,
        bool usesDeviceIndependentPixels)
    {
        Image = image ?? throw new ArgumentNullException(nameof(image));
        Kind = kind;
        OriginalText = originalText;
        SourcePageUrl = sourcePageUrl;
        UsesDeviceIndependentPixels = usesDeviceIndependentPixels;
    }

    internal BitmapSource Image { get; }

    internal PinContentKind Kind { get; }

    internal string? OriginalText { get; }

    /// <summary>
    /// Source page URL carried by the clipboard PNG's <c>SourceURL</c> chunk, or empty when
    /// the copied image did not come from a MyCapture export. An F9 upload reads this to keep
    /// the origin link with the image.
    /// </summary>
    internal string SourcePageUrl { get; }

    internal bool HasOriginalText => OriginalText is not null;

    /// <summary>
    /// Text and tables MyCapture renders itself are authored at 96 DPI and therefore use one
    /// bitmap pixel per WPF DIP. Screen captures and pictures published by another application,
    /// including a spreadsheet's picture of its cells, represent display pixels and use the
    /// target monitor's scale factor when their initial window size is calculated.
    /// </summary>
    internal bool UsesDeviceIndependentPixels { get; }

    /// <summary>
    /// Estimated bytes this content keeps alive: the decoded bitmap plus retained strings.
    /// The closed-pin restore history budgets against this value.
    /// </summary>
    internal long EstimatedRetainedBytes =>
        ClosedPinMemory.EstimatePixelBytes(Image.PixelWidth, Image.PixelHeight, Image.Format.BitsPerPixel)
        + ClosedPinMemory.EstimateTextBytes(OriginalText)
        + ClosedPinMemory.EstimateTextBytes(SourcePageUrl);

    internal static PinContent FromImage(BitmapSource image, string? sourcePageUrl = null) =>
        new(image, PinContentKind.Image, originalText: null, sourcePageUrl ?? string.Empty, usesDeviceIndependentPixels: false);

    /// <summary>Rebuilds the immutable content retained in an opt-in saved pin session.</summary>
    internal static PinContent FromSession(
        BitmapSource image,
        PinContentKind kind,
        string? originalText,
        string sourcePageUrl,
        bool usesDeviceIndependentPixels) =>
        new(image, kind, originalText, sourcePageUrl, usesDeviceIndependentPixels);

    /// <summary>Text or a table that MyCapture rendered itself from the retained source string.</summary>
    internal static PinContent FromText(BitmapSource image, string originalText, bool isTable)
    {
        ArgumentNullException.ThrowIfNull(originalText);
        return new(
            image,
            isTable ? PinContentKind.Table : PinContentKind.Text,
            originalText,
            sourcePageUrl: string.Empty,
            usesDeviceIndependentPixels: true);
    }

    /// <summary>
    /// A table shown with the picture its source application published for the same copy. The
    /// cell text stays attached, so the pin copies the original table rather than offering OCR.
    /// </summary>
    internal static PinContent FromTablePicture(BitmapSource picture, string originalText)
    {
        ArgumentNullException.ThrowIfNull(originalText);
        return new(
            picture,
            PinContentKind.Table,
            originalText,
            sourcePageUrl: string.Empty,
            usesDeviceIndependentPixels: false);
    }
}
