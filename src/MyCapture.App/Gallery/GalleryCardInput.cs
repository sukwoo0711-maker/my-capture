using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MyCapture.App.Gallery;

/// <summary>
/// The input state a capture card draws: whether anything in the library is selected, and whether the
/// card has keyboard focus that came from the keyboard (as opposed to a click).
/// </summary>
/// <remarks>
/// A click focuses the card too, but a click is not a request to see a focus ring or an empty selection
/// box. <see cref="ShowsKeyboardFocusProperty"/> is true only while the card (or something inside it)
/// holds focus and the last thing the user did was press a key, so the ring and the box appear for Tab and
/// the arrow keys and go away the moment the mouse is used.
/// </remarks>
public static class GalleryCardInput
{
    /// <summary>Set on a card's list item to follow its keyboard focus.</summary>
    public static readonly DependencyProperty TrackProperty = DependencyProperty.RegisterAttached(
        "Track", typeof(bool), typeof(GalleryCardInput), new PropertyMetadata(false, OnTrackChanged));

    /// <summary>True while the library has a selection. The list sets it; every card below it reads it.</summary>
    public static readonly DependencyProperty SelectionActiveProperty = DependencyProperty.RegisterAttached(
        "SelectionActive", typeof(bool), typeof(GalleryCardInput),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    private static readonly DependencyPropertyKey ShowsKeyboardFocusKey = DependencyProperty.RegisterAttachedReadOnly(
        "ShowsKeyboardFocus", typeof(bool), typeof(GalleryCardInput),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    /// <summary>True while the card has keyboard-initiated focus (see the class remarks).</summary>
    public static readonly DependencyProperty ShowsKeyboardFocusProperty = ShowsKeyboardFocusKey.DependencyProperty;

    public static bool GetTrack(DependencyObject element) => (bool)element.GetValue(TrackProperty);

    public static void SetTrack(DependencyObject element, bool value) => element.SetValue(TrackProperty, value);

    public static bool GetSelectionActive(DependencyObject element) => (bool)element.GetValue(SelectionActiveProperty);

    public static void SetSelectionActive(DependencyObject element, bool value) => element.SetValue(SelectionActiveProperty, value);

    public static bool GetShowsKeyboardFocus(DependencyObject element) => (bool)element.GetValue(ShowsKeyboardFocusProperty);

    /// <summary>Forces the keyboard-focus state, for tests and evidence renderings that have no real input.</summary>
    internal static void SetKeyboardFocusVisibleForTest(UIElement card, bool visible) =>
        card.SetValue(ShowsKeyboardFocusKey, visible);

    private static void OnTrackChanged(DependencyObject owner, DependencyPropertyChangedEventArgs e)
    {
        if (owner is not UIElement element) return;
        element.GotKeyboardFocus -= OnGotKeyboardFocus;
        element.LostKeyboardFocus -= OnLostKeyboardFocus;
        element.RemoveHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(OnPreviewKeyDown));
        element.RemoveHandler(UIElement.PreviewMouseDownEvent, new MouseButtonEventHandler(OnPreviewMouseDown));
        if (!(bool)e.NewValue)
        {
            element.SetValue(ShowsKeyboardFocusKey, false);
            return;
        }

        element.GotKeyboardFocus += OnGotKeyboardFocus;
        element.LostKeyboardFocus += OnLostKeyboardFocus;
        // Also when a handler above the card already took the press or the key (the list handles a card click and
        // the content area handles the card keys): the ring follows what the user did, not who handled it.
        element.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(OnPreviewKeyDown), handledEventsToo: true);
        element.AddHandler(UIElement.PreviewMouseDownEvent, new MouseButtonEventHandler(OnPreviewMouseDown), handledEventsToo: true);
    }

    private static void OnGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (InputManager.Current.MostRecentInputDevice is KeyboardDevice && Mouse.LeftButton != MouseButtonState.Pressed)
            ((UIElement)sender).SetValue(ShowsKeyboardFocusKey, true);
    }

    private static void OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        var card = (UIElement)sender;
        if (e.NewFocus is DependencyObject next && IsWithin(next, card)) return;
        card.SetValue(ShowsKeyboardFocusKey, false);
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var card = (UIElement)sender;
        if (card.IsKeyboardFocusWithin) card.SetValue(ShowsKeyboardFocusKey, true);
    }

    private static void OnPreviewMouseDown(object sender, MouseButtonEventArgs e) =>
        ((UIElement)sender).SetValue(ShowsKeyboardFocusKey, false);

    private static bool IsWithin(DependencyObject node, UIElement container)
    {
        for (DependencyObject? current = node; current is not null; current = current is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                 ? System.Windows.Media.VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, container)) return true;
        }

        return false;
    }
}
