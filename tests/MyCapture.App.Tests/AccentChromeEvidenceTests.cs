using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using MyCapture.App.Editing;
using MyCapture.App.Ocr;
using MyCapture.App.Recording;
using MyCapture.App.Themes;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Themes;
using MyCapture.Core.Undo;
using MyCapture.Ocr;
using MyCapture.Platform.Capture;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>Real product drawing surfaces and templates, without native windows or input.</summary>
public sealed class AccentChromeEvidenceTests
{
    [Fact]
    public Task SelectedSurfacesRenderOffscreen() => SettingsThemePreviewTests.RunIsolated(
        nameof(SelectedSurfacesRenderOffscreen), RenderSurfaces, typeof(AccentChromeEvidenceTests));

    private static void RenderSurfaces()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        bool? previous = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        try
        {
            foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml"),
                });
            app.Resources.MergedDictionaries.Add(LoadResources());
            foreach (AppTheme theme in Enum.GetValues<AppTheme>())
            foreach (double scale in new[] { 1d, 2d })
            {
                ThemeService.ApplyResources(theme, app.Resources, systemHighContrast: false, accent: AppAccent.Red);
                BitmapSource image = SampleImage();
                var bounds = new RectD(0, 0, 360, 180);
                var document = AnnotationDocument.CreateFor(360, 180);
                var rectangle = new RectangleAnnotation
                {
                    Rect = new RectD(60, 40, 220, 95), Stroke = ColorRgba.FromRgb(0xEF, 0x44, 0x44),
                    StrokeThickness = 3,
                };
                document.Add(rectangle);
                var controller = new AnnotationEditorController(document, new UndoStack()) { Tool = EditorTool.Select };
                controller.SetSelected(rectangle);
                var editor = new AnnotationEditorSurface(new FrozenFrame(image, bounds, null, 0), bounds,
                    controller, new AnnotationRenderer(new AnnotationImageStore())) { Width = 360, Height = 180 };
                Save(editor, "editor", theme, scale, 400, 220);
                AssertColor(app.Resources, "Text.Primary", Field<Brush>(editor, "_selectionBrush"));
                Assert.Equal(2d, Field<Pen>(editor, "_selectionOutlinePen").Thickness);
                Assert.Equal(2d, Field<Pen>(editor, "_handlePen").Thickness);
                Assert.False(Field<Pen>(editor, "_selectionOutlinePen").IsFrozen);
                Assert.Equal(ColorRgba.FromRgb(0xEF, 0x44, 0x44), rectangle.Stroke);
                // An existing selected rectangle must follow a live palette replacement.
                ThemeService.ApplyResources(AppTheme.GlassLight, app.Resources, systemHighContrast: false);
                AssertColor(app.Resources, "Text.Primary", Field<Pen>(editor, "_selectionOutlinePen").Brush);
                ThemeService.ApplyResources(theme, app.Resources, systemHighContrast: false);

                var tile = new ListBoxItem
                {
                    Width = 220, Height = 150, DataContext = new { IsSelected = true },
                    Template = (ControlTemplate)app.Resources["Evidence.TileTemplate"],
                    Content = new Image { Source = image, Margin = new Thickness(12) },
                };
                var toggle = new CheckBox
                {
                    Content = "새 캡처 저장", IsChecked = true,
                    Style = (Style)app.Resources["LibraryUx.SaveHereSwitch"],
                };
                var gallery = new StackPanel { Width = 360 };
                gallery.Children.Add(tile);
                gallery.Children.Add(toggle);
                Save(gallery, "library", theme, scale, 400, 240);
                var fill = Assert.IsType<Border>(toggle.Template.FindName("PART_SwitchFill", toggle));
                var thumb = Assert.IsType<System.Windows.Shapes.Ellipse>(toggle.Template.FindName("PART_SwitchThumbOn", toggle));
                AssertColor(app.Resources, "Text.Primary", fill.Background);
                AssertColor(app.Resources, "Surface.Raised", thumb.Fill);
                Assert.Equal(1d, fill.Opacity);
                AssertContrast(fill.Background, thumb.Fill, 4.5);
                var focus = Assert.IsType<Border>(toggle.Template.FindName("FocusRing", toggle));
                Assert.Equal(new Thickness(2), focus.BorderThickness);
                AssertColor(app.Resources, "Text.Primary", focus.BorderBrush);
                toggle.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));

                using var timeline = new TwoLineTimeline { Width = 640 };
                timeline.Initialize(12_000, 30);
                timeline.SetIn(2_000);
                timeline.SetOut(9_000);
                timeline.SetTrimMode(true);
                Save(timeline, "trim", theme, scale, 680, 300, () =>
                {
                    timeline.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                    timeline.FlushRenderForTest();
                });
                AssertColor(app.Resources, "Text.Primary", Field<Brush>(timeline, "_selectionInk"));
                AssertColor(app.Resources, "Surface.Selected", Field<Brush>(timeline, "_selectionFill"));
                AssertContrast(Field<Brush>(timeline, "_handleText"), Field<Brush>(timeline, "_handleFill"), 4.5);
                AssertContrast(Field<Brush>(timeline, "_selectionInk"), Field<Brush>(timeline, "_selectionFill"), 3);

                var ocr = new OcrImageOverlay { Width = 360, Height = 180 };
                ocr.SetImage(image);
                ocr.SetRegions([new OcrRect(60, 40, 220, 95)]);
                ocr.SetSelection(new OcrRect(60, 40, 220, 95));
                Save(ocr, "ocr", theme, scale, 400, 220);
                AssertColor(app.Resources, "Text.Primary", ocr.RegionBrush);
                AssertContrast(ocr.RegionBrush, (Brush)app.Resources["Surface.Raised"], 4.5);
            }
        }
        finally
        {
            FluidMotion.AnimationsEnabledOverrideForTest = previous;
            app.Shutdown();
        }
    }

    private static ResourceDictionary LoadResources()
    {
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var combined = new XElement(p + "ResourceDictionary");
        {
            const string name = "GalleryFixture.Window.xaml";
            using Stream stream = typeof(AccentChromeEvidenceTests).Assembly.GetManifestResourceStream(name)!;
            XElement root = XDocument.Load(stream).Root!;
            foreach (XAttribute attribute in root.Attributes().Where(a => a.IsNamespaceDeclaration))
            {
                string value = attribute.Value;
                if (value.StartsWith("clr-namespace:MyCapture.", StringComparison.Ordinal) && !value.Contains(";assembly=", StringComparison.Ordinal))
                    value += ";assembly=MyCapture";
                combined.SetAttributeValue(attribute.Name, value);
            }
            {
                foreach (string key in new[] { "LibraryUx.RestEdge", "LibraryUx.HoverWash", "LibraryUx.SaveHereSwitch" })
                    combined.Add(root.Descendants(p + "Style").Single(e => (string?)e.Attribute(x + "Key") == key));
                XElement row = root.Descendants(p + "Style").Single(e => (string?)e.Attribute(x + "Key") == "Gallery.TileRowList");
                XElement template = row.Descendants(p + "ControlTemplate").Single(e => (string?)e.Attribute("TargetType") == "ListBoxItem");
                template.SetAttributeValue(x + "Key", "Evidence.TileTemplate");
                combined.Add(template);
            }
        }
        return (ResourceDictionary)XamlReader.Parse(combined.ToString());
    }

    private static BitmapSource SampleImage()
    {
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 360, 180));
            dc.DrawRectangle(Brushes.Black, null, new Rect(180, 0, 180, 180));
            dc.DrawRectangle(Brushes.Gray, null, new Rect(0, 120, 360, 60));
        }
        var bitmap = new RenderTargetBitmap(360, 180, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static void Save(FrameworkElement content, string name, AppTheme theme, double scale,
        double width, double height, Action? prepared = null)
    {
        var root = new Border { Width = width, Height = height, Padding = new Thickness(20), Child = content };
        root.SetResourceReference(Border.BackgroundProperty, "Surface.Base");
        VisualTreeHelper.SetRootDpi(root, new DpiScale(scale, scale));
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
        // Loaded initializes the shipped switch's attached motion behavior without a Window.
        foreach (FrameworkElement element in Descendants(root).OfType<FrameworkElement>())
            element.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        prepared?.Invoke();
        root.UpdateLayout();
        root.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(root.UpdateLayout));
        Assert.Null(PresentationSource.FromVisual(root));
        Assert.True(content.ActualWidth > 0 && content.ActualHeight > 0);
        var bitmap = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(root);
        string? directory = Environment.GetEnvironmentVariable("MYCAPTURE_ACCENT_EVIDENCE");
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using FileStream stream = File.Create(Path.Combine(directory, $"{name}-{theme}-{scale * 100:0}.png"));
            encoder.Save(stream);
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (DependencyObject descendant in Descendants(child)) yield return descendant;
        }
    }

    private static T Field<T>(object value, string name) =>
        Assert.IsAssignableFrom<T>(value.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(value));

    private static void AssertColor(ResourceDictionary resources, string key, Brush actual) =>
        Assert.Equal(Assert.IsType<SolidColorBrush>(resources[key]).Color, Assert.IsType<SolidColorBrush>(actual).Color);

    private static void AssertContrast(Brush ink, Brush background, double minimum)
    {
        Color first = Assert.IsType<SolidColorBrush>(ink).Color;
        Color second = Assert.IsType<SolidColorBrush>(background).Color;
        double ratio = ThemeContrast.Ratio(ThemeColor.Argb(first.A, first.R, first.G, first.B),
            ThemeColor.Argb(second.A, second.R, second.G, second.B));
        Assert.True(ratio >= minimum, $"{first} on {second}: {ratio:F2}:1, expected {minimum}:1");
    }
}
