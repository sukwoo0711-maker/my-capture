using System.Diagnostics;
using System.Threading;
using Microsoft.Extensions.Logging;
using MyCapture.Core.Primitives;
using MyCapture.Core.Recording;

namespace MyCapture.Platform.Recording;

/// <summary>
/// Result of a finished recording.
/// </summary>
/// <param name="OutputPath">Path to the written MP4.</param>
/// <param name="DurationMs">Wall-clock duration captured.</param>
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
    int Height);

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
/// Foundation.
/// </para>
/// </remarks>
public sealed class RegionRecorder : IDisposable
{
    private readonly RegionFrameGrabber _grabber;
    private readonly Func<VideoEncoderOptions, IVideoEncoder> _encoderFactory;
    private readonly ILogger _log;

    private Thread? _thread;
    private volatile bool _stopRequested;
    private IVideoEncoder? _encoder;
    private RecordingResult? _result;
    private Exception? _failure;

    public RegionRecorder(
        RegionFrameGrabber grabber,
        Func<VideoEncoderOptions, IVideoEncoder> encoderFactory,
        ILogger log)
    {
        _grabber = grabber ?? throw new ArgumentNullException(nameof(grabber));
        _encoderFactory = encoderFactory ?? throw new ArgumentNullException(nameof(encoderFactory));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public bool IsRecording => _thread is { IsAlive: true };

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

        _stopRequested = false;
        _result = null;
        _failure = null;

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
            outputPath);
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

        _stopRequested = true;
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
        var stopwatch = Stopwatch.StartNew();
        try
        {
            // Create the encoder here, on this MTA capture thread, so the MF Sink Writer's
            // COM objects live in the same apartment that will write every frame.
            _encoder = _encoderFactory(options);

            // Always execute one capture iteration after encoder initialisation. H.264 MFT
            // startup can occasionally take longer than a very short recording; if Stop was
            // requested during that startup, finalising with zero samples fails with
            // MF_E_SINK_NO_SAMPLES_PROCESSED (0xC00D4A44). One timestamp-zero frame produces a
            // valid short clip without blocking the UI thread while the encoder warms up.
            while (true)
            {
                double elapsed = stopwatch.Elapsed.TotalMilliseconds;
                if (clock.TryClaimFrame(elapsed, out double timestampMs))
                {
                    byte[] pixels = _grabber.GrabInto();
                    _encoder!.WriteFrame(new EncoderFrame(
                        pixels,
                        _grabber.Width,
                        _grabber.Height,
                        _grabber.Stride,
                        timestampMs));
                }

                if (_stopRequested)
                {
                    break;
                }

                double sleep = clock.MillisecondsUntilNextFrame(stopwatch.Elapsed.TotalMilliseconds);
                if (sleep >= 1)
                {
                    // Cap the sleep so a stop request is honoured promptly even at low fps.
                    Thread.Sleep((int)Math.Min(sleep, 50));
                }
            }

            stopwatch.Stop();
            _encoder!.Complete();

            _result = new RecordingResult(
                options.OutputPath,
                stopwatch.Elapsed.TotalMilliseconds,
                options.Fps,
                clock.EmittedFrames,
                _grabber.Width,
                _grabber.Height);

            _log.LogInformation(
                "Recording finished: {Frames} frame(s) over {Duration:0}ms",
                clock.EmittedFrames,
                stopwatch.Elapsed.TotalMilliseconds);
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
            }
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
            _stopRequested = true;
            _thread?.Join(2000);
        }
    }
}
