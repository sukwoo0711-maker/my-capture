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

    /// <summary>
    /// How far apart Windows Media Player needs two paused seeks to act on the second. It stops a
    /// paused seek up to a millisecond past its position (0 to 0.99 ms, before and after playback),
    /// ignores a seek that moves it by less than about a millisecond (0.9 ms ignored, 1.2 ms
    /// moved), and keeps the frame it shows for a seek about that close to the position it was
    /// last given. Across a frame boundary either one left the preview on the frame before.
    /// </summary>
    internal const double SeekResolutionMs = 2;

    private readonly MediaElement _media;
    private readonly Dispatcher _dispatcher;
    private readonly double _frameMs;
    // The element position this engine set last; dispatcher-owned, NaN until known.
    private double _lastPositionMs = double.NaN;
    private bool _disposed;

    internal MediaElementPreviewEngine(MediaElement media, int fps)
    {
        _media = media ?? throw new ArgumentNullException(nameof(media));
        _dispatcher = media.Dispatcher;
        _frameMs = FrameStepCalculator.FrameDurationMs(fps);
    }

    /// <summary>Clip length; a seek at the very end is not moved past it.</summary>
    internal double DurationMs { get; set; } = double.PositiveInfinity;

    /// <summary>The element position that presents the frame shown at <paramref name="targetMs"/>.</summary>
    internal double PresentationPositionMs(double targetMs)
    {
        double target = Math.Max(0, targetMs);
        return target + PresentationToleranceMs < DurationMs ? target + PresentationToleranceMs : target;
    }

    /// <summary>
    /// Where to position the element for <paramref name="positionMs"/>: there, unless the player
    /// would not act on it, then at the nearest position in the same frame that is at least
    /// <see cref="SeekResolutionMs"/> from where the player stands and from where it was last
    /// positioned. Any position in a frame presents that frame. While the player stands where it
    /// was last positioned, a seek within that frame needs no new frame and is left as it is.
    /// </summary>
    internal static double SeparatedPositionMs(
        double positionMs,
        double currentMs,
        double lastPositionMs,
        double frameMs,
        double durationMs)
    {
        if (!(frameMs > 0) || (IsApart(positionMs, currentMs) && IsApart(positionMs, lastPositionMs)))
        {
            return positionMs;
        }

        double frameStart = Math.Floor(positionMs / frameMs) * frameMs;
        if (Math.Floor(lastPositionMs / frameMs) * frameMs == frameStart && HasLanded(currentMs, lastPositionMs))
        {
            return positionMs;
        }

        double first = frameStart + PresentationToleranceMs;
        double last = Math.Min(frameStart + frameMs, durationMs) - PresentationToleranceMs;
        for (double offset = SeekResolutionMs; offset < frameMs; offset += SeekResolutionMs)
        {
            if (Fits(positionMs + offset))
            {
                return positionMs + offset;
            }

            if (Fits(positionMs - offset))
            {
                return positionMs - offset;
            }
        }

        return positionMs;

        bool Fits(double candidate) =>
            candidate >= first && candidate <= last
            && IsApart(candidate, currentMs) && IsApart(candidate, lastPositionMs);
    }

    // An unknown (NaN) position is apart from every other.
    private static bool IsApart(double aMs, double bMs) => !(Math.Abs(aMs - bMs) < SeekResolutionMs);

    public async ValueTask<PresentedPreviewFrame> SeekAsync(
        PreviewSeekRequest request,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        // This seek positions the element again, so a watch of the last one has nothing left to do.
        await StopWatchAsync().ConfigureAwait(false);
        double target = Math.Max(0, request.TargetPositionMs);
        DispatcherPriority observedPriority = request.Mode == PreviewSeekMode.Exact
            ? DispatcherPriority.ApplicationIdle
            : DispatcherPriority.Render;
        (double position, double observedMs, bool ended) = await PositionAsync(
            PresentationPositionMs(target),
            separate: true,
            observedPriority,
            cancellationToken).ConfigureAwait(false);
        FirstReadOffsetForTest = observedMs - position;
        bool nearEnd = DurationMs - position < EndStretchMs;
        if (nearEnd)
        {
            _settleNextSeek = true;
        }

        // After the end of the stream the first seek presents its frame, though its first read can
        // already show the player past the position; the watch puts the player back afterwards.
        bool watch = request.Mode == PreviewSeekMode.Exact && !nearEnd && _settleNextSeek;
        int seeks = 1;
        int reopens = 0;
        while (true)
        {
            if (ended)
            {
                // A seek into the last frame can end the stream again right after a reopen.
                if (reopens == MaxReopens)
                {
                    break;
                }

                reopens++;
                await ReopenAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (request.Mode != PreviewSeekMode.Exact
                || watch
                || HasLanded(observedMs, position)
                || seeks >= MaxExactSeeks)
            {
                break;
            }

            seeks++;
            // The same position again: after a miss the frame is presented and only the player's
            // clock ran on, and after a reopen the player stands on its first frame.
            (_, observedMs, ended) = await PositionAsync(position, separate: false, observedPriority, cancellationToken)
                .ConfigureAwait(false);
        }

        if (watch && !ended)
        {
            StartWatch(position, observedPriority);
        }

        // A landing within the player's resolution shows the requested frame; the rest is where
        // the player's clock stopped, which can already be in the next frame's time.
        double presentedMs = (watch && !ended) || HasLanded(observedMs, position)
            ? target
            : Math.Max(0, observedMs - (position - target));
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
    /// Whether a paused seek landed where it was asked to, within the player's resolution
    /// (<see cref="SeekResolutionMs"/>): closer than that, asking again does not move the player.
    /// After the stream has run to or near its end (a playback, or the brief playback with which
    /// WPF presents a paused seek into the last frame), the next paused seek can stop past its
    /// position, by milliseconds under load and by more than a frame at worst; asking again lands it.
    /// </summary>
    internal static bool HasLanded(double observedMs, double elementPositionMs) =>
        Math.Abs(observedMs - elementPositionMs) <= SeekResolutionMs;

    // Two reads are the same position to the 100 ns sample clock.
    private const double ReadingToleranceMs = 0.001;

    /// <summary>
    /// The stream ran to its end. After that, WPF holds the player internally stopped, and the
    /// first paused seek from there presents the right frame but leaves the player stopped past
    /// the position (3 to 74 ms on loaded runners): the brief playback that fetches the frame is
    /// not undone. Its first read can still show the requested position, so that seek is watched
    /// until the position stays put, and asked again if it moved. The seek reports its frame at
    /// once, even when its first read already shows the player past it, and the watch runs after
    /// that: waiting for it held the release after a scrub into the last stretch for 108 to 140 ms
    /// on a desktop and 184 to 189 ms on runners, against about 15 ms for other seeks. A playback
    /// start waits for the watch (<see cref="WhenSettledAsync"/>), and the next seek ends it.
    /// </summary>
    internal void StreamReachedEnd() => _settleNextSeek = true;

    /// <summary>
    /// Completes when the watch of the last seek, if one runs, has ended: the player then stands
    /// where the seek asked, so playback starts there.
    /// </summary>
    internal Task WhenSettledAsync()
    {
        lock (_watchGate)
        {
            return _watch;
        }
    }

    // Set by the end of the stream, a seek into its last stretch included (the brief playback
    // can reach the end), and cleared by a watched seek elsewhere that stayed where it landed.
    private volatile bool _settleNextSeek;

    // The watch of the last watched seek and its cancellation, under _watchGate.
    private readonly object _watchGate = new();
    private Task _watch = Task.CompletedTask;
    private CancellationTokenSource? _watchCancel;

    // A seek this close to the end can end the stream while WPF fetches its frame.
    private const double EndStretchMs = 200;

    // The position counts as settled once it reads the same this long.
    private static readonly TimeSpan SettlePoll = TimeSpan.FromMilliseconds(15);
    private const int SettleStableReads = 4;
    private static readonly TimeSpan SettleLimit = TimeSpan.FromMilliseconds(600);

    // An exact seek is asked again at most twice; each try takes one dispatcher round trip.
    private const int MaxExactSeeks = 3;

    // Each reopen takes a few hundred milliseconds; one more covers the stream ending again.
    private const int MaxReopens = 2;

    /// <summary>
    /// Test hook: the next read is taken as the media-ended report. The state itself comes from a
    /// race inside Windows Media Player that no test can start on demand.
    /// </summary>
    internal bool TreatNextReadAsEndedForTest { get; set; }

    /// <summary>How many times the clip was opened again to leave the media-ended state.</summary>
    internal int ReopenCountForTest { get; private set; }

    /// <summary>How many reads were the player's own media-ended report, not an injected one.</summary>
    internal int EndedReportCountForTest { get; private set; }

    /// <summary>Test hook: the next read is taken as a seek that stopped 3 ms past its position.</summary>
    internal bool MissNextLandingForTest { get; set; }

    /// <summary>How many times the element was positioned, counting each repeated try.</summary>
    internal int PositionCountForTest { get; private set; }

    /// <summary>How many reads were watched until the position stayed put.</summary>
    internal int SettleCountForTest { get; private set; }

    /// <summary>How far past the element position the first read of the last seek was.</summary>
    internal double FirstReadOffsetForTest { get; private set; }

    // The ended report is the player's length itself, to the 100 ns tick.
    private const double LengthReportToleranceMs = 0.001;

    // A local clip normally reopens within a few hundred milliseconds.
    private static readonly TimeSpan ReopenTimeout = TimeSpan.FromSeconds(5);

    // InvokeAsync is intentional even when already on the UI thread: decoder work must not run
    // in the MouseMove call stack that produced the visual intent. Returns the element position
    // set, which with separate is the SeparatedPositionMs of the one asked for.
    private async Task<(double PositionMs, double ObservedMs, bool Ended)> PositionAsync(
        double position,
        bool separate,
        DispatcherPriority observedPriority,
        CancellationToken cancellationToken)
    {
        await _dispatcher.InvokeAsync(
            () =>
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                PositionCountForTest++;
                if (separate)
                {
                    position = SeparatedPositionMs(
                        position, _media.Position.TotalMilliseconds, _lastPositionMs, _frameMs, DurationMs);
                }

                StudioRangeVideoEffect.PositionRequested(_media);
                _media.Pause();
                _media.Position = TimeSpan.FromMilliseconds(position);
                _lastPositionMs = position;
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
                bool report = _media.HasVideo && IsMediaEndedReport(observedMs, position, PlayerLengthMs());
                EndedReportCountForTest += report ? 1 : 0;
                ended = report || (_media.HasVideo && TreatNextReadAsEndedForTest);
                TreatNextReadAsEndedForTest = false;
                if (MissNextLandingForTest)
                {
                    MissNextLandingForTest = false;
                    observedMs += 3;
                }
            },
            observedPriority,
            cancellationToken).Task.ConfigureAwait(false);
        return (position, observedMs, ended);
    }

    private void StartWatch(double position, DispatcherPriority priority)
    {
        var cancel = new CancellationTokenSource();
        Task watch = RunWatchAsync(position, priority, cancel.Token);
        lock (_watchGate)
        {
            _watch = watch;
            _watchCancel = cancel;
        }
    }

    // Ends the watch of the last seek if it still runs; a failure it met surfaces here once.
    private async Task StopWatchAsync()
    {
        Task watch;
        CancellationTokenSource? cancel;
        lock (_watchGate)
        {
            watch = _watch;
            cancel = _watchCancel;
            _watchCancel = null;
        }

        if (cancel is null)
        {
            return;
        }

        cancel.Cancel();
        try
        {
            await watch.ConfigureAwait(false);
        }
        finally
        {
            cancel.Dispose();
        }
    }

    private async Task RunWatchAsync(double position, DispatcherPriority priority, CancellationToken cancellationToken)
    {
        try
        {
            await WatchAsync(position, priority, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A later seek took over the element, or the editor's dispatcher shut down.
        }
        catch (ObjectDisposedException)
        {
            // The editor closed.
        }
    }

    // See StreamReachedEnd: reads until the position stays put and asks again if it moved.
    private async Task WatchAsync(double position, DispatcherPriority priority, CancellationToken cancellationToken)
    {
        int seeks = 1;
        int reopens = 0;
        while (true)
        {
            (double observedMs, bool ended) = await SettleAsync(position, priority, cancellationToken)
                .ConfigureAwait(false);
            if (ended)
            {
                if (reopens == MaxReopens)
                {
                    return;
                }

                reopens++;
                await ReopenAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (HasLanded(observedMs, position))
            {
                _settleNextSeek = false;
                return;
            }
            else if (seeks >= MaxExactSeeks)
            {
                return;
            }

            seeks++;
            _ = await PositionAsync(position, separate: false, priority, cancellationToken).ConfigureAwait(false);
        }
    }

    // Reads until the position stays the same for SettleStableReads polls, or SettleLimit.
    private async Task<(double ObservedMs, bool Ended)> SettleAsync(
        double position,
        DispatcherPriority observedPriority,
        CancellationToken cancellationToken)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        double last = double.NaN;
        bool ended = false;
        int stable = 0;
        while (stable < SettleStableReads && watch.Elapsed < SettleLimit)
        {
            await Task.Delay(SettlePoll, cancellationToken).ConfigureAwait(false);
            double now = 0;
            await _dispatcher.InvokeAsync(
                () =>
                {
                    now = _media.Position.TotalMilliseconds;
                    ended = _media.HasVideo && IsMediaEndedReport(now, position, PlayerLengthMs());
                },
                observedPriority,
                cancellationToken).Task.ConfigureAwait(false);
            stable = Math.Abs(now - last) < ReadingToleranceMs ? stable + 1 : 0;
            last = now;
        }

        SettleCountForTest++;
        EndedReportCountForTest += ended ? 1 : 0;
        return (last, ended);
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
                _lastPositionMs = double.NaN;
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

    public void Dispose()
    {
        _disposed = true;
        lock (_watchGate)
        {
            _watchCancel?.Cancel();
        }
    }
}
