using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class TextAnnotationStyleTests
{
    [Theory]
    [InlineData("Malgun Gothic", true, TextOutlineStyle.Legacy)]
    [InlineData("Maple Mono", true, TextOutlineStyle.Consistent)]
    [InlineData("Maple Mono NF", false, TextOutlineStyle.Consistent)]
    [InlineData("맑은 고딕", false, TextOutlineStyle.Legacy)]
    public void FontAndOutlineSurviveLayerRoundTripAndClone(string family, bool outline, TextOutlineStyle style)
    {
        var document = AnnotationDocument.CreateFor(640, 360);
        document.Add(new TextAnnotation
        {
            Rect = new RectD(12, 34, 240, 60),
            Text = "Caption 가나다",
            FontFamily = family,
            FontSize = 27,
            Bold = true,
            Italic = true,
            Background = ColorRgba.Black,
            HasOutline = outline,
            OutlineStyle = style,
        });

        var restored = AnnotationDocument.TryFromJson(document.ToJson())!;
        var text = Assert.IsType<TextAnnotation>(Assert.Single(restored.Items));
        var clone = Assert.IsType<TextAnnotation>(text.Clone());

        foreach (TextAnnotation copy in new[] { text, clone })
        {
            Assert.Equal(family, copy.FontFamily);
            Assert.Equal(27, copy.FontSize);
            Assert.True(copy.Bold);
            Assert.True(copy.Italic);
            Assert.Equal(ColorRgba.Black, copy.Background);
            Assert.Equal(outline, copy.HasOutline);
            Assert.Equal(style, copy.OutlineStyle);
        }
        Assert.NotEqual(text.Id, clone.Id);
        Assert.Equal(1, restored.SchemaVersion);
    }

    [Theory]
    [InlineData("")]
    [InlineData(",\"hasOutline\":true")]
    [InlineData(",\"hasOutline\":false")]
    public void ExistingLayersKeepTheirOriginalOutlineMode(string outlineSetting)
    {
        string json = """
            {"schemaVersion":1,"canvasWidth":640,"canvasHeight":360,"items":[
              {"kind":"text","rect":{"x":10,"y":20,"width":200,"height":40},"text":"기존 문구","fontFamily":"Maple Mono"
            """ + outlineSetting + "}]}";

        AnnotationDocument document = AnnotationDocument.TryFromJson(json)!;
        var text = Assert.IsType<TextAnnotation>(Assert.Single(document.Items));

        Assert.Equal(TextOutlineStyle.Legacy, text.OutlineStyle);
        Assert.Equal(!outlineSetting.Contains("false", StringComparison.Ordinal), text.HasOutline);
        Assert.Equal("Maple Mono", text.FontFamily);
        var reopened = AnnotationDocument.TryFromJson(document.ToJson())!;
        Assert.Equal(TextOutlineStyle.Legacy, Assert.IsType<TextAnnotation>(Assert.Single(reopened.Items)).OutlineStyle);
    }
}
