using System.Globalization;
using MyCapture.App.Pinning;
using MyCapture.Core.Pin;
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
    Action? ContactSupport = null,
    Action<PinWindow>? ActivateOpenPin = null,
    Action? InstallUpdate = null,
    Action? FirstUseGuide = null);

/// <summary>What the tray menu reflects at the moment it opens. Read cheaply on every open.</summary>
/// <param name="CanRestoreClosedPin">A closed pin can come back.</param>
/// <param name="ScrollingActive">A scrolling capture is running, so its entry cancels it.</param>
/// <param name="ClipboardHasContent">The clipboard holds an image or text a pin can show.</param>
/// <param name="PinCount">Open pins, shown or hidden.</param>
/// <param name="PinsHidden">At least one open pin is hidden.</param>
/// <param name="ClickThroughPinCount">Open pins that let clicks through to the windows behind.</param>
/// <param name="OpenPins">Open pin windows in newest-first opening order, including hidden pins.</param>
internal sealed record TrayMenuState(
    bool CanRestoreClosedPin = false,
    bool ScrollingActive = false,
    bool ClipboardHasContent = true,
    int PinCount = 0,
    bool PinsHidden = false,
    int ClickThroughPinCount = 0,
    IReadOnlyList<PinWindow>? OpenPins = null,
    bool UpdateAvailable = false,
    string? UpdateVersion = null,
    bool UpdateBusy = false);

/// <summary>
/// The tray menu's order, kept apart from the shell so it can be checked without a tray icon.
/// The first group holds what people come for: region capture, screen recording, text capture,
/// the rarer capture types in one branch. Pin commands form their own group with recovery first,
/// followed by destinations, the Help branch, and Exit. Display language lives in Settings only.
/// </summary>
internal static class TrayMenuLayout
{
    /// <summary>F1 opens the shortcut window from any MyCapture window; the tray shows the same key.</summary>
    internal const string ShortcutsGesture = "F1";
    internal const int MaxOpenPinEntries = 15;
    internal const int MaxPinTitleLength = 40;
    private const int MaxPinTitleCodeUnits = 512;

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

        int captureEnd = entries.Count;

        // A new installation assigns F3; the tray also works when the user clears the shortcut.
        if (commands.PinClipboard is { } pinClipboard)
        {
            entries.Add(new(UiText.Get("Tray_PinClipboard"), pinClipboard, Gesture: Gesture(keys.PasteToScreen),
                IsEnabled: state.ClipboardHasContent,
                ToolTip: state.ClipboardHasContent ? string.Empty : UiText.Get("Tray_ClipboardEmpty")));
        }

        // Recovery stays at the top of the pin group, even when pin shortcuts are unassigned.
        if (commands.ClearClickThrough is { } clearClickThrough && state.ClickThroughPinCount > 0)
            entries.Add(new(UiText.Get("Tray_ClearClickThrough"), clearClickThrough));
        if (state.CanRestoreClosedPin)
            entries.Add(new(UiText.Get("Onboard_TrayReopenPin"), commands.RestoreClosedPin, Gesture: Gesture(keys.RestoreClosedPin)));

        // Find one pin before the commands that act on every pin. Rebuilt on each menu open.
        if (commands.ActivateOpenPin is { } activatePin && state.OpenPins is { Count: > 0 } openPins)
        {
            var children = openPins.Take(MaxOpenPinEntries)
                .Select(pin => new ThemedShellPresenter.MenuEntry(OpenPinLabel(pin.PinnedContent), () => activatePin(pin),
                    Gesture: pin.State.IsHidden ? UiText.Get("Tray_PinHidden") : string.Empty,
                    LiteralLabel: true, Thumbnail: pin.PinnedContent.Image,
                    IsMuted: pin.State.IsHidden, ItemStatus: pin.State.IsHidden ? UiText.Get("Tray_PinHidden") : string.Empty))
                .ToList();
            if (openPins.Count > MaxOpenPinEntries)
                children.Add(new(UiText.Format("Tray_MoreOpenPins", openPins.Count - MaxOpenPinEntries), null, IsEnabled: false));
            entries.Add(new(UiText.Get("Tray_OpenPins"), null, children));
        }

        // Showing the pins again also clears click-through on every pin.
        if (commands.HideOrShowAllPins is { } hideOrShow && state.PinCount > 0)
        {
            entries.Add(new(UiText.Get(state.PinsHidden ? "Tray_ShowAllPins" : "Tray_HideAllPins"), hideOrShow,
                Gesture: Gesture(keys.HideAllPins)));
        }

