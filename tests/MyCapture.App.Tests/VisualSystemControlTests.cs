using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using MyCapture.App.Themes;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Exercises the shipped control templates without opening a Window or sending input.
/// Optional PNG evidence uses MYCAPTURE_VISUAL_EVIDENCE. A baseline directory containing
/// before-Tokens.xaml, before-Controls.xaml and before-palettes.json enables paired renders.
/// </summary>
public sealed class VisualSystemControlTests
{
    private const int SheetWidth = 720;
    private const int SheetHeight = 540;

    public static IEnumerable<object[]> ThemeAndDpi()
    {
        foreach (AppTheme theme in new[] { AppTheme.Glass, AppTheme.GlassLight, AppTheme.HighContrast })
            foreach (double scale in new[] { 1d, 1.5d, 2d })
                yield return [theme, scale];
    }

    [Theory]
    [MemberData(nameof(ThemeAndDpi))]
    public void SharedControlsKeepNeutralSelectionFocusAndReadableGeometry(AppTheme theme, double scale) => Run(() =>
    {
        ResourceDictionary resources = LoadResources();
        ThemeService.ApplyResources(theme, resources, systemHighContrast: false, accent: AppAccent.Red);
        Sheet sheet = CreateSheet(resources, scale);
        bool contrast = theme == AppTheme.HighContrast;

        Assert.Equal(12d, sheet.Caption.FontSize);
        Assert.Equal(14d, sheet.Body.FontSize);
        Assert.Equal(20d, sheet.Subtitle.FontSize);
        Assert.Equal(28d, sheet.Title.FontSize);
        Assert.Equal(14d, sheet.Primary.FontSize);
        Assert.Equal(1d, sheet.Secondary.BorderThickness.Left);
        Assert.Equal(1d, sheet.Input.BorderThickness.Left);
        Assert.Equal(1d, sheet.Combo.BorderThickness.Left);
        Assert.Equal(contrast ? 1d : 0d, sheet.Card.BorderThickness.Left);
        Assert.Equal(contrast ? 1d : 0d, sheet.Tool.BorderThickness.Left);
        Border dangerChrome = Part<Border>(sheet.Danger, "Chrome");
        Assert.Equal(new Thickness(contrast ? 1 : 0), dangerChrome.BorderThickness);
        Assert.Equal(ColorOf(resources, "Border.Control"), ColorOf(dangerChrome.BorderBrush));

        foreach (Control control in new Control[] { sheet.Primary, sheet.Secondary, sheet.Tool,
                     sheet.Focused, sheet.Toggle, sheet.Rail, sheet.SelectedTab, sheet.ListItem,
                     sheet.Input, sheet.Combo })
        {
            Assert.True(control.ActualWidth >= 24 && control.ActualHeight >= 24,
                $"{theme}/{scale}: {control.GetType().Name} hit target is {control.RenderSize}");
            Assert.True(control.FontSize >= 12, $"{theme}/{scale}: {control.GetType().Name} font is {control.FontSize}");
        }

        Border focus = Part<Border>(sheet.Focused, "FocusRing");
        Assert.Equal(new Thickness(2), focus.BorderThickness);
        Assert.Equal(1d, focus.Opacity);
        if (!contrast)
        {
            Assert.Equal(ColorOf(resources, "Text.Primary"), ColorOf(focus.BorderBrush));
            Assert.Equal(ColorOf(resources, "Text.Primary"), ColorOf(sheet.Danger.Foreground));
            Assert.NotEqual(ColorOf(resources, "Accent.Default"), ColorOf(sheet.Danger.Background));
            foreach ((Control control, string part) in new[]
            {
                ((Control)sheet.Toggle, "CheckedLayer"), (sheet.Rail, "CheckedLayer"),
                (sheet.SelectedTab, "SelectedLayer"), (sheet.ListItem, "SelectedLayer"),
            })
            {
                Border selected = Part<Border>(control, part);
                Assert.Equal(ColorOf(resources, "Surface.Selected"), ColorOf(selected.Background));
                Assert.Equal(ColorOf(resources, "Text.Primary"), ColorOf(selected.BorderBrush));
                Assert.Equal(new Thickness(2), selected.BorderThickness);
                Assert.Equal(1d, selected.Opacity);
            }
        }

        // A focus transition may change ink and opacity, never the command's layout box.
        Size focusedSize = sheet.Focused.RenderSize;
        sheet.Focused.Tag = null;
        Layout(sheet.Root);
        Assert.Equal(focusedSize, sheet.Focused.RenderSize);
        Assert.Equal(0d, focus.Opacity);
        sheet.Focused.Tag = "True";
        Layout(sheet.Root);
        Assert.Equal(focusedSize, sheet.Focused.RenderSize);

        Assert.Null(PresentationSource.FromVisual(sheet.Root));
        BitmapSource image = Render(sheet.Root, scale);
        Assert.Equal((int)(SheetWidth * scale), image.PixelWidth);
        Assert.Equal((int)(SheetHeight * scale), image.PixelHeight);
        AssertPrimaryCaptionRenders(sheet, image, scale);
        Save(image, $"after-{AppThemeNames.ToSetting(theme)}-{scale * 100:0}.png");

        string? baselineDirectory = Environment.GetEnvironmentVariable("MYCAPTURE_VISUAL_BASELINE");
        if (!string.IsNullOrWhiteSpace(baselineDirectory))
        {
            ResourceDictionary before = LoadResources(baselineDirectory);
            ApplyBaseline(before, baselineDirectory, theme);
            Sheet previous = CreateSheet(before, scale);
            Assert.Null(PresentationSource.FromVisual(previous.Root));
            BitmapSource previousImage = Render(previous.Root, scale);
            AssertPrimaryCaptionRenders(previous, previousImage, scale);
            Save(previousImage, $"before-{AppThemeNames.ToSetting(theme)}-{scale * 100:0}.png");
        }
    });

