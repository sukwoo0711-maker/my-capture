using System.Windows;

namespace MyCapture.App.Themes;

public enum SplitButtonPart { None, Main, Options }

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
    public static readonly DependencyProperty SplitPartProperty = DependencyProperty.RegisterAttached(
        "SplitPart", typeof(SplitButtonPart), typeof(ButtonSizing), new PropertyMetadata(SplitButtonPart.None, UpdateCornerRadius));

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.RegisterAttached(
        "CornerRadius", typeof(CornerRadius), typeof(ButtonSizing), new PropertyMetadata(new CornerRadius(8), UpdateCornerRadius));

    private static readonly DependencyPropertyKey ChromeCornerRadiusPropertyKey = DependencyProperty.RegisterAttachedReadOnly(
        "ChromeCornerRadius", typeof(CornerRadius), typeof(ButtonSizing), new PropertyMetadata(new CornerRadius(8)));

    public static readonly DependencyProperty ChromeCornerRadiusProperty = ChromeCornerRadiusPropertyKey.DependencyProperty;

    public static SplitButtonPart GetSplitPart(DependencyObject element) => (SplitButtonPart)element.GetValue(SplitPartProperty);
    public static void SetSplitPart(DependencyObject element, SplitButtonPart value) => element.SetValue(SplitPartProperty, value);
    public static CornerRadius GetCornerRadius(DependencyObject element) => (CornerRadius)element.GetValue(CornerRadiusProperty);
    public static void SetCornerRadius(DependencyObject element, CornerRadius value) => element.SetValue(CornerRadiusProperty, value);
    public static CornerRadius GetChromeCornerRadius(DependencyObject element) => (CornerRadius)element.GetValue(ChromeCornerRadiusProperty);

    private static void UpdateCornerRadius(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        CornerRadius radius = GetCornerRadius(element);
        element.SetValue(ChromeCornerRadiusPropertyKey, GetSplitPart(element) switch
        {
            SplitButtonPart.Main => new CornerRadius(radius.TopLeft, 0, 0, radius.BottomLeft),
            SplitButtonPart.Options => new CornerRadius(0, radius.TopRight, radius.BottomRight, 0),
            _ => radius,
        });
    }

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
