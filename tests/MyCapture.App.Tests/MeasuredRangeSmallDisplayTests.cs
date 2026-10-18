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
    [InlineData(32)] [InlineData(64)] [InlineData(128)] [InlineData(200)]
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
        foreach (double dpi in new[] { 96.0, 120.0, 144.0 })
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

    [Theory]
    [InlineData(32)] [InlineData(64)] [InlineData(128)] [InlineData(200)]
    [InlineData(256)] [InlineData(400)] [InlineData(1280)]
    public void NonIntegralScaleAndAlphaKeepTheExactLookupEntry(int width) => StaTestHost.Run(() =>
    {
        foreach (double dpi in new[] { 96.0, 120.0, 144.0 })
        foreach (double scale in new[] { 0.87, 1.13 })
        foreach (byte alpha in new byte[] { 64, 128, 192 })
        {
            byte[][] curves = Enumerable.Range(0, 3).Select(c => Curve("steep", c)).ToArray();
            var mapping = new MeasuredRangeCurve(Convert.ToBase64String(curves[0]),
                Convert.ToBase64String(curves[1]), Convert.ToBase64String(curves[2]));
            // A solid input isolates LUT addressing from input image resampling at arbitrary
            // scale. Exercise all premultiplied byte values, including both texture ends.
            var effect = new MeasuredRangeVideoEffect(mapping);
            int maximum = 0;
            string worst = "";
            var element = new Image { Width = width * 96 / dpi / scale, Height = 24 * 96 / dpi / scale,
                Stretch = Stretch.Fill, RenderTransform = new ScaleTransform(scale, scale), Effect = effect };
            element.Measure(new Size(element.Width, element.Height));
            element.Arrange(new Rect(0, 0, element.Width, element.Height));
            for (int level = 0; level <= alpha; level++)
            {
                byte[] pixel = [(byte)level, (byte)(alpha - level), (byte)level, alpha];
                element.Source = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Pbgra32, null, pixel, 4);
                element.Measure(new Size(element.Width, element.Height));
                element.Arrange(new Rect(0, 0, element.Width, element.Height));
                byte[] actual = Render(element, width, 24, dpi);
                int i = (12 * width + width / 2) * 4;
                Assert.Equal(alpha, actual[i + 3]);
                for (int c = 0; c < 3; c++)
                {
                    int entry = (int)Math.Floor(pixel[c] * 255.0 / alpha + 0.5);
                    int expected = (int)Math.Round(curves[c][entry] * alpha / 255.0);
                    int error = Math.Abs(actual[i + c] - expected);
                    if (error > maximum)
                    {
                        maximum = error;
                        worst = $"level={level}, channel={c}, entry={entry}, actual={actual[i + c]}, expected={expected}";
                    }
                }
            }
            string measurement = $"width={width}, dpi={dpi}, scale={scale}, alpha={alpha}, max={maximum}; {worst}";
            output.WriteLine(measurement);
            Assert.True(maximum <= 1, measurement);
        }
    });

    [Fact]
    public void CacheContainsFourIdenticalColumnsAnd256IdenticalRows() => StaTestHost.Run(() =>
    {
        byte[][] curves = Enumerable.Range(0, 3).Select(c => Curve("steep", c)).ToArray();
        var mapping = new MeasuredRangeCurve(Convert.ToBase64String(curves[0]),
            Convert.ToBase64String(curves[1]), Convert.ToBase64String(curves[2]));
        byte[] texture = mapping.Texture();
        Assert.Equal(1024 * 256 * 4, texture.Length);
        for (int y = 0; y < 256; y++)
        for (int x = 0; x < 1024; x++)
        {
            int i = (y * 1024 + x) * 4;
            Assert.Equal(255, texture[i + 3]);
            for (int c = 0; c < 3; c++) Assert.Equal(curves[c][x / 4], texture[i + c]);
        }
        var brush = Assert.IsType<BitmapCacheBrush>(new MeasuredRangeVideoEffect(mapping).Lookup);
        Assert.Equal(1, brush.BitmapCache.RenderAtScale);
        Assert.Equal(new Rect(0, 0, 1024, 256), VisualTreeHelper.GetContentBounds(brush.Target));
        // At each inner texel centre the two bilinear neighbours are both this entry.
        for (int entry = 0; entry < 256; entry++)
        {
            double u = (entry * 4 + 1.5) / 1024;
            int nearest = (int)Math.Floor(u * 1024);
            int bilinearLeft = (int)Math.Floor(u * 1024 - 0.5);
            Assert.Equal(entry, nearest / 4);
            Assert.Equal(entry, bilinearLeft / 4);
            Assert.Equal(entry, (bilinearLeft + 1) / 4);
        }
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
