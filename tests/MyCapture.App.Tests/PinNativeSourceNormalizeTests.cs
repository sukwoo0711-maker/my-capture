using System.Windows.Media;
using MyCapture.App.Pinning;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// The source's one pass over its pixels works sixteen bytes at a time where it can. Every row
/// length from one pixel up, including the ends a sixteen-byte step leaves over, must come out
/// as the plain per-pixel rule says.
/// </summary>
public sealed class PinNativeSourceNormalizeTests
{
    public static TheoryData<int> PixelCounts()
    {
        var data = new TheoryData<int>();
        foreach (int count in new[] { 1, 2, 3, 4, 5, 7, 8, 9, 15, 16, 17, 31, 33, 64, 257 }) data.Add(count);
        return data;
    }

    [Theory]
    [MemberData(nameof(PixelCounts))]
    public void PremultipliedPixelsAreClampedToTheirAlphaAndValidOnesAreUntouched(int pixels)
    {
        byte[] row = Pattern(pixels, seed: 11);
        byte[] expected = (byte[])row.Clone();
        for (int x = 0; x < expected.Length; x += 4)
            for (int channel = 0; channel < 3; channel++)
                expected[x + channel] = Math.Min(expected[x + channel], expected[x + 3]);
        NativePinPixelSource.Normalize(row, PixelFormats.Pbgra32);
        Assert.Equal(expected, row);
        // Already valid premultiplied pixels are a fixed point.
        NativePinPixelSource.Normalize(row, PixelFormats.Pbgra32);
        Assert.Equal(expected, row);
    }

    [Theory]
    [MemberData(nameof(PixelCounts))]
    public void PixelsWithoutAlphaBecomeOpaqueWithTheirColourUnchanged(int pixels)
    {
        byte[] row = Pattern(pixels, seed: 29);
        byte[] expected = (byte[])row.Clone();
        for (int x = 0; x < expected.Length; x += 4) expected[x + 3] = 255;
        NativePinPixelSource.Normalize(row, PixelFormats.Bgr32);
        Assert.Equal(expected, row);
    }

    [Theory]
    [MemberData(nameof(PixelCounts))]
    public void StraightAlphaPixelsArePremultipliedWithRounding(int pixels)
    {
        byte[] row = Pattern(pixels, seed: 53);
        byte[] expected = (byte[])row.Clone();
        for (int x = 0; x < expected.Length; x += 4)
            for (int channel = 0; channel < 3; channel++)
                expected[x + channel] = (byte)Math.Round(expected[x + channel] * expected[x + 3] / 255d, MidpointRounding.AwayFromZero);
        NativePinPixelSource.Normalize(row, PixelFormats.Bgra32);
        Assert.Equal(expected, row);
    }

    // Every byte value and every alpha, in an order that puts colours both above and below alpha.
    private static byte[] Pattern(int pixels, int seed)
    {
        byte[] row = new byte[pixels * 4];
        for (int index = 0; index < row.Length; index++)
            row[index] = (byte)((index * 37 + (index / 4) * seed + seed * 3) & 255);
        if (pixels > 2)
        {
            row[3] = 0;       // a fully transparent pixel with colour left in it
            row[7] = 255;     // a fully opaque one
        }
        return row;
    }
}
