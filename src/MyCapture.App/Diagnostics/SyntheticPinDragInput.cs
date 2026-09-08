using System.Reflection;
using System.Windows;
using System.Windows.Input;
using MyCapture.App.Pinning;

namespace MyCapture.App.Diagnostics;

/// <summary>Runs actual PinWindow mouse callbacks without changing the user's OS pointer or buttons.</summary>
internal sealed class SyntheticPinDragInput : IPinDragInput
{
    private const BindingFlags CallbackFlags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
    private static readonly Action<PinWindow, MouseButtonEventArgs> DownCallback =
        typeof(PinWindow).GetMethod("OnMouseLeftButtonDown", CallbackFlags)!.CreateDelegate<Action<PinWindow, MouseButtonEventArgs>>();
    private static readonly Action<PinWindow, MouseEventArgs> MoveCallback =
        typeof(PinWindow).GetMethod("OnMouseMove", CallbackFlags)!.CreateDelegate<Action<PinWindow, MouseEventArgs>>();
    private static readonly Action<PinWindow, MouseButtonEventArgs> UpCallback =
        typeof(PinWindow).GetMethod("OnMouseLeftButtonUp", CallbackFlags)!.CreateDelegate<Action<PinWindow, MouseButtonEventArgs>>();
    private static readonly Action<PinWindow, MouseEventArgs> LostCallback =
        typeof(PinWindow).GetMethod("OnLostMouseCapture", CallbackFlags)!.CreateDelegate<Action<PinWindow, MouseEventArgs>>();
    private readonly MouseButtonEventArgs _down = new(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent };
    private readonly MouseButtonEventArgs _up = new(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent };
    private readonly MouseEventArgs _move = new(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseMoveEvent };

    internal bool CaptureSucceeds { get; set; } = true;
    internal bool Captured { get; set; }
    internal bool Pressed { get; set; } = true;
    internal Point Anchor { get; set; } = new(12, 12);
    internal int ReleaseCount { get; private set; }
    public ModifierKeys Modifiers => ModifierKeys.None;
    public (int X, int Y) CursorPosition { get; set; }
    public Point LocalAnchor(MouseButtonEventArgs args, PinWindow owner) => Anchor;
    public bool LeftButtonPressed(MouseEventArgs args) => Pressed;
    public bool TryCapture(PinWindow owner) => Captured = CaptureSucceeds;
    public bool HasCapture(PinWindow owner) => Captured;
    public void Release(PinWindow owner) { Captured = false; ReleaseCount++; }

    internal void Down(PinWindow window) => DownCallback(window, _down);
    internal void Move(PinWindow window) => MoveCallback(window, _move);
    internal void Up(PinWindow window) => UpCallback(window, _up);
    internal void LoseCapture(PinWindow window)
    {
        Captured = false;
        LostCallback(window, _move);
    }
}
