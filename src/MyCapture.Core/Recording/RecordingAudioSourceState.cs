namespace MyCapture.Core.Recording;

public enum AudioSourceState
{
    Off,
    Starting,
    Capturing,
    Unavailable,
    Paused,
}

public readonly record struct RecordingAudioSourceStatus(bool Enabled, AudioSourceState State, long Generation);

/// <summary>
/// Session facts are separate from the saved preference. Device failures never clear Enabled.
/// The owner serializes commands; asynchronous results carry the generation they belong to.
/// </summary>
public sealed class RecordingAudioSourceStateMachine
{
    private bool _paused;
    private bool _ended;
    public RecordingAudioSourceStateMachine(bool enabled) => Status =
        new(enabled, enabled ? AudioSourceState.Starting : AudioSourceState.Off, 1);

    public RecordingAudioSourceStatus Status { get; private set; }

    public bool SetEnabled(bool enabled)
    {
        if (_ended || Status.Enabled == enabled) return false;
        Status = new(enabled, NextState(enabled), checked(Status.Generation + 1));
        return true;
    }

    public bool SetPaused(bool paused)
    {
        if (_ended || _paused == paused) return false;
        _paused = paused;
        Status = new(Status.Enabled, NextState(Status.Enabled), checked(Status.Generation + 1));
        return true;
    }

    public bool TrySetCapturing(long generation) => TrySetDeviceState(generation, AudioSourceState.Capturing);
    public bool TrySetUnavailable(long generation) => TrySetDeviceState(generation, AudioSourceState.Unavailable);

    /// <summary>Invalidates every callback from the prior open without replacing this controller.</summary>
    public bool BeginRestart()
    {
        if (_ended || _paused || !Status.Enabled) return false;
        Status = new(true, AudioSourceState.Starting, checked(Status.Generation + 1));
        return true;
    }

    public void End()
    {
        if (_ended) return;
        _ended = true;
        Status = new(Status.Enabled, AudioSourceState.Off, checked(Status.Generation + 1));
    }

    private AudioSourceState NextState(bool enabled) => !enabled ? AudioSourceState.Off
        : _paused ? AudioSourceState.Paused : AudioSourceState.Starting;

    private bool TrySetDeviceState(long generation, AudioSourceState state)
    {
        if (_ended || _paused || !Status.Enabled || generation != Status.Generation) return false;
        Status = Status with { State = state };
        return true;
    }
}

/// <summary>
/// Rejects an old device generation and clips late data against already consumed output,
/// including silence. A missing interval is left missing; it is never filled with old PCM.
/// </summary>
public sealed class RecordingAudioPacketGate
{
    public long Generation { get; private set; }
    public long ConsumedThroughSample { get; private set; }

    public void BeginGeneration(long generation)
    {
        if (generation <= Generation) throw new ArgumentOutOfRangeException(nameof(generation));
        Generation = generation;
    }

    public void ConsumeThrough(long sample)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sample);
        ConsumedThroughSample = Math.Max(ConsumedThroughSample, sample);
    }

    public bool TryAccept(long generation, RecordingAudioBlock block, out RecordingAudioBlock accepted)
    {
        // Acceptance does not commit output. The consumer calls ConsumeThrough only after
        // it has written this PCM or silence, so retrying a blocked writer does not lose data.
        accepted = default;
        if (generation != Generation || Generation == 0 || block.EndSample <= ConsumedThroughSample)
            return false;
        int skip = checked((int)Math.Max(0, ConsumedThroughSample - block.StartSample));
        accepted = new(block.StartSample + skip, block.Samples[(skip * RecordingAudioFormat.Channels)..]);
        return accepted.FrameCount > 0;
    }
}
