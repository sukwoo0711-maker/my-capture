using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Pinning;
using MyCapture.App.Threading;
using MyCapture.Core.Pin;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PinContentDpiTests
{
    [Fact]
    public async Task TranslatedText_KeepsCleanBackgroundAfterProvenanceAndDpiChanges()
    {
        await StaThreadTask.RunAsync(() =>
        {
            const string text = "창을 닫기 전에 저장합니다.";
            PinContent content = PinContent.FromText(ClipboardTextRenderer.RenderTranslation(text), text, false)
                with { BorderlessText = true };
            PinContent rendered = content.WithProvenance(content.Provenance).RenderedAt(2);
            byte[] corner = new byte[4];
            rendered.Image.CopyPixels(new Int32Rect(0, 0, 1, 1), corner, 4, 0);
            Assert.Equal(new byte[] { 255, 255, 255, 255 }, corner);
            Assert.Equal(content.LayoutWidthDip, rendered.LayoutWidthDip);
            Assert.Equal(content.LayoutHeightDip, rendered.LayoutHeightDip);
            Assert.Equal(text, rendered.OriginalText);
            return true;
        }, "translated pin DPI background test");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RenderedText_RetainsSourceAndCanonicalDimensionsWithActualPixelBudget(bool table)
    {
        await StaThreadTask.RunAsync(() =>
        {
            string source = table ? "이름\t값\r\nalpha\t123" : "A source note.\r\nExact original text.";
            PinContent original = PinContent.FromText(ClipboardTextRenderer.Render(source, table), source, table);
            PinContent rendered = original.RenderedAt(2);
            Assert.True(original.CanRerender);
            Assert.Equal(table ? PinTextStyle.Grid : PinTextStyle.PlainText, original.TextStyle);
            Assert.Same(original, original.RenderedAt(1));
            Assert.NotSame(original.Image, rendered.Image);
            Assert.Equal(original.Kind, rendered.Kind);
            Assert.Same(original.OriginalText, rendered.OriginalText);
            Assert.Equal(original.SourcePageUrl, rendered.SourcePageUrl);
            Assert.Equal(original.LayoutWidthDip, rendered.LayoutWidthDip);
            Assert.Equal(original.LayoutHeightDip, rendered.LayoutHeightDip);
            Assert.Equal(original.Image.PixelWidth * 2, rendered.Image.PixelWidth);
            Assert.Equal(original.Image.PixelHeight * 2, rendered.Image.PixelHeight);
            Assert.Equal(original.Image.PixelWidth * (long)original.Image.PixelHeight * 4 * 3,
                rendered.EstimatedRetainedBytes - original.EstimatedRetainedBytes);
            Assert.Same(rendered, rendered.RenderedAt(2));
            return true;
        }, "pin content native DPI test");
    }

    [Fact]
    public async Task CodeContent_RetainsDocumentAndExactOriginalAcrossRepeatedDpiChanges()
    {
        await StaThreadTask.RunAsync(() =>
        {
            const string original = "\treturn value;\r\n";
            PinCodeDocument document = Assert.IsType<PinCodeDocument>(ClipboardCodeRenderer.TryParse(original,
                "<pre style=\"background-color:#123456\"><span style=\"color:#FF0000\">\treturn</span> value;\r\n</pre>"));
            PinContent content = PinContent.FromCode(ClipboardCodeRenderer.Render(document), original, document);
            PinContent rendered = content.RenderedAt(1.25).RenderedAt(2).RenderedAt(1.5);
            Assert.Equal(PinTextStyle.Code, rendered.TextStyle);
            Assert.Equal(PinContentKind.Text, rendered.Kind);
            Assert.Equal(original, rendered.OriginalText);
            Assert.Same(document, rendered.CodeDocument);
            Assert.Equal(content.LayoutWidthDip, rendered.LayoutWidthDip);
            Assert.Equal(content.LayoutHeightDip, rendered.LayoutHeightDip);
            Assert.Equal(144, rendered.Image.DpiX);
            Assert.True(rendered.Image.IsFrozen);
            return true;
        }, "pin code retained document DPI test");
    }

    [Fact]
    public void ImageTablePictureAndLegacySession_AreNeverRerendered()
    {
        BitmapSource image = Image();
        PinContent[] contents =
        [
            PinContent.FromImage(image, "https://example.test/source"),
            PinContent.FromTablePicture(image, "a\tb"),
            PinContent.FromSession(image, PinContentKind.Text, "old text", string.Empty, true),
            PinContent.FromSession(image, PinContentKind.Table, "old\ttable", string.Empty, true),
        ];
        foreach (PinContent content in contents)
        {
            Assert.False(content.CanRerender);
            Assert.Null(content.TextStyle);
            Assert.Null(content.CodeDocument);
            Assert.Same(content, content.RenderedAt(2));
            Assert.Same(image, content.Image);
        }
    }

    [Fact]
    public async Task SessionMetadata_EnablesCodeRerenderingAndPreservesSourcePage()
    {
        await StaThreadTask.RunAsync(() =>
        {
            PinCodeDocument document = Assert.IsType<PinCodeDocument>(
                ClipboardCodeRenderer.TryParse("hello", "<pre style=\"color:#FF0000\">hello</pre>"));
            PinContent restored = PinContent.FromSession(ClipboardCodeRenderer.Render(document),
                PinContentKind.Text, "hello", "https://example.test/source", true, PinTextStyle.Code, document);
            PinContent rendered = restored.RenderedAt(2);
            Assert.True(restored.CanRerender);
            Assert.Equal(192, rendered.Image.DpiX);
            Assert.Equal("https://example.test/source", rendered.SourcePageUrl);
            Assert.Same(document, rendered.CodeDocument);
            Assert.Equal(restored.LayoutWidthDip, rendered.LayoutWidthDip);
            Assert.Equal(restored.LayoutHeightDip, rendered.LayoutHeightDip);
            return true;
        }, "restored code content DPI test");
    }

    [Fact]
    public async Task ClipboardCapture_UsesParsedCodeMetadataWithoutRereadingTheClipboard()
    {
        ClipboardImageReader.CapturedAttempt captured = await StaThreadTask.RunAsync(() =>
        {
            var data = new DataObject();
            data.SetData(DataFormats.UnicodeText, "hello");
            data.SetData(DataFormats.Html, "<pre style=\"color:#FF0000\">hello</pre>");
            return ClipboardImageReader.CaptureFrom(data, 7, () => null);
        }, "in-memory code clipboard test");

        ClipboardImageReader.PinReadAttempt result = await ClipboardImageReader.CompletePinAttemptAsync(
            captured,
            _ => throw new InvalidOperationException("Code does not need image decoding."),
            (_, _) => throw new InvalidOperationException("Code does not use the plain text renderer."),
            _ => throw new InvalidOperationException("Code does not reread the clipboard."));
        PinContent content = Assert.IsType<PinContent>(result.Content);
        Assert.Equal(PinTextStyle.Code, content.TextStyle);
        Assert.True(content.CanRerender);
        Assert.Same(captured.CodePreview, content.Image);
        Assert.Same(captured.CodeDocument, content.CodeDocument);
    }

    private static BitmapSource Image()
    {
        BitmapSource image = BitmapSource.Create(8, 6, 96, 96, PixelFormats.Bgra32, null, new byte[8 * 6 * 4], 8 * 4);
        image.Freeze();
        return image;
    }
}
