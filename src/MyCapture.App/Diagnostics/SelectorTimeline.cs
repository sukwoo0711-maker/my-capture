namespace MyCapture.App.Diagnostics;

/// <summary>
/// Named moments on the way from the capture command to the selector's first frame (the guard, the
/// window list, the grab on its worker, the hidden window, the frame attached, shown, first drawn),
/// for the resident probe's first-capture breakdown.
/// </summary>
/// <remarks>
/// Off in the product: nothing sets <see cref="Mark"/>, so each moment costs one null check and
/// changes nothing. Marks may arrive from the grab's worker thread; the probe's handler is
/// thread-safe.
/// </remarks>
internal static class SelectorTimeline
{
    internal static Action<string>? Mark { get; set; }
}
