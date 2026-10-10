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

    [Fact]
    public void QuarterTurn_KeepsUprightTextBoxSizeAndTurnsOnlyItsCentre()
    {
        AnnotationDocument document = AnnotationDocument.CreateFor(800, 600);
        var text = new TextAnnotation { Rect = new RectD(40, 10, 200, 30), Text = "확인 필요", AutoSize = true };
        document.Add(text);

        AnnotationDocumentRotator.Rotate(document, 1);

        // Centre (140, 25) maps to (600 - 25, 140); the caption keeps its 200 x 30 box, so
        // the same line still fits instead of being squeezed into a 30-pixel column.
        Assert.Equal(475, text.Rect.X, 6);
        Assert.Equal(125, text.Rect.Y, 6);
        Assert.Equal(200, text.Rect.Width, 6);
        Assert.Equal(30, text.Rect.Height, 6);
        Assert.True(text.AutoSize);
    }

    [Fact]
    public void FourTurns_RestoreTextBoxExactly()
    {
        AnnotationDocument document = AnnotationDocument.CreateFor(1280, 720);
        var text = new TextAnnotation { Rect = new RectD(1100.25, 37.5, 260.75, 48.5), Text = "오른쪽 끝" };
        document.Add(text);
        RectD before = text.Rect;

        AnnotationDocumentRotator.Rotate(document, 4);

        Assert.Equal(before.X, text.Rect.X, 6);
        Assert.Equal(before.Y, text.Rect.Y, 6);
        Assert.Equal(before.Width, text.Rect.Width, 6);
        Assert.Equal(before.Height, text.Rect.Height, 6);
    }

    [Fact]
    public void QuarterTurn_SquareStepBadgeLandsWhereTheCanvasMapsIt()
    {
        AnnotationDocument document = AnnotationDocument.CreateFor(800, 600);
        var badge = new TextAnnotation { IsStepNumber = true, Text = "1", Rect = new RectD(10, 20, 40, 40) };
        document.Add(badge);

        AnnotationDocumentRotator.Rotate(document, 1);

        Assert.Equal(540, badge.Rect.X, 6);
        Assert.Equal(10, badge.Rect.Y, 6);
        Assert.Equal(40, badge.Rect.Width, 6);
        Assert.Equal(40, badge.Rect.Height, 6);
    }
}
