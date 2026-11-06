using MyCapture.App.Themes;
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
/// cells remain visible. Layout uses fixed DIP typography, while the target DPI controls
/// rasterisation independently of the current monitor scale.
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
    private const double PlainFontSize = TypeScale.Content.PlainText;
    private const int PlainLineHeight = 24;
    private const int MaxPlainPreviewCharacters = 16_384;

    internal const int TableMargin = 16;
    internal const int TableRowHeight = 34;
    internal const double TableFontSize = TypeScale.Content.TableText;
    internal const int TableHorizontalPadding = 12;
    internal const int TableVerticalPadding = 7;
    internal const int MinimumColumnWidth = 72;
    internal const int MaximumColumnWidth = 320;
    internal const int MaxTableRows = 24;
    internal const int MaxTableColumns = 12;
    private const int MaxCellPreviewCharacters = 160;
    private const int MaxTableInspectedCharacters = 65_536;

    private static readonly CultureInfo TextCulture = CultureInfo.GetCultureInfo("ko-KR");
    internal static readonly Typeface TextTypeface = new(
        new FontFamily("Segoe UI"),
        FontStyles.Normal,
        FontWeights.Normal,
        FontStretches.Normal);

    private static readonly Brush CardBackground = FrozenBrush(0xFF, 0xFC, 0xFC, 0xFD);
    internal static readonly Brush TableBackground = FrozenBrush(0xFF, 0xFF, 0xFF, 0xFF);
    internal static readonly Brush AlternateRowBackground = FrozenBrush(0xFF, 0xF8, 0xFA, 0xFC);
    internal static readonly Brush TextForeground = FrozenBrush(0xFF, 0x1F, 0x29, 0x37);
    internal static readonly Pen GridLine = FrozenPen(0xFF, 0xD1, 0xD5, 0xDB);
    internal static readonly Pen GridBorder = FrozenPen(0xFF, 0x9C, 0xA3, 0xAF);

    /// <summary>The colour of the lines between a table picture's cells.</summary>
    internal static Brush TableGridLineBrush => GridLine.Brush;

    /// <summary>
    /// Renders <paramref name="sourceText"/> as a frozen bitmap. The input string is never
    /// retained; only a bounded visual projection is materialised.
    /// </summary>
    /// <remarks>
    /// Invoke this WPF rasterisation entry point from an STA thread. The frozen result can be
    /// consumed safely from any thread after this method returns.
    /// </remarks>
    internal static BitmapSource Render(string sourceText, bool isTabularContent) =>
        Render(sourceText, isTabularContent, 1);

    internal static BitmapSource Render(string sourceText, bool isTabularContent, double scale)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        ValidateScale(scale);
        return isTabularContent
            ? RenderTable(sourceText, scale)
            : RenderPlainText(sourceText, scale);
    }

    internal static BitmapSource RenderTranslation(string sourceText, double scale = 1)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        ValidateScale(scale);
        return RenderPlainText(sourceText, scale, borderless: true);
    }

    private static BitmapSource RenderPlainText(string sourceText, double scale, bool borderless = false)
    {
        string preview = CreatePlainPreview(sourceText).Replace("\t", "    ", StringComparison.Ordinal);
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
            if (sourceText.Contains('\t') || sourceText.Contains('\n'))
            {
                text.SetFontFamily(new FontFamily("Consolas"));
            }
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
            // The card fills its whole rectangle, as the table, code, Markdown and translation
            // pictures do, so the pin's outline is its only edge and follows the window's corners.
            // A card with its own rounded border left transparent corners: the desktop showed
            // through them inside a square Windows 10 pin, a second arc appeared inside a Windows 11
            // pin's outline, and they turned black wherever a copied bitmap's alpha is dropped.
            drawing.DrawRectangle(borderless ? Brushes.White : CardBackground, null, PixelBounds(width, height, scale));
            if (text is not null)
            {
                drawing.DrawText(text, new Point(PlainHorizontalPadding, PlainVerticalPadding));
            }
        }

        return RenderFrozen(visual, width, height, scale);
    }

    private static BitmapSource RenderTable(string sourceText, double scale)
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
            drawing.DrawRectangle(CardBackground, null, PixelBounds(width, height, scale));
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

            DrawGrid(drawing, columnWidths, preview.Rows.Count, tableWidth, scale);
        }

        return RenderFrozen(visual, width, height, scale);
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
                FormattedText text = CreateTableCellText(value, columnWidth);
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
        int tableWidth,
        double scale)
    {
        double top = SnapLine(TableMargin, scale);
        double bottom = SnapLine(TableMargin + (rowCount * TableRowHeight), scale) - (1 / scale);
        double left = SnapLine(TableMargin, scale);
        double right = SnapLine(TableMargin + tableWidth, scale) - (1 / scale);
        Pen gridLine = DevicePixelPen(GridLine, scale);

        double x = TableMargin;
        for (int columnIndex = 1; columnIndex < columnWidths.Count; columnIndex++)
        {
            x += columnWidths[columnIndex - 1];
            double snappedX = SnapLine(x, scale);
            drawing.DrawLine(gridLine, new Point(snappedX, top), new Point(snappedX, bottom));
        }

        for (int rowIndex = 1; rowIndex < rowCount; rowIndex++)
        {
            double y = SnapLine(TableMargin + (rowIndex * TableRowHeight), scale);
            drawing.DrawLine(gridLine, new Point(left, y), new Point(right, y));
        }

        drawing.DrawRectangle(
            null,
            DevicePixelPen(GridBorder, scale),
            new Rect(left, top, right - left, bottom - top));
    }

    private static int[] MeasureColumnWidths(TablePreview preview) =>
        MeasureColumnWidths(preview.Rows, preview.ColumnCount, fitColumns: true);

    internal static FormattedText CreateTableCellText(string value, double columnWidth) =>
        CreateFormattedText(value, TableFontSize, Math.Max(1, columnWidth - (TableHorizontalPadding * 2)),
            TableRowHeight - (TableVerticalPadding * 2), singleLine: true);

    internal static int[] MeasureColumnWidths(IReadOnlyList<IReadOnlyList<string>> rows, int columnCount,
        bool fitColumns, Dictionary<string, int>? measured = null)
    {
        measured ??= new(StringComparer.Ordinal);
        var widths = Enumerable.Repeat(MinimumColumnWidth, columnCount).ToArray();
        foreach (IReadOnlyList<string> row in rows.Take(MaxTableRows))
        {
            int cellCount = Math.Min(row.Count, widths.Length);
            for (int columnIndex = 0; columnIndex < cellCount; columnIndex++)
            {
                if (row[columnIndex].Length == 0)
                {
                    continue;
                }

                string value = row[columnIndex];
                if (!measured.TryGetValue(value, out int preferred))
                {
                    FormattedText text = CreateTableCellText(value, MaximumColumnWidth);
                    preferred = (int)Math.Ceiling(Math.Min(MaximumColumnWidth,
                        text.WidthIncludingTrailingWhitespace + (TableHorizontalPadding * 2)));
                    measured.Add(value, preferred);
                }
                widths[columnIndex] = Math.Max(widths[columnIndex], preferred);
            }
        }

        if (fitColumns) FitColumns(widths, MaxBitmapWidth - (TableMargin * 2));
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
        // FormattedText's ideal advances at 96 DPI keep wrapping and ellipsis decisions
        // fixed. DrawText records vector glyphs, which the target re-rasterises at its DPI;
        // no 96-DPI bitmap is scaled and no display-mode reflow is introduced.
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

    internal static TablePreview ParseTablePreview(string sourceText)
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

    /// <summary>
    /// The DIP rectangle that covers every device pixel <see cref="RenderFrozen"/> gives a picture of
    /// <paramref name="width"/> by <paramref name="height"/> DIPs. The pixel size rounds up, so a
    /// background drawn to the DIP size alone left the last column and row half transparent at
    /// fractional scales.
    /// </summary>
    internal static Rect PixelBounds(int width, int height, double scale) =>
        new(0, 0, Math.Ceiling(width * scale) / scale, Math.Ceiling(height * scale) / scale);

    internal static BitmapSource RenderFrozen(DrawingVisual visual, int width, int height, double scale)
    {
        // Integer pixel extents round outward. At fractional scales BitmapSource.Width can
        // differ from the canonical DIP width by less than one device pixel; PinContent
        // retains that canonical layout separately for window sizing.
        var target = new RenderTargetBitmap(
            checked((int)Math.Ceiling(width * scale)),
            checked((int)Math.Ceiling(height * scale)),
            Dpi * scale,
            Dpi * scale,
            PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    internal static void ValidateScale(double scale)
    {
        if (!double.IsFinite(scale) || scale <= 0 || scale > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(scale));
        }
    }

    internal static double SnapLine(double position, double scale) =>
        (Math.Floor(position * scale) + 0.5) / scale;

    internal static Pen DevicePixelPen(Pen source, double scale)
    {
        if (scale == 1) return source;
        var pen = new Pen(source.Brush, 1 / scale);
        pen.Freeze();
        return pen;
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

    internal readonly record struct TablePreview(
        IReadOnlyList<IReadOnlyList<string>> Rows,
        int ColumnCount);
}
