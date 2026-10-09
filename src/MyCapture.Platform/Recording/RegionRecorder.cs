using System.Diagnostics;
using System.Threading;
using Microsoft.Extensions.Logging;
using MyCapture.Core.Diagnostics;
using MyCapture.Core.Primitives;
using MyCapture.Core.Recording;

namespace MyCapture.Platform.Recording;

/// <summary>
/// Result of a finished recording.
/// </summary>
/// <param name="OutputPath">Path to the written MP4.</param>
/// <param name="DurationMs">Recorded duration: capture time with every paused span excluded.</param>
/// <param name="Fps">Target frame rate the clip was encoded at.</param>
/// <param name="EmittedFrames">Frames actually written (after any drops).</param>
/// <param name="Width">Frame width.</param>
/// <param name="Height">Frame height.</param>
public sealed record RecordingResult(
    string OutputPath,
    double DurationMs,
    int Fps,
    long EmittedFrames,
    int Width,
    int Height)
{
    public RecordingPerformance? Performance { get; init; }

    /// <summary>Wall-clock time spent paused. Never part of <see cref="DurationMs"/> or the clip.</summary>
    public double PausedMs { get; init; }

    /// <summary>Number of times the user paused this recording.</summary>
    public int PauseCount { get; init; }

    private RecordingFrameMetrics Metrics => new(DurationMs, Fps, EmittedFrames);

    /// <summary>Frames that should have been produced over the recorded (unpaused) time.</summary>
    public long ExpectedFrames => Metrics.ExpectedFrames;

    /// <summary>Whole frame intervals skipped to keep playback aligned to real time.</summary>
    public long DroppedFrames => Metrics.DroppedFrames;

    /// <summary>Frames actually encoded per second of recorded (unpaused) time.</summary>
    public double EffectiveFps => Metrics.EffectiveFps;

    /// <summary>Ratio of skipped intervals to expected intervals, from 0 to 1.</summary>
    public double DropRate => Metrics.DropRate;
}

/// <summary>Constant-space production timing totals, separate from the recorded duration.</summary>
public sealed record RecordingPerformance(double InitializationMs, double CaptureTotalMs,
    double CaptureMaxMs, double EncodeTotalMs, double EncodeMaxMs, double FinalizationMs);

/// <summary>
/// Drives the grab → pace → encode loop for a region recording on a dedicated
/// background thread.
/// </summary>
/// <remarks>
/// <para>
/// The loop never runs on the UI thread, so a slow software encoder on a weak PC can
/// never freeze the interface. A <see cref="RecordingClock"/> decides when each frame
/// is due from elapsed wall-clock time; if the encoder falls behind, whole frame
/// intervals are skipped (adaptive frame drop) and the surviving frames keep their
/// true timestamps, so playback stays real-time instead of speeding up.
/// </para>
/// <para>
/// The encoder is created through a factory so tests inject a fake that only records
/// timestamps, exercising the pacing and stop/finalise contract without Media
/// Foundation. The time source is injectable for the same reason.
/// </para>
/// <para>
/// Pausing stops grabbing and encoding entirely; the encoder simply idles. A
/// <see cref="RecordingPauseTimeline"/> removes every paused span from the timestamps, so
/// the clip continues seamlessly after a resume and its length is the recorded time only.
/// </para>
/// </remarks>
public sealed class RegionRecorder : IDisposable
{
    // Upper bound for one parked wait. Resume and Stop pulse the gate, so this only limits
    // how long a missed pulse could delay the loop.
    private const int PausedWaitMs = 250;

    private readonly RegionFrameGrabber _grabber;
    private readonly Func<VideoEncoderOptions, IVideoEncoder> _encoderFactory;
    private readonly ILogger _log;
    private readonly TimeProvider _time;

    // Guards the pause timeline and the parked flag; the capture thread and the UI thread
    // read the time base inside this lock so the timeline only ever moves forwards.
    private readonly object _pauseGate = new();
    private RecordingPauseTimeline _pauses = new();
    private bool _captureParked;

    private Thread? _thread;
    private volatile bool _stopRequested;
    private IVideoEncoder? _encoder;
    private RecordingResult? _result;
    private Exception? _failure;
    private long _recordingStarted;
    private long _recordingEnded;

    public bool IsReady => Interlocked.Read(ref _recordingStarted) != 0 && IsRecording && !_stopRequested;

