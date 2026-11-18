using System.IO;
using System.Security;
using Microsoft.Win32;
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

    internal static AppTheme Current { get; private set; } = AppTheme.System;
    internal static AppTheme EffectiveTheme => ResolveTheme(Current);
    internal static AppAccent CurrentAccent { get; private set; } = AppAccent.Red;

    /// <summary>Null until the first Apply; lets diagnostics restore the pre-fixture state.</summary>
    internal static AppTheme? IsAppliedForTest => _applied ? Current : null;
    private static bool _applied;
    private static DispatcherOperation? _pendingSystemRefresh;

    [ThreadStatic]
    private static bool? s_systemHighContrastOverrideForTest;

    [ThreadStatic]
    private static Func<SystemColorRole, Color>? s_systemColorsOverrideForTest;

    [ThreadStatic]
    private static Func<bool>? s_systemLightThemeReaderForTest;

    /// <summary>Injects the app-mode reader without writing the registry.</summary>
    internal static Func<bool>? SystemLightThemeReaderForTest
    {
        get => s_systemLightThemeReaderForTest;
        set => s_systemLightThemeReaderForTest = value;
    }

    internal static AppTheme ResolveTheme(AppTheme theme, Func<bool>? readLightTheme = null) => theme == AppTheme.System
        ? (readLightTheme ?? s_systemLightThemeReaderForTest ?? ReadWindowsLightTheme)() ? AppTheme.GlassLight : AppTheme.Glass
        : theme;

    private static bool ReadWindowsLightTheme() => ReadWindowsLightTheme(static () =>
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", writable: false);
        return key?.GetValue("AppsUseLightTheme");
    });

    /// <summary>Missing Windows app-mode settings mean Light; read failures retain the dark fallback.</summary>
    internal static bool ReadWindowsLightTheme(Func<object?> readAppMode)
    {
        ArgumentNullException.ThrowIfNull(readAppMode);
        try
        {
            // Both a missing Personalize key and a missing value return null. An explicit
            // DWORD zero selects Dark; retain the previous handling of other stored types.
            object? value = readAppMode();
            return value is null || value is int number && number != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }

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
        SystemEvents.UserPreferenceChanged += (_, _) => QueueSystemRefresh();
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
                ApplyResources(Current, resources, CaptureGuideColor, readHighContrast(), CurrentAccent);
                if (ReferenceEquals(resources, Application.Current?.Resources))
                {
                    WorkspaceTheme.Refresh(EffectiveTheme);
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

        ApplyResources(theme, resources, CaptureGuideColor, accent: CurrentAccent);
        WorkspaceTheme.Refresh(ResolveTheme(theme));
        ModernWindowChrome.RefreshAll();
        CaptureGuideChanged?.Invoke();
    }

    internal static void ApplyResources(AppTheme theme, ResourceDictionary resources, string? captureGuideColor = null,
        bool? systemHighContrast = null, AppAccent accent = AppAccent.Red)
    {
        theme = ResolveTheme(theme);
        bool contrast = systemHighContrast ?? IsSystemHighContrast;
        IReadOnlyDictionary<string, ThemeColor> colors = ThemeCatalog.ColorsFor(theme, accent);
        foreach ((string key, ThemeColor color) in colors)
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

        ThemeColor foreground = colors["Text.Primary"];
        SetBrush(resources, "Accent.Foreground", contrast ? SystemColor(SystemColorRole.WindowText)
            : Color.FromArgb(foreground.A, foreground.R, foreground.G, foreground.B));
        ThemeColor indicator = colors.TryGetValue("Accent.Indicator", out ThemeColor value) ? value : foreground;
        SetBrush(resources, "Accent.Indicator", contrast ? SystemColor(SystemColorRole.WindowText)
            : Color.FromArgb(indicator.A, indicator.R, indicator.G, indicator.B));
        SetBrush(resources, "Border.SelectedTool", contrast ? SystemColor(SystemColorRole.WindowText)
            : Color.FromArgb(foreground.A, foreground.R, foreground.G, foreground.B));

        SetBrush(resources, "Text.Disabled", contrast
            ? SystemColor(SystemColorRole.GrayText)
            : AppThemeNames.IsLight(theme) ? Color.FromRgb(0x74, 0x7C, 0x89) : Color.FromRgb(0x7A, 0x82, 0x90));
        // Hover layers recolour their fill; a contrast theme keeps fills on Window, so hover
        // shows as a Highlight outline instead.
        SetBrush(resources, "Border.Hover", contrast ? SystemColor(SystemColorRole.Highlight) : Colors.Transparent);
        ApplyFolderHoverResources(resources, contrast);
        bool strongChrome = contrast || theme == AppTheme.HighContrast;
        resources["Focus.RingThickness"] = new Thickness(2);
        resources["Focus.RingWidth"] = 2d;
        resources["Border.DecorativeThickness"] = new Thickness(strongChrome ? 1 : 0);
        resources["Border.ToolThickness"] = new Thickness(strongChrome ? 1 : 0);
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
        CaptureGuidePalette.Apply(resources, theme, captureGuideColor, contrast, accent);
        ApplyButtonResources(resources, theme, contrast);
        ApplyControlLanguageResources(resources, theme, contrast);
        ThemeBrushes.Refresh(resources);
        ShortcutRevealMotion.SettleAll();
        Interlocked.Increment(ref _resourceVersion);
    }

    /// <summary>Changes on every <see cref="ApplyResources"/>; layout measured before it may be stale.</summary>
    internal static int ResourceVersion => Volatile.Read(ref _resourceVersion);
    private static int _resourceVersion;

    internal static void ApplyButtonResources(ResourceDictionary resources, AppTheme theme, bool contrast)
    {
        bool highContrast = contrast || theme == AppTheme.HighContrast;
        resources["Button.Fill"] = resources[highContrast ? "Surface.Overlay" : "Fill.Button"];
        resources["Button.FontWeight"] = highContrast ? FontWeights.SemiBold : FontWeights.Normal;
        resources["Button.ChromeInset"] = highContrast ? new Thickness(0) : new Thickness(0, 4, 0, 4);
        resources["Button.ChromeInset.Compact"] = highContrast ? new Thickness(0) : new Thickness(0, 2, 0, 2);
        resources["Button.FocusInset"] = highContrast ? new Thickness(1) : new Thickness(1, 5, 1, 5);
        resources["Button.FocusInset.Compact"] = highContrast ? new Thickness(1) : new Thickness(1, 3, 1, 3);
        resources["Button.PrimaryFocusInset"] = highContrast ? new Thickness(2) : new Thickness(2, 6, 2, 6);
        resources["Button.PrimaryFocusInset.Compact"] = highContrast ? new Thickness(2) : new Thickness(2, 4, 2, 4);
        if (highContrast)
        {
            resources["Button.BorderBrush"] = resources["Border.Control"];
            return;
        }

        Color top = ((SolidColorBrush)resources["Stroke.ButtonTop"]).Color;
        Color bottom = ((SolidColorBrush)resources["Stroke.ButtonBottom"]).Color;
        var border = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0), EndPoint = new Point(0, 1),
            GradientStops = new GradientStopCollection
            {
                new GradientStop(top, 0), new GradientStop(top, 0.8), new GradientStop(bottom, 1),
            },
        };
        border.Freeze();
        resources["Button.BorderBrush"] = border;
    }

    /// <summary>Component aliases keep the original contrast-theme geometry and brushes intact.</summary>
    internal static void ApplyControlLanguageResources(ResourceDictionary resources, AppTheme theme, bool contrast)
    {
        bool legacy = contrast || theme == AppTheme.HighContrast;
        resources["ControlLanguage.Enabled"] = !legacy;
        resources["FontWeight.ControlLabel"] = legacy ? FontWeights.SemiBold : FontWeights.Normal;
        if (resources["Expander.Options"] is Style optionsStyle)
        {
            var optionalStyle = legacy ? new Style(typeof(System.Windows.Controls.Expander))
                : new Style(typeof(System.Windows.Controls.Expander), optionsStyle);
            if (!legacy) optionalStyle.Setters.Add(new Setter(ControlLanguage.PreserveExpanderMetricsProperty, true));
            resources["Expander.OptionalStyle"] = optionalStyle;
        }
        void Alias(string name, string key) => resources[name] = resources[key];
        Alias("Input.Fill", legacy ? "Surface.Sunken" : "Fill.Input");
        Alias("Input.Border", legacy ? "Border.Control" : "Border.Subtle");
        resources["Check.Fill"] = legacy ? resources["Surface.Sunken"] : ClearBrush;
        Alias("Check.Edge", legacy ? "Border.Control" : "Stroke.CheckEdge");
        resources["Check.HoverFill"] = legacy ? ClearBrush : resources["Surface.Hover"];
        resources["Check.HoverEdge"] = legacy ? resources["Border.Focus"] : ClearBrush;
        Alias("Check.CheckedFill", legacy ? "Surface.Selected" : "Text.Primary");
        Alias("Check.CheckedEdge", "Text.Primary");
        resources["Check.CheckedThickness"] = new Thickness(legacy ? 2 : 0);
        resources["Check.Radius"] = new CornerRadius(legacy ? 5 : 4);
        resources["Check.MarkMargin"] = new Thickness(legacy ? 0 : 2);
        Color surface = ((SolidColorBrush)resources["Surface.Base"]).Color;
        SetBrush(resources, "Check.Mark", legacy ? ((SolidColorBrush)resources["Text.Primary"]).Color
            : Color.FromRgb(surface.R, surface.G, surface.B));
        SetBrush(resources, "Radio.Mark", legacy ? ((SolidColorBrush)resources["Surface.Raised"]).Color
            : Color.FromRgb(surface.R, surface.G, surface.B));
        Alias("Selection.Stroke", legacy ? "Text.Primary" : "Stroke.Selected");
        resources["Selection.Thickness"] = new Thickness(legacy ? 2 : 1);
        Alias("Toggle.CheckedFill", legacy ? "Text.SelectionBackground" : "Surface.Selected");
        resources["Toggle.CheckedStroke"] = legacy ? ClearBrush : resources["Stroke.Selected"];
        resources["Toggle.CheckedThickness"] = new Thickness(legacy ? 0 : 1);
        Alias("Toggle.CheckedForeground", legacy ? "Text.SelectionForeground" : "Text.Primary");
        Alias("Toggle.CheckedFocusStroke", legacy ? "Text.SelectionForeground" : "Border.Focus");
        Alias("Combo.Fill", legacy ? "Surface.Overlay" : "Button.Fill");
        Alias("Combo.Edge", legacy ? "Border.Control" : "Button.BorderBrush");
        Alias("Combo.FocusEdge", legacy ? "Border.Focus" : "Button.BorderBrush");
        resources["Combo.HoverEdge"] = legacy ? resources["Border.Strong"] : resources["Border.Hover"];
        resources["Combo.HoverFill"] = legacy ? ClearBrush : resources["Surface.Hover"];
        resources["Combo.PressedFill"] = legacy ? ClearBrush : resources["Surface.Pressed"];
        Alias("Combo.Chevron", legacy ? "Text.Primary" : "Text.Secondary");
        resources["Combo.Radius"] = FoundationMetrics.ControlCornerRadius;
        resources["Scroll.Lane"] = legacy ? resources["Surface.Base"] : ClearBrush;
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

    internal static void ApplyFromSettings(string? themeId, string? captureGuideColor = null, string? accentColor = null)
    {
        CurrentAccent = AppAccentNames.Parse(accentColor);
        CaptureGuideColor = CaptureGuideColorValue.TryNormalize(captureGuideColor, out string color) ? color : string.Empty;
        Apply(AppThemeNames.Parse(themeId));
    }

    internal static void ApplyFromSettings(GeneralSettings settings) =>
        ApplyFromSettings(settings.Theme, settings.CaptureGuideColor, settings.AccentColor);
}
