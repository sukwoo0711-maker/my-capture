namespace MyCapture.App.Pinning;

/// <summary>Why a lightweight (native) pin moved to the WPF host. The move happens once and never back.</summary>
internal enum PinSwitchReason
{
    /// <summary>A key the native host does not handle yet (F2, O, arrows, Ctrl+C, Ctrl+S, Ctrl+Shift+S).</summary>
    Key,

    /// <summary>The context menu (right-click, Shift+F10, the menu key).</summary>
    ContextMenu,

    /// <summary>Ctrl+double-click (text recognition) or Shift+double-click (fold).</summary>
    Gesture,

    /// <summary>Click-through turned on.</summary>
    ClickThrough,

    /// <summary>A command only the WPF host has (text selection, translation, fold, hover bar).</summary>
    Command,

    /// <summary>The setting was turned off while the pin was open.</summary>
    SettingOff,

    /// <summary>A screen reader started while the pin was open.</summary>
    ScreenReader,

    /// <summary>The native window failed after it was created.</summary>
    Failure,
}
