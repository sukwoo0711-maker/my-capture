using System.Windows;
using System.Windows.Controls;

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

    /// <summary>
    /// The text settings of <see cref="ResourceKey"/> for floating surfaces that keep their own
    /// chrome (the pin bar, the recording bar): the UI font, its text formatting and shaping
    /// (<see cref="ThemeFonts"/>) and pixel snapping. Their small Korean and Latin text then
    /// renders as in the standard windows instead of in the default font.
    /// </summary>
    /// <remarks>
    /// The capture card does not take it yet: its preview keeps exact aspect at every display
    /// scale without layout rounding, and its named commands are fitted to its current font.
    /// </remarks>
    internal static void ApplyFloatingText(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (window.ReadLocalValue(Control.FontFamilyProperty) == DependencyProperty.UnsetValue)
            window.SetResourceReference(Control.FontFamilyProperty, "Font.Ui");
        ThemeFonts.ApplyText(window);
        window.UseLayoutRounding = true;
        window.SnapsToDevicePixels = true;
    }
}
