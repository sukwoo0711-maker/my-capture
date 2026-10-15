namespace MyCapture.Core.Themes;

public enum AppAccent
{
    Violet = 0,
    Red = 1,
}

public static class AppAccentNames
{
    public const string Violet = "violet";
    public const string Red = "red";

    public static AppAccent Parse(string? value) =>
        string.Equals(value, Violet, StringComparison.OrdinalIgnoreCase) ? AppAccent.Violet : AppAccent.Red;

    public static string ToSetting(AppAccent accent) => accent == AppAccent.Violet ? Violet : Red;
}

/// <summary>Accent overrides leave surfaces and semantic status colours with the chosen theme.</summary>
public static class AccentPalette
{
    public static IReadOnlyDictionary<string, ThemeColor> RedOverrides(bool light,
        IReadOnlyDictionary<string, ThemeColor> surfaces)
    {
        // M06's flat pin is retained on filled actions. Ink gives 4.79:1 there; the
        // warm-white mark colour gives only 3.93:1. Bare marks need their own ramp.
        ThemeColor fill = ThemeColor.Rgb(0xE0, 0x45, 0x3C);
        ThemeColor subtle = light ? ThemeColor.Rgb(0xFF, 0xE7, 0xE2) : ThemeColor.Rgb(0x35, 0x1A, 0x1D);
        ThemeColor[] backgrounds = [.. new[] { "Surface.Canvas", "Surface.Base", "Surface.Raised",
            "Surface.Overlay", "Surface.Floating", "Surface.Sunken", "Surface.Hover", "Surface.Pressed" }
            .SelectMany(key => new[] { ThemeContrast.Composite(surfaces[key], ThemeColor.Rgb(0, 0, 0)),
                ThemeContrast.Composite(surfaces[key], ThemeColor.Rgb(255, 255, 255)) }), subtle];
        ThemeColor foreground = ClosestPinTone(fill, light, backgrounds, 4.5);
        ThemeColor indicator = ClosestPinTone(fill, light, backgrounds, 3);
        return new Dictionary<string, ThemeColor>(StringComparer.Ordinal)
        {
            ["Text.OnAccent"] = ThemeColor.Rgb(0x0A, 0x08, 0x18),
            ["Accent.Default"] = fill,
            ["Accent.Hover"] = ThemeColor.Rgb(0xF3, 0x5B, 0x4D),
            ["Accent.Pressed"] = ThemeColor.Rgb(0xDA, 0x43, 0x3A),
            ["Accent.Subtle"] = subtle,
            ["Accent.Cool"] = foreground,
            ["Accent.Indicator"] = indicator,
            ["Accent.Gradient"] = fill,
            ["Border.Focus"] = indicator,
            ["Border.Accent"] = indicator,
            ["Overlay.SelectionBorder"] = indicator,
            ["Overlay.HandleStroke"] = fill,
        }.AsReadOnly();
    }

    private static ThemeColor ClosestPinTone(ThemeColor pin, bool light, ThemeColor[] surfaces, double minimum)
    {
        // Search from the pin toward black/white at 8-bit blend precision. The first
        // passing tone is the least adjustment along this hue-preserving tint/shade ramp.
        for (int step = 0; step <= 255; step++)
        {
            ThemeColor candidate = ThemeContrast.Composite(pin with { A = (byte)(255 - step) },
                light ? ThemeColor.Rgb(0, 0, 0) : ThemeColor.Rgb(255, 255, 255));
            if (surfaces.All(surface => ThemeContrast.Ratio(candidate, surface) >= minimum)) return candidate;
        }
        throw new InvalidOperationException("No accessible pin tone exists for these surfaces.");
    }
}
