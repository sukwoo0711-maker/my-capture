using System.Runtime.InteropServices;
using MyCapture.Core.Capture;
using Xunit;

namespace MyCapture.Core.Tests;

/// <summary>
/// The scRGB to SDR conversion of HDR displays: exact SDR values at every SDR white level,
/// half-float decoding, hue-preserving treatment of pixels brighter than SDR white, non-finite
/// and negative input, surface rotation, and the diagnostic switch.
/// </summary>
public sealed class HdrToneMapTests
{
    private static ushort Half(double value) => BitConverter.HalfToUInt16Bits((Half)value);

    private static byte[] Surface(params (double Red, double Green, double Blue)[] pixels)
    {
        var bytes = new byte[pixels.Length * 8];
        Span<ushort> halves = MemoryMarshal.Cast<byte, ushort>(bytes.AsSpan());
        for (int index = 0; index < pixels.Length; index++)
        {
            halves[index * 4] = Half(pixels[index].Red);
            halves[(index * 4) + 1] = Half(pixels[index].Green);
            halves[(index * 4) + 2] = Half(pixels[index].Blue);
            halves[(index * 4) + 3] = Half(1.0);
        }

        return bytes;
    }

    /// <summary>Converts one row of pixels and returns them as (red, green, blue, alpha).</summary>
    private static (byte Red, byte Green, byte Blue, byte Alpha)[] ConvertRow(HdrToneMap map, byte[] surface, out long scaled)
    {
        int width = surface.Length / 8;
        var bgra = new byte[width * 4];
        scaled = map.ConvertRows(surface, surface.Length, width, 1, HdrSurfaceRotation.Identity, bgra, bgra.Length, 0, 1);
        return Enumerable.Range(0, width)
            .Select(x => (bgra[(x * 4) + 2], bgra[(x * 4) + 1], bgra[x * 4], bgra[(x * 4) + 3]))
            .ToArray();
    }

    [Theory]
    [InlineData(80)]
    [InlineData(200)]
    [InlineData(240)]
    [InlineData(480)]
    public void SdrContentComesBackAsItsExactSrgbValueAtEveryWhiteLevel(double whiteNits)
    {
        HdrToneMap map = HdrToneMap.Create(whiteNits);
        double multiplier = whiteNits / 80.0;
        Assert.Equal(multiplier, map.WhiteMultiplier);
        var pixels = new List<(double, double, double)>();
        for (int value = 0; value < 256; value++)
        {
            // Windows composes SDR content as its sRGB-decoded light times the white multiplier.
            double level = HdrToneMap.DecodeSrgb((byte)value) * multiplier;
            pixels.Add((level, level, level));
            pixels.Add((level, HdrToneMap.DecodeSrgb((byte)(255 - value)) * multiplier, HdrToneMap.DecodeSrgb(128) * multiplier));
        }

        (byte Red, byte Green, byte Blue, byte Alpha)[] converted = ConvertRow(map, Surface([.. pixels]), out long scaled);

        Assert.Equal(0, scaled);
        for (int value = 0; value < 256; value++)
        {
            Assert.Equal(((byte)value, (byte)value, (byte)value, (byte)255), converted[value * 2]);
            Assert.Equal(((byte)value, (byte)(255 - value), (byte)128, (byte)255), converted[(value * 2) + 1]);
        }
    }

    [Theory]
    [InlineData(80)]
    [InlineData(200)]
    [InlineData(240)]
    [InlineData(480)]
    public void SdrValuesSurviveAHalfFloatThatIsOneStepOff(double whiteNits)
    {
        // The compositor's own arithmetic may round a half the other way; the result must not flip.
        HdrToneMap map = HdrToneMap.Create(whiteNits);
        double multiplier = whiteNits / 80.0;
        for (int value = 1; value < 256; value++)
        {
            ushort exact = Half(HdrToneMap.DecodeSrgb((byte)value) * multiplier);
            foreach (int step in new[] { -1, 1 })
            {
                var surface = new byte[8];
                Span<ushort> halves = MemoryMarshal.Cast<byte, ushort>(surface.AsSpan());
                halves[0] = halves[1] = halves[2] = (ushort)(exact + step);
                (byte Red, byte Green, byte Blue, byte Alpha) pixel = ConvertRow(map, surface, out _)[0];
                Assert.Equal(((byte)value, (byte)value, (byte)value, (byte)255), pixel);
            }
        }
    }

