using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Pinning;
using MyCapture.App.Threading;
using MyCapture.Core.Capture;
using MyCapture.Core.Pin;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class ClipboardMarkdownDeliveryTests
{
    [Theory]
    [InlineData("# Title")]
    [InlineData("**Ready**")]
    [InlineData("Read [the guide](https://example.com).")]
    public async Task ExplicitLanguageSignalRendersOtherwiseAmbiguousText(string source)
    {
        var captured = await Capture(source, "<pre class='language-markdown'>content</pre>");
        var completed = await ClipboardImageReader.CompletePinAttemptAsync(captured, _ => null,
            (_, _) => throw new InvalidOperationException("Explicit Markdown uses its renderer."),
            _ => throw new InvalidOperationException("No clipboard reread is needed."));
        Assert.True(completed.Content!.IsMarkdown);
        Assert.Equal(source, completed.Content.OriginalText);
    }

    [Theory]
    [InlineData("# A single comment")]
    [InlineData("**Ready**")]
    [InlineData("Read [the guide](https://example.com).")]
    public async Task AmbiguousPlainTextRetainsPlainStyle(string source)
    {
        var captured = await Capture(source, null);
        var completed = await ClipboardImageReader.CompletePinAttemptAsync(captured, _ => null,
            (text, table) => StaThreadTask.RunAsync(() => ClipboardTextRenderer.Render(text, table)),
            _ => throw new InvalidOperationException("No clipboard reread is needed."),
            _ => throw new InvalidOperationException("Inline syntax alone must not select Markdown."));
        Assert.Equal(PinTextStyle.PlainText, completed.Content!.TextStyle);
        Assert.Equal(source, completed.Content.OriginalText);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("<div># Release<br><br><b>Ready</b></div>")]
    [InlineData("<pre><code class='language-markdown'># Release\n\n**Ready**</code></pre>")]
    [InlineData("<div data-language='md' style='white-space:pre'># Release\n\n**Ready**</div>")]
    public async Task PlainAndExplicitBrowserMarkdown_RenderWithoutAnotherAction(string? html)
    {
        const string source = "# Release\n\n**Ready**";
        var captured = await Capture(source, html);
        var completed = await ClipboardImageReader.CompletePinAttemptAsync(captured,
            _ => throw new InvalidOperationException("No image decoding is needed."),
            (_, _) => throw new InvalidOperationException("Markdown should use its renderer."),
            _ => throw new InvalidOperationException("No clipboard reread is needed."));
        Assert.True(completed.Content!.IsMarkdown);
        Assert.Equal(PinTextStyle.Markdown, completed.Content.TextStyle);
        Assert.Equal(source, completed.Content.OriginalText);
        Assert.True(completed.Content.CanRerender);
        Assert.True(completed.Content.Image.IsFrozen);
    }

    [Theory]
    [InlineData("<pre style='background-color:#123456'><code class='language-python'># comment\nprint(1)</code></pre>")]
    [InlineData("<div style='background-color:#123456;white-space:pre'># comment\nprint(1)</div>")]
    [InlineData("<pre style='background-color:#123456'># comment\nprint(1)</pre>")]
    public async Task AmbiguousOrPythonCode_KeepsExistingCodeStyle(string html)
    {
        const string source = "# comment\nprint(1)";
        var captured = await Capture(source, html);
        var completed = await ClipboardImageReader.CompletePinAttemptAsync(captured,
            _ => throw new InvalidOperationException("Code preview already exists."),
            (_, _) => throw new InvalidOperationException("Keep the code renderer."),
            _ => throw new InvalidOperationException("No clipboard reread is needed."),
            _ => throw new InvalidOperationException("A comment is not an explicit Markdown container."));
        Assert.False(completed.Content!.IsMarkdown);
        Assert.Equal(PinTextStyle.Code, completed.Content.TextStyle);
        Assert.Same(captured.CodePreview, completed.Content.Image);
        Assert.Equal("#123456", completed.Content.CodeDocument!.Background);
        Assert.Equal(source, completed.Content.OriginalText);
    }

    [Fact]
    public async Task FormattingFailure_KeepsExactPlainSource()
    {
        const string source = "# Release\n\n**Ready**";
        var captured = await Capture(source, "<pre class='language-md'># Release\n\n**Ready**</pre>");
        var result = await ClipboardImageReader.CompletePinAttemptAsync(captured, _ => null,
            (text, table) => StaThreadTask.RunAsync(() => ClipboardTextRenderer.Render(text, table)),
            _ => Task.FromResult(ClipboardImageReader.CapturedAttempt.NoImage()),
            _ => throw new FormatException("unsupported"));
        Assert.Equal(source, result.Content!.OriginalText);
        Assert.Equal(PinTextStyle.PlainText, result.Content.TextStyle);
    }

    [Fact]
    public async Task MarkdownContent_RetainsStyleProvenanceAndDpiAcrossSessionRestore() => await StaThreadTask.RunAsync(() =>
    {
        const string source = "# Release\r\n\r\n**Ready**";
        CaptureProvenance provenance = CaptureProvenance.Create(url: "https://example.test/release");
        PinContent content = PinContent.FromMarkdown(ClipboardMarkdownRenderer.Render(source), source, provenance);
        PinContent restored = PinContent.FromSession(content.Image, content.Kind, content.OriginalText,
            content.SourcePageUrl, content.UsesDeviceIndependentPixels, content.TextStyle, content.CodeDocument, content.Provenance);
        PinContent scaled = restored.RenderedAt(1.25).RenderedAt(2).RenderedAt(1.5);
        Assert.True(restored.IsMarkdown);
        Assert.True(scaled.CanRerender);
        Assert.Equal(source, scaled.OriginalText);
        Assert.Same(provenance, scaled.Provenance);
        Assert.Equal(content.LayoutWidthDip, scaled.LayoutWidthDip);
        Assert.Equal(content.LayoutHeightDip, scaled.LayoutHeightDip);
        Assert.Equal(144, scaled.Image.DpiX);
        Assert.True(scaled.Image.IsFrozen);
        return true;
    });

    [Fact]
    public async Task UpdatingProvenanceKeepsPendingLookupForRestoredSource() => await StaThreadTask.RunAsync(() =>
    {
        const string source = "# Release\n**Ready**";
        var pending = new TaskCompletionSource<CaptureProvenance>(TaskCreationOptions.RunContinuationsAsynchronously);
        PinContent original = PinContent.FromMarkdown(ClipboardMarkdownRenderer.Render(source), source)
            with { PendingProvenance = pending.Task };
        CaptureProvenance interim = CaptureProvenance.Create(url: "https://example.test/interim");
        PinContent snapshot = original.WithProvenance(interim);
        Assert.Same(original.Image, snapshot.Image);
        Assert.Same(pending.Task, snapshot.PendingProvenance);
        Assert.False(snapshot.PendingProvenance!.IsCompleted);
        PinContent restoredAtDpi = snapshot.RenderedAt(1.25);
        Assert.Same(pending.Task, restoredAtDpi.PendingProvenance);
        Assert.Equal(source, restoredAtDpi.OriginalText);
        Assert.Same(interim, restoredAtDpi.Provenance);
        CaptureProvenance resolved = CaptureProvenance.Create(url: "https://example.test/resolved");
        pending.SetResult(resolved);
        Assert.Same(resolved, restoredAtDpi.PendingProvenance!.GetAwaiter().GetResult());
        return true;
    });

    [Fact]
    public async Task MarkdownSelection_ShowsFormattingAndSelectAllRetainsOriginal() => await StaThreadTask.RunAsync(() =>
    {
        const string source = "# Release\r\n\r\n**Ready**\r\n\r\n```ts\r\nconst timeoutMs = 5000;\r\n```";
        var view = new PinTextSelectionView(PinContent.FromMarkdown(ClipboardMarkdownRenderer.Render(source), source));
        RichTextBox text = Assert.IsType<RichTextBox>(view.MarkdownBox);
        Assert.Null(view.TextBox);
        Assert.Null(view.SelectedOriginalText);
        Assert.True(text.IsReadOnly);
        Assert.Contains("Ready", new TextRange(text.Document.ContentStart, text.Document.ContentEnd).Text);
        Assert.DoesNotContain("**Ready**", new TextRange(text.Document.ContentStart, text.Document.ContentEnd).Text);
        Paragraph heading = Assert.IsType<Paragraph>(text.Document.Blocks.FirstBlock);
        text.Selection.Select(heading.ContentStart, heading.ContentEnd);
        Assert.Equal("Release", view.SelectedOriginalText);
        view.SelectAll();
        Assert.Equal(source, view.SelectedOriginalText);
        text.Selection.Select(heading.ContentStart, heading.ContentEnd);
        Assert.Equal("Release", view.SelectedOriginalText);
        text.SelectAll();
        Assert.Equal(source, view.SelectedOriginalText);
        view.SetZoom(1.5);
        Assert.Equal(1.5, Assert.IsType<ScaleTransform>(text.LayoutTransform).ScaleX);
        Assert.Equal(source, view.SelectedOriginalText);
        return true;
    });

    private static Task<ClipboardImageReader.CapturedAttempt> Capture(string text, string? html) => StaThreadTask.RunAsync(() =>
    {
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, text);
        if (html is not null) data.SetData(DataFormats.Html, html);
        return ClipboardImageReader.CaptureFrom(data, 41, () => null);
    }, "Markdown clipboard fixture");
}
