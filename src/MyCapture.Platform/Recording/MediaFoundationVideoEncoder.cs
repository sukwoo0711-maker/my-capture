using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace MyCapture.Platform.Recording;

/// <summary>
/// Encodes BGRA32 (or, when requested, NV12) frames to an H.264 MP4 using the Media Foundation
/// Sink Writer.
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
/// <para>
/// An NV12 input type (<see cref="VideoEncoderOptions.InputFormat"/>) feeds the encoder MFT
/// directly, with no colour converter. Re-encoding decoded H.264 that way avoids a Y'CbCr to RGB
/// to Y'CbCr round trip: faster, and the unchanged samples cannot lose precision. Y'CbCr is
/// top-down in Media Foundation, so NV12 rows are copied in order.
/// </para>
/// </remarks>
public sealed partial class MediaFoundationVideoEncoder : IAudioVideoEncoder
{
    private static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00AA00389B71");
    private static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00AA00389B71");
    private static readonly Guid MFVideoFormat_RGB32 = new("00000016-0000-0010-8000-00AA00389B71");
    private static readonly Guid MFVideoFormat_NV12 = new("3231564E-0000-0010-8000-00AA00389B71");

    private static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    private static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    private static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    private static readonly Guid MF_MT_ALL_SAMPLES_INDEPENDENT = new("c9173739-5e56-461c-b713-46fb995cb95f");
    private static readonly Guid MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS = new("a634a91c-822b-41b9-a494-4de4643612b0");
    private static readonly Guid MF_MT_MPEG2_PROFILE = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    private static readonly Guid CODECAPI_AVEncCommonRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    private static readonly Guid CODECAPI_AVEncCommonQuality = new("fcbf57a3-7ea5-4b0c-9644-69b40c39c391");
    private static readonly Guid CODECAPI_AVEncCommonQualityVsSpeed = new("98332df8-03cd-476b-89fa-3f9e442dec9f");
    private static readonly Guid MF_MT_VIDEO_NOMINAL_RANGE = new("c21b8ee5-b956-4071-8daf-325edf5cab11");

    private const uint MFVideoInterlace_Progressive = 2;
    private const uint MFNominalRange_0_255 = 1;
    private const uint eAVEncCommonRateControlMode_Quality = 3;

    private readonly ILogger _log;
    private readonly VideoEncoderOptions _options;
    private MediaFoundationLifetime.Lease? _platform;

    private IMFSinkWriter? _writer;
    private int _streamIndex;
    private long _sampleDurationTicks; // 100ns units
    private bool _finalized;
    private bool _disposed;

    /// <summary>Test-only: emulate a GPU-less host by refusing hardware encoder MFTs.</summary>
    internal static bool ForceSoftwareForTest { get; set; }

    public MediaFoundationVideoEncoder(VideoEncoderOptions options, ILogger log)
        : this(options, log, diagnosticQualityVsSpeed: null)
    {
    }

    internal MediaFoundationVideoEncoder(VideoEncoderOptions options, ILogger log, uint? diagnosticQualityVsSpeed,
        Action<AudioEncoderSetupStage>? audioSetupForTest = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _audioSetupForTest = audioSetupForTest;
        _audioOwnerThread = options.Audio is null ? 0 : Environment.CurrentManagedThreadId;
        if (diagnosticQualityVsSpeed is > 100)
            throw new ArgumentOutOfRangeException(nameof(diagnosticQualityVsSpeed));
        if (diagnosticQualityVsSpeed.HasValue && (options.ConstantQuality.HasValue || options.QualityVsSpeed.HasValue))
            throw new ArgumentException("The diagnostic effort must be the only encoder quality override.", nameof(options));
        _diagnosticQualityVsSpeed = diagnosticQualityVsSpeed;
        if (options.InputFormat == VideoEncoderInputFormat.Nv12
            && ((options.Width & 1) != 0 || (options.Height & 1) != 0))
        {
            throw new ArgumentException(
                $"NV12 input needs an even frame size ({options.Width}x{options.Height}).",
                nameof(options));
        }

        // The platform stays started from the first use to app exit (#243); the lease only keeps
        // it from shutting down at exit while this encoder still holds Media Foundation objects.
        _platform = MediaFoundationPlatform.Acquire();

        try
        {
            try
            {
                Initialize(options.Audio is not null);
            }
            catch (AudioSetupException ex)
            {
                // No sample has been accepted. Release the incomplete sink before reopening
                // the same output URL; the MF file sink replaces its incomplete container.
                Marshal.ReleaseComObject(_writer!);
                _writer = null;
                _audioStreamIndex = -1;
                AudioState = AudioEncoderState.Unavailable;
                AudioUnavailableReason = ex.InnerException!.Message;
                _log.LogWarning(ex.InnerException, "Audio setup failed; starting a video-only recording");
                Initialize(withAudio: false);
            }
        }
        catch
        {
            ReleaseMediaFoundation();
            throw;
        }
    }

