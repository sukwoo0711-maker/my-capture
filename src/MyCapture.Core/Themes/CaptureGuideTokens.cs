using System.Globalization;
using MyCapture.Core.Settings;

namespace MyCapture.Core.Themes;

/// <summary>Semantic guide tokens shared by all six palettes and the independent brand override.</summary>
public static class CaptureGuideTokens
{
    public static IReadOnlyDictionary<string, ThemeColor> ColorsFor(AppTheme theme, string? customColor = null)
    {
        IReadOnlyDictionary<string, ThemeColor> palette = ThemeCatalog.ColorsFor(theme);
        ThemeColor accent = palette["Overlay.SelectionBorder"];
        if (CaptureGuideColorValue.TryNormalize(customColor, out string normalized))
        {
            uint rgb = uint.Parse(normalized.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            accent = ThemeColor.Rgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        }
        // A neutral keyline keeps a custom dark/light crosshair visible on image pixels.
        bool lightAccent = accent.R * 299 + accent.G * 587 + accent.B * 114 >= 140000;
        ThemeColor contrast = lightAccent ? ThemeColor.Rgb(0, 0, 0) : ThemeColor.Rgb(255, 255, 255);
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
            ["CaptureGuide.ChipBorder"] = accent,
            ["CaptureGuide.Text"] = palette["Text.Primary"],
            ["CaptureGuide.MutedText"] = palette["Text.Secondary"],
            ["CaptureGuide.MagnifierBorder"] = accent,
            ["CaptureGuide.MagnifierGrid"] = contrast with { A = 50 },
            ["CaptureGuide.Dimmer"] = palette["Overlay.Dimmer"] with { A = 140 },
        };
    }
}