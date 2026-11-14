using System.IO;
using System.Runtime.InteropServices;
using MyCapture.Core.Recording;
using IMFSourceReader = MyCapture.Platform.Recording.MediaFoundationVideoFrameReader.IMFSourceReader;

namespace MyCapture.Platform.Recording;

/// <summary>The compressed or PCM format declared by the first audio stream in a source.</summary>
public sealed record AudioSourceInfo(int StreamIndex, Guid Subtype, int SampleRate, int Channels, int BitsPerSample);

/// <summary>Decoded 48 kHz stereo PCM16, retaining the source presentation time in 100 ns units.</summary>
public sealed record DecodedAudioBlock(short[] Samples, long Timestamp100ns)
{
    public int FrameCount => Samples.Length / RecordingAudioFormat.Channels;
    public long Duration100ns => RecordingAudioFormat.SampleTime100ns(FrameCount);
    public long EndTimestamp100ns => checked(Timestamp100ns + Duration100ns);
}

/// <summary>
/// Sequentially decodes only the first audio stream. This reader opens no capture or playback
/// device; it owns the file decoder on the thread that constructed it until Dispose.
/// </summary>
public sealed class MediaFoundationAudioReader : IDisposable
{
    private const uint AllStreams = 0xFFFF_FFFE;
    private const uint EndOfStream = 0x2, Error = 0x1, CurrentMediaTypeChanged = 0x20;
    private const int InvalidStreamNumber = unchecked((int)0xC00D36B3);
    private const int MaximumDecodedBytes = 48_000 * 2 * 2 * 10;
    private static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid AudioType = new("73647561-0000-0010-8000-00AA00389B71");
    private static readonly Guid Pcm = new("00000001-0000-0010-8000-00AA00389B71");
    private static readonly Guid Rate = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    private static readonly Guid Channels = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    private static readonly Guid Bits = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
    private static readonly Guid BlockAlignment = new("322de230-9eeb-43bd-ab7a-ff412251541d");
    private static readonly Guid AverageBytes = new("1aab75c8-cfef-451c-ab95-ac034b8e1731");

    private readonly int _ownerThread = Environment.CurrentManagedThreadId;
    private MediaFoundationLifetime.Lease? _platform;
    private IMFSourceReader? _reader;
    private bool _end, _disposed;
    private long? _lastTimestamp;
    private static long s_openedReaders;
    private static int s_activeReaders;
    private bool _counted;

    internal static long OpenedReaders => Interlocked.Read(ref s_openedReaders);
    internal static int ActiveReaders => Volatile.Read(ref s_activeReaders);

    public MediaFoundationAudioReader(string sourcePath)
    {
        string path = ExistingPath(sourcePath);
        _platform = MediaFoundationPlatform.Acquire();
        _counted = true;
        Interlocked.Increment(ref s_openedReaders);
        Interlocked.Increment(ref s_activeReaders);
        try
        {
            Check(MFCreateSourceReaderFromURL(path, null, out _reader), "MFCreateSourceReaderFromURL(audio)");
            Audio = ReadSourceInfo(_reader);
            Check(_reader.SetStreamSelection(AllStreams, false), "SetStreamSelection(all)");
            if (Audio is null) { _end = true; return; }
            Check(_reader.SetStreamSelection((uint)Audio.StreamIndex, true), "SetStreamSelection(first audio)");
            Check(MFCreateMediaType(out IMFMediaType requested), "MFCreateMediaType(audio)");
            try
            {
                Set(requested, MajorType, AudioType);
                Set(requested, Subtype, Pcm);
                Set(requested, Rate, RecordingAudioFormat.SampleRate);
                Set(requested, Channels, RecordingAudioFormat.Channels);
                Set(requested, Bits, 16);
                Set(requested, BlockAlignment, 4);
                Set(requested, AverageBytes, 48_000 * 4);
                Check(_reader.SetCurrentMediaType((uint)Audio.StreamIndex, IntPtr.Zero, requested), "SetCurrentMediaType(48 kHz stereo PCM16)");
            }
            finally { Marshal.ReleaseComObject(requested); }
            VerifyOutputFormat();
        }
        catch { Dispose(); throw; }
    }

