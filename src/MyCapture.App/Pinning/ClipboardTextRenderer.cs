using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MyCapture.App.Pinning;

/// <summary>
/// Creates a bounded, immutable preview bitmap for exact text captured from the clipboard.
/// </summary>
/// <remarks>
/// The caller continues to own the original string (and can therefore put that exact value
/// back on the clipboard). This renderer only builds a capped visual preview: plain text is
/// presented as a readable card, while tab-delimited text is presented as a grid whose empty
/// cells remain visible. All dimensions and typography are fixed at 96 DPI so the same input
/// produces the same layout independently of the current monitor scale.
/// </remarks>
internal static class ClipboardTextRenderer
{
    private const int Dpi = 96;
    private const int MaxBitmapWidth = 1200;
    private const int MaxBitmapHeight = 900;

    private const int PlainMinimumWidth = 240;
    private const int PlainMinimumHeight = 80;
    private const int PlainHorizontalPadding = 24;
    private const int PlainVerticalPadding = 20;
    private const int PlainFontSize = 16;
    private const int PlainLineHeight = 24;
    private const int MaxPlainPreviewCharacters = 16_384;

    private const int TableMargin = 16;
    private const int TableRowHeight = 34;
    private const int TableFontSize = 14;
    private const int TableHorizontalPadding = 12;
    private const int TableVerticalPadding = 7;
    private const int MinimumColumnWidth = 72;
    private const int MaximumColumnWidth = 320;
    private const int MaxTableRows = 24;
    private const int MaxTableColumns = 12;
    private const int MaxCellPreviewCharacters = 160;
    private const int MaxTableInspectedCharacters = 65_536;

    private static readonly CultureInfo TextCulture = CultureInfo.GetCultureInfo("ko-KR");
    private static readonly Typeface TextTypeface = new(
        new FontFamily("Segoe UI"),
        FontStyles.Normal,
        FontWeights.Normal,
        FontStretches.Normal);

    private static readonly Brush CardBackground = FrozenBrush(0xFF, 0xFC, 0xFC, 0xFD);
    private static readonly Brush TableBackground = FrozenBrush(0xFF, 0xFF, 0xFF, 0xFF);
    private static readonly Brush AlternateRowBackground = FrozenBrush(0xFF, 0xF8, 0xFA, 0xFC);
    private static readonly Brush TextForeground = FrozenBrush(0xFF, 0x1F, 0x29, 0x37);
    private static readonly Pen CardBorder = FrozenPen(0xFF, 0xCB, 0xD5, 0xE1);
    private static readonly Pen GridLine = FrozenPen(0xFF, 0xD1, 0xD5, 0xDB);
    private static readonly Pen GridBorder = FrozenPen(0xFF, 0x9C, 0xA3, 0xAF);

