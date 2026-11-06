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
    public void CanvasIsClippedToTheFramesRoundedInsideEdgeAtEverySize() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create(beforeShow: UseShippedTheme);
        var viewport = Field<Grid>(host.Editor, "_viewport");
        var frame = Assert.IsType<Border>(VisualTreeHelper.GetParent(viewport));
        Assert.Equal(new CornerRadius(8), frame.CornerRadius);

        foreach (double width in new[] { 1000d, 760d, 1240d })
        {
            host.Window.Width = width;
            host.Window.UpdateLayout();
            var clip = Assert.IsType<RectangleGeometry>(viewport.Clip);
            Assert.Equal(new Rect(viewport.RenderSize), clip.Rect);
            // The outline's centre line turns at radius 8; its inside edge half a DIP tighter.
            Assert.Equal(7.5, clip.RadiusX);
            Assert.Equal(7.5, clip.RadiusY);
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
            Assert.NotEqual(behind, pixels[left + 4, top + 40]);
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

    private static void AssertClose(Color expected, Color actual, string where)
    {
        int difference = Math.Max(Math.Abs(expected.R - actual.R), Math.Max(Math.Abs(expected.G - actual.G), Math.Abs(expected.B - actual.B)));
        Assert.True(difference <= 2, $"{where}: expected {expected}, found {actual}");
    }

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
