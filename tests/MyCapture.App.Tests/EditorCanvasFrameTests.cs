using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using Xunit;
using static MyCapture.App.Tests.EditorKeysTests;

namespace MyCapture.App.Tests;

/// <summary>
/// The editor's canvas frame, step-number preview and crop size fields draw what they declare:
/// rounded frame corners, the badge the next click adds, and one unit per value.
/// </summary>
public sealed class EditorCanvasFrameTests
{
    [Fact]
    public void CanvasCornersAreDrawnOverTheCanvasInsteadOfClippingItAtEverySize() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create(beforeShow: UseShippedTheme);
        var viewport = Field<Grid>(host.Editor, "_viewport");
        var frame = Assert.IsType<Border>(VisualTreeHelper.GetParent(viewport));
        Assert.Equal(new CornerRadius(8), frame.CornerRadius);
        CanvasFrameCorners corners = Assert.Single(viewport.Children.OfType<CanvasFrameCorners>());
        // Above everything the canvas holds, and never in the pointer's way.
        Assert.Equal(int.MaxValue, Panel.GetZIndex(corners));
        Assert.False(corners.IsHitTestVisible);
        // The outline's centre line turns at radius 8; its inside edge half a DIP tighter.
        Assert.Equal(7.5, corners.InsideRadius);

