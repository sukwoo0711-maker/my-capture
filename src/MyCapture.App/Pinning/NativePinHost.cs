using System.Windows.Input;
using MyCapture.App.Themes;

namespace MyCapture.App.Pinning;

/// <summary>What a key does on a native pin in increment 1.</summary>
internal enum NativePinKeyAction
{
    /// <summary>Not a pin key: the native host passes it on and stays.</summary>
    None,
    Close,
    ZoomIn,
    ZoomOut,
    ResetZoom,

    /// <summary>A pin key that only the WPF host handles yet: switch, then replay the key there.</summary>
    Switch,
}

/// <summary>What a left press does on a native pin.</summary>
internal enum NativePinPressAction
{
    /// <summary>Move, or resize at an edge.</summary>
    Gesture,

    /// <summary>A Ctrl single click: focus only, as on a WPF pin.</summary>
    Ignore,

    /// <summary>A plain double-click with the close-on-double-click setting.</summary>
    Close,

    /// <summary>Ctrl+double-click: recognize text on the WPF host.</summary>
    SwitchRecognize,

    /// <summary>Shift+double-click: fold on the WPF host.</summary>
    SwitchFold,
}

/// <summary>
/// Presents a <see cref="PinWindow"/> through one <see cref="NativePinWindow"/>. The pin stays the
/// logical pin (state, identity, events); this host turns the native window's input into pin
/// commands and asks the pin to move to its WPF window for anything increment 1 does not draw natively.
/// The WPF window of a native-hosted pin is never shown, so it has no HwndSource or render target.
/// </summary>
internal sealed class NativePinHost : IDisposable
{
    private readonly PinWindow _pin;
    private bool _detached;

    internal NativePinHost(PinWindow pin, NativePinWindow window)
    {
        _pin = pin ?? throw new ArgumentNullException(nameof(pin));
        Window = window ?? throw new ArgumentNullException(nameof(window));
        window.KeyHandler = OnKey;
        window.PressHandler = OnPress;
        window.ContextMenuRequested = point => _pin.SwitchToWpf(PinSwitchReason.ContextMenu, () => _pin.OpenContextMenuAfterSwitch(point));
        window.CloseRequested = () => _pin.Close();
        window.ActiveChanged = _pin.OnNativeActiveChanged;
        window.GestureEnded = _pin.OnNativeGestureEnded;
        window.UserZoomChanged = _pin.OnNativeUserZoomChanged;
        window.Settled = _pin.OnNativeSettled;
        window.UserOpacityChanged = _pin.OnNativeUserOpacityChanged;
        window.Closed = _pin.OnNativeClosed;
        window.Failed = _pin.OnNativeFailed;
    }

    internal NativePinWindow Window { get; }

    /// <summary>Reads Ctrl, Shift and Alt for a key; tests substitute the state they post.</summary>
    internal Func<ModifierKeys> ReadModifiers { get; set; } = ReadKeyboardModifiers;

    /// <summary>
    /// Creates the native window for <paramref name="pin"/> at a physical origin. The pin's state is shared;
    /// the picture is copied once into the native source. Throws when Windows or memory refuses.
    /// </summary>
    internal static NativePinHost Create(PinWindow pin, PinContent content, double leftPhysical, double topPhysical, double dpiScale)
    {
        ArgumentNullException.ThrowIfNull(pin);
        ArgumentNullException.ThrowIfNull(content);
        if (CreateFailureForTest is { } fail) throw fail();
        NativePinPixelSource source = NativePinPixelSource.FromBitmap(content.Image);
        NativePinWindow window = new(source, pin.State, leftPhysical, topPhysical, dpiScale <= 0 ? 1 : dpiScale,
            ThemeService.Current, pin.FrameAccentBrush, pin.FrameNeutralBrush, UiText.Get("Text_03151E566E1D"));
        try { return new NativePinHost(pin, window); }
        catch { window.Dispose(); throw; }
    }

    /// <summary>Fault injection for the fallback: an exception to throw instead of creating the window.</summary>
    internal static Func<Exception>? CreateFailureForTest { get; set; }

