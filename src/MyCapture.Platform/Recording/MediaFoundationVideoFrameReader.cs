using System.IO;
using System.Runtime.InteropServices;

namespace MyCapture.Platform.Recording;

/// <summary>A decoded, top-down BGR32 video frame with its source presentation timestamp.</summary>
public sealed record DecodedVideoFrame(
    byte[] Pixels,
    int Width,
    int Height,
    int Stride,
    double TimestampMs,
    double SampleDurationMs)
{
    /// <summary>The source-timeline position covered by the end of this decoded sample.</summary>
    public double EndTimestampMs => TimestampMs + SampleDurationMs;
}

/// <summary>
/// Selects the most recent decoded sample for a monotonic source-time request while retaining one
/// future sample. A source whose first PTS is positive holds that first frame for earlier requests
/// without consuming successive future frames.
/// </summary>
internal sealed class DecodedVideoFrameSelector
{
    private const double TimestampEpsilonMs = 0.01;
    private DecodedVideoFrame? _lastFrame;
    private DecodedVideoFrame? _lookahead;

    internal DecodedVideoFrame Select(
        double sourceTimeMs,
        Func<DecodedVideoFrame?> readNext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(readNext);
        DecodedVideoFrame? candidate = _lastFrame is not null
            && _lastFrame.TimestampMs <= sourceTimeMs + TimestampEpsilonMs
                ? _lastFrame
                : null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DecodedVideoFrame? next = _lookahead;
            _lookahead = null;
            next ??= readNext();
            if (next is null)
            {
                break;
            }

            if (next.TimestampMs <= sourceTimeMs + TimestampEpsilonMs)
            {
                candidate = next;
                _lastFrame = next;
                continue;
            }

            _lookahead = next;
            break;
        }

        // Some sources begin at a small positive timestamp. Return (but do not consume) that first
        // future frame so 0/33/66 ms requests all hold it until its actual presentation time.
        return candidate
            ?? _lookahead
            ?? throw new InvalidDataException("The source video did not yield a decodable frame.");
    }

    internal void Reset()
    {
        _lastFrame = null;
        _lookahead = null;
    }
}

/// <summary>
/// Synchronously decodes Windows-supported video files through Media Foundation's Source Reader.
/// Requests may move forward without seeking; a backwards request performs one key-frame seek and
/// then advances decoded samples to the requested presentation position.
/// </summary>
public sealed class MediaFoundationVideoFrameReader : IDisposable
{
    private const uint MfVersion = 0x0002_0070;
    private const uint MfStartupFull = 0;
    private const uint FirstVideoStream = 0xFFFF_FFFC;
    private const uint AllStreams = 0xFFFF_FFFE;
    private const uint MediaSource = 0xFFFF_FFFF;
    private const uint EndOfStreamFlag = 0x0000_0002;
    private const uint CurrentMediaTypeChangedFlag = 0x0000_0020;
    private const int MediaFoundationAttributeNotFound = unchecked((int)0xC00D36E6);
    private const int MediaFoundationNoSampleDuration = unchecked((int)0xC00D36C9);
    private const ushort VariantInt64 = 20;
    private const ushort VariantUInt64 = 21;
    private const double TimestampEpsilonMs = 0.01;

    private static readonly Guid MediaTypeVideo = new("73646976-0000-0010-8000-00AA00389B71");
    private static readonly Guid VideoFormatRgb32 = new("00000016-0000-0010-8000-00AA00389B71");
    private static readonly Guid MediaTypeMajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MediaTypeSubtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MediaTypeFrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid MediaTypeDefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    private static readonly Guid MediaTypeMinimumDisplayAperture = new("d7388766-18fe-48c6-a177-ee894867c8c4");
    private static readonly Guid MediaTypeGeometricAperture = new("66758743-7e5f-400d-980a-aa8596c85696");
    private static readonly Guid SourceReaderEnableVideoProcessing = new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");
    private static readonly Guid ReadWriteEnableHardwareTransforms = new("a634a91c-822b-41b9-a494-4de4643612b0");
    private static readonly Guid PresentationDuration = new("6c990d33-bb8e-477a-8598-0d5d96fcd88a");

