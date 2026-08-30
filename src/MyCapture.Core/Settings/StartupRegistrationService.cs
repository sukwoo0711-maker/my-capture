namespace MyCapture.Core.Settings;

/// <summary>
/// The narrow slice of the registry the startup feature needs, abstracted so the
/// service can be exercised deterministically without touching the real registry.
/// </summary>
/// <remarks>
/// <para>
/// A value implementation only ever reads and writes a single string value under one
/// key. The production implementation is scoped to
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> and never HKLM, so the
/// feature can neither require elevation nor affect other users.
/// </para>
/// </remarks>
public interface IRunKeyStore
{
    /// <summary>Reads the named value, or <see langword="null"/> when it is absent.</summary>
    string? GetValue(string name);

    /// <summary>Creates or overwrites the named value with <paramref name="value"/>.</summary>
    void SetValue(string name, string value);

    /// <summary>Removes the named value. A no-op when the value is absent.</summary>
    void DeleteValue(string name);
}

/// <summary>
/// The outcome of a launch-at-login change, carrying enough detail to avoid recording
/// a setting that does not match reality.
/// </summary>
public sealed record StartupApplyResult(bool Succeeded, bool DesiredEnabled, string? Error)
{
    public static StartupApplyResult Ok(bool desiredEnabled) => new(true, desiredEnabled, null);

    public static StartupApplyResult Fail(bool desiredEnabled, string error) =>
        new(false, desiredEnabled, error);
}

/// <summary>
/// Manages the "launch at login" registration through the per-user Run key.
/// </summary>
/// <remarks>
/// <para>
/// The value stored is the executable path, quoted exactly once, so a path containing
/// spaces survives the shell's argument parsing. <see cref="IsEnabled"/> is not a naive
/// "does the value exist" check: it confirms the stored command points at the same
/// normalised executable this build runs from, so a stale entry left by a previous
/// install location is treated as "not enabled by this build" and reconciled.
/// </para>
/// <para>
/// The service never lies. If writing the Run key fails, the caller is told the change
/// did not take effect and must not persist <c>LaunchAtLogin = true</c> — otherwise the
/// settings file would claim a state the system does not have.
/// </para>
/// </remarks>
public sealed class StartupRegistrationService
{
    /// <summary>The Run value name. Stable across versions so an upgrade updates in place.</summary>
    public const string RunValueName = "MyCapture";

    private readonly IRunKeyStore _store;
    private readonly string _executablePath;

    public StartupRegistrationService(IRunKeyStore store, string executablePath)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        _executablePath = executablePath;
    }

    /// <summary>The exact command written to the Run key when enabled: the quoted path.</summary>
    public string ExpectedCommand => Quote(_executablePath);

    /// <summary>
    /// True only when the Run value exists and its command resolves to the same
    /// executable this process runs from.
    /// </summary>
    public bool IsEnabled()
    {
        string? current = _store.GetValue(RunValueName);
        if (string.IsNullOrWhiteSpace(current))
        {
            return false;
        }

        string storedPath = Unquote(current);
        return PathsEqual(storedPath, _executablePath);
    }

    /// <summary>Writes the Run value with the quoted executable path.</summary>
    public void Enable() => _store.SetValue(RunValueName, ExpectedCommand);

    /// <summary>Removes the Run value.</summary>
    public void Disable() => _store.DeleteValue(RunValueName);

    /// <summary>
    /// Brings the registration in line with <paramref name="desiredEnabled"/>
    /// transactionally: on any failure nothing is half-applied and the caller learns the
    /// real state so it does not persist a false setting.
    /// </summary>
    public StartupApplyResult Apply(bool desiredEnabled)
    {
        try
        {
            if (desiredEnabled)
            {
                Enable();
                // Verify the write actually took: a store that silently failed would
                // otherwise let the setting claim an enabled state that is not real.
                return IsEnabled()
                    ? StartupApplyResult.Ok(true)
                    : StartupApplyResult.Fail(true, "시작 프로그램 등록을 확인하지 못했습니다.");
            }

            Disable();
            return StartupApplyResult.Ok(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return StartupApplyResult.Fail(desiredEnabled, ex.Message);
        }
    }

    /// <summary>
    /// Best-effort startup reconciliation: when the setting says enabled but the stored
    /// command points at a different (moved) executable, rewrite it to this build's path.
    /// Returns whether a rewrite happened. Never throws.
    /// </summary>
    public bool ReconcileOnStartup(bool settingEnabled)
    {
        try
        {
            string? current = _store.GetValue(RunValueName);

            if (!settingEnabled)
            {
                // The user turned it off but a stale value survives (e.g. hand-edited
                // settings): remove it so the two agree.
                if (!string.IsNullOrWhiteSpace(current))
                {
                    _store.DeleteValue(RunValueName);
                    return true;
                }

                return false;
            }

            if (string.IsNullOrWhiteSpace(current) || !PathsEqual(Unquote(current), _executablePath))
            {
                Enable();
                return true;
            }

            return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return false;
        }
    }

    private static string Quote(string path) => "\"" + path.Trim().Trim('"') + "\"";

    private static string Unquote(string command)
    {
        string trimmed = command.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"')
        {
            int closing = trimmed.IndexOf('"', 1);
            if (closing > 0)
            {
                return trimmed[1..closing];
            }
        }

        // Unquoted legacy value: take up to the first space, matching how the shell
        // would parse the command line.
        int space = trimmed.IndexOf(' ');
        return space > 0 ? trimmed[..space] : trimmed;
    }

    private static bool PathsEqual(string a, string b)
    {
        static string Normalize(string p)
        {
            try
            {
                return Path.GetFullPath(p.Trim().Trim('"'))
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch (ArgumentException) { return p.Trim(); }
            catch (NotSupportedException) { return p.Trim(); }
            catch (PathTooLongException) { return p.Trim(); }
        }

        return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
    }
}
