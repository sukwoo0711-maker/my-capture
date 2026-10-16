using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Recording;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

public sealed class MeasuredRangeSmallDisplayTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(64)] [InlineData(128)] [InlineData(200)]
    [InlineData(256)] [InlineData(400)] [InlineData(1280)]
    public void LookupRetainsAllTexelsAtEveryPhysicalWidth(int width) => StaTestHost.Run(() =>
    {
        // A 16x16 grey grid retains ALL 256 inputs even at width 64; two more rows
        // contain the actual 16 colour patches. Integer pixel cells avoid resampling
        // the input, so this measures LUT realisation, not image downscaling error.
        const int height = 144;
        byte[] source = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int i = (y * width + x) * 4;
            if (y < 128)
                source[i] = source[i + 1] = source[i + 2] = (byte)(y / 8 * 16 + x * 16 / width);
            else
            {
                var patch = RangePatternClip.Patches[(y - 128) / 8 * 8 + x * 8 / width];
                source[i] = patch.B; source[i + 1] = patch.G; source[i + 2] = patch.R;
            }
            source[i + 3] = 255;
        }
        var failures = new List<string>();
        foreach (double dpi in new[] { 96.0, 120.0 })
        foreach (string name in new[] { "studio-inverse", "steep" })
        {
            byte[][] curves = Enumerable.Range(0, 3).Select(c => Curve(name, c)).ToArray();
            var mapping = new MeasuredRangeCurve(Convert.ToBase64String(curves[0]),
                Convert.ToBase64String(curves[1]), Convert.ToBase64String(curves[2]));
            var bitmap = BitmapSource.Create(width, height, dpi, dpi, PixelFormats.Pbgra32, null, source, width * 4);
            var element = new Image { Width = width * 96 / dpi, Height = height * 96 / dpi,
                Source = bitmap, Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(element, BitmapScalingMode.NearestNeighbor);
            element.Measure(new Size(element.Width, element.Height));
            element.Arrange(new Rect(0, 0, element.Width, element.Height));
            byte[] baseline = Render(element, width, height, dpi);
            Assert.Equal(source, baseline); // Prove exact input and the requested physical surface size.
            element.Effect = new MeasuredRangeVideoEffect(mapping);
            byte[] shown = Render(element, width, height, dpi);
            int greyError = 0, patchError = 0;
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                Assert.Equal(255, shown[i + 3]);
                for (int c = 0; c < 3; c++)
                {
                    int error = Math.Abs(shown[i + c] - curves[c][source[i + c]]);
                    if (y < 128) greyError = Math.Max(greyError, error);
                    else patchError = Math.Max(patchError, error);
                }
            }
            string measurement = $"width={width}px, dpi={dpi}, curve={name}, grey max={greyError}, patch max={patchError}";
            output.WriteLine(measurement);
            if (greyError > 1 || patchError > 1) failures.Add(measurement);
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    });

    private static byte[] Render(Image element, int width, int height, double dpi)
    {
        var target = new RenderTargetBitmap(width, height, dpi, dpi, PixelFormats.Pbgra32);
        target.Render(element);
        byte[] pixels = new byte[width * height * 4];
        target.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    private static byte[] Curve(string name, int channel)
    {
        int[] x = name == "studio-inverse" ? [12, 30, 74, channel == 0 ? 181 : 183, 232, 255]
            : [0, 110 + channel, 142 + channel, 255];
        int[] y = name == "studio-inverse" ? [0, 16, 64, 192, 235, 255] : [0, 24, 232, 255];
        return Enumerable.Range(0, 256).Select(value =>
        {
            int j = 0;
            while (j < x.Length - 2 && value > x[j + 1]) j++;
            return (byte)Math.Clamp(Math.Round(y[j] + (value - x[j]) * (y[j + 1] - y[j]) / (double)(x[j + 1] - x[j])), 0, 255);
        }).ToArray();
    }
}
