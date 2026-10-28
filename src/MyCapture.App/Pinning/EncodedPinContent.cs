using System.IO;
using MyCapture.Core.Capture;
using MyCapture.Core.Pin;
using MyCapture.Platform.Imaging;

namespace MyCapture.App.Pinning;

/// <summary>A closed pin's lossless image and semantic metadata, with no decoded bitmap reference.</summary>
internal sealed record EncodedPinContent(
    byte[] Png,
    PinContentKind Kind,
    string? OriginalText,
    string SourcePageUrl,
    bool UsesDeviceIndependentPixels,
    PinTextStyle? TextStyle,
    PinCodeDocument? CodeDocument,
    double LayoutWidthDip,
    double LayoutHeightDip,
    double RasterDpiX,
    double RasterDpiY,
    CaptureProvenance Provenance,
    Task<CaptureProvenance>? PendingProvenance,
    long MetadataBytes)
{
    internal long RetainedBytes => Png.LongLength + MetadataBytes;

    internal static EncodedPinContent Encode(PinContent content, byte[]? existingPng = null) => new(
        existingPng ?? content.EncodedPng ?? ImageCodec.EncodePng(content.Image, content.SourcePageUrl),
        content.Kind, content.OriginalText, content.SourcePageUrl, content.UsesDeviceIndependentPixels,
        content.TextStyle, content.CodeDocument, content.LayoutWidthDip, content.LayoutHeightDip,
        content.RasterDpiX, content.RasterDpiY, content.Provenance,
        content.PendingProvenance,
        content.EstimatedRetainedBytes - ClosedPinMemory.EstimatePixelBytes(
            content.Image.PixelWidth, content.Image.PixelHeight, content.Image.Format.BitsPerPixel));

    internal PinContent Decode()
    {
        var image = ImageCodec.TryDecodePng(Png) ?? throw new InvalidDataException("The closed pin PNG could not be decoded.");
        return PinContent.FromSession(image, Kind, OriginalText, SourcePageUrl, UsesDeviceIndependentPixels,
            TextStyle, CodeDocument, Provenance, LayoutWidthDip, LayoutHeightDip)
            with
            {
                EncodedPng = Png, PendingProvenance = PendingProvenance,
                RasterDpiX = RasterDpiX, RasterDpiY = RasterDpiY,
            };
    }
}
