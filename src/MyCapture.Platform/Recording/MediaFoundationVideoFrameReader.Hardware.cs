using System.IO;
using System.Runtime.InteropServices;

namespace MyCapture.Platform.Recording;

// Optional decode paths of MediaFoundationVideoFrameReader, opted into through
// MediaFoundationVideoFrameReaderOptions: D3D11 hardware decode (DXVA) with a software fallback,
// NV12 output straight from the decoder, and - for BGR32 output on the GPU - the advanced video
// processor with an optional resize. In resize mode Width and Height report the resized output.
// The default options never reach this code, so existing callers keep the original software
// reader exactly. Kept in its own file so the seek/selection logic stays a separate concern.
public sealed partial class MediaFoundationVideoFrameReader
{
    private static readonly Guid VideoFormatArgb32 = new("00000015-0000-0010-8000-00AA00389B71");
    private static readonly Guid MediaTypePixelAspectRatio = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    private static readonly Guid SourceReaderD3DManager = new("ec822da2-e1e9-4b29-a0d8-563c719f5269");
    private static readonly Guid SourceReaderEnableAdvancedVideoProcessing = new("0f81da2c-b537-4672-a8b2-a681b17307a3");
    private static readonly Guid Texture2DInterface = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    private static readonly Guid VideoFormatNv12 = new("3231564E-0000-0010-8000-00AA00389B71");
    private static readonly Guid MediaTypeYuvMatrix = new("3e23d450-2c75-4d25-a00e-b91670d12327");
    private static readonly Guid MediaTypeNominalRange = new("c21b8ee5-b956-4071-8daf-325edf5cab11");
    private const uint Lock2DRead = 1;
    private const uint TransferMatrixBt709 = 1;
    private const uint TransferMatrixBt601 = 2;
    private const uint NominalRangeFull = 1;

    private MediaFoundationD3D11DeviceManager? _d3d;
    private bool _hardwareResize;
    private DecodedVideoPixelFormat _format;

    /// <summary>True when the reader decodes through a D3D11 device manager (DXVA where available).</summary>
    public bool IsHardwareAccelerated => _d3d is not null;

    /// <summary>Why a requested hardware path was not used, or null.</summary>
    public string? HardwareFallbackReason { get; private set; }

    /// <summary>Layout of every frame this reader returns.</summary>
    public DecodedVideoPixelFormat OutputFormat => _format;

    /// <summary>
    /// Y'CbCr matrix and range of NV12 output: the stream's declaration, otherwise the Media
    /// Foundation convention (BT.709 from 720 lines up, BT.601 below, studio range).
    /// </summary>
    public VideoYuvEncoding YuvEncoding { get; private set; }

    /// <summary>
    /// Builds a Source Reader bound to a D3D11 device manager: DXVA decode where the driver
    /// offers it, plus — for BGR32 output — the advanced video processor for RGB conversion and
    /// the optional resize. NV12 output takes the decoder surfaces directly. Returns null on
    /// success, otherwise the failure after releasing every partial object so the caller can
    /// build the software reader instead.
    /// </summary>
    private string? TryInitializeHardware(string fullPath, MediaFoundationVideoFrameReaderOptions options)
    {
        MediaFoundationD3D11DeviceManager? d3d = null;
        IMFSourceReader? reader = null;
        try
        {
            bool nv12 = options.OutputFormat == DecodedVideoPixelFormat.Nv12;
            d3d = MediaFoundationD3D11DeviceManager.Create();
            Check(MFCreateAttributes(out IMFAttributes attributes, 3), nameof(MFCreateAttributes));
            try
            {
                Guid manager = SourceReaderD3DManager;
                Guid advanced = SourceReaderEnableAdvancedVideoProcessing;
                Guid hardware = ReadWriteEnableHardwareTransforms;
                Check(attributes.SetUnknown(ref manager, d3d.Manager), "IMFAttributes.SetUnknown(D3D manager)");
                if (!nv12)
                {
                    Check(attributes.SetUINT32(ref advanced, 1), "IMFAttributes.SetUINT32(advanced video processing)");
                }

                Check(attributes.SetUINT32(ref hardware, 1), "IMFAttributes.SetUINT32(hardware transforms)");
                Check(
                    MFCreateSourceReaderFromURL(fullPath, attributes, out reader),
                    nameof(MFCreateSourceReaderFromURL));
            }
            finally
            {
                Marshal.ReleaseComObject(attributes);
            }

            Check(reader.SetStreamSelection(AllStreams, false), "IMFSourceReader.SetStreamSelection(all)");
            Check(reader.SetStreamSelection(FirstVideoStream, true), "IMFSourceReader.SetStreamSelection(video)");
            if (nv12)
            {
                Check(TrySetOutputSubtype(reader, VideoFormatNv12), "IMFSourceReader.SetCurrentMediaType(hardware NV12)");
                _format = DecodedVideoPixelFormat.Nv12;
            }
            else
            {
                _hardwareResize = SetHardwareOutputType(reader, options.OutputWidth, options.OutputHeight);
            }

            _reader = reader;
            _d3d = d3d;
            reader = null;
            d3d = null;
            RefreshOutputFormat();
            DurationMs = ReadDurationMs(_reader!);
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                   or InvalidDataException
                                   or COMException
                                   or InvalidCastException
                                   or DllNotFoundException
                                   or EntryPointNotFoundException
                                   or OverflowException)
        {
            if (_d3d is not null)
            {
                // Ownership had already moved; the output-format or duration probe failed.
                if (_reader is not null)
                {
                    Marshal.ReleaseComObject(_reader);
                    _reader = null;
                }

                _d3d.Dispose();
                _d3d = null;
            }

            _hardwareResize = false;
            _format = DecodedVideoPixelFormat.Bgr32;
            return ex.Message;
        }
        finally
        {
            if (reader is not null)
            {
                Marshal.ReleaseComObject(reader);
            }

            d3d?.Dispose();
        }
    }

