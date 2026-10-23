using System.Windows;
using System.Windows.Media;
using MyCapture.App.Themes;
using MyCapture.Core.Themes;

namespace MyCapture.App.Capture;

/// <summary>
/// The colour that tells the recording region selector apart from the screenshot selector: the
/// theme's recording token (the red of the recording frame that follows), or the system highlight
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
            ThemeColor live = ThemeCatalog.ColorsFor(theme)["Recording.Live"];
            color = Color.FromArgb(live.A, live.R, live.G, live.B);
        }

        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    internal static Brush Resolve() => Create(ThemeService.EffectiveTheme, ThemeService.IsSystemHighContrast);
}
