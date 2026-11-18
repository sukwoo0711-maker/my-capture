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
        window.Closed += OnWindowClosed;
        ModernWindowChrome.SetWorkspaceRole(window, role);
        Apply(window.Resources, role, ThemeService.EffectiveTheme);
        ModernWindowChrome.Refresh(window);
    }

    private static void OnWindowClosed(object? sender, EventArgs args)
    {
        if (sender is not Window closed) return;
        closed.Closed -= OnWindowClosed;
        // A closed window may remain referenced by its owner. It no longer needs
        // palette updates; hidden windows stay registered so they can reopen.
        for (int i = Windows.Count - 1; i >= 0; i--)
            if (!Windows[i].Window.TryGetTarget(out Window? window) || ReferenceEquals(window, closed))
                Windows.RemoveAt(i);
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

    /// <summary>Workspace roles consume the same canonical palettes as all other windows.</summary>
    internal static IReadOnlyDictionary<string, string> WorkspaceOverrides(WorkspaceRole role) =>
        ThemeCatalog.ColorsFor(PaletteFor(role)).ToDictionary(pair => pair.Key, pair =>
            $"#{pair.Value.A:X2}{pair.Value.R:X2}{pair.Value.G:X2}{pair.Value.B:X2}", StringComparer.Ordinal);

    private static AppTheme PaletteFor(WorkspaceRole role) =>
        role == WorkspaceRole.Gallery ? AppTheme.Daylight : AppTheme.Midnight;

    internal static void Apply(ResourceDictionary resources, WorkspaceRole role, AppTheme theme,
        bool? systemHighContrast = null, AppAccent? accent = null)
    {
        bool contrast = systemHighContrast ?? ThemeService.IsSystemHighContrast;
        theme = ThemeService.ResolveTheme(theme);
        AppTheme palette = theme == AppTheme.Workspace ? PaletteFor(role) : theme;
        ThemeService.ApplyResources(palette, resources, ThemeService.CaptureGuideColor, contrast, AppAccent.Red);
    }
}

public sealed class GalleryWorkspaceResources : ResourceDictionary
{
    public GalleryWorkspaceResources() => MergedDictionaries.Add(WorkspaceTheme.Create(WorkspaceRole.Gallery));
}