    [Theory]
    [InlineData(0x0000, 0f)]
    [InlineData(0x8000, -0f)]
    [InlineData(0x3C00, 1f)]
    [InlineData(0xC000, -2f)]
    [InlineData(0x3800, 0.5f)]
    [InlineData(0x3555, 0.33325195f)]
    [InlineData(0x4400, 4f)]
    [InlineData(0x7BFF, 65504f)]
    [InlineData(0x0400, 6.1035156e-5f)]
    [InlineData(0x0001, 5.9604645e-8f)]
    [InlineData(0x03FF, 6.0975552e-5f)]
    [InlineData(0x7C00, float.PositiveInfinity)]
    [InlineData(0xFC00, float.NegativeInfinity)]
    public void HalfBitPatternsDecodeToTheirValue(int bits, float expected)
    {
        float decoded = HdrToneMap.HalfToSingle((ushort)bits);
        Assert.Equal(expected, decoded);
        Assert.Equal(float.IsNegative(expected), float.IsNegative(decoded));
    }

    [Fact]
    public void HalfNanPatternsDecodeToNan()
    {
        Assert.True(float.IsNaN(HdrToneMap.HalfToSingle(0x7E00)));
        Assert.True(float.IsNaN(HdrToneMap.HalfToSingle(0x7C01)));
        Assert.True(float.IsNaN(HdrToneMap.HalfToSingle(0xFE00)));
    }

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(1.0, 255)]
    [InlineData(0.5, 188)]
    [InlineData(0.0031308, 10)]
    [InlineData(0.2130, 127)]
    [InlineData(0.2158605, 128)]
    [InlineData(-0.25, 0)]
    [InlineData(7.0, 255)]
    [InlineData(double.NaN, 0)]
    [InlineData(double.PositiveInfinity, 255)]
    [InlineData(double.NegativeInfinity, 0)]
    public void SrgbEncodingIsTheTransferFunctionRoundedToNearest(double linear, int expected) =>
        Assert.Equal((byte)expected, HdrToneMap.EncodeSrgb(linear));

    [Fact]
    public void SrgbEncodingInvertsDecodingForEveryByte()
    {
        for (int value = 0; value < 256; value++)
            Assert.Equal((byte)value, HdrToneMap.EncodeSrgb(HdrToneMap.DecodeSrgb((byte)value)));
    }

    [Theory]
    [InlineData(80, HdrToneOperator.MaxChannel)]
    [InlineData(200, HdrToneOperator.MaxChannel)]
    [InlineData(480, HdrToneOperator.MaxChannel)]
    [InlineData(80, HdrToneOperator.SoftKnee)]
    [InlineData(200, HdrToneOperator.SoftKnee)]
    [InlineData(480, HdrToneOperator.SoftKnee)]
    public void GreyNeverGetsDarkerAsTheInputGetsBrighter(double whiteNits, HdrToneOperator toneOperator)
    {
        HdrToneMap map = HdrToneMap.Create(whiteNits, toneOperator, peakNits: 1000);
        // Every positive finite half in ascending order, then infinity.
        var surface = new byte[(0x7C00 + 1) * 8];
        Span<ushort> halves = MemoryMarshal.Cast<byte, ushort>(surface.AsSpan());
        for (int bits = 0; bits <= 0x7C00; bits++)
            halves[bits * 4] = halves[(bits * 4) + 1] = halves[(bits * 4) + 2] = (ushort)bits;

        (byte Red, byte Green, byte Blue, byte Alpha)[] converted = ConvertRow(map, surface, out _);

        Assert.Equal(((byte)0, (byte)0, (byte)0, (byte)255), converted[0]);
        Assert.Equal(((byte)255, (byte)255, (byte)255, (byte)255), converted[^1]);
        for (int index = 1; index < converted.Length; index++)
        {
            Assert.True(converted[index].Red >= converted[index - 1].Red, $"grey got darker at half 0x{index:X4}");
            Assert.Equal(converted[index].Red, converted[index].Green);
            Assert.Equal(converted[index].Red, converted[index].Blue);
        }
    }

    public static TheoryData<double, double, double, double> BrightColours() => new()
    {
        // white nits, then the colour in units of SDR white
        { 200, 4.0, 1.0, 0.25 },
        { 200, 1.5, 0.3, 0.0 },
        { 240, 0.2, 3.0, 0.9 },
        { 480, 0.5, 0.7, 8.0 },
        { 80, 12.0, 6.0, 3.0 },
        { 200, 1.01, 1.0, 0.5 },
    };

    [Theory]
    [MemberData(nameof(BrightColours))]
    public void ColoursBrighterThanSdrWhiteKeepTheirChannelRatios(double whiteNits, double red, double green, double blue)
    {
        HdrToneMap map = HdrToneMap.Create(whiteNits);
        double multiplier = whiteNits / 80.0;

        (byte Red, byte Green, byte Blue, byte Alpha) pixel =
            ConvertRow(map, Surface((red * multiplier, green * multiplier, blue * multiplier)), out long scaled)[0];

        Assert.Equal(1, scaled);
        double max = Math.Max(red, Math.Max(green, blue));
        // The largest channel lands on SDR white; the others keep their ratio to it in linear light.
        // One code value of slack: the half holding the input and the 16-bit encode table both quantise.
        Assert.Equal((byte)255, Math.Max(pixel.Red, Math.Max(pixel.Green, pixel.Blue)));
        Assert.InRange(pixel.Red - HdrToneMap.EncodeSrgb(red / max), -1, 1);
        Assert.InRange(pixel.Green - HdrToneMap.EncodeSrgb(green / max), -1, 1);
        Assert.InRange(pixel.Blue - HdrToneMap.EncodeSrgb(blue / max), -1, 1);
        Assert.Equal((byte)255, pixel.Alpha);
        // Only the largest channel is at white; a per-channel clip would put every channel that
        // exceeds SDR white there and change the hue.
        Assert.Equal(1, (pixel.Red == 255 ? 1 : 0) + (pixel.Green == 255 ? 1 : 0) + (pixel.Blue == 255 ? 1 : 0));
    }

    [Fact]
    public void ABrightOrangeStaysOrangeInsteadOfClippingToYellow()
    {
        HdrToneMap map = HdrToneMap.Create(200);
        // Four times SDR white in red, once in green: a per-channel clip would return (255, 255, 137).
        (byte Red, byte Green, byte Blue, byte Alpha) pixel = ConvertRow(map, Surface((4.0 * 2.5, 1.0 * 2.5, 0.25 * 2.5)), out _)[0];

        Assert.Equal(((byte)255, (byte)137, (byte)71, (byte)255), pixel);
    }

    [Fact]
    public void NegativeNanAndInfiniteChannelsAreClampedWithoutDisturbingTheOthers()
    {
        HdrToneMap map = HdrToneMap.Create(80);
        byte half = HdrToneMap.EncodeSrgb(0.5);
        byte quarter = HdrToneMap.EncodeSrgb(0.25);
        (byte Red, byte Green, byte Blue, byte Alpha)[] converted = ConvertRow(map, Surface(
            (double.NaN, 0.5, 0.5),
            (-0.75, 0.5, 0.25),
            (double.NegativeInfinity, 0.25, 0.25),
            (double.PositiveInfinity, 0.5, 0.2),
            (double.PositiveInfinity, double.PositiveInfinity, 0.1),
            (double.NaN, double.NaN, double.NaN),
            (-1.0, -2.0, -3.0),
            (double.NaN, 4.0, 2.0),
            (-0.5, 4.0, 2.0)), out _);

        Assert.Equal(((byte)0, half, half, (byte)255), converted[0]);
        Assert.Equal(((byte)0, half, quarter, (byte)255), converted[1]);
        Assert.Equal(((byte)0, quarter, quarter, (byte)255), converted[2]);
        Assert.Equal(((byte)255, (byte)0, (byte)0, (byte)255), converted[3]);
        Assert.Equal(((byte)255, (byte)255, (byte)0, (byte)255), converted[4]);
        Assert.Equal(((byte)0, (byte)0, (byte)0, (byte)255), converted[5]);
        Assert.Equal(((byte)0, (byte)0, (byte)0, (byte)255), converted[6]);
        Assert.Equal(((byte)0, (byte)255, half, (byte)255), converted[7]);
        Assert.Equal(((byte)0, (byte)255, half, (byte)255), converted[8]);
    }

    [Theory]
    [InlineData(80, HdrToneOperator.MaxChannel)]
    [InlineData(203, HdrToneOperator.MaxChannel)]
    [InlineData(480, HdrToneOperator.MaxChannel)]
    [InlineData(203, HdrToneOperator.SoftKnee)]
    [InlineData(480, HdrToneOperator.SoftKnee)]
    public void TheTablePathAgreesWithTheDoublePrecisionReference(double whiteNits, HdrToneOperator toneOperator)
    {
        HdrToneMap map = HdrToneMap.Create(whiteNits, toneOperator, peakNits: 1200);
        double multiplier = map.WhiteMultiplier;
        double directLimit = multiplier * (map.Operator == HdrToneOperator.SoftKnee ? HdrToneMap.SoftKneeStart : 1.0);
        var random = new Random(20261111);
        var pixels = new (double Red, double Green, double Blue)[4096];
        for (int index = 0; index < pixels.Length; index++)
        {
            // Half the samples inside SDR, half reaching up to eight times SDR white.
            double reach = index % 2 == 0 ? multiplier : multiplier * 8;
            pixels[index] = (random.NextDouble() * reach, random.NextDouble() * reach, random.NextDouble() * reach);
        }

        byte[] surface = Surface(pixels);
        ReadOnlySpan<ushort> halves = MemoryMarshal.Cast<byte, ushort>(surface.AsSpan());
        (byte Red, byte Green, byte Blue, byte Alpha)[] converted = ConvertRow(map, surface, out long scaled);

        int bright = 0;
        for (int index = 0; index < pixels.Length; index++)
        {
            // The reference sees the value the half actually holds.
            double red = HdrToneMap.HalfToSingle(halves[index * 4]);
            double green = HdrToneMap.HalfToSingle(halves[(index * 4) + 1]);
            double blue = HdrToneMap.HalfToSingle(halves[(index * 4) + 2]);
            (double Red, double Green, double Blue) linear = map.ToneLinear(red, green, blue);
            bool direct = Math.Max(red, Math.Max(green, blue)) <= directLimit;
            bright += direct ? 0 : 1;
            int tolerance = direct ? 0 : 1;
            Assert.InRange(converted[index].Red - HdrToneMap.EncodeSrgb(linear.Red), -tolerance, tolerance);
            Assert.InRange(converted[index].Green - HdrToneMap.EncodeSrgb(linear.Green), -tolerance, tolerance);
            Assert.InRange(converted[index].Blue - HdrToneMap.EncodeSrgb(linear.Blue), -tolerance, tolerance);
        }

        Assert.Equal(bright, scaled);
        Assert.InRange(bright, 1000, 3500);
    }

    [Fact]
    public void TheSoftKneeKeepsHighlightGradationAndDarkensBrightSdrValues()
    {
        HdrToneMap clip = HdrToneMap.Create(200);
        HdrToneMap knee = HdrToneMap.Create(200, HdrToneOperator.SoftKnee, peakNits: 1000);
        Assert.Equal(HdrToneOperator.SoftKnee, knee.Operator);
        // Grey at 0.5, 0.75, 0.9, 1, 2, 3 and 5 times SDR white (5 is the peak), then beyond it.
        double[] levels = [0.5, 0.75, 0.9, 1.0, 2.0, 3.0, 5.0, 9.0];
        byte[] surface = Surface(levels.Select(level => (level * 2.5, level * 2.5, level * 2.5)).ToArray());

        byte[] clipped = ConvertRow(clip, surface, out _).Select(pixel => pixel.Red).ToArray();
        byte[] rolled = ConvertRow(knee, surface, out long scaled).Select(pixel => pixel.Red).ToArray();

        Assert.Equal(
            new byte[] { HdrToneMap.EncodeSrgb(0.5), HdrToneMap.EncodeSrgb(0.75), HdrToneMap.EncodeSrgb(0.9), 255, 255, 255, 255, 255 },
            clipped);
        // Up to the knee nothing changes; above it SDR values darken and highlights stay distinct.
        Assert.Equal(clipped[0], rolled[0]);
        Assert.Equal(clipped[1], rolled[1]);
        Assert.True(rolled[2] < clipped[2]);
        Assert.True(rolled[3] < 255);
        Assert.True(rolled[2] < rolled[3] && rolled[3] < rolled[4] && rolled[4] < rolled[5] && rolled[5] < rolled[6]);
        Assert.Equal(255, rolled[6]);
        Assert.Equal(255, rolled[7]);
        Assert.Equal(6, scaled);
    }

    [Fact]
    public void ASoftKneeWithoutHighlightRangeIsTheDefaultOperator()
    {
        // A display whose peak is its SDR white has nothing above white to roll off.
        HdrToneMap map = HdrToneMap.Create(400, HdrToneOperator.SoftKnee, peakNits: 400);
        Assert.Equal(HdrToneOperator.MaxChannel, map.Operator);
        Assert.Equal((byte)255, ConvertRow(map, Surface((5.0, 5.0, 5.0)), out _)[0].Red);
        Assert.Equal(HdrToneMap.EncodeSrgb(0.9), ConvertRow(map, Surface((4.5, 4.5, 4.5)), out _)[0].Red);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(0.0)]
    [InlineData(-80.0)]
    [InlineData(double.PositiveInfinity)]
    public void AnUnusableWhiteLevelIsRejected(double whiteNits) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => HdrToneMap.Create(whiteNits));

    [Fact]
    public void AnUnusablePeakFallsBackToTheDefaultPeak()
    {
        Assert.Equal(HdrToneMap.DefaultPeakNits, HdrToneMap.Create(200, HdrToneOperator.SoftKnee, double.NaN).PeakNits);
        Assert.Equal(HdrToneMap.DefaultPeakNits, HdrToneMap.Create(200, HdrToneOperator.SoftKnee, 0).PeakNits);
    }

    /// <summary>A surface whose every pixel is a different SDR colour, so a wrong mapping cannot hide.</summary>
    private static byte[] CoordinateSurface(int width, int height, int stride, out (byte Red, byte Green)[,] colours)
    {
        var surface = new byte[stride * height];
        colours = new (byte, byte)[width, height];
        for (int y = 0; y < height; y++)
        {
            Span<ushort> row = MemoryMarshal.Cast<byte, ushort>(surface.AsSpan(y * stride, width * 8));
            for (int x = 0; x < width; x++)
            {
                colours[x, y] = ((byte)(10 + (x * 9)), (byte)(20 + (y * 13)));
                row[x * 4] = Half(HdrToneMap.DecodeSrgb(colours[x, y].Red));
                row[(x * 4) + 1] = Half(HdrToneMap.DecodeSrgb(colours[x, y].Green));
                row[(x * 4) + 2] = Half(HdrToneMap.DecodeSrgb(77));
            }
        }

        return surface;
    }

    [Theory]
    [InlineData(HdrSurfaceRotation.Identity)]
    [InlineData(HdrSurfaceRotation.Rotate90)]
    [InlineData(HdrSurfaceRotation.Rotate180)]
    [InlineData(HdrSurfaceRotation.Rotate270)]
    public void ARotatedSurfaceIsReadIntoDesktopOrientation(HdrSurfaceRotation rotation)
    {
        const int surfaceWidth = 7, surfaceHeight = 4, stride = (surfaceWidth * 8) + 16;
        byte[] surface = CoordinateSurface(surfaceWidth, surfaceHeight, stride, out (byte Red, byte Green)[,] colours);
        (int width, int height) = HdrSurfaceGeometry.DesktopSize(surfaceWidth, surfaceHeight, rotation);
        int destinationStride = (width * 4) + 8;
        var bgra = new byte[destinationStride * height];

        long scaled = HdrToneMap.Create(80).ConvertRows(
            surface, stride, surfaceWidth, surfaceHeight, rotation, bgra, destinationStride, 0, height);

        Assert.Equal(0, scaled);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            // DXGI rotates the desktop clockwise onto the surface; this is the inverse.
            (int sourceX, int sourceY) = rotation switch
            {
                HdrSurfaceRotation.Rotate90 => (y, surfaceHeight - 1 - x),
                HdrSurfaceRotation.Rotate180 => (surfaceWidth - 1 - x, surfaceHeight - 1 - y),
                HdrSurfaceRotation.Rotate270 => (surfaceWidth - 1 - y, x),
                _ => (x, y),
            };
            int offset = (y * destinationStride) + (x * 4);
            Assert.Equal(colours[sourceX, sourceY].Red, bgra[offset + 2]);
            Assert.Equal(colours[sourceX, sourceY].Green, bgra[offset + 1]);
            Assert.Equal((byte)77, bgra[offset]);
            Assert.Equal((byte)255, bgra[offset + 3]);
        }

        // Bytes between rows are not the conversion's to write.
        for (int y = 0; y < height; y++)
            Assert.All(bgra.AsSpan((y * destinationStride) + (width * 4), 8).ToArray(), value => Assert.Equal(0, value));
    }

    [Theory]
    [InlineData(HdrSurfaceRotation.Identity)]
    [InlineData(HdrSurfaceRotation.Rotate90)]
    [InlineData(HdrSurfaceRotation.Rotate180)]
    [InlineData(HdrSurfaceRotation.Rotate270)]
    public void ReadingOnlyTheBoxOfARectangleGivesThatRectangle(HdrSurfaceRotation rotation)
    {
        const int surfaceWidth = 9, surfaceHeight = 6, stride = surfaceWidth * 8;
        byte[] surface = CoordinateSurface(surfaceWidth, surfaceHeight, stride, out _);
        (int desktopWidth, int desktopHeight) = HdrSurfaceGeometry.DesktopSize(surfaceWidth, surfaceHeight, rotation);
        HdrToneMap map = HdrToneMap.Create(80);
        var whole = new byte[desktopWidth * desktopHeight * 4];
        map.ConvertRows(surface, stride, surfaceWidth, surfaceHeight, rotation, whole, desktopWidth * 4, 0, desktopHeight);

        const int x = 1, y = 2, width = 3, height = 2;
        HdrSurfaceBox box = HdrSurfaceGeometry.ToSurface(x, y, width, height, desktopWidth, desktopHeight, rotation);
        // What a region copy of the box hands the conversion: its rows, tightly packed.
        var part = new byte[box.Width * box.Height * 8];
        for (int row = 0; row < box.Height; row++)
            surface.AsSpan(((box.Top + row) * stride) + (box.Left * 8), box.Width * 8).CopyTo(part.AsSpan(row * box.Width * 8));
        var cropped = new byte[width * height * 4];
        map.ConvertRows(part, box.Width * 8, box.Width, box.Height, rotation, cropped, width * 4, 0, height);

        Assert.Equal((width, height), HdrSurfaceGeometry.DesktopSize(box.Width, box.Height, rotation));
        for (int row = 0; row < height; row++)
            Assert.Equal(whole.AsSpan((((y + row) * desktopWidth) + x) * 4, width * 4).ToArray(), cropped.AsSpan(row * width * 4, width * 4).ToArray());
    }

    [Theory]
    [InlineData(HdrSurfaceRotation.Identity, 10, 20, 30, 40, 10, 20, 40, 60)]
    [InlineData(HdrSurfaceRotation.Rotate90, 10, 20, 30, 40, 20, 60, 60, 90)]
    [InlineData(HdrSurfaceRotation.Rotate180, 10, 20, 30, 40, 60, 140, 90, 180)]
    [InlineData(HdrSurfaceRotation.Rotate270, 10, 20, 30, 40, 140, 10, 180, 40)]
    public void ADesktopRectangleMapsToItsSurfaceBox(
        HdrSurfaceRotation rotation, int x, int y, int width, int height, int left, int top, int right, int bottom)
    {
        // A 100 x 200 desktop: an upright surface for identity and 180, a 200 x 100 one for the quarter turns.
        HdrSurfaceBox box = HdrSurfaceGeometry.ToSurface(x, y, width, height, 100, 200, rotation);

        Assert.Equal(new HdrSurfaceBox(left, top, right, bottom), box);
        Assert.Equal(HdrSurfaceGeometry.DesktopSize(box.Width, box.Height, rotation), (width, height));
    }

    [Fact]
    public void ARectangleOutsideTheDesktopIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => HdrSurfaceGeometry.ToSurface(90, 0, 11, 1, 100, 200, HdrSurfaceRotation.Identity));
        Assert.Throws<ArgumentOutOfRangeException>(() => HdrSurfaceGeometry.ToSurface(0, 199, 1, 2, 100, 200, HdrSurfaceRotation.Rotate90));
        Assert.Throws<ArgumentOutOfRangeException>(() => HdrSurfaceGeometry.ToSurface(-1, 0, 1, 1, 100, 200, HdrSurfaceRotation.Identity));
        Assert.Throws<ArgumentOutOfRangeException>(() => HdrSurfaceGeometry.ToSurface(0, 0, 0, 1, 100, 200, HdrSurfaceRotation.Identity));
        Assert.Throws<ArgumentOutOfRangeException>(() => HdrSurfaceGeometry.ToSurface(0, 0, 1, 1, 100, 200, (HdrSurfaceRotation)0));
    }

    [Fact]
    public void RowsConvertedInSeparateCallsEqualOneCall()
    {
        const int width = 64, height = 48;
        byte[] surface = HdrTestPattern.Render(width, height, 200, 1000);
        HdrToneMap map = HdrToneMap.Create(200);
        var whole = new byte[width * height * 4];
        var banded = new byte[whole.Length];

        long scaled = map.ConvertRows(surface, width * 8, width, height, HdrSurfaceRotation.Identity, whole, width * 4, 0, height);
        long first = map.ConvertRows(surface, width * 8, width, height, HdrSurfaceRotation.Identity, banded, width * 4, 0, 17);
        long second = map.ConvertRows(surface, width * 8, width, height, HdrSurfaceRotation.Identity, banded, width * 4, 17, height - 17);

        Assert.Equal(whole, banded);
        Assert.Equal(scaled, first + second);
        Assert.True(scaled > 0);
    }

    [Fact]
    public void BuffersThatCannotHoldTheRowsAreRejected()
    {
        HdrToneMap map = HdrToneMap.Create(80);
        var surface = new byte[4 * 2 * 8];
        var bgra = new byte[4 * 2 * 4];

        Assert.Throws<ArgumentException>(() => map.ConvertRows(surface.AsSpan(0, surface.Length - 1), 32, 4, 2, HdrSurfaceRotation.Identity, bgra, 16, 0, 2));
        Assert.Throws<ArgumentException>(() => map.ConvertRows(surface, 32, 4, 2, HdrSurfaceRotation.Identity, bgra.AsSpan(0, bgra.Length - 1), 16, 0, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => map.ConvertRows(surface, 31, 4, 2, HdrSurfaceRotation.Identity, bgra, 16, 0, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => map.ConvertRows(surface, 32, 4, 2, HdrSurfaceRotation.Identity, bgra, 15, 0, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => map.ConvertRows(surface, 32, 4, 2, HdrSurfaceRotation.Identity, bgra, 16, 1, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => map.ConvertRows(surface, 32, 4, 2, HdrSurfaceRotation.Identity, bgra, 16, -1, 1));
        // A stride larger than the row needs only the last row's pixels, not its padding.
        var padded = new byte[40 + 32];
        Assert.Equal(0, map.ConvertRows(padded, 40, 4, 2, HdrSurfaceRotation.Identity, bgra, 16, 0, 2));
    }

    [Fact]
    public void TheTestPatternShowsWhatEachOperatorDoesToHighlights()
    {
        const int width = 512, height = 240;
        const double white = 200, peak = 1000;
        byte[] surface = HdrTestPattern.Render(width, height, white, peak);
        var clipped = new byte[width * height * 4];
        var rolled = new byte[clipped.Length];
        HdrToneMap.Create(white).ConvertRows(surface, width * 8, width, height, HdrSurfaceRotation.Identity, clipped, width * 4, 0, height);
        HdrToneMap.Create(white, HdrToneOperator.SoftKnee, peak)
            .ConvertRows(surface, width * 8, width, height, HdrSurfaceRotation.Identity, rolled, width * 4, 0, height);

        Assert.Equal(1.0, HdrTestPattern.RampLevel(width / 2, width, peak / white), 1);
        Assert.Equal(peak / white, HdrTestPattern.RampLevel(width - 1, width, peak / white), 6);
        int greyRow = HdrTestPattern.RampHeight(height) / 2;
        int Red(byte[] picture, int x, int y) => picture[(((y * width) + x) * 4) + 2];
        // The SDR half of the grey ramp is the even ramp 0..255 under the default operator.
        Assert.Equal(0, Red(clipped, 0, greyRow));
        Assert.Equal(128, Red(clipped, width / 4, greyRow), 2.0);
        // The highlight half is flat white under the default operator and still graded under the soft knee.
        Assert.All(Enumerable.Range((width / 2) + 2, (width / 2) - 2), x => Assert.Equal(255, Red(clipped, x, greyRow)));
        int[] rolledHighlights = [.. Enumerable.Range(width / 2, width / 2).Select(x => Red(rolled, x, greyRow))];
        Assert.True(rolledHighlights.Distinct().Count() > 10);
        Assert.Equal(rolledHighlights.Order(), rolledHighlights);
        Assert.Equal(255, rolledHighlights[^1]);
        // Dark and middle SDR tones are the same in both pictures.
        for (int x = 0; x < width / 4; x++)
            Assert.Equal(Red(clipped, x, greyRow), Red(rolled, x, greyRow));
        // Each ramp is monotonic in its brightest channel under both operators.
        foreach (byte[] picture in new[] { clipped, rolled })
        for (int ramp = 0; ramp < HdrTestPattern.RampColours.Count; ramp++)
        {
            int y = (ramp * HdrTestPattern.RampHeight(height)) + 2;
            int previous = 0;
            for (int x = 0; x < width; x++)
            {
                int offset = ((y * width) + x) * 4;
                int brightest = Math.Max(picture[offset], Math.Max(picture[offset + 1], picture[offset + 2]));
                Assert.True(brightest >= previous, $"ramp {ramp} got darker at column {x}");
                previous = brightest;
            }
        }
    }

    [Theory]
    [InlineData(null, HdrCaptureMode.Auto)]
    [InlineData("", HdrCaptureMode.Auto)]
    [InlineData("auto", HdrCaptureMode.Auto)]
    [InlineData("on", HdrCaptureMode.Auto)]
    [InlineData("offf", HdrCaptureMode.Auto)]
    [InlineData("off", HdrCaptureMode.Off)]
    [InlineData(" OFF ", HdrCaptureMode.Off)]
    [InlineData("force", HdrCaptureMode.ForceFloatRead)]
    [InlineData("Force\t", HdrCaptureMode.ForceFloatRead)]
    public void OnlyOffAndForceChangeTheCaptureMode(string? value, HdrCaptureMode expected) =>
        Assert.Equal(expected, HdrCaptureModeSelection.Parse(value));

    [Fact]
    public void TheSwitchNameIsStable() =>
        Assert.Equal("MYCAPTURE_HDR_CAPTURE", HdrCaptureModeSelection.EnvironmentVariable);
}
