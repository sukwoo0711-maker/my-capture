using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
    [InlineData("<pre><code class='language-mermaid'>flowchart TD</code></pre>")]
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
    [InlineData("/**\n * Adds two numbers.\n * @param {number} a\n * @param {number} b\n */\nfunction add(a, b) {\n  return a + b;\n}")]
    [InlineData("// - first\n// - second\nlet items = [];\nitems.push(1);")]
    [InlineData("- item one\n- item two\nconst total = 3;")]
    public void DoesNotClassifyOrdinaryTextOrCodeAsMarkdown(string source) => Assert.False(ClipboardMarkdownRenderer.IsMarkdown(source));

    [Fact]
    public void TallDiagramFollowsTheTextWithoutABlankPageBreakGap() => StaTestHost.Run(() =>
    {
        string text = string.Join("\n\n", Enumerable.Range(1, 17).Select(static n => "Paragraph number " + n + " of the document."))
            + "\n\n```mermaid\nflowchart TD\nA-->B\n```\n";
        var opaque = new byte[592 * 700 * 4];
        for (int index = 0; index < opaque.Length; index += 4) opaque[index + 2] = opaque[index + 3] = 255;
        var diagram = BitmapSource.Create(592, 700, 96, 96, PixelFormats.Bgra32, null, opaque, 592 * 4);
        diagram.Freeze();
        BitmapSource rendered = ClipboardMarkdownRenderer.Render(text, 1,
            new Dictionary<int, MermaidRenderResult> { [0] = new MermaidRenderResult(diagram, null) });
        // About 550 DIP of text and a 700 DIP diagram. A diagram that did not fit the rest of a 900 DIP page used
        // to start on the next page and leave a gap of several hundred DIPs.
        Assert.InRange(rendered.Height, 1200, 1450);
    });

    [Theory]
    [InlineData("flowchart TD\n A --> B")]
    [InlineData("graph LR\r\n A --> B")]
    [InlineData("sequenceDiagram\n Alice->>Bob: Hello")]
    [InlineData("classDiagram\n Animal <|-- Duck")]
    [InlineData("stateDiagram-v2\n [*] --> Ready")]
    [InlineData("erDiagram\n CUSTOMER ||--o{ ORDER : places")]
    [InlineData("gantt\n title Release")]
    [InlineData("pie title Pets\n \"Cats\" : 3")]
    [InlineData("mindmap\n root((Release))")]
    [InlineData("timeline\n title Release")]
    [InlineData("journey\n title Release")]
    [InlineData("\uFEFF%% Diagram comment\r\nflowchart TD\r\n A --> B\r\n")]
    public void PureMermaidIsDetectedAndRetainedExactly(string source)
    {
        Assert.True(ClipboardMarkdownRenderer.IsMarkdown(source));
        MarkdownMermaidBlock block = Assert.Single(ClipboardMarkdownRenderer.ExtractMermaidBlocks(source));
        Assert.Equal(0, block.Index);
        Assert.Equal(source, block.Source);
    }

    [Theory]
    [InlineData("graph theory is useful.\nThis is a normal sentence.")]
    [InlineData("timeline of the release\nReview tomorrow.")]
    [InlineData("일반적인 한국어 문장입니다.\n목록 기호 * 하나만 있습니다.")]
    [InlineData("- 한 가지 안내입니다.")]
    public void ProseAndSingleListMarkersDoNotBecomeDiagramsOrMarkdown(string source)
    {
        Assert.False(ClipboardMarkdownRenderer.IsMarkdown(source));
        Assert.Empty(ClipboardMarkdownRenderer.ExtractMermaidBlocks(source));
    }

    [Fact]
    public void MermaidExtractionSkipsOtherFencesAndRetainsBlockOrderAndLineEndings()
    {
        const string source = "# Diagrams\r\n````text\r\n```mermaid\r\nnot a diagram\r\n```\r\n````\r\n```mermaid\r\nflowchart TD\r\n A --> B\r\n```\r\nMiddle\n~~~mermaid\nsequenceDiagram\n Alice->>Bob: Hello\n~~~";
        var blocks = ClipboardMarkdownRenderer.ExtractMermaidBlocks(source);
        Assert.Equal(2, blocks.Count);
        Assert.Equal(new MarkdownMermaidBlock(0, "flowchart TD\r\n A --> B\r\n"), blocks[0]);
        Assert.Equal(new MarkdownMermaidBlock(1, "sequenceDiagram\n Alice->>Bob: Hello\n"), blocks[1]);
    }

    [Fact]
    public async Task GfmFormattingIncludesNestedListsRulesStrikeAndInertUnderlinedLinks()
    {
        const string source = "# First\n## Second\n### Third\n**Bold** *Italic* ~~Removed~~ `inline()` [Guide](https://example.test)\n1. Parent\n   - Child\n     1. Grandchild\n   - Second child\n2. Second parent\n\n- [x] Done\n- [ ] Pending\n\n---\n\n> Quoted\n\n| Column | State |\n| --- | --- |\n| Value | Ready |";
        await StaThreadTask.RunAsync(() =>
        {
            FlowDocument document = ClipboardMarkdownRenderer.CreateDocument(source);
            var headings = document.Blocks.OfType<Paragraph>().Where(paragraph => paragraph.Tag is int).ToArray();
            Assert.Equal(3, headings.Length);
            Assert.True(headings[0].FontSize > headings[1].FontSize && headings[1].FontSize > headings[2].FontSize);
            Paragraph inline = Assert.Single(document.Blocks.OfType<Paragraph>(), paragraph => paragraph.Inlines.OfType<Bold>().Any());
            Assert.Single(inline.Inlines.OfType<Italic>());
            Assert.Contains(inline.Inlines.OfType<Span>(), span => span.TextDecorations == TextDecorations.Strikethrough);
            Assert.Contains(inline.Inlines.OfType<Span>(), span => span.FontFamily.Source == "Consolas");
            Assert.Contains(inline.Inlines.OfType<Run>(), run => run.TextDecorations == TextDecorations.Underline && run.Foreground == Brushes.SteelBlue);
            Assert.Empty(inline.Inlines.OfType<Hyperlink>());
            var parent = document.Blocks.OfType<System.Windows.Documents.List>().First();
            Assert.Equal(TextMarkerStyle.Decimal, parent.MarkerStyle);
            Assert.Equal(2, parent.ListItems.Count);
            var child = Assert.Single(parent.ListItems.FirstListItem.Blocks.OfType<System.Windows.Documents.List>());
            Assert.Equal(TextMarkerStyle.Disc, child.MarkerStyle);
            Assert.Equal(2, child.ListItems.Count);
            var grandchild = Assert.Single(child.ListItems.FirstListItem.Blocks.OfType<System.Windows.Documents.List>());
            Assert.Equal(TextMarkerStyle.Decimal, grandchild.MarkerStyle);
            var tasks = document.Blocks.OfType<System.Windows.Documents.List>().Last();
            Assert.Equal(TextMarkerStyle.None, tasks.MarkerStyle);
            Assert.Contains("☑ Done", new TextRange(tasks.ContentStart, tasks.ContentEnd).Text);
            Assert.Contains("☐ Pending", new TextRange(tasks.ContentStart, tasks.ContentEnd).Text);
            Assert.Contains(document.Blocks.OfType<Paragraph>(), paragraph => paragraph.BorderThickness.Bottom == 1);
            Table table = Assert.Single(document.Blocks.OfType<Table>());
            Assert.Equal(FontWeights.SemiBold, table.RowGroups[0].Rows[0].Cells[0].FontWeight);
            Assert.True(table.RowGroups[0].Rows[1].Cells[0].BorderThickness.Left > 0);
            string html = ClipboardMarkdownRenderer.ToHtml(source);
            Assert.Contains("<del>Removed</del>", html);
            Assert.Contains("<hr>", html);
            return true;
        }, "GFM document structure");
    }

    [Fact]
    public async Task MermaidResultsReplaceTheirOwnPlaceholdersAndFailuresRetainExactCode()
    {
        const string source = "# Diagrams\r\n```mermaid\r\nflowchart TD\r\n A --> B\r\n```\r\nBetween\r\n```mermaid\r\nbroken syntax\r\n```\r\nAfter";
        await StaThreadTask.RunAsync(() =>
        {
            FlowDocument pending = ClipboardMarkdownRenderer.CreateDocument(source);
            Assert.Equal(2, pending.Blocks.OfType<Paragraph>().Count(paragraph => paragraph.Tag is MarkdownMermaidBlock));
            Assert.Equal(2, new TextRange(pending.ContentStart, pending.ContentEnd).Text.Split("그리는 중").Length - 1);
            var image = BitmapSource.Create(80, 40, 96, 96, PixelFormats.Bgra32, null,
                Enumerable.Repeat((byte)100, 80 * 40 * 4).ToArray(), 80 * 4);
            image.Freeze();
            var results = new Dictionary<int, MermaidRenderResult>
            {
                [0] = new(image, null),
                [1] = new(null, "Invalid syntax"),
            };
            FlowDocument completed = ClipboardMarkdownRenderer.CreateDocument(source, results);
            Block[] blocks = completed.Blocks.Cast<Block>().ToArray();
            Assert.Equal(6, blocks.Length);
            var diagram = Assert.IsType<BlockUIContainer>(blocks[1]);
            Assert.Same(image, Assert.IsType<Image>(diagram.Child).Source);
            Assert.Equal(0, Assert.IsType<MarkdownMermaidBlock>(diagram.Tag).Index);
            Assert.Equal("Between", new TextRange(blocks[2].ContentStart, blocks[2].ContentEnd).Text.TrimEnd('\r', '\n'));
            Assert.Equal("broken syntax\r\n", Assert.IsType<Paragraph>(blocks[3]).Tag);
            Assert.Contains("원문", new TextRange(blocks[4].ContentStart, blocks[4].ContentEnd).Text);
            Assert.DoesNotContain("그리는 중", new TextRange(completed.ContentStart, completed.ContentEnd).Text);
            BitmapSource bitmap = ClipboardMarkdownRenderer.Render(source, 1.5, results);
            Assert.True(bitmap.IsFrozen);
            Assert.Equal(144, bitmap.DpiX);
            Assert.InRange(bitmap.Height, 80, 900);
            return true;
        }, "Mermaid in-document state replacement");
    }

    [Fact]
    public async Task KoreanWrappingHintsNeverModifySelectableTextOrSourceCode()
    {
        const string source = "# 한국어 문서\n변경된 내용을 낱말 단위로 확인합니다.\n`한국어코드`\n```text\n한글 원문 코드\n```";
        await StaThreadTask.RunAsync(() =>
        {
            FlowDocument document = ClipboardMarkdownRenderer.CreateDocument(source);
            string selectable = new TextRange(document.ContentStart, document.ContentEnd).Text;
            Assert.Contains("변경된 내용을 낱말 단위로 확인합니다.", selectable);
            Assert.DoesNotContain('\u2060', selectable);
            Assert.DoesNotContain('\u2060', ClipboardMarkdownRenderer.ToHtml(source));
            _ = ClipboardMarkdownRenderer.Render(source);
            Assert.Equal(selectable, new TextRange(document.ContentStart, document.ContentEnd).Text);
            Assert.Equal("한글 원문 코드\n", Assert.Single(document.Blocks.OfType<Paragraph>(), paragraph => paragraph.Tag is string).Tag);
            return true;
        }, "Korean bitmap-only wrapping hints");
    }

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
        Assert.InRange(bitmap.PixelHeight, 1, 8_192);
        Assert.True((long)bitmap.PixelWidth * bitmap.PixelHeight <= 16_000_000);
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
    public async Task DiagramsAfterLongProseRemainVisibleAcrossComposedPages()
    {
        // More than one 900 DIP page of prose, so the first diagram sits beyond the first page boundary.
        string source = "# Diagram review\n" + string.Concat(Enumerable.Range(1, 32).Select(index => $"Review line {index}\n"))
            + "```mermaid\r\nflowchart TD\r\n A --> B\r\n```\r\n"
            + "```mermaid\nsequenceDiagram\n Alice->>Bob: Hello\n```\nRetained final paragraph";
        await StaThreadTask.RunAsync(() =>
        {
            static BitmapSource Solid(byte red, byte blue)
            {
                var pixels = new byte[300 * 350 * 4];
                for (int index = 0; index < pixels.Length; index += 4)
                {
                    pixels[index] = blue;
                    pixels[index + 2] = red;
                    pixels[index + 3] = 255;
                }
                var image = BitmapSource.Create(300, 350, 96, 96, PixelFormats.Bgra32, null, pixels, 300 * 4);
                image.Freeze();
                return image;
            }
            var results = new Dictionary<int, MermaidRenderResult>
            {
                [0] = new(Solid(255, 0), null),
                [1] = new(Solid(0, 255), null),
            };
            BitmapSource rendered = ClipboardMarkdownRenderer.Render(source, 1, results);
            Assert.Equal(640, rendered.PixelWidth);
            Assert.InRange(rendered.PixelHeight, 901, 2_700);
            var pixels = new byte[rendered.PixelWidth * rendered.PixelHeight * 4];
            rendered.CopyPixels(pixels, rendered.PixelWidth * 4, 0);
            int redPixels = 0, bluePixels = 0, firstRedRow = -1, firstBlueRow = -1;
            for (int index = 0; index < pixels.Length; index += 4)
            {
                if (pixels[index + 2] > 240 && pixels[index] < 10 && pixels[index + 1] < 10)
                {
                    redPixels++;
                    if (firstRedRow < 0) firstRedRow = index / (rendered.PixelWidth * 4);
                }
                if (pixels[index] > 240 && pixels[index + 2] < 10 && pixels[index + 1] < 10)
                {
                    bluePixels++;
                    if (firstBlueRow < 0) firstBlueRow = index / (rendered.PixelWidth * 4);
                }
            }
            Assert.True(redPixels > 90_000, $"First diagram painted only {redPixels} red pixels.");
            Assert.True(bluePixels > 90_000, $"Second diagram painted only {bluePixels} blue pixels.");
            Assert.True(firstRedRow >= 900);
            Assert.True(firstBlueRow > firstRedRow);
            FlowDocument reading = ClipboardMarkdownRenderer.CreateDocument(source, results);
            Assert.Equal(2, reading.Blocks.OfType<BlockUIContainer>().Count());
            Assert.Contains("Retained final paragraph", new TextRange(reading.ContentStart, reading.ContentEnd).Text);
            Assert.Equal("flowchart TD\r\n A --> B\r\n", ClipboardMarkdownRenderer.ExtractMermaidBlocks(source)[0].Source);
            return true;
        }, "Markdown composed pages retain later diagrams");
    }

    [Fact]
    public async Task OversizeMermaidUsesCodeFallbackAndVisibleContinuationWithinPixelBudget()
    {
        string source = "flowchart TD\n" + string.Concat(Enumerable.Repeat(" A-->B\n", 40_000)) + "Original final marker";
        Assert.Empty(ClipboardMarkdownRenderer.ExtractMermaidBlocks(source));
        await StaThreadTask.RunAsync(() =>
        {
            BitmapSource bitmap = ClipboardMarkdownRenderer.Render(source, 4);
            Assert.True(bitmap.IsFrozen);
            Assert.InRange(bitmap.Height, 8_191, 8_193);
            Assert.True((long)bitmap.PixelWidth * bitmap.PixelHeight <= 16_000_000);
            Assert.True(bitmap.DpiX < 384);
            // The continuation footer is painted in the final 32 DIP, outside the content clip.
            int footerHeight = (int)Math.Ceiling(32 * bitmap.DpiY / 96);
            var footer = new byte[bitmap.PixelWidth * footerHeight * 4];
            bitmap.CopyPixels(new Int32Rect(0, bitmap.PixelHeight - footerHeight, bitmap.PixelWidth, footerHeight),
                footer, bitmap.PixelWidth * 4, 0);
            Assert.Contains(Enumerable.Range(0, footer.Length / 4), index => footer[index * 4] < 220);
            FlowDocument reading = ClipboardMarkdownRenderer.CreateDocument(source);
            Paragraph paragraph = Assert.IsType<Paragraph>(Assert.Single(reading.Blocks.Cast<Block>()));
            Assert.Equal(source, Assert.IsType<Run>(Assert.Single(paragraph.Inlines.Cast<Inline>())).Text);
            Assert.DoesNotContain("그리는 중", new TextRange(reading.ContentStart, reading.ContentEnd).Text);
            return true;
        }, "Oversize Mermaid bounded source fallback");
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
