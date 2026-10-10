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
/// Every action flattens the annotations onto the original. Done, Quick Save and Save As
/// then save the result into the library (the capture queue), copy it to the clipboard and
/// close the editor; any failure keeps the editor open for a retry. Copy is the one
/// secondary action: clipboard only, the library is untouched and the editor stays open.
/// <see cref="CaptureCommitService.CommitAsync"/> carries out each action.
/// </para>
/// </remarks>
internal enum EditorCommitAction
{
    /// <summary>
    /// Done (the filled button), Ctrl+Enter, or Save and close in the close confirmation.
    /// Flatten, save into the library, copy to the clipboard, then close.
    /// </summary>
    Done,

    /// <summary>
    /// Copy, Ctrl+C. Flatten and copy to the clipboard only. Nothing is saved to the library
    /// and the editor stays open; the status line reports whether the copy succeeded.
    /// </summary>
    CopyToClipboard,

    /// <summary>
    /// Save to file ▸ Quick save, Ctrl+S. Flatten, save into the library, write a PNG to the
    /// configured folder, copy to the clipboard, then close. A failed file write or copy keeps
    /// the editor open.
    /// </summary>
    QuickSave,

    /// <summary>
    /// Save to file ▸ Save as (or the smaller-image export), Ctrl+Shift+S. Flatten and choose a
    /// file first, so cancelling the dialog keeps the editor open with nothing saved; then
    /// write the file, save into the library, copy to the clipboard, and close.
    /// </summary>
    SaveAs,
}
