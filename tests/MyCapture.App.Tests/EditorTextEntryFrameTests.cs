using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using Xunit;
using static MyCapture.App.Tests.EditorKeysTests;

namespace MyCapture.App.Tests;

public sealed class EditorTextEntryFrameTests
{
    [Theory]
    [InlineData((byte)0, (byte)255)]
    [InlineData((byte)255, (byte)255)]
    [InlineData((byte)0, (byte)32)]
    [InlineData((byte)255, (byte)32)]
    public void LiveCaretHasAnAdornerLayerAndContrastsWithTheInputBackground(byte shade, byte alpha) => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create(Solid(640, 360, Picture));
        Invoke(host.Editor, "PlaceTextBox", new PointD(80, 80));
        Field<TextBox>(host.Editor, "_activeTextBox").Text = "입력 중";
        Invoke(host.Editor, "CommitActiveText");
        var annotation = Assert.IsType<TextAnnotation>(Assert.Single(Controller(host.Editor).Document.Items));
        annotation.Background = new ColorRgba(alpha, shade, shade, shade);
        Controller(host.Editor).SetSelected(annotation);
        Assert.True(host.Editor.HandleShortcut(Key.F2, ModifierKeys.None));
        var box = Field<TextBox>(host.Editor, "_activeTextBox");
        host.Window.UpdateLayout();
        var contentHost = Assert.IsType<ScrollViewer>(box.Template.FindName("PART_ContentHost", box));
        Assert.NotNull(System.Windows.Documents.AdornerLayer.GetAdornerLayer(contentHost));
        // The box is clear: the surface draws the caption's plate under it, as the committed caption has it.
        Assert.Equal(0, Assert.IsType<SolidColorBrush>(box.Background).Color.A);
        Assert.Equal(annotation.Background, Controller(host.Editor).LiveTextForPaint()?.Background);
        box.CaretIndex = box.Text.Length;
        host.Window.UpdateLayout();
        Rect caret = box.GetRectFromCharacterIndex(box.CaretIndex);
        Assert.False(caret.IsEmpty);

        // The caret is white or black inside a ring of the caption's outline colour, so one of the
        // two contrasts with whatever the plate and the picture make under it.
        Color core = Assert.IsType<SolidColorBrush>(box.CaretBrush).Color;
        Rectangle ring = CaretRing(box);
        Assert.Equal(Visibility.Visible, ring.Visibility);
        Color ringColor = Assert.IsType<SolidColorBrush>(ring.Fill).Color;
        ColorRgba outline = AnnotationRenderer.OutlineColor(annotation);
        Assert.Equal(Color.FromRgb(outline.R, outline.G, outline.B), ringColor);
        Assert.Equal(255, core.A);
        Assert.Equal(255, ringColor.A);
        Assert.True(Contrast(core, ringColor) >= 4.5, $"core {core} and ring {ringColor}");
        Color under = Over(annotation.Background, Color.FromRgb(Picture, Picture, Picture));
        double best = Math.Max(Contrast(core, under), Contrast(ringColor, under));
        Assert.True(best >= 4.5, $"The caret ({core} in {ringColor}) reaches only {best:0.00}:1 over {under}.");

        // The ring is the caret WPF draws, one DIP larger on every side, and lies under the glyphs.
        Rect bounds = ring.TransformToAncestor(box).TransformBounds(new Rect(ring.RenderSize));
        double width = SystemParameters.CaretWidth;
        double height = Math.Min(caret.Height, box.FontFamily.LineSpacing * box.FontSize);
        Assert.Equal(caret.X, (bounds.Left + bounds.Right) / 2, 3);
        Assert.Equal(width + 2, bounds.Width, 3);
        Assert.Equal(caret.Bottom + 1, bounds.Bottom, 3);
        Assert.Equal(height + 2, bounds.Height, 3);
        var presenter = Assert.IsType<ScrollContentPresenter>(contentHost.Template.FindName("PART_ScrollContentPresenter", contentHost));
        var layer = Assert.IsType<Canvas>(VisualTreeHelper.GetParent(ring));
        Assert.True(((Panel)layer.Parent).Children.IndexOf(layer) < ((Panel)layer.Parent).Children.IndexOf(presenter));
        Assert.Equal(SystemCaretBlinks(), ring.HasAnimatedProperties);

