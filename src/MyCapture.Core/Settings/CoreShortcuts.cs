namespace MyCapture.Core.Settings;

/// <summary>The core shortcut assignments offered once at launch with the user's consent.</summary>
public static class CoreShortcuts
{
    /// <summary>Starts region capture.</summary>
    public static Hotkey Capture { get; } = new(HotkeyModifiers.Control | HotkeyModifiers.Shift, Hotkey.VkC);

    /// <summary>Starts or stops region recording.</summary>
    public static Hotkey RecordRegion { get; } = new(HotkeyModifiers.Control | HotkeyModifiers.Shift, Hotkey.VkX);

    /// <summary>Opens the library.</summary>
    public static Hotkey OpenLibrary { get; } = new(HotkeyModifiers.Control | HotkeyModifiers.Shift, Hotkey.VkZ);

    /// <summary>Pins clipboard contents to the screen.</summary>
    public static Hotkey PasteToScreen { get; } = new(HotkeyModifiers.None, Hotkey.VkF3);

    /// <summary>The four exact chords eligible for forced registration.</summary>
    public static IReadOnlyList<Hotkey> Chords { get; } = Array.AsReadOnly<Hotkey>(
        [Capture, RecordRegion, OpenLibrary, PasteToScreen]);

    /// <summary>Whether a chord exactly matches one of the four core shortcuts.</summary>
    public static bool IsCoreChord(Hotkey hotkey) => Chords.Contains(hotkey);

    /// <summary>Assigns the core shortcuts, removes other commands' conflicts and records consent.</summary>
    public static void Accept(HotkeySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        settings.Capture = Capture;
        settings.RecordRegion = RecordRegion;
        settings.OpenLibrary = OpenLibrary;
        settings.PasteToScreen = PasteToScreen;
        settings.ScrollingCapture = WithoutCoreChord(settings.ScrollingCapture);
        settings.DelayedCapture = WithoutCoreChord(settings.DelayedCapture);
        settings.HideAllPins = WithoutCoreChord(settings.HideAllPins);
        settings.ToggleClickThrough = WithoutCoreChord(settings.ToggleClickThrough);
        settings.RepeatLastRegion = WithoutCoreChord(settings.RepeatLastRegion);
        settings.CaptureWindow = WithoutCoreChord(settings.CaptureWindow);
        settings.CaptureFullScreen = WithoutCoreChord(settings.CaptureFullScreen);
        settings.UploadGitHubImage = WithoutCoreChord(settings.UploadGitHubImage);
        settings.RestoreClosedPin = WithoutCoreChord(settings.RestoreClosedPin);
        settings.CaptureText = WithoutCoreChord(settings.CaptureText);
        settings.PauseRecording = WithoutCoreChord(settings.PauseRecording);
        settings.ForceCoreShortcuts = true;
        settings.CoreShortcutsAsked = true;
    }

    /// <summary>Records an answer while preserving all shortcut assignments and preferences.</summary>
    public static void Decline(HotkeySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.CoreShortcutsAsked = true;
    }

    private static Hotkey WithoutCoreChord(Hotkey hotkey) => IsCoreChord(hotkey) ? Hotkey.None : hotkey;
}
