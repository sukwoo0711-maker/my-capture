using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
    public void LiveCaretHasAnAdornerLayerAndTheCaptionInkOverAClearBox(byte shade, byte alpha) => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
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
        // The caret takes the glyphs' colour over a clear box; the surface draws the caption's
        // plate under it, as the committed caption has it.
        Assert.Equal(annotation.Foreground.ToColor(), Assert.IsType<SolidColorBrush>(box.CaretBrush).Color);
        Assert.Equal(0, Assert.IsType<SolidColorBrush>(box.Background).Color.A);
        Assert.Equal(annotation.Background, Controller(host.Editor).LiveTextForPaint()?.Background);
        box.CaretIndex = box.Text.Length;
        Assert.False(box.GetRectFromCharacterIndex(box.CaretIndex).IsEmpty);
        Assert.Equal(annotation.Background, Assert.IsType<TextAnnotation>(Assert.Single(host.Editor.CreateCurrentImageSnapshot().Items)).Background);
    });

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
