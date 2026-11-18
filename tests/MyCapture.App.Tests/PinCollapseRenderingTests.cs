using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Diagnostics;
using MyCapture.App.Pinning;
using MyCapture.Core.Pin;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PinCollapseRenderingTests
{
    [Fact]
    public void TallerTranslationKeepsBothEndMarkersAndTopLeftFitInOriginalViewport() => StaTestHost.Run(() =>
    {
        var source = PinContent.FromText(MarkedImage(320, 180), "source", false);
        var pin = NewPin(source, 320, 180);
        try
        {
            double scale = VisualTreeHelper.GetDpi(pin).DpiScaleX;
            var translated = PinContent.FromText(MarkedImage(320, 360, scale), "translated", false);
            pin.ApplyTranslation(translated, needsReview: false);
            Assert.True(pin.IsShowingTranslation);
            Assert.Equal(320, pin.Width);
            Assert.Equal(180, pin.Height);

            pin.ToggleCollapsed();

            var thumbnail = Assert.IsAssignableFrom<BitmapSource>(pin.ImageSourceForTest);
            Assert.Equal(96, pin.Width);
            Assert.Equal(54, pin.Height);
            // 320x360 fits into the original 320x180 frame at 1/2 scale, top-left:
            // its red and blue ends occupy the left half; the right half stays white.
            Assert.Equal(Colors.Red, PixelAt(thumbnail, .25, .1));
            Assert.Equal(Colors.Blue, PixelAt(thumbnail, .25, .9));
            Assert.Equal(Colors.White, PixelAt(thumbnail, .75, .1));
            Assert.Equal(Colors.White, PixelAt(thumbnail, .75, .9));
            Assert.Equal((int)Math.Ceiling(96 * scale), thumbnail.PixelWidth);
            Assert.Equal((int)Math.Ceiling(54 * VisualTreeHelper.GetDpi(pin).DpiScaleY), thumbnail.PixelHeight);
        }
        finally { pin.Close(); }
    });

    [Fact]
    public void PictureThumbnailKeepsExpandedFrameFillWhenSourceAspectDiffers() => StaTestHost.Run(() =>
    {
        var pin = NewPin(PinContent.FromImage(MarkedImage(320, 360)), 320, 180);
        try
        {
            pin.ToggleCollapsed();

            var thumbnail = Assert.IsAssignableFrom<BitmapSource>(pin.ImageSourceForTest);
            Assert.Equal(96, pin.Width);
            Assert.Equal(54, pin.Height);
            Assert.Equal(Colors.Red, PixelAt(thumbnail, .25, .1));
            Assert.Equal(Colors.Red, PixelAt(thumbnail, .75, .1));
            Assert.Equal(Colors.Blue, PixelAt(thumbnail, .25, .9));
            Assert.Equal(Colors.Blue, PixelAt(thumbnail, .75, .9));
        }
        finally { pin.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExtremeViewportAspectCropsTheCentreOnItsLongAxis(bool portrait) => StaTestHost.Run(() =>
    {
        int width = portrait ? 100 : 1000, height = portrait ? 1000 : 100;
        BitmapSource source = Image(width, height, 1, (x, y) => (portrait ? y : x) switch
        {
            < 250 => Colors.Red,
            < 500 => Colors.Lime,
            < 750 => Colors.Blue,
            _ => Colors.Yellow,
        });
        var pin = NewPin(PinContent.FromImage(source), width, height);
        try
        {
            pin.ToggleCollapsed();

            var thumbnail = Assert.IsAssignableFrom<BitmapSource>(pin.ImageSourceForTest);
            Assert.Equal(portrait ? 32 : 160, pin.Width);
            Assert.Equal(portrait ? 160 : 32, pin.Height);
            // The 10:1 frame scales to 320x32, then loses 80 DIP from each end.
            // The retained source interval is 250..750, exactly green then blue.
            foreach (double position in new[] { .1, .4, .6, .9 })
                Assert.Equal(position < .5 ? Colors.Lime : Colors.Blue,
                    PixelAt(thumbnail, portrait ? .5 : position, portrait ? position : .5));
        }
        finally { pin.Close(); }
    });

    private static PinWindow NewPin(PinContent content, double width, double height) =>
        new(content, new PinViewState(width, height, 1, 1, .1), 100, 100,
            dragInput: new SyntheticPinDragInput()) { ShowActivated = false };

    private static BitmapSource MarkedImage(int width, int height, double scale = 1) =>
        Image(width, height, scale, (_, y) => y < height / 4 ? Colors.Red
            : y >= height * 3 / 4 ? Colors.Blue : Colors.Lime);

    private static BitmapSource Image(int width, int height, double scale, Func<double, double, Color> colorAt)
    {
        int pixelWidth = (int)Math.Ceiling(width * scale), pixelHeight = (int)Math.Ceiling(height * scale);
        var pixels = new byte[pixelWidth * pixelHeight * 4];
        for (int y = 0; y < pixelHeight; y++)
            for (int x = 0; x < pixelWidth; x++)
            {
                Color color = colorAt(x / scale, y / scale);
                int offset = (y * pixelWidth + x) * 4;
                pixels[offset] = color.B;
                pixels[offset + 1] = color.G;
                pixels[offset + 2] = color.R;
                pixels[offset + 3] = color.A;
            }
        var bitmap = BitmapSource.Create(pixelWidth, pixelHeight, 96 * scale, 96 * scale,
            PixelFormats.Bgra32, null, pixels, pixelWidth * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static Color PixelAt(BitmapSource bitmap, double x, double y)
    {
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect((int)(bitmap.PixelWidth * x), (int)(bitmap.PixelHeight * y), 1, 1), pixel, 4, 0);
        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }
}
