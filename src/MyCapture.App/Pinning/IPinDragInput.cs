using System.Windows;
using System.Windows.Input;
using MyCapture.Platform.Display;

namespace MyCapture.App.Pinning;

/// <summary>Pointer observations used by the real WPF drag callbacks; diagnostics supply synthetic input.</summary>
internal interface IPinDragInput
{
    ModifierKeys Modifiers { get; }
    Point LocalAnchor(MouseButtonEventArgs args, PinWindow owner);
    (int X, int Y) CursorPosition { get; }
    bool LeftButtonPressed(MouseEventArgs args);
    bool TryCapture(PinWindow owner);
    bool HasCapture(PinWindow owner);
    void Release(PinWindow owner);
}

internal sealed class NativePinDragInput : IPinDragInput
{
    internal static NativePinDragInput Instance { get; } = new();
    public ModifierKeys Modifiers => Keyboard.Modifiers;
    public Point LocalAnchor(MouseButtonEventArgs args, PinWindow owner) => args.GetPosition(owner);
    public (int X, int Y) CursorPosition => WindowStyleFacade.GetCursorPosition();
    public bool LeftButtonPressed(MouseEventArgs args) => args.LeftButton == MouseButtonState.Pressed;
    public bool TryCapture(PinWindow owner) => owner.CaptureMouse();
    public bool HasCapture(PinWindow owner) => owner.IsMouseCaptured;
    public void Release(PinWindow owner) => owner.ReleaseMouseCapture();
}
