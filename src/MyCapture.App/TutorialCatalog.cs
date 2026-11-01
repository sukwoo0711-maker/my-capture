using MyCapture.Core.Settings;

namespace MyCapture.App;

internal sealed record FeatureTutorial(string Id, string CategoryKey, string TitleKey, IReadOnlyList<string> HelpActions,
    Func<HotkeySettings, Hotkey>? GlobalKey, Func<string>? FixedKeys, string? UnassignedKey)
{
    internal string DisplayName => $"{UiText.Get(CategoryKey)} · {UiText.Get(TitleKey)}";
    internal IReadOnlyList<string> Steps => Id == "text-copy"
        ? new[] { UiText.Get("TextCapture_TutorialStep1"), UiText.Get("TextCapture_TutorialStep2"), UiText.Get("TextCapture_TutorialStep3") }
        : Enumerable.Range(1, 3).Select(i => UiText.Get($"Tutorial.{Id}.Step{i}")).ToArray();
    internal IReadOnlyList<string> StepsFor(HotkeySettings hotkeys)
    {
        if (Id != "pin-click-through") return Steps;
        string recovery = hotkeys.ToggleClickThrough.IsAssigned ? hotkeys.ToggleClickThrough.ToString()
            : hotkeys.HideAllPins.IsAssigned ? UiText.Format("Hotkey.Twice", hotkeys.HideAllPins)
            : UiText.Get("Hotkey.TrayRestore");
        return [Steps[0], Steps[1], UiText.Format("Tutorial.pin-click-through.Exit", recovery)];
    }
    internal string EntryText(HotkeySettings hotkeys)
    {
        if (Id == "text-copy") return TextCopyTutorialWindow.CaptureEntryText(hotkeys);
        if (GlobalKey is not null)
        {
            Hotkey key = GlobalKey(hotkeys);
            return key.IsAssigned ? UiText.Format("Tutorial.CurrentKey", key) : UiText.Get(UnassignedKey!);
        }
        return FixedKeys is { } keys ? UiText.Format("Tutorial.CurrentKey", keys()) : UiText.Get(UnassignedKey!);
    }
}

