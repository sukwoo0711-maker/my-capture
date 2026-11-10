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
    public async Task VsCodeMarkdownCopy_RendersDocumentAndMermaidInsteadOfACodePin()
    {
        const string source = "# LED 버튼 동작\r\n\r\n버튼을 누를 때마다 LED 모드가 바뀝니다.\r\n\r\n- **OFF**: 꺼짐\r\n- **ON**: 켜짐\r\n- **BLINK**: 0.5초마다 깜빡임\r\n\r\n```mermaid\r\nflowchart LR\r\n    OFF -->|버튼| ON\r\n    ON -->|버튼| BLINK\r\n    BLINK -->|버튼| OFF\r\n```\r\n";
        var captured = await Capture(source, VsCodeHtml(source), Convert.FromBase64String(VsCodeMarkdownCopyData));
        Assert.True(captured.HasCodeHtml);
        Assert.True(captured.HasExplicitMarkdown);
        Assert.Null(captured.CodePreview);
        var completed = await ClipboardImageReader.CompletePinAttemptAsync(captured,
            _ => throw new InvalidOperationException("VS Code publishes no picture."),
            (_, _) => throw new InvalidOperationException("A VS Code Markdown copy must use the document renderer."),
            _ => throw new InvalidOperationException("No clipboard reread is needed."));
        Assert.True(completed.Content!.IsMarkdown);
        Assert.Equal(PinTextStyle.Markdown, completed.Content.TextStyle);
        Assert.Equal(source, completed.Content.OriginalText);
        Assert.True(completed.Content.Image.IsFrozen);
        MarkdownMermaidBlock diagram = Assert.Single(ClipboardMarkdownRenderer.ExtractMermaidBlocks(source));
        Assert.StartsWith("flowchart LR", diagram.Source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VsCodeCSourceCopy_KeepsTheColoredCodePin()
    {
        const string source = "#include <stdint.h>\r\n#include <stdbool.h>\r\n\r\n#define LED_PIN     5\r\n\r\n/* 버튼을 누를 때마다 OFF -> ON -> BLINK 순서로 바꿈 */\r\nvoid on_button_pressed(void)\r\n{\r\n    mode = (led_mode_t)((mode + 1) % 3);\r\n}\r\n";
        var captured = await Capture(source, VsCodeHtml(source), Convert.FromBase64String(VsCodeCCopyData));
        Assert.True(captured.HasCodeHtml);
        Assert.False(captured.HasExplicitMarkdown);
        Assert.NotNull(captured.CodePreview);
        var completed = await ClipboardImageReader.CompletePinAttemptAsync(captured,
            _ => throw new InvalidOperationException("Code preview already exists."),
            (_, _) => throw new InvalidOperationException("Keep the code renderer."),
            _ => throw new InvalidOperationException("No clipboard reread is needed."),
            _ => throw new InvalidOperationException("A C copy must not select Markdown."));
        Assert.False(completed.Content!.IsMarkdown);
        Assert.Equal(PinTextStyle.Code, completed.Content.TextStyle);
        Assert.Same(captured.CodePreview, completed.Content.Image);
        Assert.Equal("#121314", completed.Content.CodeDocument!.Background);
        Assert.Equal(source, completed.Content.OriginalText);
    }

    [Fact]
    public async Task VsCodeMermaidFileCopy_UsesTheDiagramPipeline()
    {
        const string source = "flowchart LR\r\n    OFF -->|버튼| ON\r\n    ON -->|버튼| OFF\r\n";
        var captured = await Capture(source, VsCodeHtml(source), ChromiumCustomData(
            ("vscode-editor-data", "{\"version\":1,\"isFromEmptySelection\":false,\"multicursorText\":null,\"mode\":\"mermaid\"}")));
        Assert.True(captured.HasExplicitMarkdown);
        var completed = await ClipboardImageReader.CompletePinAttemptAsync(captured, _ => null,
            (_, _) => throw new InvalidOperationException("Mermaid must use the diagram placeholder."),
            _ => throw new InvalidOperationException("No clipboard reread is needed."));
        Assert.True(completed.Content!.IsMarkdown);
        Assert.Equal(source, completed.Content.OriginalText);
        Assert.Equal(source, Assert.Single(ClipboardMarkdownRenderer.ExtractMermaidBlocks(source)).Source);
    }

    [Theory]
    [InlineData(VsCodeMarkdownCopyData, "markdown")]
    [InlineData(VsCodeCCopyData, "c")]
    public void VsCodeEditorLanguage_IsReadFromRealCopiesAsStreamOrBytes(string payload, string expected)
    {
        byte[] bytes = Convert.FromBase64String(payload);
        Assert.Equal(expected, ClipboardEditorLanguage.TryParseVsCodeMode(bytes));
        var stream = new DataObject();
        stream.SetData(ClipboardEditorLanguage.ChromiumCustomDataFormat, new System.IO.MemoryStream(bytes), false);
        Assert.Equal(expected, ClipboardEditorLanguage.TryRead(stream));
        var array = new DataObject();
        array.SetData(ClipboardEditorLanguage.ChromiumCustomDataFormat, bytes, false);
        Assert.Equal(expected, ClipboardEditorLanguage.TryRead(array));
    }

    [Fact]
    public void VsCodeEditorLanguage_IgnoresMalformedOrUnrelatedCustomData()
    {
        byte[] real = Convert.FromBase64String(VsCodeMarkdownCopyData);
        Assert.Null(ClipboardEditorLanguage.TryParseVsCodeMode(real.AsSpan(0, real.Length - 8)));
        Assert.Null(ClipboardEditorLanguage.TryParseVsCodeMode(real.AsSpan(0, 6)));
        byte[] countTooLarge = (byte[])real.Clone();
        countTooLarge[7] = 0x01;
        Assert.Null(ClipboardEditorLanguage.TryParseVsCodeMode(countTooLarge));
        byte[] lengthBeyondPayload = (byte[])real.Clone();
        lengthBeyondPayload[8] = 0xFF;
        lengthBeyondPayload[9] = 0xFF;
        Assert.Null(ClipboardEditorLanguage.TryParseVsCodeMode(lengthBeyondPayload));
        Assert.Null(ClipboardEditorLanguage.TryParseVsCodeMode(ChromiumCustomData(
            ("application/vnd.code.copymetadata", "{\"mode\":\"markdown\"}"))));
        foreach (string editorData in new[] { "not json", "[\"markdown\"]", "{\"version\":1}", "{\"mode\":null}", "{\"mode\":3}" })
            Assert.Null(ClipboardEditorLanguage.TryParseVsCodeMode(ChromiumCustomData(("vscode-editor-data", editorData))));
        Assert.Null(ClipboardEditorLanguage.TryRead(new DataObject()));
        var unrelated = new DataObject();
        unrelated.SetData(ClipboardEditorLanguage.ChromiumCustomDataFormat, "markdown", false);
        Assert.Null(ClipboardEditorLanguage.TryRead(unrelated));
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
        // A view of the same pin (the original beside its translation) keeps the lookup that is still pending.
        PinContent snapshot = original.WithCurrentProvenance(interim);
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

    // "Chromium Web Custom MIME Data Format" from VS Code 1.141.0 on Windows, copied with Ctrl+A,
    // Ctrl+C from button_led.md and button_led.c on 2026-11-05. Besides vscode-editor-data it
    // carries VS Code's application/vnd.code.copymetadata entry.
    private const string VsCodeMarkdownCopyData = "uAMAAAIAAAAhAAAAYQBwAHAAbABpAGMAYQB0AGkAbwBuAC8AdgBuAGQALgBjAG8AZABlAC4AYwBvAHAAeQBtAGUAdABhAGQAYQB0AGEAAAAOAQAAewAiAGkAZAAiADoAIgBhADUANQAxAGIAZgAwAGUALQA5AGYAZQBmAC0ANAAwAGIAZQAtAGEAZQBmADkALQA4ADcAMwAxADMAOAAwADIAMQBmAGQAMwAiACwAIgBwAHIAbwB2AGkAZABlAHIAQwBvAHAAeQBNAGkAbQBlAFQAeQBwAGUAcwAiADoAWwAiAGEAcABwAGwAaQBjAGEAdABpAG8AbgAvAHYAbgBkAC4AdgBzAGMAbwBkAGUALgBtAGEAcgBrAGQAbwB3AG4ALgB1AHAAZABhAHQAZQBsAGkAbgBrAHMALgBtAGUAdABhAGQAYQB0AGEAIgAsACIAYQBwAHAAbABpAGMAYQB0AGkAbwBuAC8AdgBuAGQALgBjAG8AZABlAC4AYQBkAGQAaQB0AGkAbwBuAGEAbAAtAGUAZABpAHQAbwByAC0AZABhAHQAYQAiAF0ALAAiAGQAZQBmAGEAdQBsAHQAUABhAHMAdABlAFAAYQB5AGwAbwBhAGQAIgA6AHsAIgBtAHUAbAB0AGkAYwB1AHIAcwBvAHIAVABlAHgAdAAiADoAbgB1AGwAbAAsACIAcABhAHMAdABlAE8AbgBOAGUAdwBMAGkAbgBlACIAOgBmAGEAbABzAGUALAAiAGkAcwBCAGwAbwBjAGsAIgA6AGYAYQBsAHMAZQAsACIAbQBvAGQAZQAiADoAbgB1AGwAbAB9AH0AEgAAAHYAcwBjAG8AZABlAC0AZQBkAGkAdABvAHIALQBkAGEAdABhAI8AAAB7ACIAdgBlAHIAcwBpAG8AbgAiADoAMQAsACIAaQBkACIAOgAiADEANQAwADIAYgA1ADgAYQAtAGIANQAzADkALQA0ADUAZgA1AC0AOABmAGUAZgAtADYAYQBiADAAYgA4ADMANAAxAGIAZQBjACIALAAiAGkAcwBGAHIAbwBtAEUAbQBwAHQAeQBTAGUAbABlAGMAdABpAG8AbgAiADoAZgBhAGwAcwBlACwAIgBpAHMAQgBsAG8AYwBrACIAOgBmAGEAbABzAGUALAAiAG0AdQBsAHQAaQBjAHUAcgBzAG8AcgBUAGUAeAB0ACIAOgBuAHUAbABsACwAIgBtAG8AZABlACIAOgAiAG0AYQByAGsAZABvAHcAbgAiAH0AAAA=";
    private const string VsCodeCCopyData = "PAMAAAIAAAAhAAAAYQBwAHAAbABpAGMAYQB0AGkAbwBuAC8AdgBuAGQALgBjAG8AZABlAC4AYwBvAHAAeQBtAGUAdABhAGQAYQB0AGEAAADXAAAAewAiAGkAZAAiADoAIgAzAGEANAAyADMANgA3ADcALQBjADYAZQBhAC0ANAAyAGMAZgAtAGEAMgBiADIALQA5ADYAYwA0AGEAYQAxADQAMgA0ADYANwAiACwAIgBwAHIAbwB2AGkAZABlAHIAQwBvAHAAeQBNAGkAbQBlAFQAeQBwAGUAcwAiADoAWwAiAGEAcABwAGwAaQBjAGEAdABpAG8AbgAvAHYAbgBkAC4AYwBvAGQAZQAuAGEAZABkAGkAdABpAG8AbgBhAGwALQBlAGQAaQB0AG8AcgAtAGQAYQB0AGEAIgBdACwAIgBkAGUAZgBhAHUAbAB0AFAAYQBzAHQAZQBQAGEAeQBsAG8AYQBkACIAOgB7ACIAbQB1AGwAdABpAGMAdQByAHMAbwByAFQAZQB4AHQAIgA6AG4AdQBsAGwALAAiAHAAYQBzAHQAZQBPAG4ATgBlAHcATABpAG4AZQAiADoAZgBhAGwAcwBlACwAIgBpAHMAQgBsAG8AYwBrACIAOgBmAGEAbABzAGUALAAiAG0AbwBkAGUAIgA6AG4AdQBsAGwAfQB9AAAAEgAAAHYAcwBjAG8AZABlAC0AZQBkAGkAdABvAHIALQBkAGEAdABhAIgAAAB7ACIAdgBlAHIAcwBpAG8AbgAiADoAMQAsACIAaQBkACIAOgAiAGUAMAAxADkAYgBjADkAMwAtADQAZQA4ADQALQA0ADcAMgAxAC0AYgAzAGQAYgAtAGMAMAAyAGUAOAA5ADEANABiAGQAZABhACIALAAiAGkAcwBGAHIAbwBtAEUAbQBwAHQAeQBTAGUAbABlAGMAdABpAG8AbgAiADoAZgBhAGwAcwBlACwAIgBpAHMAQgBsAG8AYwBrACIAOgBmAGEAbABzAGUALAAiAG0AdQBsAHQAaQBjAHUAcgBzAG8AcgBUAGUAeAB0ACIAOgBuAHUAbABsACwAIgBtAG8AZABlACIAOgAiAGMAIgB9AA==";

    /// <summary>The CF_HTML VS Code publishes: one pre-formatted block, one div per line, no language name.</summary>
    private static string VsCodeHtml(string source)
    {
        var lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n')
            .Select(static line => line.Length == 0
                ? "<br>"
                : "<div><span style=\"color: #bbbebf;\">" + System.Net.WebUtility.HtmlEncode(line) + "</span></div>");
        return "Version:0.9\r\n<html>\r\n<body>\r\n<!--StartFragment--><div style=\"color: #bbbebf;background-color: #121314;"
            + "font-family: Consolas, 'Courier New', monospace;font-weight: normal;font-size: 14px;line-height: 19px;white-space: pre;\">"
            + string.Concat(lines) + "</div><!--EndFragment-->\r\n</body>\r\n</html>";
    }

    /// <summary>Writes entries the way Chromium pickles non-standard clipboard types.</summary>
    private static byte[] ChromiumCustomData(params (string Type, string Value)[] entries)
    {
        var payload = new List<byte>();
        void WriteUInt32(uint value) => payload.AddRange(BitConverter.GetBytes(value));
        void WriteString16(string value)
        {
            WriteUInt32((uint)value.Length);
            payload.AddRange(System.Text.Encoding.Unicode.GetBytes(value));
            while (payload.Count % 4 != 0) payload.Add(0);
        }
        WriteUInt32((uint)entries.Length);
        foreach ((string type, string value) in entries)
        {
            WriteString16(type);
            WriteString16(value);
        }
        return [.. BitConverter.GetBytes((uint)payload.Count), .. payload];
    }

    private static Task<ClipboardImageReader.CapturedAttempt> Capture(string text, string? html, byte[]? editorData = null) =>
        StaThreadTask.RunAsync(() =>
    {
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, text);
        if (html is not null) data.SetData(DataFormats.Html, html);
        if (editorData is not null)
            data.SetData(ClipboardEditorLanguage.ChromiumCustomDataFormat, new System.IO.MemoryStream(editorData), false);
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
