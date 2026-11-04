using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MyCapture.TutorialGenerator;

/// <summary>
/// Synthetic content the tutorials capture, edit and paste: a purchase-request spreadsheet, a note
/// app and a terminal. Only these stand-ins for other people's apps are drawn by hand; every
/// MyCapture surface in a clip is the product's own UI.
/// </summary>
internal static class Fixtures
{
    internal const string TerminalCommand = "npm run build";
    internal const string TerminalResult = "Build completed";

    // A plain Windows spreadsheet: grey headers, light grid, the green selected cell.
    private static readonly Brush SheetInk = Rgb(0x1F, 0x1F, 0x1F);
    private static readonly Brush HeaderInk = Rgb(0x55, 0x55, 0x55);
    private static readonly Brush HeaderFill = Rgb(0xF5, 0xF5, 0xF5);
    private static readonly Brush TitleRowFill = Rgb(0xF2, 0xF2, 0xF2);
    private static readonly Brush SelectedInk = Rgb(0x18, 0x5C, 0x37);
    private static readonly Brush SelectedWash = Rgb(0xDC, 0xEB, 0xE1);
    private static readonly Brush SelectedEdge = Rgb(0x21, 0x73, 0x46);
    private static readonly Pen Grid = Hairline(0xE3);
    private static readonly Pen HeaderLine = Hairline(0xD4);
    private static readonly Pen TitleRule = Hairline(0xA6);
    private static readonly Pen TotalRule = Hairline(0x7F);

    private const double FormulaBarHeight = 24, ColumnHeaderHeight = 22, RowHeight = 25, RowHeaderWidth = 30;
    private const double GridTop = FormulaBarHeight + ColumnHeaderHeight;
    // Right edges of columns A–D; E runs to the image edge.
    private static readonly double[] ColumnRights = [160, 200, 300, 480];
    private const int SelectedColumn = 2, SelectedRow = 8;

    private sealed record SheetRow(string Item, string Quantity, string Amount, string Requester, string Status);

    internal static string SheetTitle(bool korean) => korean ? "2분기 장비 구매 요청" : "Q2 equipment request";

