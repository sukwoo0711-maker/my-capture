using System.Windows;
using System.Windows.Media;
using MyCapture.Core.Themes;

namespace MyCapture.App.Themes;

internal enum WorkspaceRole { Gallery, VideoEditor }

/// <summary>Owns each window's brushes and styles; shared application resources are never recoloured here.</summary>
internal static class WorkspaceTheme
{
    private static readonly List<(WeakReference<Window> Window, WorkspaceRole Role)> Windows = [];

    internal static void Attach(Window window, WorkspaceRole role, bool resourcesLoaded = false)
    {
        if (!resourcesLoaded)
        {
            window.Resources.MergedDictionaries.Add(Create(role));
        }
        Windows.Add((new WeakReference<Window>(window), role));
        ModernWindowChrome.SetWorkspaceRole(window, role);
        Apply(window.Resources, role, ThemeService.EffectiveTheme);
        ModernWindowChrome.Refresh(window);
    }

    internal static ResourceDictionary Create(WorkspaceRole role)
    {
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        _ = new FrameworkElement();
        var resources = new ResourceDictionary();
        resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/MyCapture;component/Themes/Tokens.xaml", UriKind.Absolute) });
        // The window's palette is a set of frozen brushes swapped on every theme change.
        Apply(resources, role, ThemeService.EffectiveTheme);
        var symbols = new ResourceDictionary { Source = new Uri("pack://application:,,,/MyCapture;component/Themes/Symbols.xaml", UriKind.Absolute) };
        resources.MergedDictionaries.Add(symbols);
        var controls = new ResourceDictionary { Source = new Uri("pack://application:,,,/MyCapture;component/Themes/Controls.xaml", UriKind.Absolute) };
        resources.MergedDictionaries.Add(controls);
        return resources;
    }

    internal static void Refresh(AppTheme theme)
    {
        for (int i = Windows.Count - 1; i >= 0; i--)
        {
            if (Windows[i].Window.TryGetTarget(out Window? window)) Apply(window.Resources, Windows[i].Role, theme);
            else Windows.RemoveAt(i);
        }
    }

    /// <summary>
    /// The inline Workspace overrides, exposed so a test can assert their contrast. This is a
    /// third accent definition site separate from Tokens.xaml and ThemeCatalog, and it was the
    /// only one no test covered.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> WorkspaceOverrides(WorkspaceRole role)
    {
        bool light = role == WorkspaceRole.Gallery;
        return new Dictionary<string, string>
        {
            ["Surface.Canvas"] = light ? "#edf3f2" : "#141b22",
            ["Surface.Base"] = light ? "#edf3f2" : "#141b22",
            ["Surface.Raised"] = light ? "#ffffff" : "#202b34",
            ["Surface.Overlay"] = light ? "#f4f8f7" : "#25323c",
            ["Surface.Sunken"] = light ? "#e5eeeb" : "#10171d",
            ["Surface.Hover"] = light ? "#e0eeea" : "#2a3c43",
            ["Surface.Pressed"] = light ? "#cde2db" : "#304951",
            ["Text.Primary"] = light ? "#10211e" : "#ffffff",
            ["Text.Secondary"] = light ? "#2f3c4f" : "#d0dce3",
            ["Text.Muted"] = light ? "#495566" : "#a9bac3",
            ["Text.OnAccent"] = "#0A0818",
            ["Text.SelectionBackground"] = light ? "#10211e" : "#ffffff",
            ["Text.SelectionForeground"] = light ? "#edf3f2" : "#141b22",
            ["Accent.Default"] = "#7C6CFF",
            ["Accent.Hover"] = "#9789FF",
            ["Accent.Pressed"] = "#7A68EE",
            ["Accent.Subtle"] = light ? "#ECE9FF" : "#1A1733",
            ["Accent.Cool"] = light ? "#5A48E0" : "#9789FF",
            ["Accent.Gradient"] = "#7C6CFF",
            ["Border.Subtle"] = light ? "#ccdcd6" : "#354650",
            ["Border.Control"] = light ? "#7a8d86" : "#627784",
            ["Border.Focus"] = light ? "#5A48E0" : "#9789FF",
            ["Border.Accent"] = "#7C6CFF",
            ["Timeline.Background"] = light ? "#edf3f2" : "#141b22",
            ["Timeline.Track"] = light ? "#e5eeeb" : "#202b34",
            ["Timeline.Playhead"] = light ? "#4B3CC0" : "#FFFFFF",
            ["Timeline.TextLayer"] = light ? "#2F63D1" : "#7FA6FF",
            ["Timeline.FrameLayer"] = light ? "#0E8278" : "#3FD8C9",
            ["Timeline.LayerText"] = light ? "#FFFFFF" : "#0A0818",
        };
    }

    internal static void Apply(ResourceDictionary resources, WorkspaceRole role, AppTheme theme,
        bool? systemHighContrast = null, AppAccent? accent = null)
    {
        bool contrast = systemHighContrast ?? ThemeService.IsSystemHighContrast;
        theme = ThemeService.ResolveTheme(theme);
        AppAccent selectedAccent = AppAccent.Red;
        AppTheme palette = theme == AppTheme.Workspace
            ? role == WorkspaceRole.Gallery ? AppTheme.Daylight : AppTheme.Midnight : theme;
        ThemeService.ApplyResources(palette, resources, ThemeService.CaptureGuideColor, contrast, selectedAccent);
        // The inline role colours are a palette of their own; a Windows contrast theme replaces them too.
        if (theme != AppTheme.Workspace || contrast) return;
        var values = WorkspaceOverrides(role);
        foreach ((string key, string value) in values)
        {
            if (resources[key] is SolidColorBrush brush)
                resources[key] = ThemeBrushes.Recolored(brush, (Color)ColorConverter.ConvertFromString(value));
        }
        if (selectedAccent == AppAccent.Red)
        {
            var surfaces = ThemeCatalog.ColorsFor(palette).ToDictionary(pair => pair.Key, pair =>
            {
                Color c = ((SolidColorBrush)resources[pair.Key]).Color;
                return new ThemeColor(c.A, c.R, c.G, c.B);
            });
            var red = AccentPalette.RedOverrides(role == WorkspaceRole.Gallery, surfaces);
            foreach ((string key, ThemeColor color) in red)
            {
                if (resources[key] is SolidColorBrush brush)
                    resources[key] = ThemeBrushes.Recolored(brush, Color.FromArgb(color.A, color.R, color.G, color.B));
            }
            resources["Accent.Foreground"] = resources["Accent.Cool"];
            resources["Border.SelectedTool"] = resources["Text.Primary"];
            resources["Surface.Badge"] = resources["Surface.Raised"];
            resources["Surface.BadgeStrong"] = resources["Surface.Raised"];
            resources["Text.Badge"] = resources["Text.Primary"];
        }
        // Surface.Hover was just replaced; the folder card's wash aliases the new brush.
        ThemeService.ApplyFolderHoverResources(resources, contrast: false);
        ThemeBrushes.Refresh(resources);
    }
}

public sealed class GalleryWorkspaceResources : ResourceDictionary
{
    public GalleryWorkspaceResources() => MergedDictionaries.Add(WorkspaceTheme.Create(WorkspaceRole.Gallery));
}
