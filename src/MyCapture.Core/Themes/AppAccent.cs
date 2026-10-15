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
        string.Equals(value, Red, StringComparison.OrdinalIgnoreCase) ? AppAccent.Red : AppAccent.Violet;

    public static string ToSetting(AppAccent accent) => accent == AppAccent.Red ? Red : Violet;
}

/// <summary>Accent overrides leave surfaces and semantic status colours with the chosen theme.</summary>
public static class AccentPalette
{
    private static readonly IReadOnlyDictionary<string, ThemeColor> DarkRed = RedColors(light: false);
    private static readonly IReadOnlyDictionary<string, ThemeColor> LightRed = RedColors(light: true);

    public static IReadOnlyDictionary<string, ThemeColor> RedOverrides(bool light) => light ? LightRed : DarkRed;

    private static IReadOnlyDictionary<string, ThemeColor> RedColors(bool light)
    {
        // B07's signal red needs separate fill and foreground ramps: warm white on deep red,
        // coral on dark chrome, and burgundy on light chrome (including pressed surfaces).
        ThemeColor fill = ThemeColor.Rgb(0xC8, 0x32, 0x2C);
        ThemeColor foreground = light ? ThemeColor.Rgb(0xA0, 0x20, 0x26) : ThemeColor.Rgb(0xFF, 0xA9, 0x9F);
        return new Dictionary<string, ThemeColor>(StringComparer.Ordinal)
        {
            ["Text.OnAccent"] = ThemeColor.Rgb(0xFF, 0xF8, 0xF4),
            ["Accent.Default"] = fill,
            ["Accent.Hover"] = ThemeColor.Rgb(0xCA, 0x38, 0x2F),
            ["Accent.Pressed"] = ThemeColor.Rgb(0xBB, 0x25, 0x2D),
            ["Accent.Subtle"] = light ? ThemeColor.Rgb(0xFF, 0xE7, 0xE2) : ThemeColor.Rgb(0x35, 0x1A, 0x1D),
            ["Accent.Cool"] = foreground,
            ["Accent.Gradient"] = fill,
            ["Border.Focus"] = foreground,
            ["Border.Accent"] = foreground,
            ["Overlay.SelectionBorder"] = foreground,
            ["Overlay.HandleStroke"] = ThemeColor.Rgb(0xA7, 0x22, 0x2A),
        }.AsReadOnly();
    }
}
