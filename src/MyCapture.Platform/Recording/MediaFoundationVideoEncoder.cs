using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace MyCapture.Platform.Recording;

/// <summary>
/// Encodes BGRA32 frames to an H.264 MP4 using the Media Foundation Sink Writer.
/// </summary>
/// <remarks>
/// <para>
/// Media Foundation ships with Windows, so this works with no internet access and
/// adds no NuGet or native package to the installer — it is pure P/Invoke, exactly
/// like the rest of <c>MyCapture.Platform</c>. The OS chooses a hardware H.264 MFT
/// when the GPU exposes one and falls back to the software encoder otherwise, which
/// is what lets a weak PC still produce a standard, universally playable MP4.
/// </para>
/// <para>
/// The input media type is uncompressed RGB32; the Sink Writer inserts the colour
/// conversion and the H.264 encoder MFT between that and the MP4 sink. Frames arrive
/// top-down BGRA, so a negative stride is supplied to <c>MFCopyImage</c> equivalent
/// (handled by copying row-by-row) to keep the picture upright.
/// </para>
/// </remarks>
public sealed class MediaFoundationVideoEncoder : IVideoEncoder
{
    private const uint MF_VERSION = 0x0002_0070; // MF_SDK_VERSION << 16 | MF_API_VERSION
    private const uint MFSTARTUP_LITE = 1;

    private static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00AA00389B71");
    private static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00AA00389B71");
    private static readonly Guid MFVideoFormat_RGB32 = new("00000016-0000-0010-8000-00AA00389B71");

    private static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    private static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    private static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    private static readonly Guid MF_MT_ALL_SAMPLES_INDEPENDENT = new("c9173739-5e56-461c-b713-46fb995cb95f");
    private static readonly Guid MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS = new("a634a91c-822b-41b9-a494-4de4643612b0");

    private const uint MFVideoInterlace_Progressive = 2;

    private readonly ILogger _log;
    private readonly VideoEncoderOptions _options;
    private readonly bool _startedMf;

    private IMFSinkWriter? _writer;
    private int _streamIndex;
    private long _sampleDurationTicks; // 100ns units
    private bool _finalized;
    private bool _disposed;

    public MediaFoundationVideoEncoder(VideoEncoderOptions options, ILogger log)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log ?? throw new ArgumentNullException(nameof(log));

        Check(MFStartup(MF_VERSION, MFSTARTUP_LITE), nameof(MFStartup));
        _startedMf = true;

