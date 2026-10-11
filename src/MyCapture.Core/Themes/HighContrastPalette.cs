namespace MyCapture.Core.Themes;

/// <summary>The Windows system colours a contrast theme defines (GetSysColor roles).</summary>
public enum SystemColorRole
{
    Window,
    WindowText,
    ButtonFace,
    ButtonText,
    Highlight,
    HighlightText,
    GrayText,
    HotTrack,
}

/// <summary>
/// Which system colour each semantic token takes while a Windows contrast theme is on. Every
/// palette key resolves here, so the whole application follows the user's contrast palette
/// instead of mixing it with the app's own colours.
/// </summary>
/// <remarks>
/// Text sits on surfaces, so surfaces resolve to Window and text to WindowText; the pair is the
/// one a contrast theme guarantees. Accent fills take Highlight with HighlightText ink, the
/// pairing Windows uses for selection. Washes that carry ordinary text (selected rows, hover,
/// pressed) stay on Window, and the state shows through a Highlight outline instead.
/// </remarks>
public static class HighContrastPalette
{
    private static readonly Dictionary<string, SystemColorRole> RoleMap = new(StringComparer.Ordinal)
    {
        ["Surface.Canvas"] = SystemColorRole.Window,
        ["Surface.Base"] = SystemColorRole.Window,
        ["Surface.Raised"] = SystemColorRole.Window,
        ["Surface.Overlay"] = SystemColorRole.ButtonFace,
        ["Surface.Floating"] = SystemColorRole.Window,
        ["Surface.Sunken"] = SystemColorRole.Window,
        ["Surface.Hover"] = SystemColorRole.Window,
        ["Surface.Pressed"] = SystemColorRole.Window,
        ["Surface.Scrim"] = SystemColorRole.Window,
        ["Surface.Badge"] = SystemColorRole.Window,
        ["Surface.BadgeStrong"] = SystemColorRole.Window,
        ["Surface.Folder"] = SystemColorRole.Window,
        ["Text.Primary"] = SystemColorRole.WindowText,
        ["Text.Secondary"] = SystemColorRole.WindowText,
        ["Text.Muted"] = SystemColorRole.WindowText,
        ["Text.Disabled"] = SystemColorRole.GrayText,
        ["Text.OnAccent"] = SystemColorRole.HighlightText,
        ["Text.Badge"] = SystemColorRole.WindowText,
        ["Accent.Default"] = SystemColorRole.Highlight,
        ["Accent.Hover"] = SystemColorRole.Highlight,
        ["Accent.Pressed"] = SystemColorRole.Highlight,
        ["Accent.Subtle"] = SystemColorRole.Window,
        ["Accent.Cool"] = SystemColorRole.Highlight,
        ["Accent.Gradient"] = SystemColorRole.Highlight,
        ["Accent.Folder"] = SystemColorRole.WindowText,
        ["Border.Subtle"] = SystemColorRole.WindowText,
        ["Border.Control"] = SystemColorRole.ButtonText,
        // Strong is the hover and emphasis outline, so it takes the system emphasis colour.
        ["Border.Strong"] = SystemColorRole.Highlight,
        ["Border.Focus"] = SystemColorRole.Highlight,
        ["Border.Accent"] = SystemColorRole.Highlight,
        ["Border.Hover"] = SystemColorRole.Highlight,
        ["Border.Badge"] = SystemColorRole.WindowText,
        ["Border.Folder"] = SystemColorRole.WindowText,
        // Contrast themes have no status hues; the wording and glyphs carry the meaning.
        ["State.Warning"] = SystemColorRole.WindowText,
        ["State.Success"] = SystemColorRole.WindowText,
        ["State.Danger"] = SystemColorRole.WindowText,
        ["State.DangerHover"] = SystemColorRole.WindowText,
        ["Timeline.Background"] = SystemColorRole.Window,
        ["Timeline.Track"] = SystemColorRole.Window,
        ["Timeline.TextLayer"] = SystemColorRole.Highlight,
        ["Timeline.FrameLayer"] = SystemColorRole.HotTrack,
        ["Timeline.Grid"] = SystemColorRole.GrayText,
        ["Timeline.Playhead"] = SystemColorRole.Highlight,
        ["Timeline.TrimDeleteFill"] = SystemColorRole.Window,
        ["Timeline.TrimDeleteHandle"] = SystemColorRole.WindowText,
        ["Timeline.TrimDeleteHatch"] = SystemColorRole.WindowText,
        // The capture dimmer keeps its brush opacity so the screen stays visible beneath it.
        ["Overlay.Dimmer"] = SystemColorRole.Window,
        ["Overlay.SelectionBorder"] = SystemColorRole.Highlight,
        ["Overlay.HandleFill"] = SystemColorRole.Window,
        ["Overlay.HandleStroke"] = SystemColorRole.WindowText,
    };

    /// <summary>Every token with a contrast-theme role, palette keys and the app-wide extras.</summary>
    public static IReadOnlyDictionary<string, SystemColorRole> Roles => RoleMap;

    /// <summary>The role of <paramref name="key"/>; unknown keys fall back by their family prefix.</summary>
    public static SystemColorRole RoleFor(string key)
    {
        if (RoleMap.TryGetValue(key, out SystemColorRole role)) return role;
        if (key.StartsWith("Surface.", StringComparison.Ordinal)) return SystemColorRole.Window;
        if (key.StartsWith("Accent.", StringComparison.Ordinal)) return SystemColorRole.Highlight;
        return SystemColorRole.WindowText;
    }
}
