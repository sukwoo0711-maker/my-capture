using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace MyCapture.App.Capture;

internal sealed partial class NativeSelectorChrome
{
    internal bool KeyboardActivation { get; private set; }

    // Only the interactive bar participates. The readout, magnifier and deferred guides are
    // separate HWNDs for rendering, but must never become extra stops in the keyboard route.
    private ButtonBase[] FocusStops() => Descendants(_bar).OfType<ButtonBase>()
        .Where(button => button.IsVisible && button.IsEnabled && button.Focusable && KeyboardNavigation.GetIsTabStop(button))
        .OrderBy(KeyboardNavigation.GetTabIndex).ToArray();

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (DependencyObject descendant in Descendants(child)) yield return descendant;
        }
    }

    internal bool FocusFromBackground(bool reverse)
    {
        if (_closed || _owner is null || !_barWindow.IsVisible) return false;
        ButtonBase[] stops = FocusStops();
        return stops.Length > 0 && (reverse ? stops[^1] : stops[0]).Focus();
    }

    internal bool NavigateTab(bool reverse)
    {
        if (_closed || _owner is not { } owner) return false;
        ButtonBase[] stops = FocusStops();
        int current = Array.FindIndex(stops, button => button.IsKeyboardFocusWithin);
        if (current < 0) return FocusFromBackground(reverse);
        int next = current + (reverse ? -1 : 1);
        if (next >= 0 && next < stops.Length) return stops[next].Focus();
        Keyboard.ClearFocus();
        _ = owner.Activate();
        // SetForegroundWindow may report false for an already active owner even though SetFocus
        // succeeded. The tab boundary was handled; WPF must not wrap back into this HWND.
        return true;
    }
}
