using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Pinning;
using MyCapture.Core.Localization;
using MyCapture.Core.Pin;
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
        Assert.Equal(24, TextBlock.GetLineHeight(textBox));
        // The TextBox's own inset completes the picture's 24 DIP padding.
        const double inset = TextBoxLayout.ContentInsetDip;
        Assert.Equal(new Thickness(24 - inset, 20, 24 - inset, 20), textBox.Padding);
        Assert.Equal(LineStackingStrategy.BlockLineHeight, TextBlock.GetLineStackingStrategy(textBox));
        Assert.True(textBox.IsReadOnly);
        Assert.Equal(ScrollBarVisibility.Auto, textBox.VerticalScrollBarVisibility);
        Assert.Equal(ScrollBarVisibility.Auto, textBox.HorizontalScrollBarVisibility);
        Assert.Equal("Select text", AutomationProperties.GetName(view));
        view.SetZoom(2);
        Assert.Equal(32, textBox.FontSize);
        Assert.Equal(48, TextBlock.GetLineHeight(textBox));
        Assert.Equal(new Thickness(48 - inset, 40, 48 - inset, 40), textBox.Padding);
    });

    /// <summary>
    /// Selection mode lays the original text over the picture it replaces. The glyphs stay where
    /// the picture drew them, and a line that fits the picture does not scroll. A TextBox draws its
    /// text 2 DIP inside its content host and stacks lines from the font's ascent: that moved the
    /// text 2 DIP right and 2 DIP up (4 DIP at zoom 2), and the 4 DIP it lost made a one-line pin
    /// show both scroll bars over its last letters.
    /// </summary>
    [Theory]
    [InlineData(1.0, 1.0)]
    [InlineData(1.25, 1.0)]
    [InlineData(1.5, 1.0)]
    [InlineData(1.0, 2.0)]
    [InlineData(1.5, 2.0)]
    public void SelectableTextStaysOnThePictureGlyphsWithoutScrolling(double scale, double zoom) => StaTestHost.Run(() =>
    {
        var code = new PinCodeDocument("#define MAX_LEN 256\r\nreturn value;", [], "#1E1E1E", "#DCDCDC");
        string widest = WidestFraction(Enumerable.Range(1, 40).Select(index => $"Check item {index} of the release list"));
        string[] texts = ["배포 전에 태그와 패키지 이름을 확인합니다.", widest, "first\tvalue\r\nsecond line"];
        PinContent[] contents =
        [
            .. texts.Select(text => PinContent.FromText(ClipboardTextRenderer.Render(text, false), text, false)),
            PinContent.FromCode(ClipboardCodeRenderer.Render(code), code.DisplayText, code),
        ];
        foreach (PinContent content in contents)
        {
            string name = $"\"{content.OriginalText}\" at {scale:0.##}x, zoom {zoom}";
            BitmapSource picture = content.RenderedAt(scale * zoom).Image;
            var view = new PinTextSelectionView(content) { UseLayoutRounding = true, SnapsToDevicePixels = true };
            view.SetZoom(zoom);
            double width = content.LayoutWidthDip * zoom, height = content.LayoutHeightDip * zoom;
            VisualTreeHelper.SetRootDpi(view, new DpiScale(scale, scale));
            view.Measure(new Size(width, height));
            view.Arrange(new Rect(0, 0, width, height));
            view.UpdateLayout();

            var host = Assert.IsType<ScrollViewer>(view.TextBox!.Template.FindName("PART_ContentHost", view.TextBox));
            Assert.True(host.ComputedHorizontalScrollBarVisibility == Visibility.Collapsed
                && host.ComputedVerticalScrollBarVisibility == Visibility.Collapsed,
                $"{name}: the text fits the picture, yet the selection shows scroll bars ({host.ExtentWidth:0.##} DIP in {host.ViewportWidth:0.##}).");
            var selection = new RenderTargetBitmap(picture.PixelWidth, picture.PixelHeight, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            selection.Render(view);
            Int32Rect expected = InkBounds(picture), actual = InkBounds(selection);
            Assert.True(Math.Abs(actual.X - expected.X) <= 1 && Math.Abs(actual.Y - expected.Y) <= 1,
                $"{name}: the selectable text starts at ({actual.X}, {actual.Y}) px, the picture's at ({expected.X}, {expected.Y}) px.");
        }
    });

    [Theory]
    [InlineData("#define MAX_LEN 256", 1.0)]
    [InlineData("#define MAX_LEN 256", 1.75)]
    [InlineData("#include <stdio.h>\r\n#define MAX_LEN 256", 1.0)]
    [InlineData("#include <stdio.h>\r\n#define MAX_LEN 256", 1.75)]
    public void CodeViewUsesRendererTypographyAtEveryZoom(string original, double zoom) => StaTestHost.Run(() =>
    {
        var document = new PinCodeDocument(original, [], "#1E1E1E", "#DCDCDC");
        PinContent content = PinContent.FromCode(ClipboardCodeRenderer.Render(document), original, document);
        var view = new PinTextSelectionView(content);
        view.SetZoom(zoom);

        TextBox textBox = Assert.IsType<TextBox>(view.TextBox);
        Assert.Equal(PinTextStyle.Code, content.TextStyle);
        Assert.Equal("Consolas", textBox.FontFamily.Source);
        Assert.Equal(15 * zoom, textBox.FontSize);
        Assert.Equal(23 * zoom, TextBlock.GetLineHeight(textBox));
        const double inset = TextBoxLayout.ContentInsetDip;
        Assert.Equal(new Thickness(24 * zoom - inset, 20 * zoom, 24 * zoom - inset, 20 * zoom), textBox.Padding);
        Assert.Equal(Color.FromRgb(0x1E, 0x1E, 0x1E), Assert.IsType<SolidColorBrush>(view.Background).Color);
        Assert.Same(view.Background, textBox.Background);
        view.SelectAll();
        Assert.Equal(original, view.SelectedOriginalText);
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
        // Text cards drawn before 2026-11 had transparent rounded corners; restored ones still may.
        var visual = new DrawingVisual();
        using (DrawingContext drawing = visual.RenderOpen())
            drawing.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0xFC, 0xFC, 0xFD)), null, new Rect(0, 0, 240, 80), 10, 10);
        var bitmap = new RenderTargetBitmap(240, 80, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
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
        // The cells keep the picture's light lines instead of a DataGrid's default black ones.
        Assert.Same(ClipboardTextRenderer.TableGridLineBrush, view.TableGrid.HorizontalGridLinesBrush);
        Assert.Same(ClipboardTextRenderer.TableGridLineBrush, view.TableGrid.VerticalGridLinesBrush);
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
        TextBlock notice = Assert.Single(view.Children.OfType<TextBlock>(), block => block.Text.Contains("Ctrl+A", StringComparison.Ordinal));
        // The notice is interface text: the UI font, not the table's content font.
        object font = notice.ReadLocalValue(TextBlock.FontFamilyProperty);
        Assert.Equal("ResourceReferenceExpression", font.GetType().Name);
        Assert.Equal("Font.Ui", font.GetType().GetProperty("ResourceKey")?.GetValue(font));
        Assert.Equal(MyCapture.App.Themes.ThemeFonts.Formatting, TextOptions.GetTextFormattingMode(notice));
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

    /// <summary>The line whose measured width lies just under a whole DIP, the tightest fit.</summary>
    private static string WidestFraction(IEnumerable<string> candidates) => candidates.MaxBy(text =>
    {
        var formatted = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            ClipboardTextRenderer.TextTypeface, MyCapture.App.Themes.TypeScale.Content.PlainText, Brushes.Black, 1);
        double width = formatted.WidthIncludingTrailingWhitespace;
        return width - Math.Floor(width);
    })!;

    /// <summary>The device-pixel bounds of the pixels that clearly differ from the paper colour.</summary>
    private static Int32Rect InkBounds(BitmapSource image)
    {
        var bgra = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int width = bgra.PixelWidth, height = bgra.PixelHeight, stride = width * 4;
        byte[] pixels = new byte[stride * height];
        bgra.CopyPixels(pixels, stride, 0);
        int paper = (4 * stride) + (width / 2 * 4);
        int left = width, top = height, right = -1, bottom = -1;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * stride) + (x * 4);
                int distance = Math.Abs(pixels[i] - pixels[paper]) + Math.Abs(pixels[i + 1] - pixels[paper + 1])
                    + Math.Abs(pixels[i + 2] - pixels[paper + 2]);
                if (pixels[i + 3] < 255 || distance < 240) continue;
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        }
        Assert.True(right >= left, "The image has text.");
        return new Int32Rect(left, top, right - left + 1, bottom - top + 1);
    }

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
