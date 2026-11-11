using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32.SafeHandles;

namespace MyCapture.App.Pinning;

internal delegate void NativePinPixelInitializer(Span<byte> row, int y);

/// <summary>
/// One immutable native PBGRA source, with deterministic ownership and worker read leases. Several owners
/// (the native window, and the pin's picture once it dropped its bitmap) can hold the same pixels through
/// <see cref="Share"/>; the memory goes when the last owner is disposed, or with the last reference to it.
/// </summary>
internal sealed class NativePinPixelSource : IDisposable
{
    private Allocation? _allocation;
    internal int Width { get; }
    internal int Height { get; }
    internal int Stride { get; }
    internal long ByteLength { get; }

    /// <summary>
    /// The bitmap's own format when the pixels are its exact bytes after reading: read without a conversion,
    /// or <c>Bgr24</c>, whose conversion only adds an opaque alpha. Otherwise null.
    /// </summary>
    internal PixelFormat? ExactFormat { get; }

    /// <summary>
    /// The fourth byte of every pixel as read, before normalization, when it was the same everywhere
    /// (a <c>Bgr32</c> padding value, or an alpha), otherwise -1.
    /// </summary>
    internal int UniformFourthByte { get; }

    /// <summary>
    /// The pixels are a lossless copy of the bitmap they came from (design 9, increment 5): <c>Bgr32</c> with
    /// one padding value, <c>Bgra32</c>/<c>Pbgra32</c> with every alpha 255, or <c>Bgr24</c> (a decoded RGB PNG,
    /// such as a copied capture pinned again). <see cref="CreateBitmap"/> then gives back the same bytes in the
    /// same format.
    /// </summary>
    internal bool IsExactOpaqueCopy => ExactFormat is { } format && UniformFourthByte >= 0
        && (format == PixelFormats.Bgr32
            || (UniformFourthByte == 255
                && (format == PixelFormats.Bgra32 || format == PixelFormats.Pbgra32 || format == PixelFormats.Bgr24)));

    internal static int LiveAllocationCount => Volatile.Read(ref Allocation.LiveCount);
    internal static long LiveBytes => Interlocked.Read(ref Allocation.LiveBytes);

    private NativePinPixelSource(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        Width = width; Height = height; Stride = checked(width * 4);
        ByteLength = checked((long)Stride * height);
        UniformFourthByte = -1;
        _allocation = new Allocation(ByteLength);
    }

    private NativePinPixelSource(NativePinPixelSource shape, PixelFormat? exactFormat, int uniformFourthByte, Allocation allocation)
    {
        Width = shape.Width; Height = shape.Height; Stride = shape.Stride; ByteLength = shape.ByteLength;
        ExactFormat = exactFormat; UniformFourthByte = uniformFourthByte;
        _allocation = allocation;
    }

