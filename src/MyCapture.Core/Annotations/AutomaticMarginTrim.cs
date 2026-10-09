using MyCapture.Core.Primitives;

namespace MyCapture.Core.Annotations;

/// <summary>Conservative, lossless padding detection on a rendered BGRA image.</summary>
public static class AutomaticMarginTrim
{
    private const int MaximumPeriod = 16;
    private const int MinimumMargin = 2;

    /// <summary>
    /// A shared background must repeat at all corners and around the perimeter.
    /// Otherwise, independently verified narrow edge bands may be removed.
    /// Every differing pixel is retained; ambiguous, tiny and entirely repeating images are no-ops.
    /// </summary>
    public static RectD Find(ReadOnlySpan<byte> pixels, int width, int height, int stride,
        CancellationToken cancellationToken = default)
    {
        if (width < 1 || height < 1 || stride < (long)width * 4
            || pixels.Length < (long)(height - 1) * stride + (long)width * 4)
            throw new ArgumentException("A complete BGRA image with a valid stride is required.");

        var full = new RectD(0, 0, width, height);
        if (width < 8 || height < 8) return full;
        int maxX = Math.Min(MaximumPeriod, width / 4);
        int maxY = Math.Min(MaximumPeriod, height / 4);
        int periodX = 0, periodY = 0;
        for (int py = 1; py <= maxY && periodX == 0; py++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int px = 1; px <= maxX; px++)
            {
                if (!CornersRepeat(pixels, width, height, stride, px, py)) continue;
                periodX = px;
                periodY = py;
                break;
            }
        }
        if (periodX == 0) return FindEdgeBands(pixels, width, height, stride, cancellationToken);

        int left = width, top = height, right = -1, bottom = -1;
        for (int y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int patternRow = (y % periodY) * stride;
            for (int x = 0; x < width; x++)
            {
                if (SamePixel(pixels, y * stride + x * 4, patternRow + (x % periodX) * 4))
                    continue;
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = y;
            }
        }
        // Never turn an empty image or an isolated uncertain pixel into a tiny crop.
        if (right - left < 1 || bottom - top < 1) return full;
        if (left < MinimumMargin) left = 0;
        if (top < MinimumMargin) top = 0;
        if (width - 1 - right < MinimumMargin) right = width - 1;
        if (height - 1 - bottom < MinimumMargin) bottom = height - 1;
        return new RectD(left, top, right - left + 1, bottom - top + 1);
    }

    private static bool CornersRepeat(ReadOnlySpan<byte> pixels, int width, int height,
        int stride, int periodX, int periodY)
    {
        int sampleWidth = periodX * 2, sampleHeight = periodY * 2;
        for (int corner = 0; corner < 4; corner++)
        {
            int startX = (corner & 1) == 0 ? 0 : width - sampleWidth;
            int startY = (corner & 2) == 0 ? 0 : height - sampleHeight;
            for (int dy = 0; dy < sampleHeight; dy++)
            {
                int y = startY + dy;
                for (int dx = 0; dx < sampleWidth; dx++)
                {
                    int x = startX + dx;
                    if (!SamePixel(pixels, y * stride + x * 4,
                        (y % periodY) * stride + (x % periodX) * 4)) return false;
                }
            }
        }
        // Corner samples alone can alias a larger tile when the far edge has the
        // same phase. Verify the full perimeter before treating that tile as padding.
        for (int x = 0; x < width; x++)
            if (!SamePixel(pixels, x * 4, (x % periodX) * 4)
                || !SamePixel(pixels, (height - 1) * stride + x * 4,
                    ((height - 1) % periodY) * stride + (x % periodX) * 4)) return false;
        for (int y = 0; y < height; y++)
            if (!SamePixel(pixels, y * stride, (y % periodY) * stride)
                || !SamePixel(pixels, y * stride + (width - 1) * 4,
                    (y % periodY) * stride + ((width - 1) % periodX) * 4)) return false;
        return true;
    }

    private static RectD FindEdgeBands(ReadOnlySpan<byte> pixels, int width, int height, int stride,
        CancellationToken cancellationToken)
    {
        int top = EdgeMargin(pixels, width, height, stride, 0, cancellationToken);
        int bottom = EdgeMargin(pixels, width, height, stride, 1, cancellationToken);
        int left = EdgeMargin(pixels, width, height, stride, 2, cancellationToken);
        int right = EdgeMargin(pixels, width, height, stride, 3, cancellationToken);
        return left + right >= width - 1 || top + bottom >= height - 1
            ? new RectD(0, 0, width, height)
            : new RectD(left, top, width - left - right, height - top - bottom);
    }

    private static int EdgeMargin(ReadOnlySpan<byte> pixels, int width, int height, int stride,
        int side, CancellationToken cancellationToken)
    {
        int length = side < 2 ? width : height;
        int depth = side < 2 ? height : width;
        // An independently inferred edge may remove at most a third of the image.
        // Large flat picture areas and two differently colored halves remain intact.
        int limit = depth / 3;
        int best = 0;
        for (int py = 1; py <= Math.Min(MaximumPeriod, limit / 2); py++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int px = 1; px <= Math.Min(MaximumPeriod, length / 2); px++)
            {
                if (!PrimitiveEdgeTile(pixels, width, height, stride, side, px, py)) continue;
                int matching = 0;
                for (; matching <= limit; matching++)
                {
                    if ((matching & 31) == 0) cancellationToken.ThrowIfCancellationRequested();
                    bool rowMatches = true;
                    for (int along = 0; along < length; along++)
                    {
                        int sample = EdgeOffset(side, along, matching, width, height, stride);
                        int pattern = EdgeOffset(side, along % px, matching % py, width, height, stride);
                        if (SamePixel(pixels, sample, pattern)) continue;
                        rowMatches = false;
                        break;
                    }
                    if (!rowMatches) break;
                }
                if (matching >= Math.Max(MinimumMargin, py * 2) && matching <= limit)
                    best = Math.Max(best, matching);
            }
        }
        return best;
    }

    // Skip multiples of a simpler tile so a solid band is scanned once, not 256 times.
    private static bool PrimitiveEdgeTile(ReadOnlySpan<byte> pixels, int width, int height,
        int stride, int side, int px, int py)
    {
        for (int axis = 0; axis < 2; axis++)
        {
            int period = axis == 0 ? px : py;
            for (int smaller = 1; smaller < period; smaller++)
            {
                if (period % smaller != 0) continue;
                bool repeats = true;
                for (int y = 0; y < py && repeats; y++)
                    for (int x = 0; x < px; x++)
                    {
                        int sample = EdgeOffset(side, x, y, width, height, stride);
                        int pattern = EdgeOffset(side, axis == 0 ? x % smaller : x,
                            axis == 1 ? y % smaller : y, width, height, stride);
                        if (SamePixel(pixels, sample, pattern)) continue;
                        repeats = false;
                        break;
                    }
                if (repeats) return false;
            }
        }
        return true;
    }

    private static int EdgeOffset(int side, int along, int depth, int width, int height, int stride) =>
        side switch
        {
            0 => depth * stride + along * 4,
            1 => (height - 1 - depth) * stride + along * 4,
            2 => along * stride + depth * 4,
            _ => along * stride + (width - 1 - depth) * 4,
        };

    private static bool SamePixel(ReadOnlySpan<byte> pixels, int a, int b) =>
        pixels[a + 3] == pixels[b + 3]
        && (pixels[a + 3] == 0 || (pixels[a] == pixels[b]
            && pixels[a + 1] == pixels[b + 1] && pixels[a + 2] == pixels[b + 2]));
}