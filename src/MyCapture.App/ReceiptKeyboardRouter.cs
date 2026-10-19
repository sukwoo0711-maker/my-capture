using MyCapture.Core.Settings;
using MyCapture.Platform.Shell;

namespace MyCapture.App;

/// <summary>Shares one Alt+Q registration between the currently available receipt surfaces.</summary>
/// <remarks>All calls run on the UI thread. The newest target wins when priorities match.</remarks>
internal sealed class ReceiptKeyboardRouter : IDisposable
{
    internal const int ActivationKeyId = 0x7103;
    private const int WmHotkey = 0x0312;
    private static readonly Hotkey ActivationKey = new(HotkeyModifiers.Alt, 0x51);
    private readonly Func<nint, IHotkeyRegistrar> _factory;
    private readonly Dictionary<object, Target> _targets = new(ReferenceEqualityComparer.Instance);
    private Target? _current;
    private IHotkeyRegistrar? _registrar;
    private bool _registered;
    private bool _disposed;
    private long _order;

    internal ReceiptKeyboardRouter(Func<nint, IHotkeyRegistrar>? factory = null)
    {
        _factory = factory ?? (hwnd => new NativeHotkeyRegistrar(hwnd));
    }

    /// <summary>Raised after the selected registration changes, so surfaces can refresh their hints.</summary>
    internal event Action? RegistrationChanged;

    internal void SetTarget(object owner, nint hwnd, bool available, int priority, Func<bool> activate)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(activate);
        if (_disposed) return;
        if (available) _targets[owner] = new Target(owner, hwnd, priority, ++_order, activate);
        else _targets.Remove(owner);
        SelectTarget();
    }

    internal void Remove(object owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (_disposed || !_targets.Remove(owner)) return;
        SelectTarget();
    }

    /// <summary>Only the registered target's native window may claim this hotkey message.</summary>
    internal bool MatchesMessage(nint hwnd, int message, nint wParam) =>
        !_disposed && _registered && _current?.Hwnd == hwnd
        && message == WmHotkey && wParam == ActivationKeyId;

    /// <summary>The in-app keyboard path remains available when Windows cannot register Alt+Q.</summary>
    internal bool Activate() => !_disposed && _current is { } target && target.Activate();

    internal bool IsRegisteredFor(object owner) =>
        !_disposed && _registered && ReferenceEquals(_current?.Owner, owner);

    private void SelectTarget()
    {
        Target? next = _targets.Values.OrderByDescending(target => target.Priority)
            .ThenByDescending(target => target.Order).FirstOrDefault();
        if (ReferenceEquals(next?.Owner, _current?.Owner) && next?.Hwnd == _current?.Hwnd)
        {
            // A refresh can replace the activation delegate without touching the native lease.
            _current = next;
            return;
        }

        Unregister();
        _current = next;
        if (next is { Hwnd: not 0 })
        {
            _registrar = _factory(next.Hwnd);
            _registered = _registrar.TryRegister(ActivationKeyId, ActivationKey, out _, out _);
        }
        RegistrationChanged?.Invoke();
    }

    private void Unregister()
    {
        if (_registered) _registrar?.Unregister(ActivationKeyId);
        _registered = false;
        _registrar = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Unregister();
        _current = null;
        _targets.Clear();
        RegistrationChanged?.Invoke();
    }

    private sealed record Target(object Owner, nint Hwnd, int Priority, long Order, Func<bool> Activate);
}