        // A selection hides the caret and its ring; the ring returns with the caret.
        box.Select(0, 2);
        host.Window.UpdateLayout();
        Assert.Equal(Visibility.Collapsed, ring.Visibility);
        box.Select(box.Text.Length, 0);
        host.Window.UpdateLayout();
        Assert.Equal(Visibility.Visible, ring.Visibility);
        Assert.Equal(annotation.Background, Assert.IsType<TextAnnotation>(Assert.Single(host.Editor.CreateCurrentImageSnapshot().Items)).Background);

        // The closed box leaves no blinking ring behind.
        Invoke(host.Editor, "CommitActiveText");
        Assert.False(ring.HasAnimatedProperties);
        Assert.Null(Field<Rectangle?>(host.Editor, "_caretOutline"));
    });

    [Theory]
    [InlineData(0xEF, 0x44, 0x44)]
    [InlineData(0xFF, 0xFF, 0xFF)]
    [InlineData(0x14, 0x14, 0x14)]
    [InlineData(0x1E, 0x3A, 0x8A)]
    [InlineData(0xFA, 0xCC, 0x15)]
    public void CaretColoursStandOutFromAnyPictureForEveryCaptionColour(byte red, byte green, byte blue)
    {
        var caption = new TextAnnotation { Foreground = ColorRgba.FromRgb(red, green, blue) };
        (SolidColorBrush core, SolidColorBrush ring) = AnnotationEditorControl.CaretBrushes(caption);
        ColorRgba outline = AnnotationRenderer.OutlineColor(caption);
        Assert.Equal(Color.FromRgb(outline.R, outline.G, outline.B), ring.Color);
        // Black and white: one of them reaches 4.58:1 against any colour.
        for (int shade = 0; shade <= 255; shade++)
        {
            Color under = Color.FromRgb((byte)shade, (byte)shade, (byte)shade);
            Assert.True(Math.Max(Contrast(core.Color, under), Contrast(ring.Color, under)) >= 4.5, $"{caption.Foreground} over {under}");
        }
    }

    [Fact]
    public void EmptyEntryHasRoomToTypeWithoutCreatingSavedBackground() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        Invoke(host.Editor, "PlaceTextBox", new PointD(80, 80));
        TextBox box = Field<TextBox>(host.Editor, "_activeTextBox");
        host.Window.UpdateLayout();
        // One em of the live font inside the box's chrome, whatever the image scale.
        Point inset = Field<Point>(host.Editor, "_liveTextInset");
        Assert.True(box.ActualWidth >= box.FontSize + 2 * inset.X - 0.01, $"{box.ActualWidth} wide for a {box.FontSize} DIP font.");
        Assert.True(box.ActualHeight >= box.FontSize + inset.Y + box.Padding.Bottom + box.BorderThickness.Bottom - 0.01,
            $"{box.ActualHeight} high for a {box.FontSize} DIP font.");
        Assert.True(new Rect(box.RenderSize).Contains(box.GetRectFromCharacterIndex(0)), "The caret shows inside the box.");
        Assert.Empty(host.Editor.CreateCurrentImageSnapshot().Items);
        AssertFrame(box);
        box.Text = "메모";
        host.Window.UpdateLayout();
        var surface = Field<AnnotationEditorSurface>(host.Editor, "_surface");
        Point origin = surface.ToSurfacePoint(new PointD(80, 80));
        Point glyph = box.TranslatePoint(box.GetRectFromCharacterIndex(0).TopLeft, surface);
        Assert.True((glyph - origin).Length <= 0.75);
        var before = Assert.IsType<TextAnnotation>(Assert.Single(host.Editor.CreateCurrentImageSnapshot().Items));
        Assert.Equal(ColorRgba.Transparent, before.Background);
        Invoke(host.Editor, "CommitActiveText");
        var saved = Assert.IsType<TextAnnotation>(Assert.Single(host.Editor.CreateCurrentImageSnapshot().Items));
        Assert.Equal(before.Rect, saved.Rect);
        Assert.Equal(before.Background, saved.Background);
    });

    [Theory]
    [InlineData(0, (byte)0)]
    [InlineData(0.05, (byte)255)]
    [InlineData(1, (byte)0)]
    [InlineData(1, (byte)255)]
    public void ReopenedEntryKeepsFrameVisibleWithoutChangingSavedOpacity(double opacity, byte shade) => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create(Solid(640, 360, shade));
        Invoke(host.Editor, "PlaceTextBox", new PointD(80, 80));
        Field<TextBox>(host.Editor, "_activeTextBox").Text = "입력 영역";
        Invoke(host.Editor, "CommitActiveText");
        AnnotationEditorController controller = Controller(host.Editor);
        var annotation = Assert.IsType<TextAnnotation>(Assert.Single(controller.Document.Items));
        annotation.Opacity = opacity;
        RectD original = annotation.Rect;
        controller.SetSelected(annotation);
        Assert.True(host.Editor.HandleShortcut(Key.F2, ModifierKeys.None));
        TextBox box = Field<TextBox>(host.Editor, "_activeTextBox");
        host.Window.UpdateLayout();
        AssertFrame(box);
        Assert.Equal(opacity, Assert.IsType<TextAnnotation>(Assert.Single(host.Editor.CreateCurrentImageSnapshot().Items)).Opacity);
        Assert.True(host.Editor.HandleShortcut(Key.Escape, ModifierKeys.None));
        Assert.Equal(original, annotation.Rect);
        Assert.Equal(opacity, annotation.Opacity);
        Assert.Null(controller.EditingText);
    });

    private const byte Picture = 0x30;

    /// <summary>The bar in the caption's outline colour that lies under the live caret.</summary>
    internal static Rectangle CaretRing(TextBox box)
    {
        var host = Assert.IsType<ScrollViewer>(box.Template.FindName("PART_ContentHost", box));
        var layer = Assert.IsType<Canvas>(host.Template.FindName(AnnotationEditorControl.CaretOutlineLayerName, host));
        return Assert.IsType<Rectangle>(Assert.Single(layer.Children));
    }

    /// <summary>A plate of <paramref name="plate"/> (with its alpha) over an opaque picture.</summary>
    private static Color Over(ColorRgba plate, Color picture)
    {
        double a = plate.A / 255.0;
        byte Mix(byte top, byte bottom) => (byte)Math.Round((top * a) + (bottom * (1 - a)));
        return Color.FromRgb(Mix(plate.R, picture.R), Mix(plate.G, picture.G), Mix(plate.B, picture.B));
    }

    private static double Contrast(Color first, Color second)
    {
        double one = Luminance(first), two = Luminance(second);
        return (Math.Max(one, two) + 0.05) / (Math.Min(one, two) + 0.05);

        static double Channel(byte value)
        {
            double c = value / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        static double Luminance(Color color) => (0.2126 * Channel(color.R)) + (0.7152 * Channel(color.G)) + (0.0722 * Channel(color.B));
    }

    /// <summary>Whether Windows blinks the caret (0 and INFINITE keep it steady).</summary>
    private static bool SystemCaretBlinks() => unchecked((int)GetCaretBlinkTime()) > 0;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetCaretBlinkTime();

    private static void AssertFrame(TextBox box)
    {
        Assert.Equal(1, box.Opacity);
        Assert.Equal(new Thickness(2), box.BorderThickness);
        var rendered = new RenderTargetBitmap((int)Math.Ceiling(box.ActualWidth),
            (int)Math.Ceiling(box.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
            context.DrawRectangle(new VisualBrush(box), null, new Rect(0, 0, box.ActualWidth, box.ActualHeight));
        rendered.Render(visual);
        int midpoint = rendered.PixelWidth / 2;
        byte[] pixels = new byte[8];
        rendered.CopyPixels(new Int32Rect(midpoint, 0, 1, 2), pixels, 4, 0);
        Assert.Equal(new byte[] { 0, 0, 0, 255, 255, 255, 255, 255 }, pixels);
    }
}
