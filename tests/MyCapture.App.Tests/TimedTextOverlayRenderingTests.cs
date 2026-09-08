using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Recording;
using MyCapture.Core.Recording;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Rendered-pixel regression tests for <see cref="TimedTextOverlayRenderer"/>.
/// Catches the centered FormattedText origin bug where glyphs are drawn offset outside the dark background
/// box (resulting in video text showing only background) and verifies white glyphs are centered inside the
/// background across English, Korean, multiline text, and vertical placements on small and large canvases.
/// </summary>
public sealed class TimedTextOverlayRenderingTests
{
    [Theory]
    // 320x240 canvas (small canvas)
    [InlineData(320, 240, "Subtitle", VideoTextPlacement.Bottom)]
    [InlineData(320, 240, "자막 테스트", VideoTextPlacement.Center)]
    [InlineData(320, 240, "First Line\nSecond Line", VideoTextPlacement.Top)]
    [InlineData(320, 240, "English Note", VideoTextPlacement.Top)]
    [InlineData(320, 240, "한국어 중앙 자막", VideoTextPlacement.Center)]
    [InlineData(320, 240, "Multi Top\nMulti Bottom", VideoTextPlacement.Bottom)]
    // 1920x1080 canvas (FHD canvas)
    [InlineData(1920, 1080, "English Subtitle", VideoTextPlacement.Top)]
    [InlineData(1920, 1080, "한국어 자막 테스트", VideoTextPlacement.Center)]
    [InlineData(1920, 1080, "Title Line\nSubtitle Line", VideoTextPlacement.Bottom)]
    [InlineData(1920, 1080, "English Bottom", VideoTextPlacement.Bottom)]
    [InlineData(1920, 1080, "상단 한국어 자막", VideoTextPlacement.Top)]
    [InlineData(1920, 1080, "Center Multi 1\nCenter Multi 2", VideoTextPlacement.Center)]
    public void Draw_RendersWhiteGlyphsCenteredInsideDarkBackground(
        int width,
        int height,
        string text,
        VideoTextPlacement placement) => StaTestHost.Run(() =>
    {
        RenderAnalysis analysis = RenderAndAnalyze(width, height, text, placement);

        // 1. Dark background is rendered
        Assert.True(analysis.DarkBackgroundPixelCount > 0, "Dark background was not rendered.");

        // 2. White text glyphs are rendered
        int minExpectedGlyphs = height <= 300 ? 15 : 60;
        Assert.True(
            analysis.WhiteGlyphPixelCount >= minExpectedGlyphs,
            $"Expected at least {minExpectedGlyphs} white glyph pixels, but found {analysis.WhiteGlyphPixelCount}.");

        // 3. Regression test: Glyphs must be inside the dark background box (not displaced outside leaving only background)
        Assert.True(
            analysis.WhiteGlyphsInsideBackgroundBox > 0,
            "No white text glyphs were rendered inside the dark background box (video text showed only background).");
        Assert.True(
            analysis.WhiteGlyphsInsideBackgroundBox >= (int)(analysis.WhiteGlyphPixelCount * 0.95),
            $"Expected almost all glyph pixels ({analysis.WhiteGlyphPixelCount}) inside background box, but found {analysis.WhiteGlyphsInsideBackgroundBox}.");

        // 4. Centering: Background and glyphs are horizontally centered
        double canvasCenterX = width / 2.0;
        double horizontalTolerance = Math.Max(12.0, width * 0.04);
        Assert.InRange(analysis.BackgroundCenterX, canvasCenterX - 2.0, canvasCenterX + 2.0);
        Assert.InRange(analysis.GlyphCenterX, canvasCenterX - horizontalTolerance, canvasCenterX + horizontalTolerance);
        Assert.InRange(Math.Abs(analysis.GlyphCenterX - analysis.BackgroundCenterX), 0, horizontalTolerance);

        // 5. Vertical placement anchor
        switch (placement)
        {
            case VideoTextPlacement.Top:
                Assert.True(
                    analysis.BackgroundCenterY < height * 0.35,
                    $"Expected Top background in upper region, but was {analysis.BackgroundCenterY}");
                Assert.True(
                    analysis.GlyphCenterY < height * 0.35,
                    $"Expected Top glyphs in upper region, but was {analysis.GlyphCenterY}");
                break;

            case VideoTextPlacement.Center:
                Assert.InRange(analysis.BackgroundCenterY, (height * 0.5) - (height * 0.15), (height * 0.5) + (height * 0.15));
                Assert.InRange(analysis.GlyphCenterY, (height * 0.5) - (height * 0.15), (height * 0.5) + (height * 0.15));
                break;

            case VideoTextPlacement.Bottom:
                Assert.True(
                    analysis.BackgroundCenterY > height * 0.65,
                    $"Expected Bottom background in lower region, but was {analysis.BackgroundCenterY}");
                Assert.True(
                    analysis.GlyphCenterY > height * 0.65,
                    $"Expected Bottom glyphs in lower region, but was {analysis.GlyphCenterY}");
                break;
        }
    });

