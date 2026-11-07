using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Pinning;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class PinTextSelectionViewTests
{
    // A spreadsheet range copy: CRLF between rows and after the last one, an empty cell, a
    // number with a thousands separator and Korean text.
    private const string RangeCopy = "품목\t수량\t단가\t비고\r\n사과\t3\t1,200\t\r\n배\t12\t2,500\t제철\r\n감\t\t900\t보류\r\n";

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void CrlfTerminatedRangeCopyMatchesThePictureOutline(double scale) => StaTestHost.Run(() =>
    {
        PinContent content = PinContent.FromText(ClipboardTextRenderer.Render(RangeCopy, true), RangeCopy, true);
        var view = new PinTextSelectionView(content) { UseLayoutRounding = true, SnapsToDevicePixels = true };
        Assert.Equal(4, ClipboardTextRenderer.ParseTablePreview(RangeCopy).Rows.Count);
        Assert.Equal(4, view.TableGrid!.Items.Count);
        BitmapSource picture = content.RenderedAt(scale).Image;
        LayoutTable(view, new Size(content.LayoutWidthDip, content.LayoutHeightDip), scale);
        BitmapSource actual = RenderTableView(view, picture.PixelWidth, picture.PixelHeight, scale);
        string name = $"range copy, scale {scale}";
        SaveTableEvidence(actual, $"range-selection-{scale}");
        SaveTableEvidence(picture, $"range-picture-{scale}");
        // The outer border's last row and column, through the first row and column.
        int column = (int)Math.Floor(20 * scale), row = (int)Math.Floor((16 + 5) * scale);
        Assert.Equal(LastBorder(picture, column, vertical: true), LastBorder(actual, column, vertical: true));
        Assert.Equal(LastBorder(picture, row, vertical: false), LastBorder(actual, row, vertical: false));
        Assert.Equal((int)Math.Floor((16 + 4 * 34) * scale) - 1, LastBorder(actual, column, vertical: true));
        AssertTablePixels(picture, actual, scale, name);
        AssertCellInk(picture, actual, RangeCopy, scale, name);
    });

    // The last pixel along a column (vertical) or row that has the outer border's colour.
    private static int LastBorder(BitmapSource image, int at, bool vertical)
    {
        int length = vertical ? image.PixelHeight : image.PixelWidth;
        int last = -1;
        for (int step = 0; step < length; step++)
        {
            if (Pixel(image, vertical ? at : step, vertical ? step : at) == Color.FromRgb(156, 163, 175)) last = step;
        }
        return last;
    }

    [Fact]
    public void CellLineBreaksShowAsSpacesWithAnEllipsisAndCopyKeepsThem() => StaTestHost.Run(() =>
    {
        foreach ((string value, string shown) in new[]
        {
            ("첫 줄\n둘째 줄", "첫 줄 둘째 줄"), ("\n둘째", " 둘째"), ("첫 줄\r\n둘째 줄", "첫 줄 둘째 줄"),
            ("a\rb\u2028c\u2029d\u0085e\u000Bf\u000Cg", "a b c d e f g"),
        })
        {
            FormattedText text = ClipboardTextRenderer.CreateTableCellText(value, ClipboardTextRenderer.MaximumColumnWidth);
            Assert.Equal(shown, text.Text);
            Assert.Equal(1, text.MaxLineCount);
        }

        // A value that does not fit ends with an ellipsis: the trimmed line is as wide as a prefix
        // of the shown text followed by "…". Without the spaces it showed "첫 줄" alone.
        FormattedText narrow = ClipboardTextRenderer.CreateTableCellText("첫 줄\n둘째 줄", ClipboardTextRenderer.MinimumColumnWidth);
        Assert.True(Untrimmed("첫 줄 둘째 줄").Width > narrow.MaxTextWidth);
        Assert.Contains(Enumerable.Range(0, "첫 줄 둘째 줄".Length + 1),
            length => Math.Abs(Untrimmed(string.Concat("첫 줄 둘째 줄".AsSpan(0, length), "…")).Width - narrow.Width) < 0.01);

        const string original = "\"첫 줄\n둘째 줄\"\t\"\n둘째\"\r\nA\tB";
        PinContent content = PinContent.FromText(ClipboardTextRenderer.Render(original, true), original, true);
        var view = new PinTextSelectionView(content);
        LayoutTable(view, new Size(content.LayoutWidthDip, content.LayoutHeightDip));
        BitmapSource image = RenderTableView(view, (int)Math.Ceiling(content.LayoutWidthDip), (int)Math.Ceiling(content.LayoutHeightDip));
        SaveTableEvidence(image, "line-break-cells");
        DataGrid grid = view.TableGrid!;
        double secondColumn = 16 + grid.Columns[0].ActualWidth;
        // The value that starts with a line break draws its second line.
        Assert.True(HasInk(image, new Int32Rect((int)secondColumn + 2, 18, (int)grid.Columns[1].ActualWidth - 4, 30)));

        view.SelectCells(0, 0, 0, 0);
        Assert.Equal("첫 줄\n둘째 줄", view.SelectedOriginalText);
        view.SelectCells(0, 1, 0, 1);
        Assert.Equal("\n둘째", view.SelectedOriginalText);
        view.SelectCells(0, 0, 0, 1);
        Assert.Equal("\"첫 줄\n둘째 줄\"\t\"\n둘째\"", view.SelectedOriginalText);

        static FormattedText Untrimmed(string text) => new(text, CultureInfo.GetCultureInfo("ko-KR"), FlowDirection.LeftToRight,
            ClipboardTextRenderer.TextTypeface, ClipboardTextRenderer.TableFontSize, Brushes.Black, 1);
    });

    private static bool HasInk(BitmapSource image, Int32Rect area)
    {
        var pixels = new byte[area.Width * area.Height * 4];
        image.CopyPixels(area, pixels, area.Width * 4, 0);
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] < 110 && pixels[i + 1] < 110 && pixels[i + 2] < 110) return true;
        }
        return false;
    }

    [Fact]
    public void CellItemPeersReadTheCellText() => StaTestHost.Run(() =>
    {
        string longValue = string.Concat(Enumerable.Repeat("긴 원문 값 long value ", 12));
        string original = $"이름\t값\r\n{longValue}\t둘째\r\n셋\t넷";
        PinContent content = PinContent.FromText(ClipboardTextRenderer.Render(original, true), original, true);
        var view = new PinTextSelectionView(content);
        LayoutTable(view, new Size(content.LayoutWidthDip, content.LayoutHeightDip));
        var grid = Assert.IsType<DataGridAutomationPeer>(UIElementAutomationPeer.CreatePeerForElement(view.TableGrid!));
        string[][] expected = [["이름", "값"], [longValue, "둘째"], ["셋", "넷"]];
        List<AutomationPeer> rows = grid.GetChildren().OfType<DataGridItemAutomationPeer>().Cast<AutomationPeer>().ToList();
        Assert.Equal(expected.Length, rows.Count);
        for (int row = 0; row < rows.Count; row++)
        {
            List<AutomationPeer> cells = rows[row].GetChildren().OfType<DataGridCellItemAutomationPeer>().Cast<AutomationPeer>().ToList();
            Assert.Equal(expected[row].Length, cells.Count);
            for (int column = 0; column < cells.Count; column++)
                Assert.Equal(expected[row][column], cells[column].GetName());
        }

        // Rows recycled by scrolling are named by their new values.
        string tall = string.Join("\r\n", Enumerable.Range(0, 60).Select(index => $"행 {index}\t값 {index}"));
        var scrolled = new PinTextSelectionView(PinContent.FromText(ClipboardTextRenderer.Render(tall, true), tall, true));
        var host = new Grid();
        host.Children.Add(scrolled);
        LayoutTable(host, new Size(300, 200));
        for (int notch = 0; notch < 5; notch++)
        {
            Assert.True(scrolled.TryScroll(-120));
            host.UpdateLayout();
        }
        DataGrid tallGrid = scrolled.TableGrid!;
        var tallPeer = Assert.IsType<DataGridAutomationPeer>(UIElementAutomationPeer.CreatePeerForElement(tallGrid));
        int named = 0;
        foreach (DataGridItemAutomationPeer rowPeer in tallPeer.GetChildren().OfType<DataGridItemAutomationPeer>())
        {
            if (tallGrid.ItemContainerGenerator.ContainerFromItem(rowPeer.Item) is not DataGridRow) continue;
            string[] values = Assert.IsType<string[]>(rowPeer.Item);
            List<DataGridCellItemAutomationPeer> cells = rowPeer.GetChildren().OfType<DataGridCellItemAutomationPeer>().ToList();
            for (int column = 0; column < cells.Count; column++)
                Assert.Equal(values[column], cells[column].GetName());
            named++;
        }
        Assert.True(named > 0, "Some rows are realised.");
        Assert.True(tallGrid.ItemContainerGenerator.ContainerFromIndex(0) is null, "The first rows were recycled.");
    });

    [Fact]
    public void CurrentCellShowsAThemeFocusRingThatFollowsTheActiveCell() => StaTestHost.Run(() =>
    {
        const string original = "가\t나\t다\r\n라\t마\t바\r\n사\t아\t자";
        PinContent content = PinContent.FromText(ClipboardTextRenderer.Render(original, true), original, true);
        var view = new PinTextSelectionView(content);
        var window = new Window
        {
            Content = view,
            Width = content.LayoutWidthDip,
            Height = content.LayoutHeightDip,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowActivated = false,
            ShowInTaskbar = false,
            Left = -10000,
            Top = -10000,
        };
        // Known colours stand in for the Windows selection colours; the cells reference the keys.
        SetSelectionColors(window, Color.FromRgb(0x00, 0x78, 0xD7), Colors.White);
        try
        {
            window.Show();
            DataGrid grid = view.TableGrid!;
            view.FocusSelection();
            view.SelectCells(1, 1, 1, 1);
            Settle(window);
            Assert.True(grid.IsKeyboardFocusWithin, "The table has keyboard focus.");
            double scale = VisualTreeHelper.GetDpi(view).DpiScaleX;
            int ring = (int)Math.Max(1, Math.Round(2 * scale));
            AssertRing(1, 1, Colors.White, Color.FromRgb(0x00, 0x78, 0xD7), ring, "first cell");

            // Shift+Right extends the selection; the ring moves with the current cell.
            view.MoveCell(Key.Right, extend: true);
            Settle(window);
            AssertNoRing(1, 1, Color.FromRgb(0x00, 0x78, 0xD7), "previous current cell");
            AssertRing(1, 2, Colors.White, Color.FromRgb(0x00, 0x78, 0xD7), ring, "extended");

            // The ring width is the theme token; a contrast theme's selection pair recolours it.
            window.Resources["Focus.RingWidth"] = 4d;
            SetSelectionColors(window, Color.FromRgb(0x1A, 0xEB, 0xFF), Colors.Black);
            Settle(window);
            AssertRing(1, 2, Colors.Black, Color.FromRgb(0x1A, 0xEB, 0xFF), (int)Math.Max(1, Math.Round(4 * scale)), "contrast colours, width 4");
            AssertNoRing(1, 1, Color.FromRgb(0x1A, 0xEB, 0xFF), "selected, not current");

            void AssertRing(int row, int column, Color ink, Color fill, int width, string step)
            {
                (int left, int top, int right, int bottom) = CellLines(grid, row, column, scale);
                BitmapSource image = Snapshot(view, scale);
                SaveTableEvidence(image, $"focus-ring-{step.Replace(' ', '-')}-{scale}");
                string name = $"{step}, cell {row},{column}, scale {scale}";
                for (int x = left + 1; x < right; x++)
                {
                    Assert.True(Pixel(image, x, top + 1) == ink, $"{name}: top band at {x}: {Pixel(image, x, top + 1)}");
                    Assert.True(Pixel(image, x, bottom - 1) == ink, $"{name}: bottom band at {x}: {Pixel(image, x, bottom - 1)}");
                }
                for (int y = top + 1; y < bottom; y++)
                {
                    Assert.True(Pixel(image, left + 1, y) == ink, $"{name}: left band at {y}: {Pixel(image, left + 1, y)}");
                    Assert.True(Pixel(image, right - 1, y) == ink, $"{name}: right band at {y}: {Pixel(image, right - 1, y)}");
                }
                int middle = (top + bottom) / 2;
                Assert.Equal(ink, Pixel(image, left + width, middle));
                Assert.True(Pixel(image, left + width + 1, middle) == fill, $"{name}: inside the ring {Pixel(image, left + width + 1, middle)}");
                Assert.True(Pixel(image, left + width + 1, top + width + 1) == fill, $"{name}: inside the top band");
            }

            void AssertNoRing(int row, int column, Color fill, string step)
            {
                (int left, int top, int right, int bottom) = CellLines(grid, row, column, scale);
                BitmapSource image = Snapshot(view, scale);
                Assert.True(Pixel(image, left + 1, (top + bottom) / 2) == fill, $"{step}: {Pixel(image, left + 1, (top + bottom) / 2)}");
                Assert.True(Pixel(image, (left + right) / 2, top + 1) == fill, $"{step}: top {Pixel(image, (left + right) / 2, top + 1)}");
            }
        }
        finally { window.Close(); }
    });

    private static void SetSelectionColors(FrameworkElement owner, Color highlight, Color text)
    {
        owner.Resources[SystemColors.HighlightBrushKey] = new SolidColorBrush(highlight);
        owner.Resources[SystemColors.HighlightTextBrushKey] = new SolidColorBrush(text);
    }

    private static void Settle(Window window)
    {
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        window.UpdateLayout();
    }

    private static BitmapSource Snapshot(FrameworkElement view, double scale) =>
        RenderTableView(view, (int)Math.Round(view.ActualWidth * scale), (int)Math.Round(view.ActualHeight * scale), scale);

    // The device pixel rows and columns of a cell's four grid lines at zoom 1 in a view at the root's
    // origin. The outer border sits one device pixel inside the table, as in the picture.
    private static (int Left, int Top, int Right, int Bottom) CellLines(DataGrid grid, int row, int column, double scale)
    {
        double left = 16 + grid.Columns.Take(column).Sum(item => item.ActualWidth);
        double top = 16 + (row * 34);
        int right = (int)Math.Floor((left + grid.Columns[column].ActualWidth) * scale) - (column == grid.Columns.Count - 1 ? 1 : 0);
        int bottom = (int)Math.Floor((top + 34) * scale) - (row == grid.Items.Count - 1 ? 1 : 0);
        return ((int)Math.Floor(left * scale), (int)Math.Floor(top * scale), right, bottom);
    }

    [Fact]
    public void SelectionColorsFollowTheSystemColorResources() => StaTestHost.Run(() =>
    {
        var host = new Grid();
        SetSelectionColors(host, Color.FromRgb(0x12, 0x34, 0x56), Color.FromRgb(0xFE, 0xDC, 0xBA));
        PinTextSelectionView text = TextView("selectable text");
        PinTextSelectionView markdown = new(PinContent.FromMarkdown(Image(), "# Title\n\nBody"));
        host.Children.Add(text);
        host.Children.Add(markdown);
        foreach (TextBoxBase box in new TextBoxBase[] { text.TextBox!, markdown.MarkdownBox! })
        {
            Assert.Equal(Color.FromRgb(0x12, 0x34, 0x56), Assert.IsType<SolidColorBrush>(box.SelectionBrush).Color);
            Assert.Equal(Color.FromRgb(0xFE, 0xDC, 0xBA), Assert.IsType<SolidColorBrush>(box.SelectionTextBrush).Color);
        }
        SetSelectionColors(host, Color.FromRgb(0x1A, 0xEB, 0xFF), Colors.Black);
        Assert.Equal(Color.FromRgb(0x1A, 0xEB, 0xFF), Assert.IsType<SolidColorBrush>(text.TextBox!.SelectionBrush).Color);
        Assert.Equal(Colors.Black, Assert.IsType<SolidColorBrush>(markdown.MarkdownBox!.SelectionTextBrush).Color);

        // Without keyboard focus the table shows the inactive selection pair; the focused pair is
        // covered by the focus ring test.
        const string table = "a\tb\r\nc\td";
        var view = new PinTextSelectionView(PinContent.FromText(Image(), table, true));
        host.Resources[SystemColors.InactiveSelectionHighlightBrushKey] = new SolidColorBrush(Color.FromRgb(0x21, 0x43, 0x65));
        host.Resources[SystemColors.InactiveSelectionHighlightTextBrushKey] = new SolidColorBrush(Color.FromRgb(0xEF, 0xCD, 0xAB));
        host.Children.Add(view);
        LayoutTable(host, new Size(300, 200));
        view.SelectCells(0, 0, 0, 0);
        host.UpdateLayout();
        PinTableCell selected = Descendants<PinTableCell>(view).First(cell => cell.Value == "a");
        Assert.Equal(Color.FromRgb(0x21, 0x43, 0x65), Assert.IsType<SolidColorBrush>(selected.SelectedBackground).Color);
        Assert.Equal(Color.FromRgb(0xEF, 0xCD, 0xAB), Assert.IsType<SolidColorBrush>(selected.Ink).Color);
        host.Resources[SystemColors.InactiveSelectionHighlightBrushKey] = new SolidColorBrush(Colors.Black);
        host.Resources[SystemColors.InactiveSelectionHighlightTextBrushKey] = new SolidColorBrush(Colors.White);
        Assert.Equal(Colors.Black, Assert.IsType<SolidColorBrush>(selected.SelectedBackground).Color);
        Assert.Equal(Colors.White, Assert.IsType<SolidColorBrush>(selected.Ink).Color);
    });

    [Fact]
    public void CellsReuseFrozenZoomTransformsAndPens() => StaTestHost.Run(() =>
    {
        const string table = "a\tb\r\nc\td";
        var layout = new PinTableCellLayout(table, new PinTableSelection(table)) { Zoom = 1.25 };
        Transform zoom = layout.TextScale;
        Assert.True(zoom.IsFrozen);
        layout.Zoom = 1.25;
        Assert.Same(zoom, layout.TextScale);
        Assert.Equal(new Matrix(1.25, 0, 0, 1.25, 0, 0), layout.TextScale.Value);
        (Pen line, Pen border) = layout.Pens(1.75);
        Assert.True(line.IsFrozen && border.IsFrozen);
        Assert.Equal(1 / 1.75, line.Thickness, 10);
        Assert.Same(line, layout.Pens(1.75).Line);
        Assert.Same(border, layout.Pens(1.75).Border);
        Assert.Same(ClipboardTextRenderer.GridLine, layout.Pens(1).Line);
    });
}
