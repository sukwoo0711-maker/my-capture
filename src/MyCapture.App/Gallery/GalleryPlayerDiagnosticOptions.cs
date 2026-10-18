namespace MyCapture.App.Gallery;

internal enum GalleryPlayerOpenMode { Product, PauseImmediately, Playing }

/// <summary>Only the video-range diagnostic supplies these; product callers use the original constructor.</summary>
internal sealed record GalleryPlayerDiagnosticOptions(
    GalleryPlayerOpenMode OpenMode = GalleryPlayerOpenMode.Product,
    double? FrameHeightDip = null,
    bool CaptureExclusion = true,
    bool RoundedClip = true,
    bool ProductWindowStyle = true);