    public int Width => _options.Width;

    public int Height => _options.Height;

    /// <summary>
    /// Opt-in diagnostics, called only by tests on the encoder's owning thread. Queries never
    /// configure or drive an MFT. Unsupported queries retain their HRESULT instead of replacing
    /// a WriteSample/Finalize failure. The normal encoding path does not call this method.
    /// </summary>
    internal IReadOnlyDictionary<string, object?> CaptureDiagnostics()
    {
        var result = new Dictionary<string, object?> { ["finalized"] = _finalized, ["disposed"] = _disposed };
        if (_writer is null) { result["writerAvailable"] = false; return result; }
        result["writerAvailable"] = true;
        try
        {
            int size = Marshal.SizeOf<DiagnosticSinkStatistics>();
            if (size != 112) throw new InvalidOperationException("Unexpected MF_SINK_WRITER_STATISTICS layout.");
            IntPtr memory = Marshal.AllocCoTaskMem(size);
            try
            {
                Marshal.StructureToPtr(new DiagnosticSinkStatistics { Cb = (uint)size }, memory, false);
                int hr = _writer.GetStatistics(_streamIndex, memory);
                result["statisticsHresult"] = DiagnosticHr(hr);
                if (hr >= 0)
                {
                    DiagnosticSinkStatistics s = Marshal.PtrToStructure<DiagnosticSinkStatistics>(memory);
                    result["statistics"] = new
                    {
                        s.Cb, s.LastTimestampReceived, s.LastTimestampEncoded, s.LastTimestampProcessed,
                        s.LastStreamTickReceived, s.LastSinkSampleRequest, s.NumSamplesReceived,
                        s.NumSamplesEncoded, s.NumSamplesProcessed, s.NumStreamTicksReceived,
                        s.ByteCountQueued, s.ByteCountProcessed, s.NumOutstandingSinkSampleRequests,
                        s.AverageSampleRateReceived, s.AverageSampleRateEncoded, s.AverageSampleRateProcessed,
                    };
                }
            }
            finally { Marshal.FreeCoTaskMem(memory); }
        }
        catch (Exception ex) { result["statisticsQueryFailure"] = DiagnosticHr(ex.HResult); }
        try
        {
            // This is another interface on the owned writer RCW, not a newly returned object.
            // Do not ReleaseComObject on this alias: the encoder still owns the writer.
            var extended = (IDiagnosticSinkWriterEx)_writer;
            var transforms = new List<object>();
            result["transforms"] = transforms;
            for (int index = 0; index < 8; index++)
            {
                IDiagnosticTransform? transform = null;
                try
                {
                    int hr = extended.GetTransformForStream(_streamIndex, index, out Guid category, out transform);
                    if (hr < 0 || transform is null)
                    {
                        result["transformEnumerationEnd"] = new { index, hresult = DiagnosticHr(hr) };
                        return result;
                    }
                    var entry = new Dictionary<string, object?> { ["index"] = index, ["category"] = category };
                    transforms.Add(entry);
                    try { ReadDiagnosticTransform(transform, entry); }
                    catch (Exception ex) { entry["queryFailure"] = DiagnosticHr(ex.HResult); }
                }
                finally { if (transform is not null) Marshal.ReleaseComObject(transform); }
            }
            result["transformEnumerationTruncated"] = true;
        }
        catch (Exception ex) { result["transformQueryFailure"] = DiagnosticHr(ex.HResult); }
        return result;
    }

    private static string DiagnosticHr(int hr) => $"0x{hr:X8}";

