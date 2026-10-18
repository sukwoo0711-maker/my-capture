using System.Runtime.CompilerServices;
using MyCapture.Platform.Recording;

namespace MyCapture.App.Recording;

/// <summary>
/// Deterministic top-down BGRA to packed NV12 conversion for RGB MP4 exports and for frames
/// composed in RGB (timed text, frame layers) on the NV12 pass-through path.
/// </summary>
/// <remarks>
/// <para>
/// For 8-bit R', G', B', let L = Kr*R + (1-Kr-Kb)*G + Kb*B, with (Kr,Kb) =
/// (0.2126,0.0722) for BT.709 or (0.299,0.114) for BT.601. Studio codes are
/// Y = 16 + 219*L/255, Cb = 128 + 224*(B-L)/(255*2*(1-Kb)), and
/// Cr = 128 + 224*(R-L)/(255*2*(1-Kr)). Full range uses Y=L and 255 instead of 224.
/// Alpha is ignored: opaque channels are used directly, and premultiplied channels represent
/// their already flattened result over black. There is no unpremultiplication here.
/// </para>
/// <para>
/// Coefficients are quantised to 16 fractional bits with nearest/ties-to-even rounding; one
/// coefficient per row is derived from the others so chroma coefficients sum to zero and the
/// luma coefficients sum to the quantised scale. Each output code rounds half up (add half a
/// fixed-point unit, then shift) and clamps to [0,255]. Greys therefore have Cb=Cr=128 exactly,
/// and studio black/white map to 16/235. This is independent of the OS colour converter.
/// </para>
/// <para>
/// Chroma uses the mean RGB of each 2x2 block before conversion. Summing RGB then shifting by
/// 18 bits retains the fractional mean and rounds only once, avoiding four per-pixel chroma
/// roundings. The linear transform makes this equivalent to averaging unrounded chroma.
/// Bands of row pairs write disjoint bytes in parallel; output is independent of thread count.
/// </para>
/// </remarks>
internal static class BgraToNv12Converter
{
    private const int Shift = 16;
    private const int Half = 1 << (Shift - 1);
    private const int ChromaShift = Shift + 2;
    private const int ChromaHalf = 1 << (ChromaShift - 1);
    private const int MinimumRowPairsPerBand = 32;

    /// <summary>Bytes of a packed NV12 frame of an even <paramref name="width"/> x <paramref name="height"/>.</summary>
    internal static int RequiredLength(int width, int height) => checked(width * height * 3 / 2);

