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
    UploadGitHubImage,
    ScrollingCapture,
    DelayedCapture,
    RestoreClosedPin,
    CaptureText,
    PauseRecording,
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
        if (!hotkey.IsSafeGlobalShortcut)
        {
            errorCode = 87;
            errorMessage = MyCapture.Core.Localization.UiText.Get("Hotkey.ModifierRequired");
            return false;
        }
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
    private readonly IHotkeyOverride _override;
    private readonly ILogger<GlobalHotkeyService> _log;
    private readonly Dictionary<int, GlobalHotkeyCommand> _commandsById = [];
    private readonly Dictionary<GlobalHotkeyCommand, int> _idsByCommand = [];
    private readonly Dictionary<GlobalHotkeyCommand, Hotkey> _assignedByCommand = [];
    private readonly HashSet<GlobalHotkeyCommand> _overriddenCommands = [];
    private readonly List<HotkeyRegistrationFailure> _failures = [];
    private bool _forceCoreShortcuts;
    private bool _initialized;
    private bool _disposed;

    public GlobalHotkeyService(
        NativeMessageWindow window,
        ILogger<GlobalHotkeyService> log,
        IHotkeyOverride? hotkeyOverride = null)
        : this(window, new NativeHotkeyRegistrar(window.Handle), log, hotkeyOverride)
    {
    }

    /// <summary>
    /// Test/diagnostics constructor accepting an explicit registration seam. The window
    /// is still needed for the WM_HOTKEY message route.
    /// </summary>
    public GlobalHotkeyService(
        NativeMessageWindow window,
        IHotkeyRegistrar registrar,
        ILogger<GlobalHotkeyService> log,
        IHotkeyOverride? hotkeyOverride = null)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _registrar = registrar ?? throw new ArgumentNullException(nameof(registrar));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _override = hotkeyOverride ?? new LowLevelKeyboardHotkeyOverride(window);
        _window.MessageReceived += OnMessageReceived;
    }

    public event EventHandler<GlobalHotkeyPressedEventArgs>? Pressed;

    /// <summary>
    /// Every current shortcut that could not be registered or claimed during the last
    /// <see cref="Initialize"/> or <see cref="Reconfigure"/>. Error 1409 normally means
    /// another process already owns the chord.
    /// </summary>
    public IReadOnlyList<HotkeyRegistrationFailure> Failures => _failures;

    public IReadOnlyCollection<GlobalHotkeyCommand> RegisteredCommands => _idsByCommand.Keys;

    /// <summary>Commands claimed ahead of another program's registered shortcut.</summary>
    public IReadOnlyCollection<GlobalHotkeyCommand> OverriddenCommands => _overriddenCommands;

    public Hotkey? GetAssigned(GlobalHotkeyCommand command) => _assignedByCommand.GetValueOrDefault(command);

    public bool CanRegister(Hotkey hotkey)
    {
        if (!hotkey.IsSafeGlobalShortcut) return false;
        if (!hotkey.IsAssigned || _assignedByCommand.Values.Contains(hotkey)) return true;
        if (_forceCoreShortcuts && CoreShortcuts.IsCoreChord(hotkey)) return true;
        const int probeId = FirstHotkeyId + 127;
        if (!_registrar.TryRegister(probeId, hotkey, out _, out _)) return false;
        _registrar.Unregister(probeId);
        return true;
    }

    public void Initialize(HotkeySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_initialized)
        {
            throw new InvalidOperationException("Global hotkeys have already been initialized.");
        }

        _initialized = true;
        _forceCoreShortcuts = settings.ForceCoreShortcuts;

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
    /// With <paramref name="keepAvailable"/>, a collision does not roll back: every chord that
    /// registered or was claimed stays in effect and the rest are recorded in
    /// <see cref="Failures"/> exactly as <see cref="Initialize"/> records a startup conflict.
    /// </para>
    /// <para>
    /// Call on the dispatcher thread that owns the message window — the same thread that
    /// pumps WM_HOTKEY — so registration and message handling never race.
    /// </para>
    /// </remarks>
    public HotkeyReconfigureResult Reconfigure(HotkeySettings settings, bool keepAvailable = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_initialized)
        {
            throw new InvalidOperationException("Reconfigure requires the service to be initialized first.");
        }

        // Snapshot the current assignments so we can restore them on collision.
        var previous = new Dictionary<GlobalHotkeyCommand, Hotkey>(_assignedByCommand);
        foreach (HotkeyRegistrationFailure failure in _failures)
        {
            previous.TryAdd(failure.Command, failure.Hotkey);
        }
        bool previousForceCoreShortcuts = _forceCoreShortcuts;

        // Unregister everything currently held.
        ReleaseRegistrations();
        _failures.Clear();
        _forceCoreShortcuts = settings.ForceCoreShortcuts;

        var failures = new List<HotkeyRegistrationFailure>();
        foreach ((GlobalHotkeyCommand command, Hotkey hotkey) in Enumerate(settings))
        {
            if (!hotkey.IsAssigned)
            {
                continue;
            }

            if (!TryRegister(command, hotkey, out int error, out string message))
            {
                failures.Add(new HotkeyRegistrationFailure(command, hotkey, error, message));
            }
        }

        if (failures.Count == 0 || keepAvailable)
        {
            foreach (HotkeyRegistrationFailure failure in failures)
            {
                RecordFailure(failure);
            }

            _log.LogInformation(
                "Hotkeys reconfigured: {Commands}",
                string.Join(", ", _idsByCommand.Keys.Order()));
            return failures.Count == 0 ? HotkeyReconfigureResult.Success() : HotkeyReconfigureResult.Partial(failures);
        }

        // Roll back: drop the partial new set and restore the previous registrations.
        ReleaseRegistrations();
        _forceCoreShortcuts = previousForceCoreShortcuts;

        foreach ((GlobalHotkeyCommand command, Hotkey hotkey) in previous)
        {
            if (!TryRegister(command, hotkey, out int error, out string message))
            {
                _failures.Add(new HotkeyRegistrationFailure(command, hotkey, error, message));
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
        yield return (GlobalHotkeyCommand.UploadGitHubImage, settings.UploadGitHubImage);
        yield return (GlobalHotkeyCommand.ScrollingCapture, settings.ScrollingCapture);
        yield return (GlobalHotkeyCommand.DelayedCapture, settings.DelayedCapture);
        yield return (GlobalHotkeyCommand.RestoreClosedPin, settings.RestoreClosedPin);
        yield return (GlobalHotkeyCommand.CaptureText, settings.CaptureText);
        yield return (GlobalHotkeyCommand.PauseRecording, settings.PauseRecording);
    }

    private static int IdFor(GlobalHotkeyCommand command) => FirstHotkeyId + (int)command;

    private void Register(GlobalHotkeyCommand command, Hotkey hotkey)
    {
        if (!hotkey.IsAssigned)
        {
            return;
        }

        if (TryRegister(command, hotkey, out int error, out string message))
        {
            return;
        }

        RecordFailure(new HotkeyRegistrationFailure(command, hotkey, error, message));
    }

    private void RecordFailure(HotkeyRegistrationFailure failure)
    {
        _failures.Add(failure);

        _log.LogWarning(
            "Could not register {Command} as {Hotkey}: Win32 {Error} ({Message})",
            failure.Command,
            failure.Hotkey,
            failure.NativeErrorCode,
            failure.NativeMessage);
    }

    private bool TryRegister(GlobalHotkeyCommand command, Hotkey hotkey, out int error, out string message)
    {
        int id = IdFor(command);
        bool registered = _registrar.TryRegister(id, hotkey, out error, out message);
        if (!registered)
        {
            if (error != 1409 || !_forceCoreShortcuts || !CoreShortcuts.IsCoreChord(hotkey) ||
                !_override.TryClaim(id, hotkey))
            {
                return false;
            }

            _overriddenCommands.Add(command);
            _log.LogInformation("Claimed {Command} as {Hotkey} ahead of another program", command, hotkey);
        }
        else
        {
            _log.LogInformation("Registered {Command} as {Hotkey}", command, hotkey);
        }

        _commandsById[id] = command;
        _idsByCommand[command] = id;
        _assignedByCommand[command] = hotkey;
        return true;
    }

    private void ReleaseRegistrations()
    {
        foreach ((int id, GlobalHotkeyCommand command) in _commandsById)
        {
            _registrar.Unregister(id);
            if (_overriddenCommands.Contains(command))
            {
                _override.Release(id);
            }
        }

        _commandsById.Clear();
        _idsByCommand.Clear();
        _assignedByCommand.Clear();
        _overriddenCommands.Clear();
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

        ReleaseRegistrations();
        _override.Dispose();
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
/// previous hotkey set remains registered and <see cref="Failures"/> explains why. A partial
/// reconfigure is applied, and <see cref="Failures"/> lists the chords it left unregistered.
/// </summary>
public sealed record HotkeyReconfigureResult(bool Applied, IReadOnlyList<HotkeyRegistrationFailure> Failures)
{
    public static HotkeyReconfigureResult Success() => new(true, []);

    public static HotkeyReconfigureResult RolledBack(IReadOnlyList<HotkeyRegistrationFailure> failures) =>
        new(false, failures);

    public static HotkeyReconfigureResult Partial(IReadOnlyList<HotkeyRegistrationFailure> failures) =>
        new(true, failures);
}
