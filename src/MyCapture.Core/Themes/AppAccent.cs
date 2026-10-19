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

    // Keep accepting old settings and command-line options; the product has one accent.
    public static AppAccent Parse(string? value) => AppAccent.Red;
    public static string ToSetting(AppAccent accent) => Red;
}

/// <summary>Primary actions and floating pin frames retain the brand; selection and focus use neutral ink.</summary>
public static class AccentPalette
{
    public static IReadOnlyDictionary<string, ThemeColor> RedOverrides(bool light,
        IReadOnlyDictionary<string, ThemeColor> surfaces)
    {
        ThemeColor ink = surfaces["Text.Primary"];
        ThemeColor selected = surfaces.TryGetValue("Surface.Selected", out ThemeColor value) ? value
            : light ? ThemeColor.Rgb(0xDD, 0xE3, 0xEB) : ThemeColor.Rgb(0x30, 0x36, 0x42);
        ThemeColor pin = ThemeColor.Rgb(0xE0, 0x45, 0x3C);
        ThemeColor subtle = light ? ThemeColor.Rgb(0xFF, 0xE7, 0xE2) : ThemeColor.Rgb(0x35, 0x1A, 0x1D);
        ThemeColor[] backgrounds = [.. new[] { "Surface.Canvas", "Surface.Base", "Surface.Raised",
            "Surface.Overlay", "Surface.Floating", "Surface.Sunken", "Surface.Hover", "Surface.Pressed" }
            .SelectMany(key => new[] { ThemeContrast.Composite(surfaces[key], ThemeColor.Rgb(0, 0, 0)),
                ThemeContrast.Composite(surfaces[key], ThemeColor.Rgb(255, 255, 255)) }), subtle];
        return new Dictionary<string, ThemeColor>(StringComparer.Ordinal)
        {
            ["Pin.Frame"] = ClosestPinTone(pin, light, backgrounds),
            ["Text.OnAccent"] = ThemeColor.Rgb(0x0A, 0x08, 0x18),
            ["Accent.Default"] = ThemeColor.Rgb(0xE0, 0x45, 0x3C),
            ["Accent.Hover"] = ThemeColor.Rgb(0xF3, 0x5B, 0x4D),
            ["Accent.Pressed"] = ThemeColor.Rgb(0xDA, 0x43, 0x3A),
            ["Accent.Gradient"] = ThemeColor.Rgb(0xE0, 0x45, 0x3C),
            ["Accent.Subtle"] = selected,
            ["Accent.Cool"] = ink,
            ["Accent.Indicator"] = ink,
            ["Border.Focus"] = ink,
            ["Border.Accent"] = ink,
            ["Overlay.SelectionBorder"] = ink,
            ["Overlay.HandleStroke"] = ink,
            ["Overlay.HandleFill"] = surfaces["Surface.Raised"],
        }.AsReadOnly();
    }

    private static ThemeColor ClosestPinTone(ThemeColor pin, bool light, ThemeColor[] surfaces)
    {
        // Preserve the 4.3.1 outline tone without applying it to neutral selection tokens.
        for (int step = 0; step <= 255; step++)
        {
            ThemeColor candidate = ThemeContrast.Composite(pin with { A = (byte)(255 - step) },
                light ? ThemeColor.Rgb(0, 0, 0) : ThemeColor.Rgb(255, 255, 255));
            if (surfaces.All(surface => ThemeContrast.Ratio(candidate, surface) >= 3)) return candidate;
        }
        throw new InvalidOperationException("No accessible pin tone exists for these surfaces.");
    }
}
