using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace MyCapture.App.Recording;

/// <summary>
/// Encodes one 32-bit BGRA image as a complete single-frame GIF by calling the Windows Imaging
/// Component directly, without WPF. The output is byte-identical to
/// <see cref="AnimatedGifWriter.EncodeFrame"/> of the same pixels wrapped by
/// <c>BitmapSource.Create(..., PixelFormats.Bgra32, null, pixels, stride)</c> at 96 dpi.
/// </summary>
/// <remarks>
/// <para>
/// The calls are the ones WPF makes, through the same <c>windowscodecs.dll</c> entry points
/// (PresentationCore <c>CachedBitmap</c>, <c>BitmapEncoder.Save</c>/<c>SaveFrame</c> and
/// <c>GifBitmapEncoder.SetupFrame</c>): a bitmap copied from memory with a 96 dpi resolution;
/// <c>CreateEncoder</c>(GIF, Microsoft vendor); <c>Initialize</c>(stream, no cache);
/// <c>CreateNewFrame</c>; frame <c>Initialize</c> with the options bag untouched; <c>SetSize</c>;
/// <c>SetResolution</c>(96, 96); <c>WICSetEncoderFormat</c> without a palette, which lets the
/// codec choose its pixel format and quantise; <c>WriteSource</c> with an empty rectangle;
/// frame <c>Commit</c>; encoder <c>Commit</c>. No thumbnail, palette, metadata or colour context
/// is written, as WPF writes none for this source.
/// </para>
/// <para>
/// Threading: the imaging factory is created once per process and shared, exactly as WPF's
/// free-threaded <c>FactoryMaker</c> shares its factory; the factory and the GIF encoder
/// classes are registered with <c>ThreadingModel=Both</c>. Every call creates its own bitmap,
/// encoder, frame and stream, so no per-image object is ever touched by two threads. Unlike
/// WPF imaging this may run on MTA worker threads; concurrent use was verified by
/// <c>WicGifFrameEncoderTests</c> (see <see cref="GifExportEngine"/>).
/// </para>
/// </remarks>
internal static class WicGifFrameEncoder
{
    private const string WindowsCodecs = "windowscodecs.dll";
    private const uint SdkVersion = 0x0236; // WINCODEC_SDK_VERSION, as WPF passes
    private const int NoCache = 2;          // WICBitmapEncoderNoCache
    private const double Dpi = 96;

    private static readonly Guid ContainerFormatGif = new("1f8a5601-7d4d-4cbd-9c82-1bc8d4eeb9a5");
    private static readonly Guid VendorMicrosoft = new("f0e749ca-edef-4589-a73a-ee0e626a2a2b");
    private static readonly Guid PixelFormat32bppBgra = new("6fddc324-4e03-4bfe-b185-3d77768dc90f");
    private static readonly object FactoryGate = new();
    private static IntPtr s_factory;
    private static int s_failures;
    private static volatile bool s_unavailable;

    /// <summary>
    /// False once the WIC entry points could not be loaded; callers then use the WPF encoder.
    /// </summary>
    internal static bool IsAvailable => !s_unavailable;

    /// <summary>Direct encodes that failed in this process; tests assert that none did.</summary>
    internal static int Failures => Volatile.Read(ref s_failures);

    /// <summary>
    /// Returns the single-frame GIF of a <paramref name="width"/> x <paramref name="height"/>
    /// BGRA image. Thread-safe. Throws <see cref="COMException"/> when a WIC call fails and
    /// <see cref="DllNotFoundException"/> or <see cref="EntryPointNotFoundException"/> when the
    /// codec entry points are missing.
    /// </summary>
    internal static byte[] Encode(byte[] pixels, int width, int height, int stride)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if (stride < checked(width * 4) || pixels.Length < checked((stride * (height - 1)) + (width * 4)))
        {
            throw new ArgumentException("The pixel buffer is too small for the image and stride.", nameof(pixels));
        }

