using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using MyCapture.Core.Settings;
using MyCapture.Platform.Interop;

namespace MyCapture.Platform.Shell;

/// <summary>Semantic commands produced by global keyboard shortcuts.</summary>
public enum GlobalHotkeyCommand
{
    CaptureRegion,
    OpenLibrary,
    PasteToScreen,
    HideAllPins,
    ToggleClickThrough,
    RepeatLastRegion,
    CaptureWindow,
    CaptureFullScreen,
    RecordRegion,
}

/// <summary>
/// The native side of hotkey registration, isolated behind an interface so the
/// transactional reconfigure logic can be tested without calling <c>RegisterHotKey</c>.
/// </summary>
/// <remarks>
/// A registration seam is the only part of the service that cannot run in a unit test:
/// <c>RegisterHotKey</c> needs a real message window and can collide with whatever the
/// developer's machine already owns. Everything that matters — ordering, rollback, and
/// failure reporting — lives above this seam and is fully testable through a fake.
/// </remarks>
public interface IHotkeyRegistrar
{
    /// <summary>
    /// Attempts to claim <paramref name="hotkey"/> under <paramref name="id"/>. Returns
    /// success; on failure <paramref name="errorCode"/> carries the Win32 error (1409
    /// normally means another process owns the chord).
    /// </summary>
    bool TryRegister(int id, Hotkey hotkey, out int errorCode, out string errorMessage);

    /// <summary>Releases a previously registered id. Best-effort; never throws.</summary>
    void Unregister(int id);
}

/// <summary>
/// The production registrar backed by the message-only window and <c>RegisterHotKey</c>.
/// </summary>
public sealed class NativeHotkeyRegistrar : IHotkeyRegistrar
{
    private readonly IntPtr _windowHandle;

    public NativeHotkeyRegistrar(IntPtr windowHandle) => _windowHandle = windowHandle;

    public bool TryRegister(int id, Hotkey hotkey, out int errorCode, out string errorMessage)
    {
        // MOD_NOREPEAT stops an auto-repeating keydown from firing the command in a tight
        // loop while the chord is held.
        uint modifiers = (uint)hotkey.Modifiers | NativeMethods.MOD_NOREPEAT;
        if (NativeMethods.RegisterHotKey(_windowHandle, id, modifiers, hotkey.VirtualKey))
        {
            errorCode = 0;
            errorMessage = string.Empty;
            return true;
        }

        errorCode = Marshal.GetLastWin32Error();
        errorMessage = new Win32Exception(errorCode).Message;
        return false;
    }

    public void Unregister(int id) => _ = NativeMethods.UnregisterHotKey(_windowHandle, id);
}