    [Theory]
    [InlineData(320, 240, VideoTextPlacement.Top)]
    [InlineData(1920, 1080, VideoTextPlacement.Center)]
    public void Draw_ShortEnglishText_CentersGlyphsInsideBackground(
        int width,
        int height,
        VideoTextPlacement placement) => StaTestHost.Run(() =>
    {
        RenderAnalysis analysis = RenderAndAnalyze(width, height, "Subtitle", placement);

        Assert.True(analysis.DarkBackgroundPixelCount > 0);
        Assert.True(analysis.WhiteGlyphPixelCount >= (height <= 300 ? 15 : 60));
        Assert.True(
            analysis.WhiteGlyphsInsideBackgroundBox >= (int)(analysis.WhiteGlyphPixelCount * 0.95),
            $"English glyphs must be rendered inside the dark background box, but only {analysis.WhiteGlyphsInsideBackgroundBox} of {analysis.WhiteGlyphPixelCount} were inside.");

        double horizontalTolerance = Math.Max(12.0, width * 0.04);
        Assert.InRange(Math.Abs(analysis.GlyphCenterX - analysis.BackgroundCenterX), 0, horizontalTolerance);
    });

    [Theory]
    [InlineData(320, 240, VideoTextPlacement.Center)]
    [InlineData(1920, 1080, VideoTextPlacement.Bottom)]
    public void Draw_KoreanText_CentersHangulGlyphsInsideBackground(
        int width,
        int height,
        VideoTextPlacement placement) => StaTestHost.Run(() =>
    {
        RenderAnalysis analysis = RenderAndAnalyze(width, height, "자막 테스트", placement);

        Assert.True(analysis.DarkBackgroundPixelCount > 0);
        Assert.True(analysis.WhiteGlyphPixelCount >= (height <= 300 ? 15 : 60));
        Assert.True(
            analysis.WhiteGlyphsInsideBackgroundBox >= (int)(analysis.WhiteGlyphPixelCount * 0.95),
            $"Korean glyphs must be rendered inside the dark background box, but only {analysis.WhiteGlyphsInsideBackgroundBox} of {analysis.WhiteGlyphPixelCount} were inside.");

        double horizontalTolerance = Math.Max(12.0, width * 0.04);
        Assert.InRange(Math.Abs(analysis.GlyphCenterX - analysis.BackgroundCenterX), 0, horizontalTolerance);
    });