    [Theory]
    [MemberData(nameof(ThemeAndDpi))]
    public void ComboSelectionStaysAboveHighlightAndMenuFocusHasItsOwnOutline(AppTheme theme, double scale) => Run(() =>
    {
        ResourceDictionary resources = LoadResources();
        ThemeService.ApplyResources(theme, resources, systemHighContrast: false, accent: AppAccent.Red);
        var item = new ComboBoxItem
        {
            Content = "선택 항목", IsSelected = true, Width = 220, Height = 40,
            HorizontalAlignment = HorizontalAlignment.Left,
            Style = (Style)resources[typeof(ComboBoxItem)],
        };
        var menu = new MenuItem
        {
            Header = "저장", Width = 220, Height = 40, Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Style = (Style)resources[typeof(MenuItem)],
        };
        var panel = new StackPanel();
        panel.Children.Add(item);
        panel.Children.Add(menu);
        var root = new Border { Resources = resources, Child = panel, Width = 260, Height = 120 };
        root.SetResourceReference(Border.BackgroundProperty, "Surface.Base");
        VisualTreeHelper.SetRootDpi(root, new DpiScale(scale, scale));
        Layout(root);

        Border selected = Part<Border>(item, "SelectedLayer");
        Border highlight = Part<Border>(item, "HoverLayer");
        Border focus = Part<Border>(menu, "FocusRing");
        Assert.Equal(1d, selected.Opacity);
        Assert.Equal(0d, highlight.Opacity);
        Assert.Equal(0d, focus.Opacity);

        // The fixture's Tag predicate drives the shipped IsHighlighted and keyboard-focus
        // setters, while neither control receives native input or opens a popup.
        item.Tag = "True";
        menu.Tag = "True";
        Layout(root);
        Assert.Equal(1d, selected.Opacity);
        Assert.Equal(1d, highlight.Opacity);
        Assert.Equal(1d, focus.Opacity);
        foreach (Border outline in new[] { selected, highlight, focus })
        {
            Assert.Equal(new Thickness(2), outline.BorderThickness);
            Assert.Equal(ColorOf(resources, "Text.Primary"), ColorOf(outline.BorderBrush));
        }

        Grid itemLayers = Assert.IsType<Grid>(VisualTreeHelper.GetParent(selected));
        Assert.True(itemLayers.Children.IndexOf(selected) > itemLayers.Children.IndexOf(highlight),
            "The selected outline must be painted after the highlight fill.");
        Grid menuLayers = Assert.IsType<Grid>(VisualTreeHelper.GetParent(focus));
        Assert.True(menuLayers.Children.IndexOf(focus) > menuLayers.Children.IndexOf(Part<Border>(menu, "HoverLayer")));
        Assert.True(menuLayers.Children.IndexOf(focus) > menuLayers.Children.IndexOf(Part<Border>(menu, "PressedLayer")));

        BitmapSource image = Render(root, scale);
        Point edge = item.TranslatePoint(new Point(100, 1.5), root);
        var pixel = new byte[4];
        image.CopyPixels(new Int32Rect((int)(edge.X * scale), (int)(edge.Y * scale), 1, 1), pixel, 4, 0);
        Assert.Equal(ColorOf(resources, "Text.Primary"), Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]));
        Assert.False(menu.IsSubmenuOpen);
        Assert.Null(PresentationSource.FromVisual(root));

