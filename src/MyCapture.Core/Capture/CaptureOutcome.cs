namespace MyCapture.Core.Capture;

/// <summary>
/// Why an advanced capture ended.
/// </summary>
/// <remarks>
/// A capture tool must never leave the user guessing. Every one of the advanced modes
/// (delayed, window, full-monitor, repeat-last-region, scrolling) resolves to exactly one
/// of these states so the shell can give precise, non-throwing status feedback rather than
/// swallowing a failure or crashing the message pump.
/// </remarks>
public enum CaptureOutcomeKind
{
    /// <summary>Pixels were acquired and handed to the editor / persistence pipeline.</summary>
    Completed,

    /// <summary>The user cancelled before pixels were committed (Esc, focus loss).</summary>
    Cancelled,

    /// <summary>
    /// The mode could not run because a required precondition was absent, for example a
    /// repeat-last-region with no recorded region, or a window mode with no candidate under
    /// the cursor. Distinct from <see cref="Failed"/>: nothing went wrong, there was just
    /// nothing to do.
    /// </summary>
    NothingToCapture,

    /// <summary>An error prevented the capture. <see cref="CaptureOutcome.Message"/> explains.</summary>
    Failed,
}

/// <summary>
/// The typed result of an advanced capture attempt.
/// </summary>
/// <remarks>
/// Deliberately WPF-free and living in Core so the mode logic and its tests never depend on
/// a dispatcher. The shell maps <see cref="Kind"/> to tray state and balloons.
/// </remarks>
public sealed record CaptureOutcome(
    CaptureOutcomeKind Kind,
    string Message = "")
{
    public bool IsCompleted => Kind == CaptureOutcomeKind.Completed;

    public static CaptureOutcome Completed(string message = "") =>
        new(CaptureOutcomeKind.Completed, message);

    public static CaptureOutcome Cancelled(string message = "") =>
        new(CaptureOutcomeKind.Cancelled, message);

    public static CaptureOutcome NothingToCapture(string message = "") =>
        new(CaptureOutcomeKind.NothingToCapture, message);

    public static CaptureOutcome Failed(string message) =>
        new(CaptureOutcomeKind.Failed, message);
}
