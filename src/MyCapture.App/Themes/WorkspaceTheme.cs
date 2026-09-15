using System.Windows;
using System.Windows.Media;
using System.Windows.Data;
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
        Apply(window.Resources, role, ThemeService.Current);
        ModernWindowChrome.Refresh(window);
    }

    internal static ResourceDictionary Create(WorkspaceRole role)
    {
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        _ = new FrameworkElement();
        var resources = new ResourceDictionary();
        resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/MyCapture;component/Themes/Tokens.xaml", UriKind.Absolute) });
        foreach (string key in ThemeCatalog.ColorsFor(AppTheme.Midnight).Keys)
        {
            if (resources[key] is not SolidColorBrush original) continue;
            var brush = original.CloneCurrentValue();
            // An expression keeps the brush live when a consuming style is sealed.
            BindingOperations.SetBinding(brush, SolidColorBrush.ColorProperty,
                new Binding(nameof(SolidColorBrush.Color)) { Source = original, Mode = BindingMode.OneWay });
            resources[key] = brush;
        }
        Apply(resources, role, ThemeService.Current);
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

    internal static void Apply(ResourceDictionary resources, WorkspaceRole role, AppTheme theme)
    {
        AppTheme palette = theme == AppTheme.Workspace
            ? role == WorkspaceRole.Gallery ? AppTheme.Daylight : AppTheme.Midnight : theme;
        ThemeService.ApplyResources(palette, resources);
        if (theme != AppTheme.Workspace) return;
        bool light = role == WorkspaceRole.Gallery;
        var values = new Dictionary<string, string>
        {
            ["Surface.Canvas"] = light ? "#edf3f2" : "#141b22",
            ["Surface.Base"] = light ? "#edf3f2" : "#141b22",
            ["Surface.Raised"] = light ? "#ffffff" : "#202b34",
            ["Surface.Overlay"] = light ? "#f4f8f7" : "#25323c",
            ["Surface.Sunken"] = light ? "#e5eeeb" : "#10171d",
            ["Surface.Hover"] = light ? "#e0eeea" : "#2a3c43",
            ["Surface.Pressed"] = light ? "#cde2db" : "#304951",
            ["Text.Primary"] = light ? "#10211e" : "#ffffff",
            ["Text.Secondary"] = light ? "#243832" : "#e3eef4",
            ["Text.Muted"] = light ? "#334a43" : "#c5d4dc",
            ["Text.OnAccent"] = light ? "#ffffff" : "#102b2b",
            ["Accent.Default"] = light ? "#167768" : "#5ad9c5",
            ["Accent.Hover"] = light ? "#126557" : "#81e5d4",
            ["Accent.Pressed"] = light ? "#10584b" : "#3bbca8",
            ["Accent.Subtle"] = light ? "#daeee7" : "#183e3c",
            ["Accent.Cool"] = light ? "#167768" : "#5ad9c5",
            ["Accent.Gradient"] = light ? "#167768" : "#5ad9c5",
            ["Border.Subtle"] = light ? "#ccdcd6" : "#354650",
            ["Border.Focus"] = light ? "#167768" : "#5ad9c5",
            ["Border.Accent"] = light ? "#167768" : "#5ad9c5",
            ["Timeline.Background"] = light ? "#edf3f2" : "#141b22",
            ["Timeline.Track"] = light ? "#e5eeeb" : "#202b34",
            ["Timeline.Playhead"] = light ? "#167768" : "#5ad9c5",
            ["Timeline.TextLayer"] = light ? "#167768" : "#319c91",
            ["Timeline.FrameLayer"] = light ? "#516c98" : "#718ac5",
        };
        foreach ((string key, string value) in values)
        {
            if (resources[key] is SolidColorBrush brush)
                brush.SetCurrentValue(SolidColorBrush.ColorProperty, (Color)ColorConverter.ConvertFromString(value));
        }
    }
}

public sealed class GalleryWorkspaceResources : ResourceDictionary
{
    public GalleryWorkspaceResources() => MergedDictionaries.Add(WorkspaceTheme.Create(WorkspaceRole.Gallery));
}