    private static void ReadDiagnosticTransform(IDiagnosticTransform transform, Dictionary<string, object?> entry)
    {
        IMFAttributes? attributes = null;
        try
        {
            int hr = transform.GetAttributes(out attributes);
            entry["attributesHresult"] = DiagnosticHr(hr);
            if (hr >= 0 && attributes is not null)
            {
                Guid clsidKey = new("6821c42b-65a4-4e82-99bc-9a88205ecd0c");
                hr = attributes.GetGUID(ref clsidKey, out Guid clsid);
                entry["classIdHresult"] = DiagnosticHr(hr);
                if (hr >= 0) entry["classId"] = clsid;
                entry["friendlyName"] = DiagnosticString(attributes, new Guid("314ffbae-5b41-4c95-9c19-4e7d586face3"), out int nameHr);
                entry["friendlyNameHresult"] = DiagnosticHr(nameHr);
                // Device URLs are unnecessary local identifiers; record only presence and hash.
                string? url = DiagnosticString(attributes, new Guid("2fb866ac-b078-4942-ab6c-003d05cda674"), out int urlHr);
                entry["hardwareUrlHresult"] = DiagnosticHr(urlHr);
                entry["hardwareUrlPresent"] = url is not null;
                if (url is not null) entry["hardwareUrlSha256"] = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(url)));
            }
        }
        finally { if (attributes is not null) Marshal.ReleaseComObject(attributes); }
        try
        {
            int hr = ((IDiagnosticPersist)transform).GetClassID(out Guid clsid);
            entry["persistClassIdHresult"] = DiagnosticHr(hr);
            if (hr >= 0) entry["persistClassId"] = clsid;
        }
        catch (Exception ex) { entry["persistClassIdHresult"] = DiagnosticHr(ex.HResult); }

        int countHr = transform.GetStreamCount(out uint inputs, out uint outputs);
        entry["streamCountHresult"] = DiagnosticHr(countHr);
        if (countHr < 0) return;
        entry["inputCount"] = inputs; entry["outputCount"] = outputs;
        if (inputs > 16 || outputs > 16) { entry["streamEnumerationTruncated"] = true; return; }
        uint[] inputIds = new uint[inputs], outputIds = new uint[outputs];
        int idsHr = transform.GetStreamIDs(inputs, inputIds, outputs, outputIds);
        entry["streamIdsHresult"] = DiagnosticHr(idsHr);
        if (idsHr == unchecked((int)0x80004001)) // E_NOTIMPL specifies consecutive stream IDs.
        {
            for (uint i = 0; i < inputs; i++) inputIds[i] = i;
            for (uint i = 0; i < outputs; i++) outputIds[i] = i;
        }
        else if (idsHr < 0) return;
        var types = new List<object>(); entry["mediaTypes"] = types;
        foreach ((bool input, uint[] ids) in new[] { (true, inputIds), (false, outputIds) })
            foreach (uint id in ids)
            {
                IMFMediaType? type = null;
                try
                {
                    int hr = input ? transform.GetInputCurrentType(id, out type) : transform.GetOutputCurrentType(id, out type);
                    types.Add(new { direction = input ? "input" : "output", streamId = id,
                        hresult = DiagnosticHr(hr), attributes = hr >= 0 && type is not null ? ReadDiagnosticMediaType(type) : null });
                }
                finally { if (type is not null) Marshal.ReleaseComObject(type); }
            }
    }

    private static string? DiagnosticString(IMFAttributes attributes, Guid key, out int hr)
    {
        IntPtr text = IntPtr.Zero;
        try
        {
            hr = attributes.GetAllocatedString(ref key, out text, out uint length);
            return hr >= 0 && text != IntPtr.Zero && length <= 4096 ? Marshal.PtrToStringUni(text, (int)length) : null;
        }
        finally { if (text != IntPtr.Zero) Marshal.FreeCoTaskMem(text); }
    }

    private static Dictionary<string, object?> ReadDiagnosticMediaType(IMFMediaType type)
    {
        var values = new Dictionary<string, object?>();
        foreach ((string name, Guid key) in new[] { ("majorType", MF_MT_MAJOR_TYPE), ("subtype", MF_MT_SUBTYPE) })
        {
            Guid k = key; int hr = type.GetGUID(ref k, out Guid value);
            values[name] = new { hresult = DiagnosticHr(hr), value = hr >= 0 ? (Guid?)value : null };
        }
        foreach ((string name, Guid key) in new[] { ("frameSize", MF_MT_FRAME_SIZE), ("frameRate", MF_MT_FRAME_RATE),
                     ("pixelAspectRatio", MF_MT_PIXEL_ASPECT_RATIO) })
        {
            Guid k = key; int hr = type.GetUINT64(ref k, out ulong value);
            values[name] = new { hresult = DiagnosticHr(hr), high = hr >= 0 ? (uint?)(value >> 32) : null,
                low = hr >= 0 ? (uint?)value : null };
        }
        foreach ((string name, Guid key) in new[] { ("bitrate", MF_MT_AVG_BITRATE), ("profile", MF_MT_MPEG2_PROFILE),
                     ("nominalRange", MF_MT_VIDEO_NOMINAL_RANGE), ("interlace", MF_MT_INTERLACE_MODE),
                     ("yuvMatrix", new Guid("3e23d450-2c75-4d25-a00e-b91670d12327")),
                     ("primaries", new Guid("dbfbe4d7-0740-4ee0-8192-850ab0e21935")),
                     ("transferFunction", new Guid("5fb0fce9-be5c-4935-a811-ec838f8eed93")),
                     ("stride", new Guid("644b4e48-1e02-4516-b0eb-c01ca9d49ac6")) })
        {
            Guid k = key; int hr = type.GetUINT32(ref k, out uint value);
            values[name] = new { hresult = DiagnosticHr(hr), value = hr >= 0 ? (uint?)value : null };
        }
        return values;
    }

    // Layout and COM slots verified against Windows SDK 10.0.26100.0 mfreadwrite.h/mftransform.h.