    /// <summary>
    /// Renders <paramref name="sourceText"/> as a frozen bitmap. The input string is never
    /// retained; only a bounded visual projection is materialised.
    /// </summary>
    /// <remarks>
    /// Invoke this WPF rasterisation entry point from an STA thread. The frozen result can be
    /// consumed safely from any thread after this method returns.
    /// </remarks>
    internal static BitmapSource Render(string sourceText, bool isTabularContent)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        return isTabularContent
            ? RenderTable(sourceText)
            : RenderPlainText(sourceText);
    }

    private static BitmapSource RenderPlainText(string sourceText)
    {
        string preview = CreatePlainPreview(sourceText);
        FormattedText? text = preview.Length == 0
            ? null
            : CreateFormattedText(
                preview,
                PlainFontSize,
                MaxBitmapWidth - (PlainHorizontalPadding * 2),
                MaxBitmapHeight - (PlainVerticalPadding * 2),
                singleLine: false);

        if (text is not null)
        {
            text.LineHeight = PlainLineHeight;
        }

        double measuredWidth = text?.WidthIncludingTrailingWhitespace ?? 0;
        double measuredHeight = text?.Height ?? 0;
        int width = Math.Clamp(
            (int)Math.Ceiling(measuredWidth + (PlainHorizontalPadding * 2)),
            PlainMinimumWidth,
            MaxBitmapWidth);
        int height = Math.Clamp(
            (int)Math.Ceiling(measuredHeight + (PlainVerticalPadding * 2)),
            PlainMinimumHeight,
            MaxBitmapHeight);

        var visual = CreateVisual();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            var bounds = new Rect(0.5, 0.5, width - 1, height - 1);
            drawing.DrawRoundedRectangle(CardBackground, CardBorder, bounds, 10, 10);
            if (text is not null)
            {
                drawing.DrawText(text, new Point(PlainHorizontalPadding, PlainVerticalPadding));
            }
        }

        return RenderFrozen(visual, width, height);
    }

    private static BitmapSource RenderTable(string sourceText)
    {
        TablePreview preview = ParseTablePreview(sourceText);
        int[] columnWidths = MeasureColumnWidths(preview);
        int tableWidth = columnWidths.Sum();
        int width = Math.Min(MaxBitmapWidth, tableWidth + (TableMargin * 2));
        int height = Math.Min(
            MaxBitmapHeight,
            (preview.Rows.Count * TableRowHeight) + (TableMargin * 2));

        var visual = CreateVisual();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(CardBackground, null, new Rect(0, 0, width, height));
            var tableBounds = new Rect(
                TableMargin + 0.5,
                TableMargin + 0.5,
                tableWidth - 1,
                (preview.Rows.Count * TableRowHeight) - 1);
            drawing.DrawRectangle(TableBackground, null, tableBounds);

            for (int rowIndex = 0; rowIndex < preview.Rows.Count; rowIndex++)
            {
                double rowTop = TableMargin + (rowIndex * TableRowHeight);
                if ((rowIndex & 1) == 1)
                {
                    drawing.DrawRectangle(
                        AlternateRowBackground,
                        null,
                        new Rect(TableMargin, rowTop, tableWidth, TableRowHeight));
                }

                DrawTableRow(drawing, preview.Rows[rowIndex], columnWidths, rowTop);
            }

            DrawGrid(drawing, columnWidths, preview.Rows.Count, tableWidth);
        }

        return RenderFrozen(visual, width, height);
    }

    private static DrawingVisual CreateVisual()
    {
        var visual = new DrawingVisual();
        TextOptions.SetTextFormattingMode(visual, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(visual, TextRenderingMode.Grayscale);
        return visual;
    }

    private static void DrawTableRow(
        DrawingContext drawing,
        IReadOnlyList<string> row,
        IReadOnlyList<int> columnWidths,
        double rowTop)
    {
        double left = TableMargin;
        for (int columnIndex = 0; columnIndex < columnWidths.Count; columnIndex++)
        {
            int columnWidth = columnWidths[columnIndex];
            string value = columnIndex < row.Count ? row[columnIndex] : string.Empty;
            if (value.Length > 0)
            {
                double availableWidth = Math.Max(1, columnWidth - (TableHorizontalPadding * 2));
                FormattedText text = CreateFormattedText(
                    value,
                    TableFontSize,
                    availableWidth,
                    TableRowHeight - (TableVerticalPadding * 2),
                    singleLine: true);
                double textTop = rowTop + ((TableRowHeight - text.Height) / 2);
                drawing.DrawText(text, new Point(left + TableHorizontalPadding, textTop));
            }

            left += columnWidth;
        }
    }

    private static void DrawGrid(
        DrawingContext drawing,
        IReadOnlyList<int> columnWidths,
        int rowCount,
        int tableWidth)
    {
        double top = TableMargin + 0.5;
        double bottom = TableMargin + (rowCount * TableRowHeight) - 0.5;
        double left = TableMargin + 0.5;
        double right = TableMargin + tableWidth - 0.5;

        double x = TableMargin;
        for (int columnIndex = 1; columnIndex < columnWidths.Count; columnIndex++)
        {
            x += columnWidths[columnIndex - 1];
            drawing.DrawLine(GridLine, new Point(x + 0.5, top), new Point(x + 0.5, bottom));
        }

        for (int rowIndex = 1; rowIndex < rowCount; rowIndex++)
        {
            double y = TableMargin + (rowIndex * TableRowHeight) + 0.5;
            drawing.DrawLine(GridLine, new Point(left, y), new Point(right, y));
        }

        drawing.DrawRectangle(
            null,
            GridBorder,
            new Rect(left, top, tableWidth - 1, (rowCount * TableRowHeight) - 1));
    }

    private static int[] MeasureColumnWidths(TablePreview preview)
    {
        var widths = Enumerable.Repeat(MinimumColumnWidth, preview.ColumnCount).ToArray();
        foreach (IReadOnlyList<string> row in preview.Rows)
        {
            int cellCount = Math.Min(row.Count, widths.Length);
            for (int columnIndex = 0; columnIndex < cellCount; columnIndex++)
            {
                if (row[columnIndex].Length == 0)
                {
                    continue;
                }

                FormattedText text = CreateFormattedText(
                    row[columnIndex],
                    TableFontSize,
                    MaximumColumnWidth - (TableHorizontalPadding * 2),
                    TableRowHeight - (TableVerticalPadding * 2),
                    singleLine: true);
                int preferred = (int)Math.Ceiling(
                    Math.Min(
                        MaximumColumnWidth,
                        text.WidthIncludingTrailingWhitespace + (TableHorizontalPadding * 2)));
                widths[columnIndex] = Math.Max(widths[columnIndex], preferred);
            }
        }

        FitColumns(widths, MaxBitmapWidth - (TableMargin * 2));
        return widths;
    }

    private static void FitColumns(int[] widths, int availableWidth)
    {
        int total = widths.Sum();
        if (total <= availableWidth)
        {
            return;
        }

        int minimumTotal = MinimumColumnWidth * widths.Length;
        int availableFlexibleWidth = availableWidth - minimumTotal;
        int totalFlexibleWidth = total - minimumTotal;
        int assignedFlexibleWidth = 0;
        double cumulativeShare = 0;

        for (int index = 0; index < widths.Length; index++)
        {
            cumulativeShare += (widths[index] - MinimumColumnWidth)
                * availableFlexibleWidth
                / (double)totalFlexibleWidth;
            int nextAssigned = (int)Math.Round(
                cumulativeShare,
                MidpointRounding.ToEven);
            widths[index] = MinimumColumnWidth + (nextAssigned - assignedFlexibleWidth);
            assignedFlexibleWidth = nextAssigned;
        }
    }

    private static FormattedText CreateFormattedText(
        string value,
        double fontSize,
        double maxWidth,
        double maxHeight,
        bool singleLine)
    {
        var text = new FormattedText(
            value,
            TextCulture,
            FlowDirection.LeftToRight,
            TextTypeface,
            fontSize,
            TextForeground,
            pixelsPerDip: 1)
        {
            MaxTextWidth = Math.Max(1, maxWidth),
            MaxTextHeight = Math.Max(1, maxHeight),
            Trimming = TextTrimming.CharacterEllipsis,
        };

        if (singleLine)
        {
            text.MaxLineCount = 1;
        }

        return text;
    }

    private static string CreatePlainPreview(string sourceText)
    {
        if (sourceText.Length <= MaxPlainPreviewCharacters)
        {
            return sourceText;
        }

        int length = MaxPlainPreviewCharacters;
        if (char.IsHighSurrogate(sourceText[length - 1]))
        {
            length--;
        }

        return string.Concat(sourceText.AsSpan(0, length), "\u2026");
    }

    private static TablePreview ParseTablePreview(string sourceText)
    {
        var rows = new List<IReadOnlyList<string>>(MaxTableRows);
        var row = new List<string>(MaxTableColumns);
        var cell = new StringBuilder(MaxCellPreviewCharacters);
        bool cellWasTruncated = false;
        int logicalColumn = 0;
        int inspectedCharacters = 0;
        int index = 0;

        while (index < sourceText.Length
               && rows.Count < MaxTableRows
               && inspectedCharacters < MaxTableInspectedCharacters)
        {
            char current = sourceText[index];
            if (current == '\t')
            {
                FinishVisibleCell(row, cell, cellWasTruncated, logicalColumn);
                cell.Clear();
                cellWasTruncated = false;
                logicalColumn++;
                index++;
                inspectedCharacters++;
                continue;
            }

            if (current is '\r' or '\n')
            {
                FinishVisibleCell(row, cell, cellWasTruncated, logicalColumn);
                rows.Add(row.ToArray());
                row = new List<string>(MaxTableColumns);
                cell.Clear();
                cellWasTruncated = false;
                logicalColumn = 0;

                int delimiterLength = current == '\r'
                    && index + 1 < sourceText.Length
                    && sourceText[index + 1] == '\n'
                        ? 2
                        : 1;
                index += delimiterLength;
                inspectedCharacters += delimiterLength;
                continue;
            }

            if (logicalColumn < MaxTableColumns)
            {
                if (cell.Length < MaxCellPreviewCharacters)
                {
                    cell.Append(current);
                }
                else
                {
                    cellWasTruncated = true;
                }
            }

            index++;
            inspectedCharacters++;
        }

        if (rows.Count < MaxTableRows)
        {
            if (index < sourceText.Length && logicalColumn < MaxTableColumns)
            {
                cellWasTruncated = true;
            }

            FinishVisibleCell(row, cell, cellWasTruncated, logicalColumn);
            rows.Add(row.ToArray());
        }

        if (rows.Count == 0)
        {
            rows.Add([string.Empty]);
        }

        int columnCount = Math.Clamp(rows.Max(static currentRow => currentRow.Count), 1, MaxTableColumns);
        return new TablePreview(rows, columnCount);
    }

    private static void FinishVisibleCell(
        ICollection<string> row,
        StringBuilder cell,
        bool wasTruncated,
        int logicalColumn)
    {
        if (logicalColumn >= MaxTableColumns)
        {
            return;
        }

        int length = cell.Length;
        if (wasTruncated && length > 0 && char.IsHighSurrogate(cell[length - 1]))
        {
            length--;
        }

        string value = length == cell.Length
            ? cell.ToString()
            : cell.ToString(0, length);
        row.Add(wasTruncated ? string.Concat(value, "\u2026") : value);
    }

    private static BitmapSource RenderFrozen(DrawingVisual visual, int width, int height)
    {
        var target = new RenderTargetBitmap(width, height, Dpi, Dpi, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    private static SolidColorBrush FrozenBrush(byte alpha, byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(byte alpha, byte red, byte green, byte blue)
    {
        var pen = new Pen(FrozenBrush(alpha, red, green, blue), 1);
        pen.Freeze();
        return pen;
    }

    private readonly record struct TablePreview(
        IReadOnlyList<IReadOnlyList<string>> Rows,
        int ColumnCount);
}
