using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MyCapture.App.Pinning;

/// <summary>
/// Scanline CPU resampling directly into the display DIB. Only one source row, separable filter
/// coefficients and a ring of horizontally filtered rows are temporary; no second full frame.
/// Filtering operates on premultiplied channels so transparent colour cannot create a dark halo.
/// </summary>
internal static class NativePinResampler
{
    internal static unsafe void Write(BitmapSource source, IntPtr destination, int width, int height,
        int stride, bool highQuality, double contentScale, CancellationToken cancellationToken = default)
    {
        Write(new BitmapRows(source), destination, width, height, stride, highQuality, contentScale, cancellationToken);
    }

    internal static void Write(NativePinPixelSource.Lease source, IntPtr destination, int width, int height,
        int stride, bool highQuality, double contentScale, CancellationToken cancellationToken = default) =>
        Write(new NativeRows(source), destination, NativePinRaster.View.Whole(width, height), stride, highQuality,
            contentScale, cancellationToken);

    /// <summary>
    /// Writes only <paramref name="view"/> of the surface into a destination that holds that view.
    /// Each written pixel is computed exactly as the whole surface's pixel at the same place (the same
    /// coefficients, sums and rounding), so a clipped frame is byte-identical to that part of a whole one.
    /// </summary>
    internal static void Write(NativePinPixelSource.Lease source, IntPtr destination, NativePinRaster.View view,
        int stride, bool highQuality, double contentScale, CancellationToken cancellationToken = default) =>
        Write(new NativeRows(source), destination, view, stride, highQuality, contentScale, cancellationToken);

    private static unsafe void Write(IRows readable, IntPtr destination, int width, int height,
        int stride, bool highQuality, double contentScale, CancellationToken cancellationToken) =>
        Write(readable, destination, NativePinRaster.View.Whole(width, height), stride, highQuality, contentScale, cancellationToken);

    private static unsafe void Write(IRows readable, IntPtr destination, NativePinRaster.View view,
        int stride, bool highQuality, double contentScale, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        view.Validate();
        int width = view.Width, height = view.Height;
        if (contentScale == 1 && readable.Width == view.SurfaceWidth && readable.Height == view.SurfaceHeight)
        {
            for (int y = 0; y < height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                readable.Row(y + view.Top).Slice(view.Left * 4, width * 4)
                    .CopyTo(new Span<byte>((byte*)destination + ((long)y * stride), width * 4));
            }
            return;
        }

        var horizontal = new Axis(readable.Width, view.SurfaceWidth, view.Left, width, contentScale, highQuality, cancellationToken);
        var vertical = new Axis(readable.Height, view.SurfaceHeight, view.Top, height, contentScale, highQuality, cancellationToken);
        var rows = new RowCache(readable, horizontal, width, Math.Min(readable.Height, vertical.Taps), vertical.Taps, cancellationToken);
        for (int y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte* target = (byte*)destination + ((long)y * stride);
            int coefficient = y * vertical.Taps;
            // Resolve the rows once per output scanline rather than once per output pixel.
            for (int tap = 0; tap < vertical.Taps; tap++)
                rows.Resolve(tap, Math.Clamp(vertical.First[y] + tap, 0, readable.Height - 1));
            for (int x = 0; x < width; x++)
            {
                int offset = x * 4;
                float blue = 0, green = 0, red = 0, alpha = 0;
                for (int tap = 0; tap < vertical.Taps; tap++)
                {
                    float weight = vertical.Weights[coefficient + tap];
                    float[] row = rows.Current[tap];
                    blue += row[offset] * weight;
                    green += row[offset + 1] * weight;
                    red += row[offset + 2] * weight;
                    alpha += row[offset + 3] * weight;
                }
                byte a = Byte(alpha);
                target[offset] = (byte)Math.Min(a, Byte(blue));
                target[offset + 1] = (byte)Math.Min(a, Byte(green));
                target[offset + 2] = (byte)Math.Min(a, Byte(red));
                target[offset + 3] = a;
            }
        }
    }

    private static byte Byte(float value) => (byte)Math.Clamp((int)MathF.Floor(value + .5f), 0, 255);

    private interface IRows
    {
        int Width { get; }
        int Height { get; }
        ReadOnlySpan<byte> Row(int y);
    }

    private sealed class NativeRows(NativePinPixelSource.Lease source) : IRows
    {
        public int Width => source.Width;
        public int Height => source.Height;
        public unsafe ReadOnlySpan<byte> Row(int y) => new((byte*)source.Pointer + ((long)y * source.Stride), source.Stride);
    }

    private sealed class BitmapRows : IRows
    {
        private readonly BitmapSource _source;
        private readonly byte[] _row;
        public int Width => _source.PixelWidth;
        public int Height => _source.PixelHeight;

