using System.Numerics;
using System.Windows.Media;
using MyCapture.App.Capture;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class NativeSelectorPixelsTests
{
    [Fact]
    public void SimdAndScalarMatchEveryChannelValueAtEveryAlpha()
    {
        Color[] colors = [Colors.Black, Colors.White, Color.FromRgb(30, 70, 110), Color.FromRgb(241, 13, 177)];
        foreach (Color color in colors)
        for (int alpha = 0; alpha <= 255; alpha++)
        {
            // Offset and tail exercise unaligned vector loads and the scalar remainder.
            var vector = new uint[259];
            var scalar = new uint[259];
            vector[0] = scalar[0] = vector[^1] = scalar[^1] = 0x12345678;
            for (int i = 0; i < 257; i++)
                vector[i + 1] = scalar[i + 1] = 0x73000000u | (uint)(i & 255)
                    | ((uint)((i * 31) & 255) << 8) | ((uint)((255 - i) & 255) << 16);
            var dimmer = new NativeSelectorPixels(color, alpha);
            dimmer.Dim(vector.AsSpan(1, 257));
            dimmer.Dim(scalar.AsSpan(1, 257), useSimd: false);
            Assert.Equal(0x12345678u, vector[0]);
            Assert.Equal(0x12345678u, vector[^1]);
            for (int i = 0; i < 257; i++)
            {
                uint expected = 0xff000000u | Blend(i & 255, color.B, alpha)
                    | (Blend((i * 31) & 255, color.G, alpha) << 8)
                    | (Blend((255 - i) & 255, color.R, alpha) << 16);
                Assert.Equal(expected, vector[i + 1]);
                Assert.Equal(expected, scalar[i + 1]);
            }
        }
    }

    [Fact]
    public void EmptyShortAndOpaqueSpansPreserveRgbAndTheirNeighbors()
    {
        var dimmer = new NativeSelectorPixels(Colors.Black, 120);
        dimmer.Dim([]);
        NativeSelectorPixels.MakeOpaque([]);
        foreach (int count in new[] { 1, Vector<uint>.Count - 1, Vector<uint>.Count, Vector<uint>.Count + 3 })
        {
            uint[] pixels = Enumerable.Repeat(0x017b3f22u, count + 2).ToArray();
            NativeSelectorPixels.MakeOpaque(pixels.AsSpan(1, count));
            Assert.Equal(0x017b3f22u, pixels[0]);
            Assert.Equal(0x017b3f22u, pixels[^1]);
            Assert.All(pixels.Skip(1).Take(count), value => Assert.Equal(0xff7b3f22u, value));
            dimmer.Dim(pixels.AsSpan(1, count));
            uint expected = 0xff000000u | Blend(0x22, 0, 120) | (Blend(0x3f, 0, 120) << 8) | (Blend(0x7b, 0, 120) << 16);
            Assert.All(pixels.Skip(1).Take(count), value => Assert.Equal(expected, value));
        }
    }

    private static uint Blend(int source, int dim, int alpha) => (uint)((source * (255 - alpha) + dim * alpha + 127) / 255);
}
