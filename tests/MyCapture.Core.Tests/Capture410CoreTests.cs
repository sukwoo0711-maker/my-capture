using System.Text.Json;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class Capture410CoreTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    public void AutoTrim_FindsSolidAndRepeatingMargins_WithNonAlignedImageEdges(int period)
    {
        const int width = 101, height = 79;
        var content = new RectD(20, 18, 62, 42);
        byte[] pixels = Pattern(width, height, period, content);
        Assert.Equal(content, AutomaticMarginTrim.Find(pixels, width, height, width * 4));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    public void AutoTrim_EntirelyUniformOrRepeatingImageIsUnchanged(int period)
    {
        byte[] pixels = Pattern(101, 79, period, null);
        Assert.Equal(new RectD(0, 0, 101, 79), AutomaticMarginTrim.Find(pixels, 101, 79, 404));
    }

    [Theory]
    [InlineData(0, 1)] [InlineData(1, 1)] [InlineData(2, 1)] [InlineData(3, 1)]
    [InlineData(0, 8)] [InlineData(1, 8)] [InlineData(2, 8)] [InlineData(3, 8)]
    public void AutoTrim_HandlesPaddingOnOnlyOneEdge(int side, int period)
    {
        RectD content = side switch
        {
            0 => new RectD(0, 24, 120, 96),
            1 => new RectD(0, 0, 120, 96),
            2 => new RectD(24, 0, 96, 120),
            _ => new RectD(0, 0, 96, 120),
        };
        byte[] pixels = Pattern(120, 120, period, content);
        Assert.Equal(content, AutomaticMarginTrim.Find(pixels, 120, 120, 480));
    }

    [Fact]
    public void AutoTrim_PatternPhaseDoesNotAlias_AndLargeFlatPictureAreasStayIntact()
    {
        var content = new RectD(20, 18, 62, 42);
        Assert.Equal(content, AutomaticMarginTrim.Find(Pattern(101, 82, 8, content), 101, 82, 404));
        byte[] halves = Pattern(120, 120, 1, new RectD(0, 60, 120, 60));
        Assert.Equal(new RectD(0, 0, 120, 120), AutomaticMarginTrim.Find(halves, 120, 120, 480));
    }

    [Fact]
    public void AutoTrim_FullContentIsUnchanged_AndNoNearBackgroundPixelIsDiscarded()
    {
        const int width = 80, height = 60;
        byte[] pixels = Pattern(width, height, 1, new RectD(12, 10, 56, 40));
        // A one-channel difference is content, not a tolerance-based near-background match.
        pixels[(height / 2 * width) * 4] = 254;
        pixels[(height / 2 * width + width - 1) * 4] = 254;
        pixels[(width / 2) * 4] = 254;
        pixels[((height - 1) * width + width / 2) * 4] = 254;
        Assert.Equal(new RectD(0, 0, width, height), AutomaticMarginTrim.Find(pixels, width, height, width * 4));
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                pixels[(y * width + x) * 4] = (byte)((x * 19 + y * 37) % 251);
        Assert.Equal(new RectD(0, 0, width, height), AutomaticMarginTrim.Find(pixels, width, height, width * 4));
    }

    [Fact]
    public void AutoTrim_TransparentBackgroundIgnoresHiddenRgb_AndPreservesAlphaContent()
    {
        byte[] pixels = new byte[80 * 60 * 4];
        for (int i = 0; i < pixels.Length; i += 4) pixels[i] = (byte)(i % 251);
        for (int y = 12; y < 48; y++)
            for (int x = 10; x < 70; x++) pixels[(y * 80 + x) * 4 + 3] = 1;
        Assert.Equal(new RectD(10, 12, 60, 36), AutomaticMarginTrim.Find(pixels, 80, 60, 320));
    }

    [Fact]
    public void AutoTrim_RejectsInvalidStorage_AndHonorsCancellation()
    {
        Assert.Throws<ArgumentException>(() => AutomaticMarginTrim.Find(new byte[4], 80, 60, 320));
        Assert.Throws<ArgumentException>(() => AutomaticMarginTrim.Find(new byte[320], 80, 1, 1));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            AutomaticMarginTrim.Find(Pattern(80, 60, 1, null), 80, 60, 320, cancellation.Token));
    }

    [Theory]
    [InlineData(0, 0, CropGrip.TopLeft)]
    [InlineData(160, 0, CropGrip.Top)]
    [InlineData(640, 0, CropGrip.TopRight)]
    [InlineData(640, 100, CropGrip.Right)]
    [InlineData(640, 360, CropGrip.BottomRight)]
    [InlineData(160, 360, CropGrip.Bottom)]
    [InlineData(0, 360, CropGrip.BottomLeft)]
    [InlineData(0, 100, CropGrip.Left)]
    public void EveryEdgeAndCornerStartsTheExpectedGrip(double x, double y, CropGrip expected)
    {
        var bounds = new RectD(0, 0, 640, 360);
        Assert.Equal(expected, CropSelection.HitTestEdge(bounds, new PointD(x, y), 7));
        var crop = new CropSelection(640, 360);
        crop.Begin(new PointD(x, y), 7);
        Assert.Equal(expected, crop.Grip);
    }

    [Fact]
    public void InteriorAndDistantPointsDoNotStealAnnotationGestures()
    {
        var bounds = new RectD(0, 0, 640, 360);
        Assert.Null(CropSelection.HitTestEdge(bounds, new PointD(30, 30), 7));
        Assert.Null(CropSelection.HitTestEdge(bounds, new PointD(-30, 100), 7));
        Assert.Null(CropSelection.HitTestEdge(bounds, new PointD(double.NaN, 0), 7));
        var crop = new CropSelection(640, 360);
        Assert.Equal(CropGrip.Move, crop.HitTest(new PointD(100, 100), 7));
        crop.Begin(new PointD(0, 100), 7);
        crop.Drag(new PointD(40, 120), false);
        crop.End();
        Assert.Equal(new RectD(40, 0, 600, 360), crop.Region);
    }

    [Theory]
    [InlineData("#12abEF", "#12ABEF")]
    [InlineData("12abef", "#12ABEF")]
    [InlineData("  #ffffff  ", "#FFFFFF")]
    public void HexColorNormalizesOnlyOpaqueRgb(string input, string expected)
    {
        Assert.True(CaptureGuideColorValue.TryNormalize(input, out string actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("")]
    [InlineData("#FFF")]
    [InlineData("#80123456")]
    [InlineData("red")]
    [InlineData("#GG0000")]
    [InlineData("#-12345")]
    public void InvalidHexDoesNotBecomeAColor(string input) =>
        Assert.False(CaptureGuideColorValue.TryNormalize(input, out _));

    [Fact]
    public void GuideSettingIsAdditiveClonedSerializedAndMappedWithoutTouchingTheme()
    {
        AppSettings legacy = JsonSerializer.Deserialize<AppSettings>("{\"General\":{\"Theme\":\"daylight\"}}")!;
        Assert.Equal(string.Empty, legacy.General.CaptureGuideColor);
        legacy.General.CaptureGuideColor = "#123ABC";
        AppSettings clone = legacy.DeepClone();
        Assert.Equal("#123ABC", clone.General.CaptureGuideColor);
        clone.General.CaptureGuideColor = "#FF0000";
        Assert.Equal("#123ABC", legacy.General.CaptureGuideColor);
        AppSettings restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(legacy))!;
        var draft = new SettingsDraft(restored);
        Assert.Equal("custom", draft.CaptureGuideColorMode);
        Assert.Equal("#123ABC", draft.ToAppSettings().General.CaptureGuideColor);
        Assert.Equal("glass-light", draft.ToAppSettings().General.Theme);
        draft.CaptureGuideColorMode = "theme";
        Assert.Equal(string.Empty, draft.ToAppSettings().General.CaptureGuideColor);
        draft.ResetToDefaults();
        Assert.Equal("theme", draft.CaptureGuideColorMode);
    }

    [Fact]
    public void InvalidCustomDraftBlocksMapping_AndThemeDefaultClearsOnlyThatError()
    {
        var draft = new SettingsDraft(new AppSettings()) { CaptureGuideColorMode = "custom", CaptureGuideColorHex = "#ZZ0000" };
        Assert.True(draft.HasErrors);
        Assert.Throws<InvalidOperationException>(() => draft.ToAppSettings());
        draft.CaptureGuideColorMode = "theme";
        Assert.False(draft.HasErrors);
        draft.CaptureGuideColorMode = "custom";
        Assert.True(draft.HasErrors);
        draft.CaptureGuideColorHex = "00fF88";
        Assert.False(draft.HasErrors);
        Assert.Equal("#00FF88", draft.ToAppSettings().General.CaptureGuideColor);
    }

    [Fact]
    public void EveryThemeProvidesEveryGuideRole_AndCustomColorDoesNotReplaceTextOrSurfaces()
    {
        foreach (AppTheme theme in Enum.GetValues<AppTheme>())
        {
            var normal = CaptureGuideTokens.ColorsFor(theme);
            var custom = CaptureGuideTokens.ColorsFor(theme, "#123456");
            Assert.Equal(15, normal.Count);
            Assert.Equal(ThemeCatalog.ColorsFor(theme)["Overlay.SelectionBorder"], normal["CaptureGuide.SelectionBorder"]);
            foreach (string role in new[] { "SelectionBorder", "Crosshair", "StartPoint", "ChipBorder", "MagnifierBorder" })
                Assert.Equal(ThemeColor.Rgb(0x12, 0x34, 0x56), custom["CaptureGuide." + role]);
            Assert.Equal((byte)32, custom["CaptureGuide.InnerGlow"].A);
            Assert.Equal((byte)120, custom["CaptureGuide.Line"].A);
            Assert.Equal(normal["CaptureGuide.ChipSurface"], custom["CaptureGuide.ChipSurface"]);
            Assert.Equal(normal["CaptureGuide.Text"], custom["CaptureGuide.Text"]);
            Assert.Equal(normal, CaptureGuideTokens.ColorsFor(theme, "invalid"));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("#123456")]
    [InlineData("#FF1234")]
    [InlineData("#FFD000")]
    [InlineData("#767676")] // Mid grey: black and white are nearly tied here.
    [InlineData("#777777")]
    [InlineData("#000000")]
    [InlineData("#FFFFFF")]
    public void AccentTextIsBlackOrWhiteWithAtLeastAaContrastOnTheAccent(string? custom)
    {
        foreach (AppTheme theme in Enum.GetValues<AppTheme>())
        {
            var colors = CaptureGuideTokens.ColorsFor(theme, custom);
            ThemeColor accent = colors["CaptureGuide.SelectionBorder"];
            ThemeColor text = colors["CaptureGuide.AccentText"];
            Assert.True(text == ThemeColor.Rgb(0, 0, 0) || text == ThemeColor.Rgb(255, 255, 255), text.ToString());
            Assert.True(ThemeContrast.Ratio(text, accent) >= 4.5, $"{theme} {custom}: {ThemeContrast.Ratio(text, accent):0.00}");
            ThemeColor other = text.R == 0 ? ThemeColor.Rgb(255, 255, 255) : ThemeColor.Rgb(0, 0, 0);
            Assert.True(ThemeContrast.Ratio(text, accent) >= ThemeContrast.Ratio(other, accent));
        }
    }

    [Fact]
    public void AccentTextChoiceCoversEveryOpaqueAccent()
    {
        // Exhaustive over a 6-bit cube: the better of black and white never drops below 4.5:1.
        for (int r = 0; r < 256; r += 4)
            for (int g = 0; g < 256; g += 4)
                for (int b = 0; b < 256; b += 4)
                {
                    ThemeColor accent = ThemeColor.Rgb((byte)r, (byte)g, (byte)b);
                    Assert.True(ThemeContrast.Ratio(CaptureGuideTokens.ReadableTextOn(accent), accent) >= 4.5);
                }
    }

    private static byte[] Pattern(int width, int height, int period, RectD? content)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                byte background = period == 1 || (x % period < period / 2) == (y % period < period / 2) ? (byte)255 : (byte)220;
                bool inside = content is RectD r && x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom;
                pixels[i] = inside ? (byte)25 : background;
                pixels[i + 1] = inside ? (byte)80 : background;
                pixels[i + 2] = inside ? (byte)160 : background;
                pixels[i + 3] = 255;
            }
        return pixels;
    }
}