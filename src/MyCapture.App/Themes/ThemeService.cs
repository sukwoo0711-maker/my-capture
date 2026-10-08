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
    internal static AppTheme Current { get; private set; } = AppTheme.Workspace;

    /// <summary>Null until the first Apply; lets diagnostics restore the pre-fixture state.</summary>
    internal static AppTheme? IsAppliedForTest => _applied ? Current : null;
    private static bool _applied;

    internal static void Apply(AppTheme theme)
    {
        Current = theme;
        _applied = true;
        if (Application.Current?.Resources is not { } resources)
        {
            return;
        }

        ApplyResources(theme, resources);
        WorkspaceTheme.Refresh(theme);
        ModernWindowChrome.RefreshAll();
    }

    internal static void ApplyResources(AppTheme theme, ResourceDictionary resources)
    {
        foreach ((string key, ThemeColor color) in ThemeCatalog.ColorsFor(theme))
        {
            if (resources[key] is SolidColorBrush brush)
            {
                if (brush.IsFrozen)
                {
                    // Keep opacity and transforms: Overlay.Dimmer intentionally uses a
                    // translucent brush even though its palette color has an opaque alpha.
                    var live = brush.CloneCurrentValue();
                    live.Color = Color.FromArgb(color.A, color.R, color.G, color.B);
                    resources[key] = live;
                }
                else
                {
                    brush.SetCurrentValue(SolidColorBrush.ColorProperty, Color.FromArgb(color.A, color.R, color.G, color.B));
                }
            }
        }
        // Crop masks always darken discarded pixels, including in light themes.
        // High contrast follows the user's system palette without a translucent wash.
        bool highContrast = SystemParameters.HighContrast || theme == AppTheme.HighContrast;
        resources["Crop.Scrim"] = highContrast ? SystemColors.WindowBrush : new SolidColorBrush(Color.FromArgb(153, 8, 12, 18));
        resources["Crop.Frame"] = highContrast ? SystemColors.WindowTextBrush : Brushes.White;
        resources["Crop.FrameShadow"] = highContrast ? SystemColors.WindowBrush : Brushes.Black;
    }

    internal static void ApplyFromSettings(string? themeId) => Apply(AppThemeNames.Parse(themeId));
}
