using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using MyCapture.Platform.Shell;
using MyCapture.Platform.Display;

namespace MyCapture.App.Capture;

/// <summary>Applies the receipt's popup policy before a submenu creates its own HWND.</summary>
internal sealed class CaptureSubmenuItem(bool excludeFromCapture, Func<bool> keyboardAccess) : MenuItem
{
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (Template.FindName("PART_Popup", this) is not Popup popup) return;
        if (excludeFromCapture && !CaptureWindowExclusion.SupportsLayeredWindows)
        {
            popup.AllowsTransparency = false;
            popup.PopupAnimation = PopupAnimation.None;
            if (popup.Child is Border border)
            {
                border.Background = CaptureWindowExclusion.OpaqueBackground();
                border.CornerRadius = new CornerRadius(0);
                border.Effect = null;
                border.Margin = new Thickness(0);
            }
        }
        popup.Opened += (_, _) =>
        {
            if (!keyboardAccess() && popup.Child is System.Windows.Media.Visual visual
                && PresentationSource.FromVisual(visual) is HwndSource source)
                WindowStyleFacade.SetNoActivate(source.Handle, true);
            if (excludeFromCapture) CaptureWindowExclusion.ExcludeSubmenuPopup(this, popup);
        };
    }
}
