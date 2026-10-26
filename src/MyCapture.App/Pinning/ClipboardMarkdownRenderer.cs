using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MyCapture.App.Pinning;

/// <summary>Renders a bounded Markdown subset using inert, local WPF document elements.</summary>
internal static class ClipboardMarkdownRenderer
{
    private const int MaxDocumentCharacters = 262_144;
    private const int MaxDocumentLines = 8_192;
    private const int MaxPreviewCharacters = 16_384;
    private const int MaxColumns = 24;
    private const int PageWidthDip = 640;
    private const int PageHeightDip = 900;
    private const int MaxPreviewHeightDip = 8_192;
    private const int MaxPreviewPixels = 16_000_000;
    private const int ContinuationHeightDip = 32;
    private static readonly Regex Heading = Pattern(@"^ {0,3}(#{1,6})\s+(\S.*)$");
    private static readonly Regex ClosingHeading = Pattern(@"\s+#+\s*$");
    private static readonly Regex ListItemPattern = Pattern(@"^(?<indent>[ \t]{0,64})(?:(?<bullet>[-+*])|(?<number>\d{1,6})[.)])[ \t]+(?<body>.+)$");
    private static readonly Regex TaskItem = Pattern(@"^\[(?<done>[ xX])\]\s+(?<body>.*)$");
    private static readonly Regex SeparatorCell = Pattern(@"^:?-{3,}:?$");
    private static readonly Regex CodeStatement = Pattern(@"^\s*(?:(?:import|using|namespace|class|def|function|const|let|var)\s+|[\w.$]+\([^\r\n]*\)\s*;?\s*$|#!)");
    private static readonly Regex InlinePattern = Pattern(
        @"(?<escape>\\[\x21-\x2f\x3a-\x40\x5b-\x60\x7b-\x7e])|(?<code>`[^`\r\n]+`)|(?<strong>\*\*(?:\\[^\r\n]|[^*\\\r\n])+\*\*|__(?:\\[^\r\n]|[^_\\\r\n])+__)|(?<strike>~~(?:\\[^\r\n]|[^~\\\r\n])+~~)|(?<emphasis>\*(?:\\[^\r\n]|[^*\\\r\n])+\*|_(?:\\[^\r\n]|[^_\\\r\n])+_)|(?<link>!?\[(?<label>(?:\\[^\r\n]|[^\]\\\r\n])+)\]\((?<destination>(?:\\[^\r\n]|[^\s)\\\r\n])+)\))");
    private static readonly Regex MermaidDeclaration = Pattern(
        @"^(?:(?:flowchart|graph)[ \t]+(?:TB|TD|BT|RL|LR)(?:[ \t]*;.*)?[ \t]*|(?:sequenceDiagram|classDiagram|stateDiagram(?:-v2)?|erDiagram|gantt|mindmap|timeline|journey|gitGraph|quadrantChart|requirementDiagram|C4Context|C4Container|C4Component|C4Dynamic|C4Deployment|block-beta|packet-beta|architecture-beta|sankey-beta|xychart-beta)[ \t]*|pie(?:[ \t]+(?:showData(?:[ \t]+title.*)?|title.*))?[ \t]*)$");
    private static readonly Regex HtmlToken = new(
        "<!--[\\s\\S]*?-->|<(?:(?:\"[^\"]*\")|(?:'[^']*')|[^'\">])*>",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));
    private static readonly Regex HtmlContainer = new("^<\\s*(pre|code|div)\\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));
    private static readonly Regex HtmlAttribute = new(
        "([a-zA-Z][a-zA-Z0-9:-]*)\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)'|([^\\s>]+))",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// Recognizes an explicitly identified Markdown code container. Ordinary editor HTML
    /// and other code languages retain their source colors, even when they contain # comments.
    /// </summary>
    internal static bool IsExplicitMarkdownHtml(string? html)
    {
        if (string.IsNullOrEmpty(html) || html.Length > MaxDocumentCharacters) return false;
        bool markdown = false;
        try
        {
            foreach (Match token in HtmlToken.Matches(html))
            {
                Match container = HtmlContainer.Match(token.Value);
                if (!container.Success) continue;
                foreach (Match attribute in HtmlAttribute.Matches(token.Value[container.Length..]))
                {
                    string name = attribute.Groups[1].Value.ToLowerInvariant();
                    string value = WebUtility.HtmlDecode(attribute.Groups[2].Success ? attribute.Groups[2].Value
                        : attribute.Groups[3].Success ? attribute.Groups[3].Value : attribute.Groups[4].Value);
                    IEnumerable<string> languages = name switch
                    {
                        "data-language" or "data-lang" => [value],
                        "lang" when !container.Groups[1].Value.Equals("div", StringComparison.OrdinalIgnoreCase) => [value],
                        "class" => value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                            .Select(ClassLanguage).Where(static language => language is not null).Select(static language => language!),
                        _ => [],
                    };
                    foreach (string language in languages)
                    {
                        if (language.Equals("markdown", StringComparison.OrdinalIgnoreCase)
                            || language.Equals("md", StringComparison.OrdinalIgnoreCase)
                            || language.Equals("mermaid", StringComparison.OrdinalIgnoreCase)) markdown = true;
                        // A fragment containing mixed/other language containers is ambiguous.
                        else return false;
                    }
                }
            }
            return markdown;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static string? ClassLanguage(string value)
    {
        foreach (string prefix in new[] { "language-", "lang-", "highlight-source-" })
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return value[prefix.Length..];
        return null;
    }

    internal static bool IsMarkdown(string sourceText)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        string inspected = Limit(sourceText, MaxPreviewCharacters);
        List<SourceLine> lines = ReadLines(inspected);
        if (IsPureMermaid(lines)) return true;
        bool hasHeadingContext = lines.Count(line => !string.IsNullOrWhiteSpace(line.Text)) >= 2
            && !lines.Any(line => CodeStatement.IsMatch(line.Text));
        int listItems = 0;
        int quoteLines = 0;
        for (int index = 0; index < lines.Count; index++)
        {
            string line = lines[index].Text;
            if (hasHeadingContext && Heading.IsMatch(line)) return true;
            if (TryFence(line, out char marker, out int length))
            {
                if (lines.Skip(index + 1).Any(next => IsClosingFence(next.Text, marker, length))) return true;
            }
            if (index + 1 < lines.Count && TryTableHeader(line, lines[index + 1].Text, out _)) return true;
            if (ListItemPattern.IsMatch(line) && ++listItems >= 2) return true;
            if (line.TrimStart().StartsWith("> ", StringComparison.Ordinal) && ++quoteLines >= 2) return true;
            if (TaskItem.IsMatch(line.TrimStart().TrimStart('-', '+', '*').TrimStart())) return true;
            // Inline punctuation alone is not enough evidence to change a plain-text pin.
            // An explicit Markdown clipboard language signal is handled separately.
        }
        return false;
    }

    /// <summary>Returns exact diagram bodies, indexed in document order, without executing any content.</summary>
    internal static IReadOnlyList<MarkdownMermaidBlock> ExtractMermaidBlocks(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length > MaxDocumentCharacters) return [];
        List<SourceLine> lines = ReadLines(source, MaxDocumentLines + 1);
        if (lines.Count > MaxDocumentLines) return [];
        if (IsPureMermaid(lines)) return [new MarkdownMermaidBlock(0, source)];
        var blocks = new List<MarkdownMermaidBlock>();
        for (int index = 0; index < lines.Count; index++)
        {
            if (!TryFence(lines[index].Text, out char marker, out int length)) continue;
            bool mermaid = IsMermaidFence(lines[index].Text, length);
            int start = lines[index].End;
            index++;
            while (index < lines.Count && !IsClosingFence(lines[index].Text, marker, length)) index++;
            int end = index < lines.Count ? lines[index].Start : lines[^1].End;
            if (mermaid) blocks.Add(new MarkdownMermaidBlock(blocks.Count, source[start..end]));
        }
        return blocks;
    }

    private static bool IsPureMermaid(List<SourceLine> lines)
    {
        string? first = lines.Select(line => line.Text.Trim().TrimStart('\uFEFF'))
            .FirstOrDefault(line => line.Length > 0 && !line.StartsWith("%%", StringComparison.Ordinal));
        return first is not null && MermaidDeclaration.IsMatch(first);
    }

    private static bool IsMermaidFence(string line, int markerLength) =>
        line.TrimStart()[markerLength..].Trim().Equals("mermaid", StringComparison.OrdinalIgnoreCase);

    /// <remarks>Call on an STA thread. The returned bitmap can be used across threads.</remarks>
    internal static BitmapSource Render(string sourceText) => Render(sourceText, 1);

    internal static BitmapSource Render(string sourceText, double scale) => Render(sourceText, scale, null);

    internal static BitmapSource Render(string sourceText, double scale,
        IReadOnlyDictionary<int, MermaidRenderResult>? results)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        ClipboardTextRenderer.ValidateScale(scale);
        FlowDocument document = CreateDocument(Limit(sourceText, MaxPreviewCharacters), results);
        bool formattingLimit = sourceText.Length > MaxDocumentCharacters
            || ReadLines(sourceText, MaxDocumentLines + 1).Count > MaxDocumentLines;
        if (formattingLimit)
        {
            // The extractor deliberately avoids unbounded browser work. Match that decision
            // here instead of showing a diagram placeholder which can never complete.
            document.Blocks.Clear();
            document.Blocks.Add(MakeCodeBlock(Limit(sourceText, MaxPreviewCharacters)));
        }
        KeepKoreanWordsTogether(document.Blocks);
        // A paginator has no text-input host or TSF attachment. Rendering happens on a
        // short-lived STA; the selectable RichTextBox belongs only to the UI-lived F2 view.
        document.Background = null;
        DocumentPaginator paginator = ((IDocumentPaginatorSource)document).DocumentPaginator;
        paginator.PageSize = new Size(PageWidthDip, PageHeightDip);
        var pages = new List<DocumentPage>();
        try
        {
            int maximumPages = (int)Math.Ceiling((double)MaxPreviewHeightDip / PageHeightDip);
            bool continued = sourceText.Length > MaxPreviewCharacters || formattingLimit;
            for (int index = 0; index <= maximumPages; index++)
            {
                DocumentPage page = paginator.GetPage(index);
                if (ReferenceEquals(page, DocumentPage.Missing)) break;
                if (index == maximumPages)
                {
                    continued = true;
                    page.Dispose();
                    break;
                }
                pages.Add(page);
            }
            var whitePage = new DrawingVisual();
            using (DrawingContext drawing = whitePage.RenderOpen())
                drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, PageWidthDip, PageHeightDip));
            var lastPreview = new RenderTargetBitmap(PageWidthDip, PageHeightDip, 96, 96, PixelFormats.Pbgra32);
            lastPreview.Render(whitePage);
            if (pages.Count > 0) lastPreview.Render(pages[^1].Visual);
            int naturalHeight = Math.Max(0, pages.Count - 1) * PageHeightDip + MeasurePaintedHeight(lastPreview);
            continued |= naturalHeight > MaxPreviewHeightDip;
            int contentHeight = Math.Min(naturalHeight, MaxPreviewHeightDip - (continued ? ContinuationHeightDip : 0));
            int height = contentHeight + (continued ? ContinuationHeightDip : 0);
            double rasterScale = Math.Min(scale, Math.Sqrt((double)MaxPreviewPixels / (PageWidthDip * height)));
            int pixelWidth = checked((int)Math.Ceiling(PageWidthDip * rasterScale));
            int pixelHeight = checked((int)Math.Ceiling(height * rasterScale));
            while ((long)pixelWidth * pixelHeight > MaxPreviewPixels)
            {
                rasterScale *= 0.9999;
                pixelWidth = checked((int)Math.Ceiling(PageWidthDip * rasterScale));
                pixelHeight = checked((int)Math.Ceiling(height * rasterScale));
            }
            if (pages.Count <= 1 && !continued && rasterScale == 1)
            {
                // Keep the common short-note path free of visual-brush composition.
                var cropped = new WriteableBitmap(new CroppedBitmap(lastPreview, new Int32Rect(0, 0, PageWidthDip, height)));
                cropped.Freeze();
                return cropped;
            }
            var composed = new DrawingVisual();
            using (DrawingContext drawing = composed.RenderOpen())
            {
                drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, PageWidthDip, height));
                drawing.PushClip(new RectangleGeometry(new Rect(0, 0, PageWidthDip, contentHeight)));
                for (int index = 0; index < pages.Count; index++)
                {
                    drawing.DrawRectangle(new VisualBrush(pages[index].Visual)
                    {
                        ViewboxUnits = BrushMappingMode.Absolute,
                        Viewbox = new Rect(0, 0, PageWidthDip, PageHeightDip),
                        Stretch = Stretch.Fill,
                    }, null, new Rect(0, index * PageHeightDip, PageWidthDip, PageHeightDip));
                }
                drawing.Pop();
                if (continued)
                {
                    var note = new FormattedText("나머지 내용은 F2에서 확인합니다.", CultureInfo.GetCultureInfo("ko-KR"),
                        FlowDirection.LeftToRight, new Typeface("Segoe UI"), 13, Brushes.DimGray, rasterScale);
                    drawing.DrawText(note, new Point(24, contentHeight + 8));
                }
            }
            var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96 * rasterScale, 96 * rasterScale, PixelFormats.Pbgra32);
            bitmap.Render(composed);
            bitmap.Freeze();
            return bitmap;
        }
        finally
        {
            foreach (DocumentPage page in pages) page.Dispose();
        }
    }

    private static int MeasurePaintedHeight(BitmapSource bitmap)
    {
        const int rowsPerStrip = 64;
        int stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * rowsPerStrip];
        for (int bottom = bitmap.PixelHeight; bottom > 0;)
        {
            int top = Math.Max(0, bottom - rowsPerStrip);
            int rows = bottom - top;
            bitmap.CopyPixels(new Int32Rect(0, top, bitmap.PixelWidth, rows), pixels, stride, 0);
            for (int row = rows - 1; row >= 0; row--)
                for (int column = 0; column < stride; column += 4)
                {
                    int pixel = row * stride + column;
                    if (pixels[pixel] != 255 || pixels[pixel + 1] != 255 || pixels[pixel + 2] != 255)
                        return Math.Clamp(top + row + 1 + 20, 80, 900);
                }
            bottom = top;
        }
        return 80;
    }

    /// <summary>Creates a fresh selectable document; fenced Paragraph.Tag holds the exact code body.</summary>
    /// <remarks>Call on the owning UI/STA thread. The caller retains the unmodified original text.</remarks>
    internal static FlowDocument CreateDocument(string sourceText,
        IReadOnlyDictionary<int, MermaidRenderResult>? results = null)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        var document = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 15,
            Foreground = Brushes.Black,
            Background = Brushes.White,
            PagePadding = new Thickness(24, 20, 24, 20),
            ColumnWidth = double.PositiveInfinity,
            TextAlignment = TextAlignment.Left,
        };
        // Limit formatting complexity, never the reading/searchable payload. A single run
        // is also the safe fallback for unusually large or highly fragmented documents.
        if (sourceText.Length > MaxDocumentCharacters)
        {
            document.Blocks.Add(new Paragraph(new Run(sourceText)));
            return document;
        }
        string source = sourceText;
        List<SourceLine> lines = ReadLines(source, MaxDocumentLines + 1);
        if (lines.Count > MaxDocumentLines)
        {
            document.Blocks.Add(new Paragraph(new Run(sourceText)));
            return document;
        }
        if (IsPureMermaid(lines))
        {
            AddMermaidBlock(document.Blocks, new MarkdownMermaidBlock(0, source), results);
            return document;
        }
        int index = 0;
        int mermaidIndex = 0;
        while (index < lines.Count)
        {
            string line = lines[index].Text;
            if (string.IsNullOrWhiteSpace(line)) { index++; continue; }
            if (TryFence(line, out char fence, out int fenceLength))
            {
                bool mermaid = IsMermaidFence(line, fenceLength);
                int bodyStart = lines[index].End;
                index++;
                while (index < lines.Count && !IsClosingFence(lines[index].Text, fence, fenceLength)) index++;
                int bodyEnd = index < lines.Count ? lines[index].Start : lines[^1].End;
                string code = source[bodyStart..bodyEnd];
                if (mermaid) AddMermaidBlock(document.Blocks, new MarkdownMermaidBlock(mermaidIndex++, code), results);
                else document.Blocks.Add(MakeCodeBlock(code));
                if (index < lines.Count) index++;
                continue;
            }
            if (IsHorizontalRule(line))
            {
                document.Blocks.Add(new Paragraph
                {
                    Tag = MarkdownBlockKind.HorizontalRule,
                    BorderBrush = Brushes.LightGray,
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    FontSize = 1,
                    LineHeight = 1,
                    Margin = new Thickness(0, 8, 0, 12),
                });
                index++;
                continue;
            }
            Match heading = Heading.Match(line);
            if (heading.Success)
            {
                int level = heading.Groups[1].Length;
                Paragraph paragraph = MakeParagraph(ClosingHeading.Replace(heading.Groups[2].Value, string.Empty));
                paragraph.Tag = level;
                paragraph.FontSize = level switch { 1 => 28, 2 => 23, 3 => 19, _ => 16 };
                paragraph.FontWeight = FontWeights.SemiBold;
                paragraph.Margin = new Thickness(0, level == 1 ? 0 : 10, 0, 10);
                paragraph.KeepWithNext = true;
                document.Blocks.Add(paragraph);
                index++;
                continue;
            }
            if (index + 1 < lines.Count && TryTableHeader(line, lines[index + 1].Text, out string[]? headers))
            {
                var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 6, 0, 12) };
                for (int column = 0; column < headers.Length; column++) table.Columns.Add(new TableColumn());
                var group = new TableRowGroup();
                table.RowGroups.Add(group);
                group.Rows.Add(MakeTableRow(headers, headers.Length, true));
                index += 2;
                while (index < lines.Count && lines[index].Text.Contains('|') && !string.IsNullOrWhiteSpace(lines[index].Text))
                {
                    group.Rows.Add(MakeTableRow(SplitCells(lines[index].Text), headers.Length, false));
                    index++;
                }
                document.Blocks.Add(table);
                continue;
            }
            Match item = ListItemPattern.Match(line);
            if (item.Success)
            {
                document.Blocks.Add(MakeList(lines, ref index));
                continue;
            }
            if (line.TrimStart().StartsWith(">", StringComparison.Ordinal))
            {
                var quote = new Section
                {
                    BorderBrush = Brushes.LightGray,
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(12, 2, 0, 2),
                    Margin = new Thickness(0, 4, 0, 12),
                    Foreground = Brushes.DimGray,
                };
                while (index < lines.Count && lines[index].Text.TrimStart().StartsWith(">", StringComparison.Ordinal))
                {
                    quote.Blocks.Add(MakeParagraph(lines[index].Text.TrimStart()[1..].TrimStart()));
                    index++;
                }
                document.Blocks.Add(quote);
                continue;
            }
            document.Blocks.Add(MakeParagraph(line));
            index++;
        }
        if (document.Blocks.Count == 0) document.Blocks.Add(new Paragraph());
        return document;
    }

    private static Paragraph MakeCodeBlock(string code) => new(new Run(code))
    {
        Tag = code,
        FontFamily = new FontFamily("Consolas"),
        FontSize = 14,
        Background = Brushes.WhiteSmoke,
        Padding = new Thickness(12),
        Margin = new Thickness(0, 5, 0, 12),
        KeepTogether = false,
    };

    private static void AddMermaidBlock(BlockCollection blocks, MarkdownMermaidBlock block,
        IReadOnlyDictionary<int, MermaidRenderResult>? results)
    {
        if (results is null || !results.TryGetValue(block.Index, out MermaidRenderResult? result))
        {
            blocks.Add(new Paragraph(new Run("그리는 중"))
            {
                Tag = block,
                Foreground = Brushes.DimGray,
                Background = Brushes.WhiteSmoke,
                Padding = new Thickness(12),
                Margin = new Thickness(0, 5, 0, 12),
            });
            return;
        }
        if (result.Image is { } bitmap && bitmap.Width > 0 && bitmap.Height > 0)
        {
            double fit = Math.Min(1, Math.Min(592 / bitmap.Width, 700 / bitmap.Height));
            var image = new Image
            {
                Source = bitmap,
                Width = bitmap.Width * fit,
                Height = bitmap.Height * fit,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Left,
                IsHitTestVisible = false,
                Focusable = false,
            };
            blocks.Add(new BlockUIContainer(image) { Tag = block, Margin = new Thickness(0, 5, 0, 12) });
            return;
        }
        blocks.Add(MakeCodeBlock(block.Source));
        blocks.Add(new Paragraph(new Run("그림을 표시할 수 없어 원문을 보여줍니다."))
        {
            Foreground = Brushes.DimGray,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 12),
        });
    }

    private static System.Windows.Documents.List MakeList(List<SourceLine> lines, ref int index)
    {
        Match first = ListItemPattern.Match(lines[index].Text);
        bool ordered = first.Groups["number"].Success;
        bool tasks = TaskItem.IsMatch(first.Groups["body"].Value);
        int indent = ListIndent(first);
        var list = new System.Windows.Documents.List
        {
            MarkerStyle = tasks ? TextMarkerStyle.None : ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(tasks ? 4 : 23, 0, 0, 0),
            StartIndex = ordered ? Math.Max(1, int.Parse(first.Groups["number"].Value, CultureInfo.InvariantCulture)) : 1,
        };
        while (index < lines.Count)
        {
            Match match = ListItemPattern.Match(lines[index].Text);
            if (!match.Success || IsHorizontalRule(lines[index].Text) || ListIndent(match) < indent) break;
            if (ListIndent(match) > indent)
            {
                if (list.ListItems.LastListItem is null) break;
                list.ListItems.LastListItem.Blocks.Add(MakeList(lines, ref index));
                continue;
            }
            if (match.Groups["number"].Success != ordered || TaskItem.IsMatch(match.Groups["body"].Value) != tasks) break;
            string body = match.Groups["body"].Value;
            Match task = TaskItem.Match(body);
            Paragraph paragraph = MakeParagraph(task.Success ? task.Groups["body"].Value : body);
            if (task.Success)
            {
                // A text glyph remains selectable and has no interactive checkbox state.
                var box = new Run(task.Groups["done"].Value == " " ? "☐ " : "☑ ")
                {
                    FontFamily = new FontFamily("Segoe UI Symbol"),
                };
                if (paragraph.Inlines.FirstInline is { } firstInline) paragraph.Inlines.InsertBefore(firstInline, box);
                else paragraph.Inlines.Add(box);
            }
            list.ListItems.Add(new ListItem(paragraph));
            index++;
        }
        return list;
    }

    private static int ListIndent(Match match) => match.Groups["indent"].Value.Sum(character => character == '\t' ? 4 : 1);

    private static bool IsHorizontalRule(string line)
    {
        string trimmed = line.Trim();
        if (trimmed.Length < 3 || trimmed[0] is not ('-' or '*' or '_')) return false;
        char marker = trimmed[0];
        return trimmed.Count(character => character == marker) >= 3
            && trimmed.All(character => character == marker || character is ' ' or '\t');
    }

    private static void KeepKoreanWordsTogether(BlockCollection blocks)
    {
        foreach (Block block in blocks.Cast<Block>().ToArray())
            switch (block)
            {
                case Paragraph paragraph when paragraph.Tag is not string:
                    KeepWordsInInlines(paragraph.Inlines);
                    break;
                case Section section:
                    KeepKoreanWordsTogether(section.Blocks);
                    break;
                case System.Windows.Documents.List list:
                    foreach (ListItem item in list.ListItems.Cast<ListItem>().ToArray()) KeepKoreanWordsTogether(item.Blocks);
                    break;
                case Table table:
                    foreach (TableRowGroup group in table.RowGroups.Cast<TableRowGroup>().ToArray())
                    foreach (TableRow row in group.Rows.Cast<TableRow>().ToArray())
                    foreach (TableCell cell in row.Cells.Cast<TableCell>().ToArray()) KeepKoreanWordsTogether(cell.Blocks);
                    break;
            }
    }

    private static void KeepWordsInInlines(InlineCollection inlines)
    {
        foreach (Inline inline in inlines.Cast<Inline>().ToArray())
        {
            if (inline.FontFamily.Source == "Consolas") continue;
            if (inline is Span span) { KeepWordsInInlines(span.Inlines); continue; }
            if (inline is not Run run || !run.Text.Any(IsHangul)) continue;
            // Only the disposable bitmap document receives nonbreaking hints. Selectable
            // documents, exact source copies, HTML and fenced/inline code stay unchanged.
            var text = new StringBuilder(run.Text.Length * 2);
            for (int index = 0; index < run.Text.Length; index++)
            {
                char current = run.Text[index];
                text.Append(current);
                if (index + 1 < run.Text.Length && (IsHangul(current) || IsHangul(run.Text[index + 1]))
                    && char.IsLetterOrDigit(current) && char.IsLetterOrDigit(run.Text[index + 1])) text.Append('\u2060');
            }
            run.Text = text.ToString();
        }
    }

    private static bool IsHangul(char character) => character is >= '\uAC00' and <= '\uD7AF'
        or >= '\u1100' and <= '\u11FF' or >= '\u3130' and <= '\u318F';

    /// <summary>Produces safe fragment HTML from the same document; call on an STA thread.</summary>
    internal static string ToHtml(string sourceText)
    {
        var html = new StringBuilder("<div style=\"font-family:Segoe UI,sans-serif;font-size:15px;color:#202124;background-color:#ffffff\">");
        foreach (Block block in CreateDocument(sourceText).Blocks) AppendBlockHtml(html, block);
        return html.Append("</div>").ToString();
    }

    private static void AppendBlockHtml(StringBuilder html, Block block)
    {
        switch (block)
        {
            case Paragraph { Tag: MarkdownBlockKind.HorizontalRule }:
                html.Append("<hr>");
                break;
            case Paragraph { Tag: MarkdownMermaidBlock pending }:
                html.Append("<pre><code>").Append(WebUtility.HtmlEncode(pending.Source)).Append("</code></pre>");
                break;
            case Paragraph { Tag: string code }:
                html.Append("<pre style=\"font-family:Consolas,monospace;background-color:#f5f5f5;padding:12px;white-space:pre-wrap\"><code>");
                html.Append(WebUtility.HtmlEncode(code)).Append("</code></pre>");
                break;
            case Paragraph paragraph:
                string tag = paragraph.Tag is int level ? "h" + level.ToString(CultureInfo.InvariantCulture) : "p";
                html.Append('<').Append(tag).Append('>');
                foreach (Inline inline in paragraph.Inlines) AppendInlineHtml(html, inline);
                html.Append("</").Append(tag).Append('>');
                break;
            case System.Windows.Documents.List list:
                bool ordered = list.MarkerStyle == TextMarkerStyle.Decimal;
                html.Append(ordered ? "<ol start=\"" + list.StartIndex.ToString(CultureInfo.InvariantCulture) + "\">" : "<ul>");
                foreach (ListItem item in list.ListItems)
                {
                    html.Append("<li>");
                    foreach (Block child in item.Blocks) AppendBlockHtml(html, child);
                    html.Append("</li>");
                }
                html.Append(ordered ? "</ol>" : "</ul>");
                break;
            case Section section:
                html.Append("<blockquote style=\"border-left:3px solid #d3d3d3;padding-left:12px;color:#696969\">");
                foreach (Block child in section.Blocks) AppendBlockHtml(html, child);
                html.Append("</blockquote>");
                break;
            case Table table:
                html.Append("<table style=\"border-collapse:collapse\">");
                foreach (TableRowGroup group in table.RowGroups)
                foreach (TableRow row in group.Rows)
                {
                    html.Append("<tr>");
                    foreach (TableCell cell in row.Cells)
                    {
                        bool header = cell.FontWeight == FontWeights.SemiBold;
                        string cellTag = header ? "th" : "td";
                        html.Append('<').Append(cellTag).Append(" style=\"border:1px solid #d3d3d3;padding:6px 9px;text-align:left\">");
                        foreach (Block child in cell.Blocks) AppendBlockHtml(html, child);
                        html.Append("</").Append(cellTag).Append('>');
                    }
                    html.Append("</tr>");
                }
                html.Append("</table>");
                break;
        }
    }

    private static void AppendInlineHtml(StringBuilder html, Inline inline)
    {
        if (inline is Run run) { html.Append(WebUtility.HtmlEncode(run.Text)); return; }
        if (inline is LineBreak) { html.Append("<br>"); return; }
        if (inline is not Span span) return;
        string tag = span switch { Bold => "strong", Italic => "em", _ when span.TextDecorations == TextDecorations.Strikethrough => "del", _ => "span" };
        html.Append('<').Append(tag);
        if (span.FontFamily.Source == "Consolas") html.Append(" style=\"font-family:Consolas,monospace;background-color:#f5f5f5\"");
        html.Append('>');
        foreach (Inline child in span.Inlines) AppendInlineHtml(html, child);
        html.Append("</").Append(tag).Append('>');
    }

    private static Paragraph MakeParagraph(string text)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 7), LineHeight = 23 };
        int offset = 0;
        foreach (Match match in InlinePattern.Matches(text))
        {
            if (match.Index > offset) paragraph.Inlines.Add(new Run(text[offset..match.Index]));
            if (match.Groups["escape"].Success) paragraph.Inlines.Add(new Run(match.Value[1..]));
            else if (match.Groups["code"].Success)
            {
                paragraph.Inlines.Add(new Span(new Run(match.Value[1..^1])) { FontFamily = new FontFamily("Consolas"), Background = Brushes.WhiteSmoke });
            }
            else if (match.Groups["strong"].Success) paragraph.Inlines.Add(new Bold(new Run(UnescapePunctuation(match.Value[2..^2]))));
            else if (match.Groups["strike"].Success) paragraph.Inlines.Add(new Span(new Run(UnescapePunctuation(match.Value[2..^2]))) { TextDecorations = TextDecorations.Strikethrough });
            else if (match.Groups["emphasis"].Success) paragraph.Inlines.Add(new Italic(new Run(UnescapePunctuation(match.Value[1..^1]))));
            else
            {
                // Display destinations but never construct navigation-capable Hyperlink or Image objects.
                string label = UnescapePunctuation(match.Groups["label"].Value);
                string destination = UnescapePunctuation(match.Groups["destination"].Value);
                paragraph.Inlines.Add(new Run(label + " (" + destination + ")") { Foreground = Brushes.SteelBlue, TextDecorations = TextDecorations.Underline });
            }
            offset = match.Index + match.Length;
        }
        if (offset < text.Length) paragraph.Inlines.Add(new Run(text[offset..]));
        return paragraph;
    }

    private static string UnescapePunctuation(string text)
    {
        var unescaped = new StringBuilder(text.Length);
        for (int index = 0; index < text.Length; index++)
        {
            if (text[index] == '\\' && index + 1 < text.Length
                && text[index + 1] is >= '!' and <= '/' or >= ':' and <= '@' or >= '[' and <= '`' or >= '{' and <= '~')
                index++;
            unescaped.Append(text[index]);
        }
        return unescaped.ToString();
    }

    private static TableRow MakeTableRow(string[] values, int columns, bool header)
    {
        var row = new TableRow();
        for (int column = 0; column < columns; column++)
        {
            string value = column < values.Length ? values[column] : string.Empty;
            if (column == columns - 1 && values.Length > columns) value = string.Join(" | ", values.Skip(column));
            row.Cells.Add(new TableCell(MakeParagraph(value))
            {
                BorderBrush = Brushes.LightGray,
                BorderThickness = new Thickness(0.5),
                Padding = new Thickness(9, 6, 9, 6),
                FontWeight = header ? FontWeights.SemiBold : FontWeights.Normal,
                Background = header ? Brushes.WhiteSmoke : Brushes.Transparent,
            });
        }
        return row;
    }

    private static bool TryTableHeader(string header, string separator, out string[] headers)
    {
        headers = [];
        if (!header.Contains('|') || !separator.Contains('|')) return false;
        string[] cells = SplitCells(separator);
        if (cells.Length == 0 || cells.Length > MaxColumns || cells.Any(cell => !SeparatorCell.IsMatch(cell))) return false;
        headers = SplitCells(header);
        return headers.Length == cells.Length;
    }

    private static string[] SplitCells(string line)
    {
        string body = line.Trim();
        var cells = new List<string>();
        int start = 0;
        for (int index = 0; index < body.Length; index++)
        {
            if (body[index] == '\\' && index + 1 < body.Length) { index++; continue; }
            if (body[index] != '|') continue;
            if (index > 0) cells.Add(body[start..index].Trim());
            start = index + 1;
        }
        if (start < body.Length || !body.EndsWith('|')) cells.Add(body[start..].Trim());
        return cells.ToArray();
    }

    private static bool TryFence(string line, out char marker, out int length)
    {
        string trimmed = line.TrimStart();
        marker = trimmed.Length > 0 ? trimmed[0] : '\0';
        length = 0;
        if (line.Length - trimmed.Length > 3 || marker is not ('`' or '~')) return false;
        while (length < trimmed.Length && trimmed[length] == marker) length++;
        return length >= 3;
    }

    private static bool IsClosingFence(string line, char marker, int minimumLength) =>
        TryFence(line, out char actual, out int length) && actual == marker && length >= minimumLength
        && string.IsNullOrWhiteSpace(line.TrimStart()[length..]);

    private static List<SourceLine> ReadLines(string source, int maximum = int.MaxValue)
    {
        var result = new List<SourceLine>();
        int start = 0;
        for (int index = 0; index < source.Length; index++)
        {
            if (source[index] is not ('\r' or '\n')) continue;
            int end = index + 1;
            if (source[index] == '\r' && end < source.Length && source[end] == '\n') end++;
            result.Add(new SourceLine(source[start..index], start, end));
            if (result.Count == maximum) return result;
            index = end - 1;
            start = end;
        }
        if (start < source.Length || result.Count == 0) result.Add(new SourceLine(source[start..], start, source.Length));
        return result;
    }

    private static string Limit(string value, int maximum)
    {
        if (value.Length <= maximum) return value;
        if (char.IsHighSurrogate(value[maximum - 1])) maximum--;
        return value[..maximum];
    }

    private static Regex Pattern(string pattern) => new(pattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));
    private enum MarkdownBlockKind { HorizontalRule }
    private readonly record struct SourceLine(string Text, int Start, int End);
}
