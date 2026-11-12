using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Annotations;
using MyCapture.Core.Serialization;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.Core.Tests;

/// <summary>
/// The pixel math of a watermark blended into the page, and the stamp settings that choose it.
/// </summary>
public sealed class WatermarkInkTests
{
    [Theory]
    [InlineData(0.3, 0.30)]
    [InlineData(0.0, 0.00)]
    [InlineData(1.0, 1.00)]
    public void DarkInkIsMultiplyAndLightInkIsScreen(double ink, double expectedAlpha)
    {
        (double grey, double alpha) = WatermarkInk.Overlay(ink, darkShare: 1);
        Assert.Equal(expectedAlpha, alpha, 6);
        Assert.Equal(0, grey, 6);
        // Black at alpha w over any b equals b multiplied by 1 - w: black stays black.
        foreach (double b in new[] { 0.0, 0.2, 1.0 })
            Assert.Equal(b * (1 - ink), (b * (1 - alpha)) + (grey * alpha), 6);

        (grey, alpha) = WatermarkInk.Overlay(ink, darkShare: 0);
        Assert.Equal(expectedAlpha, alpha, 6);
        if (alpha > 0) Assert.Equal(1, grey, 6);
        // White at alpha w over any b equals b screened with w: white stays white.
        foreach (double b in new[] { 0.0, 0.2, 1.0 })
            Assert.Equal(1 - ((1 - b) * (1 - ink)), (b * (1 - alpha)) + (grey * alpha), 6);
    }

    [Fact]
    public void TheDarkShareIsOneOnLightPagesZeroOnDarkOnesAndSmoothBetween()
    {
        Assert.Equal(1, WatermarkInk.DarkShare(1.0));
        Assert.Equal(1, WatermarkInk.DarkShare(WatermarkInk.LightPageAbove));
        Assert.Equal(0, WatermarkInk.DarkShare(0.12));
        Assert.Equal(0, WatermarkInk.DarkShare(WatermarkInk.DarkPageBelow));
        double previous = 0;
        for (double l = WatermarkInk.DarkPageBelow; l <= WatermarkInk.LightPageAbove; l += 0.01)
        {
            double share = WatermarkInk.DarkShare(l);
            Assert.True(share >= previous);
            previous = share;
        }
    }

    [Fact]
    public void ADarkLogoOnTransparencyInksItsDarkPartsAndLeavesItsWhiteLettersAsPaper()
    {
        // 6x1: transparent, three navy, one white letter pixel, transparent.
        byte[] navy = [0x80, 0x20, 0x10, 255];
        byte[] bgra = [0, 0, 0, 0, .. navy, .. navy, .. navy, 255, 255, 255, 255, 0, 0, 0, 0];
        float[] ink = WatermarkInk.Compute(bgra, 6, 1, 24);
        Assert.Equal([0f, 1f, 1f, 1f, 0f, 0f], ink);
    }

    [Fact]
    public void AWhiteLogoOnTransparencyKeepsItsShape()
    {
        byte[] bgra = [0, 0, 0, 0, 255, 255, 255, 255, 255, 255, 255, 128, 0, 0, 0, 0];
        float[] ink = WatermarkInk.Compute(bgra, 4, 1, 16);
        Assert.Equal(0f, ink[0]);
        Assert.Equal(1f, ink[1]);
        Assert.InRange(ink[2], 0.49f, 0.51f);
    }

    [Fact]
    public void ALogoOnAnOpaqueWhitePlateLosesThePlate()
    {
        // 3x3 white plate with a black centre.
        byte[] bgra = new byte[3 * 3 * 4];
        Array.Fill(bgra, (byte)255);
        bgra[(4 * 4) + 0] = bgra[(4 * 4) + 1] = bgra[(4 * 4) + 2] = 0;
        float[] ink = WatermarkInk.Compute(bgra, 3, 3, 12);
        Assert.Equal(1f, ink[4]);
        Assert.All(ink.Where((_, i) => i != 4), value => Assert.Equal(0f, value));
    }

