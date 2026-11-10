namespace MyCapture.Core.Recording;

/// <summary>
/// A packet slice containing only active samples; indices are frames, not channels.
/// An active interval shorter than a native sample may have no input frames but a nonzero
/// cumulative output budget. That interval is silence, never a neighboring paused sample.
/// </summary>
public readonly record struct RecordingAudioSlice(
    int InputOffsetFrames, int InputFrameCount, long OutputStartSample, long OutputEndSample)
{
    public long OutputFrameCount => OutputEndSample - OutputStartSample;
}

/// <summary>
/// One QPC origin for both media. Pause commands and packet reads are serialized by the owner.
/// Historical queries do not advance the command clock, so a late packet can still be split
/// at the original pause boundaries. All intervals are half-open.
/// </summary>
public sealed class RecordingAudioTimeline
{
    private readonly long _epoch;
    private readonly long _frequency;
    private readonly List<(long Start, long End)> _pauses = [];
    private long _latestCommand100ns;
    private long _openPause100ns;
    private long _stop100ns = long.MaxValue;

    public RecordingAudioTimeline(long epochTimestamp, long timestampFrequency)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(epochTimestamp);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timestampFrequency);
        _epoch = epochTimestamp;
        _frequency = timestampFrequency;
    }

    public RecordingPauseState State { get; private set; } = RecordingPauseState.Recording;

    public IReadOnlyList<(long Start100ns, long End100ns)> PauseIntervals => _pauses.ToArray();

    public long Elapsed100ns(long timestamp) => timestamp <= _epoch ? 0
        : checked((long)(((Int128)timestamp - _epoch) * RecordingAudioFormat.TicksPerSecond / _frequency));

    /// <summary>WASAPI packet QPC timestamps are already in 100 ns units, unlike raw QPC.</summary>
    public long ElapsedFromPacketQpc100ns(long packetQpc100ns) => checked(packetQpc100ns
        - (long)((Int128)_epoch * RecordingAudioFormat.TicksPerSecond / _frequency));

    public bool TryPause(long timestamp)
    {
        if (State != RecordingPauseState.Recording) return false;
        _openPause100ns = ObserveCommand(timestamp);
        State = RecordingPauseState.Paused;
        return true;
    }

    public bool TryResume(long timestamp)
    {
        if (State != RecordingPauseState.Paused) return false;
        long now = ObserveCommand(timestamp);
        _pauses.Add((_openPause100ns, now));
        State = RecordingPauseState.Recording;
        return true;
    }

    public void End(long timestamp)
    {
        if (State == RecordingPauseState.Ended) return;
        long now = ObserveCommand(timestamp);
        if (State == RecordingPauseState.Paused) _pauses.Add((_openPause100ns, now));
        _stop100ns = now;
        State = RecordingPauseState.Ended;
    }

    public long Active100ns(long timestamp) => ActiveAtElapsed100ns(Elapsed100ns(timestamp));

    public long ActiveAtElapsed100ns(long elapsed100ns)
    {
        long end = Math.Clamp(elapsed100ns, 0, _stop100ns);
        long active = end;
        foreach ((long start, long stop) in _pauses)
        {
            if (start >= end) break;
            active -= Math.Min(end, stop) - start;
        }
        if (State == RecordingPauseState.Paused && end > _openPause100ns)
            active -= end - _openPause100ns;
        return active;
    }

    /// <summary>
    /// Splits native PCM at sample timestamps, discarding every sample in a paused span.
    /// Output boundaries use cumulative active time. A nominal resampler must honor the
    /// returned output length (which can differ from separately rounded slice lengths).
    /// Packet start is elapsed 100 ns from this session's origin, not arrival time.
    /// </summary>
    public IReadOnlyList<RecordingAudioSlice> SplitPacket(long packetStart100ns, int frameCount, int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frameCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        List<RecordingAudioSlice> result = [];
        long activeStart = 0;
        long removed = 0;
        foreach ((long start, long end) in _pauses)
        {
            AddSlice(activeStart, start, removed);
            removed += end - start;
            activeStart = end;
        }
        long activeEnd = State == RecordingPauseState.Paused ? _openPause100ns : _stop100ns;
        AddSlice(activeStart, activeEnd, removed);
        return result;

        void AddSlice(long start, long end, long removed100ns)
        {
            if (end <= start || frameCount == 0) return;
            Int128 rawStart = Int128.Max((Int128)packetStart100ns * sampleRate, (Int128)start * sampleRate);
            Int128 rawEnd = (Int128)packetStart100ns * sampleRate + (Int128)frameCount * RecordingAudioFormat.TicksPerSecond;
            if (end != long.MaxValue) rawEnd = Int128.Min(rawEnd, (Int128)end * sampleRate);
            if (rawEnd <= rawStart) return;
            int first = (int)Math.Clamp(RecordingAudioFormat.CeilingRatio(
                ((Int128)start - packetStart100ns) * sampleRate, RecordingAudioFormat.TicksPerSecond), 0, frameCount);
            int last = end == long.MaxValue ? frameCount : (int)Math.Clamp(RecordingAudioFormat.CeilingRatio(
                ((Int128)end - packetStart100ns) * sampleRate, RecordingAudioFormat.TicksPerSecond), 0, frameCount);
            Int128 denominator = (Int128)sampleRate * RecordingAudioFormat.TicksPerSecond;
            // At a resume boundary the first available native sample can be a fraction of a
            // frame late. Keep the output seam at the boundary; the resampler holds that first
            // active value rather than importing a sample from the paused span.
            long outputStart = RecordingAudioFormat.RoundRatio(
                (rawStart - (Int128)removed100ns * sampleRate) * RecordingAudioFormat.SampleRate,
                denominator);
            // The boundary, not the first excluded sample, determines the stored end.
            long outputEnd = RecordingAudioFormat.RoundRatio(
                (rawEnd - (Int128)removed100ns * sampleRate) * RecordingAudioFormat.SampleRate, denominator);
            if (last > first || outputEnd > outputStart)
                result.Add(new(first, last - first, outputStart, Math.Max(outputStart, outputEnd)));
        }
    }

    private long ObserveCommand(long timestamp)
    {
        _latestCommand100ns = Math.Max(_latestCommand100ns, Elapsed100ns(timestamp));
        return _latestCommand100ns;
    }
}