    public AudioSourceInfo? Audio { get; }

    /// <summary>Seeks this file decoder without creating a playback or capture object.</summary>
    public void Seek(double milliseconds)
    {
        CheckOwner();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!double.IsFinite(milliseconds) || milliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(milliseconds));
        if (Audio is null) return;
        Guid format = Guid.Empty;
        var position = new MediaFoundationVideoFrameReader.PropVariant
        {
            VariantType = 20,
            Int64Value = checked((long)Math.Round(milliseconds * 10_000)),
        };
        Check(_reader!.SetCurrentPosition(ref format, ref position), "SetCurrentPosition(audio)");
        _end = false;
        _lastTimestamp = null;
    }

    /// <summary>Reads the next block, or null at EOF (including a source with no audio).</summary>
    public DecodedAudioBlock? ReadNext(CancellationToken cancellationToken = default)
    {
        CheckOwner();
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (!_end)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IMFSample? sample = null;
            try
            {
                Check(_reader!.ReadSample((uint)Audio!.StreamIndex, 0, out uint actualStream,
                    out uint flags, out long timestamp, out sample), "ReadSample(audio)");
                if ((flags & Error) != 0) throw new InvalidDataException("The audio decoder reported an error.");
                if ((flags & CurrentMediaTypeChanged) != 0) VerifyOutputFormat();
                _end = (flags & EndOfStream) != 0;
                if (sample is null) continue;
                if (actualStream != Audio.StreamIndex) throw new InvalidDataException("The decoder returned a different audio stream.");
                if (_lastTimestamp is long previous && timestamp < previous)
                    throw new InvalidDataException("Audio presentation timestamps moved backwards.");
                Check(sample.ConvertToContiguousBuffer(out IMFMediaBuffer buffer), "ConvertToContiguousBuffer(audio)");
                try
                {
                    Check(buffer.Lock(out IntPtr data, out _, out uint length), "Lock(audio)");
                    try
                    {
                        if (length % 4 != 0 || length > MaximumDecodedBytes)
                            throw new InvalidDataException("The decoder returned an invalid stereo PCM block.");
                        if (length == 0) continue;
                        short[] samples = new short[checked((int)length / 2)];
                        Marshal.Copy(data, samples, 0, samples.Length);
                        _lastTimestamp = timestamp;
                        return new DecodedAudioBlock(samples, timestamp);
                    }
                    finally { Check(buffer.Unlock(), "Unlock(audio)"); }
                }
                finally { Marshal.ReleaseComObject(buffer); }
            }
            finally { if (sample is not null) Marshal.ReleaseComObject(sample); }
        }
        return null;
    }

    /// <summary>Reads the first native audio format without opening a decoder.</summary>
    public static AudioSourceInfo? ReadSourceInfo(string sourcePath)
    {
        string path = ExistingPath(sourcePath);
        using MediaFoundationLifetime.Lease platform = MediaFoundationPlatform.Acquire();
        IMFSourceReader? reader = null;
        try
        {
            Check(MFCreateSourceReaderFromURL(path, null, out reader), "MFCreateSourceReaderFromURL(audio metadata)");
            return ReadSourceInfo(reader);
        }
        finally { if (reader is not null) Marshal.ReleaseComObject(reader); }
    }

    internal static AudioSourceInfo? ReadSourceInfo(IMFSourceReader reader)
    {
        for (uint index = 0; ; index++)
        {
            IMFMediaType? type = null;
            try
            {
                int result = reader.GetNativeMediaType(index, 0, out type);
                if (result == InvalidStreamNumber) return null;
                Check(result, "GetNativeMediaType(audio metadata)");
                if (GetGuid(type, MajorType) == AudioType) return Describe(type, index);
            }
            finally { if (type is not null) Marshal.ReleaseComObject(type); }
        }
    }

    internal static (int TotalStreams, int AudioStreams) CountStreamsForTest(string path) => GetStreamCounts(path);

    /// <summary>Counts native tracks without opening an audio decoder or a device.</summary>
    public static (int TotalStreams, int AudioStreams) GetStreamCounts(string path)
    {
        using MediaFoundationLifetime.Lease platform = MediaFoundationPlatform.Acquire();
        IMFSourceReader? reader = null;
        try
        {
            Check(MFCreateSourceReaderFromURL(ExistingPath(path), null, out reader), "MFCreateSourceReaderFromURL(stream count)");
            int audio = 0;
            for (uint index = 0; ; index++)
            {
                IMFMediaType? type = null;
                try
                {
                    int result = reader.GetNativeMediaType(index, 0, out type);
                    if (result == InvalidStreamNumber) return (checked((int)index), audio);
                    Check(result, "GetNativeMediaType(stream count)");
                    if (GetGuid(type, MajorType) == AudioType) audio++;
                }
                finally { if (type is not null) Marshal.ReleaseComObject(type); }
            }
        }
        finally { if (reader is not null) Marshal.ReleaseComObject(reader); }
    }

    private void VerifyOutputFormat()
    {
        Check(_reader!.GetCurrentMediaType((uint)Audio!.StreamIndex, out IMFMediaType type), "GetCurrentMediaType(audio)");
        try
        {
            if (GetGuid(type, MajorType) != AudioType || GetGuid(type, Subtype) != Pcm
                || Get(type, Rate) != 48_000 || Get(type, Channels) != 2 || Get(type, Bits) != 16
                || Get(type, BlockAlignment) != 4)
                throw new InvalidDataException("The audio decoder did not produce 48 kHz stereo PCM16.");
        }
        finally { Marshal.ReleaseComObject(type); }
    }

    private static AudioSourceInfo Describe(IMFMediaType type, uint index) =>
        new(checked((int)index), GetGuid(type, Subtype), Get(type, Rate), Get(type, Channels), Get(type, Bits));

    private static int Get(IMFMediaType type, Guid key) => type.GetUINT32(ref key, out uint value) >= 0 ? checked((int)value) : 0;
    private static Guid GetGuid(IMFMediaType type, Guid key)
    {
        Check(type.GetGUID(ref key, out Guid value), "GetGUID(audio type)");
        return value;
    }
    private static void Set(IMFMediaType type, Guid key, Guid value) => Check(type.SetGUID(ref key, ref value), "SetGUID(audio type)");
    private static void Set(IMFMediaType type, Guid key, int value) => Check(type.SetUINT32(ref key, checked((uint)value)), "SetUINT32(audio type)");
    private static void Check(int result, string operation)
    {
        if (result < 0) throw new InvalidOperationException($"Media Foundation call '{operation}' failed with HRESULT 0x{result:X8}.");
    }
    private static string ExistingPath(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        string path = Path.GetFullPath(sourcePath);
        // The caller explicitly selected this local media file, as with the video reader.
        // codeql[cs/path-injection]
        if (!File.Exists(path)) throw new FileNotFoundException("The source audio is unavailable.", path);
        return path;
    }
    private void CheckOwner()
    {
        if (Environment.CurrentManagedThreadId != _ownerThread)
            throw new InvalidOperationException("The audio reader must be used on its owning thread.");
    }
    public void Dispose()
    {
        if (_disposed) return;
        CheckOwner();
        _disposed = true;
        if (_reader is not null) { Marshal.ReleaseComObject(_reader); _reader = null; }
        _platform?.Dispose();
        _platform = null;
        if (_counted)
        {
            _counted = false;
            Interlocked.Decrement(ref s_activeReaders);
        }
    }

    [DllImport("mfplat.dll")]
    private static extern int MFCreateMediaType(out IMFMediaType mediaType);
    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
    private static extern int MFCreateSourceReaderFromURL(string url, IMFAttributes? attributes, out IMFSourceReader sourceReader);
}
