using System.Windows;
using System.Windows.Media;
using MyCapture.Core.Themes;

namespace MyCapture.App.Themes;

/// <summary>
/// Recolours the live token brushes so open windows pick up a new palette without
/// swapping every StaticResource reference.
/// </summary>
internal static class ThemeService
{
    internal static AppTheme Current { get; private set; } = AppTheme.Midnight;

    internal static void Apply(AppTheme theme)
    {
        Current = theme;
        if (Application.Current?.Resources is not { } resources)
        {
            return;
        }

        foreach ((string key, ThemeColor color) in ThemeCatalog.ColorsFor(theme))
        {
            if (resources[key] is SolidColorBrush brush)
            {
                if (brush.IsFrozen)
                {
                    var live = new SolidColorBrush(Color.FromArgb(color.A, color.R, color.G, color.B));
                    resources[key] = live;
                }
                else
                {
                    brush.Color = Color.FromArgb(color.A, color.R, color.G, color.B);
                }
            }
        }
    }

    internal static void ApplyFromSettings(string? themeId) => Apply(AppThemeNames.Parse(themeId));
}
