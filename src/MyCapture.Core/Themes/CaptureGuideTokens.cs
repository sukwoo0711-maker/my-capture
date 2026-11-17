using System.Globalization;
using MyCapture.Core.Settings;

namespace MyCapture.Core.Themes;

/// <summary>Semantic guide tokens shared by all six palettes and the independent brand override.</summary>
public static class CaptureGuideTokens
{
    public static IReadOnlyDictionary<string, ThemeColor> ColorsFor(AppTheme theme, string? customColor = null,
        AppAccent accentColor = AppAccent.Violet)
    {
        IReadOnlyDictionary<string, ThemeColor> palette = ThemeCatalog.ColorsFor(theme, accentColor);
        ThemeColor accent = palette["Overlay.SelectionBorder"];
        if (CaptureGuideColorValue.TryNormalize(customColor, out string normalized))
        {
            uint rgb = uint.Parse(normalized.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            accent = ThemeColor.Rgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        }
        // A neutral keyline keeps a custom dark/light crosshair visible on image pixels.
        bool lightAccent = accent.R * 299 + accent.G * 587 + accent.B * 114 >= 140000;
        ThemeColor contrast = lightAccent ? ThemeColor.Rgb(0, 0, 0) : ThemeColor.Rgb(255, 255, 255);
        // The chips' outline, divider and key-cap roles are calm neutrals of the chip's own lightness,
        // never the guide colour: one accent (the chosen mode) is enough on the selector.
        bool lightChip = IsLightSurface(palette["Surface.Floating"]);
        return new Dictionary<string, ThemeColor>(StringComparer.Ordinal)
        {
            ["CaptureGuide.SelectionBorder"] = accent,
            ["CaptureGuide.InnerGlow"] = accent with { A = 32 },
            ["CaptureGuide.Line"] = accent with { A = 120 },
            ["CaptureGuide.LineShadow"] = contrast with { A = 64 },
            ["CaptureGuide.Crosshair"] = accent,
            ["CaptureGuide.CrosshairShadow"] = contrast,
            ["CaptureGuide.StartPoint"] = accent,
            ["CaptureGuide.ChipSurface"] = palette["Surface.Floating"],
            ["CaptureGuide.ChipBorder"] = lightChip ? ThemeColor.Rgb(0xCB, 0xD5, 0xE1) : ThemeColor.Rgb(0x2E, 0x3B, 0x4E),
            ["CaptureGuide.ChipHairline"] = lightChip ? ThemeColor.Rgb(0xE2, 0xE8, 0xF0) : ThemeColor.Rgb(0x26, 0x32, 0x44),
            ["CaptureGuide.KeycapSurface"] = lightChip ? ThemeColor.Rgb(0xF1, 0xF5, 0xF9) : ThemeColor.Rgb(0x1E, 0x29, 0x3B),
            ["CaptureGuide.Text"] = palette["Text.Primary"],
            ["CaptureGuide.MutedText"] = palette["Text.Secondary"],
            ["CaptureGuide.MagnifierBorder"] = accent,
            ["CaptureGuide.MagnifierGrid"] = contrast with { A = 50 },
            ["CaptureGuide.Dimmer"] = palette["Overlay.Dimmer"] with { A = 140 },
            ["CaptureGuide.AccentText"] = ReadableTextOn(accent),
        };
    }

    /// <summary>
    /// True when the chip surface, as the opaque selector chrome shows it, is light: the chrome
    /// windows paint the surface's own colour under it, so its alpha does not change the answer.
    /// </summary>
    private static bool IsLightSurface(ThemeColor surface) =>
        ThemeContrast.RelativeLuminance(surface with { A = 255 }) >= 0.5;

    /// <summary>
    /// Black or white, whichever has the higher WCAG contrast on <paramref name="accent"/>. One of
    /// them always reaches at least 4.58:1 on an opaque colour, so accent-filled labels stay readable
    /// for every palette and any custom guide colour.
    /// </summary>
    public static ThemeColor ReadableTextOn(ThemeColor accent)
    {
        ThemeColor opaque = accent with { A = 255 };
        ThemeColor black = ThemeColor.Rgb(0, 0, 0);
        ThemeColor white = ThemeColor.Rgb(255, 255, 255);
        return ThemeContrast.Ratio(black, opaque) >= ThemeContrast.Ratio(white, opaque) ? black : white;
    }
}
