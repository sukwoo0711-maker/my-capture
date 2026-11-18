namespace MyCapture.Core.Themes;

/// <summary>Chrome palettes. Legacy enum values remain stable for compatibility.</summary>
public enum AppTheme
{
    Midnight = 0,
    Daylight = 1,
    HighContrast = 2,
    Workspace = 3,
    Glass = 4,
    GlassLight = 5,
    System = 6,
}

public static class AppThemeNames
{
    public const string System = "system";
    public const string Workspace = "workspace";
    public const string Midnight = "midnight";
    public const string Daylight = "daylight";
    public const string HighContrast = "high-contrast";
    public const string Glass = "glass";
    public const string GlassLight = "glass-light";

    public static AppTheme Parse(string? value)
    {
        if (string.Equals(value, System, StringComparison.OrdinalIgnoreCase)) return AppTheme.System;
        if (string.Equals(value, Workspace, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, Midnight, StringComparison.OrdinalIgnoreCase)) return AppTheme.Glass;
        if (string.Equals(value, Daylight, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "light", StringComparison.OrdinalIgnoreCase))
        {
            return AppTheme.GlassLight;
        }

        if (string.Equals(value, HighContrast, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "highcontrast", StringComparison.OrdinalIgnoreCase))
        {
            return AppTheme.HighContrast;
        }

        if (string.Equals(value, Glass, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "mica", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "acrylic", StringComparison.OrdinalIgnoreCase))
        {
            return AppTheme.Glass;
        }

        if (string.Equals(value, GlassLight, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "mica-light", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "light-glass", StringComparison.OrdinalIgnoreCase))
        {
            return AppTheme.GlassLight;
        }

        return AppTheme.Midnight;
    }

    public static string ToSetting(AppTheme theme) => theme switch
    {
        AppTheme.System => System,
        AppTheme.Workspace or AppTheme.Midnight => Glass,
        AppTheme.Daylight => GlassLight,
        AppTheme.HighContrast => HighContrast,
        AppTheme.Glass => Glass,
        AppTheme.GlassLight => GlassLight,
        _ => Glass,
    };

    public static bool IsLight(AppTheme theme) => theme is AppTheme.Daylight or AppTheme.GlassLight;

    public static bool UsesBackdrop(AppTheme theme) => theme is AppTheme.Glass or AppTheme.GlassLight;
}

/// <summary>ARGB values applied to live WPF brushes. Keys match Tokens.xaml.</summary>
public readonly record struct ThemeColor(byte A, byte R, byte G, byte B)
{
    public static ThemeColor Rgb(byte r, byte g, byte b) => new(0xFF, r, g, b);

    public static ThemeColor Argb(byte a, byte r, byte g, byte b) => new(a, r, g, b);
}

