using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class AnnotationDocumentRotatorTests
{
    [Fact]
    public void QuarterTurn_SwapsCanvasAndRemapsRectangle()
    {
        AnnotationDocument document = AnnotationDocument.CreateFor(800, 600);
        var rect = new RectangleAnnotation { Rect = new RectD(10, 20, 100, 50) };
        document.Add(rect);

        AnnotationDocumentRotator.Rotate(document, 1);

        Assert.Equal(600, document.CanvasWidth);
        Assert.Equal(800, document.CanvasHeight);
        Assert.Equal(530, rect.Rect.X, 3);
        Assert.Equal(10, rect.Rect.Y, 3);
        Assert.Equal(50, rect.Rect.Width, 3);
        Assert.Equal(100, rect.Rect.Height, 3);
    }

    [Fact]
    public void FourTurns_RestoreOriginalGeometry()
    {
        AnnotationDocument document = AnnotationDocument.CreateFor(800, 600);
        var line = PolylineAnnotation.CreateArrow(new PointD(10, 20), new PointD(110, 80), ColorRgba.FromRgb(1, 2, 3), 3);
        document.Add(line);
        PointD start = line.Points[0];
        PointD end = line.Points[^1];

        AnnotationDocumentRotator.Rotate(document, 4);

        Assert.Equal(800, document.CanvasWidth);
        Assert.Equal(600, document.CanvasHeight);
        Assert.Equal(start.X, line.Points[0].X, 6);
        Assert.Equal(start.Y, line.Points[0].Y, 6);
        Assert.Equal(end.X, line.Points[^1].X, 6);
        Assert.Equal(end.Y, line.Points[^1].Y, 6);
    }

    [Fact]
    public void CounterClockwise_IsInverseOfClockwise()
    {
        AnnotationDocument document = AnnotationDocument.CreateFor(400, 200);
        var text = new TextAnnotation { Rect = new RectD(40, 10, 80, 20), Text = "hi" };
        document.Add(text);

        AnnotationDocumentRotator.Rotate(document, 1);
        AnnotationDocumentRotator.Rotate(document, -1);

        Assert.Equal(400, document.CanvasWidth);
        Assert.Equal(200, document.CanvasHeight);
        Assert.Equal(40, text.Rect.X, 3);
        Assert.Equal(10, text.Rect.Y, 3);
        Assert.Equal(80, text.Rect.Width, 3);
        Assert.Equal(20, text.Rect.Height, 3);
    }
}
