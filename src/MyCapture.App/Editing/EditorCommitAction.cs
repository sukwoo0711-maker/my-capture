namespace MyCapture.App.Editing;

/// <summary>
/// What the user asked the editor to do when they committed the edit.
/// </summary>
/// <remarks>
/// <para>
/// The editor is UI-thread WPF, but this enum carries no WPF type, so it can flow
/// through <see cref="AnnotationEditingResult"/> to the persistence/clipboard/export
/// layer without leaking a WPF dependency into the intent itself.
/// </para>
/// <para>
/// Every action persists the flattened capture into the queue. They differ only in
/// what they additionally do with the pixels — nothing, clipboard, quick-save, or a
/// chosen file — and in whether a failure keeps the editor open.
/// </para>
/// </remarks>
internal enum EditorCommitAction
{
    /// <summary>
    /// Ctrl+Enter / Done. Persist and close; no clipboard, no export.
    /// </summary>
    Done,

    /// <summary>
    /// Ctrl+C. Flatten, persist, copy the image to the clipboard, then close.
    /// </summary>
    CopyToClipboard,

    /// <summary>
    /// Ctrl+S. Flatten, persist, quick-save a PNG to the configured directory, and
    /// optionally copy to the clipboard, then close.
    /// </summary>
    QuickSave,

    /// <summary>
    /// Ctrl+Shift+S. Flatten, persist, and save through a file dialog. The editor
    /// closes only when the save succeeds; cancelling or failing keeps it open.
    /// </summary>
    SaveAs,
}
