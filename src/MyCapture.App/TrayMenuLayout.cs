using MyCapture.Core.Settings;

namespace MyCapture.App;

/// <summary>The commands the tray menu can run, wired by the application shell.</summary>
internal sealed record TrayMenuCommands(
    Action Capture,
    Action Record,
    Action CaptureWindow,
    Action CaptureFullScreen,
    Action ScrollingCapture,
    Action DelayedCapture,
    Action RepeatLastRegion,
    Action CopyText,
    Action RestoreClosedPin,
    Action Library,
    Action Settings,
    Action Shortcuts,
    Action Exit,
    Action? PinClipboard = null,
    Action? HideOrShowAllPins = null,
    Action? ClearClickThrough = null,
    Action? ContactSupport = null);

/// <summary>What the tray menu reflects at the moment it opens. Read cheaply on every open.</summary>
/// <param name="CanRestoreClosedPin">A closed pin can come back.</param>
/// <param name="ScrollingActive">A scrolling capture is running, so its entry cancels it.</param>
/// <param name="ClipboardHasContent">The clipboard holds an image or text a pin can show.</param>
/// <param name="PinCount">Open pins, shown or hidden.</param>
/// <param name="PinsHidden">The pins are hidden by "Hide all pins".</param>
/// <param name="ClickThroughPinCount">Open pins that let clicks through to the windows behind.</param>
internal sealed record TrayMenuState(
    bool CanRestoreClosedPin = false,
    bool ScrollingActive = false,
    bool ClipboardHasContent = true,
    int PinCount = 0,
    bool PinsHidden = false,
    int ClickThroughPinCount = 0);

/// <summary>
/// The tray menu's order, kept apart from the shell so it can be checked without a tray icon.
/// The first group holds what people come for: region capture, screen recording, text capture,
/// the rarer capture types in one branch, and the pin commands (pin the clipboard, hide or show
/// every pin, turn click-through off on every pin, reopen a closed pin). The destinations follow,
/// and Exit is always last. The display language lives in Settings only.
/// </summary>
internal static class TrayMenuLayout
{
    /// <summary>F1 opens the shortcut window from any MyCapture window; the tray shows the same key.</summary>
    internal const string ShortcutsGesture = "F1";

    internal static IReadOnlyList<ThemedShellPresenter.MenuEntry> Build(
        TrayMenuCommands commands, HotkeySettings keys, bool canRestoreClosedPin, bool scrollingActive) =>
        Build(commands, keys, new TrayMenuState(canRestoreClosedPin, scrollingActive));

    internal static IReadOnlyList<ThemedShellPresenter.MenuEntry> Build(
        TrayMenuCommands commands, HotkeySettings keys, TrayMenuState state)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(keys);
        var entries = new List<ThemedShellPresenter.MenuEntry>
        {
            new(UiText.Get("Text_2C20ADDA2018"), commands.Capture, Gesture: Gesture(keys.Capture)),
            new(UiText.Get("Text_4F5AF20FAA6E"), commands.Record, Gesture: Gesture(keys.RecordRegion)),
            new(UiText.Get("Onboard_TrayCopyText"), commands.CopyText, Gesture: Gesture(keys.CaptureText)),
            new(UiText.Get("Onboard_TrayMoreCaptures"), null,
            [
                new(UiText.Get("Text_EA1FBABE64F3"), commands.CaptureWindow, Gesture: Gesture(keys.CaptureWindow)),
                new(UiText.Get("Text_65BB6610ACA0"), commands.CaptureFullScreen, Gesture: Gesture(keys.CaptureFullScreen)),
                new(UiText.Get(state.ScrollingActive ? "Text_E8B876F98CD8" : "Text_4DBA30197C0A"), commands.ScrollingCapture,
                    Gesture: Gesture(keys.ScrollingCapture)),
                new(UiText.Get("Text_38FF9B0AA541"), commands.DelayedCapture, Gesture: Gesture(keys.DelayedCapture)),
                new(UiText.Get("Text_878A28AF568C"), commands.RepeatLastRegion, Gesture: Gesture(keys.RepeatLastRegion)),
            ]),
        };

        // Reachable without a shortcut: a new installation leaves Paste to screen unassigned.
        if (commands.PinClipboard is { } pinClipboard)
        {
            entries.Add(new(UiText.Get("Tray_PinClipboard"), pinClipboard, Gesture: Gesture(keys.PasteToScreen),
                IsEnabled: state.ClipboardHasContent,
                ToolTip: state.ClipboardHasContent ? string.Empty : UiText.Get("Tray_ClipboardEmpty")));
        }

        // Showing the pins again also clears click-through on every pin.
        if (commands.HideOrShowAllPins is { } hideOrShow && state.PinCount > 0)
        {
            entries.Add(new(UiText.Get(state.PinsHidden ? "Tray_ShowAllPins" : "Tray_HideAllPins"), hideOrShow,
                Gesture: Gesture(keys.HideAllPins)));
        }

        // The way back from click-through in one step. A click-through pin cannot take the click
        // that would turn it off, and a new installation leaves both pin keys unassigned.
        if (commands.ClearClickThrough is { } clearClickThrough && state.ClickThroughPinCount > 0)
            entries.Add(new(UiText.Get("Tray_ClearClickThrough"), clearClickThrough));

        // Only while a closed pin can come back; the menu is rebuilt on every open.
        if (state.CanRestoreClosedPin)
            entries.Add(new(UiText.Get("Onboard_TrayReopenPin"), commands.RestoreClosedPin, Gesture: Gesture(keys.RestoreClosedPin)));

        entries.Add(ThemedShellPresenter.MenuEntry.Separator);
        entries.Add(new(UiText.Get("Text_8739F6684453"), commands.Library, Gesture: Gesture(keys.OpenLibrary)));
        entries.Add(new(UiText.Get("Text_00480581AAB5"), commands.Settings));
        entries.Add(new(UiText.Get("Onboard_ShowShortcuts"), commands.Shortcuts, Gesture: ShortcutsGesture));
        if (commands.ContactSupport is { } contactSupport)
            entries.Add(new(UiText.Get("Support_Title"), contactSupport));
        entries.Add(ThemedShellPresenter.MenuEntry.Separator);
        entries.Add(new(UiText.Get("Text_72B6DBA8AAC5"), commands.Exit));
        return entries;
    }

    private static string Gesture(Hotkey key) => key.IsAssigned ? key.ToString() : string.Empty;
}
