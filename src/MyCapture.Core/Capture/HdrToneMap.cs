using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MyCapture.Core.Capture;

/// <summary>How a pixel brighter than SDR white is brought into the SDR range.</summary>
public enum HdrToneOperator
{
    /// <summary>
    /// Divides the pixel by its largest channel, so that channel lands on SDR white. Hue and
    /// saturation are kept, and every pixel at or below SDR white keeps the value it has with HDR
    /// off. Everything brighter than SDR white becomes the brightest SDR colour of the same hue.
    /// </summary>
    MaxChannel = 0,

    /// <summary>
    /// Rolls the range between the knee and the display peak off into the range between the knee
    /// and SDR white. Highlights keep their gradation, at the cost of darkening SDR values above
    /// the knee. Kept for comparison in diagnostics; captures use <see cref="MaxChannel"/>.
    /// </summary>
    SoftKnee = 1,
}

/// <summary>
/// How a duplicated output surface is rotated relative to the desktop. The values are those of
/// <c>DXGI_MODE_ROTATION</c>.
/// </summary>
public enum HdrSurfaceRotation
{
    Identity = 1,
    Rotate90 = 2,
    Rotate180 = 3,
    Rotate270 = 4,
}

/// <summary>
/// Converts linear scRGB half-float pixels of an HDR desktop into SDR sRGB BGRA8.
/// </summary>
/// <remarks>
/// <para>
/// With HDR on, Windows composes the desktop in scRGB: linear light, BT.709 primaries, 1.0 = 80 nits.
/// SDR content is decoded with the sRGB transfer function and multiplied by the display's SDR white
/// level, so SDR white sits at <c>white / 80</c>. Dividing by that multiplier and encoding with the
/// same transfer function gives back the SDR value the content has with HDR off.
/// </para>
/// <para>
/// A half has only 65,536 bit patterns, so a pixel whose three channels are at or below the direct
/// limit is three table reads, and its result is the exactly rounded sRGB value. A pixel with a
/// brighter channel is scaled as a whole (<see cref="HdrToneOperator"/>) before it is encoded; a
/// per-channel clip would turn a bright orange yellow. Negative channels (colours outside sRGB)
/// and NaN become 0, the nearest value the output can hold. Alpha is written opaque.
/// </para>
/// <para>No hardware, no WPF: the display read lives in the platform layer.</para>
/// </remarks>
public sealed class HdrToneMap
{
    /// <summary>scRGB 1.0 is 80 nits.</summary>
    public const double NitsPerScRgbUnit = 80.0;

    /// <summary>Peak assumed for <see cref="HdrToneOperator.SoftKnee"/> when the display reports none.</summary>
    public const double DefaultPeakNits = 1000.0;

    /// <summary>Where the soft knee starts, as a fraction of SDR white in linear light.</summary>
    public const double SoftKneeStart = 0.75;

    private const int HalfPatterns = 65536;
    private const int SignBit = 0x8000;

    // The methods that run per pixel are compiled fully optimised on first use. Left to the
    // runtime's tiers they pass through its instrumented stage, whose counters are shared by all
    // conversion threads: one or two captures of a session then took about 20 ms instead of 3 ms
    // to convert 3440x1440 on 16 threads.
    private const MethodImplOptions PerPixel = MethodImplOptions.AggressiveOptimization;

    // Decoded halves with negative values and NaN already clamped to 0; +infinity is kept.
    private static readonly Lazy<float[]> PositiveHalves = new(BuildPositiveHalves);

    // sRGB byte of i / 65535, for channels of a pixel that has been scaled into 0..1.
    private static readonly Lazy<byte[]> UnitEncoding = new(BuildUnitEncoding);

    private readonly byte[] _direct = new byte[HalfPatterns];
    private readonly float[] _halves;
    private readonly byte[] _unit;
    private readonly int _directLimit;
    private readonly float _inverseWhite;
    private readonly bool _softKnee;
    private readonly float _peakLevel;
    private readonly float _kneeGain;

    private HdrToneMap(double sdrWhiteNits, HdrToneOperator toneOperator, double peakNits)
    {
        SdrWhiteNits = sdrWhiteNits;
        PeakNits = peakNits;
        double white = sdrWhiteNits / NitsPerScRgbUnit;
        double peakLevel = peakNits / sdrWhiteNits;
        // A display that cannot exceed SDR white has no highlight range to roll off.
        _softKnee = toneOperator == HdrToneOperator.SoftKnee && peakLevel > 1.0;
        Operator = _softKnee ? HdrToneOperator.SoftKnee : HdrToneOperator.MaxChannel;
        _inverseWhite = (float)(1.0 / white);
        _peakLevel = (float)peakLevel;
        _kneeGain = (float)((peakLevel - SoftKneeStart) / (1.0 - SoftKneeStart));
        _halves = PositiveHalves.Value;
        _unit = UnitEncoding.Value;
        for (int bits = 0; bits < HalfPatterns; bits++)
        {
            _direct[bits] = EncodeSrgb(HalfToSingle((ushort)bits) / white);
        }

        // Positive halves order like their bit patterns; flipping the sign bit puts every negative
        // value below every positive one, so one comparison per pixel finds a channel above the limit.
        _directLimit = LargestHalfAtMost(white * (_softKnee ? SoftKneeStart : 1.0)) ^ SignBit;
    }

