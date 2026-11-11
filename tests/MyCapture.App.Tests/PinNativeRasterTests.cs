using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Pinning;
using MyCapture.App.Themes;
using MyCapture.Core.Pin;
using MyCapture.Core.Platform;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

public sealed class PinNativeRasterTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(.5, false)]
    [InlineData(1, false)]
    [InlineData(1.5, false)]
    [InlineData(3, false)]
    [InlineData(.5, true)]
    [InlineData(1, true)]
    [InlineData(1.5, true)]
    [InlineData(3, true)]
    public void RasterPreservesPremultiplicationDimensionsAndGuardBytes(double zoom, bool quality)
    {
        BitmapSource source = PinNativeTestImages.Picture(37, 23, transparent: true);
        int width = (int)Math.Ceiling(source.PixelWidth * zoom), height = (int)Math.Ceiling(source.PixelHeight * zoom);
        int stride = checked(width * 4 + 12), length = checked(stride * height);
        const int guard = 32;
        byte[] initial = Enumerable.Repeat((byte)0xCD, length + guard * 2).ToArray();
        IntPtr allocation = Marshal.AllocHGlobal(initial.Length);
        try
        {
            Marshal.Copy(initial, 0, allocation, initial.Length);
            NativePinRaster.Render(source, IntPtr.Add(allocation, guard), width, height, stride,
                1, PinNativeTestImages.Outline(AppTheme.Glass), 8, quality);
            var actual = new byte[initial.Length];
            Marshal.Copy(allocation, actual, 0, actual.Length);
            Assert.All(actual.Take(guard), value => Assert.Equal(0xCD, value));
            Assert.All(actual.Skip(guard + length), value => Assert.Equal(0xCD, value));
            for (int y = 0; y < height; y++)
            {
                int row = guard + y * stride;
                Assert.All(actual.Skip(row + width * 4).Take(12), value => Assert.Equal(0xCD, value));
                for (int x = 0; x < width; x++)
                {
                    int pixel = row + x * 4;
                    for (int channel = 0; channel < 3; channel++)
                        Assert.InRange(actual[pixel + channel], 0, actual[pixel + 3]);
                }
            }
        }
        finally { Marshal.FreeHGlobal(allocation); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnityScaleKeepsUntouchedSourcePixelsExactly(bool transparent)
    {
        BitmapSource source = PinNativeTestImages.Picture(64, 48, transparent);
        byte[] pixels = PinNativeTestImages.Native(source, 1, 1, AppTheme.Glass, quality: true).Pixels;
        byte[] expected = PinNativeTestImages.Pixels(source);
        for (int y = 10; y < 38; y++)
            for (int x = 10; x < 54; x++)
                for (int channel = 0; channel < 4; channel++)
                    Assert.Equal(expected[(y * 64 + x) * 4 + channel], pixels[(y * 64 + x) * 4 + channel]);
    }

    [Theory]
    [InlineData(AppTheme.Glass, false, 1)]
    [InlineData(AppTheme.Glass, true, 1)]
    [InlineData(AppTheme.GlassLight, false, 1)]
    [InlineData(AppTheme.GlassLight, true, 1)]
    [InlineData(AppTheme.Glass, false, 1.5)]
    [InlineData(AppTheme.Glass, true, 1.5)]
    [InlineData(AppTheme.GlassLight, false, 1.5)]
    [InlineData(AppTheme.GlassLight, true, 1.5)]
    public void CornersAndOutlineAgreeWithTheRealWpfPinOnGeometryAndInterior(AppTheme theme, bool emphasized, double dpiScale) => StaTestHost.Run(() =>
    {
        BitmapSource source = PinNativeTestImages.Solid(80, 60, Color.FromRgb(54, 108, 162));
        var native = PinNativeTestImages.Native(source, 1, dpiScale, theme, emphasized: emphasized);
        BitmapSource reference = PinNativeTestImages.Reference(source, 1, dpiScale, theme, emphasized);
        byte[] expected = PinNativeTestImages.Pixels(reference);
        int width = reference.PixelWidth, height = reference.PixelHeight;
        Assert.Equal(width, native.Width);
        Assert.Equal(height, native.Height);
        int corner = (int)Math.Ceiling(12 * dpiScale);
        int edge = (int)Math.Ceiling(dpiScale);
        void AssertExactPixel(int x, int y)
        {
            int pixel = (y * width + x) * 4;
            for (int channel = 0; channel < 4; channel++)
                Assert.True(expected[pixel + channel] == native.Pixels[pixel + channel],
                    $"Exact pixel ({x},{y}) channel BGRA[{channel}]: WPF={expected[pixel + channel]}, native={native.Pixels[pixel + channel]}, DPI={dpiScale * 96}.");
        }
        // An opaque solid source removes resampling differences from this geometry comparison.
        for (int y = corner; y < height - corner; y++)
            for (int x = corner; x < width - corner; x++)
                AssertExactPixel(x, y);
        // Every straight-edge pixel, including the second stroke row at 144 DPI, is exact.
        for (int depth = 0; depth < edge; depth++)
        {
            for (int x = corner; x < width - corner; x++)
            {
                AssertExactPixel(x, depth);
                AssertExactPixel(x, height - 1 - depth);
            }
            for (int y = corner; y < height - corner; y++)
            {
                AssertExactPixel(depth, y);
                AssertExactPixel(width - 1 - depth, y);
            }
        }
        if (dpiScale == 1)
        {
            // The actual WPF reference has half-pixel image coverage below the one-DIP outline.
            Assert.Equal(emphasized ? 255 : 204, expected[(width / 2) * 4 + 3]);
            Assert.Equal(emphasized ? 255 : 204, native.Pixels[(width / 2) * 4 + 3]);
        }

        // Compare each of the four small corner regions. One code value is the maximum
        // allowed difference from the independent WPF renderer's integer premultiplication.
        int[] maxima = new int[4];
        long[] sums = new long[4];
        string[] locations = new string[4];
        foreach ((int left, int top) in new[] { (0, 0), (width - corner, 0), (0, height - corner), (width - corner, height - corner) })
            for (int y = top; y < top + corner; y++)
                for (int x = left; x < left + corner; x++)
                    for (int channel = 0; channel < 4; channel++)
                    {
                        int pixel = (y * width + x) * 4 + channel;
                        int delta = Math.Abs(expected[pixel] - native.Pixels[pixel]);
                        sums[channel] += delta;
                        if (delta > maxima[channel])
                        {
                            maxima[channel] = delta;
                            locations[channel] = $"({x},{y}): WPF={expected[pixel]}, native={native.Pixels[pixel]}";
                        }
                    }
        string means = string.Join(",", sums.Select(sum => (sum / (4d * corner * corner)).ToString("F6", CultureInfo.InvariantCulture)));
        output.WriteLine($"Four {corner}x{corner} corners, DPI={dpiScale * 96}, theme={theme}, emphasized={emphasized}; BGRA max=[{string.Join(",", maxima)}], mean=[{means}].");
        for (int channel = 0; channel < 4; channel++)
            Assert.True(maxima[channel] <= 1,
                $"Corner BGRA[{channel}] max difference {maxima[channel]} exceeds one code value at {locations[channel]}; DPI={dpiScale * 96}.");

        if (WindowsFeatureSupport.WindowCornerRadiusDip(Environment.OSVersion.Version.Build) > 0)
        {
            Assert.Equal(0, expected[3]);
            Assert.Equal(0, native.Pixels[3]);
            Assert.Contains(Enumerable.Range(0, corner).SelectMany(y => Enumerable.Range(0, corner)
                .Select(x => expected[(y * width + x) * 4 + 3])), alpha => alpha is > 0 and < 255);
            Assert.Contains(Enumerable.Range(0, corner).SelectMany(y => Enumerable.Range(0, corner)
                .Select(x => native.Pixels[(y * width + x) * 4 + 3])), alpha => alpha is > 0 and < 255);
        }
    });

    [Fact]
    public void TransparentImageDoesNotAcquireOpaqueBlackInterior()
    {
        BitmapSource transparent = PinNativeTestImages.Solid(64, 48, Colors.Transparent);
        byte[] pixels = PinNativeTestImages.Native(transparent, 1.5, 1, AppTheme.Glass).Pixels;
        int width = 96;
        for (int y = 12; y < 60; y++)
            for (int x = 12; x < 84; x++)
                Assert.Equal(new byte[4], pixels.Skip((y * width + x) * 4).Take(4));
    }

    [Fact]
    public void InvalidStrideFailsBeforeWritingDestination()
    {
        BitmapSource source = PinNativeTestImages.Picture(8, 8);
        IntPtr allocation = Marshal.AllocHGlobal(256);
        try
        {
            byte[] canary = Enumerable.Repeat((byte)0xCD, 256).ToArray();
            Marshal.Copy(canary, 0, allocation, canary.Length);
            Assert.ThrowsAny<ArgumentException>(() => NativePinRaster.Render(source, allocation,
                8, 8, 31, 1, Colors.Red, 8, true));
            byte[] after = new byte[256];
            Marshal.Copy(allocation, after, 0, after.Length);
            Assert.Equal(canary, after);
        }
        finally { Marshal.FreeHGlobal(allocation); }
    }
}

internal static class PinNativeTestImages
{
    internal sealed record Raster(byte[] Pixels, int Width, int Height, double Milliseconds)
    {
        internal BitmapSource Bitmap(double dpiScale = 1)
        {
            BitmapSource result = BitmapSource.Create(Width, Height, 96 * dpiScale, 96 * dpiScale,
                PixelFormats.Pbgra32, null, Pixels, Width * 4);
            result.Freeze();
            return result;
        }
    }

    internal static Color Outline(AppTheme theme, bool emphasized = false)
    {
        ThemeColor color = ThemeCatalog.ColorsFor(theme, AppAccent.Red)[PinFrameColor.AccentKey];
        return Color.FromArgb(emphasized ? color.A : (byte)Math.Round(color.A * PinFrameColor.RestOpacity), color.R, color.G, color.B);
    }

    internal static Raster Native(BitmapSource source, double zoom, double dpiScale, AppTheme theme,
        bool quality = true, bool emphasized = false, double contentScale = 1)
    {
        int width = (int)Math.Ceiling(source.Width * zoom * dpiScale);
        int height = (int)Math.Ceiling(source.Height * zoom * dpiScale);
        int stride = checked(width * 4), bytes = checked(stride * height);
        IntPtr pixels = Marshal.AllocHGlobal(bytes);
        try
        {
            double milliseconds = NativePinRaster.Render(source, pixels, width, height, stride, dpiScale,
                Outline(theme, emphasized), WindowsFeatureSupport.WindowCornerRadiusDip(Environment.OSVersion.Version.Build), quality, contentScale);
            byte[] copy = new byte[bytes];
            Marshal.Copy(pixels, copy, 0, bytes);
            return new(copy, width, height, milliseconds);
        }
        finally { Marshal.FreeHGlobal(pixels); }
    }

    internal static BitmapSource Reference(BitmapSource image, double zoom, double dpiScale, AppTheme theme, bool emphasized = false)
    {
        bool? previousMotion = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        var pin = new PinWindow(PinContent.FromImage(image), new PinViewState(image.Width, image.Height, zoom, 1, .1),
            -20000, -20000, () => new PinSettings()) { ShowActivated = false };
        try
        {
            ApplyTheme(pin, theme);
            pin.SetChromeActiveForTest(emphasized);
            Assert.Equal(Outline(theme, emphasized), pin.FrameColorForTest);
            var content = Assert.IsType<Grid>(pin.Content);
            Size size = new(image.Width * zoom, image.Height * zoom);
            content.Measure(size);
            content.Arrange(new Rect(size));
            content.UpdateLayout();
            int width = (int)Math.Ceiling(size.Width * dpiScale), height = (int)Math.Ceiling(size.Height * dpiScale);
            var bitmap = new RenderTargetBitmap(width, height, 96 * dpiScale, 96 * dpiScale, PixelFormats.Pbgra32);
            bitmap.Render(content);
            bitmap.Freeze();
            return bitmap;
        }
        finally
        {
            pin.Close();
            FluidMotion.AnimationsEnabledOverrideForTest = previousMotion;
        }
    }

    internal static void ApplyTheme(PinWindow pin, AppTheme theme)
    {
        var resources = new ResourceDictionary();
        resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/MyCapture;component/Themes/Tokens.xaml"),
        });
        ThemeService.ApplyResources(theme, resources, systemHighContrast: false, accent: AppAccent.Red);
        pin.Resources = resources;
    }

    internal static byte[] Pixels(BitmapSource image)
    {
        if (image.Format != PixelFormats.Pbgra32)
        {
            var converted = new FormatConvertedBitmap(image, PixelFormats.Pbgra32, null, 0);
            converted.Freeze();
            image = converted;
        }
        byte[] result = new byte[checked(image.PixelWidth * image.PixelHeight * 4)];
        image.CopyPixels(result, image.PixelWidth * 4, 0);
        return result;
    }

    internal static BitmapSource Picture(int width, int height, bool transparent = false)
    {
        byte[] bytes = new byte[checked(width * height * 4)];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int offset = (y * width + x) * 4;
                byte alpha = transparent ? (byte)(x < width / 8 ? 0 : ((x * 255L / Math.Max(1, width - 1) + y * 255L / Math.Max(1, height - 1)) / 2)) : (byte)255;
                bool grid = x % 31 < 2 || y % 23 < 2 || Math.Abs(x * height - y * width) < width;
                byte b = grid ? (byte)28 : (byte)(70 + (x * 150L / Math.Max(1, width - 1)));
                byte g = grid ? (byte)44 : (byte)(70 + (y * 150L / Math.Max(1, height - 1)));
                byte r = grid ? (byte)60 : (byte)(230 - (x * 130L / Math.Max(1, width - 1)));
                bytes[offset] = (byte)((b * alpha + 127) / 255);
                bytes[offset + 1] = (byte)((g * alpha + 127) / 255);
                bytes[offset + 2] = (byte)((r * alpha + 127) / 255);
                bytes[offset + 3] = alpha;
            }
        BitmapSource image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, bytes, width * 4);
        image.Freeze();
        return image;
    }

    internal static BitmapSource Solid(int width, int height, Color color)
    {
        byte[] pixels = new byte[checked(width * height * 4)];
        for (int p = 0; p < pixels.Length; p += 4)
        {
            pixels[p] = (byte)((color.B * color.A + 127) / 255);
            pixels[p + 1] = (byte)((color.G * color.A + 127) / 255);
            pixels[p + 2] = (byte)((color.R * color.A + 127) / 255);
            pixels[p + 3] = color.A;
        }
        BitmapSource image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4);
        image.Freeze();
        return image;
    }
}
