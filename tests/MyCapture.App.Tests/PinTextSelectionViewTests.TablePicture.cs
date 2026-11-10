using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Pinning;
using MyCapture.App.Themes;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class PinTextSelectionViewTests
{
    private static string TableSample(bool squeezed) => string.Join("\r\n", Enumerable.Range(0, 6).Select(row =>
        string.Join('\t', Enumerable.Range(0, squeezed ? 12 : 4).Select(column =>
            row == 2 && column == 1 ? "" : column == 2 ? "A long English cell with Korean 한글 and enough words to require an ellipsis inside the column"
            : squeezed ? $"Column {column + 1} release details" : $"항목 {row + 1} / {column + 1}"))));

    [Theory]
    [InlineData(1.0, 1.0)]
    [InlineData(1.25, 1.0)]
    [InlineData(1.5, 1.0)]
    [InlineData(2.0, 1.0)]
    [InlineData(1.0, 2.0)]
    [InlineData(1.25, 2.0)]
    [InlineData(1.5, 2.0)]
    [InlineData(2.0, 2.0)]
    public void TableSelectionMatchesPicturePixels(double scale, double zoom) => StaTestHost.Run(() =>
    {
        foreach (bool squeezed in new[] { false, true })
        {
            string original = TableSample(squeezed);
            PinContent content = PinContent.FromText(ClipboardTextRenderer.Render(original, true), original, true);
            var view = new PinTextSelectionView(content) { UseLayoutRounding = true, SnapsToDevicePixels = true };
            view.SetZoom(zoom);
            BitmapSource picture = content.RenderedAt(scale * zoom).Image;
            Size size = new(content.LayoutWidthDip * zoom, content.LayoutHeightDip * zoom);
            LayoutTable(view, size, scale);
            BitmapSource actual = RenderTableView(view, picture.PixelWidth, picture.PixelHeight, scale);
            string name = $"{(squeezed ? "squeezed" : "fitting")}, scale {scale}, zoom {zoom}, cell DPI {VisualTreeHelper.GetDpi(Descendants<PinTableCell>(view).First()).DpiScaleX}";
            SaveTableEvidence(actual, $"selection-{squeezed}-{scale}-{zoom}");
            SaveTableEvidence(picture, $"picture-{squeezed}-{scale}-{zoom}");
            ScrollViewer scroll = Descendants<ScrollViewer>(view.TableGrid!).First();
            Assert.True(scroll.ComputedHorizontalScrollBarVisibility == Visibility.Collapsed &&
                scroll.ComputedVerticalScrollBarVisibility == Visibility.Collapsed, $"{name}: unexpected scroll bars; extent {scroll.ExtentWidth}/{scroll.ExtentHeight}, viewport {scroll.ViewportWidth}/{scroll.ViewportHeight}, grid {view.TableGrid!.ActualWidth}/{view.TableGrid.ActualHeight}; columns {string.Join(',', view.TableGrid.Columns.Select(c => c.ActualWidth))}");
            AssertTablePixels(picture, actual, scale * zoom, name);
            AssertCellInk(picture, actual, original, scale * zoom, name);
        }
    });

    [Fact]
    public void OverflowTableContinuesGeometryAndUsesMarginsForScrollBars() => StaTestHost.Run(() =>
    {
        string original = string.Join("\r\n", Enumerable.Range(0, 30).Select(row => string.Join('\t',
            Enumerable.Range(0, 15).Select(column => column < 12 ? $"r{row}c{column}" : $"Extra column {column}: release details"))));
        PinContent content = PinContent.FromText(ClipboardTextRenderer.Render(original, true), original, true);
        var view = new PinTextSelectionView(content);
        DataGrid grid = view.TableGrid!;
        var model = new PinTableSelection(original);
        int[] extra = ClipboardTextRenderer.MeasureColumnWidths(model.Rows, 15, false);
        for (int column = 12; column < 15; column++) Assert.Equal(extra[column], grid.Columns[column].Width.Value);
        Assert.True(grid.EnableRowVirtualization && grid.EnableColumnVirtualization);
        Assert.Equal(VirtualizationMode.Recycling, VirtualizingPanel.GetVirtualizationMode(grid));
        Assert.All(grid.Columns, column => Assert.True(column.Width.IsAbsolute));

        int tableWidth = (int)grid.Columns.Sum(column => column.Width.Value);
        LayoutTable(view, new Size(tableWidth + 32, 30 * 34 + 32));
        BitmapSource full = RenderTableView(view, tableWidth + 32, 30 * 34 + 32);
        for (int row = 0; row < 30; row++)
        {
            Assert.Equal(row % 2 == 0 ? Colors.White : Color.FromRgb(248, 250, 252), Pixel(full, 20, 16 + row * 34 + 5));
            Assert.Equal(Color.FromRgb(156, 163, 175), Pixel(full, 16 + tableWidth - 1, 16 + row * 34 + 5));
        }
        Assert.Equal(Color.FromRgb(209, 213, 219), Pixel(full, 20, 16 + 24 * 34));
        Assert.Equal(Color.FromRgb(156, 163, 175), Pixel(full, 20, 16 + 30 * 34 - 1));
        Assert.Equal(Assert.IsType<SolidColorBrush>(view.Background).Color, Pixel(full, 16 + tableWidth + 5, 50));
        Assert.Equal(Assert.IsType<SolidColorBrush>(view.Background).Color, Pixel(full, 20, 16 + 30 * 34 + 5));
        SaveTableEvidence(full, "overflow-full");

        LayoutTable(view, new Size(content.LayoutWidthDip, content.LayoutHeightDip));
        ScrollViewer scroll = Descendants<ScrollViewer>(grid).First();
        Assert.Equal(Visibility.Visible, scroll.ComputedHorizontalScrollBarVisibility);
        Assert.Equal(Visibility.Visible, scroll.ComputedVerticalScrollBarVisibility);
        ScrollContentPresenter viewport = Descendants<ScrollContentPresenter>(scroll).First();
        Assert.Equal(content.LayoutWidthDip - 32, viewport.ActualWidth, 5);
        Assert.Equal(24 * 34, viewport.ActualHeight, 5);
        Assert.Equal(24, scroll.ViewportHeight, 5);
        Assert.Contains(Descendants<VirtualizingStackPanel>(grid), panel => panel.IsItemsHost);
        // BringIntoView uses routed visibility/input events, so check navigation in the same
        // real, off-screen test window used by the existing pointer/drag tests.
        WithHostedView(view, () =>
        {
            view.SelectCells(29, 14, 29, 14);
            view.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            view.UpdateLayout();
            Assert.Equal("Extra column 14: release details", view.SelectedOriginalText);
            Assert.True(scroll.VerticalOffset > 0 && scroll.HorizontalOffset > 0, $"ScrollIntoView offsets: {scroll.HorizontalOffset}, {scroll.VerticalOffset}");
        });
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EllipsizedCellKeepsFullCopyAndAutomationValue(bool picture) => StaTestHost.Run(() =>
    {
        string original = string.Concat(Enumerable.Repeat("Long original 한글 value ", 20));
        PinContent content = picture ? PinContent.FromTablePicture(ClipboardTextRenderer.Render(original, true), original)
            : PinContent.FromText(ClipboardTextRenderer.Render(original, true), original, true);
        var view = new PinTextSelectionView(content);
        LayoutTable(view, new Size(content.LayoutWidthDip, content.LayoutHeightDip));
        PinTableCell cell = Assert.Single(Descendants<PinTableCell>(view));
        FormattedText text = ClipboardTextRenderer.CreateTableCellText(original, view.TableGrid!.Columns[0].ActualWidth);
        Assert.Equal(TextTrimming.CharacterEllipsis, text.Trimming);
        Assert.Equal(1, text.MaxLineCount);
        var untrimmed = new FormattedText(original, System.Globalization.CultureInfo.GetCultureInfo("ko-KR"), FlowDirection.LeftToRight,
            ClipboardTextRenderer.TextTypeface, 14, Brushes.Black, 1);
        Assert.True(untrimmed.Width > text.MaxTextWidth);
        Assert.Equal(original, FrameworkElementAutomationPeer.CreatePeerForElement(cell)!.GetName());
        view.SelectCells(0, 0, 0, 0);
        Assert.Equal(original, view.SelectedOriginalText);
    });

    /// <summary>
    /// A cell snaps its lines while it renders, which WPF does inside the cell's own arrange,
    /// before its rows and panels have their offsets, and not again when a scroll only moves it.
    /// In a real window at a zoom that puts cell edges between device pixels, every line must
    /// still be one device pixel on opening and after vertical and fractional horizontal scrolls.
    /// </summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void TableLinesStayOneDevicePixelWhenOpenedAndScrolled(double zoom) => StaTestHost.Run(() =>
    {
        string original = string.Join("\r\n", Enumerable.Range(0, 30).Select(row => string.Join('\t',
            Enumerable.Range(0, 15).Select(column => $"r{row}c{column} value {column * 7}"))));
        PinContent content = PinContent.FromText(ClipboardTextRenderer.Render(original, true), original, true);
        var view = new PinTextSelectionView(content);
        view.SetZoom(zoom);
        var window = new Window
        {
            Content = view,
            Width = content.LayoutWidthDip * zoom,
            Height = content.LayoutHeightDip * zoom,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowActivated = false,
            ShowInTaskbar = false,
            Left = -10000,
            Top = -10000,
        };
        try
        {
            window.Show();
            ScrollViewer scroll = Descendants<ScrollViewer>(view.TableGrid!).First();
            void Check(string step, bool horizontalLines)
            {
                window.UpdateLayout();
                view.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                DpiScale dpi = VisualTreeHelper.GetDpi(view);
                BitmapSource image = RenderTableView(view, (int)Math.Round(view.ActualWidth * dpi.DpiScaleX),
                    (int)Math.Round(view.ActualHeight * dpi.DpiScaleY), dpi.DpiScaleX);
                double effective = dpi.DpiScaleX * zoom;
                string name = $"zoom {zoom}, DPI {dpi.DpiScaleX}, {step}";
                // One pixel row through the first visible row above its text crosses every vertical line.
                (int crisp, int blurred) vertical = LineCrossings(image, horizontal: true, (int)Math.Floor((16 + 3) * effective), effective);
                Assert.True(vertical.blurred == 0 && vertical.crisp >= 3, $"{name}: vertical lines {vertical.crisp} one-pixel, {vertical.blurred} blurred");
                if (!horizontalLines) return;
                // One pixel column inside the first column, left of its text, crosses every horizontal line.
                (int crisp, int blurred) across = LineCrossings(image, horizontal: false, (int)Math.Floor((16 + 6) * effective), effective);
                Assert.True(across.blurred == 0 && across.crisp >= 10, $"{name}: horizontal lines {across.crisp} one-pixel, {across.blurred} blurred");
            }
            Check("opened", horizontalLines: true);
            scroll.ScrollToVerticalOffset(1);
            Check("after one row down", horizontalLines: true);
            scroll.ScrollToHorizontalOffset(37.3);
            Check("after 37.3 DIP right", horizontalLines: false);
        }
        finally { window.Close(); }
    });

    // Walks one pixel row (horizontal) or column of the table area, skipping the 16 DIP scroll bar
    // band at the far end, and sorts each run of non-background pixels into a single line-coloured
    // pixel or anything else (a line split over two pixels, or a blend).
    private static (int Crisp, int Blurred) LineCrossings(BitmapSource image, bool horizontal, int at, double effective)
    {
        int width = image.PixelWidth, height = image.PixelHeight;
        var pixels = new byte[width * height * 4];
        image.CopyPixels(pixels, width * 4, 0);
        int length = (horizontal ? width : height) - (int)Math.Ceiling(16 * effective);
        int crisp = 0, blurred = 0, run = 0;
        bool exact = true;
        for (int step = 0; step <= length; step++)
        {
            bool background = true, line = false;
            if (step < length)
            {
                int i = ((horizontal ? at : step) * width + (horizontal ? step : at)) * 4;
                (byte b, byte g, byte r) = (pixels[i], pixels[i + 1], pixels[i + 2]);
                background = (b, g, r) is (255, 255, 255) or (252, 250, 248) or (253, 252, 252);
                line = (b, g, r) is (219, 213, 209) or (175, 163, 156);
            }
            if (!background)
            {
                run++;
                exact &= line;
                continue;
            }
            if (run == 1 && exact) crisp++;
            else if (run > 0) blurred++;
            run = 0;
            exact = true;
        }
        return (crisp, blurred);
    }

    private static Color Pixel(BitmapSource bitmap, int x, int y)
    {
        byte[] bytes = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), bytes, 4, 0);
        return Color.FromArgb(bytes[3], bytes[2], bytes[1], bytes[0]);
    }

    private static void AssertCellInk(BitmapSource picture, BitmapSource actual, string original, double scale, string name)
    {
        var preview = ClipboardTextRenderer.ParseTablePreview(original);
        int[] widths = ClipboardTextRenderer.MeasureColumnWidths(preview.Rows, preview.ColumnCount, true);
        double left = 16;
        for (int column = 0; column < widths.Length; column++)
        {
            for (int row = 0; row < preview.Rows.Count; row++)
            {
                int x0 = (int)Math.Ceiling((left + 2) * scale), x1 = (int)Math.Floor((left + widths[column] - 2) * scale);
                int y0 = (int)Math.Ceiling((16 + row * 34 + 2) * scale), y1 = (int)Math.Floor((16 + (row + 1) * 34 - 2) * scale);
                Point? Start(BitmapSource image)
                {
                    int xMin = int.MaxValue, yMin = int.MaxValue;
                    byte[] pixels = new byte[(x1 - x0) * (y1 - y0) * 4];
                    image.CopyPixels(new Int32Rect(x0, y0, x1 - x0, y1 - y0), pixels, (x1 - x0) * 4, 0);
                    for (int y = y0; y < y1; y++)
                    for (int x = x0; x < x1; x++)
                    {
                        int i = ((y - y0) * (x1 - x0) + x - x0) * 4;
                        if (pixels[i] >= 110 || pixels[i + 1] >= 110 || pixels[i + 2] >= 110) continue;
                        xMin = Math.Min(xMin, x); yMin = Math.Min(yMin, y);
                    }
                    return xMin == int.MaxValue ? null : new Point(xMin, yMin);
                }
                Point? expected = Start(picture), observed = Start(actual);
                Assert.True(expected.HasValue == observed.HasValue, $"{name}: cell {row},{column} missing/unexpected text; expected {expected}, actual {observed}");
                if (expected is { } a && observed is { } b)
                    Assert.True(Math.Abs(a.X - b.X) <= 1 && Math.Abs(a.Y - b.Y) <= 1,
                        $"{name}: cell {row},{column} first ink mismatch: expected {a}, actual {b}");
            }
            left += widths[column];
        }
    }

    private static void LayoutTable(FrameworkElement view, Size size, double scale = 1)
    {
        view.Measure(size);
        view.Arrange(new Rect(size));
        view.UpdateLayout();
        // An unattached tree has no HwndTarget to propagate DPI to newly realised cells.
        // Apply the root DPI after template/virtualised children exist, as monitor changes do.
        VisualTreeHelper.SetRootDpi(view, new DpiScale(scale, scale));
        view.Measure(size);
        view.Arrange(new Rect(size));
        view.UpdateLayout();
    }

    private static BitmapSource RenderTableView(Visual view, int width, int height, double scale = 1)
    {
        var image = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        image.Render(view);
        return image;
    }

    private static void SaveTableEvidence(BitmapSource bitmap, string name)
    {
        if (Environment.GetEnvironmentVariable("MYCAPTURE_SELECTION_EVIDENCE") is not { Length: > 0 } folder) return;
        Directory.CreateDirectory(folder);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(folder, name + ".png"));
        encoder.Save(stream);
    }

    private static void AssertTablePixels(BitmapSource expected, BitmapSource actual, double scale, string name)
    {
        int width = expected.PixelWidth, height = expected.PixelHeight;
        var before = new byte[width * height * 4];
        var after = new byte[before.Length];
        expected.CopyPixels(before, width * 4, 0);
        actual.CopyPixels(after, width * 4, 0);
        int tolerance = scale == Math.Floor(scale) ? 0 : 1;
        // Compare all flat background and line pixels, in both directions. Glyph rasterisation
        // can differ between WPF visuals; its per-cell origin is checked separately below.
        bool Structural(byte[] pixels, int i) =>
            (pixels[i], pixels[i + 1], pixels[i + 2]) is (219, 213, 209) or (175, 163, 156) or
                (255, 255, 255) or (252, 250, 248) or (253, 252, 252);
        bool Same(byte[] a, int i, byte[] b, int j) => a.AsSpan(i, 4).SequenceEqual(b.AsSpan(j, 4));
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int i = (y * width + x) * 4;
            if (Same(before, i, after, i)) continue;
            bool Line(byte[] p) => (p[i], p[i + 1], p[i + 2]) is (219, 213, 209) or (175, 163, 156);
            if ((!Structural(before, i) || !Structural(after, i)) && !Line(before) && !Line(after)) continue;
            bool Nearby(byte[] source, byte[] target) => Enumerable.Range(Math.Max(0, y - tolerance), Math.Min(height - 1, y + tolerance) - Math.Max(0, y - tolerance) + 1)
                .Any(yy => Enumerable.Range(Math.Max(0, x - tolerance), Math.Min(width - 1, x + tolerance) - Math.Max(0, x - tolerance) + 1)
                    .Any(xx => Same(source, i, target, (yy * width + xx) * 4)));
            Assert.True(Nearby(before, after) && Nearby(after, before), $"{name}: first background/line mismatch ({x},{y}); expected {Convert.ToHexString(before.AsSpan(i, 4))}, actual {Convert.ToHexString(after.AsSpan(i, 4))}");
        }
    }
}