    /// <summary>SDR white of the display the pixels were composed for, in nits.</summary>
    public double SdrWhiteNits { get; }

    /// <summary>Display peak used by <see cref="HdrToneOperator.SoftKnee"/>, in nits.</summary>
    public double PeakNits { get; }

    /// <summary>The operator in effect; a soft knee without highlight range falls back to <see cref="HdrToneOperator.MaxChannel"/>.</summary>
    public HdrToneOperator Operator { get; }

    /// <summary>scRGB value of SDR white: the factor Windows multiplied SDR content by.</summary>
    public double WhiteMultiplier => SdrWhiteNits / NitsPerScRgbUnit;

    /// <param name="sdrWhiteNits">The display's SDR white level; 80 for a desktop composed without HDR.</param>
    /// <param name="toneOperator">Treatment of pixels brighter than SDR white.</param>
    /// <param name="peakNits">Display peak luminance, used only by the soft knee.</param>
    public static HdrToneMap Create(
        double sdrWhiteNits,
        HdrToneOperator toneOperator = HdrToneOperator.MaxChannel,
        double peakNits = DefaultPeakNits)
    {
        if (!double.IsFinite(sdrWhiteNits) || sdrWhiteNits <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sdrWhiteNits));
        }

        if (!Enum.IsDefined(toneOperator))
        {
            throw new ArgumentOutOfRangeException(nameof(toneOperator));
        }

        if (!double.IsFinite(peakNits) || peakNits <= 0)
        {
            peakNits = DefaultPeakNits;
        }

        return new HdrToneMap(sdrWhiteNits, toneOperator, peakNits);
    }

    /// <summary>Decodes an IEEE 754 binary16 bit pattern.</summary>
    public static float HalfToSingle(ushort bits) => (float)BitConverter.UInt16BitsToHalf(bits);

    /// <summary>
    /// sRGB transfer function of a linear value, rounded to the nearest of 0..255. Values outside
    /// 0..1 and NaN are clamped.
    /// </summary>
    public static byte EncodeSrgb(double linear)
    {
        if (!(linear > 0))
        {
            return 0;
        }

        if (linear >= 1)
        {
            return 255;
        }

        double encoded = linear <= 0.0031308
            ? linear * 12.92
            : (1.055 * Math.Pow(linear, 1.0 / 2.4)) - 0.055;
        return (byte)((encoded * 255.0) + 0.5);
    }

    /// <summary>Inverse of <see cref="EncodeSrgb"/> for one 8-bit value: its linear light in 0..1.</summary>
    public static double DecodeSrgb(byte value)
    {
        double encoded = value / 255.0;
        return encoded <= 0.04045 ? encoded / 12.92 : Math.Pow((encoded + 0.055) / 1.055, 2.4);
    }

    /// <summary>
    /// The conversion in double precision, without tables: linear scRGB in, linear SDR-relative
    /// channels in 0..1 out. The table-driven path is checked against this.
    /// </summary>
    public (double Red, double Green, double Blue) ToneLinear(double red, double green, double blue)
    {
        double white = WhiteMultiplier;
        double r = red > 0 ? red : 0, g = green > 0 ? green : 0, b = blue > 0 ? blue : 0;
        double max = Math.Max(r, Math.Max(g, b));
        double limit = white * (_softKnee ? SoftKneeStart : 1.0);
        if (max <= limit)
        {
            return (r / white, g / white, b / white);
        }

        if (double.IsPositiveInfinity(max))
        {
            return (r == max ? 1 : 0, g == max ? 1 : 0, b == max ? 1 : 0);
        }

        double scale = TargetLevel(max / white) / max;
        return (r * scale, g * scale, b * scale);
    }

    /// <summary>
    /// Converts destination rows <paramref name="firstRow"/> to <c>firstRow + rowCount - 1</c>.
    /// Disjoint row ranges may be converted on different threads.
    /// </summary>
    /// <param name="surface">
    /// Rows of R16G16B16A16 half-float pixels as read from the output surface, which is unrotated:
    /// a portrait desktop arrives as a landscape surface.
    /// </param>
    /// <param name="surfaceStride">Bytes between surface rows.</param>
    /// <param name="surfaceWidth">Surface width in pixels.</param>
    /// <param name="surfaceHeight">Surface height in pixels.</param>
    /// <param name="rotation">Rotation of the surface relative to the desktop.</param>
    /// <param name="destination">Top-down BGRA8 rows in desktop orientation, starting at row 0.</param>
    /// <param name="destinationStride">Bytes between destination rows.</param>
    /// <param name="firstRow">First destination row to write.</param>
    /// <param name="rowCount">Number of destination rows to write.</param>
    /// <returns>Pixels that were brighter than the direct limit and therefore scaled.</returns>
    [MethodImpl(PerPixel)]
    public long ConvertRows(
        ReadOnlySpan<byte> surface,
        int surfaceStride,
        int surfaceWidth,
        int surfaceHeight,
        HdrSurfaceRotation rotation,
        Span<byte> destination,
        int destinationStride,
        int firstRow,
        int rowCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(surfaceWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(surfaceHeight);
        (int width, int height) = HdrSurfaceGeometry.DesktopSize(surfaceWidth, surfaceHeight, rotation);
        int surfaceRowBytes = checked(surfaceWidth * 8);
        int rowBytes = checked(width * 4);
        ArgumentOutOfRangeException.ThrowIfLessThan(surfaceStride, surfaceRowBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(destinationStride, rowBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(firstRow);
        ArgumentOutOfRangeException.ThrowIfNegative(rowCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rowCount, height - firstRow);
        if (surface.Length < checked(((surfaceHeight - 1) * surfaceStride) + surfaceRowBytes))
        {
            throw new ArgumentException("The surface buffer is too small.", nameof(surface));
        }

        if (destination.Length < checked(((height - 1) * destinationStride) + rowBytes))
        {
            throw new ArgumentException("The destination buffer is too small.", nameof(destination));
        }

        long scaled = 0;
        for (int y = firstRow; y < firstRow + rowCount; y++)
        {
            Span<byte> target = destination.Slice(y * destinationStride, rowBytes);
            if (rotation == HdrSurfaceRotation.Identity)
            {
                scaled += ConvertRow(MemoryMarshal.Cast<byte, ushort>(surface.Slice(y * surfaceStride, surfaceRowBytes)), target);
                continue;
            }

            // Inverse of the clockwise rotation DXGI reports: where destination pixel (x, y) is on the surface.
            (int start, int step) = rotation switch
            {
                HdrSurfaceRotation.Rotate90 => (((surfaceHeight - 1) * surfaceStride) + (y * 8), -surfaceStride),
                HdrSurfaceRotation.Rotate180 => (((surfaceHeight - 1 - y) * surfaceStride) + ((surfaceWidth - 1) * 8), -8),
                _ => ((surfaceWidth - 1 - y) * 8, surfaceStride),
            };
            for (int x = 0, offset = start; x < width; x++, offset += step)
            {
                ReadOnlySpan<ushort> pixel = MemoryMarshal.Cast<byte, ushort>(surface.Slice(offset, 8));
                scaled += ConvertPixel(pixel[0], pixel[1], pixel[2], target.Slice(x * 4, 4));
            }
        }

        return scaled;
    }

    [MethodImpl(PerPixel)]
    private long ConvertRow(ReadOnlySpan<ushort> source, Span<byte> target)
    {
        ReadOnlySpan<byte> direct = _direct;
        int limit = _directLimit;
        long scaled = 0;
        for (int x = 0; x < target.Length / 4; x++)
        {
            ReadOnlySpan<ushort> pixel = source.Slice(x * 4, 4);
            Span<byte> output = target.Slice(x * 4, 4);
            ushort red = pixel[0], green = pixel[1], blue = pixel[2];
            if (Math.Max(red ^ SignBit, Math.Max(green ^ SignBit, blue ^ SignBit)) <= limit)
            {
                output[0] = direct[blue];
                output[1] = direct[green];
                output[2] = direct[red];
                output[3] = 255;
            }
            else
            {
                ToneBright(red, green, blue, output);
                scaled++;
            }
        }

        return scaled;
    }

    [MethodImpl(PerPixel)]
    private int ConvertPixel(ushort red, ushort green, ushort blue, Span<byte> output)
    {
        if (Math.Max(red ^ SignBit, Math.Max(green ^ SignBit, blue ^ SignBit)) <= _directLimit)
        {
            output[0] = _direct[blue];
            output[1] = _direct[green];
            output[2] = _direct[red];
            output[3] = 255;
            return 0;
        }

        ToneBright(red, green, blue, output);
        return 1;
    }

    [MethodImpl(PerPixel)]
    private void ToneBright(ushort redBits, ushort greenBits, ushort blueBits, Span<byte> output)
    {
        float red = _halves[redBits], green = _halves[greenBits], blue = _halves[blueBits];
        float max = MathF.Max(red, MathF.Max(green, blue));
        output[3] = 255;
        if (max * _inverseWhite <= (_softKnee ? (float)SoftKneeStart : 1f))
        {
            // The channel that tripped the limit was NaN: nothing here is brighter than the limit.
            output[0] = _direct[blueBits];
            output[1] = _direct[greenBits];
            output[2] = _direct[redBits];
            return;
        }

        if (float.IsPositiveInfinity(max))
        {
            output[0] = blue == max ? (byte)255 : (byte)0;
            output[1] = green == max ? (byte)255 : (byte)0;
            output[2] = red == max ? (byte)255 : (byte)0;
            return;
        }

        float scale = (float)TargetLevel(max * _inverseWhite) / max * (HalfPatterns - 1);
        output[0] = _unit[Math.Min(HalfPatterns - 1, (int)((blue * scale) + 0.5f))];
        output[1] = _unit[Math.Min(HalfPatterns - 1, (int)((green * scale) + 0.5f))];
        output[2] = _unit[Math.Min(HalfPatterns - 1, (int)((red * scale) + 0.5f))];
    }

    /// <summary>
    /// SDR-relative level (0..1) the largest channel of a bright pixel is brought to. The soft knee
    /// leaves the knee with slope 1 and reaches 1 at the peak, so the curve has no kink at either end
    /// of the SDR range it changes.
    /// </summary>
    [MethodImpl(PerPixel)]
    private double TargetLevel(double level)
    {
        if (!_softKnee || level >= _peakLevel)
        {
            return 1.0;
        }

        double position = (level - SoftKneeStart) / (_peakLevel - SoftKneeStart);
        return SoftKneeStart
            + ((1.0 - SoftKneeStart) * (_kneeGain * position) / (1.0 + ((_kneeGain - 1.0) * position)));
    }

    private static ushort LargestHalfAtMost(double value)
    {
        const ushort largestFinite = 0x7BFF;
        if (value >= (double)BitConverter.UInt16BitsToHalf(largestFinite))
        {
            return largestFinite;
        }

        ushort bits = BitConverter.HalfToUInt16Bits((Half)value);
        return (double)BitConverter.UInt16BitsToHalf(bits) > value ? (ushort)(bits - 1) : bits;
    }

    private static float[] BuildPositiveHalves()
    {
        var values = new float[HalfPatterns];
        for (int bits = 0; bits < HalfPatterns; bits++)
        {
            float value = HalfToSingle((ushort)bits);
            values[bits] = value > 0 ? value : 0;
        }

        return values;
    }

    private static byte[] BuildUnitEncoding()
    {
        var table = new byte[HalfPatterns];
        for (int index = 0; index < HalfPatterns; index++)
        {
            table[index] = EncodeSrgb(index / (double)(HalfPatterns - 1));
        }

        return table;
    }
}

/// <summary>A rectangle on an unrotated output surface, in pixels; right and bottom are exclusive.</summary>
public readonly record struct HdrSurfaceBox(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;
}

/// <summary>Pixel geometry between a rotated desktop and the unrotated surface it is duplicated as.</summary>
public static class HdrSurfaceGeometry
{
    /// <summary>Size in desktop orientation of a surface: a quarter turn swaps width and height.</summary>
    public static (int Width, int Height) DesktopSize(int surfaceWidth, int surfaceHeight, HdrSurfaceRotation rotation) =>
        rotation switch
        {
            HdrSurfaceRotation.Identity or HdrSurfaceRotation.Rotate180 => (surfaceWidth, surfaceHeight),
            HdrSurfaceRotation.Rotate90 or HdrSurfaceRotation.Rotate270 => (surfaceHeight, surfaceWidth),
            _ => throw new ArgumentOutOfRangeException(nameof(rotation)),
        };

    /// <summary>
    /// Where a rectangle of the output, given in desktop orientation relative to the output's
    /// top-left corner, lies on the surface. Reading that box and converting it with the same
    /// rotation yields the rectangle.
    /// </summary>
    public static HdrSurfaceBox ToSurface(
        int x, int y, int width, int height, int desktopWidth, int desktopHeight, HdrSurfaceRotation rotation)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(x);
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, desktopWidth - x);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(height, desktopHeight - y);
        return rotation switch
        {
            HdrSurfaceRotation.Identity => new(x, y, x + width, y + height),
            HdrSurfaceRotation.Rotate90 => new(y, desktopWidth - x - width, y + height, desktopWidth - x),
            HdrSurfaceRotation.Rotate180 => new(
                desktopWidth - x - width, desktopHeight - y - height, desktopWidth - x, desktopHeight - y),
            HdrSurfaceRotation.Rotate270 => new(desktopHeight - y - height, x, desktopHeight - y, x + width),
            _ => throw new ArgumentOutOfRangeException(nameof(rotation)),
        };
    }
}
