using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Diagnostics;
using MyCapture.App.Recording;
using MyCapture.App.Themes;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class SegmentedChoiceRenderingTests
{
    public static IEnumerable<object[]> PalettesAndScales()
    {
        foreach (string palette in new[] { "midnight", "daylight", "glass", "glass-light", "workspace-gallery", "workspace-video", "system-dark", "system-light" })
        foreach (double scale in new[] { 1d, 1.5d })
            yield return [palette, scale];
    }

    [Theory]
    [MemberData(nameof(PalettesAndScales))]
    public void ExportChoicesRenderRaisedSelectionAndEmphasizeOnlyTheCurrentLabel(string palette, double scale) => Run(() =>
    {
        ResourceDictionary resources = Resources(palette);
        foreach (string[] labels in new[] { new[] { "MP4", "GIF" }, new[] { "원본 화질", "작게", "더 작게" } })
        {
            (object choice, Border track, ToggleButton[] buttons, PropertyInfo selectedIndex) = ExportChoice(labels);
            WithHost(resources, track, root =>
            {
                Assert.Same(resources["Surface.Sunken"], track.Background);
                for (int selected = 0; selected < buttons.Length; selected++)
                {
                    selectedIndex.SetValue(choice, selected);
                    ControlStateMotionTests.Drain(root);
                    root.UpdateLayout();
                    BitmapSource image = Render(root, scale);
                    SaveEvidence(image, $"export-{labels.Length}-{palette}-{scale:0.0}-{selected}");
                    for (int i = 0; i < buttons.Length; i++)
                    {
                        ToggleButton button = buttons[i];
                        Border layer = Part<Border>(button, "CheckedLayer");
                        bool current = i == selected;
                        Assert.Equal(current, button.IsChecked);
                        Assert.Equal(current ? 1d : 0d, layer.Opacity);
                        AssertLabelWeight(button, labels[i], current ? FontWeights.SemiBold : FontWeights.Normal);
                        if (current) AssertRaisedPixels(resources, root, image, layer, track.Background, scale);
                    }
                }
            }, scale);
        }
    });

    [Theory]
    [MemberData(nameof(PalettesAndScales))]
    public void SharedModeTabsUseTheSameRaisedSelectionAndResetThePreviousLabel(string palette, double scale) => Run(() =>
    {
        ResourceDictionary resources = Resources(palette);
        TabControl tabs = ModeTabs(resources);
        WithHost(resources, tabs, root =>
        {
            for (int selected = 0; selected < tabs.Items.Count; selected++)
            {
                tabs.SelectedIndex = selected;
                ControlStateMotionTests.Drain(root);
                root.UpdateLayout();
                BitmapSource image = Render(root, scale);
                SaveEvidence(image, $"tabs-{palette}-{scale:0.0}-{selected}");
                for (int i = 0; i < tabs.Items.Count; i++)
                {
                    TabItem tab = Assert.IsType<TabItem>(tabs.Items[i]);
                    Border track = Part<Border>(tab, "TabBorder");
                    Border layer = Part<Border>(tab, "SelectedLayer");
                    Assert.Same(resources["Surface.Sunken"], track.Background);
                    Assert.Equal(i == selected ? 1d : 0d, layer.Opacity);
                    Assert.Equal(i == selected ? FontWeights.SemiBold : FontWeights.Normal, tab.FontWeight);
                    if (i == selected) AssertRaisedPixels(resources, root, image, layer, track.Background, scale);
                }
            }
        }, scale);
    });

    [Theory]
    [InlineData("night-sky")]
    [InlineData("desert")]
    public void WindowsContrastChoicesAndTabsPreserveTheirSystemSelectionRoles(string palette) => Run(() =>
    {
        IReadOnlyDictionary<SystemColorRole, Color> system = palette == "night-sky"
            ? SystemHighContrastThemeTests.NightSky : SystemHighContrastThemeTests.Desert;
        ThemeService.SystemHighContrastOverrideForTest = true;
        ThemeService.SystemColorsOverrideForTest = role => system[role];
        ResourceDictionary resources = SystemHighContrastThemeTests.LoadTheme();
        ThemeService.ApplyResources(AppTheme.Workspace, resources, systemHighContrast: true);
        (object choice, Border track, ToggleButton[] buttons, PropertyInfo selectedIndex) = ExportChoice(["MP4", "GIF"]);
        selectedIndex.SetValue(choice, 0);
        var panel = new StackPanel();
        panel.Children.Add(track);
        TabControl tabs = ModeTabs(resources);
        panel.Children.Add(tabs);
        WithHost(resources, panel, root =>
        {
            Border toggleLayer = Part<Border>(buttons[0], "CheckedLayer");
            TabItem selectedTab = Assert.IsType<TabItem>(tabs.Items[0]);
            Border tabLayer = Part<Border>(selectedTab, "SelectedLayer");
            foreach (Border layer in new[] { toggleLayer, tabLayer })
            {
                Assert.Equal(1d, layer.Opacity);
            }
            Assert.Equal(system[SystemColorRole.Highlight], ColorOf(toggleLayer.Background));
            Assert.Equal(system[SystemColorRole.Window], ColorOf(tabLayer.Background));
            Assert.Equal(new Thickness(0), toggleLayer.BorderThickness);
            Assert.Equal(new Thickness(2), tabLayer.BorderThickness);
            Assert.Equal(system[SystemColorRole.WindowText], ColorOf(tabLayer.BorderBrush));
            Assert.True(Contrast(ColorOf(tabLayer.BorderBrush), ColorOf(track.Background)) >= 3);
            Assert.True(Contrast(ColorOf(tabLayer.BorderBrush), ColorOf(tabLayer.Background)) >= 3);
            Assert.Equal(system[SystemColorRole.Window], ColorOf(track.Background));
            Assert.Equal(system[SystemColorRole.HighlightText], ColorOf(buttons[0].Foreground));
            Assert.Equal(system[SystemColorRole.WindowText], ColorOf(selectedTab.Foreground));
            AssertLabelWeight(buttons[0], "MP4", FontWeights.SemiBold);
            AssertLabelWeight(buttons[1], "GIF", FontWeights.Normal);
            Assert.Equal(FontWeights.Normal, Assert.IsType<TabItem>(tabs.Items[1]).FontWeight);
            BitmapSource image = Render(root, 1);
            Assert.Equal(system[SystemColorRole.Highlight], Sample(image, toggleLayer, root, toggleLayer.ActualWidth / 2, 4, 1));
            Assert.Equal(system[SystemColorRole.Window], Sample(image, tabLayer, root, tabLayer.ActualWidth / 2, 4, 1));
            SaveEvidence(image, $"contrast-{palette}");
        });
    });

    [Theory]
    [InlineData("night-sky")]
    [InlineData("desert")]
    public void ContrastModeTabFocusKeepsSystemInkForSelectedAndUnselectedTabs(string palette) => Run(() =>
    {
        IReadOnlyDictionary<SystemColorRole, Color> system = palette == "night-sky"
            ? SystemHighContrastThemeTests.NightSky : SystemHighContrastThemeTests.Desert;
        ThemeService.SystemHighContrastOverrideForTest = true;
        ThemeService.SystemColorsOverrideForTest = role => system[role];
        ResourceDictionary resources = SystemHighContrastThemeTests.LoadTheme();
        ThemeService.ApplyResources(AppTheme.Workspace, resources, systemHighContrast: true);
        // A standalone tab lets selection and actual keyboard focus vary independently, without
        // TabControl moving focus to a newly selected tab before its old template can be examined.
        var tab = new TabItem { Header = "표", IsSelected = true, Style = (Style)resources["Tab.Help"] };
        var other = new Button { Content = "Next" };
        var panel = new StackPanel();
        panel.Children.Add(tab);
        panel.Children.Add(other);
        WithHost(resources, panel, root =>
        {
            Border ring = Part<Border>(tab, "FocusRing");
            KeyboardFocusCue.NotifyKeyboardInput(root);
            Assert.Same(tab, Keyboard.Focus(tab));
            ControlStateMotionTests.Drain(root);
            Assert.True(tab.IsKeyboardFocused);
            Assert.True(KeyboardFocusCue.GetIsVisible(tab));
            Assert.Equal(1d, ring.Opacity);
            Assert.Equal(new Thickness(2), ring.BorderThickness);
            Assert.Equal(system[SystemColorRole.WindowText], ColorOf(ring.BorderBrush));
            Assert.Equal(system[SystemColorRole.WindowText], Sample(Render(root, 1), ring, root, ring.ActualWidth / 2, 0, 1));

            tab.IsSelected = false;
            ControlStateMotionTests.Drain(root);
            Assert.True(tab.IsKeyboardFocused);
            Assert.Equal(1d, ring.Opacity);
            Assert.Equal(new Thickness(2), ring.BorderThickness);
            Assert.Equal(system[SystemColorRole.WindowText], ColorOf(ring.BorderBrush));
            Assert.Equal(system[SystemColorRole.WindowText], Sample(Render(root, 1), ring, root, ring.ActualWidth / 2, 0, 1));

            tab.IsSelected = true;
            Assert.Same(other, Keyboard.Focus(other));
            ControlStateMotionTests.Drain(root);
            Assert.False(tab.IsKeyboardFocused);
            Assert.Equal(0d, ring.Opacity);
            Assert.Equal(system[SystemColorRole.WindowText], ColorOf(ring.BorderBrush));
            KeyboardFocusCue.NotifyPointerInput(root);
        });
    });

    [Fact]
    public void LivePaletteChangesKeepCurrentSelectionReadableWithoutRecreatingControls() => Run(() =>
    {
        ResourceDictionary resources = Resources("midnight");
        (object choice, Border track, ToggleButton[] buttons, PropertyInfo selectedIndex) = ExportChoice(["MP4", "GIF"]);
        selectedIndex.SetValue(choice, 1);
        WithHost(resources, track, root =>
        {
            foreach (AppTheme theme in new[] { AppTheme.Daylight, AppTheme.Midnight })
            {
                ThemeService.ApplyResources(theme, resources, systemHighContrast: false);
                ControlStateMotionTests.Drain(root);
                root.UpdateLayout();
                Assert.True(buttons[1].IsChecked);
                AssertRaisedPixels(resources, root, Render(root, 1), Part<Border>(buttons[1], "CheckedLayer"), track.Background, 1);
                AssertLabelWeight(buttons[0], "MP4", FontWeights.Normal);
                AssertLabelWeight(buttons[1], "GIF", FontWeights.SemiBold);
            }
        });
    });

    private static void AssertRaisedPixels(ResourceDictionary resources, FrameworkElement root, BitmapSource image,
        Border layer, Brush trackBrush, double scale)
    {
        Color track = ColorOf(trackBrush);
        Color face = ColorOf(layer.Background);
        Color edge = ColorOf(layer.BorderBrush);
        Assert.Equal(ColorOf(resources["Surface.Floating"]), face);
        Assert.Equal(ColorOf(resources["Stroke.Selected"]), edge);
        Assert.Equal(new Thickness(1), layer.BorderThickness);
        Assert.True(Contrast(face, track) >= 1.2, $"Selected face {face} against track {track} must reach 1.2:1.");
        Assert.True(Contrast(edge, track) >= 3, $"Selected edge {edge} against track {track} must reach 3:1.");
        Color paintedFace = Sample(image, layer, root, layer.ActualWidth / 2, 4, scale);
        Assert.Equal(face, paintedFace);
        Assert.True(Contrast(paintedFace, track) >= 1.2);

        // A one-DIP edge is one or one-and-a-half physical pixels. Sample its most covered pixel,
        // not the partially covered pixel at the fractional outer edge at 150 percent.
        Point top = layer.TransformToAncestor(root).Transform(new Point(layer.ActualWidth / 2, 0));
        int x = (int)Math.Floor(top.X * scale);
        int y = (int)Math.Floor(top.Y * scale);
        double paintedEdgeContrast = Math.Max(Contrast(Pixel(image, x, y), track), Contrast(Pixel(image, x, y + 1), track));
        Assert.True(paintedEdgeContrast >= 3, $"The rendered selected edge must reach 3:1, got {paintedEdgeContrast:F3}.");
    }

    private static (object Choice, Border Track, ToggleButton[] Buttons, PropertyInfo SelectedIndex) ExportChoice(string[] labels)
    {
        // Exercise the product's actual export composition, including its command-label wrapper.
        // Constructing this UI-only helper avoids opening a media file or starting an exporter.
        Type type = typeof(VideoExportDialog).GetNestedType("SegmentedChoice", BindingFlags.NonPublic)!;
        IReadOnlyList<(string Caption, string? Help)> choices = labels.Select(label => (label, (string?)null)).ToArray();
        object choice = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, args: [choices], culture: null)!;
        Border track = Assert.IsType<Border>(type.GetProperty("Element", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(choice));
        ToggleButton[] buttons = Assert.IsType<UniformGrid>(track.Child).Children.Cast<ToggleButton>().ToArray();
        PropertyInfo selectedIndex = type.GetProperty("SelectedIndex", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (choice, track, buttons, selectedIndex);
    }

    private static TabControl ModeTabs(ResourceDictionary resources)
    {
        var tabs = new TabControl { SelectedIndex = 0, Height = 42 };
        foreach (string label in new[] { "표", "코드", "텍스트" })
            tabs.Items.Add(new TabItem { Header = label, MinWidth = 88, Padding = new Thickness(14, 7, 14, 7), Style = (Style)resources["Tab.Help"] });
        return tabs;
    }

    private static void AssertLabelWeight(ToggleButton button, string label, FontWeight expected)
    {
        Assert.Equal(expected, button.FontWeight);
        TextBlock text = Assert.Single(Descendants(button).OfType<TextBlock>(), text => text.Text == label);
        Assert.Equal(expected, text.FontWeight);
    }

    private static ResourceDictionary Resources(string palette)
    {
        ResourceDictionary resources = SystemHighContrastThemeTests.LoadTheme();
        ThemeService.SystemLightThemeReaderForTest = () => palette == "system-light";
        if (palette.StartsWith("workspace-", StringComparison.Ordinal))
            WorkspaceTheme.Apply(resources, palette == "workspace-gallery" ? WorkspaceRole.Gallery : WorkspaceRole.VideoEditor,
                AppTheme.Workspace, systemHighContrast: false);
        else
            ThemeService.ApplyResources(palette switch
            {
                "daylight" => AppTheme.Daylight,
                "glass" => AppTheme.Glass,
                "glass-light" => AppTheme.GlassLight,
                "system-dark" or "system-light" => AppTheme.System,
                _ => AppTheme.Midnight,
            }, resources, systemHighContrast: false);
        return resources;
    }

    private static void WithHost(ResourceDictionary resources, UIElement content, Action<Border> body, double scale = 1)
    {
        var root = new Border { Resources = resources, Width = 440, Height = 140, Padding = new Thickness(16),
            Child = content, UseLayoutRounding = true, SnapsToDevicePixels = true };
        root.SetResourceReference(Border.BackgroundProperty, "Surface.Base");
        root.SetResourceReference(TextElement.FontFamilyProperty, "Font.Ui");
        root.SetResourceReference(TextElement.FontSizeProperty, "FontSize.Body");
        var window = new Window { Content = root, Width = 440, Height = 140, Left = -32000, Top = -32000,
            WindowStyle = WindowStyle.None, ShowActivated = false, ShowInTaskbar = false, SizeToContent = SizeToContent.WidthAndHeight };
        ModernWindowChrome.SetEnabled(window, false);
        FluidMotion.SetWindowEntrance(window, false);
        try
        {
            window.Show();
            VisualTreeHelper.SetRootDpi(window, new DpiScale(scale, scale));
            ControlStateMotionTests.Drain(root);
            root.UpdateLayout();
            body(root);
        }
        finally { window.Close(); }
    }

    private static BitmapSource Render(FrameworkElement root, double scale)
    {
        Assert.Equal(new Vector(0, 0), VisualTreeHelper.GetOffset(root));
        Assert.Equal(scale, VisualTreeHelper.GetDpi(root).DpiScaleX);
        Assert.Equal(scale, VisualTreeHelper.GetDpi(root).DpiScaleY);
        var image = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * scale), (int)Math.Ceiling(root.ActualHeight * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        image.Render(root);
        return image;
    }

    private static Color Sample(BitmapSource image, FrameworkElement element, Visual root, double x, double y, double scale)
    {
        Point point = element.TransformToAncestor(root).Transform(new Point(x, y));
        return Pixel(image, (int)Math.Floor(point.X * scale), (int)Math.Floor(point.Y * scale));
    }

    private static Color Pixel(BitmapSource image, int x, int y)
    {
        byte[] pixel = new byte[4];
        image.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        Assert.Equal(255, pixel[3]);
        return Color.FromRgb(pixel[2], pixel[1], pixel[0]);
    }

    private static Color ColorOf(object brush) => Assert.IsType<SolidColorBrush>(brush).Color;
    private static T Part<T>(Control control, string name) where T : DependencyObject =>
        Assert.IsType<T>(control.Template.FindName(name, control));

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (DependencyObject descendant in Descendants(child)) yield return descendant;
        }
    }

    private static double Contrast(Color first, Color second)
    {
        static double Channel(byte value) => value / 255d <= 0.04045 ? value / 255d / 12.92 : Math.Pow((value / 255d + 0.055) / 1.055, 2.4);
        static double Luminance(Color color) => 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
        double a = Luminance(first), b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static void SaveEvidence(BitmapSource image, string name)
    {
        string? requested = Environment.GetEnvironmentVariable("MYCAPTURE_SEGMENT_EVIDENCE");
        if (string.IsNullOrWhiteSpace(requested)) return;
        string parent = DiagnosticOutputPaths.Create(requested);
        string directory = TestDiagnosticArtifacts.NewDirectory(DiagnosticOutputPaths.Child(parent, $"{name}-{Guid.NewGuid():N}"));
        TestDiagnosticArtifacts.SavePng(directory, "segments.png", image);
    }

    private static void Run(Action action) => StaTestHost.Run(() =>
    {
        bool? previousMotion = FluidMotion.AnimationsEnabledOverrideForTest;
        bool? previousContrast = ThemeService.SystemHighContrastOverrideForTest;
        Func<SystemColorRole, Color>? previousColors = ThemeService.SystemColorsOverrideForTest;
        Func<bool>? previousLight = ThemeService.SystemLightThemeReaderForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        ThemeService.SystemHighContrastOverrideForTest = false;
        try { action(); }
        finally
        {
            FluidMotion.AnimationsEnabledOverrideForTest = previousMotion;
            ThemeService.SystemHighContrastOverrideForTest = previousContrast;
            ThemeService.SystemColorsOverrideForTest = previousColors;
            ThemeService.SystemLightThemeReaderForTest = previousLight;
        }
    });
}
