using System.Windows;

namespace MyCapture.App.Themes;

/// <summary>
/// The height a button takes from the shared Button style: Button.Height (32) for a plain button,
/// or CommandButton.Height.Standard (44) once its face reserves the Alt shortcut line.
/// </summary>
/// <remarks>
/// The style binds Height and MinHeight to <see cref="RestHeightProperty"/> and a trigger raises
/// it while <see cref="ReservesShortcutLineProperty"/> is set, so a style that sets its own
/// Height keeps it and a command face never clips its shortcut line.
/// </remarks>
public static class ButtonSizing
{
    public static readonly DependencyProperty RestHeightProperty = DependencyProperty.RegisterAttached(
        "RestHeight", typeof(double), typeof(ButtonSizing), new PropertyMetadata(32d));

    public static readonly DependencyProperty ReservesShortcutLineProperty = DependencyProperty.RegisterAttached(
        "ReservesShortcutLine", typeof(bool), typeof(ButtonSizing), new PropertyMetadata(false));

    public static double GetRestHeight(DependencyObject element) => (double)element.GetValue(RestHeightProperty);

    public static void SetRestHeight(DependencyObject element, double value) => element.SetValue(RestHeightProperty, value);

    public static bool GetReservesShortcutLine(DependencyObject element) => (bool)element.GetValue(ReservesShortcutLineProperty);

    public static void SetReservesShortcutLine(DependencyObject element, bool value) =>
        element.SetValue(ReservesShortcutLineProperty, value);
}
