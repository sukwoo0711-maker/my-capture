namespace MyCapture.Core.Annotations;

/// <summary>
/// Gaussian blur on tightly packed 32-bit pixels, approximated by three centred box passes
/// per axis.
/// </summary>
/// <remarks>
/// <para>
/// Three successive box filters converge on a Gaussian closely enough that the result is
/// visually indistinguishable, while each pass costs the same per pixel whatever the
/// strength (a running sum, not a kernel loop). All four channels are filtered alike, so
/// callers pass premultiplied BGRA to keep transparent edges free of colour fringes.
/// </para>
/// <para>
/// Edges are clamped: a pixel beyond the buffer repeats the nearest edge pixel, so a
/// uniform area stays exactly uniform up to the image border instead of darkening as it
/// would against zero padding.
/// </para>
/// <para>
/// The arithmetic is integer-only and independent of threading, so a region blurred on its
/// own (<see cref="BlurRegion"/>) is byte-identical to the same region cut out of a
/// whole-image blur. The editor preview relies on that to show exactly what an export
/// writes.
/// </para>
/// </remarks>
public static class GaussianBlurKernel
{
    private const int Passes = 3;

    // Below this many pixels the fork/join overhead outweighs the work.
    private const int ParallelThreshold = 256 * 1024;

    // Keeps every box window below 4096 samples, the bound under which Divider is exact.
    private const double MaximumSigma = 1024;

    /// <summary>
    /// Radii of the three centred box filters whose successive application approximates a
    /// Gaussian with standard deviation <paramref name="sigma"/>.
    /// </summary>
    public static int[] BoxRadii(double sigma)
    {
        double s = double.IsFinite(sigma) ? Math.Clamp(sigma, 0, MaximumSigma) : 0;
        if (s < 0.5)
        {
            return new int[Passes];
        }

        // Ideal box width for n passes; use the nearest odd widths below and above it and
        // pick how many of each so the summed variance matches sigma squared.
        double ideal = Math.Sqrt((12 * s * s / Passes) + 1);
        int lower = (int)Math.Floor(ideal);
        if (lower % 2 == 0)
        {
            lower--;
        }

        int upper = lower + 2;
        double lowerCount = ((12 * s * s) - (Passes * lower * lower) - (4 * Passes * lower) - (3 * Passes))
            / ((-4.0 * lower) - 4);
        int m = Math.Clamp((int)Math.Round(lowerCount, MidpointRounding.AwayFromZero), 0, Passes);

        var radii = new int[Passes];
        for (int i = 0; i < Passes; i++)
        {
            radii[i] = ((i < m ? lower : upper) - 1) / 2;
        }

        return radii;
    }

    /// <summary>
    /// How far, in pixels along each axis, an output pixel depends on its input: the sum of
    /// the box radii.
    /// </summary>
    public static int Reach(double sigma) => BoxRadii(sigma).Sum();

