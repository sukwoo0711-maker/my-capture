using MyCapture.Platform.Recording;

namespace MyCapture.App.Recording;

/// <summary>
/// Deterministic Y'CbCr 4:2:0 (packed NV12) to opaque BGRA conversion for GIF export that
/// reproduces the Media Foundation software colour converter.
/// </summary>
/// <remarks>
/// <para>
/// The Source Reader's software RGB32 converter cost about 90% of the old 1080p GIF export.
/// Reading the decoder's own NV12 instead and converting here keeps that cost off the decode
/// thread. GPU and CPU H.264 decoders produce bit-identical NV12, so hardware and software
/// decode give the same GIF bytes.
/// </para>
/// <para>
/// The arithmetic is the classic 8-bit fixed-point formula with replicated chroma, which matched
/// the system converter on every sample of the shared 1080p clip (BT.709, studio range). The WIC
/// GIF quantiser is very sensitive to near-duplicate colours: bilinear chroma (0.16 mean
/// difference) or the GPU video processor cost the 960 px GIF 8 dB, so exact reproduction is
/// the goal rather than a "better" filter.
/// </para>
/// <para>
/// A 2x reduction converts each pixel, then averages each 2x2 block with round-half-up — the
/// same bytes as converting at full size and area averaging (the old WPF 2x scale). Other sizes
/// convert at full size and area-average. Integer arithmetic keeps every thread identical.
/// </para>
/// </remarks>
internal static class Nv12FrameConverter
{
    private const int Shift = 8;
    private const int Rounding = 1 << (Shift - 1);

    /// <summary>Converts a packed NV12 frame to a new <paramref name="width"/> x <paramref name="height"/> BGRA frame.</summary>
    internal static byte[] ToBgra(
        byte[] nv12,
        int sourceWidth,
        int sourceHeight,
        VideoYuvEncoding encoding,
        int width,
        int height)
    {
        ArgumentNullException.ThrowIfNull(nv12);
        if (sourceWidth < 1 || sourceHeight < 1 || width < 1 || height < 1
            || width > sourceWidth || height > sourceHeight)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Only same-size or smaller frames are supported.");
        }

        int chromaRowBytes = ((sourceWidth + 1) / 2) * 2;
        int chromaRows = (sourceHeight + 1) / 2;
        if (nv12.Length < checked((sourceWidth * sourceHeight) + (chromaRowBytes * chromaRows)))
        {
            throw new ArgumentException("The NV12 frame is smaller than its stated dimensions.", nameof(nv12));
        }

        Coefficients coefficients = Coefficients.For(encoding);
        bool evenSize = (sourceWidth & 1) == 0 && (sourceHeight & 1) == 0;
        if (evenSize && width <= sourceWidth / 2 && height <= sourceHeight / 2)
        {
            byte[] half = Halve(nv12, sourceWidth, sourceHeight, coefficients);
            return width == sourceWidth / 2 && height == sourceHeight / 2
                ? half
                : BgraFrameScaler.Downscale(half, sourceWidth / 2, sourceHeight / 2, sourceWidth * 2, width, height);
        }