#pragma warning disable CS0649 // The native GetStatistics call fills these sequential fields.
    [StructLayout(LayoutKind.Sequential)]
    private struct DiagnosticSinkStatistics
    {
        public uint Cb;
        public long LastTimestampReceived, LastTimestampEncoded, LastTimestampProcessed, LastStreamTickReceived, LastSinkSampleRequest;
        public ulong NumSamplesReceived, NumSamplesEncoded, NumSamplesProcessed, NumStreamTicksReceived;
        public uint ByteCountQueued;
        public ulong ByteCountProcessed;
        public uint NumOutstandingSinkSampleRequests, AverageSampleRateReceived, AverageSampleRateEncoded, AverageSampleRateProcessed;
    }
#pragma warning restore CS0649

    private void Initialize(bool withAudio)
    {
        int fps = Math.Max(1, _options.Fps);
        _sampleDurationTicks = 10_000_000L / fps;

        // Attributes: enable hardware transforms so the OS uses a GPU H.264 MFT when present.
        Check(MFCreateAttributes(out IMFAttributes attributes, 1), nameof(MFCreateAttributes));
        try
        {
            Guid hwKey = MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS;
            Check(
                attributes.SetUINT32(ref hwKey, _options.AllowHardwareEncoder && !ForceSoftwareForTest ? 1u : 0u),
                "IMFAttributes.SetUINT32(hardware transforms)");

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
            if (_options.H264Profile is { } profile)
            {
                SetU32(outType, MF_MT_MPEG2_PROFILE, (uint)profile);
            }

            if (_options.FullRange)
            {
                // The Sink Writer's RGB to NV12 converter follows the encoder's range, and the
                // file declares it, so decoders that honour the declaration show true black and white.
                // Full-range NV12 input declares the same range below, so no converter is inserted.
                SetU32(outType, MF_MT_VIDEO_NOMINAL_RANGE, MFNominalRange_0_255);
            }

            Check(_writer!.AddStream(outType, out _streamIndex), nameof(IMFSinkWriter.AddStream));
        }
        finally
        {
            Marshal.ReleaseComObject(outType);
        }

        // Input type: uncompressed RGB32 (BGRA byte order in memory), or NV12 when requested.
        Check(MFCreateMediaType(out IMFMediaType inType), nameof(MFCreateMediaType));
        try
        {
            SetGuidAttr(inType, MF_MT_MAJOR_TYPE, MFMediaType_Video);
            SetGuidAttr(
                inType,
                MF_MT_SUBTYPE,
                _options.InputFormat == VideoEncoderInputFormat.Nv12 ? MFVideoFormat_NV12 : MFVideoFormat_RGB32);
            SetU32(inType, MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
            SetU32(inType, MF_MT_ALL_SAMPLES_INDEPENDENT, 1);
            SetFrameSize(inType, (uint)_options.Width, (uint)_options.Height);
            SetRatio(inType, MF_MT_FRAME_RATE, (uint)fps, 1);
            SetRatio(inType, MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
            if (_options.FullRange && _options.InputFormat == VideoEncoderInputFormat.Nv12)
            {
                SetU32(inType, MF_MT_VIDEO_NOMINAL_RANGE, MFNominalRange_0_255);
            }

            SetInputMediaType(inType);
        }
        finally
        {
            Marshal.ReleaseComObject(inType);
        }

        if (withAudio)
        {
            try
            {
                _audioSetupForTest?.Invoke(AudioEncoderSetupStage.AudioStream);
                InitializeAudio();
                _audioSetupForTest?.Invoke(AudioEncoderSetupStage.BeginWriting);
                Check(_writer!.BeginWriting(), nameof(IMFSinkWriter.BeginWriting));
                AudioState = AudioEncoderState.Available;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                throw new AudioSetupException(ex);
            }
        }
        else Check(_writer!.BeginWriting(), nameof(IMFSinkWriter.BeginWriting));
    }

    /// <summary>
    /// Connects the input type; a constant-quality request travels as encoder parameters, which the
    /// Sink Writer hands to the H.264 encoder's ICodecAPI.
    /// </summary>
    private void SetInputMediaType(IMFMediaType inType)
    {
        if (_diagnosticQualityVsSpeed is { } effortOverride)
        {
            SetDiagnosticQualityVsSpeed(inType, effortOverride);
            return;
        }
        if (_options.ConstantQuality is not { } quality)
        {
            Check(_writer!.SetInputMediaType(_streamIndex, inType, IntPtr.Zero), nameof(IMFSinkWriter.SetInputMediaType));
            return;
        }

        Check(MFCreateAttributes(out IMFAttributes parameters, 2), nameof(MFCreateAttributes));
        IntPtr pointer = IntPtr.Zero;
        try
        {
            Guid mode = CODECAPI_AVEncCommonRateControlMode;
            Guid level = CODECAPI_AVEncCommonQuality;
            Check(parameters.SetUINT32(ref mode, eAVEncCommonRateControlMode_Quality), "IMFAttributes.SetUINT32(rate control)");
            Check(parameters.SetUINT32(ref level, (uint)Math.Clamp(quality, 1, 100)), "IMFAttributes.SetUINT32(quality)");
            if (_options.QualityVsSpeed is { } effort)
            {
                Guid speed = CODECAPI_AVEncCommonQualityVsSpeed;
                Check(parameters.SetUINT32(ref speed, (uint)Math.Clamp(effort, 0, 100)), "IMFAttributes.SetUINT32(quality vs speed)");
            }
            pointer = Marshal.GetComInterfaceForObject(parameters, typeof(IMFAttributes));
            Check(_writer!.SetInputMediaType(_streamIndex, inType, pointer), nameof(IMFSinkWriter.SetInputMediaType));
        }
        finally
        {
            if (pointer != IntPtr.Zero) Marshal.Release(pointer);
            Marshal.ReleaseComObject(parameters);
        }
    }

    public void WriteFrame(in EncoderFrame frame)
    {
        if (_options.Audio is not null) RequireAudioOwnerThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_writer is null || _finalized)
        {
            return;
        }

        // NV12 is a luma plane plus a half-height interleaved chroma plane with the same row
        // length, so both layouts are a sequence of equally long rows.
        bool nv12 = _options.InputFormat == VideoEncoderInputFormat.Nv12;
        int rowBytes = nv12 ? _options.Width : _options.Width * 4;
        int rows = nv12 ? _options.Height + (_options.Height / 2) : _options.Height;
        int bufferBytes = rowBytes * rows;
        if (frame.Format != _options.InputFormat
            || frame.Width != _options.Width || frame.Height != _options.Height
            || frame.Stride < rowBytes
            || frame.Pixels.Length < checked((frame.Stride * (rows - 1)) + rowBytes))
        {
            throw new ArgumentException(
                "Encoder frame does not match the format and dimensions the encoder was opened with "
                + $"(frame {frame.Format} {frame.Width}x{frame.Height} stride {frame.Stride}, "
                + $"expected {_options.InputFormat} {_options.Width}x{_options.Height}).",
                nameof(frame));
        }

        Check(MFCreateMemoryBuffer((uint)bufferBytes, out IMFMediaBuffer buffer), nameof(MFCreateMemoryBuffer));
        try
        {
            Check(buffer.Lock(out IntPtr dest, out _, out _), nameof(IMFMediaBuffer.Lock));
            try
            {
                // Our grabber produces a TOP-DOWN BGRA frame (row 0 = top of the screen).
                // Media Foundation's uncompressed RGB32 input is interpreted BOTTOM-UP when the
                // stride is positive: the first row in the buffer is treated as the bottom of
                // the picture. Copying row-for-row therefore yields an upside-down video. We
                // flip vertically here — source row y is written to destination row
                // (Height-1-y) — so the encoded frame is upright. (Left/right is unaffected;
                // BGRA byte order within a row is unchanged, so there is no horizontal mirror.)
                // Y'CbCr formats such as NV12 are always top-down and are copied in order.
                unsafe
                {
                    fixed (byte* source = frame.Pixels)
                    {
                        byte* destination = (byte*)dest;
                        for (int y = 0; y < rows; y++)
                        {
                            int target = nv12 ? y : _options.Height - 1 - y;
                            Buffer.MemoryCopy(
                                source + (y * frame.Stride),
                                destination + (target * rowBytes),
                                rowBytes,
                                rowBytes);
                        }
                    }
                }
            }
            finally
            {
                Check(buffer.Unlock(), nameof(IMFMediaBuffer.Unlock));
            }

            Check(buffer.SetCurrentLength((uint)bufferBytes), nameof(IMFMediaBuffer.SetCurrentLength));

            Check(MFCreateSample(out IMFSample sample), nameof(MFCreateSample));
            try
            {
                Check(sample.AddBuffer(buffer), nameof(IMFSample.AddBuffer));

                // RGB32 input reaches the encoder through the Sink Writer's colour converter,
                // which delivers sample times rounded to the nearest 100 ns tick. NV12 skips
                // that converter, so it is rounded here: the same timestamps then give the same
                // sample table and container duration through either input (truncating loses a
                // tick at 30 fps whenever the frame index is a multiple of three).
                long timeTicks = nv12
                    ? (long)Math.Round(frame.TimestampMs * 10_000.0)
                    : (long)(frame.TimestampMs * 10_000.0);
                Check(sample.SetSampleTime(timeTicks), nameof(IMFSample.SetSampleTime));
                Check(sample.SetSampleDuration(_sampleDurationTicks), nameof(IMFSample.SetSampleDuration));

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
        if (_options.Audio is not null) RequireAudioOwnerThread();
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
        Check(type.SetGUID(ref k, ref v), "IMFMediaType.SetGUID");
    }

    private static void SetU32(IMFMediaType type, Guid key, uint value)
    {
        Guid k = key;
        Check(type.SetUINT32(ref k, value), "IMFMediaType.SetUINT32");
    }

    private static void SetFrameSize(IMFMediaType type, uint width, uint height)
    {
        Guid k = MF_MT_FRAME_SIZE;
        ulong packed = ((ulong)width << 32) | height;
        Check(type.SetUINT64(ref k, packed), "IMFMediaType.SetUINT64(frame size)");
    }

    private static void SetRatio(IMFMediaType type, Guid key, uint numerator, uint denominator)
    {
        Guid k = key;
        ulong packed = ((ulong)numerator << 32) | denominator;
        Check(type.SetUINT64(ref k, packed), "IMFMediaType.SetUINT64(ratio)");
    }

    private void ReleaseMediaFoundation()
    {
        if (_writer is not null)
        {
            Marshal.ReleaseComObject(_writer);
            _writer = null;
        }

        // Last, so the platform outlives the writer and the transforms it owns.
        _platform?.Dispose();
        _platform = null;
    }

    public void Dispose()
    {
        if (_options.Audio is not null) RequireAudioOwnerThread();
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

        ReleaseMediaFoundation();
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
    // MFStartup and MFShutdown belong to MediaFoundationPlatform alone.

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
//
// CRITICAL: .NET COM interop does NOT concatenate base-interface vtable slots the way C++
// does. A [ComImport] interface's methods are laid out starting immediately after IUnknown,
// and declaring `interface IMFSample : IMFAttributes` does NOT prepend IMFAttributes' 30
// slots. So every derived interface here redeclares the full IMFAttributes method list inline,
// in exact vtable order, before its own methods. Getting this wrong shifts every slot and
// causes an access violation at the first call into a misplaced method (observed at
// IMFSample.SetSampleTime). The methods we never call still MUST be declared so the slots we
// do call land at the right offset; their PROPVARIANT/blob arguments are declared as opaque
// IntPtr since we never invoke them.

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
internal interface IMFMediaType
{
    // --- IMFAttributes (30 slots, inlined; only the setters below are called) ---
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

    // --- IMFMediaType-specific (not called, declared to keep vtable honest) ---
    [PreserveSig] int GetMajorType(out Guid guidMajorType);
    [PreserveSig] int IsCompressedFormat(out bool compressed);
    [PreserveSig] int IsEqual(IMFMediaType type, out uint flags);
    [PreserveSig] int GetRepresentation(Guid representation, out IntPtr representationData);
    [PreserveSig] int FreeRepresentation(Guid representation, IntPtr representationData);
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
internal interface IMFSample
{
    // --- IMFAttributes (30 slots, inlined) ---
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

    // --- IMFSample-specific ---
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

// Diagnostic interfaces retain the native vtable order. Only getters are invoked.
[ComImport, Guid("588d72ab-5bc1-496a-8714-b70617141b25"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDiagnosticSinkWriterEx
{
    [PreserveSig] int AddStream(IMFMediaType type, out int index);
    [PreserveSig] int SetInputMediaType(int index, IMFMediaType type, IntPtr parameters);
    [PreserveSig] int BeginWriting();
    [PreserveSig] int WriteSample(int index, IMFSample sample);
    [PreserveSig] int SendStreamTick(int index, long timestamp);
    [PreserveSig] int PlaceMarker(int index, IntPtr context);
    [PreserveSig] int NotifyEndOfSegment(int index);
    [PreserveSig] int Flush(int index);
    [PreserveSig] int FinalizeWriting();
    [PreserveSig] int GetServiceForStream(int index, ref Guid service, ref Guid iid, out IntPtr value);
    [PreserveSig] int GetStatistics(int index, IntPtr statistics);
    [PreserveSig] int GetTransformForStream(int streamIndex, int transformIndex, out Guid category, out IDiagnosticTransform? transform);
}

[ComImport, Guid("bf94c121-5b05-4e6f-8000-ba598961414d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDiagnosticTransform
{
    [PreserveSig] int GetStreamLimits(out uint minInput, out uint maxInput, out uint minOutput, out uint maxOutput);
    [PreserveSig] int GetStreamCount(out uint inputs, out uint outputs);
    [PreserveSig] int GetStreamIDs(uint inputCount, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] uint[] inputs,
        uint outputCount, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] uint[] outputs);
    [PreserveSig] int GetInputStreamInfo(uint id, IntPtr info);
    [PreserveSig] int GetOutputStreamInfo(uint id, IntPtr info);
    [PreserveSig] int GetAttributes(out IMFAttributes? attributes);
    [PreserveSig] int GetInputStreamAttributes(uint id, out IMFAttributes attributes);
    [PreserveSig] int GetOutputStreamAttributes(uint id, out IMFAttributes attributes);
    [PreserveSig] int DeleteInputStream(uint id);
    [PreserveSig] int AddInputStreams(uint count, IntPtr ids);
    [PreserveSig] int GetInputAvailableType(uint id, uint index, out IMFMediaType type);
    [PreserveSig] int GetOutputAvailableType(uint id, uint index, out IMFMediaType type);
    [PreserveSig] int SetInputType(uint id, IMFMediaType type, uint flags);
    [PreserveSig] int SetOutputType(uint id, IMFMediaType type, uint flags);
    [PreserveSig] int GetInputCurrentType(uint id, out IMFMediaType? type);
    [PreserveSig] int GetOutputCurrentType(uint id, out IMFMediaType? type);
}

[ComImport, Guid("0000010c-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDiagnosticPersist
{
    [PreserveSig] int GetClassID(out Guid clsid);
}
