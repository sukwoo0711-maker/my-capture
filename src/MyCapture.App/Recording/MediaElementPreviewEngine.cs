using System.Windows.Controls;
using System.Windows.Threading;
using MyCapture.Core.Recording;

namespace MyCapture.App.Recording;

/// <summary>
/// Phase-1 adapter around the existing WPF MediaElement. A seek is always posted to the
/// dispatcher instead of being executed inside a pointer event. MediaElement has no exact
/// seek-completed contract, so the reported position is a best-effort timestamp after WPF has
/// processed render-priority work; engine-comparison frame-accuracy gates do not apply here.
/// </summary>
internal sealed class MediaElementPreviewEngine : IVideoPreviewEngine
{
    private readonly MediaElement _media;
    private readonly Dispatcher _dispatcher;
    private bool _disposed;

    internal MediaElementPreviewEngine(MediaElement media)
    {
        _media = media ?? throw new ArgumentNullException(nameof(media));
        _dispatcher = media.Dispatcher;
    }

    public async ValueTask<PresentedPreviewFrame> SeekAsync(
        PreviewSeekRequest request,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        // InvokeAsync is intentional even when already on the UI thread: decoder work must not
        // run in the MouseMove call stack that produced the visual intent.
        await _dispatcher.InvokeAsync(
            () =>
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _media.Pause();
                _media.Position = TimeSpan.FromMilliseconds(Math.Max(0, request.TargetPositionMs));
            },
            DispatcherPriority.Background,
            cancellationToken).Task.ConfigureAwait(false);

        double presentedMs = request.TargetPositionMs;
        DispatcherPriority observedPriority = request.Mode == PreviewSeekMode.Exact
            ? DispatcherPriority.ApplicationIdle
            : DispatcherPriority.Render;
        await _dispatcher.InvokeAsync(
            () => presentedMs = _media.Position.TotalMilliseconds,
            observedPriority,
            cancellationToken).Task.ConfigureAwait(false);

        int presentedFrame = request.TargetFrameIndex;
        return new PresentedPreviewFrame(
            request.Generation,
            request.TargetPositionMs,
            presentedMs,
            presentedFrame,
            request.Mode);
    }

    public void Dispose() => _disposed = true;
}
