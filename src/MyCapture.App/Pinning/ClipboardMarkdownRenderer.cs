using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
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
    private static readonly Regex Heading = Pattern(@"^ {0,3}(#{1,6})\s+(\S.*)$");
    private static readonly Regex ClosingHeading = Pattern(@"\s+#+\s*$");
    private static readonly Regex ListItemPattern = Pattern(@"^\s{0,12}(?:(?<bullet>[-+*])|(?<number>\d{1,6})[.)])\s+(?<body>.+)$");
    private static readonly Regex TaskItem = Pattern(@"^\[(?<done>[ xX])\]\s+(?<body>.*)$");
    private static readonly Regex SeparatorCell = Pattern(@"^:?-{3,}:?$");
    private static readonly Regex CodeStatement = Pattern(@"^\s*(?:(?:import|using|namespace|class|def|function|const|let|var)\s+|[\w.$]+\([^\r\n]*\)\s*;?\s*$|#!)");
    private static readonly Regex InlinePattern = Pattern(
        @"(?<escape>\\[\x21-\x2f\x3a-\x40\x5b-\x60\x7b-\x7e])|(?<code>`[^`\r\n]+`)|(?<strong>\*\*(?:\\[^\r\n]|[^*\\\r\n])+\*\*|__(?:\\[^\r\n]|[^_\\\r\n])+__)|(?<emphasis>\*(?:\\[^\r\n]|[^*\\\r\n])+\*|_(?:\\[^\r\n]|[^_\\\r\n])+_)|(?<link>!?\[(?<label>(?:\\[^\r\n]|[^\]\\\r\n])+)\]\((?<destination>(?:\\[^\r\n]|[^\s)\\\r\n])+)\))");
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
                            || language.Equals("md", StringComparison.OrdinalIgnoreCase)) markdown = true;
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

    /// <remarks>Call on an STA thread. The returned bitmap can be used across threads.</remarks>
    internal static BitmapSource Render(string sourceText) => Render(sourceText, 1);

    internal static BitmapSource Render(string sourceText, double scale)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        ClipboardTextRenderer.ValidateScale(scale);
        FlowDocument document = CreateDocument(Limit(sourceText, MaxPreviewCharacters));
        // A paginator has no text-input host or TSF attachment. Rendering happens on a
        // short-lived STA; the selectable RichTextBox belongs only to the UI-lived F2 view.
        document.Background = null;
        DocumentPaginator paginator = ((IDocumentPaginatorSource)document).DocumentPaginator;
        paginator.PageSize = new Size(640, 900);
        using DocumentPage page = paginator.GetPage(0);
        var background = new DrawingVisual();
        using (DrawingContext drawing = background.RenderOpen())
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, 640, 900));
        // The paginator visual includes its entire page in its bounds, even for one line.
        // Measure painted content at a stable 96 DPI so the logical size also stays stable
        // when the pin moves between monitors. Scan strips rather than a second full buffer.
        var preview = new RenderTargetBitmap(640, 900, 96, 96, PixelFormats.Pbgra32);
        preview.Render(background);
        preview.Render(page.Visual);
        int height = MeasurePaintedHeight(preview);
        if (scale == 1)
        {
            if (height == preview.PixelHeight)
            {
                preview.Freeze();
                return preview;
            }
            // Copy the crop so a short pin does not retain the full-height page buffer.
            var cropped = new WriteableBitmap(new CroppedBitmap(preview, new Int32Rect(0, 0, 640, height)));
            cropped.Freeze();
            return cropped;
        }
        var bitmap = new RenderTargetBitmap(checked((int)Math.Ceiling(640 * scale)),
            checked((int)Math.Ceiling(height * scale)), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(background);
        bitmap.Render(page.Visual);
        bitmap.Freeze();
        return bitmap;
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
    internal static FlowDocument CreateDocument(string sourceText)
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
        int index = 0;
        while (index < lines.Count)
        {
            string line = lines[index].Text;
            if (string.IsNullOrWhiteSpace(line)) { index++; continue; }
            if (TryFence(line, out char fence, out int fenceLength))
            {
                int bodyStart = lines[index].End;
                index++;
                while (index < lines.Count && !IsClosingFence(lines[index].Text, fence, fenceLength)) index++;
                int bodyEnd = index < lines.Count ? lines[index].Start : lines[^1].End;
                string code = source[bodyStart..bodyEnd];
                var codeBlock = new Paragraph(new Run(code))
                {
                    Tag = code,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 14,
                    Background = Brushes.WhiteSmoke,
                    Padding = new Thickness(12),
                    Margin = new Thickness(0, 5, 0, 12),
                    KeepTogether = false,
                };
                document.Blocks.Add(codeBlock);
                if (index < lines.Count) index++;
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
                bool ordered = item.Groups["number"].Success;
                var list = new System.Windows.Documents.List
                {
                    MarkerStyle = ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                    Margin = new Thickness(0, 0, 0, 10),
                    Padding = new Thickness(23, 0, 0, 0),
                    StartIndex = ordered ? Math.Max(1, int.Parse(item.Groups["number"].Value, CultureInfo.InvariantCulture)) : 1,
                };
                while (index < lines.Count && (item = ListItemPattern.Match(lines[index].Text)).Success
                    && item.Groups["number"].Success == ordered)
                {
                    string body = item.Groups["body"].Value;
                    Match task = TaskItem.Match(body);
                    if (task.Success)
                    {
                        body = (task.Groups["done"].Value == " " ? "☐ " : "☑ ") + task.Groups["body"].Value;
                    }
                    list.ListItems.Add(new ListItem(MakeParagraph(body)));
                    index++;
                }
                document.Blocks.Add(list);
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
        string tag = span switch { Bold => "strong", Italic => "em", _ => "span" };
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
            else if (match.Groups["emphasis"].Success) paragraph.Inlines.Add(new Italic(new Run(UnescapePunctuation(match.Value[1..^1]))));
            else
            {
                // Display destinations but never construct navigation-capable Hyperlink or Image objects.
                string label = UnescapePunctuation(match.Groups["label"].Value);
                string destination = UnescapePunctuation(match.Groups["destination"].Value);
                paragraph.Inlines.Add(new Run(label + " (" + destination + ")") { Foreground = Brushes.SteelBlue });
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
    private readonly record struct SourceLine(string Text, int Start, int End);
}
