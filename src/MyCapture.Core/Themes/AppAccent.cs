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

/// <summary>Brand fill is reserved for primary actions; selection and focus use neutral ink.</summary>
public static class AccentPalette
{
    public static IReadOnlyDictionary<string, ThemeColor> RedOverrides(bool light,
        IReadOnlyDictionary<string, ThemeColor> surfaces)
    {
        ThemeColor ink = surfaces["Text.Primary"];
        ThemeColor selected = surfaces.TryGetValue("Surface.Selected", out ThemeColor value) ? value
            : light ? ThemeColor.Rgb(0xDD, 0xE3, 0xEB) : ThemeColor.Rgb(0x30, 0x36, 0x42);
        return new Dictionary<string, ThemeColor>(StringComparer.Ordinal)
        {
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
}