        byte[] full = ConvertFullSize(nv12, sourceWidth, sourceHeight, coefficients);
        return width == sourceWidth && height == sourceHeight
            ? full
            : BgraFrameScaler.Downscale(full, sourceWidth, sourceHeight, sourceWidth * 4, width, height);
    }

    /// <summary>
    /// Converts the four pixels of each 2x2 block (which share one chroma sample) and stores
    /// their rounded mean, exactly like full-size conversion followed by a 2x area average.
    /// </summary>
    private static byte[] Halve(byte[] nv12, int sourceWidth, int sourceHeight, Coefficients c)
    {
        int width = sourceWidth / 2;
        int height = sourceHeight / 2;
        int chromaOffset = sourceWidth * sourceHeight;
        byte[] output = GC.AllocateUninitializedArray<byte>(checked(width * height * 4));
        for (int y = 0; y < height; y++)
        {
            int top = 2 * y * sourceWidth;
            int bottom = top + sourceWidth;
            int chroma = chromaOffset + (y * sourceWidth);
            int target = y * width * 4;
            for (int x = 0; x < width; x++, target += 4)
            {
                int column = 2 * x;
                int u = nv12[chroma + column] - 128;
                int v = nv12[chroma + column + 1] - 128;
                int blue = (c.BlueU * u) + Rounding;
                int green = Rounding - (c.GreenU * u) - (c.GreenV * v);
                int red = (c.RedV * v) + Rounding;
                int l0 = c.Luma * (nv12[top + column] - c.LumaOffset);
                int l1 = c.Luma * (nv12[top + column + 1] - c.LumaOffset);
                int l2 = c.Luma * (nv12[bottom + column] - c.LumaOffset);
                int l3 = c.Luma * (nv12[bottom + column + 1] - c.LumaOffset);
                output[target] = Mean(Channel(l0 + blue), Channel(l1 + blue), Channel(l2 + blue), Channel(l3 + blue));
                output[target + 1] = Mean(Channel(l0 + green), Channel(l1 + green), Channel(l2 + green), Channel(l3 + green));
                output[target + 2] = Mean(Channel(l0 + red), Channel(l1 + red), Channel(l2 + red), Channel(l3 + red));
                output[target + 3] = 255;
            }
        }

        return output;
    }

    private static byte[] ConvertFullSize(byte[] nv12, int width, int height, Coefficients c)
    {
        int chromaOffset = width * height;
        int chromaStride = ((width + 1) / 2) * 2;
        byte[] output = GC.AllocateUninitializedArray<byte>(checked(width * height * 4));
        for (int y = 0; y < height; y++)
        {
            int luma = y * width;
            int chroma = chromaOffset + ((y >> 1) * chromaStride);
            int target = y * width * 4;
            for (int x = 0; x < width; x++, target += 4)
            {
                int pair = chroma + ((x >> 1) * 2);
                int u = nv12[pair] - 128;
                int v = nv12[pair + 1] - 128;
                int l = c.Luma * (nv12[luma + x] - c.LumaOffset);
                output[target] = (byte)Channel(l + (c.BlueU * u) + Rounding);
                output[target + 1] = (byte)Channel(l + Rounding - (c.GreenU * u) - (c.GreenV * v));
                output[target + 2] = (byte)Channel(l + (c.RedV * v) + Rounding);
                output[target + 3] = 255;
            }
        }

        return output;
    }

    private static int Channel(int fixedPoint) => Math.Clamp(fixedPoint >> Shift, 0, 255);

    private static byte Mean(int a, int b, int c, int d) => (byte)((a + b + c + d + 2) >> 2);

    /// <summary>8-bit fixed-point Y'CbCr to R'G'B' coefficients (green terms are subtracted).</summary>
    internal readonly record struct Coefficients(int LumaOffset, int Luma, int RedV, int GreenU, int GreenV, int BlueU)
    {
        /// <summary>
        /// Studio range yields the familiar 298/409/100/208/516 (BT.601) and
        /// 298/459/55/136/541 (BT.709) integers.
        /// </summary>
        internal static Coefficients For(VideoYuvEncoding encoding)
        {
            (double kr, double kb) = encoding.Bt709 ? (0.2126, 0.0722) : (0.299, 0.114);
            double kg = 1 - kr - kb;
            double lumaScale = encoding.FullRange ? 1.0 : 255.0 / 219.0;
            double chromaScale = encoding.FullRange ? 1.0 : 255.0 / 224.0;
            return new Coefficients(
                encoding.FullRange ? 0 : 16,
                Fixed(lumaScale),
                Fixed(2 * (1 - kr) * chromaScale),
                Fixed(2 * (1 - kb) * kb / kg * chromaScale),
                Fixed(2 * (1 - kr) * kr / kg * chromaScale),
                Fixed(2 * (1 - kb) * chromaScale));
        }

        private static int Fixed(double value) => (int)Math.Round(value * (1 << Shift));
    }
}
