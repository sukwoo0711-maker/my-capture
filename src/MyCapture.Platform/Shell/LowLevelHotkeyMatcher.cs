using MyCapture.Core.Settings;
using MyCapture.Platform.Interop;

namespace MyCapture.Platform.Shell;

internal readonly record struct LowLevelHotkeyClaim(int Id, Hotkey Hotkey);

internal readonly record struct LowLevelHotkeyMatch(bool Swallow, int? CommandId, bool ReleaseKey);

/// <summary>Decides which keyboard messages belong to a claimed chord without calling Windows.</summary>
internal static class LowLevelHotkeyMatcher
{
    internal static LowLevelHotkeyMatch Match(
        uint virtualKey,
        uint message,
        HotkeyModifiers modifiers,
        ReadOnlySpan<LowLevelHotkeyClaim> claims,
        IReadOnlySet<uint> heldKeys,
        IReadOnlySet<uint> swallowedKeys)
    {
        if (message is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP)
        {
            return swallowedKeys.Contains(virtualKey)
                ? new LowLevelHotkeyMatch(true, null, true)
                : default;
        }

        if (message is not (NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN))
        {
            return default;
        }

        // Once swallowed, repeats and the eventual key-up belong to us even if
        // modifiers or the claim snapshot have changed in the meantime.
        if (swallowedKeys.Contains(virtualKey))
        {
            return new LowLevelHotkeyMatch(true, null, false);
        }

        foreach (LowLevelHotkeyClaim claim in claims)
        {
            if (claim.Hotkey.VirtualKey == virtualKey && claim.Hotkey.Modifiers == modifiers)
            {
                return new LowLevelHotkeyMatch(true, heldKeys.Contains(virtualKey) ? null : claim.Id, false);
            }
        }

        return default;
    }
}
