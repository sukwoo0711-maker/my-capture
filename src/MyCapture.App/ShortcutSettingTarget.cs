using MyCapture.Core.Settings;
using MyCapture.Platform.Shell;

namespace MyCapture.App;

/// <summary>
/// The settings row that assigns each global shortcut, named by the <see cref="SettingsDraft"/>
/// property its recorder edits. Tutorials and the shortcut-conflict notification use it to open
/// Settings → Shortcuts on the right row.
/// </summary>
internal static class ShortcutSettingTarget
{
    /// <summary>The row for a tutorial's global shortcut, or null when the feature has none.</summary>
    internal static string? ForFeature(string featureId) => featureId switch
    {
        "capture-region" => nameof(SettingsDraft.CaptureHotkey),
        "record-region" => nameof(SettingsDraft.RecordRegionHotkey),
        "record-pause" => nameof(SettingsDraft.PauseRecordingHotkey),
        "library-open" => nameof(SettingsDraft.OpenLibraryHotkey),
        "capture-window" => nameof(SettingsDraft.CaptureWindowHotkey),
        "capture-fullscreen" => nameof(SettingsDraft.CaptureFullScreenHotkey),
        "capture-scroll" => nameof(SettingsDraft.ScrollingCaptureHotkey),
        "capture-delay" => nameof(SettingsDraft.DelayedCaptureHotkey),
        "text-copy" => nameof(SettingsDraft.CaptureTextHotkey),
        "pin-clipboard" or "practice-image-pin" => nameof(SettingsDraft.PasteToScreenHotkey),
        "pin-hide-all" => nameof(SettingsDraft.HideAllPinsHotkey),
        "pin-click-through" => nameof(SettingsDraft.ToggleClickThroughHotkey),
        "pin-restore" => nameof(SettingsDraft.RestoreClosedPinHotkey),
        _ => null,
    };

    /// <summary>The row for a registered global shortcut command.</summary>
    internal static string ForCommand(GlobalHotkeyCommand command) => command switch
    {
        GlobalHotkeyCommand.CaptureRegion => nameof(SettingsDraft.CaptureHotkey),
        GlobalHotkeyCommand.OpenLibrary => nameof(SettingsDraft.OpenLibraryHotkey),
        GlobalHotkeyCommand.PasteToScreen => nameof(SettingsDraft.PasteToScreenHotkey),
        GlobalHotkeyCommand.HideAllPins => nameof(SettingsDraft.HideAllPinsHotkey),
        GlobalHotkeyCommand.ToggleClickThrough => nameof(SettingsDraft.ToggleClickThroughHotkey),
        GlobalHotkeyCommand.RepeatLastRegion => nameof(SettingsDraft.RepeatLastRegionHotkey),
        GlobalHotkeyCommand.CaptureWindow => nameof(SettingsDraft.CaptureWindowHotkey),
        GlobalHotkeyCommand.CaptureFullScreen => nameof(SettingsDraft.CaptureFullScreenHotkey),
        GlobalHotkeyCommand.RecordRegion => nameof(SettingsDraft.RecordRegionHotkey),
        GlobalHotkeyCommand.UploadGitHubImage => nameof(SettingsDraft.UploadGitHubImageHotkey),
        GlobalHotkeyCommand.ScrollingCapture => nameof(SettingsDraft.ScrollingCaptureHotkey),
        GlobalHotkeyCommand.DelayedCapture => nameof(SettingsDraft.DelayedCaptureHotkey),
        GlobalHotkeyCommand.RestoreClosedPin => nameof(SettingsDraft.RestoreClosedPinHotkey),
        GlobalHotkeyCommand.CaptureText => nameof(SettingsDraft.CaptureTextHotkey),
        GlobalHotkeyCommand.PauseRecording => nameof(SettingsDraft.PauseRecordingHotkey),
        _ => nameof(SettingsDraft.CaptureHotkey),
    };
}
