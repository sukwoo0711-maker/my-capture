using System.Windows.Input;

namespace MyCapture.App;

/// <summary>Keyboard entry and one-time focus return for a normally passive receipt.</summary>
internal sealed class ReceiptKeyboardSession
{
    private readonly Func<nint> _foreground;
    private readonly Action<bool> _setPassive;
    private readonly Func<bool> _activate;
    private readonly Func<bool> _focusFirst;
    private readonly Action<nint> _restore;
    private bool _entered;
    private nint _returnTarget;

    internal ReceiptKeyboardSession(Func<nint> foreground, Action<bool> setPassive, Func<bool> activate,
        Func<bool> focusFirst, Action<nint> restore)
    {
        ArgumentNullException.ThrowIfNull(foreground);
        ArgumentNullException.ThrowIfNull(setPassive);
        ArgumentNullException.ThrowIfNull(activate);
        ArgumentNullException.ThrowIfNull(focusFirst);
        ArgumentNullException.ThrowIfNull(restore);
        _foreground = foreground;
        _setPassive = setPassive;
        _activate = activate;
        _focusFirst = focusFirst;
        _restore = restore;
    }

    internal bool Enter(nint ownHandle)
    {
        nint previous = _foreground();
        if (!_entered || previous != ownHandle)
        {
            _returnTarget = previous == ownHandle ? 0 : previous;
        }
        _setPassive(false);
        if (!_activate() || !_focusFirst())
        {
            _setPassive(true);
            _entered = false;
            _returnTarget = 0;
            return false;
        }
        _entered = true;
        return true;
    }

    /// <summary>Take before closing the receipt; return after its native window has closed.</summary>
    internal nint TakeReturnTarget(nint ownHandle)
    {
        nint previous = _returnTarget;
        bool entered = _entered;
        _entered = false;
        _returnTarget = 0;
        return entered && previous != 0 && previous != ownHandle && _foreground() == ownHandle ? previous : 0;
    }

    internal void Return(nint target)
    {
        if (target != 0) _restore(target);
    }

    /// <param name="move">True moves forward; false moves backward through the receipt's controls.</param>
    internal bool HandleKey(Key key, ModifierKeys mods, Action<bool> move, Action invoke, Action dismiss)
    {
        if (!_entered) return false;
        switch (key)
        {
            case Key.Tab when mods is ModifierKeys.None or ModifierKeys.Shift:
                move(mods == ModifierKeys.None);
                return true;
            case Key.Enter when mods == ModifierKeys.None:
                invoke();
                return true;
            case Key.Escape when mods == ModifierKeys.None:
                dismiss();
                return true;
            default:
                return false;
        }
    }
}
