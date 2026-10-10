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
    Action Exit);

/// <summary>
/// The tray menu's order, kept apart from the shell so it can be checked without a tray icon.
/// The two things people come for sit on top (region capture and screen recording), the rarer
/// capture types share one branch, the destinations follow, and Exit is always last. The display
/// language lives in Settings only.
/// </summary>
internal static class TrayMenuLayout
{
    /// <summary>F1 opens the shortcut window from any MyCapture window; the tray shows the same key.</summary>
    internal const string ShortcutsGesture = "F1";

    internal static IReadOnlyList<ThemedShellPresenter.MenuEntry> Build(
        TrayMenuCommands commands, HotkeySettings keys, bool canRestoreClosedPin, bool scrollingActive)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(keys);
        var entries = new List<ThemedShellPresenter.MenuEntry>
        {
            new(UiText.Get("Text_2C20ADDA2018"), commands.Capture, Gesture: Gesture(keys.Capture)),
            new(UiText.Get("Text_4F5AF20FAA6E"), commands.Record, Gesture: Gesture(keys.RecordRegion)),
            new(UiText.Get("Onboard_TrayMoreCaptures"), null,
            [
                new(UiText.Get("Text_EA1FBABE64F3"), commands.CaptureWindow, Gesture: Gesture(keys.CaptureWindow)),
                new(UiText.Get("Text_65BB6610ACA0"), commands.CaptureFullScreen, Gesture: Gesture(keys.CaptureFullScreen)),
                new(UiText.Get(scrollingActive ? "Text_E8B876F98CD8" : "Text_4DBA30197C0A"), commands.ScrollingCapture,
                    Gesture: Gesture(keys.ScrollingCapture)),
                new(UiText.Get("Text_38FF9B0AA541"), commands.DelayedCapture, Gesture: Gesture(keys.DelayedCapture)),
                new(UiText.Get("Text_878A28AF568C"), commands.RepeatLastRegion, Gesture: Gesture(keys.RepeatLastRegion)),
                new(UiText.Get("Onboard_TrayCopyText"), commands.CopyText, Gesture: Gesture(keys.CaptureText)),
            ]),
        };

        // Only while a closed pin can come back; the menu is rebuilt on every open.
        if (canRestoreClosedPin)
            entries.Add(new(UiText.Get("Onboard_TrayReopenPin"), commands.RestoreClosedPin, Gesture: Gesture(keys.RestoreClosedPin)));

        entries.Add(ThemedShellPresenter.MenuEntry.Separator);
        entries.Add(new(UiText.Get("Text_8739F6684453"), commands.Library, Gesture: Gesture(keys.OpenLibrary)));
        entries.Add(new(UiText.Get("Text_00480581AAB5"), commands.Settings));
        entries.Add(new(UiText.Get("Onboard_ShowShortcuts"), commands.Shortcuts, Gesture: ShortcutsGesture));
        entries.Add(ThemedShellPresenter.MenuEntry.Separator);
        entries.Add(new(UiText.Get("Text_72B6DBA8AAC5"), commands.Exit));
        return entries;
    }

    private static string Gesture(Hotkey key) => key.IsAssigned ? key.ToString() : string.Empty;
}