    /// <summary>Recorded time so far, excluding paused spans. Frozen while paused.</summary>
    public TimeSpan RecordedElapsed
    {
        get
        {
            if (Interlocked.Read(ref _recordingStarted) == 0)
            {
                return TimeSpan.Zero;
            }

            lock (_pauseGate)
            {
                long ended = Interlocked.Read(ref _recordingEnded);
                double raw = RawElapsedMs(ended == 0 ? _time.GetTimestamp() : ended);
                return TimeSpan.FromMilliseconds(_pauses.ActiveElapsedMs(raw));
            }
        }
    }

    /// <summary>True between <see cref="Pause"/> and <see cref="Resume"/>.</summary>
    public bool IsPaused
    {
        get
        {
            lock (_pauseGate)
            {
                return _pauses.IsPaused;
            }
        }
    }

    public RegionRecorder(
        RegionFrameGrabber grabber,
        Func<VideoEncoderOptions, IVideoEncoder> encoderFactory,
        ILogger log,
        TimeProvider? timeProvider = null)
    {
        _grabber = grabber ?? throw new ArgumentNullException(nameof(grabber));
        _encoderFactory = encoderFactory ?? throw new ArgumentNullException(nameof(encoderFactory));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _time = timeProvider ?? TimeProvider.System;
    }

    public bool IsRecording => _thread is { IsAlive: true };

    /// <summary>
    /// Pauses capture: no frame is grabbed or encoded until <see cref="Resume"/>, and the
    /// paused span is excluded from the clip timeline and the frame-rate metrics.
    /// </summary>
    /// <returns><see langword="false"/> when not recording, stopping or already paused.</returns>
    public bool Pause()
    {
        lock (_pauseGate)
        {
            if (!IsRecording || _stopRequested || !_pauses.TryPause(RawElapsedMs(_time.GetTimestamp())))
            {
                return false;
            }
        }

        _log.LogInformation("Recording paused");
        return true;
    }

    /// <summary>Resumes a paused recording; the next frame continues the clip timeline.</summary>
    /// <returns><see langword="false"/> when the recording is not paused.</returns>
    public bool Resume()
    {
        lock (_pauseGate)
        {
            if (!_pauses.TryResume(RawElapsedMs(_time.GetTimestamp())))
            {
                return false;
            }

            _captureParked = false;
            Monitor.PulseAll(_pauseGate);
        }

        _log.LogInformation("Recording resumed");
        return true;
    }

    /// <summary>
    /// Waits until the capture loop has finished any frame already in flight and parked for
    /// the current pause, so nothing drawn afterwards can reach the clip.
    /// </summary>
    /// <returns><see langword="true"/> once parked; <see langword="false"/> on timeout, resume or stop.</returns>
    public bool WaitUntilCaptureParked(TimeSpan timeout)
    {
        long deadline = Environment.TickCount64 + (long)Math.Max(0, timeout.TotalMilliseconds);
        lock (_pauseGate)
        {
            while (!_captureParked)
            {
                long remaining = deadline - Environment.TickCount64;
                if (!_pauses.IsPaused || !IsRecording || remaining <= 0)
                {
                    return false;
                }

                Monitor.Wait(_pauseGate, (int)Math.Min(remaining, 50));
            }

            return true;
        }
    }

    /// <summary>
    /// Opens the encoder and starts the capture thread.
    /// </summary>
    public void Start(RectD screenRegion, string outputPath, RecordingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        if (IsRecording)
        {
            throw new InvalidOperationException("A recording is already in progress.");
        }

        Interlocked.Exchange(ref _recordingStarted, 0);
        Interlocked.Exchange(ref _recordingEnded, 0);
        _stopRequested = false;
        _result = null;
        _failure = null;
        lock (_pauseGate)
        {
            _pauses = new RecordingPauseTimeline();
            _captureParked = false;
        }

        _grabber.Open(screenRegion);

        int bitrate = settings.BitrateBitsPerSecond > 0
            ? settings.BitrateBitsPerSecond
            : VideoEncoderOptions.DeriveBitrate(_grabber.Width, _grabber.Height, settings.TargetFps);

        var options = new VideoEncoderOptions(
            outputPath,
            _grabber.Width,
            _grabber.Height,
            settings.TargetFps,
            bitrate);

        // The encoder is created INSIDE the capture thread (see CaptureLoop), not here. The
        // Media Foundation Sink Writer is an apartment-bound COM object: if it is created on
        // the caller's STA UI thread and then used from this background thread, the cross-
        // apartment QueryInterface fails with E_NOINTERFACE the first time a frame is written.
        // Creating and using it on one dedicated MTA thread keeps every COM call in-apartment.
        var clock = new RecordingClock(settings.TargetFps);
        _thread = new Thread(() => CaptureLoop(clock, options))
        {
            IsBackground = true,
            Name = "MyCapture.RegionRecorder",
            // Below-normal keeps the recorder from starving the app being recorded on
            // a single-core-constrained machine.
            Priority = ThreadPriority.BelowNormal,
        };
        // Media Foundation work belongs on an MTA thread; make it explicit rather than
        // relying on the runtime default for a fresh thread.
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();

        _log.LogInformation(
            "Recording started: {Width}x{Height} @ {Fps}fps, {Bitrate}bps -> {Path}",
            _grabber.Width,
            _grabber.Height,
            settings.TargetFps,
            bitrate,
            LogText.SingleLine(outputPath));
    }

