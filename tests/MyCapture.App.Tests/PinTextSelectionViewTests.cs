using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Pinning;
using MyCapture.Core.Localization;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PinTextSelectionViewTests
{
    [Fact]
    public void SelectedCodeLineAndMultipleLinesKeepOriginalEndings() => StaTestHost.Run(() =>
    {
        const string original = "#include <stdio.h>\r\n#define MAX_LEN 256\r\n";
        var view = TextView(original);
        TextBox textBox = Assert.IsType<TextBox>(view.TextBox);
        Assert.Null(view.SelectedOriginalText);
        textBox.Select(original.IndexOf("#define", StringComparison.Ordinal), "#define MAX_LEN 256".Length);
        Assert.Equal("#define MAX_LEN 256", view.SelectedOriginalText);
        textBox.Select(0, original.Length - 2);
        Assert.Equal("#include <stdio.h>\r\n#define MAX_LEN 256", view.SelectedOriginalText);
    });

    [Fact]
    public void DisplayTabsAndMixedNewlinesMapToExactOriginalSubstring() => StaTestHost.Run(() =>
    {
        const string original = "first\tvalue  \rsecond\n\tthird\r\nlast  ";
        var view = TextView(original);
        TextBox textBox = Assert.IsType<TextBox>(view.TextBox);
        Assert.Equal("first    value  \r\nsecond\r\n    third\r\nlast  ", textBox.Text);
        int start = textBox.Text.IndexOf("second", StringComparison.Ordinal);
        int end = textBox.Text.IndexOf("last", StringComparison.Ordinal);
        textBox.Select(start, end - start);
        Assert.Equal("second\n\tthird\r\n", view.SelectedOriginalText);
        textBox.Select(6, 1);
        Assert.Equal("\t", view.SelectedOriginalText);
        view.SelectAll();
        Assert.Equal(original, view.SelectedOriginalText);
    });

    [Theory]
    [InlineData("plain", "Segoe UI")]
    [InlineData("one\rtwo", "Consolas")]
    [InlineData("one\ntwo", "Consolas")]
    [InlineData("one\ttwo", "Consolas")]
    public void PlainViewMatchesRendererTypographyAndScalesPadding(string original, string font) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage("en-US");
        var view = TextView(original);
        TextBox textBox = Assert.IsType<TextBox>(view.TextBox);
        Assert.Equal(font, textBox.FontFamily.Source);
        Assert.Equal(16, textBox.FontSize);
        Assert.Equal(new Thickness(24, 20, 24, 20), textBox.Padding);
        Assert.True(textBox.IsReadOnly);
        Assert.Equal(ScrollBarVisibility.Auto, textBox.VerticalScrollBarVisibility);
        Assert.Equal(ScrollBarVisibility.Auto, textBox.HorizontalScrollBarVisibility);
        Assert.Equal("Select text", AutomationProperties.GetName(view));
        view.SetZoom(2);
        Assert.Equal(32, textBox.FontSize);
        Assert.Equal(new Thickness(48, 40, 48, 40), textBox.Padding);
    });

    [Theory]
    [InlineData(30, 255)]
    [InlineData(250, 0)]
    public void OpaqueBackgroundAndForegroundFollowBitmap(byte shade, byte foreground) => StaTestHost.Run(() =>
    {
        var view = new PinTextSelectionView(PinContent.FromText(Image(shade), "code", false));
        Assert.Equal(Color.FromRgb(shade, shade, shade), Assert.IsType<SolidColorBrush>(view.Background).Color);
        Assert.Equal(Color.FromRgb(foreground, foreground, foreground), Assert.IsType<SolidColorBrush>(view.TextBox!.Foreground).Color);
        Assert.Same(view.Background, view.TextBox.Background);
    });

    [Fact]
    public void TransparentRoundedCornerSamplesContentPadding() => StaTestHost.Run(() =>
    {
        BitmapSource bitmap = ClipboardTextRenderer.Render("sample", false);
        var view = new PinTextSelectionView(PinContent.FromText(bitmap, "sample", false));
        Color background = Assert.IsType<SolidColorBrush>(view.Background).Color;
        Assert.Equal(Color.FromRgb(0xFC, 0xFC, 0xFD), background);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothTableKindsUseGridAndCopyRectangularValues(bool picture) => StaTestHost.Run(() =>
    {
        const string original = "name\tvalue\r\n\"line\tbreak\"\t\"say \"\"yes\"\"\"\r\n";
        PinContent content = picture ? PinContent.FromTablePicture(Image(), original) : PinContent.FromText(Image(), original, true);
        var view = new PinTextSelectionView(content);
        Assert.Null(view.TextBox);
        Assert.NotNull(view.TableGrid);
        Assert.Null(view.SelectedOriginalText);
        view.SelectCells(1, 0, 1, 0);
        Assert.Equal("line\tbreak", view.SelectedOriginalText);
        view.SelectCells(0, 0, 1, 1);
        Assert.Equal("name\tvalue\r\n\"line\tbreak\"\t\"say \"\"yes\"\"\"", view.SelectedOriginalText);
        Assert.Equal(4, view.TableGrid.SelectedCells.Count);
        view.SelectAll();
        Assert.Equal(original, view.SelectedOriginalText);
    });

    [Fact]
    public void TableArrowSelectionExtendsFromAnchorAndClampsToBounds() => StaTestHost.Run(() =>
    {
        var view = new PinTextSelectionView(PinContent.FromText(Image(), "a\tb\r\nc\t", true));
        view.MoveCell(Key.Right, extend: false);
        Assert.Equal("a", view.SelectedOriginalText);
        view.MoveCell(Key.Right, extend: true);
        view.MoveCell(Key.Down, extend: true);
        Assert.Equal("a\tb\r\nc\t", view.SelectedOriginalText);
        view.MoveCell(Key.Right, extend: false);
        Assert.Equal(string.Empty, view.SelectedOriginalText);
        view.MoveCell(Key.Down, extend: false);
        Assert.Equal(string.Empty, view.SelectedOriginalText);
    });

    [Fact]
    public void TablePointerHitTestsSelectAndExtendRectangles() => StaTestHost.Run(() =>
    {
        var view = new PinTextSelectionView(PinContent.FromText(Image(), "a\tb\r\nc\td", true));
        WithHostedView(view, () =>
        {
            DataGrid grid = view.TableGrid!;
            DataGridCell[] cells = Descendants<DataGridCell>(grid).ToArray();
            DataGridCell first = Assert.Single(cells, cell => grid.Items.IndexOf(cell.DataContext) == 0 && cell.Column.DisplayIndex == 0);
            DataGridCell last = Assert.Single(cells, cell => grid.Items.IndexOf(cell.DataContext) == 1 && cell.Column.DisplayIndex == 1);
            Point start = first.TranslatePoint(new Point(first.ActualWidth / 2, first.ActualHeight / 2), grid);
            Point end = last.TranslatePoint(new Point(last.ActualWidth / 2, last.ActualHeight / 2), grid);

            Assert.True(view.SelectCellAt(start, extend: false));
            Assert.Equal("a", view.SelectedOriginalText);
            Assert.True(view.ExtendSelectionTo(end));
            Assert.Equal("a\tb\r\nc\td", view.SelectedOriginalText);
            Assert.Equal(4, grid.SelectedCells.Count);
            Assert.True(view.SelectCellAt(end, extend: false));
            Assert.Equal("d", view.SelectedOriginalText);
            Assert.True(view.SelectCellAt(start, extend: true));
            Assert.Equal("a\tb\r\nc\td", view.SelectedOriginalText);
            Assert.False(view.SelectCellAt(new Point(-10, -10), extend: false));
            Assert.Equal("a\tb\r\nc\td", view.SelectedOriginalText);
        });
    });

    [Fact]
    public void TableExtendedNavigationKeepsCopiedValueAndVisibleSelectionTogether() => StaTestHost.Run(() =>
    {
        var view = new PinTextSelectionView(PinContent.FromText(Image(), "a\tb\r\nc\td\r\ne\tf", true));
        Layout(view);
        view.SelectCells(0, 0, 0, 0);
        Assert.True(view.HandleTableNavigationKey(Key.End, ModifierKeys.None));
        Assert.Equal("b", view.SelectedOriginalText);
        Assert.Equal(1, Assert.Single(view.TableGrid!.SelectedCells).Column.DisplayIndex);
        Assert.True(view.HandleTableNavigationKey(Key.End, ModifierKeys.Control | ModifierKeys.Shift));
        Assert.Equal("b\r\nd\r\nf", view.SelectedOriginalText);
        Assert.True(view.HandleTableNavigationKey(Key.Home, ModifierKeys.Control));
        Assert.Equal("a", view.SelectedOriginalText);
        Assert.True(view.HandleTableNavigationKey(Key.PageDown, ModifierKeys.None));
        Assert.Equal("c", view.SelectedOriginalText);
        Assert.True(view.HandleTableNavigationKey(Key.Tab, ModifierKeys.None));
        Assert.Equal("d", view.SelectedOriginalText);
        Assert.True(view.HandleTableNavigationKey(Key.Tab, ModifierKeys.Shift));
        Assert.Equal("c", view.SelectedOriginalText);
        Assert.True(view.HandleTableNavigationKey(Key.Enter, ModifierKeys.None));
        Assert.Equal("e", view.SelectedOriginalText);
        Assert.True(view.HandleTableNavigationKey(Key.PageUp, ModifierKeys.None));
        Assert.Equal("c", view.SelectedOriginalText);
    });

    [Fact]
    public void TableDragBeyondViewportScrollsAndExtendsSelection() => StaTestHost.Run(() =>
    {
        string original = string.Join("\r\n", Enumerable.Range(0, 20).Select(index => $"row{index}"));
        var view = new PinTextSelectionView(PinContent.FromText(Image(), original, true));
        WithHostedView(view, () =>
        {
            DataGrid grid = view.TableGrid!;
            DataGridCell first = Assert.Single(Descendants<DataGridCell>(grid),
                cell => grid.Items.IndexOf(cell.DataContext) == 0);
            Point start = first.TranslatePoint(new Point(first.ActualWidth / 2, first.ActualHeight / 2), grid);
            Assert.True(view.SelectCellAt(start, extend: false));
            Assert.True(view.ExtendSelectionTo(new Point(start.X, grid.ActualHeight + 30)));
            Assert.True(Descendants<ScrollViewer>(grid).First().VerticalOffset > 0);
            Assert.True(grid.SelectedCells.Max(cell => grid.Items.IndexOf(cell.Item)) >= 2);
            Assert.StartsWith("row0\r\nrow1\r\nrow2", view.SelectedOriginalText);
        });
    });

    [Fact]
    public void TableDisplayCapStillSelectsWholeOriginalAndShowsNotice() => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage("en-US");
        string original = string.Join("\r\n", Enumerable.Range(0, PinTableSelection.MaxRows + 2).Select(index => $"row{index}\tvalue"));
        var view = new PinTextSelectionView(PinContent.FromTablePicture(Image(), original));
        Assert.Equal(PinTableSelection.MaxRows, view.TableGrid!.Items.Count);
        Assert.Contains(view.Children.OfType<TextBlock>(), block => block.Text.Contains("Ctrl+A", StringComparison.Ordinal));
        view.SelectAll();
        Assert.Equal(original, view.SelectedOriginalText);
        Assert.Equal(PinTableSelection.MaxRows * 2, view.TableGrid.SelectedCells.Count);
        view.SetZoom(5);
        view.SetZoom(0.1);
        Assert.Equal(1.4, view.TableGrid.FontSize, precision: 5);
    });

    [Fact]
    public void LongCellNoticeLeavesSmallPinGridUsable() => StaTestHost.Run(() =>
    {
        string original = new('x', PinTableSelection.MaxCellPreviewCharacters + 1);
        var view = new PinTextSelectionView(PinContent.FromTablePicture(Image(), original));
        view.Measure(new Size(280, 80));
        view.Arrange(new Rect(0, 0, 280, 80));
        view.UpdateLayout();
        TextBlock notice = Assert.Single(view.Children.OfType<TextBlock>());
        Assert.Equal(TextWrapping.NoWrap, notice.TextWrapping);
        Assert.Equal(TextTrimming.CharacterEllipsis, notice.TextTrimming);
        Assert.Equal(notice.Text, notice.ToolTip);
        Assert.Equal(notice.Text, AutomationProperties.GetHelpText(notice));
        Assert.True(view.TableGrid!.ActualHeight >= 24);
        Assert.True(notice.ActualHeight < 24);
        view.SelectCells(0, 0, 0, 0);
        Assert.Equal(original, view.SelectedOriginalText);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChildUsesPinMenuAndBuiltInClipboardCommandIsDisabled(bool table) => StaTestHost.Run(() =>
    {
        var menu = new ContextMenu();
        var view = new PinTextSelectionView(PinContent.FromText(Image(), table ? "a\tb" : "text", table))
        {
            ContextMenu = menu,
        };
        FrameworkElement child = (FrameworkElement?)view.TextBox ?? view.TableGrid!;
        Layout(view);
        child.GetBindingExpression(FrameworkElement.ContextMenuProperty)!.UpdateTarget();
        Assert.Same(menu, child.ContextMenu);
        view.SelectAll();
        Assert.False(ApplicationCommands.Copy.CanExecute(null, child));
        Assert.False(ApplicationCommands.Cut.CanExecute(null, child));
        Assert.False(ApplicationCommands.Paste.CanExecute(null, child));
    });

    [Fact]
    public void WheelScrollsOverflowAndReturnsFalseAtBoundary() => StaTestHost.Run(() =>
    {
        var view = TextView(string.Join("\r\n", Enumerable.Range(0, 80).Select(index => $"line {index}")));
        Layout(view);
        Assert.False(view.TryScroll(120));
        Assert.True(view.TryScroll(-120));
        view.UpdateLayout();
        Assert.True(view.TextBox!.VerticalOffset > 0);
        view.TextBox.ScrollToEnd();
        view.UpdateLayout();
        Assert.False(view.TryScroll(-120));
        Assert.True(view.TryScroll(120));
    });

    private static PinTextSelectionView TextView(string original) =>
        new(PinContent.FromText(Image(), original, false));

    private static void Layout(FrameworkElement element)
    {
        element.Measure(new Size(280, 120));
        element.Arrange(new Rect(0, 0, 280, 120));
        element.UpdateLayout();
    }

    private static void WithHostedView(PinTextSelectionView view, Action test)
    {
        var window = new Window
        {
            Content = view,
            Width = 280,
            Height = 120,
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
            window.UpdateLayout();
            test();
        }
        finally { window.Close(); }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is T typed) yield return typed;
            foreach (T descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static BitmapSource Image(byte shade = 250)
    {
        BitmapSource bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { shade, shade, shade, 255 }, 4);
        bitmap.Freeze();
        return bitmap;
    }
}
