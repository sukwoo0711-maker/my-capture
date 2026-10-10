namespace MyCapture.App.Recording;

/// <summary>
/// Deterministic area-averaging (box) downscaler for 32-bit BGRX/BGRA frames.
/// </summary>
/// <remarks>
/// Each output pixel is the exact coverage-weighted mean of the source pixels beneath it, in
/// fixed-point integer arithmetic, so the same input always produces the same bytes on every
/// thread and machine. Area averaging keeps thin text strokes and does not alias the way a
/// bilinear 3x/4x reduction does. The fourth byte is written as opaque 255. The scaler never
/// enlarges: GIF presets only shrink a recording.
/// </remarks>
internal static class BgraFrameScaler
{
    private const int WeightBits = 12;
    private const int WeightOne = 1 << WeightBits;
    private const long Rounding = 1L << ((WeightBits * 2) - 1);

    [ThreadStatic]
    private static long[]? t_rowAccumulator;

    /// <summary>Returns a new tightly packed <paramref name="width"/> x <paramref name="height"/> frame.</summary>
    internal static byte[] Downscale(
        byte[] source,
        int sourceWidth,
        int sourceHeight,
        int sourceStride,
        int width,
        int height)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (sourceWidth < 1 || sourceHeight < 1 || width < 1 || height < 1
            || width > sourceWidth || height > sourceHeight)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Only same-size or smaller frames are supported.");
        }

        if (sourceStride < checked(sourceWidth * 4)
            || source.Length < checked((sourceStride * (sourceHeight - 1)) + (sourceWidth * 4)))
        {
            throw new ArgumentException("The source frame is smaller than its stated dimensions.", nameof(source));
        }

        int stride = checked(width * 4);
        byte[] output = GC.AllocateUninitializedArray<byte>(checked(stride * height));
        Contribution[] columns = Contributions(sourceWidth, width);
        Contribution[] rows = Contributions(sourceHeight, height);
        int accumulatorLength = checked(sourceWidth * 3);
        long[] accumulator = t_rowAccumulator is { } cached && cached.Length >= accumulatorLength
            ? cached
            : t_rowAccumulator = new long[accumulatorLength];

        for (int y = 0; y < height; y++)
        {
            // Vertical pass: weighted sum of the source rows under this output row.
            Array.Clear(accumulator, 0, accumulatorLength);
            Contribution row = rows[y];
            for (int tap = 0; tap < row.Count; tap++)
            {
                long weight = row.Weights[tap];
                int sourceOffset = (row.First + tap) * sourceStride;
                for (int x = 0, a = 0; x < sourceWidth; x++, a += 3, sourceOffset += 4)
                {
                    accumulator[a] += source[sourceOffset] * weight;
                    accumulator[a + 1] += source[sourceOffset + 1] * weight;
                    accumulator[a + 2] += source[sourceOffset + 2] * weight;
                }
            }

            // Horizontal pass over the vertically averaged row.
            int outputOffset = y * stride;
            for (int x = 0; x < width; x++, outputOffset += 4)
            {
                Contribution column = columns[x];
                long b = Rounding;
                long g = Rounding;
                long r = Rounding;
                for (int tap = 0, a = column.First * 3; tap < column.Count; tap++, a += 3)
                {
                    long weight = column.Weights[tap];
                    b += accumulator[a] * weight;
                    g += accumulator[a + 1] * weight;
                    r += accumulator[a + 2] * weight;
                }

                output[outputOffset] = (byte)(b >> (WeightBits * 2));
                output[outputOffset + 1] = (byte)(g >> (WeightBits * 2));
                output[outputOffset + 2] = (byte)(r >> (WeightBits * 2));
                output[outputOffset + 3] = 255;
            }
        }

        return output;
    }

    /// <summary>
    /// Fixed-point coverage of every output cell. Weights of one cell sum to exactly
    /// <see cref="WeightOne"/>, so flat colours survive unchanged.
    /// </summary>
    internal static Contribution[] Contributions(int sourceLength, int outputLength)
    {
        var result = new Contribution[outputLength];
        double scale = (double)sourceLength / outputLength;
        for (int index = 0; index < outputLength; index++)
        {
            double start = index * scale;
            double end = Math.Min(sourceLength, (index + 1) * scale);
            int first = (int)Math.Floor(start);
            int last = Math.Min(sourceLength - 1, (int)Math.Ceiling(end) - 1);
            int count = Math.Max(1, last - first + 1);
            int[] weights = new int[count];
            int total = 0;
            int largest = 0;
            for (int tap = 0; tap < count; tap++)
            {
                double coverage = Math.Min(end, first + tap + 1) - Math.Max(start, first + tap);
                weights[tap] = (int)Math.Round(Math.Max(0, coverage) / (end - start) * WeightOne);
                total += weights[tap];
                if (weights[tap] > weights[largest])
                {
                    largest = tap;
                }
            }

            weights[largest] += WeightOne - total;
            result[index] = new Contribution(first, count, weights);
        }

        return result;
    }

    internal readonly record struct Contribution(int First, int Count, int[] Weights);
}
