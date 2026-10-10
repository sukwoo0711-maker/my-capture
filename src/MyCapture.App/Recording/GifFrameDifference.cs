using System.Runtime.InteropServices;

namespace MyCapture.App.Recording;

/// <summary>How <see cref="AnimatedGifWriter"/> turns consecutive frames into GIF images.</summary>
internal enum GifFrameDifferencing
{
    /// <summary>
    /// Every frame is written as a full-canvas image with its own delay: the original output,
    /// kept for size and quality comparisons.
    /// </summary>
    None,

    /// <summary>
    /// A frame identical to the previous one is not written; its delay is added to the image
    /// already on screen. Changed frames are still full-canvas images.
    /// </summary>
    MergeIdentical,

    /// <summary>
    /// As <see cref="MergeIdentical"/>, and a changed frame encodes only the smallest rectangle
    /// whose source pixels differ, at its offset over the kept canvas (disposal 1). Kept for
    /// comparison: the kept pixels show the quantisation of whichever earlier frame last painted
    /// them, which measured up to 4 dB worse than the full-frame output when that frame's
    /// palette was busier.
    /// </summary>
    ChangedRectangle,

    /// <summary>
    /// As <see cref="MergeIdentical"/>, and each changed frame repaints only the pixels needed to
    /// stay within the full-frame output's error; see <see cref="GifBoundedCanvas"/>. The
    /// production default.
    /// </summary>
    ErrorBounded,
}

/// <summary>A pixel rectangle on the GIF canvas; empty when either side is zero.</summary>
internal readonly record struct GifPixelRect(int Left, int Top, int Width, int Height)
{
    internal bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>
/// Finds where a 32-bit BGRA frame differs from the previous one. Pure managed code: it runs
/// wherever the writer runs and never touches WPF.
/// </summary>
internal static class GifFrameDifference
{
    /// <summary>
    /// Smallest rectangle holding every pixel of <paramref name="current"/> that differs from
    /// <paramref name="previous"/> in any of the four channels, or an empty rectangle when the
    /// frames are identical.
    /// </summary>
    internal static GifPixelRect ChangedBounds(
        ReadOnlySpan<byte> previous,
        int previousStride,
        ReadOnlySpan<byte> current,
        int currentStride,
        int width,
        int height)
    {
        ValidateFrame(previous.Length, previousStride, width, height, nameof(previous));
        ValidateFrame(current.Length, currentStride, width, height, nameof(current));

        int rowBytes = width * 4;
        int top = 0;
        while (top < height
               && previous.Slice(top * previousStride, rowBytes).SequenceEqual(current.Slice(top * currentStride, rowBytes)))
        {
            top++;
        }

        if (top == height)
        {
            return default;
        }

        int bottom = height - 1;
        while (bottom > top
               && previous.Slice(bottom * previousStride, rowBytes).SequenceEqual(current.Slice(bottom * currentStride, rowBytes)))
        {
            bottom--;
        }

        int left = width;
        int right = -1;
        for (int y = top; y <= bottom; y++)
        {
            ReadOnlySpan<uint> before = MemoryMarshal.Cast<byte, uint>(previous.Slice(y * previousStride, rowBytes));
            ReadOnlySpan<uint> after = MemoryMarshal.Cast<byte, uint>(current.Slice(y * currentStride, rowBytes));

            // Only columns outside the bounds found so far can widen them.
            left = Math.Min(left, before[..left].CommonPrefixLength(after[..left]));

            int last = width - 1;
            while (last > right && before[last] == after[last])
            {
                last--;
            }

            right = Math.Max(right, last);
        }

        return new GifPixelRect(left, top, right - left + 1, bottom - top + 1);
    }

    private static void ValidateFrame(int length, int stride, int width, int height, string parameterName)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if (stride < checked(width * 4) || length < checked((stride * (height - 1)) + (width * 4)))
        {
            throw new ArgumentException("The frame buffer is smaller than its stride and size require.", parameterName);
        }
    }
}
