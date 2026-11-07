using MyCapture.App.Capture;

namespace MyCapture.App.Store;

/// <summary>
/// Which results count toward the Store rating invitation. Each answer is <see langword="true"/> for a
/// success, <see langword="false"/> for an error and <see langword="null"/> when nothing is recorded.
/// </summary>
/// <remarks>
/// Every user action counts once, a success inside the practice window does not count, and an error is
/// always recorded so the invitation never follows it.
/// </remarks>
internal static class StoreRatingOutcomes
{
    /// <summary>
    /// A still capture that was saved and copied. A practice capture's success does not count, and a capture
    /// taken as a pin is counted by its new pin instead (<see cref="Pinning.PinManager.PinCreated"/>).
    /// </summary>
    internal static bool? FromStillCapture(bool delivered, bool practice, bool pinRequested) =>
        !delivered ? false : practice || pinRequested ? null : true;

    /// <summary>Text copied by text capture or the capture card; no text and a superseded run record nothing.</summary>
    internal static bool? FromTextCapture(TextCaptureStatus status) => status switch
    {
        TextCaptureStatus.Copied => true,
        TextCaptureStatus.ClipboardFailed or TextCaptureStatus.Unavailable or TextCaptureStatus.Failed => false,
        _ => null,
    };

    /// <summary>The practice window is waiting for its pin step, so the new pin is the practice's own.</summary>
    internal static bool IsPracticePin(PracticeSession? session) => session is { Ended: false, Step: PracticeStep.B1 };
}
