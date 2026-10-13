using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Capture;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class CaptureGuideStripTests
{
    private static readonly SolidColorBrush Shadow = Frozen(Color.FromArgb(64, 0, 0, 0));
    private static readonly SolidColorBrush Line = Frozen(Color.FromArgb(120, 0x97, 0x89, 0xFF));

    [Theory]
    [InlineData(7)]
    [InlineData(640)]
    [InlineData(4208)]
    public void HorizontalStrip_IsTheVerticalStrokesTransposed_AndMatchesDirectStrokes(int length) => StaTestHost.Run(() =>
    {
        byte[] horizontal = Pixels(CaptureGuideStrip.Get(Shadow, Line, length, horizontal: true));
        byte[] vertical = Pixels(CaptureGuideStrip.Get(Shadow, Line, length, horizontal: false));
        byte[] direct = Pixels(Strokes(length));
        for (int along = 0; along < length; along++)
        {
            for (int across = 0; across < CaptureGuideStrip.Thickness; across++)
            {
                int h = ((across * length) + along) * 4, v = ((along * CaptureGuideStrip.Thickness) + across) * 4;
                Assert.True(horizontal.AsSpan(h, 4).SequenceEqual(vertical.AsSpan(v, 4)), $"transpose differs at {along},{across}");
                Assert.True(horizontal.AsSpan(h, 4).SequenceEqual(direct.AsSpan(h, 4)), $"strokes differ at {along},{across}");
            }
        }
    });

    [Fact]
    public void Strip_RepeatsTheSixPixelDashPatternOnBothRows() => StaTestHost.Run(() =>
    {
        byte[] pixels = Pixels(CaptureGuideStrip.Get(Shadow, Line, 36, horizontal: true));
        uint Pixel(int x, int y) => BitConverter.ToUInt32(pixels, ((y * 36) + x) * 4);
        // Three dashed pixels, a half-covered cap, a gap, and the next dash's half-covered cap.
        Assert.NotEqual(0u, Pixel(0, 0));
        Assert.Equal(0u, Pixel(4, 0));
        Assert.NotEqual(Pixel(0, 0), Pixel(3, 0));
        Assert.Equal(Pixel(3, 0), Pixel(5, 0));
        for (int x = 0; x < 36; x++)
        {
            Assert.Equal(Pixel(x % 6, 0), Pixel(x, 0));
            Assert.Equal(Pixel(x, 0), Pixel(x, 1));
        }
    });

    [Fact]
    public void SamePaletteAndLength_ReuseOneFrozenStrip() => StaTestHost.Run(() =>
    {
        BitmapSource first = CaptureGuideStrip.Get(Shadow, Line, 333, horizontal: true);
        Assert.True(first.IsFrozen);
        Assert.Same(first, CaptureGuideStrip.Get(Frozen(Shadow.Color), Frozen(Line.Color), 333, horizontal: true));
        Assert.NotSame(first, CaptureGuideStrip.Get(Shadow, Line, 334, horizontal: true));
        Assert.NotSame(first, CaptureGuideStrip.Get(Shadow, Frozen(Colors.Red), 333, horizontal: true));
    });

    private static RenderTargetBitmap Strokes(int length)
    {
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawLine(CaptureGuideStrip.ShadowPen(Shadow, 1), new Point(0, 1), new Point(length, 1));
            dc.DrawLine(CaptureGuideStrip.LinePen(Line, 1), new Point(0, 1), new Point(length, 1));
        }
        var bitmap = new RenderTargetBitmap(length, CaptureGuideStrip.Thickness, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    private static byte[] Pixels(BitmapSource bitmap)
    {
        byte[] pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
