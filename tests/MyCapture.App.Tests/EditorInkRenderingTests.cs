using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class EditorInkRenderingTests
{
    [Theory]
    [InlineData(0, false, 1)]
    [InlineData(1, false, 1)]
    [InlineData(2, false, 1)]
    [InlineData(4096, false, 1)]
    [InlineData(4096, true, 1)]
    [InlineData(4096, false, 1.5)]
    [InlineData(4096, true, 1.5)]
    public void InkPixelsMatchPreviousSegmentGeometry(int count, bool highlighter, double scale) => StaTestHost.Run(() =>
    {
        PenAnnotation ink = Ink(count, highlighter);
        Assert.Equal(Render(ink, scale, legacy: true), Render(ink, scale, legacy: false));
    });

    [Fact]
    public void SubsequentRendersReflectMovedReplacedAndAppendedPoints() => StaTestHost.Run(() =>
    {
        PenAnnotation ink = Ink(128, highlighter: false);
        byte[] before = Render(ink, 1, legacy: false);
        // Undo/redo and dragging mutate the live model. No retained geometry may conceal
        // a same-count replacement, a changed point, or additional live ink segments.
        ink.Points = ink.Points.Select(point => new PointD(point.X + 12, point.Y + 7)).ToList();
        Assert.False(before.SequenceEqual(Render(ink, 1, legacy: false)));
        Assert.Equal(Render(ink, 1, legacy: true), Render(ink, 1, legacy: false));
        ink.Points[10] = new PointD(310, 170);
        ink.Points.Add(new PointD(20, 10));
        ink.StrokeThickness = 13;
        ink.IsHighlighter = true;
        ink.Opacity = .6;
        Assert.Equal(Render(ink, 1.5, legacy: true), Render(ink, 1.5, legacy: false));
    });

    private static PenAnnotation Ink(int count, bool highlighter) => new()
    {
        Stroke = new ColorRgba(190, 25, 120, 220),
        StrokeThickness = 7,
        IsHighlighter = highlighter,
        Opacity = .8,
        Points = Enumerable.Range(0, count).Select(index => new PointD(
            20 + index % 275,
            80 + Math.Sin(index * .13) * 50)).ToList(),
    };

    private static byte[] Render(PenAnnotation ink, double scale, bool legacy)
    {
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.PushTransform(new ScaleTransform(scale, scale));
            if (legacy)
            {
                context.PushOpacity(ink.Opacity);
                DrawPreviousGeometry(context, ink);
                context.Pop();
            }
            else
            {
                var document = AnnotationDocument.CreateFor(320, 180);
                document.Add(ink);
                new AnnotationRenderer(new AnnotationImageStore()).Render(context, document, scale);
            }
            context.Pop();
        }
        int width = (int)(320 * scale), height = (int)(180 * scale);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        byte[] pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    // Pre-change rendering is an independent pixel oracle, including caps, joins,
    // highlighter alpha, empty ink and one-point dots.
    private static void DrawPreviousGeometry(DrawingContext context, PenAnnotation ink)
    {
        if (ink.Points.Count == 0) return;
        ColorRgba color = ink.IsHighlighter ? ink.Stroke.WithAlpha((byte)Math.Min((int)ink.Stroke.A, 110)) : ink.Stroke;
        var brush = new SolidColorBrush(Color.FromArgb(color.A, color.R, color.G, color.B));
        brush.Freeze();
        if (ink.Points.Count == 1)
        {
            double radius = Math.Max(.5, ink.StrokeThickness / 2);
            context.DrawEllipse(brush, null, ToPoint(ink.Points[0]), radius, radius);
            return;
        }
        var pen = new Pen(brush, Math.Max(.01, ink.StrokeThickness))
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
            DashCap = PenLineCap.Flat,
            DashStyle = DashStyles.Solid,
        };
        pen.Freeze();
        var figure = new PathFigure { StartPoint = ToPoint(ink.Points[0]), IsClosed = false, IsFilled = false };
        foreach (PointD point in ink.Points.Skip(1)) figure.Segments.Add(new LineSegment(ToPoint(point), isStroked: true));
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        context.DrawGeometry(null, pen, geometry);
    }

    private static Point ToPoint(PointD point) => new(point.X, point.Y);
}
