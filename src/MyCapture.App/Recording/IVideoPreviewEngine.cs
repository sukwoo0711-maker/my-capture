using MyCapture.Core.Recording;

namespace MyCapture.App.Recording;

/// <summary>
/// Narrow preview-only media boundary. Frame extraction and trim export deliberately remain
/// separate contracts because they have different accuracy, lifetime, and threading needs.
/// </summary>
internal interface IVideoPreviewEngine : IDisposable
{
    ValueTask<PresentedPreviewFrame> SeekAsync(
        PreviewSeekRequest request,
        CancellationToken cancellationToken);
}
