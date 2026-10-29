using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using MyCapture.Core.Settings;

namespace MyCapture.App.Editing;

internal sealed partial class AnnotationEditorControl
{
    private Button _pinButton = null!;

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
        AutomationProperties.SetAcceleratorKey(_pinButton, hint);
        _pinButton.ToolTip = UiText.Get("EditorFlow_PinHint") + (hint.Length > 0 ? $" ({hint})" : string.Empty);
        CommandButtonPresentation.RefreshHint(_pinButton);
    }
}