        item.Tag = null;
        menu.Tag = null;
        Layout(root);
        Assert.Equal(1d, selected.Opacity);
        Assert.Equal(0d, highlight.Opacity);
        Assert.Equal(0d, focus.Opacity);
    });

    private sealed record Sheet(Border Root, TextBlock Title, TextBlock Subtitle, TextBlock Body,
        TextBlock Caption, Button Primary, Button Secondary, Button Tool, Button Danger, Button Focused,
        ToggleButton Toggle, ToggleButton Rail, TabItem SelectedTab, ListBoxItem ListItem,
        TextBox Input, ComboBox Combo, Border Card);

    private static Sheet CreateSheet(ResourceDictionary resources, double scale)
    {
        T Styled<T>(T control, object key) where T : FrameworkElement
        {
            control.Style = (Style)resources[key];
            control.Margin = new Thickness(0, 0, 10, 10);
            return control;
        }

        WrapPanel Row(params UIElement[] children)
        {
            var row = new WrapPanel();
            foreach (UIElement child in children) row.Children.Add(child);
            return row;
        }

        TextBlock title = Styled(new TextBlock { Text = "캡처 도구" }, "Text.Title");
        TextBlock subtitle = Styled(new TextBlock { Text = "화면 편집" }, "Text.Section");
        TextBlock body = Styled(new TextBlock { Text = "캡처와 편집에 사용하는 공통 컨트롤" }, "Text.Body");
        TextBlock caption = Styled(new TextBlock { Text = "1920 × 1080 · PNG · 12 KB" }, "Text.MutedBlock");
        Button primary = Styled(new Button { Content = "저장", Width = 100 }, "Button.Primary");
        Button secondary = Styled(new Button { Content = "취소", Width = 100 }, "Button.Secondary");
        Button tool = Styled(new Button { Content = "도구", Width = 100 }, "Button.Ghost");
        Button danger = Styled(new Button { Content = "삭제", Width = 100 }, "Button.Danger");
        Button focused = Styled(new Button { Content = "포커스", Width = 100, Tag = "True" }, "Button.Ghost");
        ToggleButton toggle = Styled(new ToggleButton { Content = "선택 도구", IsChecked = true, Width = 110 }, typeof(ToggleButton));
        ToggleButton rail = Styled(new ToggleButton { Content = "R", IsChecked = true }, "Rail.ToolButton");
        TabItem tab = Styled(new TabItem { Header = "선택 탭", IsSelected = true }, "Tab.Help");
        var listItem = Styled(new ListBoxItem { Content = "선택 항목", IsSelected = true, Width = 150, MinHeight = 40 }, typeof(ListBoxItem));
        CheckBox check = Styled(new CheckBox { Content = "선택", IsChecked = true }, typeof(CheckBox));
        RadioButton radio = Styled(new RadioButton { Content = "항목", IsChecked = true }, typeof(RadioButton));
        TextBox input = Styled(new TextBox { Text = "파일 이름", Width = 190 }, typeof(TextBox));
        ComboBox combo = Styled(new ComboBox { ItemsSource = new[] { "PNG 이미지", "JPEG 이미지" }, SelectedIndex = 0, Width = 190 }, typeof(ComboBox));
        Slider slider = Styled(new Slider { Minimum = 0, Maximum = 100, Value = 60, Width = 190 }, typeof(Slider));
        var card = Styled(new Border
        {
            Width = 638,
            Child = Styled(new TextBlock { Text = "캡처 정보", Margin = new Thickness(0) }, "Text.Body"),
        }, "Card");

        var panel = new StackPanel { Margin = new Thickness(24) };
        foreach (UIElement item in new UIElement[] { title, subtitle, body, caption,
                     Row(primary, secondary, tool, danger, focused), Row(toggle, rail, tab, listItem),
                     Row(check, radio), Row(input, combo, slider), card }) panel.Children.Add(item);
        var surface = new Border { Child = panel };
        surface.SetResourceReference(Border.BackgroundProperty, "Surface.Base");
        var root = new Border
        {
            Width = SheetWidth, Height = SheetHeight, Resources = resources,
            Background = Brushes.Black, Child = surface,
            UseLayoutRounding = true, SnapsToDevicePixels = true,
        };
        root.SetResourceReference(TextElement.FontFamilyProperty, "Font.Ui");
        root.SetResourceReference(TextElement.FontSizeProperty, "FontSize.Body");
        VisualTreeHelper.SetRootDpi(root, new DpiScale(scale, scale));
        Layout(root);
        return new(root, title, subtitle, body, caption, primary, secondary, tool, danger, focused,
            toggle, rail, tab, listItem, input, combo, card);
    }

    private static ResourceDictionary LoadResources(string? baselineDirectory = null)
    {
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var combined = new XElement(p + "ResourceDictionary");
        foreach (string name in new[] { "Tokens.xaml", "Controls.xaml" })
        {
            using Stream source = baselineDirectory is null
                ? typeof(VisualSystemControlTests).Assembly.GetManifestResourceStream("ThemeFixture." + name)!
                : File.OpenRead(Path.Combine(baselineDirectory, "before-" + name));
            XElement dictionary = XDocument.Load(source).Root!;
            foreach (XAttribute attribute in dictionary.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
            {
                string value = attribute.Value;
                if (value.StartsWith("clr-namespace:MyCapture.", StringComparison.Ordinal) && !value.Contains(";assembly=", StringComparison.Ordinal))
                    value += ";assembly=MyCapture";
                combined.SetAttributeValue(attribute.Name, value);
            }
            combined.Add(dictionary.Elements());
        }

        // Substitute only input-state predicates in this isolated fixture. All shipped
        // highlight/focus setters and resource bindings run unchanged, without native input.
        foreach (XElement element in combined.Descendants().Where(element =>
                     element.Name.LocalName is "Trigger" or "Condition" &&
                     (string?)element.Attribute("Property") is "IsKeyboardFocused" or "IsKeyboardFocusWithin" or "IsHighlighted"))
            element.SetAttributeValue("Property", "Tag");
        return (ResourceDictionary)XamlReader.Parse(combined.ToString());
    }

    private static void ApplyBaseline(ResourceDictionary resources, string directory, AppTheme theme)
    {
        // Exported by compiling the original Core palette files at the baseline revision.
        // No current ThemeService or palette participates in the before image.
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "before-palettes.json")));
        foreach (JsonProperty entry in document.RootElement.GetProperty(theme.ToString()).EnumerateObject())
        {
            Color color = (Color)ColorConverter.ConvertFromString(entry.Value.GetString()!);
            var brush = resources[entry.Name] is SolidColorBrush existing ? existing.Clone() : new SolidColorBrush();
            brush.Color = color;
            brush.Freeze();
            resources[entry.Name] = brush;
        }
        bool contrast = theme == AppTheme.HighContrast;
        resources["Focus.RingThickness"] = new Thickness(contrast ? 2 : 1);
        resources["Focus.RingWidth"] = contrast ? 2d : 1d;
    }

    private static T Part<T>(Control control, string name) where T : DependencyObject =>
        Assert.IsType<T>(control.Template.FindName(name, control));

    private static Color ColorOf(ResourceDictionary resources, string key) => ColorOf((Brush)resources[key]);
    private static Color ColorOf(Brush brush) => Assert.IsType<SolidColorBrush>(brush).Color;

    private static void AssertPrimaryCaptionRenders(Sheet sheet, BitmapSource image, double scale)
    {
        TextBlock caption = Assert.Single(Descendants(sheet.Primary).OfType<TextBlock>(),
            text => text.Text == (string)sheet.Primary.Content);
        Assert.Equal(Visibility.Visible, caption.Visibility);
        Assert.True(caption.ActualWidth > 0 && caption.ActualHeight > 0);
        Assert.True(caption.ActualWidth + 0.5 >= caption.DesiredSize.Width, "The primary caption is clipped horizontally.");
        Assert.True(caption.ActualHeight + 0.5 >= caption.DesiredSize.Height, "The primary caption is clipped vertically.");
        Rect buttonBounds = caption.TransformToAncestor(sheet.Primary).TransformBounds(new Rect(caption.RenderSize));
        Assert.True(buttonBounds.Left >= -0.5 && buttonBounds.Top >= -0.5 &&
            buttonBounds.Right <= sheet.Primary.ActualWidth + 0.5 && buttonBounds.Bottom <= sheet.Primary.ActualHeight + 0.5,
            $"The primary caption leaves its button: {buttonBounds}.");

        Rect bounds = caption.TransformToAncestor(sheet.Root).TransformBounds(new Rect(caption.RenderSize));
        var pixelsBounds = new Int32Rect((int)Math.Floor(bounds.Left * scale), (int)Math.Floor(bounds.Top * scale),
            (int)Math.Ceiling(bounds.Right * scale) - (int)Math.Floor(bounds.Left * scale),
            (int)Math.Ceiling(bounds.Bottom * scale) - (int)Math.Floor(bounds.Top * scale));
        int stride = pixelsBounds.Width * 4;
        var pixels = new byte[stride * pixelsBounds.Height];
        image.CopyPixels(pixelsBounds, pixels, stride, 0);
        Color ink = ColorOf(sheet.Primary.Foreground);
        int inkPixels = 0;
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            // Count the opaque stroke interior, allowing ordinary antialiasing but excluding
            // the primary fill. Bounds alone cannot detect a missing WPF glyph raster.
            if (pixels[offset + 3] >= 240 && Math.Abs(pixels[offset] - ink.B) <= 40 &&
                Math.Abs(pixels[offset + 1] - ink.G) <= 40 && Math.Abs(pixels[offset + 2] - ink.R) <= 40)
                inkPixels++;
        }
        Assert.True(inkPixels >= 12 * scale * scale,
            $"The primary caption has only {inkPixels} foreground pixels at {scale * 100:0}% DPI.");
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (DependencyObject descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Layout(FrameworkElement root)
    {
        root.Measure(new Size(root.Width, root.Height));
        root.Arrange(new Rect(0, 0, root.Width, root.Height));
        root.UpdateLayout();
    }

    private static BitmapSource Render(FrameworkElement root, double scale)
    {
        // Let queued binding, layout and glyph work complete on the isolated STA before
        // capturing its first frame. No Window or desktop input participates in this pump.
        root.Dispatcher.Invoke(DispatcherPriority.ContextIdle, new Action(root.UpdateLayout));
        var bitmap = new RenderTargetBitmap((int)(root.ActualWidth * scale), (int)(root.ActualHeight * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(root);
        bitmap.Freeze();
        return bitmap;
    }

    private static void Save(BitmapSource image, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("MYCAPTURE_VISUAL_EVIDENCE");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using FileStream stream = File.Create(Path.Combine(directory, name));
        encoder.Save(stream);
    }

    private static void Run(Action body) => StaTestHost.Run(() =>
    {
        bool? previous = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        try { body(); }
        finally { FluidMotion.AnimationsEnabledOverrideForTest = previous; }
    });
}
