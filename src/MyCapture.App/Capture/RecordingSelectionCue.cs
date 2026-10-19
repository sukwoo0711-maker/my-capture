using System.Windows;
using System.Windows.Media;
using MyCapture.App.Themes;
using MyCapture.Core.Themes;

namespace MyCapture.App.Capture;

/// <summary>
/// The colour that tells the recording region selector apart from the screenshot selector: the
/// theme's existing danger token (the red the recording bar already uses), or the system highlight
/// colour when Windows high contrast is on.
/// </summary>
internal static class RecordingSelectionCue
{
    internal static Brush Create(AppTheme theme, bool systemHighContrast)
    {
        Color color;
        if (systemHighContrast)
        {
            color = SystemColors.HighlightColor;
        }
        else
        {
            ThemeColor danger = ThemeCatalog.ColorsFor(theme)["State.Danger"];
            color = Color.FromArgb(danger.A, danger.R, danger.G, danger.B);
        }

        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    internal static Brush Resolve() => Create(ThemeService.EffectiveTheme, ThemeService.IsSystemHighContrast);
}
