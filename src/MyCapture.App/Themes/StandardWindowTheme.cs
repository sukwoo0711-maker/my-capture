using System.Windows;

namespace MyCapture.App.Themes;

/// <summary>Applies the shared application-window style to code-created Window subclasses.</summary>
internal static class StandardWindowTheme
{
    internal const string ResourceKey = "Window.Standard";

    internal static void Apply(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        // A keyed Window style can be assigned to any subclass; unlike an implicit Window
        // style, WPF will not discard it merely because the runtime type is more specific.
        if (window.TryFindResource(ResourceKey) is Style style)
        {
            window.Style = style;
            return;
        }

        // Keep the two behavioural guarantees even in isolated hosts that intentionally do
        // not load App.xaml (for example diagnostics and lightweight STA tests).
        ModernWindowChrome.SetEnabled(window, true);
        FluidMotion.SetWindowEntrance(window, true);
    }
}