    /// <summary>
    /// Writes <paramref name="bgra"/> (alpha ignored, rows <paramref name="stride"/> bytes apart)
    /// into <paramref name="destination"/> as packed NV12 with a row length of <paramref name="width"/>.
    /// </summary>
    internal static void Convert(
        byte[] bgra,
        int stride,
        int width,
        int height,
        VideoYuvEncoding encoding,
        byte[] destination,
        int maxThreads = 0)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        ArgumentNullException.ThrowIfNull(destination);
        if (width < 2 || height < 2 || (width & 1) != 0 || (height & 1) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "NV12 needs an even frame size.");
        }

        if (stride < checked(width * 4) || bgra.Length < checked((stride * (height - 1)) + (width * 4)))
        {
            throw new ArgumentException("The BGRA frame is smaller than its stated dimensions.", nameof(bgra));
        }

        if (destination.Length < RequiredLength(width, height))
        {
            throw new ArgumentException("The NV12 destination is too small.", nameof(destination));
        }

        Coefficients coefficients = Coefficients.For(encoding);
        int rowPairs = height / 2;
        int threads = maxThreads > 0 ? maxThreads : Math.Min(Environment.ProcessorCount, 8);
        int bands = Math.Clamp(rowPairs / MinimumRowPairsPerBand, 1, Math.Max(1, threads));
        if (bands == 1)
        {
            ConvertRowPairs(bgra, stride, width, height, coefficients, destination, 0, rowPairs);
            return;
        }

        Parallel.For(
            0,
            bands,
            new ParallelOptions { MaxDegreeOfParallelism = bands },
            band => ConvertRowPairs(
                bgra,
                stride,
                width,
                height,
                coefficients,
                destination,
                (int)((long)rowPairs * band / bands),
                (int)((long)rowPairs * (band + 1) / bands)));
    }

    private static void ConvertRowPairs(
        byte[] bgra,
        int stride,
        int width,
        int height,
        Coefficients c,
        byte[] destination,
        int firstPair,
        int endPair)
    {
        int chromaPlane = width * height;
        for (int pair = firstPair; pair < endPair; pair++)
        {
            int top = 2 * pair * stride;
            int bottom = top + stride;
            int lumaTop = 2 * pair * width;
            int lumaBottom = lumaTop + width;
            int chroma = chromaPlane + (pair * width);
            for (int x = 0; x < width; x += 2)
            {
                int p0 = top + (x * 4);
                int p1 = p0 + 4;
                int p2 = bottom + (x * 4);
                int p3 = p2 + 4;
                int b0 = bgra[p0], g0 = bgra[p0 + 1], r0 = bgra[p0 + 2];
                int b1 = bgra[p1], g1 = bgra[p1 + 1], r1 = bgra[p1 + 2];
                int b2 = bgra[p2], g2 = bgra[p2 + 1], r2 = bgra[p2 + 2];
                int b3 = bgra[p3], g3 = bgra[p3 + 1], r3 = bgra[p3 + 2];
                destination[lumaTop + x] = Luma(c, r0, g0, b0);
                destination[lumaTop + x + 1] = Luma(c, r1, g1, b1);
                destination[lumaBottom + x] = Luma(c, r2, g2, b2);
                destination[lumaBottom + x + 1] = Luma(c, r3, g3, b3);

                // The chroma pair of a 2x2 block from its summed R, G, B (four times the mean).
                int r = r0 + r1 + r2 + r3;
                int g = g0 + g1 + g2 + g3;
                int b = b0 + b1 + b2 + b3;
                destination[chroma + x] = ChromaU(c, r, g, b);
                destination[chroma + x + 1] = ChromaV(c, r, g, b);
            }
        }
    }

    /// <summary>Luma of one pixel, rounded to the nearest code.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static byte Luma(Coefficients c, int r, int g, int b) =>
        Clamp(((c.YR * r) + (c.YG * g) + (c.YB * b) + (c.LumaOffset << Shift) + Half) >> Shift);

    /// <summary>Cb of a 2x2 block from the sums of its four R, G and B values.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static byte ChromaU(Coefficients c, int sumR, int sumG, int sumB) =>
        Clamp(((c.UR * sumR) + (c.UG * sumG) + (c.UB * sumB) + (128 << ChromaShift) + ChromaHalf) >> ChromaShift);

    /// <summary>Cr of a 2x2 block from the sums of its four R, G and B values.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static byte ChromaV(Coefficients c, int sumR, int sumG, int sumB) =>
        Clamp(((c.VR * sumR) + (c.VG * sumG) + (c.VB * sumB) + (128 << ChromaShift) + ChromaHalf) >> ChromaShift);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);

    /// <summary>R'G'B' to Y'CbCr coefficients with <see cref="Shift"/> fractional bits.</summary>
    internal readonly record struct Coefficients(
        int LumaOffset,
        int YR,
        int YG,
        int YB,
        int UR,
        int UG,
        int UB,
        int VR,
        int VG,
        int VB)
    {
        internal static Coefficients For(VideoYuvEncoding encoding)
        {
            (double kr, double kb) = encoding.Bt709 ? (0.2126, 0.0722) : (0.299, 0.114);
            double lumaScale = encoding.FullRange ? 1.0 : 219.0 / 255.0;
            double chromaScale = encoding.FullRange ? 1.0 : 224.0 / 255.0;

            // Derive one coefficient of each row from the others so the sums are exact: R=G=B
            // gives exactly the scaled grey level and zero chroma.
            int yr = Fixed(lumaScale * kr);
            int yb = Fixed(lumaScale * kb);
            int yg = Fixed(lumaScale) - yr - yb;
            int ur = -Fixed(chromaScale * kr / (2 * (1 - kb)));
            int ub = Fixed(chromaScale / 2);
            int ug = -(ur + ub);
            int vr = Fixed(chromaScale / 2);
            int vb = -Fixed(chromaScale * kb / (2 * (1 - kr)));
            int vg = -(vr + vb);
            return new Coefficients(encoding.FullRange ? 0 : 16, yr, yg, yb, ur, ug, ub, vr, vg, vb);
        }

        private static int Fixed(double value) => (int)Math.Round(value * (1 << Shift));
    }
}
