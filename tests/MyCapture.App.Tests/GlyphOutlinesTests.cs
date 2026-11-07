using System.Globalization;
using System.Windows;
using System.Windows.Media;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// A caption's outline built a glyph at a time is the outline <see cref="FormattedText.BuildGeometry"/>
/// gives, figure for figure, and the next layout reuses the glyphs that stayed in place.
/// </summary>
public sealed class GlyphOutlinesTests
{
    [Theory]
    [InlineData("편집 응답성 측정 MyCapture 0123456789 텍스트 입력 지연 확인 ", null, false, false, 0)]
    [InlineData("Quick brown fox, AV Wa To 1/2 ffi fl", "Segoe UI", false, true, 0)]
    [InlineData("굵게 쓴 자막과 Bold text", null, true, false, 0)]
    [InlineData("첫 줄\n둘째 줄 second line\n\n넷째 줄", null, false, false, 0)]
    [InlineData("줄 너비에 맞춰 여러 줄로 나뉘는 긴 자막입니다 wrapping words at the box", "Malgun Gothic", false, false, 160)]
    [InlineData("기호 → ✓ ★ 그리고 이모지 😀 섞기", null, false, false, 0)]
    public void OutlineHasTheFiguresOfTheLayoutsOwnGeometry(string text, string? font, bool bold, bool italic, double wrap) => StaTestHost.Run(() =>
    {
        var caption = Caption(text, font, bold, italic, wrap);
        FormattedText formatted = TextAnnotationLayout.Format(caption, 1.25);
        var origin = new Point(12.5, 40.25);
        Dictionary<GlyphOutlines.Key, Geometry>? kept = null;

        Geometry built = GlyphOutlines.Build(Record(formatted, origin), formatted, origin, ref kept);

        // Plain left-to-right runs take the glyph path, not the whole-layout fallback.
        Assert.NotNull(kept);
        Assert.Equal(Figures(formatted.BuildGeometry(origin)), Figures(built));
    });

    [Fact]
    public void RightToLeftTextKeepsTheLayoutsOwnGeometry() => StaTestHost.Run(() =>
    {
        FormattedText formatted = TextAnnotationLayout.Format(Caption("שלום עולם مرحبا", "Segoe UI", false, false, 0), 1.0);
        Dictionary<GlyphOutlines.Key, Geometry>? kept = new();

        Geometry built = GlyphOutlines.Build(Record(formatted, default), formatted, default, ref kept);

        Assert.Null(kept);
        Assert.Equal(Figures(formatted.BuildGeometry(default)), Figures(built));
    });

    [Fact]
    public void TypingAtTheEndTracesOnlyTheNewGlyph() => StaTestHost.Run(() =>
    {
        var origin = new Point(30, 20);
        Dictionary<GlyphOutlines.Key, Geometry>? kept = null;
        FormattedText before = TextAnnotationLayout.Format(Caption("입력 중 자막", null, false, false, 0), 1.0);
        List<Geometry> first = Leaves(GlyphOutlines.Build(Record(before, origin), before, origin, ref kept));
        FormattedText after = TextAnnotationLayout.Format(Caption("입력 중 자막이", null, false, false, 0), 1.0);
        List<Geometry> second = Leaves(GlyphOutlines.Build(Record(after, origin), after, origin, ref kept));

        Assert.Equal(first.Count + 1, second.Count);
        for (int i = 0; i < first.Count; i++) Assert.Same(first[i], second[i]);
        Assert.Equal(Figures(after.BuildGeometry(origin)), Figures(new GeometryGroup { Children = new GeometryCollection(second) }));
    });

    private static TextAnnotation Caption(string text, string? font, bool bold, bool italic, double wrap)
    {
        var caption = new TextAnnotation
        {
            Text = text,
            FontSize = 28,
            Bold = bold,
            Italic = italic,
            Foreground = ColorRgba.FromRgb(0xEF, 0x44, 0x44),
            AutoSize = wrap <= 0,
            Rect = new RectD(0, 0, wrap > 0 ? wrap : 10, 10),
        };
        if (font is not null) caption.FontFamily = font;
        return caption;
    }

    private static DrawingGroup Record(FormattedText formatted, Point origin)
    {
        var glyphs = new DrawingGroup();
        using (DrawingContext context = glyphs.Open())
        {
            context.DrawText(formatted, origin);
        }

        glyphs.Freeze();
        return glyphs;
    }

    /// <summary>Every figure in drawing order, written out at full precision.</summary>
    private static List<string> Figures(Geometry geometry) =>
        Leaves(geometry).Select(leaf => $"{leaf.GetType().Name} {(leaf.Transform?.Value.IsIdentity ?? true)} "
            + PathGeometry.CreateFromGeometry(leaf).ToString(CultureInfo.InvariantCulture)).ToList();

    private static List<Geometry> Leaves(Geometry geometry)
    {
        var leaves = new List<Geometry>();
        void Walk(Geometry node)
        {
            if (node is GeometryGroup group)
            {
                foreach (Geometry child in group.Children) Walk(child);
            }
            else if (!node.IsEmpty())
            {
                leaves.Add(node);
            }
        }

        Walk(geometry);
        return leaves;
    }
}
