using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class TextAnnotationAutoSizeTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AutoSize_SurvivesTheLayerFileRoundTrip(bool autoSize)
    {
        AnnotationDocument document = AnnotationDocument.CreateFor(640, 360);
        document.Add(new TextAnnotation { Rect = new RectD(12, 34, 156.5, 25.5), Text = "확인", AutoSize = autoSize });

        AnnotationDocument restored = AnnotationDocument.TryFromJson(document.ToJson())!;

        var text = Assert.IsType<TextAnnotation>(Assert.Single(restored.Items));
        Assert.Equal(autoSize, text.AutoSize);
        Assert.Equal(new RectD(12, 34, 156.5, 25.5), text.Rect);
    }

    [Fact]
    public void LayerWrittenBeforeAutoSize_KeepsItsStoredWidthAsTheWrappingWidth()
    {
        const string json = """
            {"schemaVersion":1,"canvasWidth":640,"canvasHeight":360,"items":[
              {"kind":"text","zIndex":0,"rect":{"x":100,"y":100,"width":120,"height":30},"text":"확인 필요","fontSize":22}
            ]}
            """;

        AnnotationDocument document = AnnotationDocument.TryFromJson(json)!;

        var text = Assert.IsType<TextAnnotation>(Assert.Single(document.Items));
        Assert.False(text.AutoSize);
        Assert.Equal(new RectD(100, 100, 120, 30), text.Rect);
    }

    [Fact]
    public void Clone_CopiesAutoSize()
    {
        var text = new TextAnnotation { Rect = new RectD(1, 2, 3, 4), Text = "a", AutoSize = true };

        var clone = Assert.IsType<TextAnnotation>(text.Clone());

        Assert.True(clone.AutoSize);
        Assert.NotEqual(text.Id, clone.Id);
    }
}