/// <summary>
/// Registers application hotkeys against the message-only shell window and maps raw
/// WM_HOTKEY IDs back to semantic commands.
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private const int FirstHotkeyId = 0x4200;

    private readonly NativeMessageWindow _window;
    private readonly IHotkeyRegistrar _registrar;
    private readonly ILogger<GlobalHotkeyService> _log;
    private readonly Dictionary<int, GlobalHotkeyCommand> _commandsById = [];
    private readonly Dictionary<GlobalHotkeyCommand, int> _idsByCommand = [];
    private readonly Dictionary<GlobalHotkeyCommand, Hotkey> _assignedByCommand = [];
    private readonly List<HotkeyRegistrationFailure> _failures = [];
    private bool _initialized;
    private bool _disposed;

    public GlobalHotkeyService(
        NativeMessageWindow window,
        ILogger<GlobalHotkeyService> log)
        : this(window, new NativeHotkeyRegistrar(window.Handle), log)
    {
    }

    /// <summary>
    /// Test/diagnostics constructor accepting an explicit registration seam. The window
    /// is still needed for the WM_HOTKEY message route.
    /// </summary>
    public GlobalHotkeyService(
        NativeMessageWindow window,
        IHotkeyRegistrar registrar,
        ILogger<GlobalHotkeyService> log)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _registrar = registrar ?? throw new ArgumentNullException(nameof(registrar));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _window.MessageReceived += OnMessageReceived;
    }

    public event EventHandler<GlobalHotkeyPressedEventArgs>? Pressed;

    /// <summary>
    /// Every assigned shortcut that Windows rejected during the last
    /// <see cref="Initialize"/> or <see cref="Reconfigure"/>. Error 1409 normally means
    /// another process already owns the chord.
    /// </summary>
    public IReadOnlyList<HotkeyRegistrationFailure> Failures => _failures;

    public IReadOnlyCollection<GlobalHotkeyCommand> RegisteredCommands => _idsByCommand.Keys;

    public void Initialize(HotkeySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_initialized)
        {
            throw new InvalidOperationException("Global hotkeys have already been initialized.");
        }

        _initialized = true;

        foreach ((GlobalHotkeyCommand command, Hotkey hotkey) in Enumerate(settings))
        {
            Register(command, hotkey);
        }
    }

    /// <summary>
    /// Atomically switches the entire hotkey set to <paramref name="settings"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The old set is remembered, every current chord is unregistered, and each new
    /// chord is registered with <c>MOD_NOREPEAT</c>. If <em>any</em> new chord collides,
    /// every partially applied new registration is rolled back and the previous set is
    /// restored, so the app is never left without its capture hotkey. The return value
    /// reports the collisions that forced the rollback.
    /// </para>
    /// <para>
    /// Call on the dispatcher thread that owns the message window — the same thread that
    /// pumps WM_HOTKEY — so registration and message handling never race.
    /// </para>
    /// </remarks>
    public HotkeyReconfigureResult Reconfigure(HotkeySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_initialized)
        {
            throw new InvalidOperationException("Reconfigure requires the service to be initialized first.");
        }

        // Snapshot the current assignments so we can restore them on collision.
        var previous = new Dictionary<GlobalHotkeyCommand, Hotkey>(_assignedByCommand);

        // Unregister everything currently held.
        foreach (int id in _commandsById.Keys)
        {
            _registrar.Unregister(id);
        }

        _commandsById.Clear();
        _idsByCommand.Clear();
        _assignedByCommand.Clear();
        _failures.Clear();

        var failures = new List<HotkeyRegistrationFailure>();
        foreach ((GlobalHotkeyCommand command, Hotkey hotkey) in Enumerate(settings))
        {
            if (!hotkey.IsAssigned)
            {
                continue;
            }

            int id = IdFor(command);
            if (_registrar.TryRegister(id, hotkey, out int error, out string message))
            {
                _commandsById[id] = command;
                _idsByCommand[command] = id;
                _assignedByCommand[command] = hotkey;
            }
            else
            {
                failures.Add(new HotkeyRegistrationFailure(command, hotkey, error, message));
            }
        }

        if (failures.Count == 0)
        {
            _log.LogInformation(
                "Hotkeys reconfigured: {Commands}",
                string.Join(", ", _idsByCommand.Keys.Order()));
            return HotkeyReconfigureResult.Success();
        }

        // Roll back: drop the partial new set and restore the previous registrations.
        foreach (int id in _commandsById.Keys)
        {
            _registrar.Unregister(id);
        }

        _commandsById.Clear();
        _idsByCommand.Clear();
        _assignedByCommand.Clear();

        foreach ((GlobalHotkeyCommand command, Hotkey hotkey) in previous)
        {
            int id = IdFor(command);
            if (_registrar.TryRegister(id, hotkey, out _, out _))
            {
                _commandsById[id] = command;
                _idsByCommand[command] = id;
                _assignedByCommand[command] = hotkey;
            }
            else
            {
                _log.LogError(
                    "Could not restore previous hotkey {Command} ({Hotkey}) after a failed reconfigure",
                    command,
                    hotkey);
            }
        }

        _log.LogWarning(
            "Hotkey reconfigure rolled back: {Count} collision(s); previous set restored",
            failures.Count);

        return HotkeyReconfigureResult.RolledBack(failures);
    }

    /// <summary>
    /// Exercises the same WM_HOTKEY message route used by Windows without synthesizing
    /// keystrokes into the user's desktop. Intended for the packaged shell self-test.
    /// </summary>
    public bool PostDiagnosticCommand(GlobalHotkeyCommand command)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return _idsByCommand.TryGetValue(command, out int id) &&
               _window.Post(NativeMethods.WM_HOTKEY, new IntPtr(id), IntPtr.Zero);
    }

    private static IEnumerable<(GlobalHotkeyCommand Command, Hotkey Hotkey)> Enumerate(HotkeySettings settings)
    {
        yield return (GlobalHotkeyCommand.CaptureRegion, settings.Capture);
        yield return (GlobalHotkeyCommand.OpenLibrary, settings.OpenLibrary);
        yield return (GlobalHotkeyCommand.PasteToScreen, settings.PasteToScreen);
        yield return (GlobalHotkeyCommand.HideAllPins, settings.HideAllPins);
        yield return (GlobalHotkeyCommand.ToggleClickThrough, settings.ToggleClickThrough);
        yield return (GlobalHotkeyCommand.RepeatLastRegion, settings.RepeatLastRegion);
        yield return (GlobalHotkeyCommand.CaptureWindow, settings.CaptureWindow);
        yield return (GlobalHotkeyCommand.CaptureFullScreen, settings.CaptureFullScreen);
        yield return (GlobalHotkeyCommand.RecordRegion, settings.RecordRegion);
    }

    private static int IdFor(GlobalHotkeyCommand command) => FirstHotkeyId + (int)command;

    private void Register(GlobalHotkeyCommand command, Hotkey hotkey)
    {
        if (!hotkey.IsAssigned)
        {
            return;
        }

        int id = IdFor(command);
        if (_registrar.TryRegister(id, hotkey, out int error, out string message))
        {
            _commandsById[id] = command;
            _idsByCommand[command] = id;
            _assignedByCommand[command] = hotkey;
            _log.LogInformation("Registered {Command} as {Hotkey}", command, hotkey);
            return;
        }

        var failure = new HotkeyRegistrationFailure(command, hotkey, error, message);
        _failures.Add(failure);

        _log.LogWarning(
            "Could not register {Command} as {Hotkey}: Win32 {Error} ({Message})",
            command,
            hotkey,
            error,
            message);
    }

    private void OnMessageReceived(object? sender, NativeWindowMessageEventArgs e)
    {
        if (e.Message != NativeMethods.WM_HOTKEY)
        {
            return;
        }

        int id = unchecked((int)e.WParam.ToInt64());
        if (!_commandsById.TryGetValue(id, out GlobalHotkeyCommand command))
        {
            return;
        }

        e.Handled = true;
        Pressed?.Invoke(this, new GlobalHotkeyPressedEventArgs(command));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _window.MessageReceived -= OnMessageReceived;

        foreach (int id in _commandsById.Keys)
        {
            _registrar.Unregister(id);
        }

        _commandsById.Clear();
        _idsByCommand.Clear();
        _assignedByCommand.Clear();
    }
}

public sealed class GlobalHotkeyPressedEventArgs : EventArgs
{
    public GlobalHotkeyPressedEventArgs(GlobalHotkeyCommand command)
    {
        Command = command;
    }

    public GlobalHotkeyCommand Command { get; }
}

public sealed record HotkeyRegistrationFailure(
    GlobalHotkeyCommand Command,
    Hotkey Hotkey,
    int NativeErrorCode,
    string NativeMessage);

/// <summary>
/// The outcome of a <see cref="GlobalHotkeyService.Reconfigure"/>. On rollback the
/// previous hotkey set remains registered and <see cref="Failures"/> explains why.
/// </summary>
public sealed record HotkeyReconfigureResult(bool Applied, IReadOnlyList<HotkeyRegistrationFailure> Failures)
{
    public static HotkeyReconfigureResult Success() => new(true, []);

    public static HotkeyReconfigureResult RolledBack(IReadOnlyList<HotkeyRegistrationFailure> failures) =>
        new(false, failures);
}
