namespace MyCapture.Core.Capture;

/// <summary>
/// A synthetic scRGB picture with content up to a display peak, for comparing tone operators
/// without an HDR display.
/// </summary>
/// <remarks>
/// Seven ramps (grey, red, green, blue, orange, sky blue, skin) run from black to SDR white over
/// the left half, evenly in sRGB code values, and from SDR white to the peak over the right half,
/// evenly in stops. Below them is a row of full-brightness SDR patches, and at the bottom a sky
/// with a sun whose centre reaches the peak.
/// </remarks>
public static class HdrTestPattern
{
    /// <summary>Linear RGB of each ramp, top to bottom.</summary>
    public static IReadOnlyList<(double Red, double Green, double Blue)> RampColours { get; } =
    [
        (1.0, 1.0, 1.0),
        (1.0, 0.0, 0.0),
        (0.0, 1.0, 0.0),
        (0.0, 0.0, 1.0),
        (1.0, 0.45, 0.1),
        (0.25, 0.6, 1.0),
        (1.0, 0.72, 0.56),
    ];

    private static readonly (double Red, double Green, double Blue)[] Patches =
    [
        (1.0, 1.0, 1.0), (1.0, 1.0, 0.0), (0.0, 1.0, 1.0), (0.0, 1.0, 0.0),
        (1.0, 0.0, 1.0), (1.0, 0.0, 0.0), (0.0, 0.0, 1.0), (0.5, 0.5, 0.5),
    ];

    /// <summary>Rows each ramp occupies in a picture of <paramref name="height"/> rows.</summary>
    public static int RampHeight(int height) => height / 12;

    /// <summary>
    /// Brightness of ramp column <paramref name="x"/> in units of SDR white: 0..1 over the left
    /// half, 1..peak over the right half.
    /// </summary>
    public static double RampLevel(int x, int width, double peakLevel)
    {
        double position = width <= 1 ? 0 : x / (double)(width - 1);
        if (position <= 0.5)
        {
            return HdrToneMap.DecodeSrgb((byte)Math.Round(position * 2 * 255));
        }

        return Math.Pow(Math.Max(1.0, peakLevel), (position - 0.5) * 2);
    }

    /// <summary>
    /// Renders the pattern as R16G16B16A16 half-float rows, <c>width * 8</c> bytes apart.
    /// </summary>
    public static byte[] Render(int width, int height, double sdrWhiteNits, double peakNits)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 16);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 24);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sdrWhiteNits);
        ArgumentOutOfRangeException.ThrowIfLessThan(peakNits, sdrWhiteNits);
        double white = sdrWhiteNits / HdrToneMap.NitsPerScRgbUnit;
        double peakLevel = peakNits / sdrWhiteNits;
        int rampHeight = RampHeight(height);
        int patchTop = rampHeight * RampColours.Count;
        int sceneTop = patchTop + rampHeight;
        var pixels = new byte[checked(width * height * 8)];
        Span<ushort> halves = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(pixels.AsSpan());
        ushort opaque = BitConverter.HalfToUInt16Bits((Half)1.0f);
        double sunX = width * 0.72, sunY = sceneTop + ((height - sceneTop) * 0.45);
        double sunRadius = Math.Max(4.0, (height - sceneTop) * 0.28);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double red, green, blue;
                if (y < patchTop)
                {
                    (double r, double g, double b) = RampColours[Math.Min(RampColours.Count - 1, y / Math.Max(1, rampHeight))];
                    double level = RampLevel(x, width, peakLevel);
                    (red, green, blue) = (r * level, g * level, b * level);
                }
                else if (y < sceneTop)
                {
                    (red, green, blue) = Patches[Math.Min(Patches.Length - 1, x * Patches.Length / width)];
                }
                else
                {
                    double depth = (y - sceneTop) / (double)Math.Max(1, height - sceneTop - 1);
                    double sky = 0.55 - (0.35 * depth);
                    double dx = x - sunX, dy = y - sunY;
                    double sun = (peakLevel - sky) * Math.Exp(-((dx * dx) + (dy * dy)) / (2 * sunRadius * sunRadius));
                    (red, green, blue) = ((0.35 * sky) + sun, (0.6 * sky) + (0.88 * sun), sky + (0.62 * sun));
                }

                int offset = ((y * width) + x) * 4;
                halves[offset] = BitConverter.HalfToUInt16Bits((Half)(red * white));
                halves[offset + 1] = BitConverter.HalfToUInt16Bits((Half)(green * white));
                halves[offset + 2] = BitConverter.HalfToUInt16Bits((Half)(blue * white));
                halves[offset + 3] = opaque;
            }
        }

        return pixels;
    }
}
