using MyCapture.Core.Recording;

namespace MyCapture.Platform.Recording;

/// <summary>A recording's optional, fixed 128 kbps AAC-LC stream.</summary>
public sealed record AudioEncoderOptions
{
    public const int BitrateBitsPerSecond = 128_000;
}

public enum AudioEncoderState
{
    Disabled,
    Available,
    Unavailable,
}

/// <summary>
/// A single-thread-owned video sink with optional 48 kHz stereo PCM16 input. Audio availability
/// is final when construction returns; a setup failure can leave a working video-only sink.
/// </summary>
public interface IAudioVideoEncoder : IVideoEncoder
{
    AudioEncoderState AudioState { get; }
    string? AudioUnavailableReason { get; }

    /// <summary>
    /// Consumes the block synchronously. Its memory can be reused on return. Requires Available;
    /// post-start codec or muxer errors propagate just like a video write failure.
    /// </summary>
    void WriteAudio(in RecordingAudioBlock block);
}