        try
        {
            Initialize();
        }
        catch
        {
            SafeShutdown();
            throw;
        }
    }

    public int Width => _options.Width;

    public int Height => _options.Height;

    private void Initialize()
    {
        int fps = Math.Max(1, _options.Fps);
        _sampleDurationTicks = 10_000_000L / fps;

        // Attributes: enable hardware transforms so the OS uses a GPU H.264 MFT when present.
        Check(MFCreateAttributes(out IMFAttributes attributes, 1), nameof(MFCreateAttributes));
        try
        {
            Guid hwKey = MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS;
            attributes.SetUINT32(ref hwKey, 1);

            Check(
                MFCreateSinkWriterFromURL(_options.OutputPath, IntPtr.Zero, attributes, out _writer),
                nameof(MFCreateSinkWriterFromURL));
        }
        finally
        {
            Marshal.ReleaseComObject(attributes);
        }

        // Output type: H.264.
        Check(MFCreateMediaType(out IMFMediaType outType), nameof(MFCreateMediaType));
        try
        {
            SetGuidAttr(outType, MF_MT_MAJOR_TYPE, MFMediaType_Video);
            SetGuidAttr(outType, MF_MT_SUBTYPE, MFVideoFormat_H264);
            SetU32(outType, MF_MT_AVG_BITRATE, (uint)_options.BitrateBitsPerSecond);
            SetU32(outType, MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
            SetFrameSize(outType, (uint)_options.Width, (uint)_options.Height);
            SetRatio(outType, MF_MT_FRAME_RATE, (uint)fps, 1);
            SetRatio(outType, MF_MT_PIXEL_ASPECT_RATIO, 1, 1);

            Check(_writer!.AddStream(outType, out _streamIndex), nameof(IMFSinkWriter.AddStream));
        }
        finally
        {
            Marshal.ReleaseComObject(outType);
        }

        // Input type: uncompressed RGB32 (BGRA byte order in memory).
        Check(MFCreateMediaType(out IMFMediaType inType), nameof(MFCreateMediaType));
        try
        {
            SetGuidAttr(inType, MF_MT_MAJOR_TYPE, MFMediaType_Video);
            SetGuidAttr(inType, MF_MT_SUBTYPE, MFVideoFormat_RGB32);
            SetU32(inType, MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
            SetU32(inType, MF_MT_ALL_SAMPLES_INDEPENDENT, 1);
            SetFrameSize(inType, (uint)_options.Width, (uint)_options.Height);
            SetRatio(inType, MF_MT_FRAME_RATE, (uint)fps, 1);
            SetRatio(inType, MF_MT_PIXEL_ASPECT_RATIO, 1, 1);

            Check(_writer!.SetInputMediaType(_streamIndex, inType, IntPtr.Zero), nameof(IMFSinkWriter.SetInputMediaType));
        }
        finally
        {
            Marshal.ReleaseComObject(inType);
        }

        Check(_writer!.BeginWriting(), nameof(IMFSinkWriter.BeginWriting));
    }

    public void WriteFrame(in EncoderFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_writer is null || _finalized)
        {
            return;
        }

        int rowBytes = _options.Width * 4;
        int bufferBytes = rowBytes * _options.Height;

        Check(MFCreateMemoryBuffer((uint)bufferBytes, out IMFMediaBuffer buffer), nameof(MFCreateMemoryBuffer));
        try
        {
            buffer.Lock(out IntPtr dest, out _, out _);
            try
            {
                // Copy row by row so an incoming stride that differs from the packed
                // width is handled, and so a top-down BGRA frame stays upright.
                for (int y = 0; y < _options.Height; y++)
                {
                    int srcOffset = y * frame.Stride;
                    IntPtr rowDest = dest + (y * rowBytes);
                    Marshal.Copy(frame.Pixels, srcOffset, rowDest, rowBytes);
                }
            }
            finally
            {
                buffer.Unlock();
            }

            buffer.SetCurrentLength((uint)bufferBytes);

            Check(MFCreateSample(out IMFSample sample), nameof(MFCreateSample));
            try
            {
                sample.AddBuffer(buffer);

                long timeTicks = (long)(frame.TimestampMs * 10_000.0);
                sample.SetSampleTime(timeTicks);
                sample.SetSampleDuration(_sampleDurationTicks);

                Check(_writer.WriteSample(_streamIndex, sample), nameof(IMFSinkWriter.WriteSample));
            }
            finally
            {
                Marshal.ReleaseComObject(sample);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(buffer);
        }
    }

    public void Complete()
    {
        if (_finalized || _writer is null)
        {
            return;
        }

        _finalized = true;
        Check(_writer.FinalizeWriting(), nameof(IMFSinkWriter.FinalizeWriting));
    }

    private static void SetGuidAttr(IMFMediaType type, Guid key, Guid value)
    {
        Guid k = key;
        Guid v = value;
        type.SetGUID(ref k, ref v);
    }

    private static void SetU32(IMFMediaType type, Guid key, uint value)
    {
        Guid k = key;
        type.SetUINT32(ref k, value);
    }

    private static void SetFrameSize(IMFMediaType type, uint width, uint height)
    {
        Guid k = MF_MT_FRAME_SIZE;
        ulong packed = ((ulong)width << 32) | height;
        type.SetUINT64(ref k, packed);
    }

    private static void SetRatio(IMFMediaType type, Guid key, uint numerator, uint denominator)
    {
        Guid k = key;
        ulong packed = ((ulong)numerator << 32) | denominator;
        type.SetUINT64(ref k, packed);
    }

    private void SafeShutdown()
    {
        if (_writer is not null)
        {
            Marshal.ReleaseComObject(_writer);
            _writer = null;
        }

        if (_startedMf)
        {
            _ = MFShutdown();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            // A recording that is disposed without an explicit Complete (e.g. on a crash
            // path) still gets a best-effort finalise so the MP4 is playable.
            Complete();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Sink writer finalise during dispose failed");
        }

        SafeShutdown();
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0)
        {
            throw new InvalidOperationException(
                $"Media Foundation call '{what}' failed with HRESULT 0x{hr:X8}.");
        }
    }

    // ----- Media Foundation flat exports -----

    [DllImport("mfplat.dll")]
    private static extern int MFStartup(uint version, uint flags);

    [DllImport("mfplat.dll")]
    private static extern int MFShutdown();

    [DllImport("mfplat.dll")]
    private static extern int MFCreateAttributes(out IMFAttributes attributes, uint initialSize);

    [DllImport("mfplat.dll")]
    private static extern int MFCreateMediaType(out IMFMediaType mediaType);

    [DllImport("mfplat.dll")]
    private static extern int MFCreateMemoryBuffer(uint maxLength, out IMFMediaBuffer buffer);

    [DllImport("mfplat.dll")]
    private static extern int MFCreateSample(out IMFSample sample);

    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
    private static extern int MFCreateSinkWriterFromURL(
        string outputUrl,
        IntPtr byteStream,
        IMFAttributes attributes,
        out IMFSinkWriter sinkWriter);
}

