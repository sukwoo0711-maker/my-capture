using System.Windows.Media.Imaging;
using MyCapture.Core.Capture;
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
        bool usesDeviceIndependentPixels,
        PinTextStyle? textStyle = null,
        PinCodeDocument? codeDocument = null,
        double? layoutWidthDip = null,
        double? layoutHeightDip = null,
        CaptureProvenance? provenance = null)
    {
        Image = image ?? throw new ArgumentNullException(nameof(image));
        RasterDpiX = image.DpiX;
        RasterDpiY = image.DpiY;
        Kind = kind;
        OriginalText = originalText;
        SourcePageUrl = sourcePageUrl;
        Provenance = provenance ?? CaptureProvenance.Create(url: sourcePageUrl);
        UsesDeviceIndependentPixels = usesDeviceIndependentPixels;
        TextStyle = textStyle;
        CodeDocument = codeDocument;
        LayoutWidthDip = layoutWidthDip ?? image.Width;
        LayoutHeightDip = layoutHeightDip ?? image.Height;
    }

    internal BitmapSource Image { get; }

    /// <summary>The exact PNG used to decode this image, when already available.</summary>
    internal byte[]? EncodedPng { get; init; }

    // PNG's integer pixels-per-meter resolution rounds the original DPI. Keep the precise
    // raster scale so a same-monitor reopen does not mistake that rounding for a DPI change.
    internal double RasterDpiX { get; init; }
    internal double RasterDpiY { get; init; }

    internal PinContentKind Kind { get; }

    internal string? OriginalText { get; }

    /// <summary>
    /// Source page URL carried by the clipboard PNG's <c>SourceURL</c> chunk, or empty when
    /// the copied image did not come from a MyCapture export. An F9 upload reads this to keep
    /// the origin link with the image.
    /// </summary>
    internal string SourcePageUrl { get; }

    internal CaptureProvenance Provenance { get; }

    /// <summary>The source lookup detached from this pin's clipboard token, if still pending.</summary>
    internal Task<CaptureProvenance>? PendingProvenance { get; init; }

    internal PinContent WithProvenance(CaptureProvenance provenance) =>
        new(Image, Kind, OriginalText, SourcePageUrl, UsesDeviceIndependentPixels,
            TextStyle, CodeDocument, LayoutWidthDip, LayoutHeightDip, provenance)
        { EncodedPng = EncodedPng, RasterDpiX = RasterDpiX, RasterDpiY = RasterDpiY };

    internal bool HasOriginalText => OriginalText is not null;

    internal PinTextStyle? TextStyle { get; }

    internal PinCodeDocument? CodeDocument { get; }

    /// <summary>Exact layout size, independent of outward rounding to integer render pixels.</summary>
    internal double LayoutWidthDip { get; }

    internal double LayoutHeightDip { get; }

    internal bool CanRerender => UsesDeviceIndependentPixels && OriginalText is not null
        && (TextStyle is PinTextStyle.PlainText or PinTextStyle.Grid
            || (TextStyle == PinTextStyle.Code && CodeDocument is not null));

    /// <summary>
    /// Text and tables MyCapture renders itself retain a fixed DIP layout, independently of
    /// the render target's pixel density. Screen captures and pictures published by another application,
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
        + ClosedPinMemory.EstimateTextBytes(SourcePageUrl)
        + ClosedPinMemory.EstimateTextBytes(Provenance.Url)
        + ClosedPinMemory.EstimateTextBytes(Provenance.DocumentPath)
        + ClosedPinMemory.EstimateTextBytes(Provenance.AppName)
        + ClosedPinMemory.EstimateTextBytes(Provenance.AppExecutablePath)
        + ClosedPinMemory.EstimateTextBytes(Provenance.WindowTitle)
        + ClosedPinMemory.EstimateTextBytes(Provenance.DisplayText)
        + ClosedPinMemory.EstimateTextBytes(Provenance.BadgeText)
        + ClosedPinMemory.EstimateTextBytes(Provenance.Tooltip)
        + ClosedPinMemory.EstimateTextBytes(Provenance.SearchQuery)
        + (CodeDocument is null ? 0
            : ClosedPinMemory.EstimateTextBytes(CodeDocument.DisplayText)
                + ClosedPinMemory.EstimateTextBytes(CodeDocument.Background)
                + ClosedPinMemory.EstimateTextBytes(CodeDocument.Foreground)
                + CodeDocument.Runs.Sum(static run => 32L + ClosedPinMemory.EstimateTextBytes(run.Color)));

    internal static PinContent FromImage(BitmapSource image, string? sourcePageUrl = null, CaptureProvenance? provenance = null) =>
        new(image, PinContentKind.Image, originalText: null, sourcePageUrl ?? string.Empty,
            usesDeviceIndependentPixels: false, provenance: provenance);

    /// <summary>Rebuilds the immutable content retained in an opt-in saved pin session.</summary>
    internal static PinContent FromSession(
        BitmapSource image,
        PinContentKind kind,
        string? originalText,
        string sourcePageUrl,
        bool usesDeviceIndependentPixels,
        PinTextStyle? textStyle = null,
        PinCodeDocument? codeDocument = null,
        CaptureProvenance? provenance = null,
        double? layoutWidthDip = null,
        double? layoutHeightDip = null) =>
        new(image, kind, originalText, sourcePageUrl, usesDeviceIndependentPixels, textStyle, codeDocument,
            layoutWidthDip, layoutHeightDip, provenance);

    /// <summary>Text or a table that MyCapture rendered itself from the retained source string.</summary>
    internal static PinContent FromText(BitmapSource image, string originalText, bool isTable, CaptureProvenance? provenance = null)
    {
        ArgumentNullException.ThrowIfNull(originalText);
        return new(
            image,
            isTable ? PinContentKind.Table : PinContentKind.Text,
            originalText,
            sourcePageUrl: string.Empty,
            usesDeviceIndependentPixels: true,
            textStyle: isTable ? PinTextStyle.Grid : PinTextStyle.PlainText,
            provenance: provenance);
    }

    internal static PinContent FromCode(BitmapSource image, string originalText, PinCodeDocument document, CaptureProvenance? provenance = null)
    {
        ArgumentNullException.ThrowIfNull(originalText);
        ArgumentNullException.ThrowIfNull(document);
        if (!document.IsValid() || !document.MatchesOriginalText(originalText))
        {
            throw new ArgumentException("The code document must describe the source text.", nameof(document));
        }
        return new(image, PinContentKind.Text, originalText, sourcePageUrl: string.Empty,
            usesDeviceIndependentPixels: true, textStyle: PinTextStyle.Code, codeDocument: document, provenance: provenance);
    }

    /// <summary>Re-rasterises only locally authored text, retaining no reference to the old bitmap.</summary>
    internal PinContent RenderedAt(double scale)
    {
        if (!CanRerender) return this;
        ClipboardTextRenderer.ValidateScale(scale);
        if (Math.Abs(RasterDpiX - (96 * scale)) < 0.000001
            && Math.Abs(RasterDpiY - (96 * scale)) < 0.000001)
        {
            return this;
        }

        BitmapSource image = TextStyle == PinTextStyle.Code
            ? ClipboardCodeRenderer.Render(CodeDocument!, scale)
            : ClipboardTextRenderer.Render(OriginalText!, TextStyle == PinTextStyle.Grid, scale);
        return new(image, Kind, OriginalText, SourcePageUrl, UsesDeviceIndependentPixels,
            TextStyle, CodeDocument, LayoutWidthDip, LayoutHeightDip, Provenance)
        {
            PendingProvenance = PendingProvenance,
        };
    }

    /// <summary>
    /// A table shown with the picture its source application published for the same copy. The
    /// cell text stays attached, so the pin copies the original table rather than offering OCR.
    /// </summary>
    internal static PinContent FromTablePicture(BitmapSource picture, string originalText, CaptureProvenance? provenance = null)
    {
        ArgumentNullException.ThrowIfNull(originalText);
        return new(
            picture,
            PinContentKind.Table,
            originalText,
            sourcePageUrl: string.Empty,
            usesDeviceIndependentPixels: false,
            provenance: provenance);
    }
}
