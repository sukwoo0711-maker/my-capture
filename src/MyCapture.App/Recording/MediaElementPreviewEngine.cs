using System.Windows;
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

        double position = PresentationPositionMs(request.TargetPositionMs);
        double bias = position - Math.Max(0, request.TargetPositionMs);
        DispatcherPriority observedPriority = request.Mode == PreviewSeekMode.Exact
            ? DispatcherPriority.ApplicationIdle
            : DispatcherPriority.Render;
        (double observedMs, bool ended) = await PositionAsync(position, observedPriority, cancellationToken)
            .ConfigureAwait(false);
        if (ended)
        {
            await ReopenAsync(cancellationToken).ConfigureAwait(false);
            (observedMs, _) = await PositionAsync(position, observedPriority, cancellationToken)
                .ConfigureAwait(false);
        }

        double presentedMs = Math.Max(0, observedMs - bias);
        int presentedFrame = request.TargetFrameIndex;
        return new PresentedPreviewFrame(
            request.Generation,
            request.TargetPositionMs,
            presentedMs,
            presentedFrame,
            request.Mode);
    }

    /// <summary>
    /// Whether a position read after a seek is Windows Media Player's media-ended report rather
    /// than where the seek landed. After the stream reaches its end (in playback, and in the
    /// video-editor self-test after paused seeks near the end, which WPF presents by playing
    /// briefly), the player occasionally stays in WPF's media-ended state: Position then reads
    /// the player's own length, which it truncates to whole seconds (2000 ms for a 2533 ms clip,
    /// zero below one second), whatever was requested, and no later seek, pause or play presents
    /// a frame again.
    /// </summary>
    internal static bool IsMediaEndedReport(double observedMs, double elementPositionMs, double playerLengthMs) =>
        Math.Abs(observedMs - playerLengthMs) < LengthReportToleranceMs
        && Math.Abs(elementPositionMs - playerLengthMs) >= LengthReportToleranceMs;

    /// <summary>
    /// Test hook: the next read is taken as the media-ended report. The state itself comes from a
    /// race inside Windows Media Player that no test can start on demand.
    /// </summary>
    internal bool TreatNextReadAsEndedForTest { get; set; }

    /// <summary>How many times the clip was opened again to leave the media-ended state.</summary>
    internal int ReopenCountForTest { get; private set; }

    // The ended report is the player's length itself, to the 100 ns tick.
    private const double LengthReportToleranceMs = 0.001;

    // A local clip normally reopens within a few hundred milliseconds.
    private static readonly TimeSpan ReopenTimeout = TimeSpan.FromSeconds(5);

    // InvokeAsync is intentional even when already on the UI thread: decoder work must not run
    // in the MouseMove call stack that produced the visual intent.
    private async Task<(double ObservedMs, bool Ended)> PositionAsync(
        double position,
        DispatcherPriority observedPriority,
        CancellationToken cancellationToken)
    {
        await _dispatcher.InvokeAsync(
            () =>
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                StudioRangeVideoEffect.PositionRequested(_media);
                _media.Pause();
                _media.Position = TimeSpan.FromMilliseconds(position);
            },
            DispatcherPriority.Background,
            cancellationToken).Task.ConfigureAwait(false);

        double observedMs = position;
        bool ended = false;
        await _dispatcher.InvokeAsync(
            () =>
            {
                observedMs = _media.Position.TotalMilliseconds;
                // An element that never opened also reads zero; only an opened clip has ended.
                ended = _media.HasVideo
                    && (IsMediaEndedReport(observedMs, position, PlayerLengthMs()) || TreatNextReadAsEndedForTest);
                TreatNextReadAsEndedForTest = false;
            },
            observedPriority,
            cancellationToken).Task.ConfigureAwait(false);
        return (observedMs, ended);
    }

    // MediaElement reports no duration when the player's whole-second length is zero.
    private double PlayerLengthMs() =>
        _media.NaturalDuration.HasTimeSpan ? _media.NaturalDuration.TimeSpan.TotalMilliseconds : 0;

    /// <summary>
    /// Opens the clip again, the only way WPF leaves the media-ended state besides a playback
    /// error: it resets that state for a new source. The element is paused on its first frame
    /// when this returns, or after the timeout, after which the caller's read reports what it shows.
    /// </summary>
    private async Task ReopenAsync(CancellationToken cancellationToken)
    {
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnOpened(object? sender, RoutedEventArgs e) => opened.TrySetResult();
        void OnFailed(object? sender, ExceptionRoutedEventArgs e) => opened.TrySetResult();
        await _dispatcher.InvokeAsync(
            () =>
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                Uri? source = _media.Source;
                if (source is null)
                {
                    opened.TrySetResult();
                    return;
                }

                ReopenCountForTest++;
                _media.MediaOpened += OnOpened;
                _media.MediaFailed += OnFailed;
                _media.Source = null;
                _media.Source = source;
                _media.Play();
                _media.Pause();
            },
            DispatcherPriority.Background,
            cancellationToken).Task.ConfigureAwait(false);

        try
        {
            await opened.Task.WaitAsync(ReopenTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
        finally
        {
            _ = _dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() =>
                {
                    _media.MediaOpened -= OnOpened;
                    _media.MediaFailed -= OnFailed;
                }));
        }
    }

    public void Dispose() => _disposed = true;
}
