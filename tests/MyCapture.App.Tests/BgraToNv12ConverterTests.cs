using MyCapture.App.Diagnostics;
using MyCapture.App.Recording;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class BgraToNv12ConverterTests
{
    public static IEnumerable<object[]> ColourCases =>
        from bt709 in new[] { false, true }
        from patch in VideoRangeColourPattern.Patches
        select new object[] { bt709, patch.Name, patch.R, patch.G, patch.B };

    [Theory]
    [MemberData(nameof(ColourCases))]
    public void PatternColours_MatchIndependentStudioFormula(bool bt709, string name, byte r, byte g, byte b)
    {
        byte[] bgra = [b, g, r, 255, b, g, r, 255, b, g, r, 255, b, g, r, 255];
        byte[] nv12 = new byte[6];
        BgraToNv12Converter.Convert(bgra, 8, 2, 2, new VideoYuvEncoding(bt709, false), nv12);
        (byte y, byte cb, byte cr) = Reference(r, g, b, bt709);
        Assert.True(nv12.SequenceEqual(new byte[] { y, y, y, y, cb, cr }),
            $"{name}, BT.{(bt709 ? 709 : 601)}: {string.Join(',', nv12)} != {y},{y},{y},{y},{cb},{cr}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void All256Greys_HaveReferenceLumaAndExactlyNeutralChroma(bool bt709)
    {
        byte[] bgra = new byte[16];
        byte[] nv12 = new byte[6];
        for (int grey = 0; grey <= 255; grey++)
        {
            for (int p = 0; p < bgra.Length; p += 4)
            {
                bgra[p] = bgra[p + 1] = bgra[p + 2] = (byte)grey;
                bgra[p + 3] = 255;
            }
            BgraToNv12Converter.Convert(bgra, 8, 2, 2, new VideoYuvEncoding(bt709, false), nv12);
            byte y = Round(16 + (219.0 * grey / 255));
            Assert.Equal(new byte[] { y, y, y, y, 128, 128 }, nv12);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MixedBlock_AveragesUnroundedRgb_AndRespectsTopDownPaddedStride(bool bt709)
    {
        // Four distinct pixels with a fractional RGB mean; padding is deliberately nonzero.
        // Premultiplied channels are already composited over black: alpha is not divided out.
        byte[] bgra = [209, 77, 23, 255, 62, 11, 244, 255, 251, 251, 251, 251,
            41, 159, 15, 255, 137, 93, 200, 200];
        byte[] nv12 = new byte[6];
        BgraToNv12Converter.Convert(bgra, 12, 2, 2, new VideoYuvEncoding(bt709, false), nv12);
        Assert.Equal(Reference(23, 77, 209, bt709).Y, nv12[0]);
        Assert.Equal(Reference(244, 11, 62, bt709).Y, nv12[1]);
        Assert.Equal(Reference(15, 159, 41, bt709).Y, nv12[2]);
        Assert.Equal(Reference(200, 93, 137, bt709).Y, nv12[3]);
        var mean = Reference((23 + 244 + 15 + 200) / 4.0,
            (77 + 11 + 159 + 93) / 4.0, (209 + 62 + 41 + 137) / 4.0, bt709);
        Assert.Equal(mean.Cb, nv12[4]);
        Assert.Equal(mean.Cr, nv12[5]);
    }

    [Theory]
    [InlineData(3, 2)]
    [InlineData(2, 3)]
    public void OddDimensions_AreRejected(int width, int height) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => BgraToNv12Converter.Convert(
            new byte[width * height * 4], width * 4, width, height,
            new VideoYuvEncoding(false, false), new byte[width * height * 2]));

    [Theory]
    [InlineData(704, false)]
    [InlineData(706, true)]
    [InlineData(720, true)]
    public void ExportEncoding_UsesCodedHeightAndStudioRange(int height, bool bt709) =>
        Assert.Equal(new VideoYuvEncoding(bt709, false), VideoFrameRenderPipeline.DefaultYuvEncoding(height));

    // Independent double-precision equations, with no product coefficients or converter calls.
    internal static (byte Y, byte Cb, byte Cr) Reference(double r, double g, double b, bool bt709)
    {
        (double kr, double kb) = bt709 ? (0.2126, 0.0722) : (0.299, 0.114);
        double luma = (kr * r) + ((1 - kr - kb) * g) + (kb * b);
        return (Round(16 + (219.0 * luma / 255)),
            Round(128 + (224.0 * (b - luma) / (255 * 2 * (1 - kb)))),
            Round(128 + (224.0 * (r - luma) / (255 * 2 * (1 - kr)))));
    }

    private static byte Round(double value) => (byte)Math.Clamp((int)Math.Floor(value + 0.5), 0, 255);
}