    /// <summary>
    /// Requests RGB32 (or ARGB32, which some video processors prefer) from the GPU video
    /// processor, resized when a different output size was requested. Returns whether a resize
    /// was configured.
    /// </summary>
    private static bool SetHardwareOutputType(IMFSourceReader reader, int requestedWidth, int requestedHeight)
    {
        ulong? frameSize = null;
        ulong pixelAspect = (1UL << 32) | 1;
        if (requestedWidth is > 0 and <= 65_535 && requestedHeight is > 0 and <= 65_535)
        {
            Check(reader.GetNativeMediaType(FirstVideoStream, 0, out IMFMediaType native), "IMFSourceReader.GetNativeMediaType");
            try
            {
                Guid frameSizeKey = MediaTypeFrameSize;
                Check(native.GetUINT64(ref frameSizeKey, out ulong packed), "IMFMediaType.GetUINT64(native frame size)");
                int codedWidth = checked((int)(packed >> 32));
                int codedHeight = checked((int)(packed & 0xFFFF_FFFF));
                if (codedWidth is < 1 or > 65_535 || codedHeight is < 1 or > 65_535)
                {
                    throw new InvalidDataException("The source video reported invalid dimensions.");
                }

                (_, _, int displayWidth, int displayHeight) = ReadDisplayArea(native, codedWidth, codedHeight);
                if (displayWidth != requestedWidth || displayHeight != requestedHeight)
                {
                    frameSize = ((ulong)requestedWidth << 32) | (uint)requestedHeight;
                    pixelAspect = OutputPixelAspect(native, displayWidth, displayHeight, requestedWidth, requestedHeight);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(native);
            }
        }

        int lastResult = 0;
        foreach (Guid subtype in new[] { VideoFormatRgb32, VideoFormatArgb32 })
        {
            Check(MFCreateMediaType(out IMFMediaType requestedType), nameof(MFCreateMediaType));
            try
            {
                Guid majorKey = MediaTypeMajorType;
                Guid subtypeKey = MediaTypeSubtype;
                Guid majorValue = MediaTypeVideo;
                Guid subtypeValue = subtype;
                Check(requestedType.SetGUID(ref majorKey, ref majorValue), "IMFMediaType.SetGUID(major)");
                Check(requestedType.SetGUID(ref subtypeKey, ref subtypeValue), "IMFMediaType.SetGUID(subtype)");
                if (frameSize is ulong size)
                {
                    Guid frameSizeKey = MediaTypeFrameSize;
                    Guid aspectKey = MediaTypePixelAspectRatio;
                    Check(requestedType.SetUINT64(ref frameSizeKey, size), "IMFMediaType.SetUINT64(frame size)");
                    Check(requestedType.SetUINT64(ref aspectKey, pixelAspect), "IMFMediaType.SetUINT64(pixel aspect)");
                }

                lastResult = reader.SetCurrentMediaType(FirstVideoStream, IntPtr.Zero, requestedType);
                if (lastResult >= 0)
                {
                    return frameSize is not null;
                }
            }
            finally
            {
                Marshal.ReleaseComObject(requestedType);
            }
        }

        Check(lastResult, "IMFSourceReader.SetCurrentMediaType(hardware RGB)");
        return false;
    }

    /// <summary>Requests an uncompressed subtype at the decoder's own size; returns the HRESULT.</summary>
    private static int TrySetOutputSubtype(IMFSourceReader reader, Guid subtype)
    {
        Check(MFCreateMediaType(out IMFMediaType requestedType), nameof(MFCreateMediaType));
        try
        {
            Guid majorKey = MediaTypeMajorType;
            Guid subtypeKey = MediaTypeSubtype;
            Guid majorValue = MediaTypeVideo;
            Guid subtypeValue = subtype;
            Check(requestedType.SetGUID(ref majorKey, ref majorValue), "IMFMediaType.SetGUID(major)");
            Check(requestedType.SetGUID(ref subtypeKey, ref subtypeValue), "IMFMediaType.SetGUID(subtype)");
            return reader.SetCurrentMediaType(FirstVideoStream, IntPtr.Zero, requestedType);
        }
        finally
        {
            Marshal.ReleaseComObject(requestedType);
        }
    }

    /// <summary>
    /// Output pixel aspect that maps the whole source display rectangle onto the whole output
    /// frame. A processor that preserves picture aspect would otherwise letterbox the rounding
    /// difference of a proportional resize; the software path always stretches exactly.
    /// </summary>
    internal static ulong OutputPixelAspect(
        uint sourceNumerator,
        uint sourceDenominator,
        int displayWidth,
        int displayHeight,
        int outputWidth,
        int outputHeight)
    {
        ulong numerator = Math.Max(1u, sourceNumerator) * (ulong)displayWidth * (ulong)outputHeight;
        ulong denominator = Math.Max(1u, sourceDenominator) * (ulong)displayHeight * (ulong)outputWidth;
        ulong divisor = GreatestCommonDivisor(numerator, denominator);
        numerator /= divisor;
        denominator /= divisor;
        while (numerator > uint.MaxValue || denominator > uint.MaxValue)
        {
            numerator = Math.Max(1, numerator >> 1);
            denominator = Math.Max(1, denominator >> 1);
        }

        return (numerator << 32) | denominator;
    }

    private static ulong OutputPixelAspect(
        IMFMediaType native,
        int displayWidth,
        int displayHeight,
        int outputWidth,
        int outputHeight)
    {
        Guid aspectKey = MediaTypePixelAspectRatio;
        int result = native.GetUINT64(ref aspectKey, out ulong packed);
        if (result < 0 && result != MediaFoundationAttributeNotFound)
        {
            Check(result, "IMFMediaType.GetUINT64(pixel aspect)");
        }

        uint sourceNumerator = result >= 0 ? (uint)(packed >> 32) : 1;
        uint sourceDenominator = result >= 0 ? (uint)(packed & 0xFFFF_FFFF) : 1;
        return OutputPixelAspect(
            sourceNumerator,
            sourceDenominator,
            displayWidth,
            displayHeight,
            outputWidth,
            outputHeight);
    }

    private static ulong GreatestCommonDivisor(ulong left, ulong right)
    {
        while (right != 0)
        {
            (left, right) = (right, left % right);
        }

        return Math.Max(1, left);
    }

    /// <summary>
    /// The matrix and range a YUV output type declares. Undeclared values follow the Media
    /// Foundation defaults: BT.709 for 720 lines or more, BT.601 below, studio (16-235) range.
    /// </summary>
    private static VideoYuvEncoding ReadYuvEncoding(IMFMediaType mediaType, int codedHeight)
    {
        Guid matrixKey = MediaTypeYuvMatrix;
        Guid rangeKey = MediaTypeNominalRange;
        int matrixResult = mediaType.GetUINT32(ref matrixKey, out uint matrix);
        int rangeResult = mediaType.GetUINT32(ref rangeKey, out uint range);
        bool bt709 = matrixResult >= 0 && matrix is TransferMatrixBt709 or TransferMatrixBt601
            ? matrix == TransferMatrixBt709
            : codedHeight >= 720;
        return new VideoYuvEncoding(bt709, rangeResult >= 0 && range == NominalRangeFull);
    }

    /// <summary>
    /// A GPU resize maps the source display area onto the output frame. The processor may copy
    /// the decoder's source-sized aperture onto the smaller output type; such an aperture does
    /// not describe this frame, so only an aperture that fits the output is honoured.
    /// </summary>
    private static (int X, int Y, int Width, int Height) ReadResizedDisplayArea(
        IMFMediaType mediaType,
        int codedWidth,
        int codedHeight)
    {
        try
        {
            return ReadDisplayArea(mediaType, codedWidth, codedHeight);
        }
        catch (InvalidDataException)
        {
            return (0, 0, codedWidth, codedHeight);
        }
    }

    /// <summary>
    /// Reads a D3D11 texture-backed sample through the reader's persistent staging texture.
    /// Returns null for buffers that are not DXGI surfaces or not 32-bit BGRA.
    /// </summary>
    private DecodedVideoFrame? TryCopyGpuSurface(IMFMediaBuffer buffer, double timestampMs, double sampleDurationMs)
    {
        if (buffer is not IMFDXGIBuffer dxgi)
        {
            return null;
        }

        Guid textureInterface = Texture2DInterface;
        Check(dxgi.GetResource(ref textureInterface, out IntPtr texture), "IMFDXGIBuffer.GetResource");
        try
        {
            Check(dxgi.GetSubresourceIndex(out uint subresource), "IMFDXGIBuffer.GetSubresourceIndex");
            bool nv12 = _format == DecodedVideoPixelFormat.Nv12;
            byte[]? pixels = _d3d!.TryReadSurface(texture, subresource, _cropX, _cropY, Width, Height, nv12);
            return pixels is null
                ? null
                : new DecodedVideoFrame(pixels, Width, Height, nv12 ? Width : checked(Width * 4), timestampMs, sampleDurationMs)
                {
                    Format = _format,
                };
        }
        finally
        {
            _ = Marshal.Release(texture);
        }
    }

    /// <summary>
    /// Copies an NV12 sample into the packed <see cref="DecodedVideoPixelFormat.Nv12"/> layout.
    /// The chroma plane follows the luma plane after the decoder's full coded height.
    /// </summary>
    private DecodedVideoFrame CopyNv12(IMFMediaBuffer buffer, double timestampMs, double sampleDurationMs)
    {
        int chromaRowBytes = ((Width + 1) / 2) * 2;
        int chromaRows = (Height + 1) / 2;
        // Every byte is overwritten by the plane copies below.
        byte[] pixels = GC.AllocateUninitializedArray<byte>(
            checked((Width * Height) + (chromaRowBytes * chromaRows)));
        if (buffer is IMF2DBuffer2 surface)
        {
            Check(
                surface.Lock2DSize(Lock2DRead, out IntPtr scanline0, out int pitch, out IntPtr bufferStart, out uint bufferLength),
                "IMF2DBuffer2.Lock2DSize");
            try
            {
                CopyNv12Planes(bufferStart, (long)scanline0 - (long)bufferStart, pitch, bufferLength, pixels);
            }
            finally
            {
                Check(surface.Unlock2D(), "IMF2DBuffer2.Unlock2D");
            }
        }
        else
        {
            Check(buffer.Lock(out IntPtr data, out _, out uint currentLength), "IMFMediaBuffer.Lock");
            try
            {
                CopyNv12Planes(data, 0, _sourceStride, currentLength, pixels);
            }
            finally
            {
                Check(buffer.Unlock(), "IMFMediaBuffer.Unlock");
            }
        }

        return new DecodedVideoFrame(pixels, Width, Height, Width, timestampMs, sampleDurationMs)
        {
            Format = DecodedVideoPixelFormat.Nv12,
        };
    }

    private void CopyNv12Planes(IntPtr bufferStart, long lumaOffset, int pitch, uint bufferLength, byte[] destination)
    {
        int chromaRowBytes = ((Width + 1) / 2) * 2;
        int chromaRows = (Height + 1) / 2;
        long firstLuma = lumaOffset + ((long)_cropY * pitch) + _cropX;
        long chromaOffset = lumaOffset + ((long)pitch * _codedHeight);
        long firstChroma = chromaOffset + ((long)(_cropY / 2) * pitch) + _cropX;
        if (pitch < Width
            || pitch < chromaRowBytes
            || firstLuma < 0
            || firstLuma + ((long)(Height - 1) * pitch) + Width > bufferLength
            || firstChroma + ((long)(chromaRows - 1) * pitch) + chromaRowBytes > bufferLength)
        {
            throw new InvalidDataException(
                $"Decoded NV12 buffer is smaller than its frame ({bufferLength} bytes, pitch {pitch}).");
        }

        for (int row = 0; row < Height; row++)
        {
            Marshal.Copy(bufferStart + checked((nint)(firstLuma + ((long)row * pitch))), destination, row * Width, Width);
        }

        int chromaDestination = Width * Height;
        for (int row = 0; row < chromaRows; row++)
        {
            Marshal.Copy(
                bufferStart + checked((nint)(firstChroma + ((long)row * pitch))),
                destination,
                chromaDestination + (row * chromaRowBytes),
                chromaRowBytes);
        }
    }

    /// <summary>
    /// Fallback copy of a GPU (DXGI) sample through <c>Lock2DSize</c>, which reports the real
    /// pitch and extent. Media Foundation stages and synchronises a new copy on every lock, so
    /// this is only used when the persistent staging path cannot read the surface format.
    /// </summary>
    private DecodedVideoFrame CopySurface(IMF2DBuffer2 surface, double timestampMs, double sampleDurationMs)
    {
        Check(
            surface.Lock2DSize(Lock2DRead, out IntPtr scanline0, out int pitch, out IntPtr bufferStart, out uint bufferLength),
            "IMF2DBuffer2.Lock2DSize");
        try
        {
            int rowBytes = checked(Width * 4);
            long firstRow = ((long)scanline0 - (long)bufferStart) + ((long)_cropY * pitch) + ((long)_cropX * 4);
            long lastRow = firstRow + ((long)(Height - 1) * pitch);
            if (Math.Abs((long)pitch) < rowBytes
                || Math.Min(firstRow, lastRow) < 0
                || Math.Max(firstRow, lastRow) + rowBytes > bufferLength)
            {
                throw new InvalidDataException(
                    $"Decoded video surface is smaller than its frame ({bufferLength} bytes, pitch {pitch}).");
            }

            // Every byte is overwritten by the row copies below.
            byte[] pixels = GC.AllocateUninitializedArray<byte>(checked(rowBytes * Height));
            for (int y = 0; y < Height; y++)
            {
                Marshal.Copy(
                    bufferStart + checked((nint)(firstRow + ((long)y * pitch))),
                    pixels,
                    checked(y * rowBytes),
                    rowBytes);
            }

            return new DecodedVideoFrame(pixels, Width, Height, rowBytes, timestampMs, sampleDurationMs);
        }
        finally
        {
            Check(surface.Unlock2D(), "IMF2DBuffer2.Unlock2D");
        }
    }

    // .NET COM interop does not prepend base-interface slots, so the seven IMF2DBuffer methods
    // are redeclared in vtable order before the IMF2DBuffer2 additions.
    [ComImport]
    [Guid("33ae5ea6-4316-436f-8ddd-d73d22f829ec")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMF2DBuffer2
    {
        [PreserveSig] int Lock2D(out IntPtr scanline0, out int pitch);
        [PreserveSig] int Unlock2D();
        [PreserveSig] int GetScanline0AndPitch(out IntPtr scanline0, out int pitch);
        [PreserveSig] int IsContiguousFormat([MarshalAs(UnmanagedType.Bool)] out bool contiguous);
        [PreserveSig] int GetContiguousLength(out uint length);
        [PreserveSig] int ContiguousCopyTo(IntPtr destination, uint destinationLength);
        [PreserveSig] int ContiguousCopyFrom(IntPtr source, uint sourceLength);
        [PreserveSig]
        int Lock2DSize(
            uint lockFlags,
            out IntPtr scanline0,
            out int pitch,
            out IntPtr bufferStart,
            out uint bufferLength);
        [PreserveSig] int Copy2DTo(IntPtr destination);
    }

    [ComImport]
    [Guid("e7174cfa-1c9e-48b1-8866-626226bfc258")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFDXGIBuffer
    {
        [PreserveSig] int GetResource(ref Guid interfaceId, out IntPtr resource);
        [PreserveSig] int GetSubresourceIndex(out uint subresource);
        [PreserveSig] int GetUnknown(ref Guid key, ref Guid interfaceId, out IntPtr value);
        [PreserveSig] int SetUnknown(ref Guid key, IntPtr value);
    }
}
