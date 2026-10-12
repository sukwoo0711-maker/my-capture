using System.ComponentModel;
using System.Windows;
using MyCapture.Core.Settings;

namespace MyCapture.App;

/// <summary>Compatibility entry point for the original text-copy tutorial.</summary>
internal sealed class TextCopyTutorialWindow : FeatureTutorialWindow
{
    internal TextCopyTutorialWindow(HotkeySettings hotkeys, Func<ITutorialMedia>? mediaFactory = null)
        : base(hotkeys, "text-copy", mediaFactory) { }

    internal static string CaptureEntryText(HotkeySettings hotkeys) => hotkeys.Capture.IsAssigned
        ? UiText.Format("TextCapture_TutorialCaptureKey", hotkeys.Capture)
        : UiText.Get("TextCapture_TutorialCaptureMenu");

    internal static void ShowTutorial(Window owner, HotkeySettings hotkeys) =>
        FeatureTutorialWindow.ShowTutorial(owner, hotkeys, "text-copy");

    // Keep the original test's reflection entry point while sharing runtime motion behavior.
    private new void OnMotionPreferenceChanged(object? sender, PropertyChangedEventArgs e) =>
        base.OnMotionPreferenceChanged(sender, e);
}
