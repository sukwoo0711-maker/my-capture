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
        bool nearEnd = DurationMs - position < EndStretchMs;
        if (nearEnd)
        {
            _settleNextSeek = true;
        }

        bool settle = request.Mode == PreviewSeekMode.Exact && !nearEnd && _settleNextSeek;
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
            else if (request.Mode != PreviewSeekMode.Exact)
            {
                break;
            }
            else
            {
                if (settle && HasLanded(observedMs, position))
                {
                    (observedMs, ended) = await SettleAsync(position, observedPriority, cancellationToken)
                        .ConfigureAwait(false);
                    if (ended)
                    {
                        continue;
                    }

                    if (HasLanded(observedMs, position))
                    {
                        _settleNextSeek = false;
                    }
                }

                if (HasLanded(observedMs, position) || seeks >= MaxExactSeeks)
                {
                    break;
                }
            }

            seeks++;
            (observedMs, ended) = await PositionAsync(position, observedPriority, cancellationToken)
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
    /// Whether a paused seek landed where it was asked to, within the 100 ns sample clock. After
    /// the stream has run to or near its end (a playback, or the brief playback with which WPF
    /// presents a paused seek into the last frame), the next paused seek can stop past its
    /// position, by milliseconds under load and by more than a frame at worst; asking again lands it.
    /// </summary>
    internal static bool HasLanded(double observedMs, double elementPositionMs) =>
        Math.Abs(observedMs - elementPositionMs) <= LandingToleranceMs;

    // One 100 ns tick early is the player's rounding (see PresentationToleranceMs), not a miss.
    private const double LandingToleranceMs = 0.001;

    /// <summary>
    /// The stream ran to its end. After that, WPF holds the player internally stopped, and the
    /// first paused seek from there presents the right frame but leaves the player stopped past
    /// the position (3 to 74 ms on loaded runners): the brief playback that fetches the frame is
    /// not undone. Its first read still shows the requested position, so that seek is watched
    /// until the position stays put, and asked again if it moved.
    /// </summary>
    internal void StreamReachedEnd() => _settleNextSeek = true;

    // Set by the end of the stream, a seek into its last stretch included (the brief playback
    // can reach the end), and cleared by a watched seek elsewhere that stayed where it landed.
    private volatile bool _settleNextSeek;

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
                PositionCountForTest++;
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
        return (observedMs, ended);
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
            stable = Math.Abs(now - last) < LandingToleranceMs ? stable + 1 : 0;
            last = now;
        }

        SettleCountForTest++;
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