/// <summary>One user workflow per animation; global shortcuts resolve from the current saved settings.</summary>
internal static class TutorialCatalog
{
    // Clips that may ship without their MP4 and poster yet; every practice clip now ships both.
    internal static readonly IReadOnlySet<string> PendingPracticeClips = new HashSet<string>(StringComparer.Ordinal);
    internal static IReadOnlyList<FeatureTutorial> All { get; } = Array.AsReadOnly<FeatureTutorial>(
    [
        new("capture-region", "Help.Capture", "Text_096A13A757DB", ["Text_096A13A757DB"], keys => keys.Capture, null, "Tutorial.capture-region.Unassigned"),
        new("record-region", "Help.Capture", "Text_75306DAE2E60", ["Text_75306DAE2E60"], keys => keys.RecordRegion, null, "Tutorial.record-region.Unassigned"),
        new("record-pause", "Help.Capture", "RecordingPauseHotkey_Command", ["RecordingPauseHotkey_Command"], keys => keys.PauseRecording, null, "Tutorial.record-pause.Unassigned"),
        new("library-open", "Help.Capture", "Text_66BA5DCDA810", ["Text_66BA5DCDA810"], keys => keys.OpenLibrary, null, "Tutorial.library-open.Unassigned"),
        new("capture-window", "Help.Capture", "Text_36458FABBDBB", ["Text_36458FABBDBB"], keys => keys.CaptureWindow, null, "Tutorial.capture-window.Unassigned"),
        new("capture-fullscreen", "Help.Capture", "Text_254C836B9ED2", ["Text_254C836B9ED2"], keys => keys.CaptureFullScreen, null, "Tutorial.capture-fullscreen.Unassigned"),
        new("capture-scroll", "Help.Capture", "Hotkey.Scroll", ["Hotkey.Scroll"], keys => keys.ScrollingCapture, null, "Tutorial.capture-scroll.Unassigned"),
        new("capture-delay", "Help.Capture", "Hotkey.Delay", ["Hotkey.Delay"], keys => keys.DelayedCapture, null, "Tutorial.capture-delay.Unassigned"),
        new("quick-access", "Help.Capture", "QuickAccess.Title", ["QuickAccess.Title"], null, () => ShortcutCatalog.Get(ShortcutScope.QuickAccess, "Activate").Hint, null),
        new("text-copy", "Help.Capture", "TextCapture_Title", ["TextCapture_Title", "TextCapture_HelpToggle"], keys => keys.CaptureText, null, null),
        new("color-copy", "Help.Capture", "ColorPick_HelpLabel", ["ColorPick_HelpLabel"], null, () => ShortcutCatalog.Get(ShortcutScope.Selector, "Color").Hint, null),
        new("image-rectangle", "Help.Annotation", "Text_DC0760235344", ["Text_DC0760235344"], null, () => ShortcutCatalog.Get(ShortcutScope.Annotation, "Rectangle").Hint, null),
        new("image-ellipse", "Help.Annotation", "Tools.Ellipse", ["Tools.Ellipse"], null, () => ShortcutCatalog.Get(ShortcutScope.Annotation, "Ellipse").Hint, null),
        new("image-arrow", "Help.Annotation", "Text_2785CE58DF74", ["Text_2785CE58DF74"], null, () => ShortcutCatalog.Get(ShortcutScope.Annotation, "Arrow").Hint, null),
        new("image-pen", "Help.Annotation", "Text_37DB9D0B1C8E", ["Text_37DB9D0B1C8E"], null, () => ShortcutCatalog.Get(ShortcutScope.Annotation, "Pen").Hint, null),
        new("image-highlighter", "Help.Annotation", "Tools.Highlighter", ["Tools.Highlighter"], null, () => ShortcutCatalog.Get(ShortcutScope.Annotation, "Highlighter").Hint, null),
        new("image-text", "Help.Annotation", "Text_258AD4B095A1", ["Text_258AD4B095A1"], null, () => ShortcutCatalog.Get(ShortcutScope.Annotation, "Text").Hint, null),
        new("image-step-number", "Help.Annotation", "Tools.StepNumber", ["Tools.StepNumber"], null, () => ShortcutCatalog.Get(ShortcutScope.Annotation, "StepNumber").Hint, null),
        new("image-mosaic", "Help.Annotation", "EditorObscure_Pixelate", ["EditorObscure_Pixelate"], null, () => ShortcutCatalog.Get(ShortcutScope.Annotation, "Mosaic").Hint, null),
        new("image-blur", "Help.Annotation", "EditorBlur_Name", ["EditorBlur_Name"], null, () => ShortcutCatalog.Get(ShortcutScope.Annotation, "Blur").Hint, null),
        new("image-crop", "Help.Annotation", "Tools_Crop", ["Tools_Crop"], null, () => ShortcutCatalog.Get(ShortcutScope.Annotation, "Crop").Hint, null),
        new("image-rotate", "Help.Annotation", "Tutorial.image-rotate.Title", ["EditorUx_RotateLeftHint", "EditorUx_RotateRightHint"], null, () => ShortcutCatalog.Get(ShortcutScope.Annotation, "RotateLeft").Hint + " / " + ShortcutCatalog.Get(ShortcutScope.Annotation, "RotateRight").Hint, null),
        new("image-save", "Help.Annotation", "QuickAccess.Save", ["QuickAccess.Save"], null, () => ShortcutCatalog.Get(ShortcutScope.Annotation, "Save").Hint, null),
        new("image-copy", "Help.Annotation", "EditorFlow_CopyAndClose", ["EditorFlow_CopyAndClose"], null, () => ShortcutCatalog.Get(ShortcutScope.Annotation, "Copy").Hint, null),
        new("folder-create", "Workspace.Library", "Folder410.NewFolder", ["Folder410.NewFolder"], null, () => ShortcutCatalog.Get(ShortcutScope.Library, "NewFolder").Hint, null),
        new("folder-rename", "Workspace.Library", "Folder410.Rename", ["Folder410.Rename"], null, () => ShortcutCatalog.Get(ShortcutScope.Library, "Rename").Hint, null),
        new("library-move", "Workspace.Library", "Folder410.MoveCaptures", ["Folder410.MoveCaptures"], null, () => ShortcutCatalog.Get(ShortcutScope.Library, "Move").Hint, null),
        new("library-delete", "Workspace.Library", "Folder410.Delete", ["Folder410.Delete"], null, () => ShortcutCatalog.Get(ShortcutScope.Library, "Delete").Hint, null),
        new("video-play", "Help.Video", "Help.Play", ["Help.Play"], null, () => ShortcutCatalog.Get(ShortcutScope.Video, "Play").Hint, null),
        new("video-frame-step", "Help.Video", "Help.Frame", ["Help.Frame"], null, () => ShortcutHelpWindow.FrameStepKeys, null),
        new("video-trim", "Help.Video", "Tutorial.video-trim.Title", ["Video.TrimIn", "Video.TrimOut"], null, () => ShortcutCatalog.Get(ShortcutScope.Video, "In").Hint + " / " + ShortcutCatalog.Get(ShortcutScope.Video, "Out").Hint, null),
        new("video-gif-export", "Help.Video", "Help.Gif", ["Help.Gif"], null, () => ShortcutCatalog.Get(ShortcutScope.Video, "Gif").Hint, null),
        new("pin-resize", "Text_33EFD44366A7", "Tutorial.pin-resize.Title", [], null, null, "Journey.ResizeHelp"),
        new("pin-opacity", "Text_33EFD44366A7", "Tutorial.pin-opacity.Title", [], null, null, "Journey.B3.Body"),
        new("practice-image-copy", "Help.Welcome", "Tutorial.practice-image-copy.Title", [], null, null, "Journey.B0.Body"),
        new("practice-image-pin", "Text_33EFD44366A7", "Tutorial.practice-image-pin.Title", [], keys => keys.PasteToScreen, null, "Journey.B1.NoKey"),
        new("practice-editor-open", "Help.Annotation", "Tutorial.practice-editor-open.Title", [], null, null, "Journey.A3.Body"),
        new("practice-pin-close", "Text_33EFD44366A7", "Tutorial.practice-pin-close.Title", [], null, null, "Journey.B5.Body"),
        new("practice-library-find", "Workspace.Library", "Tutorial.practice-library-find.Title", [], null, null, "Journey.C2.Body"),
        new("pin-clipboard", "Text_33EFD44366A7", "Text_1FBCF51E2FF5", ["Text_1FBCF51E2FF5", "Dialog.Close"], keys => keys.PasteToScreen, null, "Tutorial.pin-clipboard.Unassigned"),
        new("pin-hide-all", "Text_33EFD44366A7", "Text_4FED89116C1F", ["Text_4FED89116C1F"], keys => keys.HideAllPins, null, "Tutorial.pin-hide-all.Unassigned"),
        new("pin-click-through", "Text_33EFD44366A7", "Onboard_PinAllowClickThrough", ["Onboard_PinAllowClickThrough"], keys => keys.ToggleClickThrough, null, "Tutorial.pin-click-through.Unassigned"),
        new("pin-restore", "Text_33EFD44366A7", "Onboard_TrayReopenPin", ["Onboard_TrayReopenPin"], keys => keys.RestoreClosedPin, null, "Tutorial.pin-restore.Unassigned"),
        new("pin-copy", "Text_33EFD44366A7", "QuickAccess.Copy", ["QuickAccess.Copy"], null, () => ShortcutCatalog.Get(ShortcutScope.Pin, "Copy").Hint, null),
        new("pin-save", "Text_33EFD44366A7", "QuickAccess.Save", ["QuickAccess.Save"], null, () => ShortcutCatalog.Get(ShortcutScope.Pin, "Save").Hint, null),
    ]);
    internal static FeatureTutorial Get(string id) => All.First(item => item.Id == id);
    internal static FeatureTutorial? FindHelp(string categoryKey, string actionKey) =>
        All.FirstOrDefault(item => item.CategoryKey == categoryKey && item.HelpActions.Contains(actionKey));
}