        try
        {
            return EncodeCore(pixels, width, height, stride);
        }
        catch (Exception ex) when (IsInteropFailure(ex))
        {
            _ = Interlocked.Increment(ref s_failures);
            if (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                s_unavailable = true;
            }

            throw;
        }
    }

    /// <summary>True for the failures a WPF fallback can stand in for.</summary>
    internal static bool IsInteropFailure(Exception ex) =>
        ex is COMException or DllNotFoundException or EntryPointNotFoundException;

    private static byte[] EncodeCore(byte[] pixels, int width, int height, int stride)
    {
        IntPtr factory = Factory();
        IntPtr bitmap = IntPtr.Zero;
        IntPtr encoder = IntPtr.Zero;
        IntPtr stream = IntPtr.Zero;
        IntPtr frame = IntPtr.Zero;
        IntPtr options = IntPtr.Zero;
        IntPtr converted = IntPtr.Zero;
        var output = new OutputStream(EstimateCapacity(width, height));
        try
        {
            // CachedBitmap: the bitmap owns a copy of the pixels, so the array is pinned only
            // for this call.
            Guid format = PixelFormat32bppBgra;
            GCHandle pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                Check(
                    CreateBitmapFromMemory(
                        factory,
                        (uint)width,
                        (uint)height,
                        ref format,
                        (uint)stride,
                        (uint)pixels.Length,
                        pin.AddrOfPinnedObject(),
                        out bitmap),
                    "IWICImagingFactory::CreateBitmapFromMemory");
            }
            finally
            {
                pin.Free();
            }

            Check(BitmapSetResolution(bitmap, Dpi, Dpi), "IWICBitmap::SetResolution");

            // BitmapEncoder.Save and SaveFrame.
            Guid container = ContainerFormatGif;
            Guid vendor = VendorMicrosoft;
            Check(CreateEncoder(factory, ref container, ref vendor, out encoder), "IWICImagingFactory::CreateEncoder");
            stream = Marshal.GetComInterfaceForObject<OutputStream, IStream>(output);
            Check(EncoderInitialize(encoder, stream, NoCache), "IWICBitmapEncoder::Initialize");
            Check(CreateNewFrame(encoder, out frame, out options), "IWICBitmapEncoder::CreateNewFrame");
            Check(FrameInitialize(frame, options), "IWICBitmapFrameEncode::Initialize");
            Check(FrameSetSize(frame, width, height), "IWICBitmapFrameEncode::SetSize");
            Check(FrameSetResolution(frame, Dpi, Dpi), "IWICBitmapFrameEncode::SetResolution");
            Check(SetEncoderFormat(bitmap, IntPtr.Zero, frame, out converted), "WICSetEncoderFormat");
            var everything = default(WicRect);
            Check(FrameWriteSource(frame, converted, ref everything), "IWICBitmapFrameEncode::WriteSource");
            Check(FrameCommit(frame), "IWICBitmapFrameEncode::Commit");
            Check(EncoderCommit(encoder), "IWICBitmapEncoder::Commit");
            return output.ToArray();
        }
        finally
        {
            Release(converted);
            Release(options);
            Release(frame);
            Release(encoder);
            Release(stream);
            Release(bitmap);
        }
    }

    private static IntPtr Factory()
    {
        IntPtr factory = Volatile.Read(ref s_factory);
        if (factory != IntPtr.Zero)
        {
            return factory;
        }

        lock (FactoryGate)
        {
            if (s_factory == IntPtr.Zero)
            {
                Check(CreateImagingFactory(SdkVersion, out IntPtr created), "WICCreateImagingFactory");
                Volatile.Write(ref s_factory, created);
            }

            return s_factory;
        }
    }

    /// <summary>Typical encoded size: a little under one byte per pixel plus the colour table.</summary>
    private static int EstimateCapacity(int width, int height) =>
        (int)Math.Min(64L * 1024 * 1024, ((long)width * height) + 1024);

    private static void Check(int hresult, string call)
    {
        if (hresult < 0)
        {
            throw new COMException($"{call} failed with 0x{hresult:X8}.", hresult);
        }
    }

    private static void Release(IntPtr unknown)
    {
        if (unknown != IntPtr.Zero)
        {
            _ = Marshal.Release(unknown);
        }
    }

    [DllImport(WindowsCodecs, EntryPoint = "WICCreateImagingFactory_Proxy", ExactSpelling = true)]
    private static extern int CreateImagingFactory(uint sdkVersion, out IntPtr factory);

    [DllImport(WindowsCodecs, EntryPoint = "IWICImagingFactory_CreateBitmapFromMemory_Proxy", ExactSpelling = true)]
    private static extern int CreateBitmapFromMemory(
        IntPtr factory,
        uint width,
        uint height,
        ref Guid pixelFormat,
        uint stride,
        uint bufferSize,
        IntPtr buffer,
        out IntPtr bitmap);

    [DllImport(WindowsCodecs, EntryPoint = "IWICBitmap_SetResolution_Proxy", ExactSpelling = true)]
    private static extern int BitmapSetResolution(IntPtr bitmap, double dpiX, double dpiY);

    [DllImport(WindowsCodecs, EntryPoint = "IWICImagingFactory_CreateEncoder_Proxy", ExactSpelling = true)]
    private static extern int CreateEncoder(IntPtr factory, ref Guid containerFormat, ref Guid vendor, out IntPtr encoder);

    [DllImport(WindowsCodecs, EntryPoint = "IWICBitmapEncoder_Initialize_Proxy", ExactSpelling = true)]
    private static extern int EncoderInitialize(IntPtr encoder, IntPtr stream, int cacheOption);

    [DllImport(WindowsCodecs, EntryPoint = "IWICBitmapEncoder_CreateNewFrame_Proxy", ExactSpelling = true)]
    private static extern int CreateNewFrame(IntPtr encoder, out IntPtr frame, out IntPtr options);

    [DllImport(WindowsCodecs, EntryPoint = "IWICBitmapEncoder_Commit_Proxy", ExactSpelling = true)]
    private static extern int EncoderCommit(IntPtr encoder);

    [DllImport(WindowsCodecs, EntryPoint = "IWICBitmapFrameEncode_Initialize_Proxy", ExactSpelling = true)]
    private static extern int FrameInitialize(IntPtr frame, IntPtr options);

    [DllImport(WindowsCodecs, EntryPoint = "IWICBitmapFrameEncode_SetSize_Proxy", ExactSpelling = true)]
    private static extern int FrameSetSize(IntPtr frame, int width, int height);

    [DllImport(WindowsCodecs, EntryPoint = "IWICBitmapFrameEncode_SetResolution_Proxy", ExactSpelling = true)]
    private static extern int FrameSetResolution(IntPtr frame, double dpiX, double dpiY);

    [DllImport(WindowsCodecs, EntryPoint = "WICSetEncoderFormat_Proxy", ExactSpelling = true)]
    private static extern int SetEncoderFormat(IntPtr source, IntPtr palette, IntPtr frame, out IntPtr converted);

    [DllImport(WindowsCodecs, EntryPoint = "IWICBitmapFrameEncode_WriteSource_Proxy", ExactSpelling = true)]
    private static extern int FrameWriteSource(IntPtr frame, IntPtr source, ref WicRect rectangle);

    [DllImport(WindowsCodecs, EntryPoint = "IWICBitmapFrameEncode_Commit_Proxy", ExactSpelling = true)]
    private static extern int FrameCommit(IntPtr frame);

    [StructLayout(LayoutKind.Sequential)]
    private struct WicRect
    {
        internal int X;
        internal int Y;
        internal int Width;
        internal int Height;
    }

    /// <summary>
    /// The encoder's destination: a seekable in-memory <see cref="IStream"/>, like the managed
    /// stream WPF hands the codec. Exposed to WIC through a COM-callable wrapper.
    /// </summary>
    private sealed class OutputStream(int capacity) : IStream
    {
        private const int StreamType = 2; // STGTY_STREAM
        private readonly MemoryStream _buffer = new(capacity);

        internal byte[] ToArray() => _buffer.ToArray();

        public void Read(byte[] pv, int cb, IntPtr pcbRead)
        {
            int read = _buffer.Read(pv, 0, cb);
            if (pcbRead != IntPtr.Zero)
            {
                Marshal.WriteInt32(pcbRead, read);
            }
        }

        public void Write(byte[] pv, int cb, IntPtr pcbWritten)
        {
            _buffer.Write(pv, 0, cb);
            if (pcbWritten != IntPtr.Zero)
            {
                Marshal.WriteInt32(pcbWritten, cb);
            }
        }

        public void Seek(long dlibMove, int dwOrigin, IntPtr plibNewPosition)
        {
            long position = _buffer.Seek(dlibMove, (SeekOrigin)dwOrigin);
            if (plibNewPosition != IntPtr.Zero)
            {
                Marshal.WriteInt64(plibNewPosition, position);
            }
        }

        public void SetSize(long libNewSize) => _buffer.SetLength(libNewSize);

        public void CopyTo(IStream pstm, long cb, IntPtr pcbRead, IntPtr pcbWritten) =>
            throw new NotSupportedException();

        public void Commit(int grfCommitFlags)
        {
        }

        public void Revert() => throw new NotSupportedException();

        public void LockRegion(long libOffset, long cb, int dwLockType) => throw new NotSupportedException();

        public void UnlockRegion(long libOffset, long cb, int dwLockType) => throw new NotSupportedException();

        public void Stat(out STATSTG pstatstg, int grfStatFlag) =>
            pstatstg = new STATSTG { type = StreamType, cbSize = _buffer.Length };

        public void Clone(out IStream ppstm) => throw new NotSupportedException();
    }
}
