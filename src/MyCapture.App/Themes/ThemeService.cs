using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using MyCapture.Core.Themes;
using MyCapture.Core.Settings;

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
    internal static string CaptureGuideColor { get; private set; } = string.Empty;
    internal static event Action? CaptureGuideChanged;

    static ThemeService()
    {
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(SystemParameters.HighContrast) || Application.Current is not { } app) return;
            ScheduleCropResourceRefresh(app.Dispatcher, app.Resources, () => SystemParameters.HighContrast);
        };
    }

    // Read the system palette after WPF has processed its system-color notifications.
    internal static DispatcherOperation ScheduleCropResourceRefresh(Dispatcher dispatcher,
        ResourceDictionary resources, Func<bool> readHighContrast) =>
        dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,
            new Action(() =>
            {
                bool highContrast = readHighContrast();
                ApplyCropResources(resources, highContrast);
                CaptureGuidePalette.Apply(resources, Current, CaptureGuideColor, highContrast);
                CaptureGuideChanged?.Invoke();
            }));

    internal static void Apply(AppTheme theme)
    {
        Current = theme;
        _applied = true;
        if (Application.Current?.Resources is not { } resources)
        {
            return;
        }

        ApplyResources(theme, resources, CaptureGuideColor);
        WorkspaceTheme.Refresh(theme);
        ModernWindowChrome.RefreshAll();
        CaptureGuideChanged?.Invoke();
    }

    internal static void ApplyResources(AppTheme theme, ResourceDictionary resources, string? captureGuideColor = null)
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
        resources["Text.Disabled"] = new SolidColorBrush(theme is AppTheme.Daylight or AppTheme.GlassLight
            ? Color.FromRgb(0x74, 0x7C, 0x89) : Color.FromRgb(0x7A, 0x82, 0x90));
        ApplyCropResources(resources, SystemParameters.HighContrast);
        CaptureGuidePalette.Apply(resources, theme, captureGuideColor, SystemParameters.HighContrast);
    }

    internal static void ApplyCropResources(ResourceDictionary resources, bool highContrast)
    {
        resources["Crop.Scrim"] = highContrast ? SystemColors.WindowBrush : new SolidColorBrush(Color.FromArgb(153, 8, 12, 18));
        resources["Crop.Frame"] = highContrast ? SystemColors.WindowTextBrush : Brushes.White;
        resources["Crop.FrameShadow"] = highContrast ? SystemColors.WindowBrush : Brushes.Black;
    }

    internal static void ApplyFromSettings(string? themeId, string? captureGuideColor = null)
    {
        CaptureGuideColor = CaptureGuideColorValue.TryNormalize(captureGuideColor, out string color) ? color : string.Empty;
        Apply(AppThemeNames.Parse(themeId));
    }

    internal static void ApplyFromSettings(GeneralSettings settings) =>
        ApplyFromSettings(settings.Theme, settings.CaptureGuideColor);
}