// ----- Minimal COM interface surface used above -----

[ComImport]
[Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFAttributes
{
    [PreserveSig] int GetItem(ref Guid key, IntPtr value);
    [PreserveSig] int GetItemType(ref Guid key, out int type);
    [PreserveSig] int CompareItem(ref Guid key, IntPtr value, out bool result);
    [PreserveSig] int Compare(IMFAttributes theirs, int matchType, out bool result);
    [PreserveSig] int GetUINT32(ref Guid key, out uint value);
    [PreserveSig] int GetUINT64(ref Guid key, out ulong value);
    [PreserveSig] int GetDouble(ref Guid key, out double value);
    [PreserveSig] int GetGUID(ref Guid key, out Guid value);
    [PreserveSig] int GetStringLength(ref Guid key, out uint length);
    [PreserveSig] int GetString(ref Guid key, IntPtr value, uint size, IntPtr length);
    [PreserveSig] int GetAllocatedString(ref Guid key, out IntPtr value, out uint length);
    [PreserveSig] int GetBlobSize(ref Guid key, out uint size);
    [PreserveSig] int GetBlob(ref Guid key, IntPtr buf, uint size, IntPtr blobSize);
    [PreserveSig] int GetAllocatedBlob(ref Guid key, out IntPtr buf, out uint size);
    [PreserveSig] int GetUnknown(ref Guid key, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int SetItem(ref Guid key, IntPtr value);
    [PreserveSig] int DeleteItem(ref Guid key);
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32(ref Guid key, uint value);
    [PreserveSig] int SetUINT64(ref Guid key, ulong value);
    [PreserveSig] int SetDouble(ref Guid key, double value);
    [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
    [PreserveSig] int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    [PreserveSig] int SetBlob(ref Guid key, IntPtr buf, uint size);
    [PreserveSig] int SetUnknown(ref Guid key, IntPtr unknown);
    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetItemByIndex(uint index, out Guid key, IntPtr value);
    [PreserveSig] int CopyAllItems(IMFAttributes dest);
}

[ComImport]
[Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaType : IMFAttributes
{
    // Additional IMFMediaType methods are not needed; attribute setters suffice.
}

[ComImport]
[Guid("045FA593-8799-42b8-BC8D-8968C6453507")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaBuffer
{
    [PreserveSig] int Lock(out IntPtr buffer, out uint maxLength, out uint currentLength);
    [PreserveSig] int Unlock();
    [PreserveSig] int GetCurrentLength(out uint length);
    [PreserveSig] int SetCurrentLength(uint length);
    [PreserveSig] int GetMaxLength(out uint length);
}

[ComImport]
[Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSample : IMFAttributes
{
    [PreserveSig] int GetSampleFlags(out uint flags);
    [PreserveSig] int SetSampleFlags(uint flags);
    [PreserveSig] int GetSampleTime(out long time);
    [PreserveSig] int SetSampleTime(long time);
    [PreserveSig] int GetSampleDuration(out long duration);
    [PreserveSig] int SetSampleDuration(long duration);
    [PreserveSig] int GetBufferCount(out uint count);
    [PreserveSig] int GetBufferByIndex(uint index, out IMFMediaBuffer buffer);
    [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
    [PreserveSig] int AddBuffer(IMFMediaBuffer buffer);
    [PreserveSig] int RemoveBufferByIndex(uint index);
    [PreserveSig] int RemoveAllBuffers();
    [PreserveSig] int GetTotalLength(out uint length);
    [PreserveSig] int CopyToBuffer(IMFMediaBuffer buffer);
}

[ComImport]
[Guid("3137f1cd-fe5e-4805-a5d8-fb477448cb3d")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSinkWriter
{
    [PreserveSig] int AddStream(IMFMediaType targetMediaType, out int streamIndex);
    [PreserveSig] int SetInputMediaType(int streamIndex, IMFMediaType inputMediaType, IntPtr encodingParameters);
    [PreserveSig] int BeginWriting();
    [PreserveSig] int WriteSample(int streamIndex, IMFSample sample);
    [PreserveSig] int SendStreamTick(int streamIndex, long timestamp);
    [PreserveSig] int PlaceMarker(int streamIndex, IntPtr context);
    [PreserveSig] int NotifyEndOfSegment(int streamIndex);
    [PreserveSig] int Flush(int streamIndex);
    [PreserveSig] int FinalizeWriting();
    [PreserveSig] int GetServiceForStream(int streamIndex, ref Guid service, ref Guid riid, out IntPtr service2);
    [PreserveSig] int GetStatistics(int streamIndex, IntPtr stats);
}
