using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using MyCapture.Core.Themes;
using MyCapture.Core.Settings;

namespace MyCapture.App.Themes;

/// <summary>
/// Swaps the token brushes for frozen brushes in the new palette; DynamicResource consumers
/// pick them up and code that keeps a brush uses <see cref="ThemeBrushes.Follow(string, Color)"/>.
/// Shared brushes are never recoloured in place (see <see cref="ThemeBrushes"/>).
/// </summary>
/// <remarks>
/// While a Windows contrast theme is on, every semantic token resolves to the user's system
/// colours (<see cref="HighContrastPalette"/>) instead of the chosen palette: opaque surfaces,
/// no shadows and a 2px focus ring. Turning the contrast theme off re-applies the chosen
/// palette, and both directions update open windows live.
/// </remarks>
internal static class ThemeService
{
    private static readonly string[] ShadowKeys = ["Shadow.Floating", "Shadow.Card"];
    private static readonly DropShadowEffect NoShadow = CreateNoShadow();

    internal static AppTheme Current { get; private set; } = AppTheme.Workspace;

    /// <summary>Null until the first Apply; lets diagnostics restore the pre-fixture state.</summary>
    internal static AppTheme? IsAppliedForTest => _applied ? Current : null;
    private static bool _applied;
    private static DispatcherOperation? _pendingSystemRefresh;

    [ThreadStatic]
    private static bool? s_systemHighContrastOverrideForTest;

    [ThreadStatic]
    private static Func<SystemColorRole, Color>? s_systemColorsOverrideForTest;

    internal static string CaptureGuideColor { get; private set; } = string.Empty;
    internal static event Action? CaptureGuideChanged;

    /// <summary>Whether a Windows contrast theme is on.</summary>
    internal static bool IsSystemHighContrast =>
        s_systemHighContrastOverrideForTest ?? SystemParameters.HighContrast;

    /// <summary>Simulates a Windows contrast theme on the current test thread only.</summary>
    internal static bool? SystemHighContrastOverrideForTest
    {
        get => s_systemHighContrastOverrideForTest;
        set => s_systemHighContrastOverrideForTest = value;
    }

    /// <summary>Supplies a contrast theme's system colours on the current test thread only.</summary>
    internal static Func<SystemColorRole, Color>? SystemColorsOverrideForTest
    {
        get => s_systemColorsOverrideForTest;
        set => s_systemColorsOverrideForTest = value;
    }

