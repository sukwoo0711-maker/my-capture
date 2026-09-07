using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class ShapeStyleTests
{
    [Theory]
    [InlineData(100, 0)]
    [InlineData(50, 128)]
    [InlineData(0, 255)]
    public void FillTransparency_UsesOutlineRgbAndExpectedAlpha(double transparency, byte expectedAlpha)
    {
        ColorRgba color = ColorRgba.FromRgb(12, 34, 56);
        Assert.Equal(new ColorRgba(expectedAlpha, 12, 34, 56), ShapeAnnotation.FillForTransparency(color, transparency));
    }

    [Fact]
    public void StyleAndFill_SurviveCloneAndDocumentRoundTrip()
    {
        var rectangle = new RectangleAnnotation
        {
            Rect = new RectD(10, 10, 80, 40),
            StrokeStyle = AnnotationStrokeStyle.ThickDashed,
            StrokeThickness = 6,
            Stroke = ColorRgba.FromRgb(12, 34, 56),
            Fill = new ColorRgba(128, 12, 34, 56),
            FillMatchesStroke = true,
        };
        var document = AnnotationDocument.CreateFor(100, 80);
        document.Add(rectangle.Clone());
        AnnotationDocument loaded = Assert.IsType<AnnotationDocument>(AnnotationDocument.TryFromJson(document.ToJson()));
        RectangleAnnotation restored = Assert.IsType<RectangleAnnotation>(Assert.Single(loaded.Items));
        Assert.Equal(rectangle.StrokeStyle, restored.StrokeStyle);
        Assert.Equal(rectangle.StrokeThickness, restored.StrokeThickness);
        Assert.Equal(rectangle.Fill, restored.Fill);
        Assert.True(restored.FillMatchesStroke);
        Assert.Equal(50, restored.FillTransparency);
    }

    [Fact]
    public void LegacyDocument_PreservesIndependentOpaqueFillAndSolidOutline()
    {
        const string legacy = """
            {"schemaVersion":1,"canvasWidth":100,"canvasHeight":80,"items":[
              {"kind":"rect","rect":{"x":10,"y":10,"width":40,"height":30},
               "stroke":"#FF000000","fill":"#FF123456","strokeThickness":1}
            ]}
            """;
        AnnotationDocument document = Assert.IsType<AnnotationDocument>(AnnotationDocument.TryFromJson(legacy));
        RectangleAnnotation rectangle = Assert.IsType<RectangleAnnotation>(Assert.Single(document.Items));
        Assert.Equal(AnnotationStrokeStyle.Solid, rectangle.StrokeStyle);
        Assert.False(rectangle.FillMatchesStroke);
        Assert.Equal(new ColorRgba(255, 0x12, 0x34, 0x56), rectangle.Fill);
        Assert.Equal(0, rectangle.FillTransparency);
    }
}
