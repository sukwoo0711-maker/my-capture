using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Xml.Linq;
using MyCapture.App.Themes;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class AccentColorThemeTests
{
    [Fact]
    public void SelectedToolAndCheckUseAccessibleRedAndRestoreTheirOriginalVioletPixels() => WithMotionDisabled(() =>
    {
        foreach ((AppTheme theme, WorkspaceRole? role) in ControlPalettes())
        {
            ResourceDictionary resources = LoadControlResources();
            ApplyPalette(resources, theme, role, AppAccent.Violet);
            var tool = new ToggleButton { IsChecked = true, Style = (Style)resources["Rail.ToolButton"], Content = "R" };
            var check = new CheckBox { IsChecked = true, Content = "Check", Style = (Style)resources[typeof(CheckBox)] };
            var panel = new StackPanel();
            panel.Children.Add(tool);
            panel.Children.Add(check);
            var root = new Border { Width = 260, Height = 280, Resources = resources, Child = panel };
            root.SetResourceReference(Border.BackgroundProperty, "Surface.Raised");
            Layout(root);
            byte[] violet = RenderTokens(resources);
            var original = Assert.IsType<SolidColorBrush>(resources["Accent.Indicator"]);
            Color originalColor = original.Color;
            ApplyPalette(resources, theme, role, AppAccent.Red);
            Layout(root);
            Assert.True(original.IsFrozen);
            Assert.Equal(originalColor, original.Color);
            Assert.NotSame(original, resources["Accent.Indicator"]);
            foreach (Control control in new Control[] { tool, check })
            {
                Border selected = Part<Border>(control, "CheckedLayer");
                Brush ink = control is CheckBox ? Part<System.Windows.Shapes.Path>(control, "Check").Stroke : control.Foreground;
                Assert.True(ThemeContrast.Ratio(BrushColor(ink), BrushColor(selected.Background)) >= 4.5);
                if (theme == AppTheme.HighContrast) continue;
                foreach (Color backdrop in new[] { Colors.Black, Colors.White })
                    AssertContrast(selected.BorderBrush, ThemeContrast.Composite(BrushColor(root.Background), ToThemeColor(backdrop)),
                        $"{theme}/{role}/{control.GetType().Name} selection edge");
            }
            ApplyPalette(resources, theme, role, AppAccent.Violet);
            Layout(root);
            Assert.Equal(violet, RenderTokens(resources));
            Assert.Equal(Colors.Transparent, ((SolidColorBrush)resources["Border.SelectedTool"]).Color);
        }
    });

    [Fact]
    public void ResolvedAccentIndicatorsMeetContrastOnTheirCompositedBackgrounds() => WithMotionDisabled(() =>
    {
        foreach ((AppTheme theme, WorkspaceRole? role) in ControlPalettes())
        {
            ResourceDictionary resources = LoadControlResources();
            ApplyPalette(resources, theme, role, AppAccent.Violet);
            ControlSheet sheet = CreateControlSheet(resources);
            // Reuse the controls across switches to exercise their live DynamicResource bindings.
            foreach (AppAccent accent in new[] { AppAccent.Violet, AppAccent.Red, AppAccent.Violet })
            {
                ApplyPalette(resources, theme, role, accent);
                Layout(sheet.Root);
                foreach (Color backdrop in new[] { Colors.Black, Colors.White })
                {
                    string context = $"{theme}/{role}/{accent}/{backdrop}";
                    ThemeColor surface = ThemeContrast.Composite(BrushColor(sheet.Surface.Background), ToThemeColor(backdrop));
                    foreach (Slider slider in sheet.Sliders)
                    {
                        var track = Assert.IsType<Track>(slider.Template.FindName("PART_Track", slider));
                        Thumb thumb = track.Thumb;
                        thumb.ApplyTemplate();
                        var centre = Assert.IsType<Ellipse>(thumb.Template.FindName("Centre", thumb));
                        var disc = Assert.IsType<Grid>(VisualTreeHelper.GetChild(thumb, 0)).Children.OfType<Ellipse>().First();
                        AssertContrast(centre.Fill, ThemeContrast.Composite(BrushColor(disc.Fill), surface), context + " slider centre");
                        AssertContrast(track.DecreaseRepeatButton.Background, surface, context + " slider track");
                    }
                    var selected = Part<Border>(sheet.SelectedTab, "SelectedLayer");
                    var tab = Part<Border>(sheet.SelectedTab, "TabBorder");
                    ThemeColor tabSurface = ThemeContrast.Composite(BrushColor(tab.Background), surface);
                    AssertContrast(selected.BorderBrush, ThemeContrast.Composite(BrushColor(selected.Background), tabSurface), context + " selected tab");
                    var focused = Part<Border>(sheet.FocusedTab, "TabBorder");
                    AssertContrast(focused.BorderBrush, ThemeContrast.Composite(BrushColor(focused.Background), surface), context + " focused tab");
                    AssertContrast(Part<Border>(sheet.ScrollThumb, "Grip").Background, surface, context + " dragged scrollbar");
                }
            }
        }
    });

    [Fact]
    public void VioletControlsMatchFixedIntegrationBaselineInEveryPaletteAndWorkspaceRole() => WithMotionDisabled(() =>
    {
        foreach ((AppTheme theme, WorkspaceRole? role) in ControlPalettes())
        {
            ResourceDictionary baseline = LoadIntegrationBaseline(role is null ? theme.ToString() : $"Workspace{role}");
            ResourceDictionary resources = LoadControlResources();
            ApplyPalette(resources, theme, role, AppAccent.Violet);
            ControlSheet expected = CreateControlSheet(baseline);
            ControlSheet actual = CreateControlSheet(resources);
            foreach (Color backdrop in new[] { Colors.Black, Colors.White })
            {
                expected.Root.Background = new SolidColorBrush(backdrop);
                actual.Root.Background = new SolidColorBrush(backdrop);
                Assert.True(RenderControlSheet(expected).SequenceEqual(RenderControlSheet(actual)), $"{theme}/{role}/{backdrop} baseline");
            }
            ApplyPalette(resources, theme, role, AppAccent.Red);
            Assert.Equal(theme == AppTheme.HighContrast, RenderControlSheet(expected).SequenceEqual(RenderControlSheet(actual)));
            ApplyPalette(resources, theme, role, AppAccent.Violet);
            Assert.True(RenderControlSheet(expected).SequenceEqual(RenderControlSheet(actual)), $"{theme}/{role} restored baseline");
        }
    });

    private static IEnumerable<(AppTheme Theme, WorkspaceRole? Role)> ControlPalettes()
    {
        foreach (AppTheme theme in Enum.GetValues<AppTheme>()) yield return (theme, null);
        foreach (WorkspaceRole role in Enum.GetValues<WorkspaceRole>()) yield return (AppTheme.Workspace, role);
    }

    private static void ApplyPalette(ResourceDictionary resources, AppTheme theme, WorkspaceRole? role, AppAccent accent)
    {
        if (role is { } workspaceRole) WorkspaceTheme.Apply(resources, workspaceRole, theme, false, accent);
        else ThemeService.ApplyResources(theme, resources, systemHighContrast: false, accent: accent);
    }

    private static ResourceDictionary LoadIntegrationBaseline(string palette)
    {
        // This fixture is pinned to d2440d5, before the accent lane. Neither its styles nor its
        // palettes are read from the current theme implementation, even after a round trip.
        ResourceDictionary resources = LoadControlResources("AccentFixture.VioletIntegrationBaseline.xaml");
        var colors = (ResourceDictionary)resources[palette];
        foreach (string key in colors.Keys) resources[key] = colors[key];
        return resources;
    }

    private static ResourceDictionary LoadControlResources(params string[] names)
    {
        if (names.Length == 0) names = ["ThemeFixture.Tokens.xaml", "ThemeFixture.Controls.xaml"];
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var combined = new XElement(p + "ResourceDictionary");
        foreach (string name in names)
        {
            using Stream stream = typeof(AccentColorThemeTests).Assembly.GetManifestResourceStream(name)!;
            XElement dictionary = XDocument.Load(stream).Root!;
            foreach (XAttribute attribute in dictionary.Attributes().Where(a => a.IsNamespaceDeclaration))
            {
                string value = attribute.Value;
                if (value.StartsWith("clr-namespace:MyCapture.", StringComparison.Ordinal) && !value.Contains(";assembly=", StringComparison.Ordinal))
                    value += ";assembly=MyCapture";
                combined.SetAttributeValue(attribute.Name, value);
            }
            combined.Add(dictionary.Elements());
        }
        // WPF computes IsKeyboardFocused from the real keyboard device. Substitute only the
        // predicate in this isolated fixture so the shipped focus setters run without taking
        // keyboard focus from another worker. Keep their resource expressions untouched.
        XElement tab = combined.Elements(p + "Style").Single(e => (string?)e.Attribute(x + "Key") == "Tab.Help");
        XElement focus = tab.Descendants(p + "Trigger").Single(e => (string?)e.Attribute("Property") == "IsKeyboardFocused");
        focus.SetAttributeValue("Property", "Tag");
        return (ResourceDictionary)XamlReader.Parse(combined.ToString());
    }

    private sealed record ControlSheet(Border Root, Border Surface, Slider[] Sliders, TabItem SelectedTab,
        TabItem FocusedTab, Thumb ScrollThumb);

    private static ControlSheet CreateControlSheet(ResourceDictionary resources)
    {
        var panel = new StackPanel { Margin = new Thickness(12) };
        var selectedTab = new TabItem { Header = "Selected", IsSelected = true, Style = (Style)resources["Tab.Help"] };
        var focusedTab = new TabItem { Header = "Focused", Style = (Style)resources["Tab.Help"] };
        panel.Children.Add(selectedTab);
        panel.Children.Add(focusedTab);
        var horizontal = new Slider { Minimum = 0, Maximum = 100, Value = 40, Width = 220, Style = (Style)resources[typeof(Slider)] };
        var vertical = new Slider { Minimum = 0, Maximum = 100, Value = 60, Height = 80, Orientation = Orientation.Vertical, Style = (Style)resources[typeof(Slider)] };
        panel.Children.Add(horizontal);
        panel.Children.Add(vertical);
        panel.Children.Add(new SymbolIcon
        {
            Data = Geometry.Parse("M 3,10 L 8,15 L 17,5"),
            Style = (Style)resources["Icon.Symbol.Accent"],
        });
        var scrollThumb = new Thumb { Width = 80, Height = 16, Style = (Style)resources["ScrollBar.Thumb"] };
        panel.Children.Add(scrollThumb);
        var surface = new Border { Child = panel };
        surface.SetResourceReference(Border.BackgroundProperty, "Surface.Base");
        var root = new Border { Child = surface, Background = Brushes.Black, Resources = resources, Width = 260, Height = 280 };
        Layout(root);
        focusedTab.Tag = "True";
        SetReadOnlyState(scrollThumb, typeof(Thumb), "IsDraggingPropertyKey");
        Layout(root);
        Assert.True(scrollThumb.IsDragging);
        Assert.Equal(1d, Part<Border>(selectedTab, "SelectedLayer").Opacity);
        return new(root, surface, [horizontal, vertical], selectedTab, focusedTab, scrollThumb);
    }

    private static void SetReadOnlyState(DependencyObject target, Type owner, string field)
    {
        var key = Assert.IsType<DependencyPropertyKey>(owner.GetField(field, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null));
        target.SetValue(key, true);
    }

    private static T Part<T>(Control control, string name) where T : DependencyObject =>
        Assert.IsType<T>(control.Template.FindName(name, control));

    private static void Layout(FrameworkElement root)
    {
        root.Measure(new Size(root.Width, root.Height));
        root.Arrange(new Rect(0, 0, root.Width, root.Height));
        root.UpdateLayout();
    }

    private static byte[] RenderControlSheet(ControlSheet sheet)
    {
        Layout(sheet.Root);
        var bitmap = new RenderTargetBitmap(260, 280, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(sheet.Root);
        byte[] pixels = new byte[260 * 280 * 4];
        bitmap.CopyPixels(pixels, 260 * 4, 0);
        return pixels;
    }

    private static ThemeColor BrushColor(Brush brush) => ToThemeColor(Assert.IsType<SolidColorBrush>(brush).Color);
    private static ThemeColor ToThemeColor(Color color) => ThemeColor.Argb(color.A, color.R, color.G, color.B);

    private static void AssertContrast(Brush brush, ThemeColor background, string context)
    {
        double ratio = ThemeContrast.Ratio(ThemeContrast.Composite(BrushColor(brush), background), background);
        Assert.True(ratio >= 3, $"{context}: {ratio:F4}:1, expected at least 3:1");
    }

    private static void WithMotionDisabled(Action body) => StaTestHost.Run(() =>
    {
        bool? previous = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        try { body(); }
        finally { FluidMotion.AnimationsEnabledOverrideForTest = previous; }
    });
}
