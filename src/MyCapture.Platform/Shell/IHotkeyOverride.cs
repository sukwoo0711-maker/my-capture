using MyCapture.Core.Settings;

namespace MyCapture.Platform.Shell;

/// <summary>
/// Claims hotkeys ahead of other programs and delivers them through the shell's WM_HOTKEY route.
/// </summary>
public interface IHotkeyOverride : IDisposable
{
    /// <summary>Attempts to claim a chord for the supplied native hotkey identifier.</summary>
    bool TryClaim(int id, Hotkey hotkey);

    /// <summary>Releases a previously claimed identifier.</summary>
    void Release(int id);
}
