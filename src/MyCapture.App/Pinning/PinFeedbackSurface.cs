using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using MyCapture.App.Capture;
using MyCapture.App.Themes;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Display;

namespace MyCapture.App.Pinning;

/// <summary>
/// The bottom message of a native-hosted pin: the pin's own feedback element in a small window owned by
/// the native HWND, at the pin's bottom centre. Like the message inside a WPF pin it is part of the pin
/// (screen captures show it), it never activates, a press on it moves the pin and the wheel zooms it.
/// It exists only while a message is on the screen.
/// </summary>
internal sealed class PinFeedbackSurface : Window
{
    internal PinFeedbackSurface(UIElement message)
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SizeToContent = SizeToContent.WidthAndHeight;
        // As in the pin window, so the message has the same size and place on either host.
        UseLayoutRounding = true;
        Left = Top = -32000;
        FluidMotion.SetWindowEntrance(this, false);
        Content = message;
        SourceInitialized += (_, _) =>
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            MyCapture.App.Diagnostics.ResidentProbeWindowHook.SourceInitialized?.Invoke(this);
            WindowStyleFacade.SetNoActivate(hwnd, true);
            WindowStyleFacade.ExcludeFromAltTab(hwnd);
            CaptureWindowExclusion.DisableTransitions(this);
            HwndSource.FromHwnd(hwnd)?.AddHook(NoActivateHook);
        };
    }

    /// <summary>A press outside the close button: the pin moves from this screen point (physical pixels).</summary>
    internal Action<Point>? MoveRequested { get; set; }

    /// <summary>A wheel turn over the message: the delta, the screen point and whether Ctrl is down.</summary>
    internal Action<int, Point, bool>? WheelRequested { get; set; }

    /// <summary>The message's place for a pin surface: centred on its bottom edge, in physical pixels.</summary>
    internal static RectD Place(RectD pin, double width, double height)
    {
        double w = Math.Ceiling(Math.Max(0, width)), h = Math.Ceiling(Math.Max(0, height));
        // The offset inside the pin is rounded, as WPF's layout rounding rounds it inside a WPF pin.
        return new RectD(pin.Left + Math.Round((pin.Width - w) / 2), pin.Top + pin.Height - h, w, h);
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        if (IsOnButton(e.OriginalSource as DependencyObject) || MoveRequested is not { } move) return;
        e.Handled = true;
        move(PointToScreen(e.GetPosition(this)));
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (WheelRequested is not { } wheel) return;
        e.Handled = true;
        wheel(e.Delta, PointToScreen(e.GetPosition(this)), Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
    }

    /// <summary>Gives the message back before the window goes, so the pin can show it elsewhere.</summary>
    internal void Release()
    {
        Content = null;
        Close();
    }

    private static bool IsOnButton(DependencyObject? source)
    {
        for (DependencyObject? node = source; node is not null;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node is ButtonBase) return true;
        }
        return false;
    }

    private static IntPtr NoActivateHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != 0x0021) return IntPtr.Zero;
        handled = true;
        return new IntPtr(3); // MA_NOACTIVATE
    }
}
