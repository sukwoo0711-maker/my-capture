using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class ShapeRenderingTests
{
    [Fact]
    public void Render_StrokeStylesProduceGapsAndThickDashesHaveMoreCoverage() => StaTestHost.Run(() =>
    {
        byte[] solid = Render(AnnotationStrokeStyle.Solid, 100);
        byte[] dashed = Render(AnnotationStrokeStyle.Dashed, 100);
        byte[] dotted = Render(AnnotationStrokeStyle.Dotted, 100);
        byte[] thick = Render(AnnotationStrokeStyle.ThickDashed, 100);
        Assert.True(AlphaCoverage(solid) > AlphaCoverage(dashed));
        Assert.True(AlphaCoverage(thick) > AlphaCoverage(dashed));
        Assert.False(dashed.SequenceEqual(dotted));
        Assert.Equal(0, solid[((60 * 200 + 100) * 4) + 3]);
        byte[] filled = Render(AnnotationStrokeStyle.Dashed, 50);
        int center = (60 * 200 + 100) * 4;
        Assert.Equal(128, filled[center + 3]);
        Assert.Equal(128, filled[center + 2]);
        Assert.Equal(0, filled[center]);
    });

    private static long AlphaCoverage(byte[] pixels)
    {
        long sum = 0;
        for (int index = 3; index < pixels.Length; index += 4)
        {
            sum += pixels[index];
        }
        return sum;
    }

    private static byte[] Render(AnnotationStrokeStyle style, double transparency)
    {
        var document = AnnotationDocument.CreateFor(200, 120);
        ColorRgba red = ColorRgba.FromRgb(255, 0, 0);
        document.Add(new RectangleAnnotation
        {
            Rect = new RectD(20, 20, 160, 80),
            Stroke = red,
            StrokeThickness = 3,
            StrokeStyle = style,
            Fill = ShapeAnnotation.FillForTransparency(red, transparency),
        });
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            new AnnotationRenderer(new AnnotationImageStore()).Render(context, document, 1);
        }
        var bitmap = new RenderTargetBitmap(200, 120, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[200 * 120 * 4];
        bitmap.CopyPixels(pixels, 200 * 4, 0);
        return pixels;
    }
}