    private IMFSourceReader? _reader;
    private readonly bool _startedMediaFoundation;
    private int _codedWidth;
    private int _codedHeight;
    private int _cropX;
    private int _cropY;
    private int _sourceStride;
    private bool _initialized;
    private bool _endOfStream;
    private bool _disposed;
    private double _lastRequestedMs = -1;
    private readonly DecodedVideoFrameSelector _frameSelector = new();
    private DecodedVideoFrame? _unresolvedDurationFrame;
    private DecodedVideoFrame? _rawLookaheadFrame;
    private double _lastResolvedSampleDurationMs;

    public MediaFoundationVideoFrameReader(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        string fullPath = Path.GetFullPath(sourcePath);
        // This desktop API intentionally opens the exact local file selected by the caller. The
        // gallery caller supplies a queue-contained path; diagnostic callers supply test media.
        // codeql[cs/path-injection]
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The source video is unavailable.", fullPath);
        }

        Check(MFStartup(MfVersion, MfStartupFull), nameof(MFStartup));
        _startedMediaFoundation = true;
        try
        {
            Initialize(fullPath);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public int Width { get; private set; }

    public int Height { get; private set; }

    public double DurationMs { get; private set; }

    /// <summary>
    /// Returns the frame presented at <paramref name="sourceTimeMs"/>. The reader retains at most
    /// one previous frame and one decoded look-ahead frame, so long exports stay memory bounded.
    /// </summary>
    public DecodedVideoFrame ReadFrameAt(double sourceTimeMs, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!double.IsFinite(sourceTimeMs) || sourceTimeMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceTimeMs));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!_initialized || sourceTimeMs + TimestampEpsilonMs < _lastRequestedMs)
        {
            Seek(sourceTimeMs);
        }

        _lastRequestedMs = sourceTimeMs;
        return _frameSelector.Select(
            sourceTimeMs,
            () => ReadNextFrame(cancellationToken),
            cancellationToken);
    }

    private void Initialize(string fullPath)
    {
        Check(MFCreateAttributes(out IMFAttributes attributes, 2), nameof(MFCreateAttributes));
        try
        {
            Guid processing = SourceReaderEnableVideoProcessing;
            Guid hardware = ReadWriteEnableHardwareTransforms;
            Check(attributes.SetUINT32(ref processing, 1), "IMFAttributes.SetUINT32(video processing)");
            Check(attributes.SetUINT32(ref hardware, 1), "IMFAttributes.SetUINT32(hardware transforms)");
            Check(
                MFCreateSourceReaderFromURL(fullPath, attributes, out _reader),
                nameof(MFCreateSourceReaderFromURL));
        }
        finally
        {
            Marshal.ReleaseComObject(attributes);
        }

        Check(_reader!.SetStreamSelection(AllStreams, false), "IMFSourceReader.SetStreamSelection(all)");
        Check(_reader.SetStreamSelection(FirstVideoStream, true), "IMFSourceReader.SetStreamSelection(video)");

        Check(MFCreateMediaType(out IMFMediaType requestedType), nameof(MFCreateMediaType));
        try
        {
            Guid majorKey = MediaTypeMajorType;
            Guid subtypeKey = MediaTypeSubtype;
            Guid majorValue = MediaTypeVideo;
            Guid subtypeValue = VideoFormatRgb32;
            Check(requestedType.SetGUID(ref majorKey, ref majorValue), "IMFMediaType.SetGUID(major)");
            Check(requestedType.SetGUID(ref subtypeKey, ref subtypeValue), "IMFMediaType.SetGUID(subtype)");
            Check(
                _reader.SetCurrentMediaType(FirstVideoStream, IntPtr.Zero, requestedType),
                "IMFSourceReader.SetCurrentMediaType");
        }
        finally
        {
            Marshal.ReleaseComObject(requestedType);
        }

        RefreshOutputFormat();
        DurationMs = ReadDurationMs();
    }

    private void RefreshOutputFormat()
    {
        Check(
            _reader!.GetCurrentMediaType(FirstVideoStream, out IMFMediaType currentType),
            "IMFSourceReader.GetCurrentMediaType");
        try
        {
            Guid frameSizeKey = MediaTypeFrameSize;
            Check(currentType.GetUINT64(ref frameSizeKey, out ulong packed), "IMFMediaType.GetUINT64(frame size)");
            _codedWidth = checked((int)(packed >> 32));
            _codedHeight = checked((int)(packed & 0xFFFF_FFFF));
            if (_codedWidth is < 1 or > 65_535 || _codedHeight is < 1 or > 65_535)
            {
                throw new InvalidDataException("The source video reported invalid dimensions.");
            }

            (_cropX, _cropY, Width, Height) = ReadDisplayArea(currentType, _codedWidth, _codedHeight);

            Guid strideKey = MediaTypeDefaultStride;
            int strideResult = currentType.GetUINT32(ref strideKey, out uint rawStride);
            if (strideResult < 0 && strideResult != MediaFoundationAttributeNotFound)
            {
                Check(strideResult, "IMFMediaType.GetUINT32(default stride)");
            }

            _sourceStride = strideResult >= 0 ? unchecked((int)rawStride) : checked(_codedWidth * 4);
            if (_sourceStride == 0)
            {
                _sourceStride = checked(_codedWidth * 4);
            }

            if (Math.Abs((long)_sourceStride) < checked((long)_codedWidth * 4))
            {
                throw new InvalidDataException("The decoded video stride is smaller than one pixel row.");
            }
        }
        finally
        {
            Marshal.ReleaseComObject(currentType);
        }
    }

    private static (int X, int Y, int Width, int Height) ReadDisplayArea(
        IMFMediaType mediaType,
        int codedWidth,
        int codedHeight)
    {
        if (!TryReadVideoArea(mediaType, MediaTypeMinimumDisplayAperture, out NativeVideoArea area)
            && !TryReadVideoArea(mediaType, MediaTypeGeometricAperture, out area))
        {
            return (0, 0, codedWidth, codedHeight);
        }

        int x = checked((int)Math.Floor(area.OffsetX.Value + (area.OffsetX.Fraction / 65_536.0)));
        int y = checked((int)Math.Floor(area.OffsetY.Value + (area.OffsetY.Fraction / 65_536.0)));
        if (x < 0
            || y < 0
            || area.Area.Width <= 0
            || area.Area.Height <= 0
            || x + area.Area.Width > codedWidth
            || y + area.Area.Height > codedHeight)
        {
            throw new InvalidDataException("The source video reported an invalid display aperture.");
        }

        return (x, y, area.Area.Width, area.Area.Height);
    }

    private static bool TryReadVideoArea(
        IMFMediaType mediaType,
        Guid attribute,
        out NativeVideoArea area)
    {
        int size = Marshal.SizeOf<NativeVideoArea>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Guid key = attribute;
            int result = mediaType.GetBlob(ref key, buffer, checked((uint)size), IntPtr.Zero);
            if (result < 0)
            {
                if (result != MediaFoundationAttributeNotFound)
                {
                    Check(result, "IMFMediaType.GetBlob(display aperture)");
                }

                area = default;
                return false;
            }

            area = Marshal.PtrToStructure<NativeVideoArea>(buffer);
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private double ReadDurationMs()
    {
        Guid durationKey = PresentationDuration;
        int result = _reader!.GetPresentationAttribute(MediaSource, ref durationKey, out PropVariant value);
        if (result == MediaFoundationAttributeNotFound)
        {
            return 0;
        }

        Check(result, "IMFSourceReader.GetPresentationAttribute(duration)");
        try
        {
            if (value.VariantType is not (VariantUInt64 or VariantInt64))
            {
                return 0;
            }

            long ticks = value.VariantType == VariantUInt64
                ? checked((long)value.UInt64Value)
                : value.Int64Value;
            return ticks > 0 ? ticks / 10_000.0 : 0;
        }
        finally
        {
            _ = PropVariantClear(ref value);
        }
    }

    private void Seek(double sourceTimeMs)
    {
        Check(_reader!.Flush(FirstVideoStream), "IMFSourceReader.Flush");
        var position = PropVariant.FromInt64(checked((long)Math.Round(sourceTimeMs * 10_000.0)));
        Guid timeFormat = Guid.Empty;
        Check(
            _reader.SetCurrentPosition(ref timeFormat, ref position),
            "IMFSourceReader.SetCurrentPosition");
        _initialized = true;
        _endOfStream = false;
        _frameSelector.Reset();
        _unresolvedDurationFrame = null;
        _rawLookaheadFrame = null;
        _lastResolvedSampleDurationMs = 0;
    }

    private DecodedVideoFrame? ReadNextFrame(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DecodedVideoFrame? raw = _rawLookaheadFrame;
            _rawLookaheadFrame = null;
            raw ??= ReadRawNextFrame(cancellationToken);

            if (_unresolvedDurationFrame is null)
            {
                if (raw is null)
                {
                    return null;
                }

                if (raw.SampleDurationMs > 0)
                {
                    _lastResolvedSampleDurationMs = raw.SampleDurationMs;
                    return raw;
                }

                _unresolvedDurationFrame = raw;
                continue;
            }

            DecodedVideoFrame pending = _unresolvedDurationFrame;
            _unresolvedDurationFrame = null;
            if (raw is not null)
            {
                if (raw.TimestampMs + TimestampEpsilonMs < pending.TimestampMs)
                {
                    throw new InvalidDataException("Decoded video timestamps moved backwards.");
                }

                _rawLookaheadFrame = raw;
            }

            double resolvedDuration = ResolveMissingSampleDuration(
                pending.TimestampMs,
                raw?.TimestampMs,
                DurationMs,
                _lastResolvedSampleDurationMs);
            if (resolvedDuration > 0)
            {
                _lastResolvedSampleDurationMs = resolvedDuration;
            }

            return pending with { SampleDurationMs = resolvedDuration };
        }
    }

    /// <summary>
    /// Resolves an omitted sample duration from the following presentation timestamp. The
    /// previous interval is only a final fallback because it describes the previous sample on
    /// variable-frame-rate media, not the current one.
    /// </summary>
    internal static double ResolveMissingSampleDuration(
        double timestampMs,
        double? nextTimestampMs,
        double containerDurationMs,
        double previousResolvedDurationMs)
    {
        if (nextTimestampMs is double next)
        {
            if (double.IsFinite(next) && next > timestampMs + TimestampEpsilonMs)
            {
                return next - timestampMs;
            }

            return double.IsFinite(previousResolvedDurationMs) && previousResolvedDurationMs > 0
                ? previousResolvedDurationMs
                : 0;
        }

        if (double.IsFinite(containerDurationMs)
            && containerDurationMs > timestampMs + TimestampEpsilonMs)
        {
            return containerDurationMs - timestampMs;
        }

        return double.IsFinite(previousResolvedDurationMs) && previousResolvedDurationMs > 0
            ? previousResolvedDurationMs
            : 0;
    }

    private DecodedVideoFrame? ReadRawNextFrame(CancellationToken cancellationToken)
    {
        if (_endOfStream)
        {
            return null;
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Check(
                _reader!.ReadSample(
                    FirstVideoStream,
                    0,
                    out _,
                    out uint flags,
                    out long timestamp,
                    out IMFSample? sample),
                "IMFSourceReader.ReadSample");

            if ((flags & CurrentMediaTypeChangedFlag) != 0)
            {
                RefreshOutputFormat();
            }

            if ((flags & EndOfStreamFlag) != 0)
            {
                _endOfStream = true;
            }

            if (sample is null)
            {
                if (_endOfStream)
                {
                    return null;
                }

                continue;
            }

            try
            {
                return CopySample(sample, timestamp / 10_000.0);
            }
            finally
            {
                Marshal.ReleaseComObject(sample);
            }
        }
    }

    private DecodedVideoFrame CopySample(IMFSample sample, double timestampMs)
    {
        int durationResult = sample.GetSampleDuration(out long sampleDurationTicks);
        if (durationResult < 0 && durationResult != MediaFoundationNoSampleDuration)
        {
            Check(durationResult, "IMFSample.GetSampleDuration");
        }

        double sampleDurationMs = durationResult >= 0 && sampleDurationTicks > 0
            ? sampleDurationTicks / 10_000.0
            : 0;
        Check(sample.ConvertToContiguousBuffer(out IMFMediaBuffer buffer), "IMFSample.ConvertToContiguousBuffer");
        try
        {
            Check(buffer.Lock(out IntPtr data, out _, out uint currentLength), "IMFMediaBuffer.Lock");
            try
            {
                int rowBytes = checked(Width * 4);
                int absoluteStride = checked((int)Math.Abs((long)_sourceStride));
                long required = checked((long)absoluteStride * _codedHeight);
                if (currentLength < required)
                {
                    throw new InvalidDataException(
                        $"Decoded video buffer is truncated ({currentLength} bytes, expected at least {required}).");
                }

                int outputStride = rowBytes;
                byte[] pixels = new byte[checked(outputStride * Height)];
                IntPtr topRow = _sourceStride < 0
                    ? data + checked(absoluteStride * (_codedHeight - 1))
                    : data;
                for (int y = 0; y < Height; y++)
                {
                    IntPtr sourceRow = topRow
                        + checked((y + _cropY) * _sourceStride)
                        + checked(_cropX * 4);
                    Marshal.Copy(sourceRow, pixels, checked(y * outputStride), rowBytes);
                }

                return new DecodedVideoFrame(
                    pixels,
                    Width,
                    Height,
                    outputStride,
                    timestampMs,
                    sampleDurationMs);
            }
            finally
            {
                Check(buffer.Unlock(), "IMFMediaBuffer.Unlock");
            }
        }
        finally
        {
            Marshal.ReleaseComObject(buffer);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _frameSelector.Reset();
        _unresolvedDurationFrame = null;
        _rawLookaheadFrame = null;
        if (_reader is not null)
        {
            Marshal.ReleaseComObject(_reader);
            _reader = null;
        }

        if (_startedMediaFoundation)
        {
            _ = MFShutdown();
        }
    }

    private static void Check(int result, string operation)
    {
        if (result < 0)
        {
            throw new InvalidOperationException(
                $"Media Foundation call '{operation}' failed with HRESULT 0x{result:X8}.");
        }
    }

    internal static int NativePropVariantSize => Marshal.SizeOf<PropVariant>();

    internal static (int Size, int FractionOffset, int ValueOffset) NativeVideoOffsetLayout =>
        (Marshal.SizeOf<NativeVideoOffset>(),
         checked((int)Marshal.OffsetOf<NativeVideoOffset>(nameof(NativeVideoOffset.Fraction))),
         checked((int)Marshal.OffsetOf<NativeVideoOffset>(nameof(NativeVideoOffset.Value))));

    // The solution is x64-only. Native PROPVARIANT is 24 bytes on x64 because its union includes
    // pointer/count pairs, even though this reader currently consumes only VT_I8 and VT_UI8.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort VariantType;
        [FieldOffset(2)] private ushort _reserved1;
        [FieldOffset(4)] private ushort _reserved2;
        [FieldOffset(6)] private ushort _reserved3;
        [FieldOffset(8)] public long Int64Value;
        [FieldOffset(8)] public ulong UInt64Value;

        internal static PropVariant FromInt64(long value) => new()
        {
            VariantType = VariantInt64,
            Int64Value = value,
        };
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct NativeVideoOffset
    {
        public ushort Fraction;
        public short Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeVideoSize
    {
        public int Width;
        public int Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeVideoArea
    {
        public NativeVideoOffset OffsetX;
        public NativeVideoOffset OffsetY;
        public NativeVideoSize Area;
    }

    [DllImport("mfplat.dll")]
    private static extern int MFStartup(uint version, uint flags);

    [DllImport("mfplat.dll")]
    private static extern int MFShutdown();

    [DllImport("mfplat.dll")]
    private static extern int MFCreateAttributes(out IMFAttributes attributes, uint initialSize);

    [DllImport("mfplat.dll")]
    private static extern int MFCreateMediaType(out IMFMediaType mediaType);

    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
    private static extern int MFCreateSourceReaderFromURL(
        string url,
        IMFAttributes attributes,
        out IMFSourceReader sourceReader);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);

    [ComImport]
    [Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSourceReader
    {
        [PreserveSig] int GetStreamSelection(uint streamIndex, [MarshalAs(UnmanagedType.Bool)] out bool selected);
        [PreserveSig] int SetStreamSelection(uint streamIndex, [MarshalAs(UnmanagedType.Bool)] bool selected);
        [PreserveSig] int GetNativeMediaType(uint streamIndex, uint mediaTypeIndex, out IMFMediaType mediaType);
        [PreserveSig] int GetCurrentMediaType(uint streamIndex, out IMFMediaType mediaType);
        [PreserveSig] int SetCurrentMediaType(uint streamIndex, IntPtr reserved, IMFMediaType mediaType);
        [PreserveSig] int SetCurrentPosition(ref Guid timeFormat, ref PropVariant position);
        [PreserveSig]
        int ReadSample(
            uint streamIndex,
            uint controlFlags,
            out uint actualStreamIndex,
            out uint streamFlags,
            out long timestamp,
            [MarshalAs(UnmanagedType.Interface)] out IMFSample? sample);
        [PreserveSig] int Flush(uint streamIndex);
        [PreserveSig] int GetServiceForStream(uint streamIndex, ref Guid service, ref Guid interfaceId, out IntPtr serviceObject);
        [PreserveSig] int GetPresentationAttribute(uint streamIndex, ref Guid attribute, out PropVariant value);
    }
}