    /// <summary>
    /// Signals the loop to stop and blocks until the file is finalised.
    /// </summary>
    public RecordingResult Stop()
    {
        if (_thread is null)
        {
            throw new InvalidOperationException("No recording is in progress.");
        }

        RequestStopAndWake();
        _thread.Join();
        _thread = null;

        if (_failure is not null)
        {
            throw new InvalidOperationException("Recording failed.", _failure);
        }

        return _result ?? throw new InvalidOperationException("Recording produced no result.");
    }

    private void CaptureLoop(RecordingClock clock, VideoEncoderOptions options)
    {
        long initializationStarted = Stopwatch.GetTimestamp();
        double captureTotal = 0, captureMax = 0, encodeTotal = 0, encodeMax = 0;
        try
        {
            // Create the encoder here, on this MTA capture thread, so the MF Sink Writer's
            // COM objects live in the same apartment that will write every frame.
            _encoder = _encoderFactory(options);
            double initializationMs = Stopwatch.GetElapsedTime(initializationStarted).TotalMilliseconds;
            Interlocked.Exchange(ref _recordingStarted, _time.GetTimestamp());

            // Always execute one capture iteration after encoder initialisation. H.264 MFT
            // startup can occasionally take longer than a very short recording; if Stop was
            // requested during that startup, finalising with zero samples fails with
            // MF_E_SINK_NO_SAMPLES_PROCESSED (0xC00D4A44). One timestamp-zero frame produces a
            // valid short clip without blocking the UI thread while the encoder warms up.
            // The same rule holds for a pause requested during startup: the loop only parks
            // once that first frame exists.
            while (true)
            {
                if (ParkWhilePaused(clock.EmittedFrames > 0))
                {
                    // Never grab while paused, not even on the way out: a frame taken now would
                    // show the paused period at the timestamp where the recorded span ended.
                    if (_stopRequested)
                    {
                        break;
                    }

                    continue;
                }

                double elapsed = ActiveElapsedMs();
                if (clock.TryClaimFrame(elapsed, out double timestampMs))
                {
                    long phaseStarted = Stopwatch.GetTimestamp();
                    byte[] pixels = _grabber.GrabInto();
                    double captureMs = Stopwatch.GetElapsedTime(phaseStarted).TotalMilliseconds;
                    captureTotal += captureMs;
                    captureMax = Math.Max(captureMax, captureMs);
                    phaseStarted = Stopwatch.GetTimestamp();
                    _encoder!.WriteFrame(new EncoderFrame(
                        pixels,
                        _grabber.Width,
                        _grabber.Height,
                        _grabber.Stride,
                        timestampMs));
                    double encodeMs = Stopwatch.GetElapsedTime(phaseStarted).TotalMilliseconds;
                    encodeTotal += encodeMs;
                    encodeMax = Math.Max(encodeMax, encodeMs);
                }

                if (_stopRequested)
                {
                    break;
                }

                double sleep = clock.MillisecondsUntilNextFrame(ActiveElapsedMs());
                if (sleep >= 1)
                {
                    // Cap the sleep so a stop request is honoured promptly even at low fps.
                    Thread.Sleep((int)Math.Min(sleep, 50));
                }
            }

            long endedAt = _time.GetTimestamp();
            double recordedMs, pausedMs;
            int pauseCount;
            lock (_pauseGate)
            {
                // Stopping while paused closes the open span here, so the recorded duration
                // ends where the last recorded span ended.
                double rawEnd = RawElapsedMs(endedAt);
                _pauses.End(rawEnd);
                recordedMs = _pauses.ActiveElapsedMs(rawEnd);
                pausedMs = _pauses.PausedMs(rawEnd);
                pauseCount = _pauses.PauseCount;
                Interlocked.Exchange(ref _recordingEnded, endedAt);
            }

            long finalizeStarted = Stopwatch.GetTimestamp();
            _encoder!.Complete();
            double finalizationMs = Stopwatch.GetElapsedTime(finalizeStarted).TotalMilliseconds;

            _result = new RecordingResult(
                options.OutputPath,
                recordedMs,
                options.Fps,
                clock.EmittedFrames,
                _grabber.Width,
                _grabber.Height)
            {
                Performance = new RecordingPerformance(initializationMs, captureTotal, captureMax, encodeTotal, encodeMax, finalizationMs),
                PausedMs = pausedMs,
                PauseCount = pauseCount,
            };

            _log.LogInformation(
                "Recording finished: {Frames}/{ExpectedFrames} frame(s) over {Duration:0}ms; " +
                "dropped {DroppedFrames} ({DropRate:P1}), effective {EffectiveFps:0.0}fps; " +
                "paused {PauseCount} time(s) for {PausedMs:0}ms",
                _result.EmittedFrames,
                _result.ExpectedFrames,
                _result.DurationMs,
                _result.DroppedFrames,
                _result.DropRate,
                _result.EffectiveFps,
                _result.PauseCount,
                _result.PausedMs);
        }
        catch (Exception ex)
        {
            _failure = ex;
            _log.LogError(ex, "Recording loop failed");
            TryFinalizeAfterFailure();
        }
        finally
        {
            // Dispose the encoder on THIS capture thread (same apartment it was created and
            // used in). Disposing MF COM objects from another thread would repeat the
            // cross-apartment fault. After this the reference is cleared so Dispose()/Stop()
            // on the caller thread never touch apartment-bound COM.
            try
            {
                _encoder?.Dispose();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Encoder dispose on the capture thread failed");
            }
            finally
            {
                _encoder = null;
                lock (_pauseGate)
                {
                    long endedAt = _time.GetTimestamp();
                    _pauses.End(RawElapsedMs(endedAt));
                    Interlocked.CompareExchange(ref _recordingEnded, endedAt, 0);
                    // Release anyone waiting for the loop to park; it never will again.
                    _captureParked = false;
                    Monitor.PulseAll(_pauseGate);
                }

                _grabber.Dispose();
            }
        }
    }

