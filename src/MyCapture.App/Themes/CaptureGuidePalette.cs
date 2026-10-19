using System.Windows;
using System.Windows.Media;
using MyCapture.Core.Themes;

namespace MyCapture.App.Themes;

/// <summary>Resolves and freezes guide brushes once, outside pointer/render hot paths.</summary>
internal static class CaptureGuidePalette
{
    internal static IReadOnlyDictionary<string, Brush> Create(AppTheme theme, string? customColor,
        bool systemHighContrast, AppAccent accent = AppAccent.Violet)
    {
        var brushes = new Dictionary<string, Brush>(StringComparer.Ordinal);
        foreach ((string key, ThemeColor value) in CaptureGuideTokens.ColorsFor(theme, customColor, accent))
        {
            Color color = Color.FromArgb(value.A, value.R, value.G, value.B);
            if (systemHighContrast || theme == AppTheme.HighContrast)
            {
                color = key switch
                {
                    // Accent fills use WindowText, so the text on them is the system window colour.
                    "CaptureGuide.ChipSurface" or "CaptureGuide.InnerGlow"
                        or "CaptureGuide.CrosshairShadow" or "CaptureGuide.LineShadow"
                        or "CaptureGuide.AccentText" => SystemColors.WindowColor,
                    "CaptureGuide.Dimmer" => Color.FromArgb(140, SystemColors.WindowColor.R,
                        SystemColors.WindowColor.G, SystemColors.WindowColor.B),
                    _ => SystemColors.WindowTextColor,
                };
            }
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            brushes[key] = brush;
        }
        return brushes;
    }

    internal static void Apply(ResourceDictionary resources, AppTheme theme, string? customColor,
        bool systemHighContrast, AppAccent accent = AppAccent.Violet)
    {
        foreach ((string key, Brush brush) in Create(theme, customColor, systemHighContrast, accent))
            resources[key] = brush;
    }

    internal static IReadOnlyDictionary<string, Brush> Resolve()
    {
        var resolved = new Dictionary<string, Brush>(
            Create(ThemeService.EffectiveTheme, ThemeService.CaptureGuideColor, ThemeService.IsSystemHighContrast, ThemeService.CurrentAccent),
            StringComparer.Ordinal);
        if (ThemeService.IsSystemHighContrast || ThemeService.Current == AppTheme.HighContrast
            || Application.Current?.Resources is not { } resources) return resolved;
        foreach (string key in resolved.Keys.ToArray())
        {
            if (resources[key] is not Brush resource) continue;
            Brush brush = resource.CloneCurrentValue();
            if (brush.CanFreeze) brush.Freeze();
            resolved[key] = brush;
        }
        return resolved;
    }
}