    internal static unsafe NativePinPixelSource CreateFilled(int width, int height, NativePinPixelInitializer fill)
    {
        ArgumentNullException.ThrowIfNull(fill);
        var result = new NativePinPixelSource(width, height);
        try
        {
            using Lease lease = result.AcquireLease();
            for (int y = 0; y < height; y++)
            {
                Span<byte> row = new((byte*)lease.Pointer + ((long)y * lease.Stride), lease.Stride);
                row.Clear();
                fill(row, y);
                Normalize(row, PixelFormats.Pbgra32);
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    internal static unsafe NativePinPixelSource FromBitmap(BitmapSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.IsFrozen) throw new ArgumentException("A frozen source bitmap is required.", nameof(source));
        // A frozen bitmap's pixels can be read on any thread, but a frame decoded on another thread keeps a
        // decoder bound to that thread, and a WPF conversion of the frame would touch it. So the pixels are
        // read in the bitmap's own format; Bgr24 (a decoded RGB PNG) is widened here.
        if (source.Format == PixelFormats.Bgr24) return FromBgr24(source);
        BitmapSource readable = source;
        bool exact = true;
        if (source.Format != PixelFormats.Pbgra32 && source.Format != PixelFormats.Bgra32 && source.Format != PixelFormats.Bgr32)
        {
            readable = new FormatConvertedBitmap(FreeThreadedCopy(source), PixelFormats.Pbgra32, null, 0);
            readable.Freeze();
            exact = false;
        }
        var result = new NativePinPixelSource(readable.PixelWidth, readable.PixelHeight);
        try
        {
            FourthByte seen = FourthByte.Start;
            using (Lease lease = result.AcquireLease())
            {
                for (int y = 0; y < result.Height; y++)
                {
                    byte* row = (byte*)lease.Pointer + ((long)y * lease.Stride);
                    readable.CopyPixels(new Int32Rect(0, y, result.Width, 1), (IntPtr)row, lease.Stride, lease.Stride);
                    Normalize(new Span<byte>(row, lease.Stride), readable.Format, ref seen);
                }
            }
            if (!exact) return result;
            // The same allocation, now described: the one normalization pass established what it holds.
            Allocation allocation = Interlocked.Exchange(ref result._allocation, null)!;
            return new NativePinPixelSource(result, source.Format, seen.Uniform, allocation);
        }
        catch { result.Dispose(); throw; }
    }

    // B, G, R and an opaque fourth byte: what a WPF conversion of Bgr24 to Pbgra32 gives, without one.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static unsafe NativePinPixelSource FromBgr24(BitmapSource source)
    {
        var result = new NativePinPixelSource(source.PixelWidth, source.PixelHeight);
        int packedStride = checked(result.Width * 3);
        // Each packed row is read into this buffer and widened into the source's row.
        IntPtr row = Marshal.AllocHGlobal(packedStride);
        try
        {
            using (Lease lease = result.AcquireLease())
            {
                for (int y = 0; y < result.Height; y++)
                {
                    source.CopyPixels(new Int32Rect(0, y, result.Width, 1), row, packedStride, packedStride);
                    byte* from = (byte*)row;
                    uint* to = (uint*)((byte*)lease.Pointer + ((long)y * lease.Stride));
                    for (int x = 0; x < result.Width; x++, from += 3)
                        to[x] = 0xFF000000u | ((uint)from[2] << 16) | ((uint)from[1] << 8) | from[0];
                }
            }
            Allocation allocation = Interlocked.Exchange(ref result._allocation, null)!;
            return new NativePinPixelSource(result, PixelFormats.Bgr24, 255, allocation);
        }
        catch { result.Dispose(); throw; }
        finally { Marshal.FreeHGlobal(row); }
    }

    /// <summary>
    /// The same pixels, format and palette in a new bitmap that is bound to no thread, for a conversion that
    /// must not touch the source's decoder. A bitmap that is not a decoded frame is used as it is.
    /// </summary>
    private static BitmapSource FreeThreadedCopy(BitmapSource source)
    {
        if (source is not BitmapFrame) return source;
        int stride = checked((source.PixelWidth * source.Format.BitsPerPixel + 7) / 8);
        int bytes = checked(stride * source.PixelHeight);
        IntPtr buffer = Marshal.AllocHGlobal(bytes);
        try
        {
            source.CopyPixels(Int32Rect.Empty, buffer, bytes, stride);
            BitmapSource copy = BitmapSource.Create(source.PixelWidth, source.PixelHeight, source.DpiX, source.DpiY,
                source.Format, source.Palette, buffer, bytes, stride);
            copy.Freeze();
            return copy;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>
    /// Another owner of the same pixels. Each owner is disposed on its own; the memory stays until the last
    /// one is, so the pin's picture can outlive the window that drew it, and the reverse.
    /// </summary>
    internal NativePinPixelSource Share()
    {
        Allocation allocation = Volatile.Read(ref _allocation) ?? throw new ObjectDisposedException(nameof(NativePinPixelSource));
        allocation.AddOwner();
        return new NativePinPixelSource(this, ExactFormat, UniformFourthByte, allocation);
    }

    /// <summary>
    /// Tells the garbage collector about these unmanaged bytes, as a WPF bitmap does about its own: once the
    /// pin's picture holds them in place of its bitmap they may go only with the last reference to them.
    /// </summary>
    internal void AddMemoryPressure() =>
        (Volatile.Read(ref _allocation) ?? throw new ObjectDisposedException(nameof(NativePinPixelSource))).AddPressure();

    /// <summary>
    /// The bitmap these pixels were copied from, in its own format, resolution and bytes. Only for an
    /// <see cref="IsExactOpaqueCopy"/>; one copy of the pixels, on any thread; the result is frozen.
    /// </summary>
    internal unsafe BitmapSource CreateBitmap(double dpiX, double dpiY)
    {
        if (!IsExactOpaqueCopy || ExactFormat is not { } format)
            throw new InvalidOperationException("Only an exact copy of an opaque bitmap can give it back.");
        using Lease lease = AcquireLease();
        int bytes = checked((int)lease.ByteLength);
        BitmapSource result;
        if (format == PixelFormats.Bgr24)
        {
            int packedStride = checked(Width * 3);
            int packed = checked(packedStride * Height);
            IntPtr buffer = Marshal.AllocHGlobal(packed);
            try
            {
                for (int y = 0; y < Height; y++)
                {
                    byte* from = (byte*)lease.Pointer + ((long)y * Stride);
                    byte* to = (byte*)buffer + ((long)y * packedStride);
                    for (int x = 0; x < Width; x++, from += 4, to += 3)
                    {
                        to[0] = from[0]; to[1] = from[1]; to[2] = from[2];
                    }
                }
                result = BitmapSource.Create(Width, Height, dpiX, dpiY, format, null, buffer, packed, packedStride);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        else if (format == PixelFormats.Bgr32 && UniformFourthByte != 255)
        {
            // Normalization set the padding to 255; the bitmap had another value, the same everywhere.
            IntPtr buffer = Marshal.AllocHGlobal(bytes);
            try
            {
                Buffer.MemoryCopy((void*)lease.Pointer, (void*)buffer, bytes, bytes);
                Span<uint> words = new((void*)buffer, bytes / 4);
                uint padding = (uint)UniformFourthByte << 24;
                for (int index = 0; index < words.Length; index++) words[index] = (words[index] & 0x00FFFFFFu) | padding;
                result = BitmapSource.Create(Width, Height, dpiX, dpiY, format, null, buffer, bytes, Stride);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        else
        {
            result = BitmapSource.Create(Width, Height, dpiX, dpiY, format, null, lease.Pointer, bytes, Stride);
        }
        result.Freeze();
        return result;
    }

    internal Lease AcquireLease()
    {
        Allocation allocation = Volatile.Read(ref _allocation) ?? throw new ObjectDisposedException(nameof(NativePinPixelSource));
        return new Lease(allocation, Width, Height, Stride);
    }

    public void Dispose() => Interlocked.Exchange(ref _allocation, null)?.ReleaseOwner();

    /// <summary>What one normalization pass saw in the fourth byte of the pixels it read.</summary>
    internal struct FourthByte
    {
        internal byte And;
        internal byte Or;

        internal static FourthByte Start => new() { And = 0xFF, Or = 0 };

        /// <summary>The value every pixel had, or -1 when they differed.</summary>
        internal readonly int Uniform => And == Or ? And : -1;
    }

    internal static void Normalize(Span<byte> row, PixelFormat format)
    {
        FourthByte seen = FourthByte.Start;
        Normalize(row, format, ref seen);
    }

    // Every source pixel passes through here once before a pin's first frame. As a plain loop the
    // premultiplied clamp of a 4K source took 8 ms once the runtime had optimized it, and the
    // first sources of a process ran before that (36 ms instead of 7 ms for each of the first
    // three 1600x1000 probe sources). So this is compiled fully at once, and the two formats a
    // capture arrives in are done sixteen bytes at a time (1 ms for the same 4K clamp). The same
    // pass keeps the AND and the OR of the fourth byte as read, which say whether the bitmap was opaque.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void Normalize(Span<byte> row, PixelFormat format, ref FourthByte seen)
    {
        int x = 0;
        byte and = seen.And, or = seen.Or;
        if (format == PixelFormats.Bgr32)
        {
            if (Vector128.IsHardwareAccelerated)
            {
                Vector128<byte> opaque = Vector128.Create(0xFF000000u).AsByte();
                Vector128<byte> all = Vector128<byte>.AllBitsSet, any = Vector128<byte>.Zero;
                for (; x <= row.Length - 16; x += 16)
                {
                    Vector128<byte> pixels = Vector128.Create<byte>(row.Slice(x, 16));
                    all &= pixels;
                    any |= pixels;
                    (pixels | opaque).CopyTo(row.Slice(x, 16));
                }
                and &= FourthBytes(all, and: true);
                or |= FourthBytes(any, and: false);
            }
            for (; x < row.Length; x += 4)
            {
                and &= row[x + 3];
                or |= row[x + 3];
                row[x + 3] = 255;
            }
        }
        else if (format == PixelFormats.Bgra32)
            for (; x < row.Length; x += 4)
            {
                int alpha = row[x + 3];
                and &= (byte)alpha;
                or |= (byte)alpha;
                row[x] = (byte)((row[x] * alpha + 127) / 255);
                row[x + 1] = (byte)((row[x + 1] * alpha + 127) / 255);
                row[x + 2] = (byte)((row[x + 2] * alpha + 127) / 255);
            }
        else
        {
            if (Vector128.IsHardwareAccelerated)
            {
                // Each pixel's alpha in all four of its bytes; the alpha byte is then its own minimum.
                Vector128<byte> alphaOfPixel = Vector128.Create((byte)3, 3, 3, 3, 7, 7, 7, 7, 11, 11, 11, 11, 15, 15, 15, 15);
                Vector128<byte> all = Vector128<byte>.AllBitsSet, any = Vector128<byte>.Zero;
                for (; x <= row.Length - 16; x += 16)
                {
                    Vector128<byte> pixels = Vector128.Create<byte>(row.Slice(x, 16));
                    all &= pixels;
                    any |= pixels;
                    Vector128.Min(pixels, Vector128.Shuffle(pixels, alphaOfPixel)).CopyTo(row.Slice(x, 16));
                }
                and &= FourthBytes(all, and: true);
                or |= FourthBytes(any, and: false);
            }
            for (; x < row.Length; x += 4)
            {
                and &= row[x + 3];
                or |= row[x + 3];
                row[x] = Math.Min(row[x], row[x + 3]);
                row[x + 1] = Math.Min(row[x + 1], row[x + 3]);
                row[x + 2] = Math.Min(row[x + 2], row[x + 3]);
            }
        }
        seen.And = and;
        seen.Or = or;
    }

    private static byte FourthBytes(Vector128<byte> lanes, bool and) => and
        ? (byte)(lanes.GetElement(3) & lanes.GetElement(7) & lanes.GetElement(11) & lanes.GetElement(15))
        : (byte)(lanes.GetElement(3) | lanes.GetElement(7) | lanes.GetElement(11) | lanes.GetElement(15));

    internal sealed class Lease : IDisposable
    {
        private Allocation? _allocation;
        private readonly IntPtr _pointer;
        internal IntPtr Pointer => Volatile.Read(ref _allocation) is not null ? _pointer : throw new ObjectDisposedException(nameof(Lease));
        internal int Width { get; }
        internal int Height { get; }
        internal int Stride { get; }
        internal long ByteLength => checked((long)Stride * Height);

        internal Lease(Allocation allocation, int width, int height, int stride)
        {
            bool acquired = false;
            allocation.DangerousAddRef(ref acquired);
            if (!acquired) throw new ObjectDisposedException(nameof(NativePinPixelSource));
            _allocation = allocation;
            _pointer = allocation.DangerousGetHandle();
            Width = width; Height = height; Stride = stride;
        }

        public void Dispose() => Interlocked.Exchange(ref _allocation, null)?.DangerousRelease();
    }

    internal sealed class Allocation : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal static int LiveCount;
        internal static long LiveBytes;
        private readonly long _bytes;
        private int _owners = 1;
        private int _pressure;

        internal Allocation(long bytes) : base(ownsHandle: true)
        {
            _bytes = bytes;
            SetHandle(Marshal.AllocHGlobal(checked((nint)bytes)));
            Interlocked.Increment(ref LiveCount);
            Interlocked.Add(ref LiveBytes, bytes);
        }

        // Called only by an owner that still holds its share, so the count is never zero here.
        internal void AddOwner() => Interlocked.Increment(ref _owners);

        internal void ReleaseOwner()
        {
            if (Interlocked.Decrement(ref _owners) == 0) Dispose();
        }

        internal void AddPressure()
        {
            if (Interlocked.Exchange(ref _pressure, 1) == 0) GC.AddMemoryPressure(_bytes);
        }

        protected override bool ReleaseHandle()
        {
            Marshal.FreeHGlobal(handle);
            if (Interlocked.Exchange(ref _pressure, 0) != 0) GC.RemoveMemoryPressure(_bytes);
            Interlocked.Decrement(ref LiveCount);
            Interlocked.Add(ref LiveBytes, -_bytes);
            return true;
        }
    }
}
