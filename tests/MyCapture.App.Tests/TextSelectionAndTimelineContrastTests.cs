using System.IO;
using System.Reflection;
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
using MyCapture.App.Recording;
using MyCapture.App.Themes;
using MyCapture.Core.Recording;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>Renders real text selection and timeline drawing commands without a Window or native input.</summary>
public sealed class TextSelectionAndTimelineContrastTests
{
    private const string SelectedText = "Selected text";
    private static readonly string[] Scenarios =
    ["glass", "glass-light", "high-contrast", "system-light", "windows-dark", "windows-light",
     "workspace-gallery", "workspace-video"];

    private static readonly IReadOnlyDictionary<SystemColorRole, Color> DarkContrast = new Dictionary<SystemColorRole, Color>
    {
        [SystemColorRole.Window] = Colors.Black,
        [SystemColorRole.WindowText] = Colors.White,
        [SystemColorRole.ButtonFace] = Colors.Black,
        [SystemColorRole.ButtonText] = Color.FromRgb(255, 238, 50),
        [SystemColorRole.Highlight] = Color.FromRgb(214, 180, 253),
        [SystemColorRole.HighlightText] = Color.FromRgb(43, 43, 43),
        [SystemColorRole.GrayText] = Color.FromRgb(166, 166, 166),
        [SystemColorRole.HotTrack] = Color.FromRgb(128, 128, 255),
    };

    private static readonly IReadOnlyDictionary<SystemColorRole, Color> LightContrast = new Dictionary<SystemColorRole, Color>
    {
        [SystemColorRole.Window] = Color.FromRgb(255, 250, 239),
        [SystemColorRole.WindowText] = Color.FromRgb(61, 61, 61),
        [SystemColorRole.ButtonFace] = Color.FromRgb(255, 250, 239),
        [SystemColorRole.ButtonText] = Color.FromRgb(32, 32, 32),
        [SystemColorRole.Highlight] = Color.FromRgb(144, 57, 9),
        [SystemColorRole.HighlightText] = Color.FromRgb(255, 245, 227),
        [SystemColorRole.GrayText] = Color.FromRgb(103, 103, 103),
        [SystemColorRole.HotTrack] = Color.FromRgb(28, 94, 117),
    };

    public static IEnumerable<object[]> ThemeAndScale()
    {
        foreach (string scenario in Scenarios)
            foreach (double scale in new[] { 1d, 2d })
                yield return [scenario, scale];
    }

    [Theory]
    [MemberData(nameof(ThemeAndScale))]
    public void SelectedTextAndActualTimelineGlyphsRemainReadable(string scenario, double scale) => Run(() =>
    {
        AssertSelectionRendererConfiguration();
        using Fixture fixture = CreateFixture(scenario, scale);
        BitmapSource image = Render(fixture.Root, scale);
        SaveEvidence(fixture, image, scenario, scale, "after",
            Environment.GetEnvironmentVariable("MYCAPTURE_FIX_EVIDENCE"));
        AssertSelection(fixture, image, scale);
        AssertTimeline(fixture, image, scale);
        Assert.Null(PresentationSource.FromVisual(fixture.Root));
        Rect selectionBounds = SelectionBounds(fixture);
        Color selectionFill = Solid(fixture.Box.SelectionBrush).Color;
        fixture.Box.Select(0, 0);
        BitmapSource unselected = Render(fixture.Root, scale);
        Assert.False(HasSelectionFill(unselected, selectionBounds, scale, selectionFill),
            "The same pixel check must reject ordinary unselected text.");
    });

    [Fact]
    public void ExistingControlsFollowSelectionAndTimelineTokensAcrossSystemContrastChanges() => Run(() =>
    {
        AssertSelectionRendererConfiguration();
        using Fixture fixture = CreateFixture("glass-light", 1);
        SolidColorBrush originalSelection = Solid(fixture.Box.SelectionBrush);
        Color originalColor = originalSelection.Color;
        foreach (string scenario in new[] { "windows-dark", "windows-light", "glass-light" })
        {
            Apply(scenario, fixture.Resources);
            Layout(fixture.Root);
            BitmapSource image = Render(fixture.Root, 1);
            SaveEvidence(fixture, image, scenario, 1, "after-live",
                Environment.GetEnvironmentVariable("MYCAPTURE_FIX_EVIDENCE"));
            AssertSelection(fixture, image, 1);
            AssertTimeline(fixture, image, 1);
            Assert.Equal(originalColor, originalSelection.Color);
            Assert.NotSame(originalSelection, fixture.Box.SelectionBrush);
        }
    });