    [Fact]
    public void ThePageBrightnessIsALocalAverageThatThinTextDoesNotFlip()
    {
        // 200x100 white with one black 2-pixel line, and a dark right quarter.
        const int width = 200, height = 100;
        byte[] bgra = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte v = x >= 150 ? (byte)0x1E : (y is 50 or 51 && x < 120 ? (byte)0 : (byte)255);
                int o = ((y * width) + x) * 4;
                bgra[o] = bgra[o + 1] = bgra[o + 2] = v;
                bgra[o + 3] = 255;
            }
        }

        PageBrightness page = PageBrightness.From(bgra, width, height, width * 4);
        Assert.Equal(1, WatermarkInk.DarkShare(page.At(60, 50)));
        Assert.Equal(0, WatermarkInk.DarkShare(page.At(190, 50)));
    }

    [Fact]
    public void ThePageReadInBandsGivesTheSameMapAsTheWholePage()
    {
        const int width = 300, height = 170;
        byte[] bgra = new byte[width * height * 4];
        var random = new Random(7);
        random.NextBytes(bgra);

        PageBrightness whole = PageBrightness.From(bgra, width, height, width * 4);
        var builder = new PageBrightness.Builder(width, height);
        for (int y = 0; y < height; y += 32)
        {
            int count = Math.Min(32, height - y);
            builder.AddRows(bgra.AsSpan(y * width * 4, count * width * 4), y, count, width * 4);
        }

        PageBrightness banded = builder.Build();
        for (int y = 0; y < height; y += 7)
            for (int x = 0; x < width; x += 7)
                Assert.Equal(whole.At(x, y), banded.At(x, y), 6);
    }

    [Fact]
    public void TheGridFillMatchesTheOverlayAtEveryNodeAndBlendsBetween()
    {
        const int width = 33, height = 17, step = 8;
        int columns = WatermarkInk.GridCount(width, step);
        int rows = WatermarkInk.GridCount(height, step);
        Assert.Equal(5, columns);
        Assert.Equal(32, WatermarkInk.GridNode(columns - 1, width, step));
        float[] ink = Enumerable.Repeat(0.5f, width * height).ToArray();
        float[] shares = new float[columns * rows];
        for (int gy = 0; gy < rows; gy++)
            for (int gx = 0; gx < columns; gx++)
                shares[(gy * columns) + gx] = gx % 2 == 0 ? 1f : 0f;
        byte[] grid = new byte[width * height * 4];

        WatermarkInk.FillGrid(ink, width, height, shares, columns, rows, step, grid);

        for (int gx = 0; gx < columns; gx++)
        {
            int x = WatermarkInk.GridNode(gx, width, step);
            (double grey, double alpha) = WatermarkInk.Overlay(0.5, gx % 2 == 0 ? 1 : 0);
            Assert.InRange(grid[(x * 4) + 3], (int)Math.Round(alpha * 255) - 1, (int)Math.Round(alpha * 255) + 1);
            Assert.InRange(grid[x * 4], (int)Math.Round(grey * alpha * 255) - 1, (int)Math.Round(grey * alpha * 255) + 1);
        }

        // Halfway between a dark node and a light one the share is one half.
        (double midGrey, double midAlpha) = WatermarkInk.Overlay(0.5, 0.5);
        Assert.InRange(grid[(4 * 4) + 3], (int)Math.Round(midAlpha * 255) - 1, (int)Math.Round(midAlpha * 255) + 1);
        Assert.InRange(grid[4 * 4], (int)Math.Round(midGrey * midAlpha * 255) - 1, (int)Math.Round(midGrey * midAlpha * 255) + 1);

        byte[] uniform = new byte[width * height * 4];
        byte[] flat = new byte[width * height * 4];
        WatermarkInk.FillUniform(ink, 1, uniform);
        WatermarkInk.FillGrid(ink, width, height, Enumerable.Repeat(1f, columns * rows).ToArray(), columns, rows, step, flat);
        Assert.Equal(uniform, flat);
    }

    [Fact]
    public void ANewInstallBlendsTheStampIntoThePageAtTwentyPercentAndKeepsTheOverOpacity()
    {
        StampSettings stamp = new AppSettings().Annotation.Stamp;
        Assert.Equal(ImageBlend.IntoPage, stamp.Blend);
        Assert.Equal(20, stamp.PageOpacityPercent);
        Assert.Equal(100, stamp.OpacityPercent);
        Assert.Equal(20, stamp.CurrentOpacityPercent);
        stamp.Blend = ImageBlend.Normal;
        Assert.Equal(100, stamp.CurrentOpacityPercent);
    }

    [Fact]
    public void ASettingsFileFromBeforeBlendingKeepsItsOpacityAndBlendsFromNowOn()
    {
        using var workspace = new TempWorkspace();
        workspace.Paths.EnsureCreated();
        File.WriteAllText(workspace.Paths.SettingsFile,
            "{\"annotation\":{\"stamp\":{\"anchor\":\"topLeft\",\"marginX\":40,\"marginY\":12,\"widthFraction\":0.25,\"opacityPercent\":70}}}");
        var store = new SettingsStore(workspace.Paths, NullLogger<SettingsStore>.Instance);

        StampSettings stamp = store.Load().Annotation.Stamp;

        Assert.Equal(StampAnchor.TopLeft, stamp.Anchor);
        Assert.Equal(70, stamp.OpacityPercent);
        Assert.Equal(ImageBlend.IntoPage, stamp.Blend);
        Assert.Equal(20, stamp.PageOpacityPercent);
    }

    [Fact]
    public void TheWayAndPageStrengthRoundTripCloneAndRepair()
    {
        using var workspace = new TempWorkspace();
        var store = new SettingsStore(workspace.Paths, NullLogger<SettingsStore>.Instance);
        var settings = new AppSettings();
        settings.Annotation.Stamp.Blend = ImageBlend.Normal;
        settings.Annotation.Stamp.PageOpacityPercent = 50;

        store.Save(settings);
        StampSettings loaded = store.Load().Annotation.Stamp;
        Assert.Equal(ImageBlend.Normal, loaded.Blend);
        Assert.Equal(50, loaded.PageOpacityPercent);
        Assert.Contains("\"normal\"", File.ReadAllText(workspace.Paths.SettingsFile), StringComparison.Ordinal);

        StampSettings clone = loaded.Clone();
        Assert.Equal(ImageBlend.Normal, clone.Blend);
        Assert.Equal(50, clone.PageOpacityPercent);

        var draft = new SettingsDraft(settings);
        Assert.False(draft.HasUnappliedChanges);
        Assert.Equal(ImageBlend.Normal, draft.ToAppSettings().Annotation.Stamp.Blend);

        var bad = new StampSettings { Blend = (ImageBlend)42, PageOpacityPercent = 3 };
        bad.Normalize();
        Assert.Equal(ImageBlend.IntoPage, bad.Blend);
        Assert.Equal(StampSettings.MinOpacityPercent, bad.PageOpacityPercent);

        var parsed = JsonSerializer.Deserialize<AppSettings>("{\"annotation\":{\"stamp\":{\"blend\":\"intoPage\",\"pageOpacityPercent\":20}}}", JsonDefaults.Readable)!;
        Assert.Equal(20, parsed.Annotation.Stamp.PageOpacityPercent);
    }
}
