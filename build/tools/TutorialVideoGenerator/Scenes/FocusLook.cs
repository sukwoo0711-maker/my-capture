using System.Windows;
using System.Windows.Controls;

namespace MyCapture.TutorialGenerator.Scenes;

/// <summary>
/// An off-screen window is never activated, so its controls cannot hold keyboard focus. Where a
/// clip shows a focused control, this applies the control template's own IsKeyboardFocused
/// setters (the focus border and ring), so it looks as it does in the active window.
/// </summary>
internal static class FocusLook
{
    internal static void Apply(Control control, bool focused = true)
    {
        control.ApplyTemplate();
        if (control.Template?.FindName("FocusRing", control) is UIElement ring) ring.Opacity = focused ? 1 : 0;
        foreach (string part in new[] { "Chrome", "HoverLayer" })
        {
            if (control.Template?.FindName(part, control) is not Border border) continue;
            if (part == "HoverLayer" && control is not TextBox) continue;
            if (focused) border.SetResourceReference(Border.BorderBrushProperty, "Border.Focus");
            else border.ClearValue(Border.BorderBrushProperty);
        }

        if (control is TextBox box) box.IsInactiveSelectionHighlightEnabled = focused;
    }
}
