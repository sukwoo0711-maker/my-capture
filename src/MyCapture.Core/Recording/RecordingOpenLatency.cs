using System.Diagnostics;

namespace MyCapture.Core.Recording;

/// <summary>Stages between the user's Stop request and the video editor being on screen.</summary>
public enum RecordingOpenStage
{
    /// <summary>Capture-thread join and MP4 finalisation, from Stop to the completed clip.</summary>
    Finalize,

    /// <summary>Decoding the clip's first and final samples before publication.</summary>
    Validate,

    /// <summary>Decoding, scaling and saving the gallery thumbnail.</summary>
    Thumbnail,

    /// <summary>Durable publication: sidecars, rename, edit document and queue index.</summary>
    Publish,

    /// <summary>Creating, showing and activating the editor window.</summary>
    EditorOpen,

    /// <summary>From the editor being shown until its first rendered frame.</summary>
    EditorFirstRender,
}

/// <summary>
/// Measures the stop-to-editor path for diagnostics. Each <see cref="Mark"/> charges the time
/// since the previous mark to one stage, so the stages always add up to <see cref="TotalMs"/>.
/// It holds only durations: the log line built from it never carries paths, titles or other
/// user content.
/// </summary>
public sealed class RecordingOpenLatency
{
    private readonly Func<long> _timestamp;
    private readonly double _millisecondsPerTick;
    private readonly long _started;
    private readonly double[] _stageMs = new double[Enum.GetValues<RecordingOpenStage>().Length];
    private long _lastMark;

    /// <summary>Starts measuring at <paramref name="startedTimestamp"/> on the <see cref="Stopwatch"/> clock.</summary>
    public RecordingOpenLatency(long startedTimestamp)
        : this(startedTimestamp, Stopwatch.GetTimestamp, Stopwatch.Frequency)
    {
    }

    /// <summary>Starts measuring on an injected monotonic clock (tests).</summary>
    public RecordingOpenLatency(long startedTimestamp, Func<long> timestamp, long ticksPerSecond)
    {
        ArgumentNullException.ThrowIfNull(timestamp);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ticksPerSecond);
        _timestamp = timestamp;
        _millisecondsPerTick = 1000.0 / ticksPerSecond;
        _started = startedTimestamp;
        _lastMark = startedTimestamp;
    }

    /// <summary>Elapsed time from the start to the latest mark.</summary>
    public double TotalMs => (_lastMark - _started) * _millisecondsPerTick;

    /// <summary>Accumulated time charged to <paramref name="stage"/>.</summary>
    public double this[RecordingOpenStage stage] => _stageMs[(int)stage];

    /// <summary>Charges the time since the previous mark (or the start) to <paramref name="stage"/>.</summary>
    public void Mark(RecordingOpenStage stage)
    {
        if ((uint)stage >= (uint)_stageMs.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(stage));
        }

        long now = _timestamp();
        // A clock that steps backwards must not produce negative stage durations.
        long elapsed = Math.Max(0, now - _lastMark);
        _stageMs[(int)stage] += elapsed * _millisecondsPerTick;
        _lastMark = Math.Max(_lastMark, now);
    }
}