        if (entries.Count > captureEnd)
            entries.Insert(captureEnd, ThemedShellPresenter.MenuEntry.Separator);
        entries.Add(ThemedShellPresenter.MenuEntry.Separator);
        entries.Add(new(UiText.Get("Text_8739F6684453"), commands.Library, Gesture: Gesture(keys.OpenLibrary)));
        entries.Add(new(UiText.Get("Text_00480581AAB5"), commands.Settings));
        if (state.UpdateAvailable && commands.InstallUpdate is { } installUpdate)
            entries.Add(new(state.UpdateVersion is { } version ? UiText.Format("Update_TrayInstallVersion", version)
                : UiText.Get("Update_TrayInstall"), installUpdate, IsEnabled: !state.UpdateBusy));
        entries.Add(ThemedShellPresenter.MenuEntry.Separator);
        var help = new List<ThemedShellPresenter.MenuEntry>
        {
            new(UiText.Get("Onboard_ShowShortcuts"), commands.Shortcuts, Gesture: ShortcutsGesture),
        };
        if (commands.FirstUseGuide is { } firstUseGuide)
            help.Add(new(UiText.Get("Onboarding.Replay"), firstUseGuide));
        if (commands.ContactSupport is { } contactSupport)
            help.Add(new(UiText.Get("Support_Title"), contactSupport));
        entries.Add(new(UiText.Get("Tray_Help"), null, help));
        entries.Add(ThemedShellPresenter.MenuEntry.Separator);
        entries.Add(new(UiText.Get("Text_72B6DBA8AAC5"), commands.Exit));
        return entries;
    }

    private static string Gesture(Hotkey key) => key.IsAssigned ? key.ToString() : string.Empty;

    private static string OpenPinLabel(PinContent content)
    {
        string kind = UiText.Get(content.Kind switch
        {
            PinContentKind.Image => "Tray_PinKindImage",
            PinContentKind.Table => "Tray_PinKindTable",
            _ when content.TextStyle == PinTextStyle.Code => "Tray_PinKindCode",
            _ => "Tray_PinKindText",
        });
        string title = OpenPinTitle(content.OriginalText, content.Kind == PinContentKind.Table);
        if (title.Length == 0) title = OpenPinTitle(content.Provenance.WindowTitle, isTable: false);
        if (title.Length == 0)
            title = UiText.Format("Tray_PinImageTitle", content.Image.PixelWidth, content.Image.PixelHeight);
        return $"{title} · {kind}";
    }

    private static string OpenPinTitle(string? source, bool isTable)
    {
        ReadOnlySpan<char> remaining = source.AsSpan();
        Span<char> buffer = stackalloc char[MaxPinTitleCodeUnits];
        while (!remaining.IsEmpty)
        {
            int lineEnd = remaining.IndexOfAny('\r', '\n');
            ReadOnlySpan<char> line = lineEnd < 0 ? remaining : remaining[..lineEnd];
            remaining = lineEnd < 0 ? [] : remaining[(lineEnd + 1)..];
            if (line.Trim().IsEmpty) continue;

            // Only normalize a bounded prefix. A source line or a single grapheme may be huge.
            int length = 0;
            bool complete;
            if (isTable)
            {
                complete = AppendTableTitle(line, buffer, ref length);
            }
            else
            {
                complete = AppendTitlePart(line.Trim(), buffer, ref length);
                for (int i = 0; i < length; i++)
                    if (buffer[i] == '\t') buffer[i] = ' ';
            }

            // If the prefix ends inside a surrogate pair, remove it before finding graphemes.
            if (!complete && length > 0 && char.IsHighSurrogate(buffer[length - 1])) length--;
            string title = new(buffer[..length]);
            int[] elements = StringInfo.ParseCombiningCharacters(title);
            if (complete && elements.Length <= MaxPinTitleLength) return title;

            // An incomplete prefix's last grapheme may continue outside the buffer.
            int keep = Math.Min(MaxPinTitleLength - 1, complete ? elements.Length : Math.Max(0, elements.Length - 1));
            int end = keep < elements.Length ? elements[keep] : title.Length;
            return title[..end].TrimEnd() + "…";
        }
        return string.Empty;
    }

    private static bool AppendTableTitle(ReadOnlySpan<char> row, Span<char> buffer, ref int length)
    {
        while (true)
        {
            int cellEnd = row.IndexOf('\t');
            ReadOnlySpan<char> cell = cellEnd < 0 ? row : row[..cellEnd];
            if (!AppendTitlePart(cell.Trim(), buffer, ref length)) return false;
            if (cellEnd < 0) return true;
            if (!AppendTitlePart(" | ", buffer, ref length)) return false;
            row = row[(cellEnd + 1)..];
        }
    }

    private static bool AppendTitlePart(ReadOnlySpan<char> part, Span<char> buffer, ref int length)
    {
        int copied = Math.Min(part.Length, buffer.Length - length);
        part[..copied].CopyTo(buffer[length..]);
        length += copied;
        return copied == part.Length;
    }
}