        internal BitmapRows(BitmapSource source)
        {
            _source = source;
            if (source.Format != PixelFormats.Pbgra32 && source.Format != PixelFormats.Bgra32 && source.Format != PixelFormats.Bgr32)
            {
                _source = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
                _source.Freeze();
            }
            _row = new byte[checked(Width * 4)];
        }

        public ReadOnlySpan<byte> Row(int y)
        {
            _source.CopyPixels(new Int32Rect(0, y, Width, 1), _row, _row.Length, 0);
            NativePinPixelSource.Normalize(_row, _source.Format);
            return _row;
        }
    }

    private sealed class Axis
    {
        internal int[] First { get; }
        internal float[] Weights { get; }
        internal int Taps { get; }

        // Coefficients for the outputs [offset, offset + count) of a destination axis of destinationSize;
        // index i holds output offset + i, computed as the whole axis computes it.
        internal Axis(int sourceSize, int destinationSize, int offset, int count, double contentScale, bool highQuality,
            CancellationToken cancellationToken)
        {
            double actualSize = destinationSize * contentScale;
            double origin = (destinationSize - actualSize) / 2;
            double ratio = actualSize / sourceSize;
            double lowPassScale = Math.Min(1, ratio);
            double support = highQuality ? 3 / lowPassScale : 1;
            Taps = highQuality ? checked((int)Math.Ceiling(support * 2) + 1) : 2;
            First = new int[count];
            Weights = new float[checked(count * Taps)];
            for (int index = 0; index < count; index++)
            {
                if ((index & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                int output = offset + index;
                double position = ((output + .5 - origin) / ratio) - .5;
                int first = highQuality ? (int)Math.Ceiling(position - support) : (int)Math.Floor(position);
                First[index] = first;
                double total = 0;
                for (int tap = 0; tap < Taps; tap++)
                {
                    double weight = highQuality ? Lanczos((position - first - tap) * lowPassScale)
                        : tap == 0 ? 1 - (position - first) : position - first;
                    Weights[(index * Taps) + tap] = (float)weight;
                    total += weight;
                }
                // Every coefficient footprint has at least one sample. Normalize edge-clamped
                // weights as well so a constant (including partial-alpha) source stays constant.
                for (int tap = 0; tap < Taps; tap++) Weights[(index * Taps) + tap] /= (float)total;
            }
        }

        private static double Lanczos(double x)
        {
            x = Math.Abs(x);
            if (x >= 3) return 0;
            if (x < 1e-12) return 1;
            double angle = Math.PI * x;
            return Math.Sin(angle) * Math.Sin(angle / 3) / (angle * angle / 3);
        }
    }

    private sealed class RowCache
    {
        private readonly IRows _source;
        private readonly Axis _horizontal;
        private readonly int _width;
        private readonly float[][] _rows;
        private readonly int[] _ids;
        private readonly CancellationToken _cancellationToken;
        internal float[][] Current { get; }

        internal RowCache(IRows source, Axis horizontal, int width, int capacity, int verticalTaps, CancellationToken cancellationToken)
        {
            _source = source; _horizontal = horizontal; _width = width;
            _cancellationToken = cancellationToken;
            _rows = new float[capacity][];
            _ids = new int[capacity];
            Array.Fill(_ids, -1);
            // At extreme downscales many taps clamp to the same source row. The Current array
            // contains references only; each distinct cached row is allocated once.
            Current = new float[verticalTaps][];
        }

        internal void Resolve(int tap, int sourceY)
        {
            int slot = sourceY % _rows.Length;
            float[] row = _rows[slot] ??= new float[checked(_width * 4)];
            if (_ids[slot] != sourceY)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                ReadOnlySpan<byte> sourceRow = _source.Row(sourceY);
                for (int x = 0; x < _width; x++)
                {
                    if ((x & 2047) == 0) _cancellationToken.ThrowIfCancellationRequested();
                    int target = x * 4, coefficient = x * _horizontal.Taps;
                    float blue = 0, green = 0, red = 0, alpha = 0;
                    for (int k = 0; k < _horizontal.Taps; k++)
                    {
                        int sample = Math.Clamp(_horizontal.First[x] + k, 0, _source.Width - 1) * 4;
                        float weight = _horizontal.Weights[coefficient + k];
                        blue += sourceRow[sample] * weight;
                        green += sourceRow[sample + 1] * weight;
                        red += sourceRow[sample + 2] * weight;
                        alpha += sourceRow[sample + 3] * weight;
                    }
                    row[target] = blue; row[target + 1] = green; row[target + 2] = red; row[target + 3] = alpha;
                }
                _ids[slot] = sourceY;
            }
            Current[tap] = row;
        }
    }
}
