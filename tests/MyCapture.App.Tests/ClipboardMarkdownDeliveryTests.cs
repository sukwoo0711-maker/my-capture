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
    [InlineData(null)]
    [InlineData("<div>Copied Markdown</div>")]
    [InlineData("<pre style='background-color:#123456'><code class='language-markdown'>Copied Markdown</code></pre>")]
    public async Task RequiredKoreanReleaseSampleRendersImmediatelyAndRetainsExactOriginal(string? html)
    {
        const string source = "# 배포 전 확인\r\n\r\n변경 내용을 **한눈에 확인**합니다.\r\n\r\n- [x] 빌드 확인\r\n- [ ] 최종 화면 확인\r\n\r\n> 원문은 그대로 보존합니다.\r\n\r\n| 항목 | 상태 |\r\n| --- | --- |\r\n| Markdown | 자동 렌더링 |\r\n| 코드 | 원문 유지 |\r\n\r\n```js\r\nconst timeout = 5000;\r\n```\r\n";
        Assert.True(ClipboardMarkdownRenderer.IsMarkdown(source));
        var captured = await Capture(source, html);
        var completed = await ClipboardImageReader.CompletePinAttemptAsync(captured, _ => null,
            (_, _) => throw new InvalidOperationException("The required sample must use Markdown."),
            _ => throw new InvalidOperationException("No clipboard reread is needed."));
        Assert.True(completed.Content!.IsMarkdown);
        Assert.Equal(source, completed.Content.OriginalText);
        Assert.True(completed.Content.Image.IsFrozen);
        if (captured.HasExplicitMarkdown) Assert.Null(captured.CodePreview);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("<pre class='language-mermaid'>flowchart TD</pre>")]
    public async Task PureMermaidUsesImmediatePlaceholderThroughTheMarkdownPipeline(string? html)
    {
        const string source = "flowchart TD\r\n A[복사] --> B[핀]\r\n";
        var captured = await Capture(source, html);
        var completed = await ClipboardImageReader.CompletePinAttemptAsync(captured, _ => null,
            (_, _) => throw new InvalidOperationException("Mermaid must use the diagram placeholder."),
            _ => throw new InvalidOperationException("No clipboard reread is needed."));
        Assert.True(completed.Content!.IsMarkdown);
        Assert.Equal(source, completed.Content.OriginalText);
        Assert.True(completed.Content.Image.IsFrozen);
        Assert.Equal(source, Assert.Single(ClipboardMarkdownRenderer.ExtractMermaidBlocks(source)).Source);
    }

    [Fact]
    public async Task ExcelTabularTextKeepsTheExistingTablePath()
    {
        const string source = "항목\t상태\r\nMarkdown\t완료\r\n";
        var captured = await Capture(source, "<table><tr><td>항목</td><td>상태</td></tr><tr><td>Markdown</td><td>완료</td></tr></table>");
        var completed = await ClipboardImageReader.CompletePinAttemptAsync(captured, _ => null,
            (text, table) =>
            {
                Assert.True(table);
                return StaThreadTask.RunAsync(() => ClipboardTextRenderer.Render(text, table));
            },
            _ => throw new InvalidOperationException("No clipboard reread is needed."),
            _ => throw new InvalidOperationException("Excel must keep the table renderer."));
        Assert.False(completed.Content!.IsMarkdown);
        Assert.Equal(source, completed.Content.OriginalText);
    }

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
    [InlineData(null)]
    [InlineData("<div># Release<br><br><b>Ready</b></div>")]
    [InlineData("<pre class='language-markdown'># Release\n\n**Ready**</pre>")]
    public async Task MarkdownSourceAndPng_RepinsAsMarkdownWithExactSource(string? html)
    {
        const string source = "# Release\r\n\r\n**Ready**\r\n\r\n- [x] 완료\r\n";
        var (captured, _) = await CaptureWithPng(source, html);
        Assert.NotEmpty(captured.PngBytes!);
        var completed = await ClipboardImageReader.CompletePinAttemptAsync(captured,
            _ => throw new InvalidOperationException("Retained Markdown must take priority over its PNG."),
            (_, _) => throw new InvalidOperationException("Markdown must use its renderer."),
            _ => throw new InvalidOperationException("No clipboard reread is needed."));
        Assert.True(completed.Content!.IsMarkdown);
        Assert.Equal(source, completed.Content.OriginalText);
        Assert.True(completed.Content.CanRerender);
        Assert.True(completed.Content.Image.IsFrozen);
    }

    [Theory]
    [InlineData("A photograph of the release team.")]
    [InlineData("# A single comment")]
    [InlineData("**Ready**")]
    public async Task PngAndOrdinaryOrAmbiguousCaption_RemainsAnImage(string source)
    {
        var (captured, picture) = await CaptureWithPng(source, null);
        var completed = await ClipboardImageReader.CompletePinAttemptAsync(captured, _ => picture,
            (_, _) => throw new InvalidOperationException("The copied picture must remain the visual."),
            _ => throw new InvalidOperationException("No clipboard reread is needed."),
            _ => throw new InvalidOperationException("A caption must not select Markdown."));
        Assert.Equal(PinContentKind.Image, completed.Content!.Kind);
        Assert.Same(picture, completed.Content.Image);
    }

    [Fact]
    public async Task ExcelPictureAndMarkdownLikeCellText_KeepsTablePictureAndExactSource()
    {
        const string source = "# Release\t상태\r\n**Ready**\t완료\r\n";
        var (captured, picture) = await CaptureWithPng(source,
            "<table><tr><td># Release</td><td>상태</td></tr><tr><td>**Ready**</td><td>완료</td></tr></table>");
        Assert.True(captured.IsTabularText);
        var completed = await ClipboardImageReader.CompletePinAttemptAsync(captured, _ => picture,
            (_, _) => throw new InvalidOperationException("Excel must retain its copied picture."),
            _ => throw new InvalidOperationException("No clipboard reread is needed."),
            _ => throw new InvalidOperationException("Excel cells must not select Markdown."));
        Assert.Equal(PinContentKind.Table, completed.Content!.Kind);
        Assert.False(completed.Content.IsMarkdown);
        Assert.Equal(source, completed.Content.OriginalText);
        Assert.Equal(picture.PixelWidth, completed.Content.Image.PixelWidth);
        Assert.Equal(picture.PixelHeight, completed.Content.Image.PixelHeight);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CodeHtmlWithPng_DoesNotBecomeMarkdownWhenSourceLooksLikeMarkdown(bool previewUnavailable)
    {
        const string source = "# Release\n\n**Ready**";
        var (captured, picture) = await CaptureWithPng(source,
            "<pre style='background-color:#123456'><code class='language-python'># Release\n\n**Ready**</code></pre>");
        Assert.True(captured.HasCodeHtml);
        Assert.NotNull(captured.CodePreview);
        if (previewUnavailable) captured = captured with { CodePreview = null, CodeDocument = null };
        var completed = await ClipboardImageReader.CompletePinAttemptAsync(captured, _ => picture,
            (_, _) => throw new InvalidOperationException("Use the code preview or picture."),
            _ => throw new InvalidOperationException("No clipboard reread is needed."),
            _ => throw new InvalidOperationException("Explicit code HTML must not select Markdown."));
        Assert.False(completed.Content!.IsMarkdown);
        if (previewUnavailable)
        {
            Assert.Equal(PinContentKind.Image, completed.Content.Kind);
            Assert.Same(picture, completed.Content.Image);
        }
        else
        {
            Assert.Equal(PinTextStyle.Code, completed.Content.TextStyle);
            Assert.Same(captured.CodePreview, completed.Content.Image);
            Assert.Equal(source, completed.Content.OriginalText);
        }
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

    private static Task<(ClipboardImageReader.CapturedAttempt Captured, BitmapSource Picture)> CaptureWithPng(
        string text, string? html) => StaThreadTask.RunAsync(() =>
    {
        BitmapSource picture = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { 30, 60, 90, 255, 90, 60, 30, 255, 20, 40, 80, 255, 80, 40, 20, 255 }, 8);
        picture.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(picture));
        using var png = new System.IO.MemoryStream();
        encoder.Save(png);
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, text);
        data.SetData("PNG", png.ToArray(), false);
        if (html is not null) data.SetData(DataFormats.Html, html);
        return (ClipboardImageReader.CaptureFrom(data, 42, () => null), picture);
    }, "Markdown and PNG clipboard fixture");
}
