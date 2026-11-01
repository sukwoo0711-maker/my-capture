using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using MyCapture.Core.Settings;

namespace MyCapture.App.Editing;

internal sealed partial class AnnotationEditorControl
{
    private Button _pinButton = null!;
    private (Button Button, string Hint, string Label)? _pinShortcutPresentation;

    internal bool AllowsPin
    {
        get => _pinButton.Visibility == Visibility.Visible;
        set => _pinButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void RefreshPinShortcut()
    {
        if (_pinButton is null) return;
        Hotkey key = AnnotationEditorPreferences.ReadPinHotkey?.Invoke() ?? new HotkeySettings().PasteToScreen;
        string hint = key.IsAssigned ? key.ToString() : string.Empty;
        string label = UiText.Get("EditorFlow_PinHint");
        // Loaded and focus changes can arrive together. The shared command handler
        // refreshes inherited font/DPI; unchanged shortcut metadata needs no new layout.
        if (_pinShortcutPresentation == (_pinButton, hint, label)) return;
        _pinShortcutPresentation = (_pinButton, hint, label);
        AutomationProperties.SetAcceleratorKey(_pinButton, hint);
        _pinButton.ToolTip = label + (hint.Length > 0 ? $" ({hint})" : string.Empty);
        CommandButtonPresentation.RefreshHint(_pinButton);
    }
}