    [Theory]
    [InlineData(320, 240)]
    [InlineData(1920, 1080)]
    public void Draw_MultilineText_ExpandsVerticallyAndRemainsCenteredInsideBackground(int width, int height) => StaTestHost.Run(() =>
    {
        RenderAnalysis single = RenderAndAnalyze(width, height, "Single line", VideoTextPlacement.Center);
        RenderAnalysis multi = RenderAndAnalyze(width, height, "First line\nSecond line", VideoTextPlacement.Center);

        // Multiline should take more vertical space
        Assert.True(
            multi.GlyphHeight > single.GlyphHeight,
            $"Multiline glyph height ({multi.GlyphHeight}) should exceed single line height ({single.GlyphHeight})");
        Assert.True(
            multi.BackgroundHeight > single.BackgroundHeight,
            $"Multiline background height ({multi.BackgroundHeight}) should exceed single line background height ({single.BackgroundHeight})");

        // Both should be centered inside background box
        Assert.True(
            multi.WhiteGlyphsInsideBackgroundBox >= (int)(multi.WhiteGlyphPixelCount * 0.95),
            $"Multiline glyphs must be rendered inside the dark background box, but only {multi.WhiteGlyphsInsideBackgroundBox} of {multi.WhiteGlyphPixelCount} were inside.");
        double horizontalTolerance = Math.Max(12.0, width * 0.04);
        Assert.InRange(Math.Abs(multi.GlyphCenterX - multi.BackgroundCenterX), 0, horizontalTolerance);
    });