        // Along a corner's diagonal, with the corner centre 7.5 DIP in from both sides: the
        // outline's outside edge (radius 8.5) crosses at 1.49 DIP from the canvas's corner and its
        // inside edge (radius 7.5) at 2.20, where the canvas begins to show.
        const double behindTheFrame = 0.5, onTheOutline = 1.85, onTheCanvas = 2.6;
        foreach (double width in new[] { 1000d, 760d, 1240d })
        {
            host.Window.Width = width;
            host.Window.UpdateLayout();
            // A clip that is not an axis-aligned rectangle makes WPF draw the whole canvas through
            // an intermediate surface as large as the canvas: about 30 MB for a 4K editor.
            Assert.Null(viewport.Clip);
            Size size = viewport.RenderSize;
            Assert.Equal(size, corners.RenderSize);
            Geometry outside = Assert.IsAssignableFrom<Geometry>(corners.OutsideCorners);
            Geometry arcs = Assert.IsAssignableFrom<Geometry>(corners.OutlineArcs);

            foreach ((double x, double y, double dx, double dy) in new[]
            {
                (0d, 0d, 1d, 1d), (size.Width, 0d, -1d, 1d), (0d, size.Height, 1d, -1d), (size.Width, size.Height, -1d, -1d),
            })
            {
                Point At(double along) => new(x + dx * along, y + dy * along);
                string where = $"width {width}, corner ({x},{y})";
                Assert.True(outside.FillContains(At(behindTheFrame)), where);
                Assert.False(arcs.FillContains(At(behindTheFrame)), where);
                Assert.True(outside.FillContains(At(onTheOutline)), where);
                Assert.True(arcs.FillContains(At(onTheOutline)), where);
                Assert.False(outside.FillContains(At(onTheCanvas)), where);
                Assert.False(arcs.FillContains(At(onTheCanvas)), where);
            }

            // The sides and the middle are the canvas's: the outline's straight runs lie outside it.
            foreach (Point canvas in new[] { new Point(size.Width / 2, 0.5), new Point(0.5, size.Height / 2), new Point(size.Width / 2, size.Height / 2) })
            {
                Assert.False(outside.FillContains(canvas), $"width {width}, {canvas}");
                Assert.False(arcs.FillContains(canvas), $"width {width}, {canvas}");
            }
        }
    });

    [Fact]
    public void FrameCornersShowTheWindowBehindTheFrameNotTheCanvas() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create(beforeShow: UseShippedTheme);
        // Crop mode adds the scrim over the whole canvas: the corners stay rounded under it too.
        foreach (bool crop in new[] { false, true })
        {
            if (crop) Assert.True(host.Editor.HandleShortcut(Key.X, ModifierKeys.None));
            host.Window.UpdateLayout();
            var frame = Assert.IsType<Border>(VisualTreeHelper.GetParent(Field<Grid>(host.Editor, "_viewport")));
            Point origin = frame.TranslatePoint(new Point(0, 0), host.Window);
            int left = (int)Math.Round(origin.X), top = (int)Math.Round(origin.Y);
            int right = left + (int)Math.Round(frame.ActualWidth) - 1, bottom = top + (int)Math.Round(frame.ActualHeight) - 1;
            Pixels pixels = Pixels.Render(host.Window);

            // The body's own surface, just outside the frame's left edge.
            Color behind = pixels[left - 4, top + 40];
            // One pixel in from each corner of the frame's box lies well outside its rounded
            // outline (about 1.4 DIP), where the body shows through.
            foreach ((int x, int y) in new[] { (left + 1, top + 1), (right - 1, top + 1), (left + 1, bottom - 1), (right - 1, bottom - 1) })
            {
                AssertClose(behind, pixels[x, y], $"crop={crop} corner ({x},{y})");
            }

            // The canvas itself still fills the frame away from the corners.
            Color canvas = pixels[left + 4, top + 40];
            Assert.NotEqual(behind, canvas);

            // The outline's arcs show over the canvas: two pixels in from each corner of the
            // frame's box, about half the pixel lies on the arc and the rest outside the outline.
            foreach ((int x, int y) in new[] { (left + 2, top + 2), (right - 2, top + 2), (left + 2, bottom - 2), (right - 2, bottom - 2) })
            {
                Color arc = pixels[x, y];
                Assert.True(Difference(behind, arc) > 8, $"crop={crop} arc ({x},{y}): {arc} is the surface behind the frame {behind}");
                Assert.True(Difference(canvas, arc) > 8, $"crop={crop} arc ({x},{y}): {arc} is the canvas {canvas}");
            }
        }
    });

    [Fact]
    public void StepPreviewDrawsTheBadgeTheNextClickAdds() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create(beforeShow: UseShippedTheme);
        AnnotationEditorControl editor = host.Editor;
        AnnotationEditorController controller = Controller(editor);
        Assert.True(Press(editor, Key.N, ime: false));
        controller.HitTolerance = 1;
        controller.PointerDown(new PointD(80, 80));
        controller.PointerUp(new PointD(80, 80));
        TextAnnotation placed = Assert.Single(controller.Document.Items.OfType<TextAnnotation>(), text => text.IsStepNumber);
        Assert.Equal("1", placed.Text);
        Assert.True(controller.TrySetNextStepNumber(1));
        typeof(AnnotationEditorControl).GetMethod("UpdateStepPreview",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(editor, null);
        host.Window.UpdateLayout();

        var preview = Field<Border>(editor, "_stepPreview");
        // The canvas paints a badge's plate square.
        Assert.Equal(new CornerRadius(0), preview.CornerRadius);

        // Same font, weight and fit as the placed badge: its "1" scaled to the preview's size.
        Rect canvasInk = TextAnnotationLayout.Format(placed, 1).BuildGeometry(default).Bounds;
        double scale = preview.Width / placed.Rect.Width;
        var number = Field<Path>(editor, "_stepPreviewNumber");
        Rect previewInk = number.Data.Bounds;
        Assert.InRange(previewInk.Width, canvasInk.Width * scale - 0.25, canvasInk.Width * scale + 0.25);
        Assert.InRange(previewInk.Height, canvasInk.Height * scale - 0.25, canvasInk.Height * scale + 0.25);

        // The outline sits under the number, as the canvas strokes it before filling the glyphs.
        var outline = Field<Path>(editor, "_stepPreviewOutline");
        var layers = Assert.IsType<Grid>(preview.Child);
        Assert.Equal(new UIElement[] { outline, number }, layers.Children.Cast<UIElement>().ToArray());
        Assert.Null(outline.Fill);
        Assert.NotNull(outline.Stroke);
        Assert.Equal(Colors.Transparent, Assert.IsType<SolidColorBrush>(number.Stroke).Color);
        Assert.Same(number.Data, outline.Data);
        // Equal measured sizes: the two layers centre on the same point.
        Assert.Equal(outline.StrokeThickness, number.StrokeThickness);
        Assert.Equal(outline.DesiredSize, number.DesiredSize);
    });

    [Theory]
    [InlineData("ko-KR", "너비", "높이")]
    [InlineData("en-US", "Width", "Height")]
    public void CropSizeFieldsNameTheValueAndShowTheUnitOnce(string language, string width, string height) => StaTestHost.Run(() =>
    {
        using var scope = UiText.UseLanguage(language);
        using EditorWindowHost host = EditorWindowHost.Create(beforeShow: UseShippedTheme);
        Assert.True(host.Editor.HandleShortcut(Key.X, ModifierKeys.None));
        host.Window.UpdateLayout();

        var section = Field<FrameworkElement>(host.Editor, "_cropSection");
        string[] texts = Descendants(section).OfType<TextBlock>().Where(text => text.IsVisible).Select(text => text.Text).ToArray();
        Assert.Contains(width, texts);
        Assert.Contains(height, texts);
        // Like the stroke width and badge size fields: the unit is the field's suffix only.
        Assert.Equal(2, texts.Count(text => text == "px"));
        Assert.DoesNotContain(texts, text => text.Contains("(px)", StringComparison.Ordinal));
    });

    private static void UseShippedTheme(Window window)
    {
        window.Resources = SystemHighContrastThemeTests.LoadTheme();
        window.SetResourceReference(Control.FontFamilyProperty, "Font.Ui");
        MyCapture.App.Themes.ThemeFonts.ApplyText(window);
    }

    private static void AssertClose(Color expected, Color actual, string where) =>
        Assert.True(Difference(expected, actual) <= 2, $"{where}: expected {expected}, found {actual}");

    private static int Difference(Color a, Color b) =>
        Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));

    /// <summary>The window's client content rendered offscreen at 96 DPI.</summary>
    private sealed class Pixels
    {
        private readonly byte[] _bgra;
        private readonly int _width;

        private Pixels(byte[] bgra, int width)
        {
            _bgra = bgra;
            _width = width;
        }

        public Color this[int x, int y]
        {
            get
            {
                int offset = (y * _width + x) * 4;
                return Color.FromRgb(_bgra[offset + 2], _bgra[offset + 1], _bgra[offset]);
            }
        }

        public static Pixels Render(Window window)
        {
            int width = (int)Math.Ceiling(window.ActualWidth), height = (int)Math.Ceiling(window.ActualHeight);
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            byte[] bgra = new byte[width * height * 4];
            bitmap.CopyPixels(bgra, width * 4, 0);
            return new Pixels(bgra, width);
        }
    }
}
