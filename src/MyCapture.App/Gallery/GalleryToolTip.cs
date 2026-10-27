using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MyCapture.App.Gallery;

internal static class GalleryToolTip
{
    private static readonly DependencyProperty KeyboardOpenedProperty = DependencyProperty.RegisterAttached(
        "KeyboardOpened", typeof(bool), typeof(GalleryToolTip), new PropertyMetadata(false));

    internal static void OpenForKeyboard(ToolTip tip)
    {
        tip.SetValue(KeyboardOpenedProperty, true);
        tip.IsOpen = true;
    }

    internal static void CloseKeyboardPreview(ToolTip tip)
    {
        if (!(bool)tip.GetValue(KeyboardOpenedProperty)) return;
        tip.SetValue(KeyboardOpenedProperty, false);
        tip.IsOpen = false;
    }

    internal static void DismissOnEscape(FrameworkElement owner, ToolTip tip, Action dismiss)
    {
        Window? window = null;
        KeyEventHandler handler = (_, args) =>
        {
            if (args.Key != Key.Escape || !tip.IsOpen) return;
            dismiss();
            args.Handled = true;
        };
        tip.Opened += (_, _) =>
        {
            window = Window.GetWindow(owner);
            if (window is not null) window.PreviewKeyDown += handler;
        };
        tip.Closed += (_, _) =>
        {
            tip.SetValue(KeyboardOpenedProperty, false);
            if (window is not null) window.PreviewKeyDown -= handler;
            window = null;
        };
    }
}
