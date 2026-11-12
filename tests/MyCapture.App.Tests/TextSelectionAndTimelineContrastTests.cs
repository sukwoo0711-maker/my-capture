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
        // FontSize.Body is an x:Static of the type scale, whose prefix the copied element no longer carries.
        var markup = new XElement(presentation + "ResourceDictionary",
            new XAttribute(XNamespace.Xmlns + "x", xaml.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "themes", "clr-namespace:MyCapture.App.Themes;assembly=MyCapture"), fontSize, style);
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

    // Over a layer bar the playhead and the pointer line alone kept 1.2 to 1.8:1 (2026-11-06
    // aesthetic review). Each now has a Surface.Base edge, one DIP on each side, drawn first.
    [Theory]
    [MemberData(nameof(ThemeAndScale))]
    public void PlayheadAndPointerLinesKeepASurfaceHaloOverLayerBars(string scenario, double scale) => Run(() =>
    {
        using Fixture fixture = CreateFixture(scenario, scale);
        VideoLayerTimeline timeline = fixture.Timeline;
        timeline.SetPlayhead(3000);
        timeline.SetHover(6000);
        Layout(fixture.Root);
        Drawing[] drawn = Drawings(VisualTreeHelper.GetDrawing(timeline)!).ToArray();
        Color halo = Solid((Brush)fixture.Resources["Surface.Base"]).Color;
        GeometryDrawing textFill = Assert.Single(drawn.OfType<GeometryDrawing>(),
            item => item.Brush is not null && item.Geometry.Bounds == fixture.TextBar);
        ThemeColor bar = ToTheme(Solid(textFill.Brush).Color);
        var measured = new List<object>();
        foreach ((string name, string token, double thickness) in new[]
        {
            ("playhead", "Timeline.Playhead", 2d), ("pointer line", "Text.Secondary", 1d),
        })
        {
            Color ink = Solid((Brush)fixture.Resources[token]).Color;
            int line = Array.FindIndex(drawn, item => IsFullHeightLine(item, ink, thickness, timeline.ActualHeight));
            Assert.True(line >= 0, $"{scenario}: the {name} is missing.");
            double x = ((LineGeometry)((GeometryDrawing)drawn[line]).Geometry).StartPoint.X;
            Assert.True(x > fixture.TextBar.Left && x < fixture.TextBar.Right, $"{scenario}: the {name} must cross the text bar.");
            int edge = Array.FindIndex(drawn, item =>
                IsFullHeightLine(item, halo, thickness + (2 * TimelineVisuals.HaloWidth), timeline.ActualHeight)
                && ((LineGeometry)((GeometryDrawing)item).Geometry).StartPoint.X == x);
            Assert.True(edge >= 0 && edge < line, $"{scenario}: the {name} needs its Surface.Base halo drawn under it.");
            ThemeColor edgeOverBar = ThemeContrast.Composite(ToTheme(halo), bar);
            double ratio = ThemeContrast.Ratio(ToTheme(ink), edgeOverBar);
            measured.Add(new
            {
                line = name, ink = ink.ToString(), halo = halo.ToString(), bar = Solid(textFill.Brush).Color.ToString(),
                lineAgainstBar = Math.Round(ThemeContrast.Ratio(ToTheme(ink), bar), 2),
                lineAgainstHalo = Math.Round(ratio, 2),
                haloAgainstBar = Math.Round(ThemeContrast.Ratio(edgeOverBar, bar), 2),
            });
            Assert.True(ratio >= 3,
                $"{scenario}: the {name} keeps {ratio:F2}:1 against its halo over the text bar, expected at least 3:1.");
        }
        if (Environment.GetEnvironmentVariable("MYCAPTURE_FIX_EVIDENCE") is { Length: > 0 } requested)
        {
            string directory = MyCapture.App.Diagnostics.DiagnosticOutputPaths.Create(requested);
            File.WriteAllText(MyCapture.App.Diagnostics.DiagnosticOutputPaths.Child(directory, $"halo-{scenario}-{scale * 100:0}.json"),
                JsonSerializer.Serialize(new { scenario, scale, lines = measured }, new JsonSerializerOptions { WriteIndented = true }));
        }
    });

    // The row headers read "T  텍스트" and "F  프레임", and the letters read as a word of their own
    // (2026-11-06 aesthetic review). Each header now starts with the icon the layer list draws
    // beside the same layer: the 20x20 grid glyph at 16 DIP with the grid stroke, in Text.Secondary.
    [Theory]
    [MemberData(nameof(ThemeAndScale))]
    public void RowHeadersDrawTheLayerListIconsBeforeTheirNames(string scenario, double scale) => Run(() =>
    {
        using var language = MyCapture.Core.Localization.UiText.UseLanguage("ko-KR");
        using Fixture fixture = CreateFixture(scenario, scale);
        VideoLayerTimeline timeline = fixture.Timeline;
        timeline.InvalidateVisual();
        Layout(fixture.Root);
        DrawingGroup root = VisualTreeHelper.GetDrawing(timeline)!;
        Color ink = Solid((Brush)fixture.Resources["Text.Secondary"]).Color;
        foreach ((string key, string name, Rect bar) in new[]
        {
            ("Icon.Text", "텍스트", fixture.TextBar), ("Icon.Image", "프레임", fixture.FrameBar),
        })
        {
            string glyph = ((Geometry)fixture.Resources[key]).ToString(System.Globalization.CultureInfo.InvariantCulture);
            DrawingGroup placed = Assert.Single(Drawings(root).OfType<DrawingGroup>(), group =>
                group.Children.Count == 1 && group.Children[0] is GeometryDrawing { Brush: null, Pen: { } pen } icon
                && icon.Geometry.ToString(System.Globalization.CultureInfo.InvariantCulture) == glyph
                && pen.Brush is SolidColorBrush stroke && stroke.Color == ink
                && Math.Abs(pen.Thickness - SymbolIcon.GridStrokeThickness) < 0.001
                && pen.StartLineCap == PenLineCap.Round && pen.LineJoin == PenLineJoin.Round);
            Matrix place = placed.Transform.Value;
            double size = SymbolIcon.InlineSize;
            Assert.Equal(size / SymbolIcon.GridSize, place.M11, 3);
            Assert.Equal(size / SymbolIcon.GridSize, place.M22, 3);
            Assert.Equal(8, place.OffsetX, 3);
            Assert.True(Math.Abs(place.OffsetY + (size / 2) - (bar.Top + (bar.Height / 2))) < 0.01,
                $"{scenario}: the {key} icon is not centred on its row.");
            var label = Assert.Single(HeaderNames(root), header => header.Text == name);
            Assert.True(label.Left >= 8 + size + 4, $"{scenario}: the {name} name starts at {label.Left} DIP, over its icon.");
        }
        Assert.Equal(2, HeaderNames(root).Length);
    });

    // With the icon in front, a numbered row name still fits the 82 DIP header untrimmed. The
    // fixture keeps its theme on the root element, so the timeline draws with its system
    // fallback (Segoe UI, Hangul from Malgun Gothic); the app draws with the bundled Pretendard,
    // which is narrower in Hangul and wider in "Frame". Both have to fit.
    [Theory]
    [InlineData("ko-KR", "텍스트 12", "프레임 12")]
    [InlineData("en-US", "Text 12", "Frame 12")]
    public void NumberedRowNamesFitBesideTheirIcons(string culture, string lastText, string lastFrame) => Run(() =>
    {
        using var language = MyCapture.Core.Localization.UiText.UseLanguage(culture);
        var product = new Typeface(ThemeFonts.Ui, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        foreach (string name in new[] { lastText, lastFrame })
        {
            var formatted = new FormattedText(name, System.Globalization.CultureInfo.GetCultureInfo(culture),
                FlowDirection.LeftToRight, product, TypeScale.Caption, Brushes.Black, 1);
            Assert.True(formatted.Width <= VideoLayerTimeline.RowNameMaxWidth,
                $"{name} needs {formatted.Width:F2} DIP in the app's font, past the {VideoLayerTimeline.RowNameMaxWidth} DIP beside the icon.");
        }

        using Fixture fixture = CreateFixture("glass", 1);
        VideoLayerTimeline timeline = fixture.Timeline;
        timeline.SetLayers(
            Enumerable.Range(0, 12).Select(index => new TimedTextOverlay { Text = $"Caption {index}", StartMs = 500, EndMs = 9500 }).ToArray(),
            Enumerable.Range(0, 12).Select(index => new FrameEditLayer { Name = $"Frame {index}", StartMs = 500, EndMs = 9500 }).ToArray());
        Layout(fixture.Root);
        var headers = HeaderNames(VisualTreeHelper.GetDrawing(timeline)!);
        Assert.Equal(24, headers.Length);
        Assert.Contains(headers, header => header.Text == lastText);
        Assert.Contains(headers, header => header.Text == lastFrame);
        Assert.All(headers, header =>
        {
            Assert.DoesNotContain('…', header.Text);
            Assert.True(header.Right <= VideoLayerTimeline.LabelWidth - 2,
                $"{header.Text} ends at {header.Right} DIP, past the {VideoLayerTimeline.LabelWidth} DIP header.");
        });
    });

    /// <summary>
    /// The row names as drawn left of the tracks, one per baseline. A name may draw as several
    /// glyph runs when a fallback font takes part of it, so the runs of a row are joined.
    /// </summary>
    private static (string Text, double Left, double Right)[] HeaderNames(DrawingGroup drawing) =>
        Drawings(drawing).OfType<GlyphRunDrawing>()
            .Where(run => run.GlyphRun.BaselineOrigin.X < VideoLayerTimeline.LabelWidth)
            .GroupBy(run => Math.Round(run.GlyphRun.BaselineOrigin.Y, 1))
            .Select(row => row.OrderBy(run => run.GlyphRun.BaselineOrigin.X).ToArray())
            .Select(row => (string.Concat(row.Select(run => new string(run.GlyphRun.Characters.ToArray()))),
                row[0].GlyphRun.BaselineOrigin.X, row.Max(run => run.Bounds.Right)))
            .ToArray();

    private static bool IsFullHeightLine(Drawing drawing, Color color, double thickness, double height) =>
        drawing is GeometryDrawing { Geometry: LineGeometry line, Pen: { Brush: SolidColorBrush brush } pen }
        && brush.Color == color && Math.Abs(pen.Thickness - thickness) < 0.001
        && Math.Abs(line.StartPoint.X - line.EndPoint.X) < 0.001
        && Math.Abs(line.StartPoint.Y) < 0.001 && Math.Abs(line.EndPoint.Y - height) < 0.001;

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