    /// <summary>
    /// Parks the loop while a pause is active. Returns <see langword="true"/> when the caller
    /// must not capture this iteration.
    /// </summary>
    private bool ParkWhilePaused(bool firstFrameWritten)
    {
        lock (_pauseGate)
        {
            bool park = firstFrameWritten && _pauses.IsPaused;
            if (_captureParked != park)
            {
                _captureParked = park;
                Monitor.PulseAll(_pauseGate);
            }

            if (park && !_stopRequested)
            {
                // No frame is grabbed or encoded while paused; the encoder idles with its
                // pending samples intact until Resume or Stop pulses the gate.
                Monitor.Wait(_pauseGate, PausedWaitMs);
            }

            return park;
        }
    }

    private double ActiveElapsedMs()
    {
        lock (_pauseGate)
        {
            return _pauses.ActiveElapsedMs(RawElapsedMs(_time.GetTimestamp()));
        }
    }

    /// <summary>Capture-session time since the encoder became ready; zero before that.</summary>
    private double RawElapsedMs(long timestamp)
    {
        long started = Interlocked.Read(ref _recordingStarted);
        return started == 0 ? 0 : _time.GetElapsedTime(started, timestamp).TotalMilliseconds;
    }

    private void RequestStopAndWake()
    {
        _stopRequested = true;
        lock (_pauseGate)
        {
            Monitor.PulseAll(_pauseGate);
        }
    }

    private void TryFinalizeAfterFailure()
    {
        try
        {
            _encoder?.Complete();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Encoder finalisation after failure also failed");
        }
    }

    public void Dispose()
    {
        // The encoder is created, used, completed AND disposed entirely on the capture
        // thread (see CaptureLoop's finally), so Dispose only needs to make sure that
        // thread has finished; touching the MF COM objects from here would cross apartments.
        if (IsRecording)
        {
            RequestStopAndWake();
            _thread?.Join(2000);
        }
    }
}