    [Fact]
    public void Draw_InactiveOverlay_RendersNoPixels() => StaTestHost.Run(() =>
    {
        var overlay = new TimedTextOverlay
        {
            Text = "Inactive Subtitle",
            StartMs = 1000,
            EndMs = 2000,
        };

        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            TimedTextOverlayRenderer.Draw(
                dc,
                [overlay],
                sourceTimeMs: 500, // outside [1000, 2000)
                width: 320,
                height: 240,
                pixelsPerDip: 1.0);
        }

        var target = new RenderTargetBitmap(320, 240, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);

        int stride = 320 * 4;
        byte[] pixels = new byte[stride * 240];
        target.CopyPixels(pixels, stride, 0);

        Assert.True(Array.TrueForAll(pixels, static b => b == 0), "Expected no pixels to be drawn for inactive overlay.");
    });

    private static RenderAnalysis RenderAndAnalyze(
        int width,
        int height,
        string text,
        VideoTextPlacement placement)
    {
        var overlay = new TimedTextOverlay
        {
            Text = text,
            Placement = placement,
            StartMs = 0,
            EndMs = 1000,
        };

        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            TimedTextOverlayRenderer.Draw(
                dc,
                [overlay],
                sourceTimeMs: 500,
                width: width,
                height: height,
                pixelsPerDip: 1.0);
        }

        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);

        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        target.CopyPixels(pixels, stride, 0);

        int darkBgCount = 0;
        int minBgX = int.MaxValue, maxBgX = int.MinValue;
        int minBgY = int.MaxValue, maxBgY = int.MinValue;

        int glyphCount = 0;
        int minGlyphX = int.MaxValue, maxGlyphX = int.MinValue;
        int minGlyphY = int.MaxValue, maxGlyphY = int.MinValue;
        long sumGlyphX = 0;
        long sumGlyphY = 0;

        for (int y = 0; y < height; y++)
        {
            int rowOffset = y * stride;
            for (int x = 0; x < width; x++)
            {
                int offset = rowOffset + (x * 4);
                byte b = pixels[offset];
                byte g = pixels[offset + 1];
                byte r = pixels[offset + 2];
                byte a = pixels[offset + 3];

                // Dark background pixel (Color.FromArgb(0xC8, 0x08, 0x08, 0x08) -> A=200, premultiplied R,G,B ≈ 6)
                if (a >= 150 && r <= 30 && g <= 30 && b <= 30)
                {
                    darkBgCount++;
                    if (x < minBgX) minBgX = x;
                    if (x > maxBgX) maxBgX = x;
                    if (y < minBgY) minBgY = y;
                    if (y > maxBgY) maxBgY = y;
                }

                // White text glyph pixel (antialiased white on background or canvas)
                if (r >= 180 && g >= 180 && b >= 180)
                {
                    glyphCount++;
                    if (x < minGlyphX) minGlyphX = x;
                    if (x > maxGlyphX) maxGlyphX = x;
                    if (y < minGlyphY) minGlyphY = y;
                    if (y > maxGlyphY) maxGlyphY = y;
                    sumGlyphX += x;
                    sumGlyphY += y;
                }
            }
        }

        int whiteGlyphsInsideBox = 0;
        if (darkBgCount > 0 && glyphCount > 0)
        {
            for (int y = 0; y < height; y++)
            {
                int rowOffset = y * stride;
                for (int x = 0; x < width; x++)
                {
                    int offset = rowOffset + (x * 4);
                    byte b = pixels[offset];
                    byte g = pixels[offset + 1];
                    byte r = pixels[offset + 2];

                    if (r >= 180 && g >= 180 && b >= 180)
                    {
                        if (x >= minBgX && x <= maxBgX && y >= minBgY && y <= maxBgY)
                        {
                            whiteGlyphsInsideBox++;
                        }
                    }
                }
            }
        }

        double bgCenterX = darkBgCount > 0 ? (minBgX + maxBgX) / 2.0 : 0;
        double bgCenterY = darkBgCount > 0 ? (minBgY + maxBgY) / 2.0 : 0;
        double glyphCenterX = glyphCount > 0 ? sumGlyphX / (double)glyphCount : 0;
        double glyphCenterY = glyphCount > 0 ? sumGlyphY / (double)glyphCount : 0;

        return new RenderAnalysis(
            CanvasWidth: width,
            CanvasHeight: height,
            DarkBackgroundPixelCount: darkBgCount,
            WhiteGlyphPixelCount: glyphCount,
            WhiteGlyphsInsideBackgroundBox: whiteGlyphsInsideBox,
            MinBgX: darkBgCount > 0 ? minBgX : 0,
            MaxBgX: darkBgCount > 0 ? maxBgX : 0,
            MinBgY: darkBgCount > 0 ? minBgY : 0,
            MaxBgY: darkBgCount > 0 ? maxBgY : 0,
            MinGlyphX: glyphCount > 0 ? minGlyphX : 0,
            MaxGlyphX: glyphCount > 0 ? maxGlyphX : 0,
            MinGlyphY: glyphCount > 0 ? minGlyphY : 0,
            MaxGlyphY: glyphCount > 0 ? maxGlyphY : 0,
            BackgroundCenterX: bgCenterX,
            BackgroundCenterY: bgCenterY,
            GlyphCenterX: glyphCenterX,
            GlyphCenterY: glyphCenterY);
    }

    private sealed record RenderAnalysis(
        int CanvasWidth,
        int CanvasHeight,
        int DarkBackgroundPixelCount,
        int WhiteGlyphPixelCount,
        int WhiteGlyphsInsideBackgroundBox,
        int MinBgX,
        int MaxBgX,
        int MinBgY,
        int MaxBgY,
        int MinGlyphX,
        int MaxGlyphX,
        int MinGlyphY,
        int MaxGlyphY,
        double BackgroundCenterX,
        double BackgroundCenterY,
        double GlyphCenterX,
        double GlyphCenterY)
    {
        public int GlyphWidth => WhiteGlyphPixelCount > 0 ? MaxGlyphX - MinGlyphX + 1 : 0;
        public int GlyphHeight => WhiteGlyphPixelCount > 0 ? MaxGlyphY - MinGlyphY + 1 : 0;
        public int BackgroundWidth => DarkBackgroundPixelCount > 0 ? MaxBgX - MinBgX + 1 : 0;
        public int BackgroundHeight => DarkBackgroundPixelCount > 0 ? MaxBgY - MinBgY + 1 : 0;
    }
}
