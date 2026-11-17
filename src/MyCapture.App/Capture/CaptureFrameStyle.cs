namespace MyCapture.App.Capture;

/// <summary>
/// Geometry of the selector's target frame, shared by the frame painter and the cues placed around it.
/// </summary>
/// <remarks>
/// The frame lies entirely outside the target, so it never covers a captured pixel: a one-pixel
/// contrast keyline touches the target, and the two-pixel guide-colour stroke sits outside that.
/// Values are physical pixels. When the target reaches a screen edge the painter keeps the frame on
/// screen, so cues must not assume the outset is always outside.
/// </remarks>
internal static class CaptureFrameStyle
{
    /// <summary>Width of the contrast keyline next to the target.</summary>
    internal const int KeylineWidth = 1;

    /// <summary>Width of the guide-colour stroke outside the keyline.</summary>
    internal const int StrokeWidth = 2;

    /// <summary>How far the frame reaches beyond the target on each side.</summary>
    internal const int Outset = KeylineWidth + StrokeWidth;
}