    /// <summary>Stops turning native input into pin commands; the window itself stays until it is closed.</summary>
    internal void Detach()
    {
        if (_detached) return;
        _detached = true;
        Window.KeyHandler = static _ => true;
        Window.PressHandler = static _ => true;
        Window.ContextMenuRequested = static _ => { };
        Window.CloseRequested = static () => { };
        Window.ActiveChanged = null;
        Window.GestureEnded = null;
        Window.UserZoomChanged = null;
        Window.Settled = null;
        Window.UserOpacityChanged = null;
        // Failed stays: a closing pin whose exit fails still has to close its window.
    }

    public void Dispose()
    {
        Detach();
        Window.Closed = null;
        Window.Failed = null;
        if (!Window.IsClosed) Window.Dispose();
    }

    /// <summary>The increment-1 key table. Keys that increment 2 draws natively switch for now.</summary>
    internal static NativePinKeyAction RouteKey(Key key, ModifierKeys modifiers)
    {
        bool ctrl = modifiers.HasFlag(ModifierKeys.Control);
        return key switch
        {
            Key.Escape or Key.Delete => NativePinKeyAction.Close,
            Key.Add or Key.OemPlus => NativePinKeyAction.ZoomIn,
            Key.Subtract or Key.OemMinus => NativePinKeyAction.ZoomOut,
            Key.D0 or Key.NumPad0 => NativePinKeyAction.ResetZoom,
            Key.F2 => NativePinKeyAction.Switch,
            Key.O when modifiers == ModifierKeys.None => NativePinKeyAction.Switch,
            Key.Left or Key.Right or Key.Up or Key.Down => NativePinKeyAction.Switch,
            Key.C or Key.S when ctrl => NativePinKeyAction.Switch,
            _ => NativePinKeyAction.None,
        };
    }

    /// <summary>The press table, in the order PinWindow's mouse handlers decide.</summary>
    internal static NativePinPressAction RoutePress(NativePinPress press, bool closeOnDoubleClick)
    {
        bool twice = press.Clicks >= 2;
        // PinWindow.HandleCollapseDoubleClick runs first, in the preview pass.
        if (twice && press.Shift) return NativePinPressAction.SwitchFold;
        if (press.Control) return twice ? NativePinPressAction.SwitchRecognize : NativePinPressAction.Ignore;
        if (twice && closeOnDoubleClick) return NativePinPressAction.Close;
        return NativePinPressAction.Gesture;
    }

    private bool OnKey(int virtualKey)
    {
        if (_pin.IsClosed) return true;
        Key key = KeyInterop.KeyFromVirtualKey(virtualKey);
        ModifierKeys modifiers = ReadModifiers();
        switch (RouteKey(key, modifiers))
        {
            case NativePinKeyAction.Close: _pin.CloseWithExit(); return true;
            case NativePinKeyAction.ZoomIn: Window.StepZoom(1); return true;
            case NativePinKeyAction.ZoomOut: Window.StepZoom(-1); return true;
            case NativePinKeyAction.ResetZoom: Window.ResetZoom(); return true;
            case NativePinKeyAction.Switch:
                _pin.SwitchToWpf(PinSwitchReason.Key, () => _pin.ReplayKeyAfterSwitch(key, modifiers));
                return true;
            default: return false;
        }
    }

    private bool OnPress(NativePinPress press)
    {
        if (_pin.IsClosed) return true;
        switch (RoutePress(press, _pin.CloseOnDoubleClick))
        {
            case NativePinPressAction.Ignore: return true;
            case NativePinPressAction.Close: _pin.CloseWithExit(); return true;
            case NativePinPressAction.SwitchRecognize:
                _pin.SwitchToWpf(PinSwitchReason.Gesture, _pin.RecognizeAfterSwitch);
                return true;
            case NativePinPressAction.SwitchFold:
                _pin.SwitchToWpf(PinSwitchReason.Gesture, _pin.ToggleCollapsed);
                return true;
            default: return false;
        }
    }

    private static ModifierKeys ReadKeyboardModifiers()
    {
        ModifierKeys modifiers = ModifierKeys.None;
        if ((NativePinWindowInterop.GetKeyState(0x11) & 0x8000) != 0) modifiers |= ModifierKeys.Control;
        if ((NativePinWindowInterop.GetKeyState(0x10) & 0x8000) != 0) modifiers |= ModifierKeys.Shift;
        if ((NativePinWindowInterop.GetKeyState(0x12) & 0x8000) != 0) modifiers |= ModifierKeys.Alt;
        return modifiers;
    }
}
