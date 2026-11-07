using System.Windows;

namespace MyCapture.App.Themes;

/// <summary>Sizes the app's popups share.</summary>
public static class PopupMetrics
{
    /// <summary>
    /// The tallest a submenu may be: the work area less the clear band its popup window keeps
    /// above and below it for its shadow (its 12-DIP margin, <see cref="ThemedShellPresenter.ShadowMargin"/>),
    /// so the whole popup fits the work area and a longer list scrolls.
    /// </summary>
    public static double SubmenuMaxHeight =>
        Math.Max(0, SystemParameters.WorkArea.Height - (2 * ThemedShellPresenter.ShadowMargin));
}
