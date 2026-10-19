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
        foreach (string key in new[] { "Text.Primary", "Text.Secondary", "Text.Muted" })
        {
            ThemeColor text = colors[key];
            ThemeColor surface = colors["Surface.Base"];
            Assert.True(ThemeContrast.RatioOverBackdrop(text, surface, DarkMica) >= 4.5, $"{theme} {key} on dark mica");
            Assert.True(ThemeContrast.RatioOverBackdrop(text, surface, LightMica) >= 4.5, $"{theme} {key} on light mica");
        }
    }

    /// <summary>
    /// Every surface that carries text (canvas, base, sidebar/raised, overlay controls) must
    /// keep AA contrast for its intended text colour, in every palette. Workspace resolves to
    /// its dark video-editor palette or the Daylight gallery palette per role, and glass
    /// composites over both Mica extremes.
    /// </summary>
    [Fact]
    public void EveryTextSurfacePairMeetsAaInEveryPalette()
    {
        (string Text, string Surface)[] pairs =
        [
            ("Text.Primary", "Surface.Canvas"),
            ("Text.Primary", "Surface.Base"),
            ("Text.Primary", "Surface.Raised"),
            ("Text.Primary", "Surface.Overlay"),
            ("Text.Secondary", "Surface.Base"),
            ("Text.Secondary", "Surface.Raised"),
            ("Text.Secondary", "Surface.Overlay"),
            ("Text.Muted", "Surface.Base"),
            ("Text.Muted", "Surface.Raised"),
            ("Text.Muted", "Surface.Overlay"),
        ];

        foreach (AppTheme theme in Enum.GetValues<AppTheme>())
        {
            IReadOnlyDictionary<string, ThemeColor> colors = ThemeCatalog.ColorsFor(theme);
            foreach ((string textKey, string surfaceKey) in pairs)
            {
                double ratio = AppThemeNames.UsesBackdrop(theme)
                    ? ThemeContrast.RatioOverBackdrop(
                        colors[textKey],
                        colors[surfaceKey],
                        theme == AppTheme.GlassLight ? LightMica : DarkMica)
                    : ThemeContrast.Ratio(colors[textKey], Opaque(colors[surfaceKey]));
                Assert.True(ratio >= 4.5, $"{theme} {textKey} on {surfaceKey} was {ratio:0.00}");
            }
        }
    }

    /// <summary>
    /// Library folder cards sit on the window base like capture cards (Surface.Raised) but use their
    /// own warm surface, outline and glyph. Text stays AA, the glyph and outline stay visible as
    /// UI graphics, and the card fill is visibly different from a capture card in every palette.
    /// The window base is composited over WPF's black client clear, as the window shows it.
    /// </summary>
    [Fact]
    public void FolderCardsStayReadableAndDistinctFromCaptureCardsInEveryPalette()
    {
        ThemeColor clear = ThemeColor.Rgb(0, 0, 0);
        foreach (AppTheme theme in Enum.GetValues<AppTheme>())
        {
            IReadOnlyDictionary<string, ThemeColor> colors = ThemeCatalog.ColorsFor(theme);
            ThemeColor windowBase = ThemeContrast.Composite(colors["Surface.Base"], clear);
            ThemeColor folder = ThemeContrast.Composite(colors["Surface.Folder"], windowBase);
            ThemeColor capture = ThemeContrast.Composite(colors["Surface.Raised"], windowBase);
            foreach (string key in new[] { "Text.Primary", "Text.Secondary", "Text.Muted" })
                Assert.True(ThemeContrast.Ratio(colors[key], folder) >= 4.5, $"{theme} {key} on Surface.Folder");
            Assert.True(ThemeContrast.Ratio(ThemeContrast.Composite(colors["Accent.Folder"], folder), folder) >= 3.0,
                $"{theme} Accent.Folder glyph");
            ThemeColor outline = ThemeContrast.Composite(colors["Border.Folder"], windowBase);
            Assert.True(ThemeContrast.Ratio(outline, windowBase) >= 1.5, $"{theme} Border.Folder outline");
            int distance = Math.Abs(folder.R - capture.R) + Math.Abs(folder.G - capture.G) + Math.Abs(folder.B - capture.B);
            bool distinctOutline = colors["Border.Folder"] != colors["Border.Subtle"];
            Assert.True(distance >= 24 && distinctOutline, $"{theme} folder card must differ from a capture card ({distance})");
        }
    }

    [Fact]
    public void TextHierarchyHasDistinctStepsAndMeetsAaOnEveryTextSurface()
    {
        foreach (AppTheme theme in Enum.GetValues<AppTheme>())
        {
            var colors = ThemeCatalog.ColorsFor(theme);
            foreach (string surface in new[] { "Surface.Canvas", "Surface.Base", "Surface.Raised", "Surface.Overlay",
                         "Surface.Floating", "Surface.Sunken", "Surface.Hover", "Surface.Pressed", "Surface.Selected", "Surface.Folder" })
            foreach (ThemeColor backdrop in new[] { ThemeColor.Rgb(0, 0, 0), ThemeColor.Rgb(255, 255, 255) })
            {
                ThemeColor background = ThemeContrast.Composite(colors[surface], backdrop);
                double primary = ThemeContrast.Ratio(colors["Text.Primary"], background);
                double secondary = ThemeContrast.Ratio(colors["Text.Secondary"], background);
                double muted = ThemeContrast.Ratio(colors["Text.Muted"], background);
                Assert.True(muted >= 4.5, $"{theme}/{surface}: muted {muted:F2}");
                Assert.True(primary - secondary >= 1.25, $"{theme}/{surface}: primary/secondary gap {primary - secondary:F2}");
                Assert.True(secondary - muted >= 1.25, $"{theme}/{surface}: secondary/muted gap {secondary - muted:F2}");
            }
        }
    }

    [Fact]
    public void SelectionFocusAndBadgesUseNeutralRoles()
    {
        foreach (AppTheme theme in Enum.GetValues<AppTheme>())
        {
            var colors = ThemeCatalog.ColorsFor(theme);
            foreach (string key in new[] { "Border.Focus", "Border.Accent", "Overlay.SelectionBorder", "Accent.Cool", "Accent.Indicator", "Text.Badge" })
                Assert.Equal(colors["Text.Primary"], colors[key]);
            Assert.Equal(colors["Surface.Selected"], colors["Accent.Subtle"]);
            Assert.Equal(colors["Surface.Raised"], colors["Surface.Badge"]);
            Assert.Equal(colors["Surface.Raised"], colors["Surface.BadgeStrong"]);
        }
    }

    [Fact]
    public void ColoredTimelineLayerTextMeetsAaInEveryPalette()
    {
        foreach (AppTheme theme in Enum.GetValues<AppTheme>())
        {
            var colors = ThemeCatalog.ColorsFor(theme);
            foreach (string surface in new[] { "Timeline.TextLayer", "Timeline.FrameLayer" })
            {
                double ratio = ThemeContrast.Ratio(colors["Timeline.LayerText"], colors[surface]);
                Assert.True(ratio >= 4.5, $"{theme}/Timeline.LayerText on {surface}: {ratio:F4}");
            }
        }
    }

    [Fact]
    public void TextSelectionHasAnOpaqueDistinctBackgroundAndReadableText()
    {
        foreach (AppTheme theme in Enum.GetValues<AppTheme>())
        {
            var colors = ThemeCatalog.ColorsFor(theme);
            ThemeColor background = colors["Text.SelectionBackground"];
            ThemeColor foreground = colors["Text.SelectionForeground"];
            Assert.Equal(255, background.A);
            Assert.Equal(255, foreground.A);
            Assert.True(ThemeContrast.Ratio(foreground, background) >= 4.5, $"{theme} selected text");
            foreach (ThemeColor backdrop in new[] { ThemeColor.Rgb(0, 0, 0), ThemeColor.Rgb(255, 255, 255) })
                Assert.True(ThemeContrast.RatioOverBackdrop(background, colors["Surface.Sunken"], backdrop) >= 3,
                    $"{theme} selection must remain visible against the input surface");
        }
    }

    [Fact]
    public void WindowsSelectionAndTimelineTextUsePairedSystemRoles()
    {
        Assert.Equal(SystemColorRole.Highlight, HighContrastPalette.RoleFor("Text.SelectionBackground"));
        Assert.Equal(SystemColorRole.HighlightText, HighContrastPalette.RoleFor("Text.SelectionForeground"));
        Assert.Equal(SystemColorRole.Highlight, HighContrastPalette.RoleFor("Timeline.TextLayer"));
        Assert.Equal(SystemColorRole.Highlight, HighContrastPalette.RoleFor("Timeline.FrameLayer"));
        Assert.Equal(SystemColorRole.HighlightText, HighContrastPalette.RoleFor("Timeline.LayerText"));
    }
    private static ThemeColor Opaque(ThemeColor color) => ThemeColor.Rgb(color.R, color.G, color.B);
}
