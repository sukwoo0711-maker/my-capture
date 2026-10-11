using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.Core.Tests;

/// <summary>
/// Decorative lines (Border.Subtle) and control outlines (Border.Control) are separate roles, so
/// glass can quiet its panel and separator lines without losing the edge of inputs and buttons.
/// </summary>
public sealed class ThemeBorderRoleTests
{
    private static readonly ThemeColor Clear = ThemeColor.Rgb(0, 0, 0);

    [Fact]
    public void EveryPaletteDefinesAControlOutlineBetweenDecorativeAndStrongBoundaries()
    {
        foreach (AppTheme theme in Enum.GetValues<AppTheme>())
        {
            IReadOnlyDictionary<string, ThemeColor> colors = ThemeCatalog.ColorsFor(theme);
            Assert.True(colors.ContainsKey("Border.Control"), $"{theme} has no Border.Control");
            ThemeColor panel = Panel(colors);
            double subtle = Ratio(colors["Border.Subtle"], panel);
            double control = Ratio(colors["Border.Control"], panel);
            double strong = Ratio(colors["Border.Strong"], panel);

            // WCAG 1.4.11: the outline that identifies an input or bordered button is 3:1 on the
            // panel it sits on. Hover (Strong) never weakens it.
            Assert.True(control >= 3.0, $"{theme} Border.Control on Surface.Raised was {control:0.00}");
            Assert.True(strong >= control, $"{theme} Border.Strong {strong:0.00} is weaker than Border.Control {control:0.00}");
            if (theme != AppTheme.HighContrast)
                Assert.True(subtle < control, $"{theme} decorative line {subtle:0.00} is as strong as a control outline {control:0.00}");
        }
    }

    [Theory]
    [InlineData(AppTheme.Glass)]
    [InlineData(AppTheme.GlassLight)]
    [InlineData(AppTheme.Midnight)]
    [InlineData(AppTheme.Daylight)]
    [InlineData(AppTheme.Workspace)]
    public void DecorativeLinesStayQuiet(AppTheme theme)
    {
        IReadOnlyDictionary<string, ThemeColor> colors = ThemeCatalog.ColorsFor(theme);
        double subtle = Ratio(colors["Border.Subtle"], Panel(colors));
        // Visible as a hairline, but well below the 3:1 a control outline needs.
        Assert.InRange(subtle, 1.3, 2.2);
    }

    [Fact]
    public void GlassDecorativeLineIsAFaintTintOfTheTextColour()
    {
        ThemeColor subtle = ThemeCatalog.ColorsFor(AppTheme.Glass)["Border.Subtle"];
        Assert.InRange(subtle.A, (byte)0x33, (byte)0x3D);
        Assert.Equal((0xF6, 0xF8, 0xFC), (subtle.R, subtle.G, subtle.B));
        Assert.NotEqual(subtle, ThemeCatalog.ColorsFor(AppTheme.Glass)["Border.Control"]);
    }

    [Fact]
    public void EveryPaletteKeyHasAnExplicitContrastThemeRole()
    {
        foreach (AppTheme theme in Enum.GetValues<AppTheme>())
        foreach (string key in ThemeCatalog.ColorsFor(theme).Keys)
            Assert.True(HighContrastPalette.Roles.ContainsKey(key), $"{key} ({theme}) has no contrast-theme role");

        foreach (string key in new[] { "Text.Disabled", "Border.Hover" })
            Assert.True(HighContrastPalette.Roles.ContainsKey(key), key);
    }

    [Fact]
    public void ContrastThemeRolesPairTextWithItsSurface()
    {
        // Text sits on surfaces and washes; the pair a contrast theme guarantees is
        // WindowText on Window. Accent fills carry HighlightText.
        foreach ((string key, SystemColorRole role) in HighContrastPalette.Roles)
        {
            if (key.StartsWith("Text.", StringComparison.Ordinal) && key is not "Text.OnAccent" and not "Text.Disabled")
                Assert.Equal(SystemColorRole.WindowText, role);
            if (key is "Surface.Hover" or "Surface.Pressed" or "Accent.Subtle")
                Assert.Equal(SystemColorRole.Window, role);
        }

        Assert.Equal(SystemColorRole.Highlight, HighContrastPalette.RoleFor("Accent.Default"));
        Assert.Equal(SystemColorRole.HighlightText, HighContrastPalette.RoleFor("Text.OnAccent"));
        Assert.Equal(SystemColorRole.GrayText, HighContrastPalette.RoleFor("Text.Disabled"));
        Assert.Equal(SystemColorRole.Highlight, HighContrastPalette.RoleFor("Border.Focus"));
        Assert.Equal(SystemColorRole.ButtonText, HighContrastPalette.RoleFor("Border.Control"));
        Assert.Equal(SystemColorRole.Window, HighContrastPalette.RoleFor("Surface.Unknown"));
    }

    // Controls sit on Surface.Raised over the window base; glass composites over WPF's black clear.
    private static ThemeColor Panel(IReadOnlyDictionary<string, ThemeColor> colors) =>
        ThemeContrast.Composite(colors["Surface.Raised"], ThemeContrast.Composite(colors["Surface.Base"], Clear));

    private static double Ratio(ThemeColor line, ThemeColor panel) =>
        ThemeContrast.Ratio(ThemeContrast.Composite(line, panel), panel);
}
