using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32.SafeHandles;

namespace MyCapture.App.Pinning;

internal delegate void NativePinPixelInitializer(Span<byte> row, int y);

/// <summary>One immutable native PBGRA source, with deterministic ownership and worker read leases.</summary>
internal sealed class NativePinPixelSource : IDisposable
{
    private Allocation? _allocation;
    internal int Width { get; }
    internal int Height { get; }
    internal int Stride { get; }
    internal long ByteLength { get; }
    internal static int LiveAllocationCount => Volatile.Read(ref Allocation.LiveCount);
    internal static long LiveBytes => Interlocked.Read(ref Allocation.LiveBytes);

    private NativePinPixelSource(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        Width = width; Height = height; Stride = checked(width * 4);
        ByteLength = checked((long)Stride * height);
        _allocation = new Allocation(ByteLength);
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
        BitmapSource readable = source;
        if (source.Format != PixelFormats.Pbgra32 && source.Format != PixelFormats.Bgra32 && source.Format != PixelFormats.Bgr32)
        {
            readable = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
            readable.Freeze();
        }
        var result = new NativePinPixelSource(readable.PixelWidth, readable.PixelHeight);
        try
        {
            using Lease lease = result.AcquireLease();
            for (int y = 0; y < result.Height; y++)
            {
                byte* row = (byte*)lease.Pointer + ((long)y * lease.Stride);
                readable.CopyPixels(new Int32Rect(0, y, result.Width, 1), (IntPtr)row, lease.Stride, lease.Stride);
                Normalize(new Span<byte>(row, lease.Stride), readable.Format);
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    internal Lease AcquireLease()
    {
        Allocation allocation = Volatile.Read(ref _allocation) ?? throw new ObjectDisposedException(nameof(NativePinPixelSource));
        return new Lease(allocation, Width, Height, Stride);
    }

    public void Dispose() => Interlocked.Exchange(ref _allocation, null)?.Dispose();

    // Every source pixel passes through here once before a pin's first frame. As a plain loop the
    // premultiplied clamp of a 4K source took 8 ms once the runtime had optimized it, and the
    // first sources of a process ran before that (36 ms instead of 7 ms for each of the first
    // three 1600x1000 probe sources). So this is compiled fully at once, and the two formats a
    // capture arrives in are done sixteen bytes at a time (1 ms for the same 4K clamp).
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void Normalize(Span<byte> row, PixelFormat format)
    {
        int x = 0;
        if (format == PixelFormats.Bgr32)
        {
            if (Vector128.IsHardwareAccelerated)
            {
                Vector128<byte> opaque = Vector128.Create(0xFF000000u).AsByte();
                for (; x <= row.Length - 16; x += 16)
                    (Vector128.Create<byte>(row.Slice(x, 16)) | opaque).CopyTo(row.Slice(x, 16));
            }
            for (; x < row.Length; x += 4) row[x + 3] = 255;
        }
        else if (format == PixelFormats.Bgra32)
            for (; x < row.Length; x += 4)
            {
                int alpha = row[x + 3];
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
                for (; x <= row.Length - 16; x += 16)
                {
                    Vector128<byte> pixels = Vector128.Create<byte>(row.Slice(x, 16));
                    Vector128.Min(pixels, Vector128.Shuffle(pixels, alphaOfPixel)).CopyTo(row.Slice(x, 16));
                }
            }
            for (; x < row.Length; x += 4)
            {
                row[x] = Math.Min(row[x], row[x + 3]);
                row[x + 1] = Math.Min(row[x + 1], row[x + 3]);
                row[x + 2] = Math.Min(row[x + 2], row[x + 3]);
            }
        }
    }

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

        internal Allocation(long bytes) : base(ownsHandle: true)
        {
            _bytes = bytes;
            SetHandle(Marshal.AllocHGlobal(checked((nint)bytes)));
            Interlocked.Increment(ref LiveCount);
            Interlocked.Add(ref LiveBytes, bytes);
        }

        protected override bool ReleaseHandle()
        {
            Marshal.FreeHGlobal(handle);
            Interlocked.Decrement(ref LiveCount);
            Interlocked.Add(ref LiveBytes, -_bytes);
            return true;
        }
    }
}
