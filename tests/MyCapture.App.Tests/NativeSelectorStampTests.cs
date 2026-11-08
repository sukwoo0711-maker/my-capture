using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Capture;
using MyCapture.Core.Primitives;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class NativeSelectorStampTests
{
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(2.0)]
    public void StaticChipsReuseBoundedOpaquePixelsAndClampOnNegativeMonitors(double scale) => StaTestHost.Run(() =>
    {
        var size = new TextBlock { Text = "100 × 200" };
        var guide = new TextBlock { Text = "Drag" };
        var chrome = new NativeSelectorStaticChrome(size, guide);
        var bounds = new RectD(-2000, -800, 2000, 1200);
        var work = new RectD(-1800, -700, 1600, 900);
        var first = chrome.Update(size.Text, guide.Text, new PointD(-201, 199), work, bounds, scale, true);
        Assert.Equal(2, first.Count);
        foreach (var stamp in first)
        {
            stamp.Validate();
            Assert.True(work.Contains(new PointD(stamp.Bounds.X + bounds.Left, stamp.Bounds.Y + bounds.Top)));
            Assert.True(work.Contains(new PointD(stamp.Bounds.X + stamp.Bounds.Width + bounds.Left, stamp.Bounds.Y + stamp.Bounds.Height + bounds.Top)));
            for (int i = 3; i < stamp.Pixels.Length; i += 4) Assert.Equal((byte)255, stamp.Pixels[i]);
        }
        var moved = chrome.Update(size.Text, guide.Text, new PointD(-1600, -500), work, bounds, scale, true);
        Assert.Same(first[0].Pixels, moved[0].Pixels);
        Assert.Same(first[1].Pixels, moved[1].Pixels);
        Assert.NotEqual(first[1].Bounds, moved[1].Bounds);
        Assert.Single(chrome.Update(size.Text, guide.Text, new PointD(-1600, -500), work, bounds, scale, false));
        size.Text = "50 × 50";
        var changed = chrome.Update(size.Text, guide.Text, new PointD(-1600, -500), work, bounds, scale, true);
        Assert.NotSame(first[1].Pixels, changed[1].Pixels);
        Assert.Same(first[0].Pixels, changed[0].Pixels);
    });

    [Fact]
    public void MovingOverlappingAndClippedStampsRestoreTheUnmodifiedImage() => StaTestHost.Run(() =>
    {
        var source = Source();
        using var display = new NativeSelectorDib(source, new SolidColorBrush(Color.FromArgb(128, 0, 0, 0)));
        using var reference = new NativeSelectorDib(source, new SolidColorBrush(Color.FromArgb(128, 0, 0, 0)));
        var first = Stamp(new Int32Rect(-2, -1, 6, 5), [30, 20, 10, 128]);
        var second = Stamp(new Int32Rect(2, 2, 6, 5), [50, 40, 30, 255]);
        IReadOnlyList<NativeSelectorStamp>[] sequences = [[first], [first, second], [second], [], [second, first], []];
        foreach (Int32Rect reveal in new Int32Rect[] { Int32Rect.Empty, new(1, 1, 5, 6), new(0, 0, 8, 8), Int32Rect.Empty })
        {
            display.SetReveal(reveal);
            reference.SetReveal(reveal);
            foreach (var stamps in sequences)
            {
                display.SetStamps(stamps);
                byte[] expected = Read(reference);
                foreach (var stamp in stamps)
                for (int y = Math.Max(0, stamp.Bounds.Y); y < Math.Min(8, stamp.Bounds.Y + stamp.Bounds.Height); y++)
                for (int x = Math.Max(0, stamp.Bounds.X); x < Math.Min(8, stamp.Bounds.X + stamp.Bounds.Width); x++)
                {
                    int offset = (y * 8 + x) * 4;
                    for (int c = 0; c < 3; c++)
                        expected[offset + c] = (byte)(stamp.Pixels[c] + (expected[offset + c] * (255 - stamp.Pixels[3]) + 127) / 255);
                }
                Assert.Equal(expected, Read(display));
                long before = display.PixelsUpdated;
                Assert.Empty(display.SetStamps(stamps));
                Assert.Equal(before, display.PixelsUpdated);
            }
        }
        byte[] original = new byte[8 * 8 * 4];
        source.CopyPixels(original, 32, 0);
        Assert.All(original, value => Assert.Equal((byte)100, value));
    });

    [Fact]
    public void RevealMovementRecompositesExistingStampsAndRemovalRestoresReveal() => StaTestHost.Run(() =>
    {
        using var dib = new NativeSelectorDib(Source(), new SolidColorBrush(Color.FromArgb(128, 0, 0, 0)));
        var stamp = Stamp(new Int32Rect(2, 2, 4, 4), [80, 60, 40, 255]);
        dib.SetStamps([stamp]);
        dib.SetReveal(new Int32Rect(0, 0, 8, 8));
        byte[] pixels = Read(dib);
        Assert.Equal((byte)80, pixels[(2 * 8 + 2) * 4]);
        Assert.Equal((byte)100, pixels[0]);
        dib.SetStamps([]);
        pixels = Read(dib);
        for (int i = 0; i < pixels.Length; i += 4)
        {
            Assert.Equal((byte)100, pixels[i]);
            Assert.Equal((byte)100, pixels[i + 1]);
            Assert.Equal((byte)100, pixels[i + 2]);
            Assert.Equal((byte)255, pixels[i + 3]);
        }
    });

    [Fact]
    public void ParallelPixelFillMatchesTheStaFillByteForByte() => StaTestHost.Run(() =>
    {
        var source = Source();
        var dim = new SolidColorBrush(Color.FromArgb(107, 30, 70, 80));
        using var expected = new NativeSelectorDib(source, dim);
        using var actual = new NativeSelectorDib(source, dim, deferPixels: true);
        Task.Run(actual.InitializePixels).GetAwaiter().GetResult();
        Assert.Equal(Read(expected), Read(actual));
        Assert.Equal(expected.PixelsUpdated, actual.PixelsUpdated);
        Assert.Throws<InvalidOperationException>(actual.InitializePixels);
    });

    [Fact]
    public void InvalidStampLeavesTheDisplayUntouched() => StaTestHost.Run(() =>
    {
        using var dib = new NativeSelectorDib(Source(), new SolidColorBrush(Colors.Black));
        byte[] before = Read(dib);
        Assert.Throws<ArgumentException>(() => dib.SetStamps([new(new Int32Rect(0, 0, 2, 2), [1, 2, 3, 4])]));
        Assert.Equal(before, Read(dib));
    });

    private static NativeSelectorStamp Stamp(Int32Rect bounds, byte[] pixel) =>
        new(bounds, Enumerable.Range(0, bounds.Width * bounds.Height).SelectMany(_ => pixel).ToArray());

    private static BitmapSource Source()
    {
        var source = BitmapSource.Create(8, 8, 96, 96, PixelFormats.Bgr32, null, Enumerable.Repeat((byte)100, 8 * 8 * 4).ToArray(), 32);
        source.Freeze();
        return source;
    }

    private static byte[] Read(NativeSelectorDib dib)
    {
        byte[] pixels = new byte[dib.ByteLength];
        Marshal.Copy(dib.Bits, pixels, 0, pixels.Length);
        return pixels;
    }
}
