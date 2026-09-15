using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class ThemeContrastTests
{
    private static readonly ThemeColor DarkMica = ThemeColor.Rgb(0x20, 0x20, 0x20);
    private static readonly ThemeColor LightMica = ThemeColor.Rgb(0xF3, 0xF3, 0xF3);

    [Fact]
    public void EveryPaletteKeepsPrimarySecondaryAndMutedTextReadable()
    {
        foreach (AppTheme theme in Enum.GetValues<AppTheme>())
        {
            IReadOnlyDictionary<string, ThemeColor> colors = ThemeCatalog.ColorsFor(theme);
            ThemeColor surface = colors["Surface.Base"];
            ThemeColor raised = colors["Surface.Raised"];
            Assert.True(
                ThemeContrast.Ratio(colors["Text.Primary"], Opaque(surface)) >= 7.0,
                $"{theme} Text.Primary on Base");
            Assert.True(
                ThemeContrast.Ratio(colors["Text.Secondary"], Opaque(surface)) >= 4.5,
                $"{theme} Text.Secondary on Base");
            Assert.True(
                ThemeContrast.Ratio(colors["Text.Muted"], Opaque(raised)) >= 4.5,
                $"{theme} Text.Muted on Raised");
        }
    }

    [Theory]
    [InlineData(AppTheme.Glass)]
    [InlineData(AppTheme.GlassLight)]
    public void GlassTextStaysReadableOnLightAndDarkMica(AppTheme theme)
    {
        IReadOnlyDictionary<string, ThemeColor> colors = ThemeCatalog.ColorsFor(theme);
        ThemeColor text = colors["Text.Primary"];
        ThemeColor surface = colors["Surface.Base"];
        Assert.True(ThemeContrast.RatioOverBackdrop(text, surface, DarkMica) >= 4.5, $"{theme} on dark mica");
        Assert.True(ThemeContrast.RatioOverBackdrop(text, surface, LightMica) >= 4.5, $"{theme} on light mica");
    }

    private static ThemeColor Opaque(ThemeColor color) => ThemeColor.Rgb(color.R, color.G, color.B);
}