    /// <summary>
    /// The part of a <paramref name="width"/> by <paramref name="height"/> image that
    /// <see cref="BlurRegion"/> reads for the given region: the region grown by
    /// <see cref="Reach"/> and intersected with the image.
    /// </summary>
    public static (int X, int Y, int Width, int Height) SourceWindow(
        int width, int height, int x, int y, int regionWidth, int regionHeight, double sigma)
    {
        int reach = Reach(sigma);
        int left = Math.Max(0, x - reach);
        int top = Math.Max(0, y - reach);
        int right = (int)Math.Min(width, (long)x + regionWidth + reach);
        int bottom = (int)Math.Min(height, (long)y + regionHeight + reach);
        return (left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    /// <summary>
    /// Blurs one region of an image and returns it as a tightly packed buffer
    /// (<paramref name="regionWidth"/> × 4 bytes per row).
    /// </summary>
    /// <remarks>
    /// Only the <see cref="SourceWindow"/> is read. Errors introduced by clamping at a window
    /// edge that is not an image edge travel inward by at most one box radius per pass, which
    /// the <see cref="Reach"/> margin absorbs, so the region matches a whole-image blur.
    /// </remarks>
    public static byte[] BlurRegion(
        ReadOnlySpan<byte> image,
        int width,
        int height,
        int stride,
        int x,
        int y,
        int regionWidth,
        int regionHeight,
        double sigma,
        CancellationToken cancellationToken = default)
    {
        if (width < 1 || height < 1 || stride < (long)width * 4
            || image.Length < ((long)(height - 1) * stride) + ((long)width * 4))
        {
            throw new ArgumentException("A complete 32-bit image with a valid stride is required.", nameof(image));
        }

        if (regionWidth < 1 || regionHeight < 1 || x < 0 || y < 0
            || (long)x + regionWidth > width || (long)y + regionHeight > height)
        {
            throw new ArgumentOutOfRangeException(nameof(regionWidth), "The region must lie inside the image.");
        }

        (int wx, int wy, int ww, int wh) = SourceWindow(width, height, x, y, regionWidth, regionHeight, sigma);
        int windowRow = ww * 4;
        byte[] window = new byte[checked(windowRow * wh)];
        for (int row = 0; row < wh; row++)
        {
            image.Slice(((wy + row) * stride) + (wx * 4), windowRow).CopyTo(window.AsSpan(row * windowRow, windowRow));
        }

        BlurInPlace(window, ww, wh, sigma, cancellationToken);

        int regionRow = regionWidth * 4;
        byte[] result = new byte[checked(regionRow * regionHeight)];
        for (int row = 0; row < regionHeight; row++)
        {
            window.AsSpan(((y - wy + row) * windowRow) + ((x - wx) * 4), regionRow)
                .CopyTo(result.AsSpan(row * regionRow, regionRow));
        }

        return result;
    }

    /// <summary>
    /// Blurs a tightly packed 32-bit buffer (<paramref name="width"/> × 4 bytes per row) in
    /// place, clamping at its edges.
    /// </summary>
    public static void BlurInPlace(
        byte[] pixels, int width, int height, double sigma, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (width < 1 || height < 1 || pixels.Length < (long)width * height * 4)
        {
            throw new ArgumentException("The buffer is smaller than the stated size.", nameof(pixels));
        }

        int[] radii = BoxRadii(sigma);
        if (radii.All(static r => r == 0))
        {
            return;
        }

        bool parallel = (long)width * height >= ParallelThreshold;
        int rowLength = width * 4;

        // Horizontal: every row is independent; ping-pong through two row buffers so the
        // three passes leave the result back in the image row.
        void BlurRow(int row, byte[] first, byte[] second)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Span<byte> line = pixels.AsSpan(row * rowLength, rowLength);
            BoxLine(line, first, width, radii[0]);
            BoxLine(first, second, width, radii[1]);
            BoxLine(second, line, width, radii[2]);
        }

        if (parallel)
        {
            Parallel.For(
                0,
                height,
                new ParallelOptions { CancellationToken = cancellationToken },
                () => (First: new byte[rowLength], Second: new byte[rowLength]),
                (row, _, buffers) =>
                {
                    BlurRow(row, buffers.First, buffers.Second);
                    return buffers;
                },
                static _ => { });
        }
        else
        {
            byte[] first = new byte[rowLength];
            byte[] second = new byte[rowLength];
            for (int row = 0; row < height; row++)
            {
                BlurRow(row, first, second);
            }
        }

        // Vertical: a running sum per byte column walks down the rows, which keeps memory
        // access sequential. Columns are split into independent bands for parallelism.
        byte[] scratch = new byte[pixels.Length];
        const int band = 1024;
        int bands = (rowLength + band - 1) / band;

        void BlurBand(int index)
        {
            int start = index * band;
            int length = Math.Min(band, rowLength - start);
            BoxColumns(pixels, scratch, rowLength, height, start, length, radii[0], cancellationToken);
            BoxColumns(scratch, pixels, rowLength, height, start, length, radii[1], cancellationToken);
            BoxColumns(pixels, scratch, rowLength, height, start, length, radii[2], cancellationToken);
        }

        if (parallel)
        {
            Parallel.For(0, bands, new ParallelOptions { CancellationToken = cancellationToken }, BlurBand);
        }
        else
        {
            for (int index = 0; index < bands; index++)
            {
                BlurBand(index);
            }
        }

        Buffer.BlockCopy(scratch, 0, pixels, 0, checked(rowLength * height));
    }

    /// <summary>One centred box pass along a row of <paramref name="count"/> pixels.</summary>
    private static void BoxLine(ReadOnlySpan<byte> source, Span<byte> destination, int count, int radius)
    {
        if (radius == 0)
        {
            source[..(count * 4)].CopyTo(destination);
            return;
        }

        Divider divide = new(radius);
        int last = count - 1;
        int s0 = (radius + 1) * source[0];
        int s1 = (radius + 1) * source[1];
        int s2 = (radius + 1) * source[2];
        int s3 = (radius + 1) * source[3];
        for (int k = 1; k <= radius; k++)
        {
            int i = Math.Min(k, last) * 4;
            s0 += source[i];
            s1 += source[i + 1];
            s2 += source[i + 2];
            s3 += source[i + 3];
        }

        for (int x = 0; x < count; x++)
        {
            int o = x * 4;
            destination[o] = divide.Apply(s0);
            destination[o + 1] = divide.Apply(s1);
            destination[o + 2] = divide.Apply(s2);
            destination[o + 3] = divide.Apply(s3);

            int add = Math.Min(x + radius + 1, last) * 4;
            int remove = Math.Max(x - radius, 0) * 4;
            s0 += source[add] - source[remove];
            s1 += source[add + 1] - source[remove + 1];
            s2 += source[add + 2] - source[remove + 2];
            s3 += source[add + 3] - source[remove + 3];
        }
    }

    /// <summary>One centred box pass down a band of byte columns.</summary>
    private static void BoxColumns(
        byte[] source,
        byte[] destination,
        int rowLength,
        int height,
        int start,
        int length,
        int radius,
        CancellationToken cancellationToken)
    {
        if (radius == 0)
        {
            for (int row = 0; row < height; row++)
            {
                Buffer.BlockCopy(source, (row * rowLength) + start, destination, (row * rowLength) + start, length);
            }

            return;
        }

        Divider divide = new(radius);
        int last = height - 1;
        int[] sums = new int[length];
        for (int c = 0; c < length; c++)
        {
            sums[c] = (radius + 1) * source[start + c];
        }

        for (int k = 1; k <= radius; k++)
        {
            int offset = (Math.Min(k, last) * rowLength) + start;
            for (int c = 0; c < length; c++)
            {
                sums[c] += source[offset + c];
            }
        }

        for (int row = 0; row < height; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int output = (row * rowLength) + start;
            int add = (Math.Min(row + radius + 1, last) * rowLength) + start;
            int remove = (Math.Max(row - radius, 0) * rowLength) + start;
            for (int c = 0; c < length; c++)
            {
                int sum = sums[c];
                destination[output + c] = divide.Apply(sum);
                sums[c] = sum + source[add + c] - source[remove + c];
            }
        }
    }

    /// <summary>
    /// Rounded division of a box sum by its window width, as a multiply and shift.
    /// </summary>
    /// <remarks>
    /// A rounded sum stays below 256 × window, and the window stays below 4096 samples (see
    /// <see cref="MaximumSigma"/>), so the error of a 32-bit reciprocal rounded up is smaller
    /// than the gap between any quotient and the next integer: the result is exact.
    /// </remarks>
    private readonly struct Divider
    {
        private readonly long _multiplier;
        private readonly int _half;

        internal Divider(int radius)
        {
            int window = (2 * radius) + 1;
            _multiplier = ((1L << 32) / window) + 1;
            _half = window / 2;
        }

        internal byte Apply(int sum) => (byte)(((sum + _half) * _multiplier) >> 32);
    }
}
