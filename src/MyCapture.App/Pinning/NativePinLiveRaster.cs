using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MyCapture.App.Pinning;

internal enum NativePinLiveMode { GdiNearest, IntegerBilinear }

/// <summary>Two measured live candidates. Neither creates a managed full-frame image.</summary>
internal static class NativePinLiveRaster
{
    internal static double Write(NativePinLiveMode mode, NativePinPixelSource.Lease source,
        IntPtr destinationDc, IntPtr destination, int width, int height, int stride,
        double contentScale = 1, CancellationToken cancellationToken = default) =>
        Write(mode, source, destinationDc, destination, NativePinRaster.View.Whole(width, height), stride,
            contentScale, cancellationToken);

    /// <summary>
    /// Scales the source over the whole surface but writes only the pixels of <paramref name="view"/>,
    /// which is all the destination holds. The cost follows the view's area, not the surface's.
    /// </summary>
    internal static double Write(NativePinLiveMode mode, NativePinPixelSource.Lease source,
        IntPtr destinationDc, IntPtr destination, NativePinRaster.View view, int stride,
        double contentScale = 1, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (destination == IntPtr.Zero) throw new ArgumentException("A destination buffer is required.", nameof(destination));
        view.Validate();
        if (stride < checked(view.Width * 4)) throw new ArgumentOutOfRangeException(nameof(stride));
        if (!double.IsFinite(contentScale) || contentScale <= 0 || contentScale > 1)
            throw new ArgumentOutOfRangeException(nameof(contentScale));
        cancellationToken.ThrowIfCancellationRequested();
        long started = Stopwatch.GetTimestamp();
        if (mode == NativePinLiveMode.GdiNearest)
            WriteGdi(source, destinationDc, destination, view, stride, contentScale);
        else if (mode == NativePinLiveMode.IntegerBilinear)
            WriteBilinear(source, destination, view, stride, contentScale, cancellationToken);
        else throw new ArgumentOutOfRangeException(nameof(mode));
        cancellationToken.ThrowIfCancellationRequested();
        return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    private static unsafe void WriteGdi(NativePinPixelSource.Lease source, IntPtr destinationDc,
        IntPtr destination, NativePinRaster.View view, int stride, double contentScale)
    {
        if (destinationDc == IntPtr.Zero) throw new ArgumentException("An owner-thread display DC is required.", nameof(destinationDc));
        var info = new BitmapInfo
        {
            Size = (uint)Marshal.SizeOf<BitmapInfo>(), Width = source.Width, Height = -source.Height,
            Planes = 1, BitCount = 32,
        };
        // COLORONCOLOR/SRCCOPY samples complete PBGRA pixels; HALFTONE is deliberately not used
        // because GDI's colour interpolation does not promise premultiplied alpha semantics.
        int previous = SetStretchBltMode(destinationDc, 3);
        if (previous == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "SetStretchBltMode failed.");
        try
        {
            int width = view.SurfaceWidth, height = view.SurfaceHeight;
            int left = (int)Math.Floor(width * (1 - contentScale) / 2);
            int top = (int)Math.Floor(height * (1 - contentScale) / 2);
            int targetWidth = Math.Max(1, width - (2 * left));
            int targetHeight = Math.Max(1, height - (2 * top));
            // The picture's columns inside the view, in the view's own coordinates.
            int pictureFrom = Math.Clamp(left - view.Left, 0, view.Width);
            int pictureTo = Math.Clamp(left + targetWidth - view.Left, 0, view.Width);
            bool anyRow = false;
            // Clear only the outside strips during exit, never the whole live frame. Rounding
            // the draw rectangle outward ensures every partially clipped image pixel was filled.
            if (left != 0 || top != 0)
                for (int y = 0; y < view.Height; y++)
                {
                    byte* row = (byte*)destination + ((long)y * stride);
                    int surfaceY = y + view.Top;
                    if (surfaceY < top || surfaceY >= top + targetHeight) new Span<byte>(row, view.Width * 4).Clear();
                    else
                    {
                        anyRow = true;
                        new Span<byte>(row, pictureFrom * 4).Clear();
                        new Span<byte>(row + (pictureTo * 4), (view.Width - pictureTo) * 4).Clear();
                    }
                }
            else anyRow = true;
            // A view that only shows the cleared margin of a shrinking picture has nothing to stretch.
            if (!anyRow || pictureTo <= pictureFrom) return;
            // The destination rectangle is the whole picture; GDI clips it to the selected DIB, so
            // a pixel of the view is the same pixel the whole surface would have at that place.
            int copied = StretchDIBits(destinationDc, left - view.Left, top - view.Top, targetWidth, targetHeight,
                0, 0, source.Width, source.Height, source.Pointer, ref info, 0, 0x00CC0020);
            if (copied == 0 || copied == -1) throw new Win32Exception(Marshal.GetLastWin32Error(), "StretchDIBits failed.");
            if (!GdiFlush()) throw new Win32Exception(Marshal.GetLastWin32Error(), "GdiFlush failed.");
        }
        finally { _ = SetStretchBltMode(destinationDc, previous); }
    }

    private static unsafe void WriteBilinear(NativePinPixelSource.Lease source, IntPtr destination,
        NativePinRaster.View view, int stride, double contentScale, CancellationToken cancellationToken)
    {
        int width = view.Width, height = view.Height;
        var first = new int[width];
        var second = new int[width];
        var weights = new uint[width];
        double ratioX = view.SurfaceWidth * contentScale / source.Width;
        double ratioY = view.SurfaceHeight * contentScale / source.Height;
        double originX = view.SurfaceWidth * (1 - contentScale) / 2;
        double originY = view.SurfaceHeight * (1 - contentScale) / 2;
        for (int x = 0; x < width; x++)
        {
            double position = ((x + view.Left + .5 - originX) / ratioX) - .5;
            int start = (int)Math.Floor(position);
            first[x] = Math.Clamp(start, 0, source.Width - 1);
            second[x] = Math.Clamp(start + 1, 0, source.Width - 1);
            weights[x] = (uint)Math.Clamp((int)Math.Floor((position - start) * 256 + .5), 0, 256);
        }
        byte* original = (byte*)source.Pointer;
        for (int y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double position = ((y + view.Top + .5 - originY) / ratioY) - .5;
            int start = (int)Math.Floor(position);
            uint weight = (uint)Math.Clamp((int)Math.Floor((position - start) * 256 + .5), 0, 256);
            uint* upper = (uint*)(original + ((long)Math.Clamp(start, 0, source.Height - 1) * source.Stride));
            uint* lower = (uint*)(original + ((long)Math.Clamp(start + 1, 0, source.Height - 1) * source.Stride));
            uint* target = (uint*)((byte*)destination + ((long)y * stride));
            for (int x = 0; x < width; x++)
            {
                uint a = Interpolate(upper[first[x]], upper[second[x]], weights[x]);
                uint b = Interpolate(lower[first[x]], lower[second[x]], weights[x]);
                target[x] = Interpolate(a, b, weight);
            }
        }
    }

    // Independent 16-bit lanes avoid channel carries; both interpolation stages round once.
    private static uint Interpolate(uint a, uint b, uint weight)
    {
        uint inverse = 256 - weight;
        uint rb = ((((a & 0x00FF00FF) * inverse) + ((b & 0x00FF00FF) * weight) + 0x00800080) >> 8) & 0x00FF00FF;
        uint ga = (((((a >> 8) & 0x00FF00FF) * inverse) + (((b >> 8) & 0x00FF00FF) * weight) + 0x00800080)) & 0xFF00FF00;
        return rb | ga;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        internal uint Size;
        internal int Width, Height;
        internal ushort Planes, BitCount;
        internal uint Compression, SizeImage;
        internal int XPelsPerMeter, YPelsPerMeter;
        internal uint ColorsUsed, ColorsImportant;
    }

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int StretchDIBits(IntPtr dc, int x, int y, int width, int height,
        int sourceX, int sourceY, int sourceWidth, int sourceHeight, IntPtr bits,
        ref BitmapInfo info, uint usage, uint operation);
    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GdiFlush();
}
