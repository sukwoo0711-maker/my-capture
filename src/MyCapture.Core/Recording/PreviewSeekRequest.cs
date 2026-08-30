namespace MyCapture.Core.Recording;

/// <summary>Quality/priority requested from a video preview engine.</summary>
public enum PreviewSeekMode
{
    /// <summary>Responsive, sampled seek used while a pointer is moving.</summary>
    Preview,

    /// <summary>Priority seek used after release or for frame-sensitive actions.</summary>
    Exact,
}

/// <summary>
/// Immutable seek command. Generation is the sole ordering key used to suppress stale results.
/// </summary>
public readonly record struct PreviewSeekRequest(
    long Generation,
    double TargetPositionMs,
    int TargetFrameIndex,
    PreviewSeekMode Mode);

/// <summary>A preview position reported after an engine has accepted/presented a seek.</summary>
public readonly record struct PresentedPreviewFrame(
    long Generation,
    double RequestedPositionMs,
    double PresentedPositionMs,
    int PresentedFrameIndex,
    PreviewSeekMode Mode);
