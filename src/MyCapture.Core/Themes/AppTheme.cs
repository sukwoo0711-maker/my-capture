namespace MyCapture.Core.Themes;

/// <summary>User-selectable chrome palettes. Glass is the shipped Windows-native default.</summary>
public enum AppTheme
{
    Midnight = 0,
    Daylight = 1,
    HighContrast = 2,
    Workspace = 3,
    Glass = 4,
    GlassLight = 5,
}

public static class AppThemeNames
{
    public const string Workspace = "workspace";
    public const string Midnight = "midnight";
    public const string Daylight = "daylight";
    public const string HighContrast = "high-contrast";
    public const string Glass = "glass";
    public const string GlassLight = "glass-light";

    public static AppTheme Parse(string? value)
    {
        if (string.Equals(value, Workspace, StringComparison.OrdinalIgnoreCase)) return AppTheme.Workspace;
        if (string.Equals(value, Daylight, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "light", StringComparison.OrdinalIgnoreCase))
        {
            return AppTheme.Daylight;
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
        AppTheme.Workspace => Workspace,
        AppTheme.Daylight => Daylight,
        AppTheme.HighContrast => HighContrast,
        AppTheme.Glass => Glass,
        AppTheme.GlassLight => GlassLight,
        _ => Midnight,
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
    public static IReadOnlyDictionary<string, ThemeColor> ColorsFor(AppTheme theme) => theme switch
    {
        AppTheme.Workspace => WorkspaceColors,
        AppTheme.Daylight => DaylightColors,
        AppTheme.HighContrast => HighContrastColors,
        AppTheme.Glass => GlassColors,
        AppTheme.GlassLight => GlassLightColors,
        _ => MidnightColors,
    };

    internal static readonly Dictionary<string, ThemeColor> MidnightColors = new(StringComparer.Ordinal)
    {
        ["Surface.Canvas"] = ThemeColor.Rgb(0x08, 0x0C, 0x12),
        ["Surface.Base"] = ThemeColor.Rgb(0x0B, 0x0F, 0x17),
        ["Surface.Raised"] = ThemeColor.Rgb(0x10, 0x17, 0x22),
        ["Surface.Overlay"] = ThemeColor.Rgb(0x15, 0x1E, 0x2B),
        ["Surface.Floating"] = ThemeColor.Argb(0xF2, 0x15, 0x1E, 0x2B),
        ["Surface.Sunken"] = ThemeColor.Rgb(0x08, 0x0C, 0x12),
        ["Surface.Hover"] = ThemeColor.Rgb(0x1B, 0x26, 0x36),
        ["Surface.Pressed"] = ThemeColor.Rgb(0x24, 0x32, 0x46),
        ["Surface.Scrim"] = ThemeColor.Argb(0xC2, 0x08, 0x0C, 0x12),
        ["Surface.Badge"] = ThemeColor.Argb(0xE6, 0x08, 0x0C, 0x12),
        ["Surface.BadgeStrong"] = ThemeColor.Argb(0xF2, 0x08, 0x0C, 0x12),
        ["Text.Primary"] = ThemeColor.Rgb(0xFF, 0xFF, 0xFF),
        ["Text.Secondary"] = ThemeColor.Rgb(0xE4, 0xEB, 0xF5),
        ["Text.Muted"] = ThemeColor.Rgb(0xC2, 0xCD, 0xDC),
        ["Text.OnAccent"] = ThemeColor.Rgb(0x06, 0x11, 0x16),
        ["Text.Badge"] = ThemeColor.Rgb(0xFF, 0xFF, 0xFF),
        ["Accent.Default"] = ThemeColor.Rgb(0x58, 0xC7, 0xF3),
        ["Accent.Hover"] = ThemeColor.Rgb(0x7D, 0xD7, 0xF8),
        ["Accent.Pressed"] = ThemeColor.Rgb(0x38, 0xA8, 0xD8),
        ["Accent.Subtle"] = ThemeColor.Rgb(0x15, 0x2F, 0x3E),
        ["Accent.Cool"] = ThemeColor.Rgb(0x7D, 0xD7, 0xF8),
        ["Accent.Gradient"] = ThemeColor.Rgb(0x58, 0xC7, 0xF3),
        ["Border.Subtle"] = ThemeColor.Rgb(0x2B, 0x3A, 0x50),
        ["Border.Strong"] = ThemeColor.Rgb(0x47, 0x59, 0x73),
        ["Border.Focus"] = ThemeColor.Rgb(0x7D, 0xD7, 0xF8),
        ["Border.Accent"] = ThemeColor.Rgb(0x58, 0xC7, 0xF3),
        ["State.Warning"] = ThemeColor.Rgb(0xF5, 0xB9, 0x42),
        ["State.Success"] = ThemeColor.Rgb(0x45, 0xD6, 0xA2),
        ["State.Danger"] = ThemeColor.Rgb(0xFF, 0x6B, 0x74),
        ["State.DangerHover"] = ThemeColor.Rgb(0xFF, 0x8A, 0x91),
        ["Border.Badge"] = ThemeColor.Argb(0x66, 0xF6, 0xF8, 0xFC),
        ["Timeline.Background"] = ThemeColor.Rgb(0x0B, 0x0F, 0x17),
        ["Timeline.Track"] = ThemeColor.Rgb(0x15, 0x1E, 0x2B),
        ["Timeline.TextLayer"] = ThemeColor.Rgb(0x3B, 0x82, 0xF6),
        ["Timeline.FrameLayer"] = ThemeColor.Rgb(0x9B, 0x7E, 0xDE),
        ["Timeline.Grid"] = ThemeColor.Rgb(0x2B, 0x3A, 0x50),
        ["Timeline.Playhead"] = ThemeColor.Rgb(0x7D, 0xD7, 0xF8),
        ["Timeline.TrimDeleteFill"] = ThemeColor.Argb(0x55, 0xFF, 0x6B, 0x74),
        ["Timeline.TrimDeleteHandle"] = ThemeColor.Rgb(0xFF, 0x8A, 0x91),
        ["Timeline.TrimDeleteHatch"] = ThemeColor.Argb(0xCC, 0xFF, 0xB0, 0xB6),
        ["Overlay.Dimmer"] = ThemeColor.Rgb(0x08, 0x0C, 0x12),
        ["Overlay.SelectionBorder"] = ThemeColor.Rgb(0x7D, 0xD7, 0xF8),
        ["Overlay.HandleFill"] = ThemeColor.Rgb(0xF6, 0xF8, 0xFC),
        ["Overlay.HandleStroke"] = ThemeColor.Rgb(0x38, 0xA8, 0xD8),
    };

    internal static readonly Dictionary<string, ThemeColor> DaylightColors = new(StringComparer.Ordinal)
    {
        ["Surface.Canvas"] = ThemeColor.Rgb(0xF3, 0xF5, 0xF8),
        ["Surface.Base"] = ThemeColor.Rgb(0xF7, 0xF8, 0xFB),
        ["Surface.Raised"] = ThemeColor.Rgb(0xFF, 0xFF, 0xFF),
        ["Surface.Overlay"] = ThemeColor.Rgb(0xEE, 0xF2, 0xF6),
        ["Surface.Floating"] = ThemeColor.Argb(0xF2, 0xFF, 0xFF, 0xFF),
        ["Surface.Sunken"] = ThemeColor.Rgb(0xE7, 0xEC, 0xF2),
        ["Surface.Hover"] = ThemeColor.Rgb(0xE2, 0xE8, 0xF0),
        ["Surface.Pressed"] = ThemeColor.Rgb(0xD0, 0xD8, 0xE4),
        ["Surface.Scrim"] = ThemeColor.Argb(0xC2, 0xF3, 0xF5, 0xF8),
        ["Surface.Badge"] = ThemeColor.Argb(0xE6, 0x12, 0x18, 0x24),
        ["Surface.BadgeStrong"] = ThemeColor.Argb(0xF2, 0x12, 0x18, 0x24),
        ["Text.Primary"] = ThemeColor.Rgb(0x0B, 0x12, 0x1C),
        ["Text.Secondary"] = ThemeColor.Rgb(0x1A, 0x27, 0x38),
        ["Text.Muted"] = ThemeColor.Rgb(0x2F, 0x3D, 0x50),
        ["Text.OnAccent"] = ThemeColor.Rgb(0x06, 0x11, 0x16),
        ["Text.Badge"] = ThemeColor.Rgb(0xFF, 0xFF, 0xFF),
        ["Accent.Default"] = ThemeColor.Rgb(0x1A, 0x8F, 0xC2),
        ["Accent.Hover"] = ThemeColor.Rgb(0x38, 0xA8, 0xD8),
        ["Accent.Pressed"] = ThemeColor.Rgb(0x0F, 0x6F, 0x99),
        ["Accent.Subtle"] = ThemeColor.Rgb(0xD6, 0xEE, 0xF8),
        ["Accent.Cool"] = ThemeColor.Rgb(0x1A, 0x8F, 0xC2),
        ["Accent.Gradient"] = ThemeColor.Rgb(0x1A, 0x8F, 0xC2),
        ["Border.Subtle"] = ThemeColor.Rgb(0xC5, 0xD0, 0xDE),
        ["Border.Strong"] = ThemeColor.Rgb(0x8E, 0x9C, 0xAF),
        ["Border.Focus"] = ThemeColor.Rgb(0x1A, 0x8F, 0xC2),
        ["Border.Accent"] = ThemeColor.Rgb(0x1A, 0x8F, 0xC2),
        ["State.Warning"] = ThemeColor.Rgb(0xB5, 0x7E, 0x12),
        ["State.Success"] = ThemeColor.Rgb(0x1B, 0x8A, 0x62),
        ["State.Danger"] = ThemeColor.Rgb(0xC4, 0x2B, 0x3A),
        ["State.DangerHover"] = ThemeColor.Rgb(0xE0, 0x45, 0x54),
        ["Border.Badge"] = ThemeColor.Argb(0x66, 0x12, 0x18, 0x24),
        ["Timeline.Background"] = ThemeColor.Rgb(0xF3, 0xF5, 0xF8),
        ["Timeline.Track"] = ThemeColor.Rgb(0xE7, 0xEC, 0xF2),
        ["Timeline.TextLayer"] = ThemeColor.Rgb(0x1A, 0x6F, 0xC2),
        ["Timeline.FrameLayer"] = ThemeColor.Rgb(0x6B, 0x4F, 0xB3),
        ["Timeline.Grid"] = ThemeColor.Rgb(0xC5, 0xD0, 0xDE),
        ["Timeline.Playhead"] = ThemeColor.Rgb(0x1A, 0x8F, 0xC2),
        ["Timeline.TrimDeleteFill"] = ThemeColor.Argb(0x55, 0xC4, 0x2B, 0x3A),
        ["Timeline.TrimDeleteHandle"] = ThemeColor.Rgb(0xC4, 0x2B, 0x3A),
        ["Timeline.TrimDeleteHatch"] = ThemeColor.Argb(0xCC, 0xC4, 0x2B, 0x3A),
        ["Overlay.Dimmer"] = ThemeColor.Rgb(0xF3, 0xF5, 0xF8),
        ["Overlay.SelectionBorder"] = ThemeColor.Rgb(0x1A, 0x8F, 0xC2),
        ["Overlay.HandleFill"] = ThemeColor.Rgb(0xFF, 0xFF, 0xFF),
        ["Overlay.HandleStroke"] = ThemeColor.Rgb(0x0F, 0x6F, 0x99),
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
        ["Text.Secondary"] = ThemeColor.Rgb(0xFF, 0xFF, 0xFF),
        ["Text.Muted"] = ThemeColor.Rgb(0xDC, 0xDC, 0xDC),
        ["Text.OnAccent"] = ThemeColor.Rgb(0x00, 0x00, 0x00),
        ["Text.Badge"] = ThemeColor.Rgb(0xFF, 0xFF, 0xFF),
        ["Accent.Default"] = ThemeColor.Rgb(0xFF, 0xD0, 0x00),
        ["Accent.Hover"] = ThemeColor.Rgb(0xFF, 0xE0, 0x4D),
        ["Accent.Pressed"] = ThemeColor.Rgb(0xC9, 0xA6, 0x00),
        ["Accent.Subtle"] = ThemeColor.Rgb(0x3A, 0x30, 0x00),
        ["Accent.Cool"] = ThemeColor.Rgb(0xFF, 0xD0, 0x00),
        ["Accent.Gradient"] = ThemeColor.Rgb(0xFF, 0xD0, 0x00),
        ["Border.Subtle"] = ThemeColor.Rgb(0xFF, 0xFF, 0xFF),
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
    };
    internal static readonly Dictionary<string, ThemeColor> WorkspaceColors = new(MidnightColors, StringComparer.Ordinal)
    {
        ["Surface.Canvas"] = ThemeColor.Rgb(0x14, 0x1b, 0x22),
        ["Surface.Base"] = ThemeColor.Rgb(0x14, 0x1b, 0x22),
        ["Surface.Raised"] = ThemeColor.Rgb(0x20, 0x2b, 0x34),
        ["Surface.Overlay"] = ThemeColor.Rgb(0x25, 0x32, 0x3c),
        ["Surface.Sunken"] = ThemeColor.Rgb(0x10, 0x17, 0x1d),
        ["Surface.Hover"] = ThemeColor.Rgb(0x2a, 0x3c, 0x43),
        ["Surface.Pressed"] = ThemeColor.Rgb(0x30, 0x49, 0x51),
        ["Text.Primary"] = ThemeColor.Rgb(0xff, 0xff, 0xff),
        ["Text.Secondary"] = ThemeColor.Rgb(0xe3, 0xee, 0xf4),
        ["Text.Muted"] = ThemeColor.Rgb(0xc5, 0xd4, 0xdc),
        ["Text.OnAccent"] = ThemeColor.Rgb(0x10, 0x2b, 0x2b),
        ["Accent.Default"] = ThemeColor.Rgb(0x5a, 0xd9, 0xc5),
        ["Accent.Hover"] = ThemeColor.Rgb(0x81, 0xe5, 0xd4),
        ["Accent.Pressed"] = ThemeColor.Rgb(0x3b, 0xbc, 0xa8),
        ["Accent.Subtle"] = ThemeColor.Rgb(0x18, 0x3e, 0x3c),
        ["Accent.Cool"] = ThemeColor.Rgb(0x5a, 0xd9, 0xc5),
        ["Accent.Gradient"] = ThemeColor.Rgb(0x5a, 0xd9, 0xc5),
        ["Border.Subtle"] = ThemeColor.Rgb(0x35, 0x46, 0x50),
        ["Border.Focus"] = ThemeColor.Rgb(0x5a, 0xd9, 0xc5),
        ["Border.Accent"] = ThemeColor.Rgb(0x5a, 0xd9, 0xc5),
        ["Timeline.Background"] = ThemeColor.Rgb(0x14, 0x1b, 0x22),
        ["Timeline.Track"] = ThemeColor.Rgb(0x20, 0x2b, 0x34),
        ["Timeline.Playhead"] = ThemeColor.Rgb(0x5a, 0xd9, 0xc5),
        ["Timeline.TextLayer"] = ThemeColor.Rgb(0x31, 0x9c, 0x91),
        ["Timeline.FrameLayer"] = ThemeColor.Rgb(0x71, 0x8a, 0xc5),
    };

    internal static readonly Dictionary<string, ThemeColor> GlassColors = new(MidnightColors, StringComparer.Ordinal)
    {
        ["Surface.Canvas"] = ThemeColor.Argb(0xF4, 0x12, 0x14, 0x1A),
        ["Surface.Base"] = ThemeColor.Argb(0xF6, 0x16, 0x18, 0x20),
        ["Surface.Raised"] = ThemeColor.Argb(0xF2, 0x24, 0x28, 0x32),
        ["Surface.Overlay"] = ThemeColor.Argb(0xF4, 0x2A, 0x30, 0x3C),
        ["Surface.Floating"] = ThemeColor.Argb(0xF6, 0x22, 0x26, 0x30),
        ["Surface.Sunken"] = ThemeColor.Argb(0xF0, 0x10, 0x12, 0x18),
        ["Surface.Hover"] = ThemeColor.Argb(0xF4, 0x32, 0x3A, 0x48),
        ["Surface.Pressed"] = ThemeColor.Argb(0xF6, 0x3C, 0x46, 0x56),
        ["Surface.Scrim"] = ThemeColor.Argb(0xE6, 0x08, 0x0C, 0x12),
        ["Text.Primary"] = ThemeColor.Rgb(0xFF, 0xFF, 0xFF),
        ["Text.Secondary"] = ThemeColor.Rgb(0xEE, 0xF3, 0xFA),
        ["Text.Muted"] = ThemeColor.Rgb(0xD0, 0xD8, 0xE6),
        ["Border.Subtle"] = ThemeColor.Argb(0x99, 0xF6, 0xF8, 0xFC),
        ["Border.Strong"] = ThemeColor.Argb(0xCC, 0xC6, 0xD0, 0xDF),
        ["Timeline.Background"] = ThemeColor.Argb(0xF4, 0x12, 0x14, 0x1A),
        ["Timeline.Track"] = ThemeColor.Argb(0xF2, 0x24, 0x28, 0x32),
    };

    internal static readonly Dictionary<string, ThemeColor> GlassLightColors = new(DaylightColors, StringComparer.Ordinal)
    {
        ["Surface.Canvas"] = ThemeColor.Argb(0xF6, 0xF4, 0xF6, 0xF9),
        ["Surface.Base"] = ThemeColor.Argb(0xF8, 0xF7, 0xF8, 0xFB),
        ["Surface.Raised"] = ThemeColor.Argb(0xF6, 0xFF, 0xFF, 0xFF),
        ["Surface.Overlay"] = ThemeColor.Argb(0xF6, 0xEE, 0xF2, 0xF6),
        ["Surface.Floating"] = ThemeColor.Argb(0xF8, 0xFF, 0xFF, 0xFF),
        ["Surface.Sunken"] = ThemeColor.Argb(0xF4, 0xE4, 0xEA, 0xF0),
        ["Surface.Hover"] = ThemeColor.Argb(0xF6, 0xDC, 0xE4, 0xEE),
        ["Surface.Pressed"] = ThemeColor.Argb(0xF6, 0xC8, 0xD2, 0xE0),
        ["Text.Primary"] = ThemeColor.Rgb(0x08, 0x0E, 0x16),
        ["Text.Secondary"] = ThemeColor.Rgb(0x16, 0x22, 0x32),
        ["Text.Muted"] = ThemeColor.Rgb(0x2A, 0x38, 0x4C),
        ["Border.Subtle"] = ThemeColor.Argb(0xB3, 0x8A, 0x98, 0xAA),
        ["Timeline.Background"] = ThemeColor.Argb(0xF6, 0xF4, 0xF6, 0xF9),
        ["Timeline.Track"] = ThemeColor.Argb(0xF6, 0xE4, 0xEA, 0xF0),
    };
}
