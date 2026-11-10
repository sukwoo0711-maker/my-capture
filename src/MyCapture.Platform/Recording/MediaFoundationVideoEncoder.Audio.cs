using System.Runtime.InteropServices;
using MyCapture.Core.Recording;

namespace MyCapture.Platform.Recording;

internal enum AudioEncoderSetupStage { AudioStream, BeginWriting }

public sealed partial class MediaFoundationVideoEncoder
{
    private static readonly Guid AudioMajor = new("73647561-0000-0010-8000-00aa00389b71");
    private static readonly Guid AudioAac = new("00001610-0000-0010-8000-00aa00389b71");
    private static readonly Guid AudioPcm = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid AudioChannels = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    private static readonly Guid AudioRate = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    private static readonly Guid AudioBits = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
    private static readonly Guid AudioAlign = new("322de230-9eeb-43bd-ab7a-ff412251541d");
    private static readonly Guid AudioBytesPerSecond = new("1aab75c8-cfef-451c-ab95-ac034b8e1731");
    private static readonly Guid AudioAacPayload = new("bfbabe79-7434-4d1c-94f0-72a3b9e17188");
    private static readonly Guid AudioAacProfile = new("7632f0e6-9538-4d61-acda-ea29c8c14456");
    private readonly int _audioOwnerThread;
    private readonly Action<AudioEncoderSetupStage>? _audioSetupForTest;
    private int _audioStreamIndex = -1;
    private long _audioWrittenThrough;

    public AudioEncoderState AudioState { get; private set; }
    public string? AudioUnavailableReason { get; private set; }

    private sealed class AudioSetupException(Exception inner) : Exception("Audio initialization failed.", inner);

    private void RequireAudioOwnerThread()
    {
        if (Environment.CurrentManagedThreadId != _audioOwnerThread)
            throw new InvalidOperationException("The audio/video encoder must be used and disposed on its creating thread.");
    }

    private void InitializeAudio()
    {
        IMFMediaType output = CreateAudioType(compressed: true);
        try { Check(_writer!.AddStream(output, out _audioStreamIndex), "IMFSinkWriter.AddStream(audio)"); }
        finally { Marshal.ReleaseComObject(output); }
        IMFMediaType input = CreateAudioType(compressed: false);
        try { Check(_writer!.SetInputMediaType(_audioStreamIndex, input, IntPtr.Zero), "IMFSinkWriter.SetInputMediaType(audio)"); }
        finally { Marshal.ReleaseComObject(input); }
    }

    private static IMFMediaType CreateAudioType(bool compressed)
    {
        Check(MFCreateMediaType(out IMFMediaType type), nameof(MFCreateMediaType));
        try
        {
            SetGuidAttr(type, MF_MT_MAJOR_TYPE, AudioMajor);
            SetGuidAttr(type, MF_MT_SUBTYPE, compressed ? AudioAac : AudioPcm);
            SetU32(type, AudioChannels, RecordingAudioFormat.Channels);
            SetU32(type, AudioRate, RecordingAudioFormat.SampleRate);
            SetU32(type, AudioBits, RecordingAudioFormat.BitsPerSample);
            SetU32(type, AudioAlign, compressed ? 1u : RecordingAudioFormat.BytesPerFrame);
            SetU32(type, AudioBytesPerSecond, (uint)(compressed ? AudioEncoderOptions.BitrateBitsPerSecond / 8
                : RecordingAudioFormat.SampleRate * RecordingAudioFormat.BytesPerFrame));
            if (compressed)
            {
                SetU32(type, AudioAacPayload, 0);
                SetU32(type, AudioAacProfile, 0x29);
            }
            else SetU32(type, MF_MT_ALL_SAMPLES_INDEPENDENT, 1);
            return type;
        }
        catch { Marshal.ReleaseComObject(type); throw; }
    }

    public void WriteAudio(in RecordingAudioBlock block)
    {
        RequireAudioOwnerThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (AudioState != AudioEncoderState.Available || _writer is null || _finalized)
            throw new InvalidOperationException("The encoder has no writable audio stream.");
        if (block.StartSample < _audioWrittenThrough)
            throw new ArgumentException("Audio samples must not overlap or move backwards.", nameof(block));
        if (block.FrameCount == 0) return;
        long timestamp = block.Timestamp100ns;
        long duration = block.Duration100ns;
        int bytes = checked(block.Samples.Length * sizeof(short));
        Check(MFCreateMemoryBuffer((uint)bytes, out IMFMediaBuffer buffer), nameof(MFCreateMemoryBuffer));
        try
        {
            Check(buffer.Lock(out IntPtr destination, out _, out _), nameof(IMFMediaBuffer.Lock));
            try
            {
                unsafe
                {
                    fixed (short* source = block.Samples.Span)
                        Buffer.MemoryCopy(source, (void*)destination, bytes, bytes);
                }
            }
            finally { Check(buffer.Unlock(), nameof(IMFMediaBuffer.Unlock)); }
            Check(buffer.SetCurrentLength((uint)bytes), nameof(IMFMediaBuffer.SetCurrentLength));
            Check(MFCreateSample(out IMFSample sample), nameof(MFCreateSample));
            try
            {
                Check(sample.AddBuffer(buffer), nameof(IMFSample.AddBuffer));
                Check(sample.SetSampleTime(timestamp), nameof(IMFSample.SetSampleTime));
                Check(sample.SetSampleDuration(duration), nameof(IMFSample.SetSampleDuration));
                Check(_writer.WriteSample(_audioStreamIndex, sample), "IMFSinkWriter.WriteSample(audio)");
                _audioWrittenThrough = block.EndSample;
            }
            finally { Marshal.ReleaseComObject(sample); }
        }
        finally { Marshal.ReleaseComObject(buffer); }
    }
}
