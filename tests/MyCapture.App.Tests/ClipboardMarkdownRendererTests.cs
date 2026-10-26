using System.Windows;
using System.Windows.Documents;
using MyCapture.App.Pinning;
using MyCapture.App.Threading;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class ClipboardMarkdownRendererTests
{
    [Theory]
    [InlineData("<pre><code class='language-markdown'># Title</code></pre>")]
    [InlineData("<PRE class=language-md># Title</PRE>")]
    [InlineData("<div data-language=markdown style='white-space:pre'># Title</div>")]
    [InlineData("<code data-lang='MD'># Title</code>")]
    [InlineData("<div class='highlight highlight-source-md'><pre># Title</pre></div>")]
    public void ExplicitMarkdownHtml_RecognizesLanguageMetadata(string html) =>
        Assert.True(ClipboardMarkdownRenderer.IsExplicitMarkdownHtml(html));

    [Theory]
    [InlineData(null)]
    [InlineData("<pre># Python comment</pre>")]
    [InlineData("<pre><code class='language-python'># Python comment</code></pre>")]
    [InlineData("<div style='white-space:pre'># Python comment</div>")]
    [InlineData("<div title=\"class='language-markdown'\"># Title</div>")]
    [InlineData("<!-- <code class='language-markdown'> --># Title")]
    [InlineData("<pre>&lt;code class='language-markdown'&gt;</pre>")]
    [InlineData("<code class='language-md'># Title</code><code class='language-python'># comment</code>")]
    public void ExplicitMarkdownHtml_DoesNotGuessAmbiguousOrOtherLanguageCode(string? html) =>
        Assert.False(ClipboardMarkdownRenderer.IsExplicitMarkdownHtml(html));

    [Theory]
    [InlineData("# 배포 확인\n본문")]
    [InlineData("```csharp\nvar x = 1;\n```")]
    [InlineData("| 항목 | 값 |\n| --- | ---: |\n| 재시도 | 3 |")]
    [InlineData("- [x] 검증 완료")]
    [InlineData("1. 준비\n2. 배포")]
    public void DetectsExplicitMarkdownSyntax(string source) => Assert.True(ClipboardMarkdownRenderer.IsMarkdown(source));

    [Theory]
    [InlineData("")]
    [InlineData("일반 메모 한 줄")]
    [InlineData("**중요한 내용**")]
    [InlineData("Read [the guide](https://example.com).")]
    [InlineData("# A single comment")]
    [InlineData("# Python comment\nprint(1)")]
    [InlineData("const string file_name = \"plain_text\";\nreturn value * factor;")]
    [InlineData("#if DEBUG\nConsole.WriteLine(\"test\");\n#endif")]
    [InlineData("using System;\nclass Example { int x = 1; }")]
    [InlineData("<script>alert('example')</script><img src='https://example.invalid/a'>")]
    [InlineData("A\tB\n1\t2")]
    public void DoesNotClassifyOrdinaryTextOrCodeAsMarkdown(string source) => Assert.False(ClipboardMarkdownRenderer.IsMarkdown(source));

    [Fact]
    public async Task DocumentContainsSemanticBlocksAndExactCodeBody()
    {
        const string source = "# 배포 확인\r\n\r\n**담당자**와 *참고*\r\n- [x] 빌드\r\n- [ ] 배포\r\n> 확인 후 진행\r\n\r\n```csharp\r\nvar timeout = 5000;\r\n\r\n```\r\n| 이름 | 값 |\r\n| --- | --- |\r\n| 재시도 | 3 |";
        await StaThreadTask.RunAsync(() =>
        {
            FlowDocument document = ClipboardMarkdownRenderer.CreateDocument(source);
            Paragraph heading = Assert.IsType<Paragraph>(document.Blocks.FirstBlock);
            Assert.Equal(1, heading.Tag);
            Assert.True(heading.FontSize > document.FontSize);
            Paragraph emphasis = document.Blocks.OfType<Paragraph>().First(paragraph => paragraph.Inlines.OfType<Bold>().Any());
            Assert.Single(emphasis.Inlines.OfType<Italic>());
            var list = Assert.Single(document.Blocks.OfType<System.Windows.Documents.List>());
            Assert.Equal(2, list.ListItems.Count);
            Assert.Contains("☑ 빌드", new TextRange(document.ContentStart, document.ContentEnd).Text);
            Assert.Single(document.Blocks.OfType<Section>());
            Paragraph code = Assert.Single(document.Blocks.OfType<Paragraph>(), paragraph => paragraph.Tag is string);
            Assert.Equal("var timeout = 5000;\r\n\r\n", code.Tag);
            Table table = Assert.Single(document.Blocks.OfType<Table>());
            Assert.Equal(2, table.Columns.Count);
            Assert.Equal(2, table.RowGroups[0].Rows.Count);
            return true;
        }, "Markdown document test");
    }

    [Fact]
    public async Task RenderIsFrozenVisibleAndBoundedForLargeInput()
    {
        var bitmap = await StaThreadTask.RunAsync(() => ClipboardMarkdownRenderer.Render(
            "# 큰 문서\n" + string.Concat(Enumerable.Repeat("- **항목**과 긴 내용 😀\n", 20_000))), "Markdown bitmap test");
        Assert.True(bitmap.IsFrozen);
        Assert.InRange(bitmap.PixelWidth, 1, 1200);
        Assert.InRange(bitmap.PixelHeight, 1, 900);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        Assert.Contains(Enumerable.Range(0, pixels.Length / 4), index => pixels[index * 4] < 200 && pixels[index * 4 + 3] > 0);
    }

    [Fact]
    public async Task ShortPreviewFitsItsContentWhileKeepingLaterBlocksVisible()
    {
        await StaThreadTask.RunAsync(() =>
        {
            var shortNote = ClipboardMarkdownRenderer.Render("# Release\nReady");
            var checklist = ClipboardMarkdownRenderer.Render("# Release\n- [x] Build\n- [ ] Payment\n> Review before release.\n```ts\nconst timeoutMs = 5000;\n```\n| Item | State |\n| --- | --- |\n| Search | Done |\n| Payment | Review |");
            Assert.Equal(640, shortNote.PixelWidth);
            Assert.InRange(shortNote.PixelHeight, 80, 180);
            Assert.InRange(checklist.PixelHeight, shortNote.PixelHeight + 100, 600);
            Assert.True(shortNote.IsFrozen);
            Assert.True(checklist.IsFrozen);
            return true;
        }, "Markdown compact preview test");
    }

    [Fact]
    public async Task EscapedPunctuationDisplaysLiterallyWithoutChangingCode()
    {
        const string prose = @"지금 배포\! \- 옵션 \[검토\] \\ C:\Temp \*일반\* **강조\!**";
        const string code = @"C:\Temp\file\!";
        await StaThreadTask.RunAsync(() =>
        {
            FlowDocument document = ClipboardMarkdownRenderer.CreateDocument(prose + "\n`" + code + "`\n```text\n" + code + "\n```");
            Paragraph paragraph = Assert.IsType<Paragraph>(document.Blocks.FirstBlock);
            Assert.Equal(@"지금 배포! - 옵션 [검토] \ C:\Temp *일반* 강조!",
                new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text);
            Assert.Single(paragraph.Inlines.OfType<Bold>());
            Assert.Empty(paragraph.Inlines.OfType<Italic>());
            Paragraph inlineCode = document.Blocks.OfType<Paragraph>().ElementAt(1);
            Assert.Equal(code, new TextRange(inlineCode.ContentStart, inlineCode.ContentEnd).Text);
            Paragraph fencedCode = Assert.Single(document.Blocks.OfType<Paragraph>(), block => block.Tag is string);
            Assert.Equal(code + "\n", fencedCode.Tag);
            string html = ClipboardMarkdownRenderer.ToHtml(prose);
            Assert.Contains("<strong>강조!</strong>", html);
            Assert.DoesNotContain(@"\!", html);
            Assert.False(ClipboardMarkdownRenderer.IsMarkdown(@"\*\*literal\*\*"));
            return true;
        }, "Markdown punctuation escape test");
    }

    [Fact]
    public async Task EscapedPipesRemainWithinTheirTableCells()
    {
        const string source = "| A \\| B | State |\n| --- | --- |\n| x \\| y | done\\! |\nlast | tail\\|\n| path\\\\| right |";
        await StaThreadTask.RunAsync(() =>
        {
            Table table = Assert.Single(ClipboardMarkdownRenderer.CreateDocument(source).Blocks.OfType<Table>());
            TableRowCollection rows = table.RowGroups[0].Rows;
            Assert.Equal(2, table.Columns.Count);
            Assert.Equal(4, rows.Count);
            Assert.All(rows.Cast<TableRow>(), row => Assert.Equal(2, row.Cells.Count));
            static string CellText(TableCell cell) => new TextRange(cell.ContentStart, cell.ContentEnd).Text.TrimEnd('\r', '\n');
            Assert.Equal("A | B", CellText(rows[0].Cells[0]));
            Assert.Equal("x | y", CellText(rows[1].Cells[0]));
            Assert.Equal("done!", CellText(rows[1].Cells[1]));
            Assert.Equal("tail|", CellText(rows[2].Cells[1]));
            Assert.Equal(@"path\", CellText(rows[3].Cells[0]));
            Assert.Equal("right", CellText(rows[3].Cells[1]));
            return true;
        }, "Markdown escaped table delimiter test");
    }

    [Theory]
    [InlineData(1.25)]
    [InlineData(2)]
    public async Task RenderAtDpi_KeepsLogicalLayoutAndUsesNativePixelDensity(double scale)
    {
        const string source = "# DPI 확인\n\n**동일한 내용**\n- 첫째\n- 둘째";
        await StaThreadTask.RunAsync(() =>
        {
            var normal = ClipboardMarkdownRenderer.Render(source);
            var scaled = ClipboardMarkdownRenderer.Render(source, scale);
            Assert.Equal((int)Math.Ceiling(normal.PixelWidth * scale), scaled.PixelWidth);
            Assert.Equal((int)Math.Ceiling(normal.PixelHeight * scale), scaled.PixelHeight);
            Assert.Equal(96 * scale, scaled.DpiX);
            Assert.Equal(96 * scale, scaled.DpiY);
            Assert.InRange(Math.Abs(normal.Width - scaled.Width), 0, 1 / scale);
            Assert.InRange(Math.Abs(normal.Height - scaled.Height), 0, 1 / scale);
            Assert.True(scaled.IsFrozen);
            return true;
        }, "Markdown DPI test");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(8.1)]
    [InlineData(double.NaN)]
    public async Task RenderRejectsUnsupportedScale(double scale) => await StaThreadTask.RunAsync(() =>
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ClipboardMarkdownRenderer.Render("# Title", scale));
        return true;
    }, "Markdown invalid DPI test");

    [Fact]
    public async Task RepeatedShortStaRendering_ReturnsDetachedBitmapsAfterEveryThreadShutsDown()
    {
        for (int index = 0; index < 20; index++)
        {
            var bitmap = await StaThreadTask.RunAsync(() => ClipboardMarkdownRenderer.Render(
                "# 반복 렌더\n\n**확인**\n\n```ts\nconst timeoutMs = 5000;\n```", index % 2 == 0 ? 1 : 1.5),
                "Markdown isolated rendering lifecycle");
            Assert.True(bitmap.IsFrozen);
            var pixel = new byte[4];
            bitmap.CopyPixels(new Int32Rect(0, 0, 1, 1), pixel, 4, 0);
            Assert.Equal(255, pixel[3]);
        }
    }

    [Fact]
    public async Task ReadingDocumentRetainsContentBeyondBitmapPreview()
    {
        string source = "# 전체 내용\n" + string.Concat(Enumerable.Repeat("일반 내용입니다.\n", 2_000)) + "문서의 마지막 내용";
        await StaThreadTask.RunAsync(() =>
        {
            FlowDocument first = ClipboardMarkdownRenderer.CreateDocument(source);
            FlowDocument second = ClipboardMarkdownRenderer.CreateDocument(source);
            Assert.NotSame(first, second);
            Assert.Contains("문서의 마지막 내용", new TextRange(first.ContentStart, first.ContentEnd).Text);
            return true;
        }, "Markdown reading test");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FormattingLimitFallsBackToCompletePlainSource(bool manyLines)
    {
        string source = manyLines
            ? "# Heading\n" + string.Concat(Enumerable.Repeat("line\n", 9_000)) + "Retained end"
            : "# Heading\n" + new string('a', 270_000) + "Retained end";
        await StaThreadTask.RunAsync(() =>
        {
            FlowDocument document = ClipboardMarkdownRenderer.CreateDocument(source);
            Paragraph paragraph = Assert.IsType<Paragraph>(Assert.Single(document.Blocks.Cast<Block>()));
            Assert.Null(paragraph.Tag);
            Assert.Equal(source, Assert.IsType<Run>(Assert.Single(paragraph.Inlines.Cast<Inline>())).Text);
            string html = ClipboardMarkdownRenderer.ToHtml(source);
            Assert.Contains("Retained end", html);
            return true;
        }, "Markdown full-source fallback test");
    }

    [Fact]
    public async Task HeadingHashAndEmptyTableCellAreRetained()
    {
        await StaThreadTask.RunAsync(() =>
        {
            FlowDocument document = ClipboardMarkdownRenderer.CreateDocument("# C#\n| A | B | C |\n| --- | --- | --- |\n| value |||");
            Assert.Contains("C#", new TextRange(document.ContentStart, document.ContentEnd).Text);
            Table table = Assert.Single(document.Blocks.OfType<Table>());
            Assert.Equal(3, table.RowGroups[0].Rows[1].Cells.Count);
            return true;
        }, "Markdown literal preservation test");
    }

    [Fact]
    public async Task HtmlAndLinksRemainInertAndHtmlExportEscapesUntrustedText()
    {
        const string source = "# 안전한 표시\n<script>alert('x')</script>\n[사이트](javascript:alert)\n![이미지](https://example.invalid/a.png)\n```html\n<img src=x onerror=alert(1)>\n```";
        await StaThreadTask.RunAsync(() =>
        {
            FlowDocument document = ClipboardMarkdownRenderer.CreateDocument(source);
            Assert.DoesNotContain(document.Blocks, block => block is BlockUIContainer);
            Assert.DoesNotContain(document.Blocks.OfType<Paragraph>().SelectMany(paragraph => paragraph.Inlines), inline => inline is Hyperlink or InlineUIContainer);
            Assert.Contains("<script>", new TextRange(document.ContentStart, document.ContentEnd).Text);
            string html = ClipboardMarkdownRenderer.ToHtml(source);
            Assert.Contains("&lt;script&gt;", html);
            Assert.Contains("&lt;img", html);
            Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("href=", html, StringComparison.OrdinalIgnoreCase);
            return true;
        }, "Markdown inert content test");
    }

    [Fact]
    public async Task RichHtmlPreservesDocumentStructure()
    {
        await StaThreadTask.RunAsync(() =>
        {
            string html = ClipboardMarkdownRenderer.ToHtml("## 점검\n**강조**와 *기울임*\n1. 첫째\n2. 둘째\n| 항목 | 값 |\n| --- | --- |\n| A | 1 |\n```js\nconst value = 1;\n```");
            Assert.Contains("<h2>", html);
            Assert.Contains("<strong>강조</strong>", html);
            Assert.Contains("<em>기울임</em>", html);
            Assert.Contains("<ol start=\"1\">", html);
            Assert.Contains("<table", html);
            Assert.Contains("<th ", html);
            Assert.Contains("<code>const value = 1;", html);
            return true;
        }, "Markdown HTML export test");
    }
}
