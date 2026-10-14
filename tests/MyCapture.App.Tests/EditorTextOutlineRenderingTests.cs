using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class EditorTextOutlineRenderingTests
{
    [Theory]
    [InlineData("#FFFFFF", false)]
    [InlineData("#FFEB3B", false)]
    [InlineData("#EF4444", false)]
    [InlineData("#007ACC", false)]
    [InlineData("#808080", false)]
    [InlineData("#101010", true)]
    [InlineData("#000000", true)]
    [InlineData("#80101010", true)]
    public void ConsistentOutlineHasTheSameRelativeWeightAcrossColors(string foreground, bool lightOutline) => StaTestHost.Run(() =>
    {
        foreach (double size in new[] { 4.0, 12.0, 18.0, 72.0, 800.0 })
        {
            TextAnnotation text = Caption(foreground);
            text.FontSize = size;
            text.OutlineStyle = TextOutlineStyle.Consistent;

            DrawingGroup drawing = Draw(text);
            GeometryDrawing outline = Assert.Single(Descendants(drawing).OfType<GeometryDrawing>());
            Pen pen = Assert.IsType<Pen>(outline.Pen);
            SolidColorBrush brush = Assert.IsType<SolidColorBrush>(pen.Brush);
            byte shade = lightOutline ? (byte)255 : (byte)0;

            Assert.Equal(Color.FromArgb(text.Foreground.A, shade, shade, shade), brush.Color);
            Assert.Equal(1.0 / 18, pen.Thickness / size, 10);
            Assert.Equal(PenLineJoin.Round, pen.LineJoin);
            Assert.Null(outline.Brush);
            Assert.False(Assert.IsAssignableFrom<Geometry>(outline.Geometry).IsEmpty());
            Assert.Contains(Descendants(drawing), child => child is GlyphRunDrawing);
        }
    });

    [Theory]
    [InlineData(TextOutlineStyle.Legacy)]
    [InlineData(TextOutlineStyle.Consistent)]
    public void OutlineToggleRemovesOnlyTheOutline(TextOutlineStyle style) => StaTestHost.Run(() =>
    {
        TextAnnotation text = Caption("#EF4444");
        text.OutlineStyle = style;
        text.HasOutline = false;

        DrawingGroup drawing = Draw(text);

        Assert.Empty(Descendants(drawing).OfType<GeometryDrawing>());
        Assert.Contains(Descendants(drawing), child => child is GlyphRunDrawing);
    });

    [Theory]
    [InlineData("#FFFFFF", 12)]
    [InlineData("#FFEB3B", 36)]
    [InlineData("#EF4444", 36)]
    [InlineData("#007ACC", 36)]
    [InlineData("#101010", 36)]
    [InlineData("#80101010", 12)]
    public void LayerWithoutOutlineStyleMatchesPreviousRendererPixels(string foreground, double size) => StaTestHost.Run(() =>
    {
        string json = $$"""
            {"schemaVersion":1,"canvasWidth":320,"canvasHeight":180,"items":[
              {"kind":"text","rect":{"x":20,"y":20,"width":280,"height":120},"text":"Caption 가나다","fontSize":{{size}},"foreground":"{{foreground}}"}
            ]}
            """;
        AnnotationDocument document = AnnotationDocument.TryFromJson(json)!;
        var text = Assert.IsType<TextAnnotation>(Assert.Single(document.Items));
        Assert.Equal(TextOutlineStyle.Legacy, text.OutlineStyle);

        var previous = new DrawingGroup();
        using (DrawingContext context = previous.Open())
        {
            FormattedText formatted = TextAnnotationLayout.Format(text, 1.0);
            var origin = new Point(text.Rect.X, text.Rect.Y);
            ColorRgba fill = text.Foreground;
            double luma = (0.299 * fill.R) + (0.587 * fill.G) + (0.114 * fill.B);
            ColorRgba edge = luma > 140 ? new ColorRgba(fill.A, 0, 0, 0) : new ColorRgba(fill.A, 255, 255, 255);
            var pen = new Pen(edge.ToBrush(), Math.Max(1, text.FontSize / 12)) { LineJoin = PenLineJoin.Round };
            pen.Freeze();
            context.DrawGeometry(null, pen, formatted.BuildGeometry(origin));
            context.DrawText(formatted, origin);
        }

        Assert.Equal(Pixels(previous), Pixels(Draw(text)));
        AnnotationDocument reopened = AnnotationDocument.TryFromJson(document.ToJson())!;
        Assert.Equal(Pixels(previous), Pixels(Draw(Assert.IsType<TextAnnotation>(Assert.Single(reopened.Items)))));
    });

    private static TextAnnotation Caption(string foreground) => new()
    {
        Rect = new RectD(20, 20, 280, 120),
        Text = "Caption 가나다",
        Foreground = ColorRgba.Parse(foreground),
        FontSize = 36,
        AutoSize = true,
    };

    private static DrawingGroup Draw(TextAnnotation text)
    {
        var document = AnnotationDocument.CreateFor(320, 180);
        document.Add(text);
        var group = new DrawingGroup();
        using (DrawingContext context = group.Open())
        {
            new AnnotationRenderer(new AnnotationImageStore()).Render(context, document, 1.0);
        }
        return group;
    }

    private static byte[] Pixels(Drawing drawing)
    {
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen()) context.DrawDrawing(drawing);
        var bitmap = new RenderTargetBitmap(320, 180, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[320 * 180 * 4];
        bitmap.CopyPixels(pixels, 320 * 4, 0);
        return pixels;
    }

    private static IEnumerable<Drawing> Descendants(DrawingGroup group)
    {
        foreach (Drawing drawing in group.Children)
        {
            if (drawing is DrawingGroup nested)
            {
                foreach (Drawing child in Descendants(nested)) yield return child;
            }
            else
            {
                yield return drawing;
            }
        }
    }
}
