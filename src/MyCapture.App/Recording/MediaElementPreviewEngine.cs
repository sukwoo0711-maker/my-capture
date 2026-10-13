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
    /// <summary>
    /// How far past the requested time the element is positioned. MediaElement presents the last
    /// frame that starts at or before its position, and the position reaches Media Foundation
    /// through Windows Media Player's double seconds, which can land one 100 ns tick early: a step
    /// to a frame's exact start (266.667 ms at 15, 30 or 60 fps) then showed the frame before it.
    /// This equals the export decoder's tolerance, so the preview shows the frame an export takes.
    /// </summary>
    internal const double PresentationToleranceMs = 0.01;

    private readonly MediaElement _media;
    private readonly Dispatcher _dispatcher;
    private bool _disposed;

    internal MediaElementPreviewEngine(MediaElement media)
    {
        _media = media ?? throw new ArgumentNullException(nameof(media));
        _dispatcher = media.Dispatcher;
    }

    /// <summary>Clip length; a seek at the very end is not moved past it.</summary>
    internal double DurationMs { get; set; } = double.PositiveInfinity;

    /// <summary>The element position that presents the frame shown at <paramref name="targetMs"/>.</summary>
    internal double PresentationPositionMs(double targetMs)
    {
        double target = Math.Max(0, targetMs);
        return target + PresentationToleranceMs < DurationMs ? target + PresentationToleranceMs : target;
    }

    public async ValueTask<PresentedPreviewFrame> SeekAsync(
        PreviewSeekRequest request,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        // InvokeAsync is intentional even when already on the UI thread: decoder work must not
        // run in the MouseMove call stack that produced the visual intent.
        double position = PresentationPositionMs(request.TargetPositionMs);
        double bias = position - Math.Max(0, request.TargetPositionMs);
        await _dispatcher.InvokeAsync(
            () =>
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _media.Pause();
                _media.Position = TimeSpan.FromMilliseconds(position);
            },
            DispatcherPriority.Background,
            cancellationToken).Task.ConfigureAwait(false);

        double presentedMs = request.TargetPositionMs;
        DispatcherPriority observedPriority = request.Mode == PreviewSeekMode.Exact
            ? DispatcherPriority.ApplicationIdle
            : DispatcherPriority.Render;
        await _dispatcher.InvokeAsync(
            () => presentedMs = Math.Max(0, _media.Position.TotalMilliseconds - bias),
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
