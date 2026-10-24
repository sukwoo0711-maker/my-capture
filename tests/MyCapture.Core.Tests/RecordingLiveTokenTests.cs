using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.Core.Tests;

/// <summary>
/// One red marks a live recording: the region frame, the compact bar's dot and the region
/// selector's cue all read <c>Recording.Live</c>, the accent the recording frame used before.
/// </summary>
public sealed class RecordingLiveTokenTests
{
    [Fact]
    public void EveryPaletteGivesLiveRecordingTheAccentColour()
    {
        foreach (AppTheme theme in Enum.GetValues<AppTheme>())
        {
            IReadOnlyDictionary<string, ThemeColor> colors = ThemeCatalog.ColorsFor(theme);
            Assert.Equal(colors["Accent.Default"], colors["Recording.Live"]);
        }

        Assert.Equal(ThemeColor.Rgb(0xE0, 0x45, 0x3C), ThemeCatalog.ColorsFor(AppTheme.Glass)["Recording.Live"]);
        Assert.Equal(ThemeColor.Rgb(0xE0, 0x45, 0x3C), ThemeCatalog.ColorsFor(AppTheme.GlassLight)["Recording.Live"]);
    }

    [Fact]
    public void LiveRecordingMarksStayVisibleOnTheFloatingBar()
    {
        // The dot is a non-text mark (WCAG 1.4.11, 3:1) on the floating recording bar.
        foreach (AppTheme theme in new[] { AppTheme.Glass, AppTheme.GlassLight })
        {
            IReadOnlyDictionary<string, ThemeColor> colors = ThemeCatalog.ColorsFor(theme);
            ThemeColor floating = colors["Surface.Floating"];
            ThemeColor opaque = ThemeContrast.Composite(floating,
                AppThemeNames.IsLight(theme) ? ThemeColor.Rgb(255, 255, 255) : ThemeColor.Rgb(0, 0, 0));
            double ratio = ThemeContrast.Ratio(colors["Recording.Live"], opaque);
            Assert.True(ratio >= 3, $"{theme}: recording dot contrast {ratio:0.00}");
        }
    }

    [Fact]
    public void WindowsContrastThemesDrawLiveRecordingInTheHighlightColour() =>
        Assert.Equal(SystemColorRole.Highlight, HighContrastPalette.RoleFor("Recording.Live"));
}
