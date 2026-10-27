using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.Core.Tests;

/// <summary>
/// One red marks a live recording: the region frame, the compact bar's dot and the region
/// selector's cue all read <c>Recording.Live</c>, the brand red the recording frame always used.
/// The primary fill is a deeper red with white ink, so a recording mark never reads as a button.
/// </summary>
public sealed class RecordingLiveTokenTests
{
    [Fact]
    public void EveryPaletteGivesLiveRecordingTheBrandRedApartFromThePrimaryFill()
    {
        foreach (AppTheme theme in Enum.GetValues<AppTheme>().Where(theme => theme != AppTheme.HighContrast))
        {
            IReadOnlyDictionary<string, ThemeColor> colors = ThemeCatalog.ColorsFor(theme);
            Assert.Equal(ThemeColor.Rgb(0xE0, 0x45, 0x3C), colors["Recording.Live"]);
            Assert.NotEqual(colors["Accent.Default"], colors["Recording.Live"]);
        }

        IReadOnlyDictionary<string, ThemeColor> contrast = ThemeCatalog.ColorsFor(AppTheme.HighContrast);
        Assert.Equal(contrast["Accent.Default"], contrast["Recording.Live"]);
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
