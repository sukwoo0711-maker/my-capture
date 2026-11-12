using System.Reflection;
using System.Windows;
using System.Windows.Media;
using MyCapture.App.Recording;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class TimelineConnectorBoundsTests
{
    [Theory]
    [InlineData(1.0, 18.0, "en-US")]
    [InlineData(1.25, 17.6, "en-US")]
    [InlineData(1.5, 18.0, "en-US")]
    [InlineData(1.75, 17.714285714285715, "en-US")]
    [InlineData(1.75, 18.285714285714285, "en-US")]
    [InlineData(2.0, 18.0, "en-US")]
    [InlineData(1.25, 18.4, "en-US")]
    [InlineData(1.0, 17.6, "en-US")]
    [InlineData(1.25, 17.6, "ko-KR")]
    [InlineData(1.75, 17.714285714285715, "ko-KR")]
    public void CaptionAndPaddingFitActualHeightWithoutReducingTypeSize(double dpiScale, double height, string languageCode) => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage(languageCode);
        using var timeline = new TwoLineTimeline();
        VisualTreeHelper.SetRootDpi(timeline, new DpiScale(dpiScale, dpiScale));
        timeline.Initialize(10000, 30);

        Drawing[] drawings = DrawConnector(timeline, 720, height);
        GeometryDrawing background = Assert.Single(drawings.OfType<GeometryDrawing>(),
            drawing => drawing.Pen is null && drawing.Brush is not null);
        Assert.True(background.Bounds.Top >= 0 && background.Bounds.Bottom <= height,
            $"Caption background {background.Bounds} must fit {height} DIP at {dpiScale}x.");
        GlyphRunDrawing[] glyphs = drawings.OfType<GlyphRunDrawing>().ToArray();
        Assert.NotEmpty(glyphs);
        Assert.Contains(glyphs, glyph => !glyph.Bounds.IsEmpty);
        Assert.All(glyphs, glyph =>
        {
            if (glyph.Bounds.IsEmpty)
            {
                // Font fallback can emit a separate space run, which has advance but no ink.
                Assert.NotEmpty(glyph.GlyphRun.Characters!);
                Assert.All(glyph.GlyphRun.Characters!, character => Assert.True(char.IsWhiteSpace(character)));
            }
            else
            {
                Assert.True(background.Bounds.Contains(glyph.Bounds),
                    $"Caption glyphs {glyph.Bounds} must fit their background {background.Bounds}.");
            }
            Assert.DoesNotContain((ushort)0, glyph.GlyphRun.GlyphIndices);
            Assert.Equal(TypeScale.Caption, glyph.GlyphRun.FontRenderingEmSize);
        });
        Assert.Equal(UiText.Get("Text_41F81C272691"),
            string.Concat(glyphs.Select(glyph => new string(glyph.GlyphRun.Characters!.ToArray()))));
        AssertConnectorEndpoints(drawings, height);
    });

    [Theory]
    [InlineData(359, 18)]
    [InlineData(720, 8)]
    public void OptionalCaptionIsOmittedWhenItsFullTextCannotFit(double width, double height) => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("en-US");
        using var timeline = new TwoLineTimeline();
        timeline.Initialize(10000, 30);

        Drawing[] drawings = DrawConnector(timeline, width, height);
        Assert.Empty(drawings.OfType<GlyphRunDrawing>());
        Assert.DoesNotContain(drawings.OfType<GeometryDrawing>(), drawing => drawing.Pen is null);
        AssertConnectorEndpoints(drawings, height);
    });

    private static Drawing[] DrawConnector(TwoLineTimeline timeline, double width, double height)
    {
        var visual = new DrawingVisual();
        // Exercise the production renderer before the surface's clip can conceal overflow.
        using (DrawingContext dc = visual.RenderOpen())
        {
            typeof(TwoLineTimeline).GetMethod("DrawConnectorLayer", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(timeline, [dc, TimelineRenderLayer.Range, new Size(width, height)]);
        }
        return Leaves(visual.Drawing).ToArray();
    }

    private static void AssertConnectorEndpoints(Drawing[] drawings, double height)
    {
        LineGeometry[] lines = drawings.OfType<GeometryDrawing>().Select(drawing => drawing.Geometry).OfType<LineGeometry>().ToArray();
        Assert.Equal(2, lines.Length);
        Assert.All(lines, line =>
        {
            Assert.Equal(0, line.StartPoint.Y);
            Assert.Equal(height, line.EndPoint.Y);
        });
    }

    private static IEnumerable<Drawing> Leaves(Drawing drawing)
    {
        if (drawing is DrawingGroup group)
        {
            foreach (Drawing child in group.Children)
                foreach (Drawing leaf in Leaves(child)) yield return leaf;
        }
        else yield return drawing;
    }
}
