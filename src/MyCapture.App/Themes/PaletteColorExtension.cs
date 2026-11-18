using System.Windows.Markup;
using System.Windows.Media;
using MyCapture.Core.Themes;

namespace MyCapture.App.Themes;

/// <summary>
/// Resolves startup colours from the same immutable palette that ThemeService applies live.
/// This extension reads no system settings and does not retain a target or resource dictionary.
/// </summary>
[MarkupExtensionReturnType(typeof(Color))]
public sealed class PaletteColorExtension(string key) : MarkupExtension
{
    public string Key { get; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        ThemeColor color = ThemeCatalog.ColorsFor(AppTheme.Midnight)[Key];
        return Color.FromArgb(color.A, color.R, color.G, color.B);
    }
}
