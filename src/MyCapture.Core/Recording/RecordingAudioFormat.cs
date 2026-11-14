namespace MyCapture.Core.Recording;

/// <summary>The PCM contract between the mixer, editor and encoder.</summary>
public static class RecordingAudioFormat
{
    public const int SampleRate = 48_000;
    public const int Channels = 2;
    public const int BitsPerSample = 16;
    public const int BytesPerFrame = Channels * BitsPerSample / 8;
    public const long TicksPerSecond = 10_000_000;

    public static long SampleTime100ns(long sample)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sample);
        return checked((long)((Int128)sample * TicksPerSecond / SampleRate));
    }

    /// <summary>Rounds an absolute boundary once, not each packet or cut duration.</summary>
    public static long SampleAt100ns(long time100ns)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(time100ns);
        return RoundRatio((Int128)time100ns * SampleRate, TicksPerSecond);
    }

    internal static long RoundRatio(Int128 numerator, Int128 denominator) =>
        checked((long)((numerator + denominator / 2) / denominator));

    internal static long CeilingRatio(Int128 numerator, Int128 denominator)
    {
        Int128 quotient = numerator / denominator;
        if (numerator % denominator > 0) quotient++;
        return checked((long)quotient);
    }
}

/// <summary>
/// Interleaved signed PCM16 stereo at 48 kHz. The producer keeps the backing memory
/// unchanged until the consumer has finished the block. StartSample is on the stored timeline.
/// </summary>
public readonly record struct RecordingAudioBlock
{
    public RecordingAudioBlock(long startSample, ReadOnlyMemory<short> samples)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startSample);
        if (samples.Length % RecordingAudioFormat.Channels != 0)
            throw new ArgumentException("PCM must contain complete stereo frames.", nameof(samples));
        _ = checked(startSample + samples.Length / RecordingAudioFormat.Channels);
        StartSample = startSample;
        Samples = samples;
    }

    public long StartSample { get; }
    public ReadOnlyMemory<short> Samples { get; }
    public int FrameCount => Samples.Length / RecordingAudioFormat.Channels;
    public long EndSample => StartSample + FrameCount;
    public long Timestamp100ns => RecordingAudioFormat.SampleTime100ns(StartSample);
    public long Duration100ns => RecordingAudioFormat.SampleTime100ns(EndSample) - Timestamp100ns;
}