public static class ThemeCatalog
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<AppTheme, IReadOnlyDictionary<string, ThemeColor>> Palettes = new();

    // The accent argument is retained for older callers and tutorial options. Red is now fixed.
    public static IReadOnlyDictionary<string, ThemeColor> ColorsFor(AppTheme theme, AppAccent accent) => ColorsFor(theme);

    // System has a deterministic dark fallback here. The UI resolves the Windows preference
    // before asking for a palette; Core never reads a user's registry.
    public static IReadOnlyDictionary<string, ThemeColor> ColorsFor(AppTheme theme) => Palettes.GetOrAdd(theme, value =>
    {
        IReadOnlyDictionary<string, ThemeColor> source = value switch
        {
            AppTheme.Workspace => WorkspaceColors,
            AppTheme.Daylight => DaylightColors,
            AppTheme.HighContrast => HighContrastColors,
            AppTheme.Glass or AppTheme.System => GlassColors,
            AppTheme.GlassLight => GlassLightColors,
            _ => MidnightColors,
        };
        var colors = new Dictionary<string, ThemeColor>(source, StringComparer.Ordinal)
        {
            ["Surface.Selected"] = value switch
            {
                AppTheme.Daylight or AppTheme.GlassLight => ThemeColor.Rgb(0xC5, 0xCA, 0xD1),
                AppTheme.HighContrast => ThemeColor.Rgb(0x40, 0x46, 0x52),
                _ => ThemeColor.Rgb(0x39, 0x3C, 0x42),
            },
        };
        // Floating is the canonical fourth surface; Overlay remains its compatibility name.
        // Contrast palettes retain their distinct ButtonFace mapping at the WPF boundary.
        if (value != AppTheme.HighContrast) colors["Surface.Overlay"] = colors["Surface.Floating"];
        colors["Timeline.Background"] = colors["Surface.Base"];
        colors["Timeline.Track"] = colors["Surface.Raised"];
        colors["Surface.Badge"] = colors["Surface.Raised"];
        colors["Surface.BadgeStrong"] = colors["Surface.Raised"];
        colors["Text.Badge"] = colors["Text.Primary"];
        // Text selection has no outline: use an opaque, contrasting ink/fill pair of its own.
        colors["Text.SelectionBackground"] = colors["Text.Primary"];
        ThemeColor selectionInk = colors["Surface.Base"];
        colors["Text.SelectionForeground"] = ThemeColor.Rgb(selectionInk.R, selectionInk.G, selectionInk.B);
        // Layer bars keep their categorical colours; their ink must not follow neutral badges.
        colors["Timeline.LayerText"] = AppThemeNames.IsLight(value)
            ? ThemeColor.Rgb(0xFF, 0xFF, 0xFF) : ThemeColor.Rgb(0x0A, 0x08, 0x18);
        colors["Border.Focus"] = colors["Text.Primary"];
        colors["Pin.Frame"] = source["Border.Focus"];
        colors["Border.Accent"] = colors["Text.Primary"];
        colors["Overlay.SelectionBorder"] = colors["Text.Primary"];
        colors["Overlay.HandleStroke"] = colors["Text.Primary"];
        colors["Overlay.HandleFill"] = colors["Surface.Raised"];
        colors["Accent.Subtle"] = colors["Surface.Selected"];
        colors["Accent.Cool"] = colors["Text.Primary"];
        colors["Accent.Indicator"] = colors["Text.Primary"];
        if (value != AppTheme.HighContrast)
            foreach ((string key, ThemeColor color) in AccentPalette.RedOverrides(AppThemeNames.IsLight(value), colors))
                colors[key] = color;
        // Every live-recording mark (region frame, compact bar dot, region selector cue) shares
        // one colour: the brighter brand red, apart from the deeper primary fill. Status words
        // keep State.Danger.
        colors["Recording.Live"] = value == AppTheme.HighContrast ? colors["Accent.Default"] : AccentPalette.BrandRed;
        bool controlsHighContrast = value == AppTheme.HighContrast;
        bool controlsLight = AppThemeNames.IsLight(value);
        colors["Fill.Input"] = controlsHighContrast ? colors["Surface.Sunken"]
            : controlsLight ? ThemeColor.Rgb(255, 255, 255) : ThemeColor.Argb(13, 255, 255, 255);
        colors["Fill.InputHover"] = controlsHighContrast ? colors["Surface.Sunken"]
            : controlsLight ? ThemeColor.Rgb(0xF9, 0xFA, 0xFC) : ThemeColor.Argb(20, 255, 255, 255);
        colors["Fill.InputActive"] = controlsHighContrast || !controlsLight ? colors["Surface.Sunken"] : ThemeColor.Rgb(255, 255, 255);
        colors["Stroke.InputEdge"] = controlsHighContrast ? colors["Border.Control"]
            : controlsLight ? ThemeColor.Argb(128, 8, 14, 22) : ThemeColor.Argb(140, 255, 255, 255);
        colors["Stroke.CheckEdge"] = colors["Stroke.InputEdge"];
        // An opaque neutral edge keeps its contrast and raster independent of overlapping hover fills.
        colors["Stroke.Selected"] = controlsHighContrast ? colors["Text.Primary"] : colors["Border.Strong"];
        colors["Fill.ScrollThumb"] = controlsHighContrast ? colors["Border.Strong"]
            : controlsLight ? ThemeColor.Argb(71, 8, 14, 22) : ThemeColor.Argb(77, 255, 255, 255);
        colors["Fill.ScrollThumbHover"] = controlsHighContrast ? colors["Text.Muted"]
            : controlsLight ? ThemeColor.Argb(115, 8, 14, 22) : ThemeColor.Argb(128, 255, 255, 255);
        colors["Fill.ScrollThumbPressed"] = controlsHighContrast ? colors["Text.Primary"]
            : controlsLight ? ThemeColor.Argb(153, 8, 14, 22) : ThemeColor.Argb(166, 255, 255, 255);
        // Decorative boundaries remain distinct from the accessible control and selection edges.
        if (!controlsHighContrast)
            colors["Border.Subtle"] = controlsLight ? ThemeColor.Argb(32, 8, 14, 22) : ThemeColor.Argb(0x38, 0xF6, 0xF8, 0xFC);
        colors["Fill.Button"] = controlsHighContrast ? colors["Surface.Overlay"]
            : controlsLight ? ThemeColor.Rgb(255, 255, 255) : colors["Surface.Raised"];
        colors["Stroke.ButtonTop"] = controlsHighContrast ? colors["Border.Control"]
            : controlsLight ? ThemeColor.Rgb(0xD7, 0xDC, 0xE2) : ThemeColor.Rgb(0x44, 0x49, 0x4F);
        colors["Stroke.ButtonBottom"] = controlsHighContrast ? colors["Border.Control"]
            : controlsLight ? ThemeColor.Rgb(0xAD, 0xB5, 0xBF) : ThemeColor.Rgb(0x17, 0x19, 0x1C);
        return colors.AsReadOnly();
    });

    internal static readonly Dictionary<string, ThemeColor> MidnightColors = new(StringComparer.Ordinal)
    {
        ["Surface.Canvas"] = ThemeColor.Rgb(0x11, 0x12, 0x14),
        ["Surface.Base"] = ThemeColor.Rgb(0x19, 0x1B, 0x1E),
        ["Surface.Raised"] = ThemeColor.Rgb(0x24, 0x27, 0x2B),
        ["Surface.Overlay"] = ThemeColor.Rgb(0x30, 0x34, 0x3A),
        ["Surface.Floating"] = ThemeColor.Rgb(0x30, 0x34, 0x3A),
        ["Surface.Sunken"] = ThemeColor.Rgb(0x11, 0x12, 0x14),
        ["Surface.Hover"] = ThemeColor.Rgb(0x2B, 0x2E, 0x34),
        ["Surface.Pressed"] = ThemeColor.Rgb(0x32, 0x35, 0x3A),
        ["Surface.Scrim"] = ThemeColor.Argb(0xC2, 0x08, 0x0C, 0x12),
        ["Surface.Badge"] = ThemeColor.Argb(0xE6, 0x08, 0x0C, 0x12),
        ["Surface.BadgeStrong"] = ThemeColor.Argb(0xF2, 0x08, 0x0C, 0x12),
        ["Text.Primary"] = ThemeColor.Rgb(0xFF, 0xFF, 0xFF),
        ["Text.Secondary"] = ThemeColor.Rgb(0xC9, 0xCD, 0xD4),
        ["Text.Muted"] = ThemeColor.Rgb(0xA0, 0xA6, 0xAF),
        ["Text.OnAccent"] = ThemeColor.Rgb(0x0A, 0x08, 0x18),
        ["Text.Badge"] = ThemeColor.Rgb(0xFF, 0xFF, 0xFF),
        ["Text.Warning"] = ThemeColor.Rgb(0xF5, 0xB9, 0x42),
        ["Text.Success"] = ThemeColor.Rgb(0x45, 0xD6, 0xA2),
        ["Accent.Default"] = ThemeColor.Rgb(0x7C, 0x6C, 0xFF),
        ["Accent.Hover"] = ThemeColor.Rgb(0x97, 0x89, 0xFF),
        ["Accent.Pressed"] = ThemeColor.Rgb(0x7A, 0x68, 0xEE),
        ["Accent.Subtle"] = ThemeColor.Rgb(0x1A, 0x17, 0x33),
        ["Accent.Cool"] = ThemeColor.Rgb(0x97, 0x89, 0xFF),
        ["Accent.Gradient"] = ThemeColor.Rgb(0x7C, 0x6C, 0xFF),
        // Border roles: Subtle draws decorative panel and separator lines, Control outlines
        // inputs and bordered buttons (WCAG 3:1 on Surface.Raised), Strong is the hover and
        // floating-surface boundary and never weaker than Control.
        ["Border.Subtle"] = ThemeColor.Rgb(0x2B, 0x3A, 0x50),
        ["Border.Control"] = ThemeColor.Rgb(0x85, 0x8B, 0x94),
        ["Border.Strong"] = ThemeColor.Rgb(0xA0, 0xA6, 0xAF),
        ["Border.Focus"] = ThemeColor.Rgb(0x97, 0x89, 0xFF),
        ["Border.Accent"] = ThemeColor.Rgb(0x7C, 0x6C, 0xFF),
        ["State.Warning"] = ThemeColor.Rgb(0xF5, 0xB9, 0x42),
        ["State.Success"] = ThemeColor.Rgb(0x45, 0xD6, 0xA2),
        ["State.Danger"] = ThemeColor.Rgb(0xFF, 0x6B, 0x74),
        ["State.DangerHover"] = ThemeColor.Rgb(0xFF, 0x8A, 0x91),
        ["Border.Badge"] = ThemeColor.Argb(0x66, 0xF6, 0xF8, 0xFC),
        ["Timeline.Background"] = ThemeColor.Rgb(0x19, 0x1B, 0x1E),
        ["Timeline.Track"] = ThemeColor.Rgb(0x24, 0x27, 0x2B),
        ["Timeline.TextLayer"] = ThemeColor.Rgb(0x7F, 0xA6, 0xFF),
        ["Timeline.FrameLayer"] = ThemeColor.Rgb(0x3F, 0xD8, 0xC9),
        ["Timeline.Grid"] = ThemeColor.Rgb(0x45, 0x4A, 0x52),
        ["Timeline.Playhead"] = ThemeColor.Rgb(0xFF, 0xFF, 0xFF),
        ["Timeline.TrimDeleteFill"] = ThemeColor.Argb(0x55, 0xFF, 0x6B, 0x74),
        ["Timeline.TrimDeleteHandle"] = ThemeColor.Rgb(0xFF, 0x8A, 0x91),
        ["Timeline.TrimDeleteHatch"] = ThemeColor.Argb(0xCC, 0xFF, 0xB0, 0xB6),
        ["Overlay.Dimmer"] = ThemeColor.Rgb(0x11, 0x12, 0x14),
        ["Overlay.SelectionBorder"] = ThemeColor.Rgb(0x97, 0x89, 0xFF),
        ["Overlay.HandleFill"] = ThemeColor.Rgb(0xF6, 0xF8, 0xFC),
        ["Overlay.HandleStroke"] = ThemeColor.Rgb(0x5A, 0x48, 0xE0),
        // Library folder cards: a warm surface, outline and glyph keep folders apart from captures.
        ["Surface.Folder"] = ThemeColor.Rgb(0x2A, 0x22, 0x13),
        ["Border.Folder"] = ThemeColor.Rgb(0x8A, 0x6A, 0x2E),
        ["Accent.Folder"] = ThemeColor.Rgb(0xF0, 0xB4, 0x4C),
    };

    internal static readonly Dictionary<string, ThemeColor> DaylightColors = new(StringComparer.Ordinal)
    {
        ["Surface.Canvas"] = ThemeColor.Rgb(0xE4, 0xE7, 0xEB),
        ["Surface.Base"] = ThemeColor.Rgb(0xED, 0xF0, 0xF3),
        ["Surface.Raised"] = ThemeColor.Rgb(0xF7, 0xF8, 0xFA),
        ["Surface.Overlay"] = ThemeColor.Rgb(0xFF, 0xFF, 0xFF),
        ["Surface.Floating"] = ThemeColor.Rgb(0xFF, 0xFF, 0xFF),
        ["Surface.Sunken"] = ThemeColor.Rgb(0xE4, 0xE7, 0xEB),
        ["Surface.Hover"] = ThemeColor.Rgb(0xDE, 0xE2, 0xE7),
        ["Surface.Pressed"] = ThemeColor.Rgb(0xD0, 0xD5, 0xDC),
        ["Surface.Scrim"] = ThemeColor.Argb(0xC2, 0xF3, 0xF5, 0xF8),
        ["Surface.Badge"] = ThemeColor.Argb(0xE6, 0x12, 0x18, 0x24),
        ["Surface.BadgeStrong"] = ThemeColor.Argb(0xF2, 0x12, 0x18, 0x24),
        ["Text.Primary"] = ThemeColor.Rgb(0x10, 0x13, 0x18),
        ["Text.Secondary"] = ThemeColor.Rgb(0x34, 0x3B, 0x46),
        ["Text.Muted"] = ThemeColor.Rgb(0x4D, 0x56, 0x63),
        ["Text.OnAccent"] = ThemeColor.Rgb(0x0A, 0x08, 0x18),
        ["Text.Badge"] = ThemeColor.Rgb(0xFF, 0xFF, 0xFF),
        // Words need 4.5:1 on the surfaces that carry them; State.Warning/State.Success reach only
        // 2.9-4.1:1 there, so status words take darker hues. The dark and high-contrast palettes keep
        // their State hue for words because it already passes.
        ["Text.Warning"] = ThemeColor.Rgb(0x88, 0x5E, 0x0C),
        ["Text.Success"] = ThemeColor.Rgb(0x16, 0x74, 0x51),
        ["Accent.Default"] = ThemeColor.Rgb(0x7C, 0x6C, 0xFF),
        ["Accent.Hover"] = ThemeColor.Rgb(0x97, 0x89, 0xFF),
        ["Accent.Pressed"] = ThemeColor.Rgb(0x7A, 0x68, 0xEE),
        ["Accent.Subtle"] = ThemeColor.Rgb(0xEC, 0xE9, 0xFF),
        ["Accent.Cool"] = ThemeColor.Rgb(0x5A, 0x48, 0xE0),
        ["Accent.Gradient"] = ThemeColor.Rgb(0x7C, 0x6C, 0xFF),
        ["Border.Subtle"] = ThemeColor.Rgb(0xC5, 0xD0, 0xDE),
        ["Border.Control"] = ThemeColor.Rgb(0x7B, 0x82, 0x8C),
        ["Border.Strong"] = ThemeColor.Rgb(0x62, 0x6B, 0x78),
        ["Border.Focus"] = ThemeColor.Rgb(0x5A, 0x48, 0xE0),
        ["Border.Accent"] = ThemeColor.Rgb(0x7C, 0x6C, 0xFF),
        ["State.Warning"] = ThemeColor.Rgb(0xB5, 0x7E, 0x12),
        ["State.Success"] = ThemeColor.Rgb(0x1B, 0x8A, 0x62),
        ["State.Danger"] = ThemeColor.Rgb(0xC4, 0x2B, 0x3A),
        ["State.DangerHover"] = ThemeColor.Rgb(0xE0, 0x45, 0x54),
        ["Border.Badge"] = ThemeColor.Argb(0x66, 0x12, 0x18, 0x24),
        ["Timeline.Background"] = ThemeColor.Rgb(0xED, 0xF0, 0xF3),
        ["Timeline.Track"] = ThemeColor.Rgb(0xF7, 0xF8, 0xFA),
        ["Timeline.TextLayer"] = ThemeColor.Rgb(0x2F, 0x63, 0xD1),
        ["Timeline.FrameLayer"] = ThemeColor.Rgb(0x0E, 0x82, 0x78),
        ["Timeline.Grid"] = ThemeColor.Rgb(0xB9, 0xC0, 0xC9),
        ["Timeline.Playhead"] = ThemeColor.Rgb(0x4B, 0x3C, 0xC0),
        ["Timeline.TrimDeleteFill"] = ThemeColor.Argb(0x55, 0xC4, 0x2B, 0x3A),
        ["Timeline.TrimDeleteHandle"] = ThemeColor.Rgb(0xC4, 0x2B, 0x3A),
        ["Timeline.TrimDeleteHatch"] = ThemeColor.Argb(0xCC, 0xC4, 0x2B, 0x3A),
        ["Overlay.Dimmer"] = ThemeColor.Rgb(0xE4, 0xE7, 0xEB),
        ["Overlay.SelectionBorder"] = ThemeColor.Rgb(0x5A, 0x48, 0xE0),
        ["Overlay.HandleFill"] = ThemeColor.Rgb(0xFF, 0xFF, 0xFF),
        ["Overlay.HandleStroke"] = ThemeColor.Rgb(0x4B, 0x3C, 0xC0),
        ["Surface.Folder"] = ThemeColor.Rgb(0xFF, 0xF3, 0xD6),
        ["Border.Folder"] = ThemeColor.Rgb(0xC9, 0x95, 0x38),
        ["Accent.Folder"] = ThemeColor.Rgb(0x9A, 0x62, 0x00),
    };

    internal static readonly Dictionary<string, ThemeColor> HighContrastColors = new(StringComparer.Ordinal)
    {
        ["Surface.Canvas"] = ThemeColor.Rgb(0x00, 0x00, 0x00),
        ["Surface.Base"] = ThemeColor.Rgb(0x00, 0x00, 0x00),
        ["Surface.Raised"] = ThemeColor.Rgb(0x12, 0x12, 0x12),
        ["Surface.Overlay"] = ThemeColor.Rgb(0x1A, 0x1A, 0x1A),
        ["Surface.Floating"] = ThemeColor.Argb(0xFF, 0x12, 0x12, 0x12),
        ["Surface.Sunken"] = ThemeColor.Rgb(0x00, 0x00, 0x00),
        ["Surface.Hover"] = ThemeColor.Rgb(0x2A, 0x2A, 0x2A),
        ["Surface.Pressed"] = ThemeColor.Rgb(0x3A, 0x3A, 0x3A),
        ["Surface.Scrim"] = ThemeColor.Argb(0xE6, 0x00, 0x00, 0x00),
        ["Surface.Badge"] = ThemeColor.Argb(0xFF, 0x00, 0x00, 0x00),
        ["Surface.BadgeStrong"] = ThemeColor.Argb(0xFF, 0x00, 0x00, 0x00),
        ["Text.Primary"] = ThemeColor.Rgb(0xFF, 0xFF, 0xFF),
        ["Text.Secondary"] = ThemeColor.Rgb(0xDA, 0xDA, 0xDA),
        ["Text.Muted"] = ThemeColor.Rgb(0xB3, 0xB3, 0xB3),
        ["Text.OnAccent"] = ThemeColor.Rgb(0x00, 0x00, 0x00),
        ["Text.Badge"] = ThemeColor.Rgb(0xFF, 0xFF, 0xFF),
        ["Text.Warning"] = ThemeColor.Rgb(0xFF, 0xD0, 0x00),
        ["Text.Success"] = ThemeColor.Rgb(0x00, 0xE6, 0x76),
        ["Accent.Default"] = ThemeColor.Rgb(0xFF, 0xD0, 0x00),
        ["Accent.Hover"] = ThemeColor.Rgb(0xFF, 0xE0, 0x4D),
        ["Accent.Pressed"] = ThemeColor.Rgb(0xC9, 0xA6, 0x00),
        ["Accent.Subtle"] = ThemeColor.Rgb(0x3A, 0x30, 0x00),
        ["Accent.Cool"] = ThemeColor.Rgb(0xFF, 0xD0, 0x00),
        ["Accent.Gradient"] = ThemeColor.Rgb(0xFF, 0xD0, 0x00),
        ["Border.Subtle"] = ThemeColor.Rgb(0xFF, 0xFF, 0xFF),
        ["Border.Control"] = ThemeColor.Rgb(0xFF, 0xFF, 0xFF),
        ["Border.Strong"] = ThemeColor.Rgb(0xFF, 0xFF, 0xFF),
        ["Border.Focus"] = ThemeColor.Rgb(0xFF, 0xD0, 0x00),
        ["Border.Accent"] = ThemeColor.Rgb(0xFF, 0xD0, 0x00),
        ["State.Warning"] = ThemeColor.Rgb(0xFF, 0xD0, 0x00),
        ["State.Success"] = ThemeColor.Rgb(0x00, 0xE6, 0x76),
        ["State.Danger"] = ThemeColor.Rgb(0xFF, 0x6B, 0x74),
        ["State.DangerHover"] = ThemeColor.Rgb(0xFF, 0x8A, 0x91),
        ["Border.Badge"] = ThemeColor.Argb(0xFF, 0xFF, 0xFF, 0xFF),
        ["Timeline.Background"] = ThemeColor.Rgb(0x00, 0x00, 0x00),
        ["Timeline.Track"] = ThemeColor.Rgb(0x12, 0x12, 0x12),
        ["Timeline.TextLayer"] = ThemeColor.Rgb(0x80, 0xC0, 0xFF),
        ["Timeline.FrameLayer"] = ThemeColor.Rgb(0xD0, 0xB0, 0xFF),
        ["Timeline.Grid"] = ThemeColor.Rgb(0xFF, 0xFF, 0xFF),
        ["Timeline.Playhead"] = ThemeColor.Rgb(0xFF, 0xD0, 0x00),
        ["Timeline.TrimDeleteFill"] = ThemeColor.Argb(0x80, 0xFF, 0x6B, 0x74),
        ["Timeline.TrimDeleteHandle"] = ThemeColor.Rgb(0xFF, 0x6B, 0x74),
        ["Timeline.TrimDeleteHatch"] = ThemeColor.Argb(0xCC, 0xFF, 0xB0, 0xB6),
        ["Overlay.Dimmer"] = ThemeColor.Rgb(0x00, 0x00, 0x00),
        ["Overlay.SelectionBorder"] = ThemeColor.Rgb(0xFF, 0xD0, 0x00),
        ["Overlay.HandleFill"] = ThemeColor.Rgb(0xFF, 0xFF, 0xFF),
        ["Overlay.HandleStroke"] = ThemeColor.Rgb(0xFF, 0xD0, 0x00),
        ["Surface.Folder"] = ThemeColor.Rgb(0x00, 0x00, 0x00),
        ["Border.Folder"] = ThemeColor.Rgb(0x80, 0xC0, 0xFF),
        ["Accent.Folder"] = ThemeColor.Rgb(0x80, 0xC0, 0xFF),
    };
    // Stored theme identities remain compatible. The 6.0 chrome uses solid surfaces in every
    // non-contrast theme; Workspace chooses this dark palette or Daylight by window role.
    internal static readonly Dictionary<string, ThemeColor> WorkspaceColors = new(MidnightColors, StringComparer.Ordinal);
    internal static readonly Dictionary<string, ThemeColor> GlassColors = new(MidnightColors, StringComparer.Ordinal);
    internal static readonly Dictionary<string, ThemeColor> GlassLightColors = new(DaylightColors, StringComparer.Ordinal);
}
