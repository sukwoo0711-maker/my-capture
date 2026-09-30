using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MyCapture.App.Gallery;

/// <summary>Uses the same native tooltip for keyboard navigation and pointer hover.</summary>
public static class GalleryCaptionPreview
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(GalleryCaptionPreview), new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetIsEnabled(DependencyObject owner) => (bool)owner.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject owner, bool value) => owner.SetValue(IsEnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject owner, DependencyPropertyChangedEventArgs e)
    {
        if (owner is not ListBoxItem item) return;
        if ((bool)e.NewValue)
        {
            item.GotKeyboardFocus += OnGotKeyboardFocus;
            item.LostKeyboardFocus += OnLostKeyboardFocus;
            item.PreviewKeyDown += OnPreviewKeyDown;
            item.Unloaded += OnUnloaded;
            item.IsVisibleChanged += OnVisibilityChanged;
        }
        else
        {
            item.GotKeyboardFocus -= OnGotKeyboardFocus;
            item.LostKeyboardFocus -= OnLostKeyboardFocus;
            item.PreviewKeyDown -= OnPreviewKeyDown;
            item.Unloaded -= OnUnloaded;
            item.IsVisibleChanged -= OnVisibilityChanged;
            Close(item);
        }
    }

    private static void OnGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Buttons retain their own action hints; only the card itself reveals its
        // full caption on focus. Pointer hover still uses the native hover delay.
        if (sender is ListBoxItem item && ReferenceEquals(e.NewFocus, item)
            && item.ToolTip is ToolTip tip)
        {
            tip.PlacementTarget = item;
            tip.IsOpen = true;
        }
    }

    private static void OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => Close((ListBoxItem)sender);
    private static void OnUnloaded(object sender, RoutedEventArgs e) => Close((ListBoxItem)sender);
    private static void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!(bool)e.NewValue) Close((ListBoxItem)sender);
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || ((ListBoxItem)sender).ToolTip is not ToolTip { IsOpen: true }) return;
        Close((ListBoxItem)sender);
        e.Handled = true;
    }

    private static void Close(ListBoxItem item)
    {
        if (item.ToolTip is ToolTip tip && ReferenceEquals(tip.PlacementTarget, item)) tip.IsOpen = false;
    }
}
