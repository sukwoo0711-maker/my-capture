namespace MyCapture.Core.Undo;

/// <summary>
/// A reversible edit.
/// </summary>
/// <remarks>
/// Commands store the data needed to reverse themselves, not a snapshot of the whole
/// document. Snapshotting a document that can contain pasted bitmaps would make undo
/// depth expensive in memory, and the app keeps undo history for the lifetime of an
/// editing session.
/// </remarks>
public interface IUndoableCommand
{
    /// <summary>
    /// Shown in tooltips and in the undo history list.
    /// </summary>
    string Description { get; }

    void Execute();

    void Undo();

    /// <summary>
    /// Attempts to absorb <paramref name="next"/> into this command.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without merging, one drag of a colour slider or one resize gesture would push
    /// dozens of entries onto the stack and the user would have to press Ctrl+Z
    /// dozens of times to get back. Merging collapses a continuous gesture into a
    /// single reversible step.
    /// </para>
    /// <para>
    /// Implementations must only merge when the result is still exactly reversible:
    /// the merged command has to restore the state that existed before the first of
    /// the merged edits.
    /// </para>
    /// </remarks>
    /// <returns><see langword="true"/> when <paramref name="next"/> was absorbed.</returns>
    bool TryMergeWith(IUndoableCommand next) => false;
}