    /// <summary>
    /// The spreadsheet the image-editor, pin and quick-access clips capture (pixels, 1 pixel = 1 DIP).
    /// Rows 3–7 are the requests, row 8 their total with C8 selected.
    /// </summary>
    internal static BitmapSource PurchaseSheet(bool korean, int width = 600, int height = 300)
    {
        SheetRow[] rows = korean
            ?
            [
                new("27형 모니터", "4", "1,556,000", "minji.kim@contoso.com", "승인"),
                new("무선 키보드", "6", "354,000", "joon.park@contoso.com", "승인"),
                new("USB-C 도크", "4", "856,000", "minji.kim@contoso.com", "검토 중"),
                new("노트북 거치대", "6", "192,000", "hana.lee@contoso.com", "검토 중"),
                new("웹캠", "2", "178,000", "joon.park@contoso.com", "반려"),
            ]
            :
            [
                new("27-inch monitor", "4", "1,156", "minji.kim@contoso.com", "Approved"),
                new("Wireless keyboard", "6", "270", "joon.park@contoso.com", "Approved"),
                new("USB-C dock", "4", "636", "minji.kim@contoso.com", "In review"),
                new("Laptop stand", "6", "144", "hana.lee@contoso.com", "In review"),
                new("Webcam", "2", "258", "joon.park@contoso.com", "Declined"),
            ];
        string totalQuantity = "22", totalAmount = korean ? "3,136,000" : "2,464";
        Typeface face = SheetFace(korean, bold: false), boldFace = SheetFace(korean, bold: true);

        double[] lefts = [RowHeaderWidth, .. ColumnRights];
        double[] rights = [.. ColumnRights, width];
        double ColumnLeft(int c) => lefts[c];
        double ColumnRight(int c) => rights[c];
        double RowTop(int r) => GridTop + (r - 1) * RowHeight;
        int rowCount = (int)Math.Ceiling((height - GridTop) / RowHeight);

        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));

            // Formula bar: the name box and the selected cell's formula.
            dc.DrawLine(HeaderLine, new Point(0, FormulaBarHeight - 0.5), new Point(width, FormulaBarHeight - 0.5));
            dc.DrawLine(HeaderLine, new Point(62.5, 0), new Point(62.5, FormulaBarHeight));
            Cell(dc, "C8", new Rect(2, 0, 60, FormulaBarHeight), face, 12, SheetInk, TextAlignment.Left);
            Cell(dc, "fx", new Rect(66, 0, 28, FormulaBarHeight), new Typeface(new FontFamily("Segoe UI"), FontStyles.Italic, FontWeights.Normal, FontStretches.Normal), 12, HeaderInk, TextAlignment.Left);
            Cell(dc, "=SUM(C3:C7)", new Rect(90, 0, width - 90, FormulaBarHeight), face, 12, SheetInk, TextAlignment.Left);

            // Column and row headers, the selected cell's column and row tinted.
            dc.DrawRectangle(HeaderFill, null, new Rect(0, FormulaBarHeight, width, ColumnHeaderHeight));
            dc.DrawRectangle(HeaderFill, null, new Rect(0, GridTop, RowHeaderWidth, height - GridTop));
            var selectedColumn = new Rect(ColumnLeft(SelectedColumn), FormulaBarHeight, ColumnRight(SelectedColumn) - ColumnLeft(SelectedColumn), ColumnHeaderHeight);
            var selectedRow = new Rect(0, RowTop(SelectedRow), RowHeaderWidth, RowHeight);
            dc.DrawRectangle(SelectedWash, null, selectedColumn);
            dc.DrawRectangle(SelectedWash, null, selectedRow);
            dc.DrawRectangle(SelectedEdge, null, new Rect(selectedColumn.X, selectedColumn.Bottom - 2, selectedColumn.Width, 2));
            dc.DrawRectangle(SelectedEdge, null, new Rect(selectedRow.Right - 2, selectedRow.Y, 2, selectedRow.Height));
            var headerFace = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var headerBold = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            for (int c = 0; c < lefts.Length; c++)
            {
                bool selected = c == SelectedColumn;
                Cell(dc, ((char)('A' + c)).ToString(), new Rect(lefts[c], FormulaBarHeight, rights[c] - lefts[c], ColumnHeaderHeight),
                    selected ? headerBold : headerFace, 11, selected ? SelectedInk : HeaderInk, TextAlignment.Center);
            }
            for (int r = 1; r <= rowCount; r++)
            {
                bool selected = r == SelectedRow;
                Cell(dc, r.ToString(CultureInfo.InvariantCulture), new Rect(0, RowTop(r), RowHeaderWidth, RowHeight),
                    selected ? headerBold : headerFace, 11, selected ? SelectedInk : HeaderInk, TextAlignment.Center);
            }

            // Grid. Row 1 holds the sheet title, which runs across its empty neighbours.
            dc.DrawLine(HeaderLine, new Point(RowHeaderWidth - 0.5, FormulaBarHeight), new Point(RowHeaderWidth - 0.5, height));
            dc.DrawLine(HeaderLine, new Point(0, GridTop - 0.5), new Point(width, GridTop - 0.5));
            foreach (double right in ColumnRights)
            {
                dc.DrawLine(HeaderLine, new Point(right - 0.5, FormulaBarHeight), new Point(right - 0.5, GridTop));
                dc.DrawLine(Grid, new Point(right - 0.5, RowTop(2)), new Point(right - 0.5, height));
            }
            for (int r = 1; r <= rowCount; r++)
            {
                double bottom = RowTop(r) + RowHeight - 0.5;
                dc.DrawLine(r == 2 ? TitleRule : Grid, new Point(RowHeaderWidth, bottom), new Point(width, bottom));
                dc.DrawLine(HeaderLine, new Point(0, bottom), new Point(RowHeaderWidth, bottom));
            }

            Cell(dc, SheetTitle(korean), new Rect(ColumnLeft(0), RowTop(1), width - ColumnLeft(0), RowHeight), boldFace, 14, SheetInk, TextAlignment.Left);

            dc.DrawRectangle(TitleRowFill, null, new Rect(RowHeaderWidth, RowTop(2), width - RowHeaderWidth, RowHeight - 1));
            foreach (double right in ColumnRights)
                dc.DrawLine(Grid, new Point(right - 0.5, RowTop(2)), new Point(right - 0.5, RowTop(3) - 1));
            string[] headings = korean ? ["품목", "수량", "금액", "요청자", "상태"] : ["Item", "Qty", "Amount", "Requester", "Status"];
            for (int c = 0; c < headings.Length; c++)
                Cell(dc, headings[c], new Rect(lefts[c], RowTop(2), rights[c] - lefts[c], RowHeight), boldFace, 13, SheetInk, TextAlignment.Center);

            for (int i = 0; i < rows.Length; i++)
            {
                double top = RowTop(3 + i);
                SheetRow row = rows[i];
                Cell(dc, row.Item, new Rect(lefts[0], top, rights[0] - lefts[0], RowHeight), face, 13, SheetInk, TextAlignment.Left);
                Cell(dc, row.Quantity, new Rect(lefts[1], top, rights[1] - lefts[1], RowHeight), face, 13, SheetInk, TextAlignment.Right);
                Cell(dc, row.Amount, new Rect(lefts[2], top, rights[2] - lefts[2], RowHeight), face, 13, SheetInk, TextAlignment.Right);
                Cell(dc, row.Requester, new Rect(lefts[3], top, rights[3] - lefts[3], RowHeight), face, 13, SheetInk, TextAlignment.Left);
                Cell(dc, row.Status, new Rect(lefts[4], top, rights[4] - lefts[4], RowHeight), face, 13, SheetInk, TextAlignment.Left);
            }

            double totalTop = RowTop(SelectedRow);
            dc.DrawLine(TotalRule, new Point(RowHeaderWidth, totalTop - 0.5), new Point(width, totalTop - 0.5));
            Cell(dc, korean ? "합계" : "Total", new Rect(lefts[0], totalTop, rights[0] - lefts[0], RowHeight), boldFace, 13, SheetInk, TextAlignment.Left);
            Cell(dc, totalQuantity, new Rect(lefts[1], totalTop, rights[1] - lefts[1], RowHeight), boldFace, 13, SheetInk, TextAlignment.Right);
            Cell(dc, totalAmount, new Rect(lefts[2], totalTop, rights[2] - lefts[2], RowHeight), boldFace, 13, SheetInk, TextAlignment.Right);

            // The selected cell: a two-pixel green frame and its fill handle.
            var selectedCell = new Rect(ColumnLeft(SelectedColumn), totalTop, ColumnRight(SelectedColumn) - ColumnLeft(SelectedColumn), RowHeight);
            dc.DrawRectangle(null, new Pen(SelectedEdge, 2), new Rect(selectedCell.X + 1, selectedCell.Y + 1, selectedCell.Width - 2, selectedCell.Height - 2));
            dc.DrawRectangle(SelectedEdge, new Pen(Brushes.White, 1), new Rect(selectedCell.Right - 3.5, selectedCell.Bottom - 3.5, 6, 6));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Image-pixel rectangles of notable parts of <see cref="PurchaseSheet"/>.</summary>
    internal static class PurchaseSheetParts
    {
        /// <summary>Row 4, columns A–C: the request the clips mark.</summary>
        internal static readonly Rect SecondItem = new(31, 122, 268, 23);
        /// <summary>D3:D7, the requesters' addresses the privacy clips cover.</summary>
        internal static readonly Rect Requesters = new(300, 96, 180, 125);
        /// <summary>A1:C8, the sheet as far as its total, for the pin clips.</summary>
        internal static readonly Int32Rect PinCapture = new(0, 0, 300, 246);
        /// <summary>A1:C5, the region the quick-access clip selects.</summary>
        internal static readonly Int32Rect QuickRegion = new(30, 46, 270, 125);
    }

    internal static void Text(DrawingContext dc, string text, double x, double y, double size, Brush brush, bool bold = false, bool mono = false)
    {
        var typeface = new Typeface(mono ? Stage.MonoFont : Stage.UiFont, FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal);
        dc.DrawText(new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, size, brush, 1), new Point(x, y));
    }

    // Korean Windows spreadsheets default to Malgun Gothic; English ones to a Segoe-like sans.
    private static Typeface SheetFace(bool korean, bool bold) =>
        new(new FontFamily(korean ? "Malgun Gothic" : "Segoe UI"), FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);

    /// <summary>Cell text, vertically centred, with a spreadsheet's 6-pixel inner margin.</summary>
    private static void Cell(DrawingContext dc, string text, Rect cell, Typeface face, double size, Brush brush, TextAlignment alignment)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, brush, 1);
        double x = alignment switch
        {
            TextAlignment.Right => cell.Right - 6 - formatted.WidthIncludingTrailingWhitespace,
            TextAlignment.Center => cell.X + (cell.Width - formatted.WidthIncludingTrailingWhitespace) / 2,
            _ => cell.X + 6,
        };
        dc.DrawText(formatted, new Point(Math.Round(x), Math.Round(cell.Y + (cell.Height - formatted.Height) / 2)));
    }

    private static Pen Hairline(byte grey)
    {
        var pen = new Pen(Rgb(grey, grey, grey), 1);
        pen.Freeze();
        return pen;
    }

    private static SolidColorBrush Rgb(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
