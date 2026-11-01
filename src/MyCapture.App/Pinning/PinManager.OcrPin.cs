using System.Net;
using System.Windows;
using MyCapture.Core.Capture;
using MyCapture.Ocr;

namespace MyCapture.App.Pinning;

internal sealed partial class PinManager
{
    internal async Task<PinWindow> PinOcrAsync(string text, OcrExtractionMode kind, CaptureProvenance? provenance = null)
    {
        PinContent content = await CreateOcrContentAsync(text, kind, provenance);
        return OpenPin(content, activate: true);
    }

    internal static async Task<PinContent> CreateOcrContentAsync(string text, OcrExtractionMode kind,
        CaptureProvenance? provenance = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (kind is not (OcrExtractionMode.Table or OcrExtractionMode.Code or OcrExtractionMode.Text))
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (kind == OcrExtractionMode.Code && OcrExtractionAnalyzer.Analyze(
                OcrResult.Success(text, string.Empty, [], TimeSpan.Zero)).Code.Language is not null)
        {
            // Reuse the clipboard code capture and completion pipeline with an in-memory
            // data object. No read or write of the system clipboard is involved.
            var data = new DataObject();
            data.SetData(DataFormats.UnicodeText, text);
            data.SetData(DataFormats.Html, "<pre>" + WebUtility.HtmlEncode(text) + "</pre>");
            ClipboardImageReader.CapturedAttempt captured = ClipboardImageReader.CaptureFrom(data, 0, () => null)
                with { Provenance = provenance };
            ClipboardImageReader.PinReadAttempt result = await ClipboardImageReader.CompletePinAttemptAsync(
                captured, _ => null,
                (value, table) => Task.FromResult(ClipboardTextRenderer.Render(value, table)),
                _ => Task.FromResult(ClipboardImageReader.CapturedAttempt.NoImage()));
            if (result.Content is { } code) return code;
        }
        bool isTable = kind == OcrExtractionMode.Table;
        return PinContent.FromText(ClipboardTextRenderer.Render(text, isTable), text, isTable, provenance);
    }
}