    [Fact]
    public void ActualSettingsPasswordStyleFollowsReadableSelectionTokens() => Run(() =>
    {
        AssertSelectionRendererConfiguration();
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        Assembly assembly = typeof(TextSelectionAndTimelineContrastTests).Assembly;
        using Stream settings = assembly.GetManifestResourceStream("SettingsFixture.Window.xaml")!;
        XElement style = XDocument.Load(settings).Descendants(presentation + "Style")
            .Single(element => (string?)element.Attribute(xaml + "Key") == "Settings.PasswordBox");
        using Stream tokens = assembly.GetManifestResourceStream("ThemeFixture.Tokens.xaml")!;
        XElement fontSize = XDocument.Load(tokens).Root!.Elements()
            .Single(element => (string?)element.Attribute(xaml + "Key") == "FontSize.Body");
        var markup = new XElement(presentation + "ResourceDictionary",
            new XAttribute(XNamespace.Xmlns + "x", xaml.NamespaceName), fontSize, style);
        ResourceDictionary resources = LoadTheme();
        resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(markup.ToString()));
        var box = new PasswordBox
        {
            Password = "synthetic-fixture", Width = 300,
            Style = (Style)resources["Settings.PasswordBox"],
        };
        var root = new Border { Width = 340, Height = 70, Child = box, Resources = resources };
        foreach (string scenario in Scenarios)
        {
            Apply(scenario, resources);
            Layout(root);
            Assert.NotNull(box.Template.FindName("PART_ContentHost", box));
            Assert.Same(resources["Text.SelectionBackground"], box.SelectionBrush);
            Assert.Same(resources["Text.SelectionForeground"], box.SelectionTextBrush);
            Assert.Equal(1d, box.SelectionOpacity);
            Color fill = Solid(box.SelectionBrush).Color;
            Color ink = Solid(box.SelectionTextBrush).Color;
            Assert.Equal(255, fill.A);
            Assert.Equal(255, ink.A);
            AssertContrast(ink, fill, $"Settings.PasswordBox {scenario}");
            Assert.True(ThemeContrast.Ratio(ToTheme(fill), ToTheme(Solid(box.Background).Color)) >= 3);
        }
        Assert.Null(PresentationSource.FromVisual(root));
    });

    // The scratch baseline runner links this very file and calls only this helper against
    // preserved pre-fix product DLLs. Before images therefore execute the original read site,
    // TextBox template and ThemeService, with identical fixture content and dimensions.
    internal static void RenderEvidence(string directory, string prefix)
    {
        foreach (string scenario in new[] { "glass", "glass-light", "high-contrast", "windows-dark", "windows-light" })
            Run(() =>
            {
                using Fixture fixture = CreateFixture(scenario, 2);
                BitmapSource image = Render(fixture.Root, 2);
                SaveEvidence(fixture, image, scenario, 2, prefix, directory);
            });
    }

    private sealed record Fixture(Border Root, ResourceDictionary Resources, TextBox Box,
        VideoLayerTimeline Timeline, Rect TextBar, Rect FrameBar, object Selection) : IDisposable
    {
        public void Dispose() => Selection.GetType()
            .GetMethod("ClearThreadSelection", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Selection, null);
    }

    private static void AssertSelectionRendererConfiguration()
    {
        Assert.True(AppContext.TryGetSwitch("Switch.System.Windows.Controls.Text.UseAdornerForTextboxSelectionRendering", out bool legacy));
        Assert.False(legacy, "The product startup configuration must enable SelectionTextBrush rendering.");
    }

    private static Fixture CreateFixture(string scenario, double scale)
    {
        ResourceDictionary resources = LoadTheme();
        Apply(scenario, resources);
        var box = new TextBox
        {
            Text = SelectedText + " · 선택 영역", Width = 620, Height = 44, FontSize = 16,
            HorizontalAlignment = HorizontalAlignment.Left,
            IsInactiveSelectionHighlightEnabled = true,
            Style = (Style)resources[typeof(TextBox)],
        };
        var timeline = new VideoLayerTimeline { Width = 620, HorizontalAlignment = HorizontalAlignment.Left };
        timeline.Initialize(10_000);
        var text = new TimedTextOverlay { Text = "Caption layer", StartMs = 500, EndMs = 9500 };
        var frame = new FrameEditLayer { Name = "Frame layer", StartMs = 500, EndMs = 9500 };
        timeline.SetLayers([text], [frame]);

        TextBlock Heading(string content) => new()
        {
            Text = content, FontSize = 16, Margin = new Thickness(0, 0, 0, 10),
            Foreground = (Brush)resources["Text.Primary"],
        };
        var content = new StackPanel { Margin = new Thickness(24) };
        content.Children.Add(Heading("텍스트 선택"));
        content.Children.Add(box);
        var timelineHeading = Heading("타임라인 레이어");
        timelineHeading.Margin = new Thickness(0, 24, 0, 10);
        content.Children.Add(timelineHeading);
        content.Children.Add(timeline);
        var root = new Border { Width = 680, Height = 270,
            Child = new AdornerDecorator { Child = content }, Resources = resources };
        root.SetResourceReference(Border.BackgroundProperty, "Surface.Base");
        VisualTreeHelper.SetRootDpi(root, new DpiScale(scale, scale));
        Layout(root);
        // Invoke the product's normal palette observer after the real resource scope exists.
        timeline.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, timeline));
        timeline.SelectLayer(text.Id);
        Rect textBar = timeline.SelectedBarBounds;
        timeline.SelectLayer(frame.Id);
        Rect frameBar = timeline.SelectedBarBounds;
        timeline.SelectLayer(null);
        box.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, box));
        box.Select(0, SelectedText.Length);
        // TextBoxLine uses IsSelectionActive to draw the selected glyph run. Set only its
        // readonly state in this isolated fixture; no keyboard focus or OS input is acquired.
        FieldInfo keyField = typeof(TextBoxBase).GetField("IsSelectionActivePropertyKey", BindingFlags.Static | BindingFlags.NonPublic)
            ?? typeof(Selector).GetField("IsSelectionActivePropertyKey", BindingFlags.Static | BindingFlags.NonPublic)!;
        box.SetValue((DependencyPropertyKey)keyField.GetValue(null)!, true);
        // WPF's selection renderer also needs its thread-local selection and caret owner.
        // Prepare those internal states without Keyboard.Focus, a Window, or native input.
        // The actual runtime switch chooses the original adorner or current glyph renderer.
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        object editor = typeof(TextBoxBase).GetField("_textEditor", instance)!.GetValue(box)!;
        object selection = editor.GetType().GetField("_selection", instance)!.GetValue(editor)!;
        selection.GetType().GetMethod("SetThreadSelection", instance)!.Invoke(selection, null);
        MethodInfo ensureCaret = selection.GetType().GetMethod("EnsureCaret", instance)!;
        ensureCaret.Invoke(selection, [false, true, Enum.Parse(ensureCaret.GetParameters()[2].ParameterType, "None")]);
        selection.GetType().GetMethod("Highlight", instance)!.Invoke(selection, null);
        Layout(root);
        return new(root, resources, box, timeline, textBar, frameBar, selection);
    }

    private static void AssertSelection(Fixture fixture, BitmapSource image, double scale)
    {
        Assert.True(fixture.Box.IsSelectionActive);
        Assert.Equal(SelectedText, fixture.Box.SelectedText);
        Assert.Same(fixture.Resources["Text.SelectionBackground"], fixture.Box.SelectionBrush);
        Assert.Same(fixture.Resources["Text.SelectionForeground"], fixture.Box.SelectionTextBrush);
        Assert.Equal(1d, fixture.Box.SelectionOpacity);
        Color fill = Solid(fixture.Box.SelectionBrush).Color;
        Color ink = Solid(fixture.Box.SelectionTextBrush).Color;
        Assert.Equal(255, fill.A);
        Assert.Equal(255, ink.A);
        AssertContrast(ink, fill, "selected text");
        Assert.True(ThemeContrast.Ratio(ToTheme(fill), ToTheme(Solid(fixture.Box.Background).Color)) >= 3,
            "The selected background must remain distinguishable from the input background.");
        Rect bounds = SelectionBounds(fixture);
        Assert.True(bounds.Width > 20 && bounds.Height > 10);
        var selectedGlyphs = new List<GlyphRunDrawing>();
        foreach (Visual visual in Descendants(fixture.Box))
        {
            if (VisualTreeHelper.GetDrawing(visual) is not DrawingGroup drawing) continue;
            foreach (GlyphRunDrawing glyph in Drawings(drawing).OfType<GlyphRunDrawing>())
            {
                Rect glyphBounds = visual.TransformToAncestor(fixture.Root).TransformBounds(glyph.Bounds);
                if (glyphBounds.Left >= bounds.Left - 1 && glyphBounds.Right <= bounds.Right + 1 &&
                    glyphBounds.IntersectsWith(bounds)) selectedGlyphs.Add(glyph);
            }
        }
        Assert.NotEmpty(selectedGlyphs);
        Assert.All(selectedGlyphs, glyph => Assert.Equal(ink, Solid(glyph.ForegroundBrush).Color));
        Assert.True(HasSelectionFill(image, bounds, scale, fill),
            "The actual TextBox selection background is missing from the rendered image.");
        Assert.True(CountPixels(image, bounds, scale, ink) >= 8 * scale * scale,
            "The actual selected glyphs are missing from the rendered image.");
    }

    private static bool HasSelectionFill(BitmapSource image, Rect bounds, double scale, Color fill) =>
        CountPixels(image, bounds, scale, fill) >= bounds.Width * bounds.Height * scale * scale * 0.45;

    private static IEnumerable<Visual> Descendants(Visual visual)
    {
        yield return visual;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(visual); index++)
            if (VisualTreeHelper.GetChild(visual, index) is Visual child)
                foreach (Visual descendant in Descendants(child)) yield return descendant;
    }

    private static void AssertTimeline(Fixture fixture, BitmapSource image, double scale)
    {
        Color token = Solid((Brush)fixture.Resources["Timeline.LayerText"]).Color;
        DrawingGroup drawing = VisualTreeHelper.GetDrawing(fixture.Timeline)!;
        foreach ((Rect bar, string label) in new[] { (fixture.TextBar, "caption"), (fixture.FrameBar, "frame") })
        {
            GeometryDrawing fill = Assert.Single(Drawings(drawing).OfType<GeometryDrawing>(),
                item => item.Brush is not null && item.Geometry.Bounds == bar);
            GlyphRunDrawing[] glyphs = Drawings(drawing).OfType<GlyphRunDrawing>()
                .Where(item => item.Bounds.Left >= bar.Left + 5 && item.Bounds.IntersectsWith(bar)).ToArray();
            Assert.NotEmpty(glyphs);
            foreach (GlyphRunDrawing glyph in glyphs)
            {
                Color actualInk = Solid(glyph.ForegroundBrush).Color;
                Assert.Equal(token, actualInk);
                AssertContrast(actualInk, Solid(fill.Brush).Color, $"actual {label} timeline drawing");
            }
            Rect glyphBounds = glyphs.Select(item => item.Bounds).Aggregate(Rect.Union);
            Rect rootBounds = fixture.Timeline.TransformToAncestor(fixture.Root).TransformBounds(glyphBounds);
            Assert.True(CountPixels(image, rootBounds, scale, token) >= 3 * scale * scale,
                $"The actual {label} timeline glyph raster is missing.");
        }
    }

    private static Rect SelectionBounds(Fixture fixture)
    {
        Rect first = fixture.Box.GetRectFromCharacterIndex(0);
        Rect last = fixture.Box.GetRectFromCharacterIndex(SelectedText.Length - 1, trailingEdge: true);
        return fixture.Box.TransformToAncestor(fixture.Root).TransformBounds(
            new Rect(first.Left, first.Top, last.Right - first.Left, Math.Max(first.Height, last.Height)));
    }

    private static int CountPixels(BitmapSource image, Rect bounds, double scale, Color color)
    {
        int left = Math.Max(0, (int)Math.Floor(bounds.Left * scale));
        int top = Math.Max(0, (int)Math.Floor(bounds.Top * scale));
        int right = Math.Min(image.PixelWidth, (int)Math.Ceiling(bounds.Right * scale));
        int bottom = Math.Min(image.PixelHeight, (int)Math.Ceiling(bounds.Bottom * scale));
        if (right <= left || bottom <= top) return 0;
        int stride = (right - left) * 4;
        var pixels = new byte[stride * (bottom - top)];
        image.CopyPixels(new Int32Rect(left, top, right - left, bottom - top), pixels, stride, 0);
        int count = 0;
        for (int offset = 0; offset < pixels.Length; offset += 4)
            if (pixels[offset + 3] >= 240 && Math.Abs(pixels[offset] - color.B) <= 24 &&
                Math.Abs(pixels[offset + 1] - color.G) <= 24 && Math.Abs(pixels[offset + 2] - color.R) <= 24)
                count++;
        return count;
    }

    private static IEnumerable<Drawing> Drawings(Drawing drawing)
    {
        yield return drawing;
        if (drawing is DrawingGroup group)
            foreach (Drawing child in group.Children)
                foreach (Drawing descendant in Drawings(child)) yield return descendant;
    }

    private static void Apply(string scenario, ResourceDictionary resources)
    {
        bool systemContrast = scenario.StartsWith("windows-", StringComparison.Ordinal);
        bool light = scenario.EndsWith("light", StringComparison.Ordinal);
        ThemeService.SystemHighContrastOverrideForTest = systemContrast;
        ThemeService.SystemColorsOverrideForTest = role => (light ? LightContrast : DarkContrast)[role];
        ThemeService.SystemLightThemeReaderForTest = () => light;
        if (scenario is "workspace-gallery" or "workspace-video")
        {
            WorkspaceTheme.Apply(resources,
                scenario == "workspace-gallery" ? WorkspaceRole.Gallery : WorkspaceRole.VideoEditor,
                AppTheme.Workspace, systemHighContrast: false, accent: AppAccent.Red);
            return;
        }
        AppTheme theme = scenario switch
        {
            "midnight" => AppTheme.Midnight,
            "daylight" => AppTheme.Daylight,
            "workspace" => AppTheme.Workspace,
            "glass-light" => AppTheme.GlassLight,
            "high-contrast" => AppTheme.HighContrast,
            "system-dark" or "system-light" => AppTheme.System,
            _ => AppTheme.Glass,
        };
        ThemeService.ApplyResources(theme, resources, systemHighContrast: systemContrast, accent: AppAccent.Red);
    }

    private static ResourceDictionary LoadTheme()
    {
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        _ = new FrameworkElement();
        var resources = new ResourceDictionary();
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
            resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml", UriKind.Absolute),
            });
        return resources;
    }

    private static void Layout(FrameworkElement root)
    {
        root.Measure(new Size(root.Width, root.Height));
        root.Arrange(new Rect(0, 0, root.Width, root.Height));
        root.UpdateLayout();
        root.Dispatcher.Invoke(DispatcherPriority.ContextIdle, new Action(root.UpdateLayout));
    }

    private static BitmapSource Render(FrameworkElement root, double scale)
    {
        Layout(root);
        var bitmap = new RenderTargetBitmap((int)(root.ActualWidth * scale), (int)(root.ActualHeight * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(root);
        bitmap.Freeze();
        return bitmap;
    }

    private static void SaveEvidence(Fixture fixture, BitmapSource image, string scenario, double scale, string prefix, string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        string stem = $"{prefix}-{scenario}-{scale * 100:0}";
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using (FileStream output = File.Create(Path.Combine(directory, stem + ".png"))) encoder.Save(output);
        GlyphRunDrawing[] layerGlyphs = Drawings(VisualTreeHelper.GetDrawing(fixture.Timeline)!)
            .OfType<GlyphRunDrawing>().Where(item => item.Bounds.Left >= VideoLayerTimeline.LabelWidth).ToArray();
        var evidence = new
        {
            scenario, scale, productAssembly = typeof(VideoLayerTimeline).Assembly.Location,
            selectionActive = fixture.Box.IsSelectionActive,
            selectionBackground = Solid(fixture.Box.SelectionBrush).Color.ToString(),
            selectionForeground = (fixture.Box.SelectionTextBrush as SolidColorBrush)?.Color.ToString(),
            selectionOpacity = fixture.Box.SelectionOpacity,
            timelineDrawnForeground = layerGlyphs.Select(glyph => Solid(glyph.ForegroundBrush).Color.ToString()).Distinct().ToArray(),
            timelineTextFill = Solid((Brush)fixture.Resources["Timeline.TextLayer"]).Color.ToString(),
            timelineFrameFill = Solid((Brush)fixture.Resources["Timeline.FrameLayer"]).Color.ToString(),
        };
        File.WriteAllText(Path.Combine(directory, stem + ".json"), JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static SolidColorBrush Solid(Brush brush) => (SolidColorBrush)brush;
    private static ThemeColor ToTheme(Color color) => new(color.A, color.R, color.G, color.B);
    private static void AssertContrast(Color ink, Color fill, string label)
    {
        double ratio = ThemeContrast.Ratio(ToTheme(ink), ToTheme(fill));
        Assert.True(ratio >= 4.5, $"{label}: {ratio:F4}:1, expected at least 4.5:1");
    }

    private static void Run(Action action) => StaTestHost.Run(() =>
    {
        bool? contrast = ThemeService.SystemHighContrastOverrideForTest;
        Func<SystemColorRole, Color>? colors = ThemeService.SystemColorsOverrideForTest;
        Func<bool>? light = ThemeService.SystemLightThemeReaderForTest;
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        try { action(); }
        finally
        {
            ThemeService.SystemHighContrastOverrideForTest = contrast;
            ThemeService.SystemColorsOverrideForTest = colors;
            ThemeService.SystemLightThemeReaderForTest = light;
            FluidMotion.AnimationsEnabledOverrideForTest = motion;
        }
    });
}