    static ThemeService()
    {
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.HighContrast)) QueueSystemRefresh();
        };
    }

    /// <summary>
    /// Re-applies the current palette after Windows turns a contrast theme on or off, or changes
    /// its colours (ModernWindowChrome forwards WM_SYSCOLORCHANGE). Bursts collapse to one pass.
    /// </summary>
    internal static void QueueSystemRefresh()
    {
        if (Application.Current is not { } app
            || _pendingSystemRefresh is { Status: DispatcherOperationStatus.Pending })
        {
            return;
        }

        _pendingSystemRefresh = ScheduleSystemThemeRefresh(app.Dispatcher, app.Resources, () => IsSystemHighContrast);
    }

    // Read the system palette after WPF has processed its system-color notifications.
    internal static DispatcherOperation ScheduleSystemThemeRefresh(Dispatcher dispatcher,
        ResourceDictionary resources, Func<bool> readHighContrast) =>
        dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,
            new Action(() =>
            {
                ApplyResources(Current, resources, CaptureGuideColor, readHighContrast());
                if (ReferenceEquals(resources, Application.Current?.Resources))
                {
                    WorkspaceTheme.Refresh(Current);
                    ModernWindowChrome.RefreshAll();
                }

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

    internal static void ApplyResources(AppTheme theme, ResourceDictionary resources, string? captureGuideColor = null,
        bool? systemHighContrast = null)
    {
        bool contrast = systemHighContrast ?? IsSystemHighContrast;
        foreach ((string key, ThemeColor color) in ThemeCatalog.ColorsFor(theme))
        {
            if (resources[key] is SolidColorBrush brush)
            {
                // Keep opacity and transforms: Overlay.Dimmer intentionally uses a
                // translucent brush even though its palette color has an opaque alpha.
                resources[key] = ThemeBrushes.Recolored(brush, contrast
                    ? SystemColor(HighContrastPalette.RoleFor(key))
                    : Color.FromArgb(color.A, color.R, color.G, color.B));
            }
        }

        SetBrush(resources, "Text.Disabled", contrast
            ? SystemColor(SystemColorRole.GrayText)
            : AppThemeNames.IsLight(theme) ? Color.FromRgb(0x74, 0x7C, 0x89) : Color.FromRgb(0x7A, 0x82, 0x90));
        // Hover layers recolour their fill; a contrast theme keeps fills on Window, so hover
        // shows as a Highlight outline instead.
        SetBrush(resources, "Border.Hover", contrast ? SystemColor(SystemColorRole.Highlight) : Colors.Transparent);
        ApplyFolderHoverResources(resources, contrast);
        bool strongChrome = contrast || theme == AppTheme.HighContrast;
        resources["Focus.RingThickness"] = new Thickness(strongChrome ? 2 : 1);
        resources["Focus.RingWidth"] = strongChrome ? 2d : 1d;
        foreach (string key in ShadowKeys)
        {
            // Contrast themes draw no shadows. A null resource would fall through to the merged
            // Tokens.xaml effect, so the override is an invisible one; removing it restores the shadow.
            if (strongChrome) resources[key] = NoShadow;
            else resources.Remove(key);
        }

        // Crop masks always darken discarded pixels, including in light themes.
        // High contrast follows the user's system palette without a translucent wash.
        ApplyCropResources(resources, contrast);
        CaptureGuidePalette.Apply(resources, theme, captureGuideColor, contrast);
        ThemeBrushes.Refresh(resources);
    }

    /// <summary>The system colour of <paramref name="role"/>, always opaque.</summary>
    internal static Color SystemColor(SystemColorRole role)
    {
        Color color = s_systemColorsOverrideForTest?.Invoke(role) ?? role switch
        {
            SystemColorRole.Window => SystemColors.WindowColor,
            SystemColorRole.WindowText => SystemColors.WindowTextColor,
            SystemColorRole.ButtonFace => SystemColors.ControlColor,
            SystemColorRole.ButtonText => SystemColors.ControlTextColor,
            SystemColorRole.Highlight => SystemColors.HighlightColor,
            SystemColorRole.HighlightText => SystemColors.HighlightTextColor,
            SystemColorRole.GrayText => SystemColors.GrayTextColor,
            SystemColorRole.HotTrack => SystemColors.HotTrackColor,
            _ => SystemColors.WindowTextColor,
        };
        return Color.FromRgb(color.R, color.G, color.B);
    }

    private static DropShadowEffect CreateNoShadow()
    {
        var effect = new DropShadowEffect { Opacity = 0, BlurRadius = 0, ShadowDepth = 0 };
        effect.Freeze();
        return effect;
    }

    private static readonly SolidColorBrush ClearBrush = CreateClearBrush();

    private static SolidColorBrush CreateClearBrush()
    {
        var brush = new SolidColorBrush(Colors.Transparent);
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// The library folder card's hover edge and wash. App palettes alias the very Accent.Folder and
    /// Surface.Hover brushes, so their look is unchanged. A Windows contrast theme draws the rest and
    /// hover edges both in WindowText and the wash in the Window colour, so the hover edge takes
    /// Highlight and the wash, which would dim that edge, turns clear. Brushes are swapped, never
    /// recoloured. Call again after anything that replaces Accent.Folder or Surface.Hover.
    /// </summary>
    internal static void ApplyFolderHoverResources(ResourceDictionary resources, bool contrast)
    {
        if (contrast)
        {
            SetBrush(resources, "Border.FolderHover", SystemColor(SystemColorRole.Highlight));
            resources["Surface.FolderHoverWash"] = ClearBrush;
            return;
        }

        if (resources["Accent.Folder"] is Brush folder) resources["Border.FolderHover"] = folder;
        if (resources["Surface.Hover"] is Brush hover) resources["Surface.FolderHoverWash"] = hover;
    }

    private static void SetBrush(ResourceDictionary resources, string key, Color color)
    {
        if (resources[key] is SolidColorBrush brush)
        {
            resources[key] = ThemeBrushes.Recolored(brush, color);
            return;
        }

        var created = new SolidColorBrush(color);
        created.Freeze();
        resources[key] = created;
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
