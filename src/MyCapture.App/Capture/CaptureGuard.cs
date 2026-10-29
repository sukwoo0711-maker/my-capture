using MyCapture.Platform.Shell;

namespace MyCapture.App.Capture;

/// <summary>Why a capture command did not start; <see cref="None"/> lets it start.</summary>
internal enum CaptureBlock
{
    None,

    /// <summary>A Store update or a library import holds the app until it finishes.</summary>
    AppUpdating,

    /// <summary>The app is getting ready to exit.</summary>
    AppExiting,

    /// <summary>An earlier full-screen, window or repeat capture is still taking its frame.</summary>
    CaptureStarting,

    /// <summary>The region selector is open or about to open.</summary>
    Selecting,

    /// <summary>The previous selection's editor is about to open.</summary>
    EditorOpening,

    /// <summary>The editor is asking what to do with its unsaved edits.</summary>
    EditorAsking,

    /// <summary>The editor is saving or copying its result.</summary>
    EditorSaving,

    /// <summary>The editor is closing, another of its dialogs is open, or an editing operation runs.</summary>
    EditorBusy,

    /// <summary>An editor is open and this command does not replace it.</summary>
    EditorOpen,

    /// <summary>Asked about unsaved edits, the user kept the editor open.</summary>
    EditorKept,

    /// <summary>Asked about unsaved edits, the user chose to save, and saving failed.</summary>
    EditorSaveFailed,

    /// <summary>A delayed capture is counting down.</summary>
    Countdown,

    /// <summary>A scrolling capture is running.</summary>
    ScrollingCapture,

    /// <summary>A recording is starting or finishing and cannot take a still image now.</summary>
    RecordingBusy,
}

/// <summary>What stands between a capture command and a new capture at the moment it arrives.</summary>
internal readonly record struct CaptureGuardState
{
    internal bool AppUpdating { get; init; }
    internal bool AppExiting { get; init; }
    internal bool CaptureStarting { get; init; }
    internal bool Selecting { get; init; }
    internal bool EditorOpening { get; init; }
    internal bool EditorOpen { get; init; }
    internal bool EditorAsking { get; init; }
    internal bool EditorSaving { get; init; }
    internal bool EditorBusy { get; init; }
    internal bool Countdown { get; init; }
    internal bool ScrollingCapture { get; init; }
    internal bool RecordingActive { get; init; }
    internal bool RecordingCanCaptureStill { get; init; }
}

/// <summary>
/// Decides whether a capture command may start and, when it may not, why. The shell turns every
/// answer other than <see cref="CaptureBlock.None"/> into something the user sees (the editor or
/// selector brought forward, a one-line notice), so a blocked command never fails silently.
/// </summary>
internal static class CaptureGuard
{
    /// <summary>Normal capture commands keep idle editors and their unsaved work open.</summary>
    internal static CaptureBlock Find(in CaptureGuardState state, GlobalHotkeyCommand command) => Find(state,
        allowRetake: command is GlobalHotkeyCommand.CaptureRegion or GlobalHotkeyCommand.CaptureText
            or GlobalHotkeyCommand.CaptureFullScreen or GlobalHotkeyCommand.CaptureWindow
            or GlobalHotkeyCommand.RepeatLastRegion or GlobalHotkeyCommand.DelayedCapture
            or GlobalHotkeyCommand.ScrollingCapture or GlobalHotkeyCommand.RecordRegion);

    /// <summary>The first blocker, in the order the shell has always checked them.</summary>
    /// <param name="allowRetake">The caller can proceed with an idle editor; this check never closes it.</param>
    internal static CaptureBlock Find(in CaptureGuardState state, bool allowRetake)
    {
        if (state.AppUpdating) return CaptureBlock.AppUpdating;
        if (state.AppExiting) return CaptureBlock.AppExiting;
        if (state.CaptureStarting) return CaptureBlock.CaptureStarting;
        if (state.Selecting) return CaptureBlock.Selecting;
        if (state.EditorOpening) return CaptureBlock.EditorOpening;
        if (state.EditorOpen)
        {
            // Asking, saving or busy, the editor cannot be closed now, even by a command that replaces it.
            if (state.EditorAsking) return CaptureBlock.EditorAsking;
            if (state.EditorSaving) return CaptureBlock.EditorSaving;
            if (state.EditorBusy) return CaptureBlock.EditorBusy;
            if (!allowRetake) return CaptureBlock.EditorOpen;
        }

        if (state.Countdown) return CaptureBlock.Countdown;
        if (state.ScrollingCapture) return CaptureBlock.ScrollingCapture;
        return state.RecordingActive && !state.RecordingCanCaptureStill ? CaptureBlock.RecordingBusy : CaptureBlock.None;
    }

    /// <summary>
    /// Checks the blockers, closes a replaceable editor the way its own close does (unsaved edits
    /// are confirmed first), then checks again: closing can take a while and others may start meanwhile.
    /// </summary>
    /// <param name="find">Returns the blocker for a command that may (true) or may not replace the editor.</param>
    /// <param name="closeEditor">Returns <see cref="CaptureBlock.None"/> once the editor has closed, otherwise why it stayed open.</param>
    internal static async Task<CaptureBlock> PrepareAsync(bool canRetake, Func<bool, CaptureBlock> find,
        Func<Task<CaptureBlock>> closeEditor)
    {
        CaptureBlock block = find(canRetake);
        if (block != CaptureBlock.None || !canRetake) return block;
        block = await closeEditor();
        return block != CaptureBlock.None ? block : find(false);
    }

    /// <summary>
    /// The resource key of the one line the editor shows when it kept a command from starting, or
    /// null when the editor is not what stopped it.
    /// </summary>
    /// <param name="recording">The command was the screen recording, not a still capture.</param>
    internal static string? EditorNoticeKey(CaptureBlock block, bool recording = false) => block switch
    {
        CaptureBlock.EditorSaveFailed => "CaptureBlocked_EditorSaveFailed",
        CaptureBlock.EditorAsking or CaptureBlock.EditorSaving or CaptureBlock.EditorBusy
            or CaptureBlock.EditorOpen or CaptureBlock.EditorKept =>
            recording ? "CaptureBlocked_EditorRecording" : "CaptureBlocked_Editor",
        _ => null,
    };
}
