using Microsoft.Win32;
using MyCapture.Core.Settings;

namespace MyCapture.Platform.Shell;

/// <summary>
/// The production <see cref="IRunKeyStore"/> backed by the per-user Run key.
/// </summary>
/// <remarks>
/// <para>
/// Scoped to <c>HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run</c>.
/// It never opens HKLM: launch-at-login is a per-user preference and touching the
/// machine hive would require elevation and affect every account.
/// </para>
/// <para>
/// The registry access is confined to this thin adapter so the
/// <see cref="StartupRegistrationService"/> logic stays fully testable against an
/// in-memory fake.
/// </para>
/// </remarks>
public sealed class RegistryRunKeyStore : IRunKeyStore
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public string? GetValue(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(name) as string;
    }

    public void SetValue(string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);

        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        key.SetValue(name, value, RegistryValueKind.String);
    }

    public void DeleteValue(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        // DeleteValue with throwOnMissingValue:false makes removal idempotent.
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}
