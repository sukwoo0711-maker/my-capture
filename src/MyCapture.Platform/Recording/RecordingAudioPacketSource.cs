using MyCapture.Core.Recording;

namespace MyCapture.Platform.Recording;

public enum RecordingAudioSourceKind { System, Microphone }

[Flags]
public enum RecordingAudioPacketFlags { None = 0, Silent = 1, Discontinuity = 2, TimestampError = 4 }

/// <summary>Native-rate, interleaved stereo floats. QPC is in WASAPI's 100 ns units.</summary>
public readonly record struct RecordingAudioPacket(ReadOnlyMemory<float> Stereo, int SampleRate,
    long DevicePosition, long Qpc100ns, RecordingAudioPacketFlags Flags = RecordingAudioPacketFlags.None);

public enum RecordingAudioSourceEvent { Opened, Closed }

/// <summary>
/// Run owns every native object on its calling MTA thread. Cancellation requests release; it
/// must never marshal native release to the UI or video thread. Callbacks borrow their PCM.
/// A source instance is used for one generation, and disposed on that same thread.
/// </summary>
public interface IRecordingAudioPacketSource : IDisposable
{
    void Run(Action<RecordingAudioPacket> packet, Action<RecordingAudioSourceEvent> state,
        CancellationToken cancellationToken);
}

public delegate IRecordingAudioPacketSource RecordingAudioSourceFactory(RecordingAudioSourceKind kind);

public sealed record RecordingAudioSourceDiagnostics(RecordingAudioSourceStatus Status,
    long OpenedObjects, long ActiveObjects, long Packets, long SilentPackets, long RejectedPackets,
    long OverflowFrames, long TimestampErrors, long Failures, double DriftPpm, string? LastFailure)
{
    public int PeakPendingCorrectedFrames { get; init; }
}

public sealed record RecordingAudioDiagnostics(AudioEncoderState EncoderState,
    RecordingAudioSourceDiagnostics System, RecordingAudioSourceDiagnostics Microphone,
    long OutputFrames, long SilentFrames, long FirstAudioTimestamp100ns, long LastAudioEnd100ns,
    long FirstVideoTimestamp100ns, long LastVideoTimestamp100ns, long VideoQueueDrops,
    long PeakQueueBytes, int PeakQueueAudioFrames, long MaximumQueuedLead100ns,
    double SourceJoinWaitMs, int SourceThreadsStillRunning, double StopMs, double FinalizeMs)
{
    public bool AllSilence => OutputFrames > 0 && SilentFrames == OutputFrames;
    public long EpochTimestamp { get; init; }
    public long TimestampFrequency { get; init; }
    public long ActiveSampleBudget { get; init; }
    public long StopElapsed100ns { get; init; }
    public long PaddingFrames { get; init; }
    public long TrimmedFrames { get; init; }
    public IReadOnlyList<RecordingAudioPauseInterval> PauseIntervals { get; init; } = [];
}

public readonly record struct RecordingAudioPauseInterval(long Start100ns, long End100ns);
